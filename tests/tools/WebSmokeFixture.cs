#:project ../../src/Weixin.Channel/Weixin.Channel.csproj
#:property PublishAot=false
#:property RestoreLockedMode=true

using System.Net;
using System.Runtime.CompilerServices;
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
VerifyContract();
Seed();
var before = Snapshot();
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
await Post("/api/inbox/claim?accountId=" + account, null, HttpStatusCode.NoContent);
var renewed = await Post("/api/inbox/" + delivery + "/renew", new JsonObject { ["leaseId"] = lease }, HttpStatusCode.OK);
Check(renewed?["leaseUntil"] is not null, "renew 未返回 leaseUntil。");
await Post("/api/inbox/" + delivery + "/ack", new JsonObject { ["leaseId"] = "stale-fixture-lease" }, HttpStatusCode.Conflict);
await Post("/api/inbox/" + delivery + "/ack", new JsonObject { ["leaseId"] = lease }, HttpStatusCode.NoContent);
var receiptsText = await client.GetStringAsync("/api/inbox/" + receiptDelivery + "/receipts", timeout.Token);
var receipts = JsonNode.Parse(receiptsText)!.AsArray();
Check(receipts.Count == 3 && receipts.Select(item => item!["status"]!.GetValue<string>()).SequenceEqual(new[] { "success", "failed", "unknown" }),
    "收据查询未恢复所有持久化分段结果。");
Check(!receiptsText.Contains(secret, StringComparison.Ordinal) && receipts.All(item => item!.AsObject().Select(pair => pair.Key).All(name => name is "clientId" or "messageId" or "status" or "error")),
    "发送收据公开了协议请求或凭据字段。");
File.WriteAllText(Path.Combine(evidenceDirectory, "receipts.json"), receiptsText);
Console.WriteLine("HTTP 契约、非空 SSE 不消费、立即 claim 原 DeliveryId、租约校验与脱敏持久收据全部通过；合成账号未启动轮询。");

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
        ["item_list"] = new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = "HTTP SSE 持久性合成消息" } })
    };
    using var transaction = database.BeginTransaction();
    using var command = database.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO accounts(id,owner,token,base_url,updated) VALUES($account,$peer,$secret,'http://127.0.0.1:1/',$now);
        INSERT INTO inbox(delivery,account,peer,message_id,raw,context_token,available,created)
          VALUES($delivery,$account,$peer,$message,$raw,$secret,$now,$now);
        INSERT INTO inbox(delivery,account,peer,message_id,raw,context_token,state,available,created)
          VALUES($receipt,$account,$peer,'receipt-message',$raw,$secret,'acked',$now,$now);
        INSERT INTO sends(client_id,account,peer,delivery,request,status,message_id,error,created)
          VALUES('receipt-1',$account,$peer,$receipt,$raw,'success','server-1',NULL,$now),
                ('receipt-2',$account,$peer,$receipt,$raw,'failed',NULL,'http',$now+1),
                ('receipt-3',$account,$peer,$receipt,$raw,'unknown',NULL,'timeout',$now+2);
        """;
    command.Parameters.AddWithValue("$account", account);
    command.Parameters.AddWithValue("$peer", peer);
    command.Parameters.AddWithValue("$secret", secret);
    command.Parameters.AddWithValue("$now", now);
    command.Parameters.AddWithValue("$delivery", delivery);
    command.Parameters.AddWithValue("$receipt", receiptDelivery);
    command.Parameters.AddWithValue("$message", messageId);
    command.Parameters.AddWithValue("$raw", raw.ToJsonString());
    command.ExecuteNonQuery();
    transaction.Commit();
}

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
    foreach (var path in new[] { "/api/messages", "/api/messages/reply" })
    {
        var schema = ResponseSchema(path, "post", "200");
        RequireProperties(schema, "accountId", "peerId", "status", "segments");
        var segments = Resolve(schema["properties"]!["segments"]!);
        Check(segments["type"]?.GetValue<string>() == "array", path + " segments 缺少数组schema。");
        RequireProperties(Resolve(segments["items"]!), "clientId", "messageId", "status", "error");
    }
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
