using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class LoginProtocolTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weixin-login-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task<T> PumpAsync<T>(Task<T> task, FakeTimeProvider clock)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2, limit.Token);
        }
        return await task;
    }

    [Fact]
    public async Task Login_preserves_verification_retry_redirect_and_normalized_persistent_owner()
    {
        using var workspace = new Workspace();
        var responses = new Queue<string>([
            "{\"status\":\"wait\"}", "{\"status\":\"scaned\"}", "{\"status\":\"need_verifycode\"}",
            "{\"status\":\"need_verifycode\"}", "{\"status\":\"scaned\"}",
            "{\"status\":\"scaned_but_redirect\",\"redirect_host\":\"idc.weixin.test\"}",
            "{\"status\":\"confirmed\",\"ilink_bot_id\":\"Bot@im.bot\",\"bot_token\":\"secret\",\"ilink_user_id\":\"owner\",\"baseurl\":\"https://idc.weixin.test\"}"
        ]);
        var urls = new List<string>();
        var updates = new List<LoginUpdate>();
        var retries = new List<bool>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(request.RequestUri.AbsolutePath.EndsWith("get_bot_qrcode", StringComparison.Ordinal)
                ? "{\"qrcode\":\"qr-secret\",\"qrcode_img_content\":\"https://scan.test/qr\"}" : responses.Dequeue()));
        }));
        await using (var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock }))
        {
            var result = await PumpAsync(channel.LoginAsync(new LoginCallbacks
            {
                Updated = (update, _) => { updates.Add(update); return ValueTask.CompletedTask; },
                RequestVerificationCode = (retry, _) => { retries.Add(retry); return ValueTask.FromResult(retry ? "5678" : "1234"); }
            }), workspace.Clock);
            Assert.Equal("bot-im-bot", result.Account!.AccountId);
            Assert.Equal("owner", result.Account.OwnerId);
            Assert.Equal(new[] { false, true }, retries);
            Assert.Contains(urls, url => url.Contains("verify_code=1234", StringComparison.Ordinal));
            Assert.Contains(urls, url => url.Contains("verify_code=5678", StringComparison.Ordinal));
            Assert.StartsWith("https://idc.weixin.test/", urls[^1], StringComparison.Ordinal);
            Assert.DoesNotContain("verify_code", urls[^1], StringComparison.Ordinal);
            Assert.Contains(updates, update => update.QrContent == "https://scan.test/qr");
        }
        await using var reopened = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path });
        Assert.Equal("owner", Assert.Single(reopened.GetAccounts()).OwnerId);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("verify_code_blocked")]
    public async Task Expired_or_blocked_QR_refreshes_without_reusing_verification_code(string status)
    {
        using var workspace = new Workspace();
        var qrCount = 0;
        var statusCount = 0;
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.EndsWith("get_bot_qrcode", StringComparison.Ordinal)
                ? $"{{\"qrcode\":\"qr-{++qrCount}\",\"qrcode_img_content\":\"display-{qrCount}\"}}"
                : ++statusCount == 1 ? $"{{\"status\":\"{status}\"}}" : "{\"status\":\"binded_redirect\"}"))));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        var updates = new List<LoginUpdate>();
        var result = await PumpAsync(channel.LoginAsync(new LoginCallbacks
        {
            Updated = (update, _) => { updates.Add(update); return ValueTask.CompletedTask; },
            RequestVerificationCode = (_, _) => ValueTask.FromResult("1234")
        }), workspace.Clock);
        Assert.True(result.AlreadyConnected);
        Assert.Null(result.Account);
        Assert.Equal(2, qrCount);
        Assert.Contains(updates, update => update.QrContent == "display-2" && update.RefreshCount == 2);
    }

    [Fact]
    public async Task Login_only_sends_latest_ten_tokens_and_never_exposes_them_to_callback()
    {
        using var workspace = new Workspace();
        using (var store = new CoreStore(workspace.Path, workspace.Clock))
            for (var i = 0; i < 12; i++)
            {
                store.SaveAccount("account-" + i, "owner-" + i, "token-" + i, "https://unit.test");
                workspace.Clock.Advance(TimeSpan.FromMilliseconds(1));
            }
        JsonArray? sent = null;
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            if (request.Content is not null)
            {
                var payload = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation))!;
                sent = payload["local_token_list"]!.AsArray();
                return Json("{\"qrcode\":\"qr\",\"qrcode_img_content\":\"display\"}");
            }
            return Json("{\"status\":\"binded_redirect\"}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        await channel.LoginAsync(new LoginCallbacks
        {
            Updated = (update, _) => { Assert.DoesNotContain("token-", System.Text.Json.JsonSerializer.Serialize(update), StringComparison.Ordinal); return ValueTask.CompletedTask; },
            RequestVerificationCode = (_, _) => throw new InvalidOperationException()
        });
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => "token-" + i), sent!.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Five_minute_ttl_aborts_login_poll_in_flight()
    {
        using var workspace = new Workspace();
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            if (request.Content is not null) return Json("{\"qrcode\":\"qr\",\"qrcode_img_content\":\"display\"}");
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json("{}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        var updates = new List<string>();
        var login = channel.LoginAsync(new LoginCallbacks
        {
            Updated = (update, _) => { updates.Add(update.Status); return ValueTask.CompletedTask; },
            RequestVerificationCode = (_, _) => throw new InvalidOperationException()
        });
        workspace.Clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<TimeoutException>(async () => await login.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("expired", updates[^1]);
        Assert.Empty(channel.GetAccounts());
    }

    [Fact]
    public async Task User_cancellation_interrupts_pending_verification_callback()
    {
        using var workspace = new Workspace();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.Content is not null
            ? "{\"qrcode\":\"qr\",\"qrcode_img_content\":\"display\"}" : "{\"status\":\"need_verifycode\"}"))));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http });
        using var stop = new CancellationTokenSource();
        var login = channel.LoginAsync(new LoginCallbacks
        {
            Updated = (_, _) => ValueTask.CompletedTask,
            RequestVerificationCode = async (_, cancellation) => { requested.SetResult(); await Task.Delay(Timeout.Infinite, cancellation); return ""; }
        }, stop.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await login.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Protocol_wire_metadata_authentication_and_uint64_match_official_fixture()
    {
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            Assert.Equal("bot", Assert.Single(request.Headers.GetValues("iLink-App-Id")));
            Assert.Equal("132106", Assert.Single(request.Headers.GetValues("iLink-App-ClientVersion")));
            Assert.Equal("test-route", Assert.Single(request.Headers.GetValues("SKRouteTag")));
            Assert.Equal("ilink_bot_token", Assert.Single(request.Headers.GetValues("AuthorizationType")));
            Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
            Assert.True(uint.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(Assert.Single(request.Headers.GetValues("X-WECHAT-UIN")))), out _));
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;
            Assert.Equal("2.4.10", payload["base_info"]!["channel_version"]!.GetValue<string>());
            Assert.Equal("Agent/1.2 (host)", payload["base_info"]!["bot_agent"]!.GetValue<string>());
            return Json("{\"ret\":0,\"message_id\":18446744073709551615}");
        }));
        var protocol = new ProtocolClient(http, new WeixinOptions { StateDirectory = ".", BotAgent = "invalid 中文 Agent/1.2 (host)", RouteTag = "test-route" });
        var response = await protocol.PostAsync(new AccountRow("bot", "owner", " token ", "https://unit.test", true, false, [], null), "ilink/bot/sendmessage", new JsonObject { ["msg"] = new JsonObject() });
        Assert.Equal("18446744073709551615", ProtocolClient.Text(response["message_id"]));
    }

    [Theory]
    [InlineData(null, "OpenClaw")]
    [InlineData("garbage 中文", "OpenClaw")]
    [InlineData("One/1 (good comment) invalid Two/2", "One/1 (good comment) Two/2")]
    [InlineData("One/1 (bad(comment)) Two/2", "One/1 Two/2")]
    public void BotAgent_sanitization_drops_invalid_tokens_without_leaking_control_characters(string? raw, string expected) => Assert.Equal(expected, ProtocolClient.SanitizeBotAgent(raw));

    [Theory]
    [InlineData("ilink/bot/getconfig")]
    [InlineData("ilink/bot/getuploadurl")]
    [InlineData("ilink/bot/sendtyping")]
    [InlineData("ilink/bot/msg/notifystart")]
    public async Task Every_account_request_pauses_on_stale_session_and_blocks_queued_stale_account_snapshots(string endpoint)
    {
        using var workspace = new Workspace();
        using var store = new CoreStore(workspace.Path, workspace.Clock);
        store.SaveAccount("bot", "owner", "token", "https://unit.test/");
        var oldSnapshot = store.GetAccount("bot");
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Json("{\"ret\":-1,\"errcode\":-14}")); }));
        var protocol = new ProtocolClient(http, new WeixinOptions { StateDirectory = workspace.Path, TimeProvider = workspace.Clock }, store);
        await protocol.PostAsync(oldSnapshot, endpoint, new JsonObject());
        Assert.Equal(workspace.Clock.GetUtcNow().AddHours(1), store.GetAccount("bot").PausedUntil);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.PostAsync(oldSnapshot, "ilink/bot/sendmessage", new JsonObject()));
        Assert.True(failure.Data.Contains("WeixinRequestNotSent"));
        Assert.Equal(1, requests);
    }
}
