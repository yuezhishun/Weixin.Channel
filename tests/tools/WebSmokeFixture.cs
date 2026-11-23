#:project ../../src/Weixin.Channel/Weixin.Channel.csproj
#:property PublishAot=false
#:property RestoreLockedMode=true

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

if (args.Length != 2) throw new ArgumentException("用法：WebSmokeFixture.cs <artifacts内测试状态目录> <artifacts内证据目录>");
var artifactRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../../artifacts"));
var stateDirectory = RequireArtifactPath(args[0]);
var evidenceDirectory = RequireArtifactPath(args[1]);
const string account = "web-fixture";
const string peer = "fixture-owner";
const string delivery = "web-sse-fixture";
const string receiptDelivery = "web-receipt-fixture";
const string messageId = "18446744073709551615";
const string secret = "FIXTURE_SECRET_MUST_NOT_APPEAR";
const string otherAccount = "web-fixture-other";
const string otherOwner = "other-owner";
const string coldAccount = "web-fixture-cold";
const string coldOwner = "fresh-owner";
const string historyPath = "/api/accounts/" + account + "/messages";
const string mediaPath = historyPath + "/media";
const string attachmentPath = historyPath + "/inbound/" + receiptDelivery + "/attachments/1";
const string inboundText = "HTTP SSE 持久性合成消息";
const string outboundText = "独立发送的合成消息";
const string transcript = "合成语音转写";
const string fileName = "fixture-report.txt";
using var database = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(stateDirectory, "core.sqlite"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
}.ToString());
database.Open();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:3083"), Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.Add("x-weixin-request", "1");
var openapiText = await client.GetStringAsync("/openapi/v1.json", timeout.Token);
File.WriteAllText(Path.Combine(evidenceDirectory, "openapi.json"), openapiText);
var openapi = JsonNode.Parse(openapiText)!.AsObject();
Check(openapi["openapi"]?.GetValue<string>().StartsWith("3.1.", StringComparison.Ordinal) == true,
    "OpenAPI 应保持 3.1，以兼容现有客户端生成工具。");
VerifyContract();
Seed();
await VerifyAccountRemark();
var before = Snapshot();
await VerifyHistory();
Check(JsonNode.DeepEquals(before, Snapshot()), "浏览聊天记录改变了待办或租约。");
await VerifyRejectedSends();
await VerifyMediaRequests();
File.WriteAllText(Path.Combine(evidenceDirectory, "sse-before.json"), before.ToJsonString());
using (var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token))
{
    Check(response.StatusCode == HttpStatusCode.OK, "SSE 未成功连接。");
    using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
    using var reader = new StreamReader(stream);
    Check(await reader.ReadLineAsync(timeout.Token) == "event: pending", "SSE 首帧不是 pending 摘要。");
    var line = await reader.ReadLineAsync(timeout.Token);
    Check(line?.StartsWith("data: ", StringComparison.Ordinal) == true, "SSE 缺少摘要数据。");
    var summary = JsonNode.Parse(line![6..])!;
    Check(summary["count"]!.GetValue<int>() == 1 && summary["accounts"]![0]!["accountId"]!.GetValue<string>() == account,
        "SSE 没有报告已持久化的合成待办。");
    File.WriteAllText(Path.Combine(evidenceDirectory, "events.txt"), "event: pending\n" + line + "\n\n");
}
var after = Snapshot();
File.WriteAllText(Path.Combine(evidenceDirectory, "sse-after.json"), after.ToJsonString());
Check(JsonNode.DeepEquals(before, after), "SSE 连接/断开改变了 state、attempts 或 lease。");
Check(before["state"]!.GetValue<string>() == "pending" && before["attempts"]!.GetValue<int>() == 0 && before["lease"] is null,
    "合成待办未保持未领取状态。");
var claimed = await Post("/api/inbox/claim?accountId=" + account, null, HttpStatusCode.OK);
Check(claimed!["deliveryId"]!.GetValue<string>() == delivery && claimed["messageId"]!.GetValue<string>() == messageId,
    "SSE 断开后不能立即领取同一 DeliveryId，或服务器 uint64 标识损失。");
var lease = claimed["leaseId"]!.GetValue<string>();
var claimedSnapshot = Snapshot();
Check(claimedSnapshot["attempts"]!.GetValue<int>() == 1 && claimedSnapshot["lease"]!.GetValue<string>() == lease,
    "只有显式 claim 才应开始第一次租约。");
Check((await History(historyPath))["items"]!.AsArray().Single(item => Read(item, "id") == delivery)!["status"]!.GetValue<string>() == "leased" &&
    JsonNode.DeepEquals(claimedSnapshot, Snapshot()), "读取历史应显示现有租约状态且保留原租约。");
await Post("/api/inbox/claim?accountId=" + account, null, HttpStatusCode.NoContent);
var renewed = await Post("/api/inbox/" + delivery + "/renew", new JsonObject { ["leaseId"] = lease }, HttpStatusCode.OK);
Check(renewed?["leaseUntil"] is not null, "renew 未返回 leaseUntil。");
await Post("/api/inbox/" + delivery + "/ack", new JsonObject { ["leaseId"] = "stale-fixture-lease" }, HttpStatusCode.Conflict);
await Post("/api/inbox/" + delivery + "/ack", new JsonObject { ["leaseId"] = lease }, HttpStatusCode.NoContent);
var historyAfterAck = await History(historyPath);
var acknowledged = historyAfterAck["items"]!.AsArray().Single(item => Read(item, "id") == delivery);
Check(Read(acknowledged, "status") == "acked" && Read(acknowledged, "text") == inboundText,
    "Ack 后该消息应保留正文及已处理状态供查询。");
Check(historyAfterAck["hasSendContext"]!.GetValue<bool>(), "确认 Inbox 不应关闭已建立的会话发送入口。");
var receiptsText = await client.GetStringAsync("/api/inbox/" + receiptDelivery + "/receipts", timeout.Token);
var receipts = JsonNode.Parse(receiptsText)!.AsArray();
Check(receipts.Count == 3 && receipts.Select(item => item!["status"]!.GetValue<string>()).SequenceEqual(new[] { "success", "failed", "unknown" }),
    "收据查询未恢复所有持久化分段结果。");
Check(!receiptsText.Contains(secret, StringComparison.Ordinal) && receipts.All(item => item!.AsObject().Select(pair => pair.Key).All(name => name is "clientId" or "messageId" or "status" or "error")),
    "发送收据公开了协议请求或凭据字段。");
File.WriteAllText(Path.Combine(evidenceDirectory, "receipts.json"), receiptsText);
Console.WriteLine("账号聊天历史分页、媒体与引用投影、访问边界、发送参数与上传限制、HTTP 契约、SSE、租约与持久收据全部通过。合成账号未启动轮询。");

async Task VerifyAccountRemark()
{
    var path = "/api/accounts/" + account;
    await PutRemark(peer, "  测试微信  ", HttpStatusCode.NoContent);
    await PutRemark(otherOwner, "错误绑定", HttpStatusCode.Conflict);
    var accounts = JsonNode.Parse(await client.GetStringAsync("/api/accounts", timeout.Token))!.AsArray();
    Check(Read(accounts.Single(item => Read(item, "accountId") == account), "remark") == "测试微信",
        "账号列表应显示已保存的本地备注，其他扫码人不能覆盖。");

    async Task PutRemark(string owner, string remark, HttpStatusCode expected)
    {
        using var content = new StringContent(new JsonObject { ["remark"] = remark, ["expectedOwnerId"] = owner }.ToJsonString(),
            Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(path, content, timeout.Token);
        Check(response.StatusCode == expected, $"保存备注期望 {(int)expected}，实际 {(int)response.StatusCode}。");
    }
}

async Task VerifyHistory()
{
    var first = await History(historyPath);
    Check(Read(first, "ownerId") == peer, "聊天历史没有返回当前扫码账号的 owner。");
    Check(first["hasSendContext"]!.GetValue<bool>(), "已收到有上下文的单聊时应开放发送。");
    var nextBefore = Read(first, "nextBefore");
    var firstItems = first["items"]!.AsArray();
    Check(firstItems.Count == 50 && !string.IsNullOrEmpty(nextBefore), "首屏应限制为 50 条并返回下一页游标。");
    var second = await History(historyPath + "?before=" + Uri.EscapeDataString(nextBefore!));
    Check(second["hasSendContext"]!.GetValue<bool>(), "发送前置状态不能取决于当前历史页。");
    var items = firstItems.Concat(second["items"]!.AsArray()).ToArray();
    Check(items.Length == 58 && items.Select(item => Read(item, "id")).Distinct().Count() == items.Length && second["nextBefore"] is null,
        "跨页时遗漏或重复了同一时间戳的消息，或最后一页错误返回后续游标。");
    var dates = items.Select(item => item!["at"]!.GetValue<DateTimeOffset>()).ToArray();
    Check(dates.SequenceEqual(dates.OrderDescending()), "聊天历史应从较新到较旧返回。");
    var incoming = items.Single(item => Read(item, "id") == delivery);
    var outgoing = items.Single(item => Read(item, "id") == "standalone-send");
    Check(Read(incoming, "direction") == "inbound" && Read(incoming, "text") == inboundText && Read(incoming, "status") == "pending",
        "待办应以只读入站正文和状态出现在历史中。");
    Check(Read(outgoing, "direction") == "outbound" && Read(outgoing, "text") == outboundText && Read(outgoing, "status") == "success",
        "没有绑定 DeliveryId 的主动发送也应出现在当前 owner 的历史。");
    var awaiting = items.Single(item => Read(item, "id") == "accepted-without-id")!;
    Check(awaiting["awaitingDeliveryConfirmation"]!.GetValue<bool>() && !outgoing!["awaitingDeliveryConfirmation"]!.GetValue<bool>(),
        "没有消息 ID 的成功回执应标记为手机送达待核对。");
    var mixed = items.Single(item => Read(item, "id") == receiptDelivery)!;
    var attachments = mixed["attachments"]!.AsArray();
    Check(attachments.Select(item => Read(item, "kind")).SequenceEqual(new[] { "voice", "image", "video" }) &&
        attachments.Take(2).All(item => !string.IsNullOrEmpty(Read(item, "id"))) && attachments[2]!["id"] is null,
        "混合消息应保留可下载媒体，异常部分只显示占位。");
    var parts = mixed["parts"]!.AsArray();
    Check(parts.Any(part => Read(part, "text") == transcript) &&
        parts.Where(part => part?["attachment"] is not null).Select(part => Read(part!["attachment"], "kind")).SequenceEqual(new[] { "voice", "image", "video" }),
        "有转写的语音应同时保留转写和可取回的原音。");
    Check(Read(mixed["quote"], "text") == outboundText && !mixed["quote"]!["missing"]!.GetValue<bool>(),
        "按 ID 引用应从同一账号与对端的出站记录恢复正文。");
    var failed = items.Single(item => Read(item, "id") == "receipt-2");
    var file = failed!["attachments"]!.AsArray().Single();
    Check(Read(failed, "status") == "failed" && Read(file, "kind") == "file" && Read(file, "name") == fileName &&
        Read(items.Single(item => Read(item, "id") == "receipt-3"), "status") == "unknown",
        "未成功送达的分段应保留失败或未知状态，附件只返回可展示的元数据。");
    Check(items.All(item => Read(item, "id") is not ("other-account-delivery" or "other-peer-delivery" or "other-account-send" or "other-peer-send")),
        "历史泄露了其他账号或对端的消息。");
    var other = await History("/api/accounts/" + otherAccount + "/messages");
    Check(Read(other, "ownerId") == otherOwner &&
        other["items"]!.AsArray().Select(item => Read(item, "id")).SequenceEqual(new[] { "other-account-send", "other-account-delivery" }),
        "切换账号后应只返回新选中账号的历史。");
    File.WriteAllText(Path.Combine(evidenceDirectory, "history.json"), first.ToJsonString());
}

async Task<JsonObject> History(string path)
{
    var text = await client.GetStringAsync(path, timeout.Token);
    var history = JsonNode.Parse(text)!.AsObject();
    Check(!text.Contains(secret, StringComparison.Ordinal) &&
        history.All(pair => pair.Key is "ownerId" or "items" or "nextBefore" or "hasSendContext") &&
        history["items"]!.AsArray().All(item =>
            item!.AsObject().All(pair => pair.Key is "id" or "direction" or "at" or "text" or "attachments" or "status" or "quote" or "parts" or "canReply" or "awaitingDeliveryConfirmation") &&
            item["attachments"]!.AsArray().All(IsSafeAttachment) &&
            item["parts"]!.AsArray().All(part => part!.AsObject().All(pair => pair.Key is "text" or "attachment") && IsSafeAttachment(part["attachment"])) &&
            (item["quote"] is not JsonObject quote || (quote.All(pair => pair.Key is "text" or "missing" or "attachment") && IsSafeAttachment(quote["attachment"])))),
        "聊天历史公开了协议数据、凭据或附件地址。");
    return history;
}

static bool IsSafeAttachment(JsonNode? value) => value is null || value.AsObject().All(pair => pair.Key is "id" or "kind" or "name");

async Task VerifyRejectedSends()
{
    using var count = database.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM sends";
    var sendsBefore = count.ExecuteScalar();
    await Post("/api/accounts/missing/messages", new JsonObject { ["ownerId"] = peer, ["text"] = "test" }, HttpStatusCode.NotFound);
    await Post(historyPath, new JsonObject { ["ownerId"] = otherOwner, ["text"] = "test" }, HttpStatusCode.Conflict);
    await Post(historyPath, new JsonObject { ["ownerId"] = peer, ["text"] = " \t " }, HttpStatusCode.BadRequest);
    await Post(historyPath, new JsonObject { ["ownerId"] = peer, ["text"] = "test", ["replyDeliveryId"] = "other-account-delivery" },
        HttpStatusCode.BadRequest);
    var coldPath = "/api/accounts/" + coldAccount + "/messages";
    var cold = await History(coldPath);
    Check(Read(cold, "ownerId") == coldOwner && !cold["hasSendContext"]!.GetValue<bool>() && cold["items"]!.AsArray().Count == 0,
        "只绑定而未收到有效单聊的账号应等待会话上下文。");
    await Post(coldPath, new JsonObject { ["ownerId"] = coldOwner, ["text"] = "test" }, HttpStatusCode.Conflict);
    Check(Equals(sendsBefore, count.ExecuteScalar()), "请求无效或缺会话上下文时不应写入出站记录。");
}

async Task VerifyMediaRequests()
{
    using var count = database.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM sends";
    var sendsBefore = count.ExecuteScalar();
    using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
    using var blocked = await anonymous.GetAsync(attachmentPath, timeout.Token);
    Check(blocked.StatusCode == HttpStatusCode.Forbidden, "媒体读取必须保留专用请求头。");

    using var wrongOrigin = new HttpRequestMessage(HttpMethod.Get, attachmentPath);
    wrongOrigin.Headers.Add("Origin", "https://evil.example");
    using var rejected = await client.SendAsync(wrongOrigin, timeout.Token);
    Check(rejected.StatusCode == HttpStatusCode.Forbidden, "跨域来源不能读取聊天媒体。");
    using var crossAccount = await client.GetAsync(attachmentPath.Replace(account, otherAccount, StringComparison.Ordinal), timeout.Token);
    Check(crossAccount.StatusCode == HttpStatusCode.NotFound, "不能使用其他账号读取同一媒体。");
    using var absent = await client.GetAsync(historyPath + "/inbound/" + delivery + "/attachments/999", timeout.Token);
    Check(absent.StatusCode == HttpStatusCode.NotFound, "未知附件不能触发下载。");
    using var cached = await client.GetAsync(attachmentPath, timeout.Token);
    Check(cached.StatusCode == HttpStatusCode.OK && cached.Content.Headers.ContentType?.MediaType == "audio/wav" &&
        cached.Content.Headers.ContentDisposition is null &&
        (await cached.Content.ReadAsByteArrayAsync(timeout.Token)).SequenceEqual(File.ReadAllBytes(AudioFixture())),
        "已确认的消息应通过受保护端点返回可播放的本地 WAV。");

    using var coldForm = new MultipartFormDataContent();
    coldForm.Add(new StringContent(coldOwner), "ownerId");
    coldForm.Add(new ByteArrayContent("fixture"u8.ToArray()), "file", "fixture.txt");
    await CheckUpload("/api/accounts/" + coldAccount + "/messages/media", coldForm, HttpStatusCode.Conflict);
    using var missingFile = new MultipartFormDataContent();
    missingFile.Add(new StringContent(peer), "ownerId");
    await CheckUpload(mediaPath, missingFile, HttpStatusCode.BadRequest);
    using var large = new MultipartFormDataContent();
    large.Add(new StringContent(peer), "ownerId");
    large.Add(new ByteArrayContent(new byte[100 * 1024 * 1024 + 1]), "file", "too-large.bin");
    await CheckUpload(mediaPath, large, HttpStatusCode.RequestEntityTooLarge);

    Check(Equals(sendsBefore, count.ExecuteScalar()), "无效媒体上传不能产生任何出站分段。");
    var uploadDirectory = Path.Combine(stateDirectory, "media", "web-upload");
    Check(!Directory.Exists(uploadDirectory) || !Directory.EnumerateFileSystemEntries(uploadDirectory).Any(),
        "拒绝的上传残留了临时文件。");

    async Task CheckUpload(string path, MultipartFormDataContent content, HttpStatusCode expected)
    {
        using var response = await client.PostAsync(path, content, timeout.Token);
        Check(response.StatusCode == expected && response.Content.Headers.ContentType?.MediaType == "application/problem+json",
            $"{path} 上传期望 {(int)expected} ProblemDetails，实际 {(int)response.StatusCode}。");
    }
}

static string? Read(JsonNode? node, string key) => node?[key]?.GetValue<string>();

void Seed()
{
    using var count = database.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM accounts";
    Check(Convert.ToInt64(count.ExecuteScalar()) == 0, "fixture 只能写入空测试数据库。");
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var raw = new JsonObject
    {
        ["message_id"] = messageId, ["from_user_id"] = peer, ["message_type"] = 1, ["create_time_ms"] = now,
        ["context_token"] = secret,
        ["item_list"] = new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = inboundText } })
    };
    var request = new JsonObject
    {
        ["msg"] = new JsonObject
        {
            ["to_user_id"] = peer, ["context_token"] = secret, ["message_type"] = 2,
            ["item_list"] = new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = outboundText } })
        }
    };
    var mixed = raw.DeepClone().AsObject();
    mixed["item_list"]![0]!["ref_msg"] = new JsonObject { ["svr_id"] = "server-2" };
    mixed["item_list"]!.AsArray().Add(new JsonObject
    {
        ["type"] = 3,
        ["voice_item"] = new JsonObject
        {
            ["text"] = transcript,
            ["media"] = new JsonObject { ["encrypt_query_param"] = secret, ["aes_key"] = secret }
        }
    });
    mixed["item_list"]!.AsArray().Add(new JsonObject
    {
        ["type"] = 2,
        ["image_item"] = new JsonObject { ["media"] = new JsonObject { ["encrypt_query_param"] = secret, ["aes_key"] = secret } }
    });
    mixed["item_list"]!.AsArray().Add(new JsonObject { ["type"] = 5, ["video_item"] = new JsonObject { ["media"] = true } });
    var fileRequest = request.DeepClone();
    fileRequest["msg"]!["item_list"] = new JsonArray(new JsonObject
    {
        ["type"] = 4, ["file_item"] = new JsonObject
        {
            ["file_name"] = fileName,
            ["media"] = new JsonObject { ["full_url"] = "http://127.0.0.1:1/" + secret, ["aes_key"] = secret }
        }
    });
    using var transaction = database.BeginTransaction();
    using var command = database.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO accounts(id,owner,token,base_url,updated)
          VALUES($account,$peer,$secret,'http://127.0.0.1:1/',$now),
                ($otherAccount,$otherOwner,$secret,'http://127.0.0.1:1/',$now),
                ($coldAccount,$coldOwner,$secret,'http://127.0.0.1:1/',$now);
        INSERT INTO inbox(delivery,account,peer,message_id,raw,context_token,available,created)
          VALUES($delivery,$account,$peer,$message,$raw,$secret,$now,$now);
        INSERT INTO inbox(delivery,account,peer,message_id,raw,context_token,state,available,created)
          VALUES($receipt,$account,$peer,'receipt-message',$mixed,$secret,'acked',$now,$now),
                ('other-account-delivery',$otherAccount,$otherOwner,'other-message',json_set($raw,'$.from_user_id',$otherOwner),$secret,'acked',$now,$now),
                ('other-peer-delivery',$account,$otherOwner,'other-message',json_set($raw,'$.from_user_id',$otherOwner),$secret,'acked',$now,$now);
        WITH RECURSIVE extra(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM extra WHERE n<51)
        INSERT INTO inbox(delivery,account,peer,message_id,raw,context_token,state,available,created)
          SELECT 'archived-delivery-' || n,$account,$peer,'archived-message-' || n,$raw,$secret,'acked',$now,$now-1000 FROM extra;
        INSERT INTO sends(client_id,account,peer,delivery,request,status,message_id,error,created)
          VALUES('receipt-1',$account,$peer,$receipt,$request,'success','server-1',NULL,$now),
                ('receipt-2',$account,$peer,$receipt,$fileRequest,'failed',NULL,'http',$now+1),
                ('receipt-3',$account,$peer,$receipt,$request,'unknown',NULL,'timeout',$now+2),
                ('standalone-send',$account,$peer,NULL,$request,'success','server-2',NULL,$now+3),
                ('accepted-without-id',$account,$peer,NULL,$request,'success',NULL,NULL,$now+4),
                ('other-account-send',$otherAccount,$otherOwner,NULL,json_set($request,'$.msg.to_user_id',$otherOwner),'success','server-3',NULL,$now+4),
                ('other-peer-send',$account,$otherOwner,NULL,json_set($request,'$.msg.to_user_id',$otherOwner),'success','server-4',NULL,$now+4);
        """;
    command.Parameters.AddWithValue("$account", account);
    command.Parameters.AddWithValue("$peer", peer);
    command.Parameters.AddWithValue("$otherAccount", otherAccount);
    command.Parameters.AddWithValue("$otherOwner", otherOwner);
    command.Parameters.AddWithValue("$coldAccount", coldAccount);
    command.Parameters.AddWithValue("$coldOwner", coldOwner);
    command.Parameters.AddWithValue("$secret", secret);
    command.Parameters.AddWithValue("$now", now);
    command.Parameters.AddWithValue("$delivery", delivery);
    command.Parameters.AddWithValue("$receipt", receiptDelivery);
    command.Parameters.AddWithValue("$message", messageId);
    command.Parameters.AddWithValue("$raw", raw.ToJsonString());
    command.Parameters.AddWithValue("$mixed", mixed.ToJsonString());
    command.Parameters.AddWithValue("$request", request.ToJsonString());
    command.Parameters.AddWithValue("$fileRequest", fileRequest.ToJsonString());
    command.ExecuteNonQuery();
    transaction.Commit();
    var voiceAsset = Path.Combine(stateDirectory, "media", "conversation", Hash(account), Hash(peer),
        Hash("inbound\0" + receiptDelivery + "\0" + "1\0" + mixed["item_list"]![1]!.ToJsonString()));
    Directory.CreateDirectory(voiceAsset);
    File.Copy(AudioFixture(), Path.Combine(voiceAsset, "content.wav"));
}

string AudioFixture() => Path.Combine(artifactRoot, "..", "tests", "fixtures", "live", "acceptance-audio.wav");
static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

JsonObject Snapshot()
{
    using var command = database.CreateCommand();
    command.CommandText = "SELECT state,attempts,lease,lease_until,available FROM inbox WHERE delivery=$delivery";
    command.Parameters.AddWithValue("$delivery", delivery);
    using var reader = command.ExecuteReader();
    Check(reader.Read(), "fixture 投递不存在。");
    return new JsonObject
    {
        ["deliveryId"] = delivery, ["state"] = reader.GetString(0), ["attempts"] = reader.GetInt32(1),
        ["lease"] = reader.IsDBNull(2) ? null : reader.GetString(2),
        ["leaseUntil"] = reader.IsDBNull(3) ? null : reader.GetInt64(3), ["available"] = reader.GetInt64(4)
    };
}

async Task<JsonNode?> Post(string path, JsonObject? body, HttpStatusCode expected)
{
    using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    using var response = await client.PostAsync(path, content, timeout.Token);
    var text = await response.Content.ReadAsStringAsync(timeout.Token);
    Check(response.StatusCode == expected, $"{path} 期望 {(int)expected}，实际 {(int)response.StatusCode}：{text}");
    if ((int)expected >= 400) Check(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "错误响应不是 ProblemDetails。");
    return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
}

void VerifyContract()
{
    foreach (var path in new[] { "/api/messages", "/api/messages/reply", "/api/accounts/{id}/messages", "/api/accounts/{id}/messages/media" })
    {
        var schema = ResponseSchema(path, "post", "200");
        RequireProperties(schema, "accountId", "peerId", "status", "segments");
        var segments = Resolve(schema["properties"]!["segments"]!);
        Check(segments["type"]?.GetValue<string>() == "array", path + " segments 缺少数组schema。");
        RequireProperties(Resolve(segments["items"]!), "clientId", "messageId", "status", "error");
    }
    var history = ResponseSchema("/api/accounts/{id}/messages", "get", "200");
    RequireProperties(history, "ownerId", "items", "nextBefore", "hasSendContext");
    Check(history["properties"]!["hasSendContext"]!["type"]?.GetValue<string>() == "boolean", "发送上下文状态必须为布尔值。");
    var historyItem = Resolve(history["properties"]!["items"]!["items"]!);
    RequireProperties(historyItem, "id", "direction", "at", "text", "attachments", "status", "parts", "quote", "canReply", "awaitingDeliveryConfirmation");
    const string attachment = "/api/accounts/{id}/messages/{direction}/{messageId}/attachments/{attachmentId}";
    foreach (var media in new[] { "application/octet-stream", "audio/wav", "image/png", "video/mp4" })
    {
        var schema = ResponseSchema(attachment, "get", "200", media);
        Check(Read(schema, "type") == "string" && Read(schema, "format") == "binary", "附件下载应声明二进制成功响应。");
    }
    var chatBody = openapi["paths"]!["/api/accounts/{id}/messages"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
    RequireProperties(Resolve(chatBody), "ownerId", "text", "replyDeliveryId");
    var uploadBody = openapi["paths"]!["/api/accounts/{id}/messages/media"]!["post"]!["requestBody"]!["content"]!["multipart/form-data"]!["schema"]!;
    RequireProperties(Resolve(uploadBody), "ownerId", "text", "replyDeliveryId", "file");
    RequireProperties(ResponseSchema("/api/inbox/claim", "post", "200"), "deliveryId", "accountId", "peerId", "messageId", "leaseId", "leaseUntil");
    Check(openapi["paths"]!["/api/inbox/claim"]!["post"]!["responses"]!["204"] is not null, "Claim缺少204响应。");
    Check(openapi["paths"]!["/api/inbox/{id}/ack"]!["post"]!["responses"]!["204"] is not null, "Ack缺少204响应。");
    RequireProperties(ResponseSchema("/api/inbox/{id}/renew", "post", "200"), "leaseUntil");
    RequireProperties(ResponseSchema("/api/inbox/{id}/retry", "post", "200"), "duplicateRiskAccepted", "warning");
    var receipts = ResponseSchema("/api/inbox/{id}/receipts", "get", "200");
    Check(receipts["type"]?.GetValue<string>() == "array", "收据缺少数组schema。");
    RequireProperties(Resolve(receipts["items"]!), "clientId", "messageId", "status", "error");
    foreach (var path in new[] { "/api/messages", "/api/inbox/claim", "/api/inbox/{id}/ack", "/api/inbox/{id}/renew" })
        foreach (var status in new[] { "400", "403", "404", "409", "500" })
            RequireProperties(ResponseSchema(path, "post", status, "application/problem+json"), "type", "title", "status", "detail");
    foreach (var action in new[] { "ack", "retry", "renew" })
    {
        var body = openapi["paths"]![$"/api/inbox/{{id}}/{action}"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        RequireProperties(Resolve(body), "leaseId");
    }
}

JsonNode ResponseSchema(string path, string method, string status, string media = "application/json")
{
    var schema = openapi["paths"]?[path]?[method]?["responses"]?[status]?["content"]?[media]?["schema"];
    Check(schema is not null, $"OpenAPI缺少 {method} {path} {status} {media} 响应schema。");
    return Resolve(schema!);
}

JsonNode Resolve(JsonNode schema)
{
    for (var depth = 0; depth < 16 && schema["$ref"] is JsonNode reference; depth++)
    {
        var path = reference.GetValue<string>();
        Check(path.StartsWith("#/components/schemas/", StringComparison.Ordinal), "不支持的schema引用。");
        schema = openapi["components"]!["schemas"]![path[21..]]!;
    }
    return schema;
}

static void RequireProperties(JsonNode schema, params string[] names)
{
    foreach (var name in names) Check(schema["properties"]?[name] is not null, "OpenAPI schema缺少字段 " + name);
}

string RequireArtifactPath(string path)
{
    var fullPath = Path.GetFullPath(path);
    Check(fullPath.StartsWith(artifactRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
        "fixture 只允许显式指定本项目 artifacts 下的测试目录。");
    return fullPath;
}
static string SourcePath([CallerFilePath] string path = "") => path;
static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
