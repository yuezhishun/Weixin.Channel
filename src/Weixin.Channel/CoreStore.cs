using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Weixin.Channel;

internal sealed record AccountRow(string AccountId, string OwnerId, string Token, string BaseUrl,
    bool Enabled, bool Debug, string[] Allowlist, DateTimeOffset? PausedUntil)
{
    internal string? Remark { get; init; }

    internal bool AllowsInboundPeer(string peer) => Allowlist.Length > 0
        ? Allowlist.Contains(peer, StringComparer.Ordinal) : OwnerId.Length > 0 && OwnerId == peer;

    internal string? InboundDenial(string peer, JsonObject raw)
    {
        if (!AllowsInboundPeer(peer)) return "unauthorized";
        if (!string.IsNullOrEmpty(ProtocolClient.Text(raw["group_id"]))) return "group-unsupported";
        return ProtocolClient.Number(raw["message_type"], 1) == 1 ? null : "non-user-message";
    }
}
internal sealed record InboxRow(string DeliveryId, string AccountId, string PeerId, string MessageId,
    string Raw, string LeaseId, DateTimeOffset LeaseUntil, DateTimeOffset CreatedAt);

internal sealed class CoreStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    internal CoreStore(string directory, TimeProvider time)
    {
        _time = time;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "core.sqlite"), Pooling = false }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY,owner TEXT NOT NULL,token TEXT NOT NULL,base_url TEXT NOT NULL,enabled INTEGER NOT NULL DEFAULT 1,debug INTEGER NOT NULL DEFAULT 0,allowlist TEXT NOT NULL DEFAULT '[]',paused_until INTEGER,updated INTEGER NOT NULL,cursor TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS account_remarks(account TEXT PRIMARY KEY,owner TEXT NOT NULL,remark TEXT NOT NULL,FOREIGN KEY(account) REFERENCES accounts(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS contexts(account TEXT NOT NULL,peer TEXT NOT NULL,token TEXT NOT NULL,PRIMARY KEY(account,peer),FOREIGN KEY(account) REFERENCES accounts(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS inbox(sequence INTEGER PRIMARY KEY AUTOINCREMENT,delivery TEXT NOT NULL UNIQUE,account TEXT NOT NULL,peer TEXT NOT NULL,message_id TEXT NOT NULL,raw TEXT NOT NULL,context_token TEXT,state TEXT NOT NULL DEFAULT 'pending',attempts INTEGER NOT NULL DEFAULT 0,available INTEGER NOT NULL,lease TEXT,lease_until INTEGER,created INTEGER NOT NULL,last_error TEXT,command_result TEXT,UNIQUE(account,peer,message_id),FOREIGN KEY(account) REFERENCES accounts(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS inbox_pending ON inbox(account,sequence,state);
            CREATE INDEX IF NOT EXISTS inbox_conversation ON inbox(account,peer,created,sequence) WHERE state<>'denied';
            CREATE INDEX IF NOT EXISTS inbox_owner_context ON inbox(account,peer,sequence) WHERE state<>'denied' AND context_token IS NOT NULL;
            CREATE TABLE IF NOT EXISTS sends(client_id TEXT PRIMARY KEY,account TEXT NOT NULL,peer TEXT NOT NULL,delivery TEXT,request TEXT NOT NULL,status TEXT NOT NULL,message_id TEXT,error TEXT,created INTEGER NOT NULL,attempt INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(account) REFERENCES accounts(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS sends_delivery ON sends(delivery,status);
            CREATE INDEX IF NOT EXISTS sends_conversation ON sends(account,peer,created);
            CREATE INDEX IF NOT EXISTS sends_conversation_message ON sends(account,peer,message_id) WHERE message_id IS NOT NULL;
            """);
        if (!OperatingSystem.IsWindows())
            foreach (var name in new[] { "core.sqlite", "core.sqlite-wal", "core.sqlite-shm" })
            {
                var file = Path.Combine(directory, name);
                if (File.Exists(file)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
    }
    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();
    internal static string NormalizeAccountId(string accountId)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(accountId.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
        return normalized.Length > 0 ? normalized : throw new ArgumentException("账号ID不能为空。", nameof(accountId));
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }
    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteScalar();
    }
    private static AccountRow ReadAccount(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), JsonSerializer.Deserialize<string[]>(r.GetString(6)) ?? [], r.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)))
    {
        Remark = r.IsDBNull(8) ? null : r.GetString(8)
    };
    internal IReadOnlyList<AccountRow> Accounts()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT a.id,a.owner,a.token,a.base_url,a.enabled,a.debug,a.allowlist,a.paused_until,r.remark
                FROM accounts a LEFT JOIN account_remarks r ON r.account=a.id AND r.owner=a.owner
                ORDER BY a.updated DESC,a.rowid DESC
                """);
            using var reader = command.ExecuteReader();
            var accounts = new List<AccountRow>();
            while (reader.Read()) accounts.Add(ReadAccount(reader));
            return accounts;
        }
    }
    internal AccountRow GetAccount(string id) => Accounts().SingleOrDefault(a => a.AccountId == NormalizeAccountId(id)) ?? throw new KeyNotFoundException("账号不存在。");
    internal void SaveAccount(string rawId, string owner, string token, string baseUrl)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var id = NormalizeAccountId(rawId);
            if (!string.IsNullOrEmpty(owner)) Execute("DELETE FROM accounts WHERE owner=$owner AND id<>$id", tx, ("$owner", owner), ("$id", id));
            Execute("DELETE FROM account_remarks WHERE account=$id AND owner<>$owner", tx, ("$id", id), ("$owner", owner));
            Execute("""
                INSERT INTO accounts(id,owner,token,base_url,updated) VALUES($id,$owner,$token,$url,$now)
                ON CONFLICT(id) DO UPDATE SET owner=excluded.owner,token=excluded.token,base_url=excluded.base_url,updated=excluded.updated,paused_until=NULL
                """, tx, ("$id", id), ("$owner", owner), ("$token", token), ("$url", baseUrl), ("$now", Now));
            tx.Commit();
        }
    }
    internal void Configure(string accountId, AccountConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Allowlist?.Any(value => value is null) == true) throw new ArgumentException("allowlist 不能包含 null。", nameof(configuration));
        var remark = configuration.Remark?.Trim();
        if (remark is not null)
        {
            if (remark.Length > 40 || remark.Any(char.IsControl))
                throw new ArgumentException("备注最多 40 个 UTF-16 单元，不能包含控制字符。", nameof(configuration));
            for (var i = 0; i < remark.Length;)
            {
                if (!Rune.TryGetRuneAt(remark, i, out var rune)) throw new ArgumentException("备注包含无效的 Unicode 字符。", nameof(configuration));
                i += rune.Utf16SequenceLength;
            }
        }
        lock (_gate)
        {
            var account = GetAccount(accountId);
            if (configuration.ExpectedOwnerId is not null && configuration.ExpectedOwnerId != account.OwnerId)
                throw new InvalidOperationException("微信账号已换绑，请刷新后重试。");
            using var tx = _connection.BeginTransaction();
            if (configuration.Enabled is not null || configuration.Debug is not null || configuration.Allowlist is not null)
                Execute("UPDATE accounts SET enabled=$enabled,debug=$debug,allowlist=$allow WHERE id=$id", tx,
                    ("$id", account.AccountId), ("$enabled", configuration.Enabled ?? account.Enabled), ("$debug", configuration.Debug ?? account.Debug),
                    ("$allow", JsonSerializer.Serialize(configuration.Allowlist?.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray() ?? account.Allowlist)));
            if (remark is "") Execute("DELETE FROM account_remarks WHERE account=$id", tx, ("$id", account.AccountId));
            else if (remark is not null) Execute("""
                INSERT INTO account_remarks(account,owner,remark) VALUES($id,$owner,$remark)
                ON CONFLICT(account) DO UPDATE SET owner=excluded.owner,remark=excluded.remark
                """, tx, ("$id", account.AccountId), ("$owner", account.OwnerId), ("$remark", remark));
            tx.Commit();
        }
    }
    internal void DeleteAccount(string accountId)
    {
        lock (_gate) Execute("DELETE FROM accounts WHERE id=$id", null, ("$id", NormalizeAccountId(accountId)));
    }
    internal void Pause(string accountId, TimeSpan duration)
    {
        lock (_gate) Execute("UPDATE accounts SET paused_until=$until WHERE id=$id", null, ("$until", Now + (long)duration.TotalMilliseconds), ("$id", accountId));
    }
    internal string Cursor(string accountId)
    {
        lock (_gate) return Convert.ToString(Scalar("SELECT cursor FROM accounts WHERE id=$id", null, ("$id", accountId)), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    internal IReadOnlyList<string> CommitBatch(string accountId, JsonArray messages, string? cursor)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var deliveries = new List<string>();
            foreach (var node in messages)
            {
                if (node is not JsonObject message) throw new InvalidDataException("入站消息不是对象。");
                var raw = message.ToJsonString();
                var peer = ProtocolClient.Text(message["from_user_id"]) ?? "";
                var messageId = ProtocolClient.Text(message["message_id"])?.Trim();
                if (string.IsNullOrEmpty(messageId))
                    messageId = (message["item_list"] as JsonArray ?? []).Select(item => ProtocolClient.Text(item?["msg_id"])?.Trim()).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                messageId ??= "local:" + Guid.NewGuid().ToString("N");
                var context = ProtocolClient.Text(message["context_token"]);
                var delivery = Guid.NewGuid().ToString("N");
                var created = ProtocolClient.Number(message["create_time_ms"], Now);
                if (created < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || created > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) created = Now;
                var added = Execute("INSERT OR IGNORE INTO inbox(delivery,account,peer,message_id,raw,context_token,available,created) VALUES($delivery,$account,$peer,$message,$raw,$context,$now,$created)", tx,
                    ("$delivery", delivery), ("$account", accountId), ("$peer", peer), ("$message", messageId), ("$raw", raw), ("$context", context), ("$now", Now), ("$created", created));
                if (added == 0) continue;
                deliveries.Add(delivery);
                if (!string.IsNullOrEmpty(context)) Execute("INSERT INTO contexts(account,peer,token) VALUES($account,$peer,$token) ON CONFLICT(account,peer) DO UPDATE SET token=excluded.token", tx,
                    ("$account", accountId), ("$peer", peer), ("$token", context));
            }
            if (cursor is not null) Execute("UPDATE accounts SET cursor=$cursor WHERE id=$id", tx, ("$cursor", cursor), ("$id", accountId));
            tx.Commit();
            return deliveries;
        }
    }
    internal InboxRow? Claim(string? accountId, TimeSpan duration, IReadOnlySet<string>? excluded = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("UPDATE inbox SET state='unknown' WHERE state NOT IN ('acked','denied') AND (lease_until IS NULL OR lease_until<=$now) AND delivery IN (SELECT delivery FROM sends WHERE status='unknown')", tx, ("$now", Now));
            using var command = Command("""
                SELECT i.delivery,i.account,i.peer,i.message_id,i.raw,i.created,i.state,i.available,i.lease_until FROM inbox i JOIN accounts a ON a.id=i.account
                WHERE i.sequence=(SELECT MIN(j.sequence) FROM inbox j WHERE j.account=i.account AND j.state NOT IN ('acked','denied'))
                AND a.enabled=1 AND (a.paused_until IS NULL OR a.paused_until<=$now)
                AND NOT EXISTS(SELECT 1 FROM sends s WHERE s.delivery=i.delivery AND s.status IN ('unknown','sending'))
                AND ($account IS NULL OR i.account=$account) ORDER BY i.sequence
                """, tx, ("$account", accountId is null ? null : NormalizeAccountId(accountId)), ("$now", Now));
            InboxRow? row = null;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (excluded?.Contains(reader.GetString(1)) == true || reader.GetString(6) == "unknown" || reader.GetInt64(7) > Now || (!reader.IsDBNull(8) && reader.GetInt64(8) > Now)) continue;
                    row = new InboxRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Guid.NewGuid().ToString("N"), _time.GetUtcNow() + duration, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)));
                    break;
                }
            }
            if (row is null) { tx.Commit(); return null; }
            Execute("UPDATE inbox SET state='leased',lease=$lease,lease_until=$until,attempts=attempts+1 WHERE delivery=$id", tx,
                ("$lease", row.LeaseId), ("$until", row.LeaseUntil.ToUnixTimeMilliseconds()), ("$id", row.DeliveryId));
            tx.Commit();
            return row;
        }
    }
    internal IReadOnlyList<InboxEntry> Inbox(string? accountId, bool includeCompleted)
    {
        lock (_gate)
        {
            using var command = Command("SELECT delivery,account,peer,message_id,state,attempts,available,lease_until,last_error FROM inbox WHERE ($account IS NULL OR account=$account) AND ($all=1 OR state NOT IN ('acked','denied')) ORDER BY sequence", null,
                ("$account", accountId is null ? null : NormalizeAccountId(accountId)), ("$all", includeCompleted));
            using var reader = command.ExecuteReader();
            var items = new List<InboxEntry>();
            while (reader.Read()) items.Add(new InboxEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)), reader.IsDBNull(8) ? null : reader.GetString(8)));
            return items;
        }
    }
    internal OwnerConversation ReadOwnerConversation(string accountId, string? before)
    {
        const int pageSize = 50;
        var beforeTime = long.MaxValue;
        var beforeDirection = 1;
        var beforeSequence = long.MaxValue;
        if (before is not null)
        {
            var parts = before.Split('.', 3);
            if (parts.Length != 3 || !long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out beforeTime) ||
                !int.TryParse(parts[1], out beforeDirection) || beforeDirection is not (0 or 1) ||
                !long.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out beforeSequence) || beforeSequence <= 0)
                throw new ArgumentException("历史记录游标无效。", nameof(before));
        }

        lock (_gate)
        {
            var account = GetAccount(accountId);
            if (account.OwnerId.Length == 0) return new OwnerConversation(account.OwnerId, [], null);
            var allowsOwner = account.AllowsInboundPeer(account.OwnerId);
            var hasSendContext = ReadOwnerContext(account) is not null;
            var items = new List<ConversationMessage>(pageSize);
            string? lastCursor = null;
            while (true)
            {
                // Bound each direction's candidates before merging them, and advance past filtered rows.
                using var command = Command("""
                    WITH incoming AS (
                        SELECT delivery AS id, 0 AS direction, created, sequence, raw AS packet, state AS status,
                            context_token, NULL AS message_id
                        FROM inbox WHERE $allowsOwner=1 AND account=$account AND peer=$owner AND state<>'denied'
                            AND (created, sequence) <= ($time, $incoming)
                        ORDER BY created DESC, sequence DESC LIMIT $limit
                    ), outgoing AS (
                        SELECT client_id AS id, 1 AS direction, created, rowid AS sequence, request AS packet, status,
                            NULL AS context_token, message_id
                        FROM sends WHERE account=$account AND peer=$owner AND (created, rowid) <= ($time, $outgoing)
                        ORDER BY created DESC, rowid DESC LIMIT $limit
                    )
                    SELECT * FROM incoming UNION ALL SELECT * FROM outgoing
                    ORDER BY created DESC, direction DESC, sequence DESC LIMIT $limit
                    """, null, ("$account", account.AccountId), ("$owner", account.OwnerId), ("$allowsOwner", allowsOwner), ("$time", beforeTime),
                    ("$incoming", beforeDirection == 0 ? beforeSequence - 1 : long.MaxValue),
                    ("$outgoing", beforeDirection == 1 ? beforeSequence - 1 : 0), ("$limit", pageSize + 1));
                using var reader = command.ExecuteReader();
                var scanned = 0;
                while (reader.Read())
                {
                    scanned++;
                    beforeDirection = reader.GetInt32(1);
                    beforeTime = reader.GetInt64(2);
                    beforeSequence = reader.GetInt64(3);
                    var stored = JsonNode.Parse(reader.GetString(4)) as JsonObject;
                    var status = reader.GetString(5);
                    var inbound = beforeDirection == 0;
                    if (stored is null || (inbound && account.InboundDenial(account.OwnerId, stored) is not null)) continue;

                    var packet = inbound ? stored : stored["msg"] as JsonObject;
                    var contents = packet?["item_list"] as JsonArray;
                    var parts = ConversationParts(contents);
                    var (quote, _) = ResolveConversationQuote(account, contents);
                    if (parts.Count == 0 && quote is null && !(!inbound && status == "failed" && stored.Count == 0)) continue;
                    if (items.Count == pageSize) return new OwnerConversation(account.OwnerId, items, lastCursor) { HasSendContext = hasSendContext };

                    var text = string.Join('\n', parts.Select(part => part.Text).Where(value => value.Length > 0));
                    var attachments = parts.Select(part => part.Attachment).OfType<ConversationAttachment>().ToArray();
                    items.Add(new ConversationMessage(reader.GetString(0), inbound ? "inbound" : "outbound",
                        DateTimeOffset.FromUnixTimeMilliseconds(beforeTime), text, attachments, status)
                    {
                        Parts = parts, Quote = quote,
                        CanReply = inbound && !reader.IsDBNull(6) && !string.IsNullOrWhiteSpace(reader.GetString(6)),
                        AwaitingDeliveryConfirmation = !inbound && status == "success" &&
                            (reader.IsDBNull(7) || string.IsNullOrWhiteSpace(reader.GetString(7)))
                    });
                    lastCursor = FormattableString.Invariant($"{beforeTime}.{beforeDirection}.{beforeSequence}");
                }
                if (scanned <= pageSize) break;
            }
            return new OwnerConversation(account.OwnerId, items, null) { HasSendContext = hasSendContext };
        }
    }

    private static List<ConversationPart> ConversationParts(JsonArray? items)
    {
        var parts = new List<ConversationPart>();
        if (items is null) return parts;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject item) continue;
            var text = ConversationItemText(item);
            var attachment = DescribeAttachment(item, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (text.Length > 0 || attachment is not null) parts.Add(new ConversationPart(text, attachment));
        }
        return parts;
    }

    private static string ConversationItemText(JsonObject item)
    {
        var text = Media.Number(item["type"]) switch
        {
            1 => (item["text_item"] as JsonObject)?["text"],
            3 => (item["voice_item"] as JsonObject)?["text"],
            _ => null
        };
        return text is JsonValue value && value.TryGetValue<string>(out var body) ? body : "";
    }

    private static ConversationAttachment? DescribeAttachment(JsonObject item, string id)
    {
        var kind = Media.Number(item["type"]) switch { 2 => "image", 3 => "voice", 4 => "file", 5 => "video", _ => null };
        if (kind is null) return null;
        var name = (Media.Reference(item)?["file_name"] as JsonValue)?.TryGetValue<string>(out var file) == true
            ? Path.GetFileName(file.Replace('\\', '/')) : null;
        return new ConversationAttachment(kind, name) { Id = Media.Downloadable(item) ? id : null };
    }

    private (ConversationQuote? Quote, JsonObject? MediaItem) ResolveConversationQuote(AccountRow account, JsonArray? items)
    {
        var reference = items?.OfType<JsonObject>().Select(item => item["ref_msg"]).OfType<JsonObject>().FirstOrDefault();
        if (reference is null) return (null, null);

        var inline = reference["message_item"] as JsonObject;
        var title = ProtocolClient.Text(reference["title"])?.Trim();
        var id = ProtocolClient.Text(reference["svr_id"])?.Trim();
        if (string.IsNullOrEmpty(id)) id = ProtocolClient.Text(inline?["msg_id"])?.Trim();
        var inlineText = inline is null ? "" : ConversationItemText(inline);
        var mediaItem = inline is not null && Media.Downloadable(inline) ? inline : null;
        if (inlineText.Length == 0 && mediaItem is not null) inlineText = Media.Label(Media.Number(mediaItem["type"]));
        if (!string.IsNullOrEmpty(id) && (inlineText.Length == 0 || mediaItem is null))
        {
            var original = ReadConversationPacket(account, "inbound", id, byServerId: true)
                ?? ReadConversationPacket(account, "outbound", id, byServerId: true);
            if (original?["item_list"] is JsonArray source)
            {
                if (inlineText.Length == 0)
                    inlineText = string.Join('\n', ConversationParts(source).Select(part => part.Text).Where(value => value.Length > 0));
                mediaItem ??= source.OfType<JsonObject>().FirstOrDefault(Media.Downloadable);
            }
        }
        var text = string.Join(" | ", new[] { title, inlineText }.Where(value => !string.IsNullOrEmpty(value)));
        if (reference["partial_text"] is JsonObject partial && Media.PartialQuote(text, partial) is { } selection) text = selection;
        var attachment = mediaItem is null ? null : DescribeAttachment(mediaItem, "quote");
        var missing = text.Length == 0 && attachment is null;
        return (new ConversationQuote(missing ? "[引用消息内容不可用]" : text, missing, attachment), mediaItem);
    }

    // Callers hold the store lock so message ownership and SQLite reads use the same account snapshot.
    private JsonObject? ReadConversationPacket(AccountRow account, string direction, string id, bool byServerId = false)
    {
        var inbound = direction == "inbound";
        var filter = byServerId ? "message_id" : inbound ? "delivery" : "client_id";
        var query = inbound
            ? $"SELECT raw FROM inbox WHERE account=$account AND peer=$owner AND state<>'denied' AND {filter}=$id"
            : $"SELECT request FROM sends WHERE account=$account AND peer=$owner AND {filter}=$id ORDER BY rowid DESC LIMIT 1";
        var raw = Scalar(query, null, ("$account", account.AccountId), ("$owner", account.OwnerId), ("$id", id)) as string;
        if (raw is null || JsonNode.Parse(raw) is not JsonObject stored) return null;
        return inbound
            ? account.InboundDenial(account.OwnerId, stored) is null ? stored : null
            : stored["msg"] as JsonObject;
    }

    internal (AccountRow Account, JsonObject Item, ConversationAttachment Attachment) GetOwnerAttachment(
        string accountId, string direction, string messageId, string attachmentId)
    {
        lock (_gate)
        {
            var account = GetAccount(accountId);
            if (account.OwnerId.Length == 0 || direction is not ("inbound" or "outbound") ||
                ReadConversationPacket(account, direction, messageId)?["item_list"] is not JsonArray items)
                throw new KeyNotFoundException("附件不存在。");
            var item = attachmentId == "quote"
                ? ResolveConversationQuote(account, items).MediaItem
                : int.TryParse(attachmentId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
                  index < items.Count && attachmentId == index.ToString(System.Globalization.CultureInfo.InvariantCulture) ? items[index] as JsonObject : null;
            if (item is null || DescribeAttachment(item, attachmentId) is not { Id: not null } attachment)
                throw new KeyNotFoundException("附件不存在。");
            return (account, item, attachment);
        }
    }

    private void CheckLease(string delivery, string lease, SqliteTransaction tx)
    {
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inbox WHERE delivery=$id AND lease=$lease AND lease_until>$now AND state='leased'", tx, ("$id", delivery), ("$lease", lease), ("$now", Now)), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("租约无效或已过期，必须使用当前领取的 leaseId。");
    }
    internal void Complete(string delivery, string lease, bool denied = false, string? reason = null)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inbox WHERE delivery=$id AND lease=$lease AND state=$state", tx,
                ("$id", delivery), ("$lease", lease), ("$state", denied ? "denied" : "acked")), System.Globalization.CultureInfo.InvariantCulture) == 1) return;
            CheckLease(delivery, lease, tx);
            if (!denied && HasUnknownSend(delivery, tx)) throw new InvalidOperationException("发送结果未知，消息必须核查后才能确认。");
            Execute("UPDATE inbox SET state=$state,lease_until=NULL,last_error=$reason WHERE delivery=$id", tx, ("$state", denied ? "denied" : "acked"), ("$reason", denied ? reason : null), ("$id", delivery));
            tx.Commit();
        }
    }
    internal DateTimeOffset Renew(string delivery, string lease, TimeSpan duration)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            CheckLease(delivery, lease, tx);
            var until = _time.GetUtcNow() + duration;
            Execute("UPDATE inbox SET lease_until=$until WHERE delivery=$id", tx, ("$until", until.ToUnixTimeMilliseconds()), ("$id", delivery));
            tx.Commit();
            return until;
        }
    }
    internal void Retry(string delivery, string? lease, bool allowUnknown, bool automatic = false, string? error = null, bool release = false)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            if (lease is not null) CheckLease(delivery, lease, tx);
            else if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inbox WHERE delivery=$id AND (state IN ('acked','denied') OR (lease_until IS NOT NULL AND lease_until>$now))", tx, ("$id", delivery), ("$now", Now)), System.Globalization.CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("消息已完成或正在租用，重试需要当前租约。");
            var attempts = Scalar("SELECT attempts FROM inbox WHERE delivery=$id", tx, ("$id", delivery));
            if (attempts is null) throw new KeyNotFoundException("消息不存在。");
            var unknown = HasUnknownSend(delivery, tx);
            if (unknown && !allowUnknown && !automatic && !release) throw new InvalidOperationException("存在结果未知的发送；显式允许重试可能重复发送。");
            if (allowUnknown) Execute("UPDATE sends SET status='retry-approved' WHERE delivery=$id AND status IN ('unknown','sending')", tx, ("$id", delivery));
            var delay = automatic && !release ? Math.Min(30_000, 2000 * Math.Pow(2, Math.Min(20, Convert.ToInt32(attempts, System.Globalization.CultureInfo.InvariantCulture) - 1))) : 0;
            Execute("UPDATE inbox SET state=$state,lease=NULL,lease_until=NULL,available=$available,last_error=$error WHERE delivery=$id", tx,
                ("$state", unknown && !allowUnknown ? "unknown" : "pending"), ("$available", Now + (long)delay), ("$error", error), ("$id", delivery));
            tx.Commit();
        }
    }
    private string? ReadOwnerContext(AccountRow account)
    {
        if (!account.AllowsInboundPeer(account.OwnerId)) return null;
        using var command = Command("""
            SELECT context_token, raw FROM inbox
            WHERE account=$account AND peer=$owner AND state<>'denied' AND context_token IS NOT NULL
            ORDER BY sequence DESC
            """, null, ("$account", account.AccountId), ("$owner", account.OwnerId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var token = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(token) && JsonNode.Parse(reader.GetString(1)) is JsonObject raw &&
                account.InboundDenial(account.OwnerId, raw) is null) return token;
        }
        return null;
    }

    internal (AccountRow Account, string PeerId, string? ContextToken) ResolveSend(SendRequest request, bool ownerText = false)
    {
        ArgumentNullException.ThrowIfNull(request.PeerId);
        lock (_gate)
        {
            if (request.DeliveryId is not null)
            {
                using var command = Command("SELECT account,peer,context_token,raw,state FROM inbox WHERE delivery=$id", null, ("$id", request.DeliveryId));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new KeyNotFoundException("回复对应的 DeliveryId 不存在。");
                var id = reader.GetString(0);
                var peer = reader.GetString(1);
                if ((request.AccountId is not null && NormalizeAccountId(request.AccountId) != id) || (request.PeerId.Length > 0 && request.PeerId != peer))
                    throw new ArgumentException("DeliveryId 与账号或收件人不匹配。");
                var deliveryAccount = GetAccount(id);
                if (ownerText && (deliveryAccount.OwnerId != peer || reader.GetString(4) == "denied" ||
                    JsonNode.Parse(reader.GetString(3)) is not JsonObject raw || deliveryAccount.InboundDenial(peer, raw) is not null))
                    throw new KeyNotFoundException("回复对应的消息不存在。");
                var token = reader.IsDBNull(2) ? null : reader.GetString(2);
                return (deliveryAccount, peer, ownerText && string.IsNullOrWhiteSpace(token) ? null : token);
            }
            if (string.IsNullOrWhiteSpace(request.PeerId)) throw new ArgumentException("必须指定 PeerId 或 DeliveryId。");
            AccountRow account;
            if (request.AccountId is not null) account = GetAccount(request.AccountId);
            else
            {
                var accounts = Accounts();
                if (accounts.Count == 1) account = accounts[0];
                else
                {
                    var candidates = accounts.Where(a => Scalar("SELECT token FROM contexts WHERE account=$account AND peer=$peer", null, ("$account", a.AccountId), ("$peer", request.PeerId)) is not null).ToArray();
                    account = candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException("无法唯一确定账号，请指定 AccountId。");
                }
            }
            if (ownerText) return (account, request.PeerId, ReadOwnerContext(account));
            var context = Convert.ToString(Scalar("SELECT token FROM contexts WHERE account=$account AND peer=$peer", null, ("$account", account.AccountId), ("$peer", request.PeerId)), System.Globalization.CultureInfo.InvariantCulture);
            return (account, request.PeerId, string.IsNullOrEmpty(context) ? null : context);
        }
    }
    internal bool RecordSend(string clientId, string accountId, string peerId, string? deliveryId, string requestJson, string status, string? messageId, string? error,
        string? requiredOwner = null)
    {
        lock (_gate) return Execute("""
            INSERT INTO sends(client_id,account,peer,delivery,request,status,message_id,error,created,attempt)
            SELECT $client,$account,$peer,$delivery,$request,$status,$message,$error,$now,COALESCE((SELECT attempts FROM inbox WHERE delivery=$delivery),0)
            WHERE $owner IS NULL OR EXISTS(SELECT 1 FROM accounts WHERE id=$account AND owner=$owner)
            ON CONFLICT(client_id) DO UPDATE SET status=excluded.status,message_id=excluded.message_id,error=excluded.error
            """, null, ("$client", clientId), ("$account", accountId), ("$peer", peerId), ("$delivery", deliveryId), ("$request", requestJson),
            ("$status", status), ("$message", messageId), ("$error", error), ("$now", Now), ("$owner", requiredOwner)) == 1;
    }
    internal bool HasUnknownSend(string? deliveryId, SqliteTransaction? transaction = null)
    {
        if (deliveryId is null) return false;
        lock (_gate) return Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sends WHERE delivery=$id AND status IN ('unknown','sending')", transaction, ("$id", deliveryId)), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
    internal bool HasFailedSendInCurrentAttempt(string deliveryId)
    {
        lock (_gate) return Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sends WHERE delivery=$id AND status IN ('failed','unknown','sending') AND attempt=(SELECT attempts FROM inbox WHERE delivery=$id)", null, ("$id", deliveryId)), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }
    internal void ApproveUnknownSends(string deliveryId)
    {
        lock (_gate) Execute("UPDATE sends SET status='retry-approved' WHERE delivery=$id AND status IN ('unknown','sending')", null, ("$id", deliveryId));
    }
    internal IReadOnlyList<SendSegment> SendSegments(string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        lock (_gate)
        {
            if (Scalar("SELECT delivery FROM inbox WHERE delivery=$id", null, ("$id", deliveryId)) is null) throw new KeyNotFoundException("消息不存在。");
            using var command = Command("SELECT client_id,message_id,status,error FROM sends WHERE delivery=$id ORDER BY created,rowid", null, ("$id", deliveryId));
            using var reader = command.ExecuteReader();
            var segments = new List<SendSegment>();
            while (reader.Read()) segments.Add(new SendSegment(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            return segments;
        }
    }
    internal string ToggleDebugOnce(string deliveryId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var previous = Scalar("SELECT command_result FROM inbox WHERE delivery=$id", tx, ("$id", deliveryId));
            if (previous is string result) return result;
            Execute("UPDATE accounts SET debug=1-debug WHERE id=(SELECT account FROM inbox WHERE delivery=$id)", tx, ("$id", deliveryId));
            var enabled = Convert.ToBoolean(Scalar("SELECT debug FROM accounts WHERE id=(SELECT account FROM inbox WHERE delivery=$id)", tx, ("$id", deliveryId)), System.Globalization.CultureInfo.InvariantCulture);
            result = enabled ? "Debug 模式已开启" : "Debug 模式已关闭";
            Execute("UPDATE inbox SET command_result=$result WHERE delivery=$id", tx, ("$result", result), ("$id", deliveryId));
            tx.Commit();
            return result;
        }
    }
    internal void RecoverInterruptedSends()
    {
        lock (_gate) Execute("UPDATE sends SET status='unknown',error='进程在发送结果落盘前结束，必须核查。' WHERE status='sending'; UPDATE inbox SET state='unknown',lease=NULL,lease_until=NULL WHERE state NOT IN ('acked','denied') AND delivery IN (SELECT delivery FROM sends WHERE status='unknown');");
    }
    public void Dispose() => _connection.Dispose();
}
