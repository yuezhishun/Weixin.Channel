using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static JsonObject Message(string id, string peer = "owner", string text = "hello", string context = "context-original") => new()
    {
        ["message_id"] = JsonNode.Parse(id), ["from_user_id"] = peer, ["message_type"] = 1,
        ["context_token"] = context, ["create_time_ms"] = Start.ToUnixTimeMilliseconds(),
        ["item_list"] = new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = text } })
    };
    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weixin-tests-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Clock { get; } = new(Start);
        internal Workspace() => Directory.CreateDirectory(Path);
        internal CoreStore Store()
        {
            var store = new CoreStore(Path, Clock);
            if (store.Accounts().Count == 0) store.SaveAccount("bot@im.bot", "owner", "private-token", "https://unit.test/");
            return store;
        }
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    [Fact]
    public void Failed_batch_commit_keeps_cursor_context_and_inbox_unchanged_to_allow_redelivery()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var previous = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), "previous"));
        var batch = new JsonArray(
            Message("9007199254740993", context: "new-owner-context"),
            Message("18446744073709551615", peer: "other-peer", context: "other-context"));
        using var db = new SqliteConnection($"Data Source={System.IO.Path.Combine(workspace.Path, "core.sqlite")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_cursor BEFORE UPDATE OF cursor ON accounts BEGIN SELECT RAISE(ABORT,'simulated disk failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.CommitBatch("bot-im-bot", batch, "next"));
        Assert.Equal(previous, Assert.Single(store.Inbox(null, false)).DeliveryId);
        Assert.Equal("previous", store.Cursor("bot-im-bot"));
        Assert.Equal("context-original", store.ResolveSend(new SendRequest { PeerId = "owner" }).ContextToken);
        Assert.Null(store.ResolveSend(new SendRequest { PeerId = "other-peer" }).ContextToken);

        command.CommandText = "DROP TRIGGER reject_cursor";
        command.ExecuteNonQuery();
        var deliveries = store.CommitBatch("bot-im-bot", batch, "next");
        Assert.Equal("next", store.Cursor("bot-im-bot"));
        Assert.Equal("new-owner-context", store.ResolveSend(new SendRequest { PeerId = "owner" }).ContextToken);
        Assert.Equal("other-context", store.ResolveSend(new SendRequest { PeerId = "other-peer" }).ContextToken);
        Assert.Empty(store.CommitBatch("bot-im-bot", batch, "next"));
        var stored = store.Inbox(null, false);
        Assert.Equal(new[] { "1", "9007199254740993", "18446744073709551615" }, stored.Select(row => row.MessageId));
        Assert.Equal(deliveries, stored.Skip(1).Select(row => row.DeliveryId));
    }

    [Fact]
    public void Committed_unacked_message_survives_restart_with_stable_delivery_id_and_uint64()
    {
        using var workspace = new Workspace();
        string delivery;
        using (var store = workspace.Store())
        {
            delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("18446744073709551615")), "cursor"));
            Assert.Empty(store.CommitBatch("bot-im-bot", new JsonArray(Message("18446744073709551615")), "cursor2"));
            Assert.Equal("18446744073709551615", Assert.Single(store.Inbox(null, false)).MessageId);
        }
        using var reopened = workspace.Store();
        var claimed = Assert.IsType<InboxRow>(reopened.Claim(null, TimeSpan.FromSeconds(60)));
        Assert.Equal(delivery, claimed.DeliveryId);
        Assert.Equal("cursor2", reopened.Cursor("bot-im-bot"));
        Assert.Equal("context-original", reopened.ResolveSend(new SendRequest { DeliveryId = delivery }).ContextToken);
    }

    [Fact]
    public void Expired_worker_cannot_ack_renew_or_retry_a_reassigned_delivery()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.CommitBatch("bot-im-bot", new JsonArray(Message("1"), Message("2")), "cursor");
        var first = Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60)));
        Assert.Null(store.Claim(null, TimeSpan.FromSeconds(60)));
        workspace.Clock.Advance(TimeSpan.FromSeconds(61));
        var second = Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60)));
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Throws<InvalidOperationException>(() => store.Complete(first.DeliveryId, first.LeaseId));
        Assert.Throws<InvalidOperationException>(() => store.Renew(first.DeliveryId, first.LeaseId, TimeSpan.FromSeconds(60)));
        Assert.Throws<InvalidOperationException>(() => store.Retry(first.DeliveryId, first.LeaseId, false));
        store.Complete(second.DeliveryId, second.LeaseId);
        store.Complete(second.DeliveryId, second.LeaseId);
        Assert.Equal("2", Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60))).MessageId);
    }

    [Fact]
    public void Failed_head_blocks_its_account_but_other_account_can_progress_without_loss()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.SaveAccount("other", "other-owner", "token", "https://unit.test");
        store.CommitBatch("bot-im-bot", new JsonArray(Message("1"), Message("2")), null);
        store.CommitBatch("other", new JsonArray(Message("3", "other-owner")), null);
        var head = Assert.IsType<InboxRow>(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60)));
        store.Retry(head.DeliveryId, head.LeaseId, false, automatic: true, error: "host failure");
        Assert.Null(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60)));
        Assert.Equal("other", Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60))).AccountId);
        workspace.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(head.DeliveryId, Assert.IsType<InboxRow>(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60))).DeliveryId);
        Assert.Equal(3, store.Inbox(null, false).Count);
    }

    [Fact]
    public void Reply_to_old_delivery_uses_its_original_context_not_latest_peer_context()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var first = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1", context: "old")), null));
        store.CommitBatch("bot-im-bot", new JsonArray(Message("2", context: "new")), null);
        Assert.Equal("old", store.ResolveSend(new SendRequest { DeliveryId = first }).ContextToken);
        Assert.Equal("new", store.ResolveSend(new SendRequest { PeerId = "owner" }).ContextToken);
        Assert.Throws<ArgumentException>(() => store.ResolveSend(new SendRequest { DeliveryId = first, PeerId = "intruder" }));
    }

    [Fact]
    public void Unknown_send_after_crash_blocks_automatic_delivery_until_explicit_duplicate_risk_acceptance()
    {
        using var workspace = new Workspace();
        string delivery;
        using (var store = workspace.Store())
        {
            delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null));
            store.RecordSend("client-1", "bot-im-bot", "owner", delivery, "{}", "sending", null, null);
        }
        using var reopened = workspace.Store();
        reopened.RecoverInterruptedSends();
        Assert.Equal("unknown", Assert.Single(reopened.Inbox(null, false)).State);
        Assert.Null(reopened.Claim(null, TimeSpan.FromSeconds(60)));
        Assert.Throws<InvalidOperationException>(() => reopened.Retry(delivery, null, false));
        reopened.Retry(delivery, null, true);
        Assert.False(reopened.HasUnknownSend(delivery));
        Assert.Equal(delivery, Assert.IsType<InboxRow>(reopened.Claim(null, TimeSpan.FromSeconds(60))).DeliveryId);
    }

    [Fact]
    public void Duplicate_toggle_command_changes_debug_only_once_even_after_restart()
    {
        using var workspace = new Workspace();
        string delivery;
        using (var store = workspace.Store())
        {
            delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1", text: "/toggle-debug")), null));
            Assert.Equal("Debug 模式已开启", store.ToggleDebugOnce(delivery));
        }
        using var reopened = workspace.Store();
        Assert.Equal("Debug 模式已开启", reopened.ToggleDebugOnce(delivery));
        Assert.True(reopened.GetAccount("bot@im.bot").Debug);
    }

    [Fact]
    public void Same_owner_rebind_removes_stale_credentials_context_inbox_and_send_history()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.Configure("bot-im-bot", new AccountConfiguration { Remark = "旧绑定" });
        var delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null));
        store.RecordSend("client", "bot-im-bot", "owner", delivery, "{}", "unknown", null, null);
        store.SaveAccount("replacement@im.bot", "owner", "new-token", "https://unit.test/");
        var replaced = Assert.Single(store.Accounts());
        Assert.Equal("replacement-im-bot", replaced.AccountId);
        Assert.Null(replaced.Remark);
        Assert.Empty(store.Inbox(null, true));
        Assert.False(store.HasUnknownSend(delivery));
        Assert.Null(store.ResolveSend(new SendRequest { PeerId = "owner" }).ContextToken);
    }

    [Fact]
    public async Task Account_remark_survives_restart_and_same_binding_login_until_cleared()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            store.SaveAccount("second", "second-owner", "token", "https://unit.test/");
            store.Configure("bot-im-bot", new AccountConfiguration { Remark = "  测试微信 🐱  ", ExpectedOwnerId = "owner" });
            store.Configure("bot-im-bot", new AccountConfiguration(Debug: true));
            store.SaveAccount("bot@im.bot", "owner", "new-token", "https://unit.test/");
        }

        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, TimeProvider = workspace.Clock });
        var account = channel.GetAccounts().Single(a => a.AccountId == "bot-im-bot");
        Assert.Equal("测试微信 🐱", account.Remark);
        Assert.True(account.Debug);
        Assert.Null(channel.GetAccounts().Single(a => a.AccountId == "second").Remark);
        channel.ConfigureAccount("bot-im-bot", new AccountConfiguration { Remark = "  ", ExpectedOwnerId = "owner" });
        Assert.Null(channel.GetAccounts().Single(a => a.AccountId == "bot-im-bot").Remark);
    }

    [Fact]
    public void Switching_owner_or_deleting_binding_drops_remark_and_rejects_a_stale_owner_edit()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.Configure("bot-im-bot", new AccountConfiguration { Remark = "旧用户", ExpectedOwnerId = "owner" });
        store.SaveAccount("bot@im.bot", "new-owner", "new-token", "https://unit.test/");
        Assert.Null(Assert.Single(store.Accounts()).Remark);
        Assert.Throws<InvalidOperationException>(() => store.Configure("bot-im-bot", new AccountConfiguration(Enabled: false)
        {
            Remark = "过期编辑", ExpectedOwnerId = "owner"
        }));
        Assert.True(Assert.Single(store.Accounts()).Enabled);
        Assert.Null(Assert.Single(store.Accounts()).Remark);

        store.Configure("bot-im-bot", new AccountConfiguration { Remark = "新用户", ExpectedOwnerId = "new-owner" });
        store.DeleteAccount("bot-im-bot");
        store.SaveAccount("bot@im.bot", "new-owner", "new-token", "https://unit.test/");
        Assert.Null(Assert.Single(store.Accounts()).Remark);
    }

    [Fact]
    public void Oversized_or_malformed_remark_cannot_overwrite_a_valid_remark()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var valid = new string('中', 38) + "🐱";
        store.Configure("bot-im-bot", new AccountConfiguration { Remark = valid });
        foreach (var invalid in new[] { valid + "字", "用户\n备注", new string((char)0xD800, 1) })
            Assert.Throws<ArgumentException>(() => store.Configure("bot-im-bot", new AccountConfiguration { Remark = invalid }));
        Assert.Equal(valid, Assert.Single(store.Accounts()).Remark);
    }

    [Fact]
    public void Multiaccount_selection_requires_unique_peer_context_and_never_guesses()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.SaveAccount("second", "second-owner", "token", "https://unit.test/");
        Assert.Throws<InvalidOperationException>(() => store.ResolveSend(new SendRequest { PeerId = "owner" }));
        store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        Assert.Equal("bot-im-bot", store.ResolveSend(new SendRequest { PeerId = "owner" }).Account.AccountId);
        store.CommitBatch("second", new JsonArray(Message("1")), null);
        Assert.Throws<InvalidOperationException>(() => store.ResolveSend(new SendRequest { PeerId = "owner" }));
    }

    [Fact]
    public async Task Directory_lock_refuses_a_second_channel_and_releases_after_disposal()
    {
        using var workspace = new Workspace();
        var options = new WeixinOptions { StateDirectory = workspace.Path };
        await using (var first = new WeixinChannel(options))
            Assert.Contains("状态目录", Assert.Throws<IOException>(() => new WeixinChannel(options)).Message);
        await using var reopened = new WeixinChannel(options);
        Assert.Empty(reopened.GetAccounts());
    }

    [Fact]
    public async Task Unauthorized_sender_is_terminal_without_media_network_or_command_execution()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            var message = Message("1", "attacker", "/toggle-debug");
            message["item_list"]!.AsArray().Add(new JsonObject { ["type"] = 2, ["image_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://forbidden.test/image" } } });
            store.CommitBatch("bot-im-bot", new JsonArray(message), null);
        }
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { Interlocked.Increment(ref requests); throw new InvalidOperationException("must not request"); }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        Assert.Null(await channel.ClaimAsync());
        Assert.False(Assert.Single(channel.GetAccounts()).Debug);
        Assert.Equal("denied", Assert.Single(channel.GetInbox(includeCompleted: true)).State);
        Assert.Equal("unauthorized", Assert.Single(channel.GetInbox(includeCompleted: true)).LastError);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Sse_disconnect_does_not_claim_or_ack_pending_delivery()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, TimeProvider = workspace.Clock });
        await using (var events = channel.EventsAsync().GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("pending", events.Current.Kind);
            Assert.Equal("1", events.Current.Detail);
        }
        Assert.Equal("pending", Assert.Single(channel.GetInbox()).State);
        var message = Assert.IsType<InboundMessage>(await channel.ClaimAsync());
        await channel.AcknowledgeAsync(message.DeliveryId, message.LeaseId);
        Assert.Empty(channel.GetInbox());
    }

    [Fact]
    public async Task Cancelling_long_poll_interrupts_request_and_best_effort_stop_is_sent()
    {
        using var workspace = new Workspace();
        using (workspace.Store()) { }
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal))
            {
                polling.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("notifystop", StringComparison.Ordinal)) stopped = true;
            return Json("{\"ret\":0}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http });
        using var cancellation = new CancellationTokenSource();
        var run = channel.RunAsync("bot@im.bot", cancellationToken: cancellation.Token);
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stopped);
        Assert.False(Assert.Single(channel.GetAccounts()).Running);
    }

    [Fact]
    public async Task Cancelled_handler_releases_lease_for_same_delivery_after_restart()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        var received = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return Json("{\"ret\":0}");
        }));
        string delivery;
        await using (var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock }))
        {
            using var cancellation = new CancellationTokenSource();
            var run = channel.RunAsync("bot-im-bot", async (message, token) => { received.TrySetResult(message); await Task.Delay(Timeout.Infinite, token); }, cancellation.Token);
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            delivery = message.DeliveryId;
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ClaimAsync("bot-im-bot"));
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("pending", Assert.Single(channel.GetInbox()).State);
        }
        await using var reopened = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        Assert.Equal(delivery, Assert.IsType<InboundMessage>(await reopened.ClaimAsync()).DeliveryId);
    }

    [Fact]
    public async Task Stale_session_in_either_error_field_pauses_account_without_extra_polls()
    {
        using var workspace = new Workspace();
        using (workspace.Store()) { }
        var polls = 0;
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal) && Interlocked.Increment(ref polls) > 0 ? "{\"ret\":-1,\"errcode\":-14}" : "{\"ret\":0}"))));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var cancellation = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", cancellationToken: cancellation.Token);
        await EventuallyAsync(() => channel.GetAccounts()[0].PausedUntil is not null);
        Assert.Equal(Start.AddHours(1), Assert.Single(channel.GetAccounts()).PausedUntil);
        Assert.Equal(1, polls);
        cancellation.Cancel();
        await run;
    }

    [Fact]
    public void Identifierless_empty_items_preserve_distinct_events_instead_of_collapsing_equal_payloads()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var message = new JsonObject { ["from_user_id"] = "owner", ["item_list"] = new JsonArray() };
        store.CommitBatch("bot-im-bot", new JsonArray(message.DeepClone(), message.DeepClone()), null);
        Assert.Equal(2, store.Inbox(null, false).Count);
    }

    [Fact]
    public async Task Public_account_json_never_exposes_token()
    {
        using var workspace = new Workspace();
        using (workspace.Store()) { }
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path });
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(channel.GetAccounts()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "unknown")]
    public async Task Handler_ignoring_reply_failure_cannot_ack_a_business_response_that_did_not_succeed(bool unknown, string expectedState)
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            if (request.RequestUri.AbsolutePath.EndsWith("sendmessage", StringComparison.Ordinal))
            {
                if (unknown) throw new HttpRequestException("connection lost");
                return Json("{\"ret\":4001}");
            }
            return Json("{\"ret\":0}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", async (message, token) =>
        {
            await message.ReplyAsync("business response", cancellationToken: token);
            replied.TrySetResult();
        }, stop.Token);
        await replied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => channel.GetInbox().Single().State == expectedState);
        Assert.Equal(expectedState, Assert.Single(channel.GetInbox(includeCompleted: true)).State);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Poll_failures_retry_after_two_seconds_and_back_off_thirty_after_three_failures()
    {
        using var workspace = new Workspace();
        using (workspace.Store()) { }
        var polls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) return Task.FromResult(Json("{\"ret\":0}"));
            Interlocked.Increment(ref polls);
            return Task.FromResult(Json("{\"ret\":1}"));
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", cancellationToken: stop.Token);
        Assert.Equal(1, polls);
        workspace.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(1, polls);
        workspace.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => polls == 2);
        await Task.Delay(20);
        workspace.Clock.Advance(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => polls == 3);
        await Task.Delay(20);
        workspace.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(3, polls);
        workspace.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => polls == 4);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Server_long_poll_timeout_is_adopted_without_treating_normal_expiration_as_failure()
    {
        using var workspace = new Workspace();
        using (workspace.Store()) { }
        var polls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) return Json("{\"ret\":0}");
            if (Interlocked.Increment(ref polls) == 1) return Json("{\"ret\":0,\"longpolling_timeout_ms\":7000,\"msgs\":[],\"get_updates_buf\":\"server-cursor\"}");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("server-cursor", body["get_updates_buf"]!.GetValue<string>());
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", cancellationToken: stop.Token);
        Assert.Equal(2, polls);
        workspace.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(2, polls);
        workspace.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => polls == 3);
        Assert.Null(Assert.Single(channel.GetAccounts()).LastError);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Unix_state_directory_and_credential_database_are_private_to_the_current_user()
    {
        using var workspace = new Workspace();
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path });
        var database = System.IO.Path.Combine(workspace.Path, "core.sqlite");
        Assert.True(File.Exists(database));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(workspace.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database));
        }
    }

    [Fact]
    public void Official_id_fallback_scans_all_items_and_trims_whitespace_to_prevent_duplicate_delivery()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var message = Message("1");
        message["message_id"] = "   ";
        message["item_list"]!.AsArray().Add(new JsonObject { ["msg_id"] = " 18446744073709551615 " });
        Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(message.DeepClone()), null));
        Assert.Empty(store.CommitBatch("bot-im-bot", new JsonArray(message.DeepClone()), null));
        Assert.Equal("18446744073709551615", Assert.Single(store.Inbox(null, false)).MessageId);
    }

    [Fact]
    public void Disabled_and_session_paused_accounts_do_not_process_backlog_but_other_accounts_continue()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.SaveAccount("other", "other-owner", "token", "https://unit.test");
        store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        store.CommitBatch("other", new JsonArray(Message("2", "other-owner")), null);
        store.Configure("bot-im-bot", new AccountConfiguration(Enabled: false));
        Assert.Null(store.Claim("bot-im-bot", TimeSpan.FromMinutes(1)));
        var other = Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromMinutes(1)));
        Assert.Equal("other", other.AccountId);
        store.Complete(other.DeliveryId, other.LeaseId);
        store.Configure("bot-im-bot", new AccountConfiguration(Enabled: true));
        store.Pause("bot-im-bot", TimeSpan.FromHours(1));
        Assert.Null(store.Claim(null, TimeSpan.FromMinutes(1)));
        workspace.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("bot-im-bot", Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromMinutes(1))).AccountId);
    }

    [Fact]
    public async Task Unknown_debug_timing_notice_does_not_replay_successful_business_handler()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            store.Configure("bot-im-bot", new AccountConfiguration(Debug: true));
            store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        }
        var notices = 0;
        string? observedContext = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            if (request.RequestUri.AbsolutePath.EndsWith("sendmessage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref notices);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                observedContext = body["msg"]?["context_token"]?.GetValue<string>();
                throw new HttpRequestException("notice lost");
            }
            return Json("{\"ret\":0}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", (_, _) => Task.CompletedTask, stop.Token);
        await EventuallyAsync(() => channel.GetInbox(includeCompleted: true).Single().State == "acked");
        Assert.Equal(1, notices);
        Assert.Equal("context-original", observedContext);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Account_removal_erases_unacknowledged_downloads_as_well_as_persistent_messages()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            var message = Message("1");
            message["item_list"]!.AsArray().Add(new JsonObject { ["type"] = 2, ["image_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://unit.test/image" } } });
            store.CommitBatch("bot-im-bot", new JsonArray(message), null);
        }
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([137, 80, 78, 71, 13, 10, 26, 10]) })));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        var incoming = Assert.IsType<InboundMessage>(await channel.ClaimAsync());
        var path = Assert.Single(incoming.Attachments).Path;
        Assert.True(File.Exists(path));
        await channel.DeleteAccountAsync("bot-im-bot");
        Assert.False(File.Exists(path));
        Assert.Empty(channel.GetAccounts());
        Assert.Empty(channel.GetInbox(includeCompleted: true));
    }

    [Fact]
    public void Explicit_null_json_values_are_invalid_input_instead_of_internal_server_errors()
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null));
        var request = JsonSerializer.Deserialize<SendRequest>("{\"PeerId\":null}")! with { DeliveryId = delivery };
        Assert.Throws<ArgumentNullException>(() => store.ResolveSend(request));
        var configuration = JsonSerializer.Deserialize<AccountConfiguration>("{\"Allowlist\":[null]}")!;
        Assert.Throws<ArgumentException>(() => store.Configure("bot-im-bot", configuration));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("sending")]
    public void Unknown_manual_send_blocks_expired_redelivery_and_same_account_tail_without_blocking_other_accounts(string sendState)
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        store.SaveAccount("other", "other-owner", "token", "https://unit.test/");
        store.CommitBatch("bot-im-bot", new JsonArray(Message("1"), Message("2")), null);
        store.CommitBatch("other", new JsonArray(Message("3", "other-owner")), null);
        var claimed = Assert.IsType<InboxRow>(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60)));
        store.RecordSend("client", "bot-im-bot", "owner", claimed.DeliveryId, "{}", sendState, null, null);
        Assert.Null(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60)));
        workspace.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(store.Claim("bot-im-bot", TimeSpan.FromSeconds(60)));
        Assert.Equal("other", Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60))).AccountId);
        Assert.Equal(1, store.Inbox("bot-im-bot", false)[0].Attempts);
        Assert.Equal(0, store.Inbox("bot-im-bot", false)[1].Attempts);
        if (sendState == "unknown") Assert.Equal("unknown", store.Inbox("bot-im-bot", false)[0].State);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Invalid_wire_time_preserves_raw_evidence_but_uses_receipt_time_to_keep_fifo_claimable(long invalid)
    {
        using var workspace = new Workspace();
        using var store = workspace.Store();
        var message = Message("1");
        message["create_time_ms"] = invalid;
        store.CommitBatch("bot-im-bot", new JsonArray(message, Message("2")), null);
        var first = Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60)));
        Assert.Equal(Start, first.CreatedAt);
        Assert.Equal(invalid, JsonNode.Parse(first.Raw)!["create_time_ms"]!.GetValue<long>());
        store.Complete(first.DeliveryId, first.LeaseId);
        Assert.Equal("2", Assert.IsType<InboxRow>(store.Claim(null, TimeSpan.FromSeconds(60))).MessageId);
    }

    [Fact]
    public async Task Nonarray_poll_messages_never_advance_the_durable_cursor()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", [], "previous");
        var observedCursors = new List<string>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) return Json("{\"ret\":0}");
            observedCursors.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["get_updates_buf"]!.GetValue<string>());
            if (observedCursors.Count == 1) return Json("{\"ret\":0,\"msgs\":{\"message_id\":99},\"get_updates_buf\":\"must-not-commit\"}");
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        }));
        await using (var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock }))
        {
            using var stop = new CancellationTokenSource();
            var run = channel.RunAsync("bot-im-bot", cancellationToken: stop.Token);
            workspace.Clock.Advance(TimeSpan.FromSeconds(2));
            await EventuallyAsync(() => observedCursors.Count == 2);
            stop.Cancel();
            await run;
            Assert.Equal(new[] { "previous", "previous" }, observedCursors);
        }
        using var reopened = workspace.Store();
        Assert.Equal("previous", reopened.Cursor("bot-im-bot"));
        Assert.Empty(reopened.Inbox(null, false));
    }

    [Fact]
    public async Task Text_builtin_echo_runs_before_an_unavailable_media_attachment()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            var message = Message("1", text: "/echo business");
            message["item_list"]!.AsArray().Insert(0, new JsonObject { ["type"] = 2, ["image_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://unavailable.test/media" } } });
            store.CommitBatch("bot-im-bot", new JsonArray(message), null);
        }
        var downloads = 0;
        string? responseText = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "unavailable.test") { downloads++; throw new HttpRequestException("media unavailable"); }
            responseText = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))?["msg"]?["item_list"]?[0]?["text_item"]?["text"]?.GetValue<string>();
            return Json("{\"ret\":0,\"message_id\":10}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        Assert.Null(await channel.ClaimAsync());
        Assert.Equal(0, downloads);
        Assert.StartsWith("business\n", responseText, StringComparison.Ordinal);
        Assert.Equal("acked", Assert.Single(channel.GetInbox(includeCompleted: true)).State);
    }

    [Fact]
    public async Task Builtin_command_skips_text_items_without_a_body_and_uses_the_next_text_item()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            var message = Message("1", text: "/toggle-debug");
            message["item_list"]!.AsArray().Insert(0, new JsonObject { ["type"] = 1 });
            store.CommitBatch("bot-im-bot", new JsonArray(message), null);
        }
        var sends = 0;
        using var http = new HttpClient(new Handler((_, _) => { sends++; return Task.FromResult(Json("{\"ret\":0,\"message_id\":10}")); }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        Assert.Null(await channel.ClaimAsync());
        Assert.True(Assert.Single(channel.GetAccounts()).Debug);
        Assert.Equal("acked", Assert.Single(channel.GetInbox(includeCompleted: true)).State);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("/echo business")]
    [InlineData("/toggle-debug")]
    public async Task Support_can_disable_builtins_so_customer_commands_enter_business_history(string text)
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", new JsonArray(Message("1", text: text)), null);
        var sends = 0;
        using var http = new HttpClient(new Handler((_, _) => { sends++; return Task.FromResult(Json("{\"ret\":0}")); }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock, EnableBuiltinCommands = false });
        var inbound = Assert.IsType<InboundMessage>(await channel.ClaimAsync());
        Assert.Equal(text, inbound.Text);
        Assert.Equal(0, sends);
        Assert.False(Assert.Single(channel.GetAccounts()).Debug);
        Assert.Equal("leased", Assert.Single(channel.GetInbox()).State);
    }

    [Fact]
    public async Task Voice_transcription_is_business_text_and_cannot_execute_text_commands()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store())
        {
            var message = Message("1");
            message["item_list"] = new JsonArray(new JsonObject { ["type"] = 3, ["voice_item"] = new JsonObject { ["text"] = "/toggle-debug" } });
            store.CommitBatch("bot-im-bot", new JsonArray(message), null);
        }
        var sends = 0;
        using var http = new HttpClient(new Handler((_, _) => { sends++; return Task.FromResult(Json("{\"ret\":0}")); }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        Assert.Equal("/toggle-debug", Assert.IsType<InboundMessage>(await channel.ClaimAsync()).Text);
        Assert.False(Assert.Single(channel.GetAccounts()).Debug);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Host_work_longer_than_sixty_seconds_is_renewed_without_duplicate_delivery_and_eventually_acked()
    {
        using var workspace = new Workspace();
        using (var store = workspace.Store()) store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getupdates", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
            return Json("{\"ret\":0}");
        }));
        await using var channel = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, HttpClient = http, TimeProvider = workspace.Clock });
        using var stop = new CancellationTokenSource();
        var run = channel.RunAsync("bot-im-bot", async (_, token) => { handled++; entered.TrySetResult(); await finish.Task.WaitAsync(token); }, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
        {
            workspace.Clock.Advance(TimeSpan.FromSeconds(20));
            var expected = workspace.Clock.GetUtcNow().AddSeconds(60);
            await EventuallyAsync(() => channel.GetInbox()[0].LeaseUntil >= expected);
            await Task.Delay(5);
        }
        Assert.Equal(1, handled);
        Assert.Equal(1, Assert.Single(channel.GetInbox()).Attempts);
        finish.SetResult();
        await EventuallyAsync(() => channel.GetInbox(includeCompleted: true)[0].State == "acked");
        Assert.Equal(1, handled);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task Receipts_survive_restart_and_expose_only_segment_results_for_unknown_investigation()
    {
        using var workspace = new Workspace();
        string delivery;
        using (var store = workspace.Store())
        {
            delivery = Assert.Single(store.CommitBatch("bot-im-bot", new JsonArray(Message("1")), null));
            store.RecordSend("caption-client", "bot-im-bot", "owner", delivery, "private context token payload", "success", "18446744073709551615", null);
            store.RecordSend("media-client", "bot-im-bot", "owner", delivery, "private key payload", "unknown", null, "需要核查");
        }
        await using var reopened = new WeixinChannel(new WeixinOptions { StateDirectory = workspace.Path, TimeProvider = workspace.Clock });
        var segments = reopened.GetSendSegments(delivery);
        Assert.Equal(new[] { "caption-client", "media-client" }, segments.Select(segment => segment.ClientId));
        Assert.Equal("18446744073709551615", segments[0].MessageId);
        Assert.Equal("unknown", segments[1].Status);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(segments), StringComparison.Ordinal);
    }
}
