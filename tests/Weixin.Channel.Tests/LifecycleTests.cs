using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class LifecycleTests
{
    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weixin-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        internal Workspace(bool media = false, bool second = false)
        {
            Directory.CreateDirectory(Path);
            using var store = new CoreStore(Path, Clock);
            store.SaveAccount("bot", "owner", "token", "https://unit.test/");
            store.CommitBatch("bot", new JsonArray(Message("owner", media)), null);
            if (second)
            {
                store.SaveAccount("second", "other", "token-2", "https://unit.test/");
                store.CommitBatch("second", new JsonArray(Message("other", false)), null);
            }
        }
        private static JsonObject Message(string peer, bool media) => new()
        {
            ["message_id"] = 1, ["from_user_id"] = peer, ["message_type"] = 1, ["context_token"] = "context",
            ["item_list"] = media
                ? new JsonArray(new JsonObject { ["type"] = 2, ["image_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://unit.test/media" } } })
                : new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = "business" } })
        };
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private static HttpResponseMessage Json(string text = "{\"ret\":0}") => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("send")]
    [InlineData("claim")]
    [InlineData("login")]
    [InlineData("typing")]
    [InlineData("progress")]
    [InlineData("with-typing")]
    public async Task Dispose_cancels_and_drains_manual_operations_before_releasing_the_state_directory(string kind)
    {
        using var workspace = new Workspace(media: kind == "claim");
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult(token);
            await release.Task; // 模拟 transport 收到取消但尚未完成退出。
            token.ThrowIfCancellationRequested();
            return Json();
        }));
        var options = new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock };
        var channel = new WeixinChannel(options);
        var originalDeliveryId = Assert.Single(channel.GetInbox()).DeliveryId;
        Task work = kind switch
        {
            "send" => channel.SendAsync(new SendRequest { AccountId = "bot", PeerId = "owner", Text = "reply" }),
            "claim" => channel.ClaimAsync("bot"),
            "login" => channel.LoginAsync(new LoginCallbacks { Updated = (_, _) => ValueTask.CompletedTask, RequestVerificationCode = (_, _) => ValueTask.FromResult("1234") }),
            "typing" => channel.TypingAsync("owner", accountId: "bot"),
            "progress" => channel.ProgressAsync(new ToolProgress("owner", "run", "tool-id", "tool", AccountId: "bot")),
            _ => channel.WithTypingAsync("owner", token => Task.Delay(Timeout.Infinite, token), accountId: "bot")
        };
        Task? disposing = null;
        try
        {
            var requestToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = channel.DisposeAsync().AsTask();
            await Task.Delay(20);
            Assert.True(requestToken.IsCancellationRequested);
            Assert.False(disposing.IsCompleted);
            Assert.Throws<IOException>(() => new WeixinChannel(options));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new SendRequest { AccountId = "bot", PeerId = "owner", Text = "too late" }));
            Assert.Throws<ObjectDisposedException>(() => channel.GetAccounts());
        }
        finally
        {
            release.TrySetResult();
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await (disposing ?? channel.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var recoveryHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10])
        })));
        await using var reopened = new WeixinChannel(options with { HttpClient = recoveryHttp });
        Assert.Single(reopened.GetAccounts());
        if (kind == "claim")
        {
            var pending = Assert.Single(reopened.GetInbox());
            Assert.Equal(originalDeliveryId, pending.DeliveryId);
            Assert.Equal("pending", pending.State);
            Assert.Null(pending.LeaseUntil);
            var reclaimed = Assert.IsType<InboundMessage>(await reopened.ClaimAsync("bot"));
            Assert.Equal(originalDeliveryId, reclaimed.DeliveryId);
            Assert.True(File.Exists(Assert.Single(reclaimed.Attachments).Path));
        }
    }

    [Fact]
    public async Task Account_deletion_holds_the_start_boundary_until_the_old_run_stops_and_state_is_removed()
    {
        using var workspace = new Workspace();
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref polls);
                polled.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("notifystop", StringComparison.Ordinal))
            {
                stopping.TrySetResult();
                await allowStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return Json();
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        var original = channel.RunAsync("bot");
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deleting = channel.DeleteAccountAsync("bot");
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var competingStart = channel.RunAsync("bot");
        try
        {
            Assert.False(competingStart.IsCompleted);
            Assert.False(deleting.IsCompleted);
        }
        finally { allowStop.TrySetResult(); }
        await deleting.WaitAsync(TimeSpan.FromSeconds(5));
        await original;
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await competingStart.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(channel.GetAccounts());
        Assert.Equal(1, polls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_callbacks_can_query_account_state_without_lock_inversion(bool disable)
    {
        using var workspace = new Workspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return Json();
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        var run = channel.RunAsync("bot", async (_, token) =>
        {
            using var registration = token.Register(() => callback.TrySetResult(channel.GetAccounts().Count));
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var readers = Task.Run(() => { for (var i = 0; i < 100; i++) channel.GetAccounts(); });
        if (disable) channel.ConfigureAccount("bot", new AccountConfiguration(Enabled: false));
        else await channel.StopAsync("bot").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(run, readers).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task One_accounts_typing_config_request_cannot_hold_another_accounts_business_handler()
    {
        using var workspace = new Workspace(second: true);
        var firstConfig = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            if (request.RequestUri.AbsolutePath.EndsWith("getconfig", StringComparison.Ordinal))
            {
                var peer = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))?["ilink_user_id"]?.GetValue<string>();
                if (peer == "owner")
                {
                    firstConfig.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
            return Json();
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var first = channel.RunAsync("bot", (_, token) => Task.Delay(Timeout.Infinite, token), stop.Token);
        await firstConfig.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = channel.RunAsync("second", async (_, token) => { secondHandler.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, stop.Token);
        try { await secondHandler.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { stop.Cancel(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public async Task Gateway_send_failure_does_not_automatically_run_the_business_handler_again(int status)
    {
        using var workspace = new Workspace();
        var sends = 0;
        var handlers = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            if (request.RequestUri.AbsolutePath.EndsWith("sendmessage", StringComparison.Ordinal))
            {
                sends++;
                return new HttpResponseMessage((HttpStatusCode)status);
            }
            return Json();
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot", async (message, token) => { handlers++; await message.ReplyAsync("reply", cancellationToken: token); }, stop.Token);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (channel.GetInbox()[0].State != "unknown") await Task.Delay(5, limit.Token);
        workspace.Clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(20);
        Assert.Equal(1, handlers);
        Assert.Equal(1, sends);
        Assert.Equal("unknown", Assert.Single(channel.GetSendSegments(channel.GetInbox()[0].DeliveryId)).Status);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Throwing_host_cancellation_callback_cannot_prevent_disposal_from_draining_and_releasing_the_directory()
    {
        using var workspace = new Workspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCalls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return Json();
        }));
        var options = new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock };
        var channel = new WeixinChannel(options);
        var run = channel.RunAsync("bot", async (_, token) =>
        {
            using var registration = token.Register(() => { callbackCalls++; throw new InvalidOperationException("host cancellation callback failed"); });
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        Assert.Equal(1, callbackCalls);
        await using var reopened = new WeixinChannel(options);
        Assert.Single(reopened.GetAccounts());
        Assert.Equal("pending", Assert.Single(reopened.GetInbox()).State);
    }
}
