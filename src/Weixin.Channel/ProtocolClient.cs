using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weixin.Channel;

internal sealed class ProtocolClient(HttpClient http, WeixinOptions options, CoreStore? store = null)
{
    internal async Task<JsonObject> PostAsync(AccountRow account, string endpoint, JsonObject body,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (store is not null)
        {
            account = store.GetAccount(account.AccountId);
            if (!account.Enabled || account.PausedUntil > options.TimeProvider.GetUtcNow())
            {
                var unavailable = new InvalidOperationException("账号已停用或暂停，未发起微信请求。");
                unavailable.Data["WeixinRequestNotSent"] = true;
                throw unavailable;
            }
        }
        body["base_info"] = new JsonObject { ["channel_version"] = "2.4.10", ["bot_agent"] = SanitizeBotAgent(options.BotAgent) };
        var response = await RequestAsync(new Uri(account.BaseUrl), endpoint, body, account.Token,
            timeout ?? TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (Error(response) == -14) store?.Pause(account.AccountId, TimeSpan.FromHours(1));
        return response;
    }

    internal async Task<JsonObject> RequestAsync(Uri baseUrl, string endpoint, JsonObject? body, string? token,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/"), endpoint));
        request.Headers.TryAddWithoutValidation("iLink-App-Id", "bot");
        request.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", "132106");
        if (!string.IsNullOrWhiteSpace(options.RouteTag)) request.Headers.Add("SKRouteTag", options.RouteTag);
        if (body is not null)
        {
            request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
            var uin = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
            request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", Convert.ToBase64String(Encoding.UTF8.GetBytes(uin.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var deadline = new CancellationTokenSource(timeout, options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"微信接口 HTTP {(int)response.StatusCode}", null, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw) && endpoint == "ilink/bot/sendtyping") return new JsonObject();
            return JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidDataException("微信接口返回空JSON。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("微信接口请求超时。");
        }
    }

    internal static string? Text(JsonNode? node) => node is null ? null : node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    internal static long Number(JsonNode? node, long fallback = 0) => long.TryParse(Text(node), out var value) ? value : fallback;
    internal static int Error(JsonObject response)
    {
        var ret = Number(response["ret"]);
        var code = Number(response["errcode"]);
        return ret == -14 || code == -14 ? -14 : (int)(ret != 0 ? ret : code);
    }
    internal static string Classify(Exception error) => error switch
    {
        TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "dns",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "tcp",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "tls",
        HttpRequestException { StatusCode: not null } => "http",
        IOException => "storage-or-media",
        _ => "unknown"
    };

    internal static string SanitizeBotAgent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "OpenClaw";
        var rawTokens = Regex.Split(raw.Trim(), @"\s+");
        var tokens = new List<string>();
        for (var i = 0; i < rawTokens.Length; i++)
        {
            var token = rawTokens[i];
            if (token.StartsWith('('))
                while (!token.EndsWith(')') && i + 1 < rawTokens.Length) token += " " + rawTokens[++i];
            tokens.Add(token);
        }
        var accepted = new List<string>();
        string? pending = null;
        foreach (var token in tokens)
        {
            if (token.StartsWith('(') && token.EndsWith(')'))
            {
                if (pending is not null) accepted.Add(Regex.IsMatch(token[1..^1], @"^[\x20-\x27\x2A-\x7E]{1,64}$") ? pending + " " + token : pending);
                pending = null;
                continue;
            }
            if (pending is not null) accepted.Add(pending);
            pending = Regex.IsMatch(token, @"^[A-Za-z0-9_.\-]{1,32}/[A-Za-z0-9_.+\-]{1,32}$") ? token : null;
        }
        if (pending is not null) accepted.Add(pending);
        var result = "";
        foreach (var token in accepted)
        {
            var candidate = result.Length == 0 ? token : result + " " + token;
            if (candidate.Length > 256) break;
            result = candidate;
        }
        return result.Length == 0 ? "OpenClaw" : result;
    }
}
