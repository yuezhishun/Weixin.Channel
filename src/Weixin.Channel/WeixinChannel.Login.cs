using System.Text.Json.Nodes;

namespace Weixin.Channel;

public sealed partial class WeixinChannel
{
    public Task<LoginResult> LoginAsync(LoginCallbacks callbacks, CancellationToken cancellationToken = default) =>
        TrackOperationAsync(token => LoginCoreAsync(callbacks, token), cancellationToken);
    private async Task<LoginResult> LoginCoreAsync(LoginCallbacks callbacks, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callbacks);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5), _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, lifetime.Token);
        var token = linked.Token;
        var baseUrl = _options.ApiBaseUrl;
        var refresh = 0;
        string? code = null;
        string qr = "";
        async Task FetchQrAsync()
        {
            refresh++;
            if (refresh > 3) throw new InvalidOperationException("二维码多次失效或验证码受限，请稍后重新登录。");
            var localTokens = new JsonArray(_store.Accounts().Take(10).Select(a => JsonValue.Create(a.Token)).ToArray<JsonNode?>());
            var response = await _protocol.RequestAsync(_options.ApiBaseUrl, "ilink/bot/get_bot_qrcode?bot_type=3", new JsonObject { ["local_token_list"] = localTokens }, null, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            qr = ProtocolClient.Text(response["qrcode"]) ?? throw new InvalidDataException("服务器未返回二维码。");
            var content = ProtocolClient.Text(response["qrcode_img_content"]) ?? throw new InvalidDataException("服务器未返回二维码内容。");
            code = null;
            baseUrl = _options.ApiBaseUrl;
            await callbacks.Updated(new LoginUpdate("wait", content, refresh), token).ConfigureAwait(false);
        }
        try
        {
            await FetchQrAsync().ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                JsonObject response;
                try
                {
                    var endpoint = "ilink/bot/get_qrcode_status?qrcode=" + Uri.EscapeDataString(qr);
                    if (!string.IsNullOrEmpty(code)) endpoint += "&verify_code=" + Uri.EscapeDataString(code);
                    response = await _protocol.RequestAsync(baseUrl, endpoint, null, null, TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TimeoutException or HttpRequestException)
                {
                    Log($"二维码轮询失败: {ProtocolClient.Classify(e)}，稍后重试。");
                    await DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }
                var status = ProtocolClient.Text(response["status"]) ?? "wait";
                await callbacks.Updated(new LoginUpdate(status, RefreshCount: refresh), token).ConfigureAwait(false);
                switch (status)
                {
                    case "wait": break;
                    case "scaned": code = null; break;
                    case "need_verifycode":
                        code = (await callbacks.RequestVerificationCode(code is not null, token).ConfigureAwait(false)).Trim();
                        continue;
                    case "expired":
                    case "verify_code_blocked":
                        await FetchQrAsync().ConfigureAwait(false);
                        break;
                    case "scaned_but_redirect":
                        var host = ProtocolClient.Text(response["redirect_host"]);
                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out var redirect) || redirect.AbsolutePath != "/" || !string.IsNullOrEmpty(redirect.UserInfo) || redirect.Query.Length > 0)
                                throw new InvalidDataException("服务器返回无效的IDC主机。");
                            baseUrl = redirect;
                        }
                        break;
                    case "binded_redirect": return new LoginResult(null, true);
                    case "confirmed":
                        var rawId = ProtocolClient.Text(response["ilink_bot_id"]);
                        var credential = ProtocolClient.Text(response["bot_token"]);
                        if (string.IsNullOrWhiteSpace(rawId) || string.IsNullOrWhiteSpace(credential)) throw new InvalidDataException("登录确认缺少账号或凭据。");
                        var owner = ProtocolClient.Text(response["ilink_user_id"])?.Trim() ?? "";
                        var url = ProtocolClient.Text(response["baseurl"]) ?? baseUrl.AbsoluteUri;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var accountUrl) || accountUrl.Scheme is not ("https" or "http") || (accountUrl.Scheme == "http" && !accountUrl.IsLoopback) || accountUrl.UserInfo.Length > 0)
                            throw new InvalidDataException("登录返回无效接口地址。");
                        await _loginCommit.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var id = CoreStore.NormalizeAccountId(rawId);
                            var gate = AccountGate(id);
                            await gate.WaitAsync(token).ConfigureAwait(false);
                            try
                            {
                                await StopRunAsync(id).ConfigureAwait(false);
                                foreach (var old in _store.Accounts().Where(a => a.AccountId == id || (owner.Length > 0 && a.OwnerId == owner)))
                                {
                                    if (old.AccountId != id) await DeleteAccountCoreAsync(old.AccountId, token).ConfigureAwait(false);
                                    else if (old.OwnerId != owner) DeleteAccountState(id);
                                }
                                token.ThrowIfCancellationRequested();
                                _store.SaveAccount(rawId, owner, credential.Trim(), accountUrl.AbsoluteUri);
                                Publish("account", id, null, "logged-in");
                                return new LoginResult(PublicAccount(_store.GetAccount(id)), false);
                            }
                            finally { gate.Release(); }
                        }
                        finally { _loginCommit.Release(); }
                    default: throw new InvalidDataException("服务器返回未支持的登录状态。");
                }
                await DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            await callbacks.Updated(new LoginUpdate("expired", RefreshCount: refresh), cancellationToken).ConfigureAwait(false);
            throw new TimeoutException("登录会话超过五分钟，请重新生成二维码。");
        }
    }
}
