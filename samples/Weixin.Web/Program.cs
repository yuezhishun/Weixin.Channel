using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.OpenApi;
using QRCoder;
using Weixin.Channel;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:3083");
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
var app = builder.Build();
var stateDirectory = builder.Configuration["state"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weixin.Channel");
await using var channel = new WeixinChannel(new WeixinOptions
{
    StateDirectory = stateDirectory,
    BotAgent = "Weixin.Web/1.0.0",
    Log = message => app.Logger.LogInformation("{ChannelMessage}", message)
});

app.UseStatusCodePages(async context =>
    await Results.Problem(statusCode: context.HttpContext.Response.StatusCode, title: "请求未成功，请检查路径、方法与请求参数").ExecuteAsync(context.HttpContext));
using var shutdown = new CancellationTokenSource();
using var stopping = app.Lifetime.ApplicationStopping.Register(shutdown.Cancel);
var logins = new ConcurrentDictionary<string, LoginSession>();
var loginGate = new object();
var runs = new ConcurrentDictionary<string, Task>();
using var runGate = new SemaphoreSlim(1, 1);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var remote = context.Connection.RemoteIpAddress;
    var host = context.Request.Host;
    var origin = context.Request.Headers.Origin.ToString();
    var validOrigin = string.IsNullOrEmpty(origin) ||
        (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
         uri.Scheme == context.Request.Scheme &&
         string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase) &&
         uri.Port == (host.Port ?? (context.Request.IsHttps ? 443 : 80)) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
    if (remote is null || !IsLoopback(remote) || !IsLoopbackHost(host.Host) ||
        (host.Port ?? (context.Request.IsHttps ? 443 : 80)) != context.Connection.LocalPort ||
        !validOrigin || context.Request.Headers["x-weixin-request"].ToString() != "1")
    {
        await Results.Problem(statusCode: 403, title: "请求被拒绝", detail: "仅允许本机客户端、正确 Host/Origin 与 x-weixin-request: 1；不开放跨域。").ExecuteAsync(context);
        return;
    }
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error)
    {
        if (context.Response.HasStarted) throw;
        var (status, title) = error switch
        {
            KeyNotFoundException => (404, "资源不存在"),
            ArgumentException or FormatException or JsonException or BadHttpRequestException => (400, "请求参数无效"),
            InvalidOperationException => (409, "当前状态不允许此操作；请核对账号、租约和 unknown 发送记录"),
            OperationCanceledException => (408, "操作超时或已取消"),
            _ => (500, "渠道操作失败")
        };
        app.Logger.LogWarning("请求失败：{ErrorType}", error.GetType().Name);
        await Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", accounts = channel.GetAccounts().Count }));
app.MapOpenApi();
var api = app.MapGroup("/api")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status500InternalServerError);

api.MapPost("/logins", () =>
{
    lock (loginGate)
    {
        PurgeLogins();
        if (logins.Values.Count(session => !session.Completed) >= 10) return Results.Problem(statusCode: 429, title: "登录会话过多，请先取消已有会话");
        var id = Guid.NewGuid().ToString("N");
        var session = new LoginSession(shutdown.Token);
        if (!logins.TryAdd(id, session)) throw new InvalidOperationException();
        session.Work = RunLoginAsync(session);
        return Results.Accepted($"/api/logins/{id}", new { id, status = "starting", url = $"/api/logins/{id}", qrUrl = $"/api/logins/{id}/qr" });
    }
});
api.MapGet("/logins/{id}", (string id) => Results.Ok(GetLogin(id).Snapshot(id)));
api.MapDelete("/logins/{id}", async (string id) =>
{
    var session = GetLogin(id);
    session.Cancel();
    await session.Work;
    return Results.NoContent();
});
api.MapGet("/logins/{id}/qr", (string id) =>
{
    var content = GetLogin(id).QrContent;
    if (content is null) return Results.Problem(statusCode: 409, title: "二维码尚未生成或会话已结束");
    using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
    using var qr = new PngByteQRCode(data);
    return Results.File(qr.GetGraphic(8), "image/png");
});
api.MapPost("/logins/{id}/code", (string id, VerificationCode body) =>
{
    if (string.IsNullOrWhiteSpace(body.Code) || body.Code.Length > 32 || !body.Code.All(char.IsAsciiDigit)) throw new ArgumentException("验证码格式无效。");
    if (!GetLogin(id).SubmitCode(body.Code.Trim())) return Results.Problem(statusCode: 409, title: "当前会话没有等待验证码");
    return Results.NoContent();
});

api.MapGet("/accounts", () => Results.Ok(channel.GetAccounts())).Produces<WeixinAccount[]>();
api.MapPut("/accounts/{id}", (string id, AccountConfiguration body) =>
{
    channel.ConfigureAccount(id, body);
    return Results.NoContent();
});
api.MapPost("/accounts/{id}/start", async (string id, CancellationToken ct) =>
{
    await runGate.WaitAsync(ct);
    try
    {
        var account = channel.GetAccounts().SingleOrDefault(a => a.AccountId == id) ?? throw new KeyNotFoundException();
        if (!account.Enabled) throw new InvalidOperationException("账号已禁用。");
        if (runs.TryGetValue(id, out var running) && !running.IsCompleted) return Results.Problem(statusCode: 409, title: "账号已在运行");
        runs[id] = RunAccountAsync(id);
        return Results.Accepted($"/api/accounts", new { accountId = id, consumption = "manual-inbox" });
    }
    finally { runGate.Release(); }
});
api.MapPost("/accounts/{id}/stop", async (string id) =>
{
    await channel.StopAsync(id);
    return Results.NoContent();
});
api.MapDelete("/accounts/{id}", async (string id, CancellationToken ct) =>
{
    await channel.DeleteAccountAsync(id, ct);
    return Results.NoContent();
});

api.MapPost("/messages", async (SendRequest body, CancellationToken ct) => TypedResults.Ok(await channel.SendAsync(body, ct)));
api.MapPost("/messages/reply", async (SendRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.DeliveryId)) throw new ArgumentException("deliveryId 必填。");
    return TypedResults.Ok(await channel.ReplyAsync(body.DeliveryId, body.Text, body.Media, ct));
});
api.MapPost("/messages/typing", async (TypingRequest body, CancellationToken ct) =>
{
    await channel.TypingAsync(body.PeerId, body.Active, body.AccountId, body.DeliveryId, ct);
    return Results.NoContent();
});
api.MapPost("/messages/progress", async (ToolProgress body, CancellationToken ct) =>
{
    await channel.ProgressAsync(body, ct);
    return Results.NoContent();
});
api.MapPost("/messages/progress/{runId}/complete", async (string runId, CancellationToken ct) =>
{
    await channel.DrainProgressAsync(runId, ct);
    return Results.NoContent();
});
api.MapPost("/messages/timing", async (TimingRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.DeliveryId) || string.IsNullOrWhiteSpace(body.Operation) ||
        !double.IsFinite(body.ElapsedMilliseconds) || body.ElapsedMilliseconds < 0 || body.ElapsedMilliseconds >= TimeSpan.MaxValue.TotalMilliseconds)
        throw new ArgumentException("deliveryId、operation 和有效的非负 elapsedMilliseconds 必填。");
    await channel.ReportTimingAsync(body.DeliveryId, body.Operation, TimeSpan.FromMilliseconds(body.ElapsedMilliseconds), ct);
    return Results.NoContent();
});

api.MapGet("/inbox/pending", (string? accountId) => TypedResults.Ok(channel.GetInbox(accountId)));
api.MapGet("/inbox/{id}/receipts", (string id) => TypedResults.Ok(channel.GetSendSegments(id).ToArray()));
api.MapPost("/inbox/claim", async (string? accountId, CancellationToken ct) =>
{
    var message = await channel.ClaimAsync(accountId, ct);
    return message is null ? Results.NoContent() : Results.Ok(message);
}).Produces<InboundMessage>().Produces(StatusCodes.Status204NoContent);
api.MapPost("/inbox/{id}/ack", async (string id, LeaseRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.LeaseId)) throw new ArgumentException("leaseId 必填。");
    await channel.AcknowledgeAsync(id, body.LeaseId, ct);
    return Results.NoContent();
}).Produces(StatusCodes.Status204NoContent);
api.MapPost("/inbox/{id}/retry", async (string id, LeaseRequest body, CancellationToken ct) =>
{
    await channel.RetryAsync(id, body.LeaseId, body.AllowUnknownSend, ct);
    return TypedResults.Ok(new { duplicateRiskAccepted = body.AllowUnknownSend, warning = body.AllowUnknownSend ? "unknown 发送可能已送达；重试可能产生重复消息。" : null });
});
api.MapPost("/inbox/{id}/renew", async (string id, LeaseRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.LeaseId)) throw new ArgumentException("leaseId 必填。");
    return TypedResults.Ok(new { leaseUntil = await channel.RenewLeaseAsync(id, body.LeaseId, ct) });
});

api.MapGet("/events", async (HttpContext context) =>
{
    var ct = context.RequestAborted;
    using var eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await using var events = channel.EventsAsync(eventLifetime.Token).GetAsyncEnumerator(eventLifetime.Token);
    var next = events.MoveNextAsync().AsTask();
    var pending = channel.GetInbox();
    await WriteEventAsync("pending", new { count = pending.Count, accounts = pending.GroupBy(entry => entry.AccountId).Select(group => new { accountId = group.Key, count = group.Count() }) });
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var ready = await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (ready == next)
            {
                if (!await next) break;
                await WriteEventAsync("channel", events.Current);
                next = events.MoveNextAsync().AsTask();
            }
            else
            {
                await context.Response.WriteAsync(": keepalive\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
    }
    finally
    {
        eventLifetime.Cancel();
        try { await next; } catch (OperationCanceledException) when (eventLifetime.IsCancellationRequested) { }
    }
    async Task WriteEventAsync<T>(string name, T value)
    {
        await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(value, json)}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

try { await app.RunAsync(); }
finally
{
    shutdown.Cancel();
    await Task.WhenAll(logins.Values.Select(session => session.Work).Concat(runs.Values));
    foreach (var session in logins.Values) session.Dispose();
}

LoginSession GetLogin(string id) => logins.TryGetValue(id, out var session) ? session : throw new KeyNotFoundException();

void PurgeLogins()
{
    foreach (var (id, session) in logins)
        if (session.Completed && DateTimeOffset.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(10) && logins.TryRemove(id, out _)) session.Dispose();
}

async Task RunLoginAsync(LoginSession session)
{
    try
    {
        var result = await channel.LoginAsync(new LoginCallbacks
        {
            Updated = (update, _) => { session.Update(update); return ValueTask.CompletedTask; },
            RequestVerificationCode = session.RequestCodeAsync
        }, session.Token);
        session.Finish(result);
    }
    catch (OperationCanceledException) { session.Finish(null, "cancelled"); }
    catch (TimeoutException) { session.Finish(null, "expired"); }
    catch (Exception error)
    {
        app.Logger.LogWarning("登录失败：{ErrorType}", error.GetType().Name);
        session.Finish(null, "failed");
    }
}

async Task RunAccountAsync(string id)
{
    try { await channel.RunAsync(id, cancellationToken: shutdown.Token); }
    catch (OperationCanceledException) { }
    catch (Exception error) { app.Logger.LogWarning("账号运行终止：{ErrorType}", error.GetType().Name); }
}

static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
static bool IsLoopbackHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsLoopback(ip));

internal sealed record VerificationCode(string Code);
internal sealed record TypingRequest(string PeerId, bool Active = true, string? AccountId = null, string? DeliveryId = null);
internal sealed record LeaseRequest(string? LeaseId = null, bool AllowUnknownSend = false);
internal sealed record TimingRequest(string DeliveryId, string Operation, double ElapsedMilliseconds);

// 登录是独立于 HTTP 请求的短期任务；验证码只交给当前等待者，不进入查询快照或日志。
internal sealed class LoginSession(CancellationToken stopping) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    private TaskCompletionSource<string>? code;
    private string state = "starting";
    private string? qr;
    private int refreshCount;
    private LoginResult? result;
    private bool completed;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationToken Token => cancellation.Token;
    public Task Work { get; set; } = Task.CompletedTask;
    public bool Completed { get { lock (gate) return completed; } }
    public string? QrContent { get { lock (gate) return completed ? null : qr; } }
    public void Cancel() => cancellation.Cancel();
    public void Update(LoginUpdate update)
    {
        lock (gate)
        {
            state = update.Status;
            if (update.QrContent is not null) qr = update.QrContent;
            refreshCount = update.RefreshCount;
        }
    }
    public async ValueTask<string> RequestCodeAsync(bool retry, CancellationToken ct)
    {
        Task<string> waiting;
        lock (gate)
        {
            state = retry ? "need_verifycode_retry" : "need_verifycode";
            code = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = code.Task;
        }
        try { return await waiting.WaitAsync(ct); }
        finally { lock (gate) code = null; }
    }
    public bool SubmitCode(string value) { lock (gate) return code?.TrySetResult(value) == true; }
    public void Finish(LoginResult? value, string? terminalState = null)
    {
        lock (gate)
        {
            result = value;
            state = terminalState ?? (value?.AlreadyConnected == true ? "binded_redirect" : "confirmed");
            completed = true;
            qr = null;
            code = null;
        }
    }
    public object Snapshot(string id)
    {
        lock (gate) return new { id, status = state, createdAt = CreatedAt, refreshCount, completed, needsVerificationCode = code is not null, qrAvailable = qr is not null, result };
    }
    public void Dispose() => cancellation.Dispose();
}
