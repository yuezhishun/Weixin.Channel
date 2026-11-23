using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class OwnerConversationTests : IDisposable
{
    private const string Bot = "bot-one";
    private const string Owner = "owner-one";
    private const string Secret = "fixture-secret";
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("weixin-owner-chat-");
    private readonly FakeTimeProvider _clock = new(Timestamp);

    [Fact]
    public async Task Owner_history_excludes_other_conversations_and_keeps_inbox_leases_unchanged()
    {
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, Secret, "https://unit.test/");
            store.SaveAccount("bot-two", "owner-two", Secret, "https://unit.test/");
            var acknowledged = Assert.Single(store.CommitBatch(Bot, [Message("ack", Owner, "已经确认")], null));
            var first = Assert.IsType<InboxRow>(store.Claim(Bot, TimeSpan.FromMinutes(1)));
            store.Complete(acknowledged, first.LeaseId);
            var denied = Assert.Single(store.CommitBatch(Bot, [Message("denied", Owner, "已拒绝内容")], null));
            var second = Assert.IsType<InboxRow>(store.Claim(Bot, TimeSpan.FromMinutes(1)));
            store.Complete(denied, second.LeaseId, denied: true, reason: "unauthorized");

            var file = Message("file", Owner, "附件说明");
            file["item_list"]!.AsArray().Add(new JsonObject
            {
                ["type"] = 4,
                ["file_item"] = new JsonObject
                {
                    ["file_name"] = $"C:\\{Secret}\\收据.txt",
                    ["media"] = new JsonObject { ["full_url"] = "https://forbidden.test/" + Secret, ["aes_key"] = Secret }
                }
            });
            store.CommitBatch(Bot, [file], null);
            Assert.NotNull(store.Claim(Bot, TimeSpan.FromMinutes(1)));
            var group = Message("group", Owner, "群消息");
            group["group_id"] = "group-one";
            var nonUser = Message("system", Owner, "非用户消息");
            nonUser["message_type"] = 2;
            store.CommitBatch(Bot, [group, nonUser, Message("peer", "other-peer", "其他对端")], null);
            store.CommitBatch("bot-two", [Message("second-bot", "owner-two", "另一个 Bot")], null);
            store.RecordSend("sent", Bot, Owner, null, Wire(TextItem("主动发送")), "unknown", null, "待核查");
            store.RecordSend("other-peer", Bot, "other-peer", null, Wire(TextItem("发往其他对端")), "success", "2", null);
            store.RecordSend("tool", Bot, Owner, null, Wire(new JsonObject { ["type"] = 11 }), "success", "3", null);
        }

        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            throw new InvalidOperationException("查看历史不应下载附件。");
        }));
        await using var channel = OpenChannel(http);
        var inboxBefore = channel.GetInbox(Bot, includeCompleted: true).ToArray();

        var history = channel.GetOwnerConversation(Bot);

        Assert.Equal(Owner, history.OwnerId);
        Assert.True(history.HasSendContext);
        Assert.Equal(3, history.Items.Count);
        Assert.Contains(history.Items, row => row.Direction == "inbound" && row.Text == "已经确认");
        var fileRow = Assert.Single(history.Items, row => row.Direction == "inbound" && row.Text == "附件说明");
        var attachment = Assert.Single(fileRow.Attachments);
        Assert.Equal("file", attachment.Kind);
        Assert.Equal("收据.txt", attachment.Name);
        var outgoing = Assert.Single(history.Items, row => row.Direction == "outbound");
        Assert.Equal("主动发送", outgoing.Text);
        Assert.Equal("unknown", outgoing.Status);
        Assert.Equal("另一个 Bot", Assert.Single(channel.GetOwnerConversation("bot-two").Items).Text);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(history));
        Assert.Equal(inboxBefore, channel.GetInbox(Bot, includeCompleted: true).ToArray());
        Assert.Equal(0, requests);

        channel.ConfigureAccount(Bot, new AccountConfiguration(Allowlist: ["other-peer"]));
        var restrictedHistory = channel.GetOwnerConversation(Bot);
        Assert.False(restrictedHistory.HasSendContext);
        Assert.DoesNotContain(restrictedHistory.Items, row => row.Direction == "inbound");
    }

    [Fact]
    public async Task Owner_history_cursor_keeps_equal_timestamp_messages_reachable_after_restart()
    {
        const int count = 55;
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, Secret, "https://unit.test/");
            for (var i = 0; i < count; i++)
            {
                var text = $"message-{i:D2}";
                if (i % 2 == 0) store.CommitBatch(Bot, [Message(text, Owner, text)], null);
                else store.RecordSend(text, Bot, Owner, null, Wire(TextItem(text)), "success", null, null);
            }
        }

        string before;
        string[] firstIds;
        string[] firstTexts;
        await using (var channel = OpenChannel())
        {
            var first = channel.GetOwnerConversation(Bot);
            Assert.Equal(50, first.Items.Count);
            Assert.All(first.Items, row => Assert.Equal(Timestamp, row.At));
            before = Assert.IsType<string>(first.NextBefore);
            firstIds = first.Items.Select(row => row.Id).ToArray();
            firstTexts = first.Items.Select(row => row.Text).ToArray();
        }

        await using var reopened = OpenChannel();
        Assert.Equal(firstIds, reopened.GetOwnerConversation(Bot).Items.Select(row => row.Id));
        var older = reopened.GetOwnerConversation(Bot, before);
        Assert.Equal(count - firstIds.Length, older.Items.Count);
        Assert.Null(older.NextBefore);
        var actual = firstTexts.Concat(older.Items.Select(row => row.Text)).Order(StringComparer.Ordinal);
        Assert.Equal(Enumerable.Range(0, count).Select(i => $"message-{i:D2}"), actual);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "success")]
    [InlineData(HttpStatusCode.BadGateway, "unknown")]
    public async Task Owner_send_uses_current_binding_once_and_persists_the_upstream_result(HttpStatusCode upstreamStatus, string expected)
    {
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, "owner-bot-token", "https://unit.test/");
            store.SaveAccount("bot-two", "owner-two", "second-bot-token", "https://unit.test/");
            store.CommitBatch("bot-two", [Message("other", Owner, "其他 Bot 的上下文")], null);
            store.CommitBatch(Bot, [Message("old", Owner, "旧消息", "old-context"), Message("new", Owner, "新消息", "new-context")], null);
        }

        var requests = new List<(string? Path, string? Token, JsonNode Message)>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            requests.Add((request.RequestUri?.AbsolutePath, request.Headers.Authorization?.Parameter, body!["msg"]!));
            return new HttpResponseMessage(upstreamStatus) { Content = new StringContent("{\"ret\":0,\"message_id\":\"123\"}", Encoding.UTF8, "application/json") };
        }));
        await using var channel = OpenChannel(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendOwnerMessageAsync(Bot, "owner-two", "不能串号"));
        await Assert.ThrowsAsync<ArgumentException>(() => channel.SendOwnerMessageAsync(Bot, Owner, " \n "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.SendOwnerMessageAsync("missing", Owner, "不能猜账号"));
        Assert.Empty(requests);

        var result = await channel.SendOwnerMessageAsync(Bot, Owner, "网页主动消息");

        Assert.Equal(expected, result.Status);
        var (path, token, message) = Assert.Single(requests);
        Assert.Equal("/ilink/bot/sendmessage", path);
        Assert.Equal("owner-bot-token", token);
        Assert.Equal(Owner, message["to_user_id"]?.GetValue<string>());
        Assert.Equal("new-context", message["context_token"]?.GetValue<string>());
        var sent = Assert.Single(channel.GetOwnerConversation(Bot).Items, row => row.Direction == "outbound");
        Assert.Equal("网页主动消息", sent.Text);
        Assert.Equal(expected, sent.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_owner_reply_keeps_inbox_consumable_and_allows_another_manual_reply(bool acknowledgedBeforeReply)
    {
        string first;
        string second;
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, Secret, "https://unit.test/");
            var deliveries = store.CommitBatch(Bot, [Message("first", Owner, "先收到", "first-context"),
                Message("second", Owner, "后收到", "second-context")], null);
            Assert.Equal(2, deliveries.Count);
            first = deliveries[0];
            second = deliveries[1];
            if (acknowledgedBeforeReply)
            {
                var lease = Assert.IsType<InboxRow>(store.Claim(Bot, TimeSpan.FromMinutes(1)));
                store.Complete(first, lease.LeaseId);
            }
        }

        var contexts = new List<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            contexts.Add(payload["msg"]!["context_token"]!.GetValue<string>());
            return JsonResponse("""{"ret":0,"message_id":"123"}""",
                contexts.Count == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK);
        }));
        await using var channel = OpenChannel(http);

        var unknown = await channel.SendOwnerMessageAsync(Bot, Owner, "送达待核查的回复", null, first);
        Assert.Equal("unknown", unknown.Status);
        Assert.Equal("first-context", Assert.Single(contexts));

        if (!acknowledgedBeforeReply)
        {
            var firstClaim = Assert.IsType<InboundMessage>(await channel.ClaimAsync(Bot));
            Assert.Equal(first, firstClaim.DeliveryId);
            await channel.AcknowledgeAsync(first, firstClaim.LeaseId);
        }
        var secondClaim = Assert.IsType<InboundMessage>(await channel.ClaimAsync(Bot));
        Assert.Equal(second, secondClaim.DeliveryId);

        var later = await channel.SendOwnerMessageAsync(Bot, Owner, "人工核查后发出的新回复", null, first);
        Assert.Equal("success", later.Status);
        Assert.Equal(new[] { "first-context", "first-context" }, contexts);
        Assert.Empty(channel.GetSendSegments(first));
        var history = channel.GetOwnerConversation(Bot).Items;
        var unknownSegment = Assert.Single(unknown.Segments);
        var laterSegment = Assert.Single(later.Segments);
        Assert.Contains(history, row => row.Id == unknownSegment.ClientId && row.Status == "unknown");
        Assert.Contains(history, row => row.Id == laterSegment.ClientId && row.Status == "success");
    }

    [Fact]
    public async Task Owner_send_waits_for_direct_inbound_and_ignores_other_contexts()
    {
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, Secret, "https://unit.test/");
            store.SaveAccount("bot-two", "owner-two", Secret, "https://unit.test/");
        }
        var contexts = new List<string?>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            contexts.Add(payload?["msg"]?["context_token"]?.GetValue<string>());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ret\":0}") };
        }));
        await using var channel = OpenChannel(http);

        await AssertWaitingAsync();
        using (var store = OpenStore())
        {
            var denied = Assert.Single(store.CommitBatch(Bot, [Message("denied", Owner, "已拒绝", "denied-context")], null));
            var lease = Assert.IsType<InboxRow>(store.Claim(Bot, TimeSpan.FromMinutes(1)));
            store.Complete(denied, lease.LeaseId, denied: true, reason: "unauthorized");
            var group = Message("group", Owner, "群消息", "group-context");
            group["group_id"] = "group-one";
            var system = Message("system", Owner, "非用户消息", "system-context");
            system["message_type"] = 2;
            store.CommitBatch(Bot, [group, system, Message("peer", "other-peer", "其他对端"),
                Message("no-context", Owner, "没有可用上下文", "")], null);
            store.CommitBatch("bot-two", [Message("other-bot", Owner, "其他 Bot")], null);
        }
        await AssertWaitingAsync();

        using (var store = OpenStore())
        {
            var newestGroup = Message("new-group", Owner, "之后的群消息", "new-group-context");
            newestGroup["group_id"] = "group-one";
            store.CommitBatch(Bot, [Message("direct", Owner, "首条有上下文的单聊", "direct-context"), newestGroup], null);
        }
        Assert.True(channel.GetOwnerConversation(Bot).HasSendContext);
        var result = await channel.SendOwnerMessageAsync(Bot, Owner, "收到首条单聊后发送");
        Assert.Equal("success", result.Status);
        Assert.Equal("direct-context", Assert.Single(contexts));

        async Task AssertWaitingAsync()
        {
            var history = channel.GetOwnerConversation(Bot);
            Assert.False(history.HasSendContext);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => channel.SendOwnerMessageAsync(Bot, Owner, "未建立会话"));
            Assert.Empty(contexts);
            Assert.DoesNotContain(channel.GetOwnerConversation(Bot).Items, row => row.Direction == "outbound");
        }
    }

    [Theory]
    [InlineData("photo.png", 1, 2, null, HttpStatusCode.OK, "success")]
    [InlineData("movie.mp4", 2, 5, "**说明**", HttpStatusCode.OK, "success")]
    [InlineData("voice.wav", 3, 4, "**说明**", HttpStatusCode.OK, "success")]
    [InlineData("voice.wav", 3, 4, "**说明**", HttpStatusCode.BadRequest, "partial")]
    [InlineData("voice.wav", 3, 4, "**说明**", HttpStatusCode.BadGateway, "unknown")]
    public async Task Owner_media_reply_uses_original_context_and_keeps_each_wire_result(string name, int uploadType, int itemType,
        string? caption, HttpStatusCode mediaResponse, string expected)
    {
        string delivery;
        string otherDelivery;
        string deniedDelivery;
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, "original-token", "https://unit.test/");
            delivery = Assert.Single(store.CommitBatch(Bot, [Message("old", Owner, "第一条", "original-context")], null));
            var lease = Assert.IsType<InboxRow>(store.Claim(Bot, TimeSpan.FromMinutes(1)));
            store.Complete(delivery, lease.LeaseId);
            var denied = Message("group", Owner, "群消息", "group-context");
            denied["group_id"] = "group-one";
            deniedDelivery = Assert.Single(store.CommitBatch(Bot, [denied], null));
            store.CommitBatch(Bot, [Message("new", Owner, "最新消息", "latest-context")], null);
            store.SaveAccount("other-bot", "other-owner", Secret, "https://other.test/");
            otherDelivery = Assert.Single(store.CommitBatch("other-bot", [Message("other", "other-owner", "另一个 Bot")], null));
        }
        var file = Path.Combine(_directory.FullName, name);
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        var calls = new List<string>();
        var sent = new List<JsonNode>();
        JsonNode? upload = null;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path == "/cdn-upload") return AcceptMediaUpload();
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (path == "/ilink/bot/getuploadurl")
            {
                upload = payload;
                return JsonResponse("""{"upload_full_url":"https://cdn.test/cdn-upload"}""");
            }
            var message = payload["msg"]!;
            sent.Add(message);
            var type = message["item_list"]![0]!["type"]!.GetValue<int>();
            return JsonResponse("""{"ret":0,"message_id":"123"}""", type == 1 ? HttpStatusCode.OK : mediaResponse);
        }));
        await using var channel = OpenChannel(http);

        await Assert.ThrowsAsync<ArgumentException>(() => channel.SendOwnerMessageAsync(Bot, Owner, caption, file, otherDelivery));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => channel.SendOwnerMessageAsync(Bot, Owner, caption, file, deniedDelivery));
        Assert.Empty(calls);

        var result = await channel.SendOwnerMessageAsync(Bot, Owner, caption, file, delivery);
        Assert.Equal(expected, result.Status);
        Assert.Equal(uploadType, upload?["media_type"]?.GetValue<int>());
        Assert.Equal(Owner, upload?["to_user_id"]?.GetValue<string>());
        Assert.Equal(caption is null ? 1 : 2, sent.Count);
        Assert.All(sent, message =>
        {
            Assert.Equal(Owner, message["to_user_id"]!.GetValue<string>());
            Assert.Equal("original-context", message["context_token"]!.GetValue<string>());
        });
        if (caption is not null)
        {
            Assert.Equal("/ilink/bot/sendmessage", calls[0]);
            Assert.Equal(caption, sent[0]["item_list"]![0]!["text_item"]!["text"]!.GetValue<string>());
        }
        Assert.Equal(new[] { "/ilink/bot/getuploadurl", "/cdn-upload", "/ilink/bot/sendmessage" }, calls.TakeLast(3));
        var mediaItem = sent[^1]["item_list"]![0]!;
        Assert.Equal(itemType, mediaItem["type"]!.GetValue<int>());
        if (itemType == 4) Assert.Equal(name, mediaItem["file_item"]!["file_name"]!.GetValue<string>());
        var outbound = channel.GetOwnerConversation(Bot).Items.Where(row => row.Direction == "outbound").ToArray();
        Assert.Equal(result.Segments.Count, outbound.Length);
        Assert.All(result.Segments, segment => Assert.Contains(outbound, row => row.Id == segment.ClientId && row.Status == segment.Status));
        Assert.Empty(channel.GetSendSegments(delivery));
    }

    [Theory]
    [InlineData("/ilink/bot/sendmessage", 1)]
    [InlineData("/ilink/bot/getuploadurl", 2)]
    [InlineData("/cdn-upload", 3)]
    public async Task Rebinding_during_media_reply_stops_at_the_completed_stage_and_never_writes_to_replacement(string switchAfter, int expectedCalls)
    {
        string delivery;
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, "original-token", "https://unit.test/");
            delivery = Assert.Single(store.CommitBatch(Bot, [Message("reply", Owner, "来信")], null));
        }
        var file = Path.Combine(_directory.FullName, "attachment.txt");
        await File.WriteAllTextAsync(file, "附件");
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path == switchAfter)
            {
                using var store = OpenStore();
                store.DeleteAccount(Bot);
                store.SaveAccount(Bot, "replacement-owner", "replacement-token", "https://unit.test/");
            }
            return Task.FromResult(path switch
            {
                "/ilink/bot/getuploadurl" => JsonResponse("""{"upload_full_url":"https://cdn.test/cdn-upload"}"""),
                "/cdn-upload" => AcceptMediaUpload(),
                _ => JsonResponse("""{"ret":0,"message_id":"123"}""")
            });
        }));
        await using var channel = OpenChannel(http);

        var result = await channel.SendOwnerMessageAsync(Bot, Owner, "先发说明", file, delivery);

        Assert.Equal("partial", result.Status);
        Assert.Collection(result.Segments, part => Assert.Equal("success", part.Status), part => Assert.Equal("failed", part.Status));
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Equal(switchAfter, calls[^1]);
        Assert.Empty(channel.GetOwnerConversation(Bot).Items);
    }

    [Fact]
    public async Task Changing_owner_between_segments_cannot_send_remaining_text_with_replacement_credentials()
    {
        using (var store = OpenStore())
        {
            store.SaveAccount(Bot, Owner, "original-token", "https://unit.test/");
            store.CommitBatch(Bot, [Message("direct", Owner, "建立当前会话")], null);
        }
        var tokens = new List<string?>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            tokens.Add(request.Headers.Authorization?.Parameter);
            using var store = OpenStore();
            store.SaveAccount(Bot, "replacement-owner", "replacement-token", "https://unit.test/");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ret\":0}") });
        }));
        await using var channel = new WeixinChannel(new WeixinOptions
        {
            StateDirectory = _directory.FullName, TimeProvider = _clock, HttpClient = http, TextChunkSize = 2
        });

        var result = await channel.SendOwnerMessageAsync(Bot, Owner, "第一第二");

        Assert.Equal("replacement-owner", Assert.Single(channel.GetAccounts()).OwnerId);
        Assert.Equal("original-token", Assert.Single(tokens));
        Assert.Equal("partial", result.Status);
        Assert.Collection(result.Segments, part => Assert.Equal("success", part.Status), part => Assert.Equal("failed", part.Status));
    }

    private CoreStore OpenStore() => new(_directory.FullName, _clock);

    private WeixinChannel OpenChannel(HttpClient? http = null) => new(new WeixinOptions
    {
        StateDirectory = _directory.FullName,
        TimeProvider = _clock,
        HttpClient = http
    });

    private static JsonObject Message(string id, string peer, string text, string context = Secret) => new()
    {
        ["message_id"] = id,
        ["from_user_id"] = peer,
        ["message_type"] = 1,
        ["context_token"] = context,
        ["create_time_ms"] = Timestamp.ToUnixTimeMilliseconds(),
        ["item_list"] = new JsonArray(TextItem(text))
    };

    private static JsonObject TextItem(string value) => new()
    {
        ["type"] = 1,
        ["text_item"] = new JsonObject { ["text"] = value }
    };

    private static string Wire(JsonObject item) => new JsonObject
    {
        ["msg"] = new JsonObject { ["context_token"] = Secret, ["item_list"] = new JsonArray(item) }
    }.ToJsonString();

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage AcceptMediaUpload()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-encrypted-param", "uploaded");
        return response;
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> request) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken) => request(message, cancellationToken);
    }
}
