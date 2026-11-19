using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Weixin.Channel;

internal sealed class QuoteStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _mediaRoot;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly WeixinOptions _options;
    private readonly ITimer _timer;
    private SqliteConnection? _connection;
    private int _writes;
    private bool _disposed;

    internal QuoteStore(WeixinOptions options, Action<string> log)
    {
        _options = options;
        var directory = options.StateDirectory;
        var time = options.TimeProvider;
        _time = time;
        _log = log;
        _mediaRoot = Path.GetFullPath(Path.Combine(directory, "media", "quotes"));
        try
        {
            Directory.CreateDirectory(_mediaRoot);
            if (!options.EnableQuoteCache)
            {
                _timer = time.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "quotes.sqlite"), Pooling = false }.ToString());
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS quotes (
                    account TEXT NOT NULL,peer TEXT NOT NULL,id TEXT NOT NULL,body TEXT NOT NULL,
                    path TEXT,mime TEXT,name TEXT,kind TEXT,size INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL,
                    PRIMARY KEY(account,peer,id));
                CREATE INDEX IF NOT EXISTS quotes_age ON quotes(account,created);
                CREATE INDEX IF NOT EXISTS quotes_path ON quotes(path);
                """;
            command.ExecuteNonQuery();
            Cleanup();
        }
        catch (Exception ex) { Disable(ex); }
        _timer = time.CreateTimer(_ => Cleanup(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    internal static string AccountDirectory(string account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();

    private void Disable(Exception error)
    {
        _log("引用缓存不可用，普通消息仍可处理：" + error.GetType().Name);
        _connection?.Dispose();
        _connection = null;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] values)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    internal void Put(string account, string peer, string id, string body, WeixinAttachment? attachment, DateTimeOffset created)
    {
        lock (_gate)
        {
            if (_disposed || _connection is null || string.IsNullOrEmpty(id)) return;
            try
            {
                using var previous = Command("SELECT path FROM quotes WHERE account=$account AND peer=$peer AND id=$id", ("$account", account), ("$peer", peer), ("$id", id));
                var previousPath = previous.ExecuteScalar() as string;
                string? managed = null;
                long size = 0;
                if (attachment is not null && File.Exists(attachment.Path))
                {
                    var file = new FileInfo(attachment.Path);
                    if (file.Length <= _options.QuoteMediaFileBytesLimit)
                    {
                        var accountDirectory = Path.Combine(_mediaRoot, AccountDirectory(account));
                        Directory.CreateDirectory(accountDirectory);
                        using var source = File.OpenRead(attachment.Path);
                        var hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
                        managed = Path.Combine(accountDirectory, hash + Media.Extension(attachment.MimeType));
                        if (!File.Exists(managed)) File.Copy(attachment.Path, managed);
                        size = file.Length;
                    }
                }
                using var command = Command("""
                    INSERT INTO quotes(account,peer,id,body,path,mime,name,kind,size,created)
                    VALUES($account,$peer,$id,$body,$path,$mime,$name,$kind,$size,$created)
                    ON CONFLICT(account,peer,id) DO UPDATE SET body=excluded.body,
                    path=CASE WHEN $hasMedia=1 THEN excluded.path ELSE quotes.path END,mime=COALESCE(excluded.mime,quotes.mime),
                    name=COALESCE(excluded.name,quotes.name),kind=COALESCE(excluded.kind,quotes.kind),
                    size=CASE WHEN $hasMedia=1 THEN excluded.size ELSE quotes.size END,created=excluded.created;
                    """, ("$account", account), ("$peer", peer), ("$id", id), ("$body", body), ("$path", managed),
                    ("$mime", attachment?.MimeType), ("$name", attachment?.Name), ("$kind", attachment?.Kind), ("$size", size), ("$created", created.ToUnixTimeMilliseconds()), ("$hasMedia", attachment is not null));
                command.ExecuteNonQuery();
                if (attachment is not null && previousPath is not null && previousPath != managed)
                {
                    using var references = Command("SELECT COUNT(*) FROM quotes WHERE path=$path", ("$path", previousPath));
                    if (Convert.ToInt64(references.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) DeleteOwnedFile(previousPath);
                }
                _writes++;
                if (_writes >= 100) { _writes = 0; Cleanup(); }
                else
                {
                    EnforceMediaBudget(account, _time.GetUtcNow());
                    using var trim = Command("DELETE FROM quotes WHERE rowid IN (SELECT rowid FROM quotes WHERE account=$account ORDER BY created DESC,rowid DESC LIMIT -1 OFFSET $limit)", ("$account", account), ("$limit", _options.QuoteTextLimit));
                    if (trim.ExecuteNonQuery() > 0) PruneOrphans();
                }
            }
            catch (Exception ex) { _log("引用写入失败：" + ex.GetType().Name); }
        }
    }

    internal WeixinQuote? Find(string account, string peer, string id, string? copyMediaTo = null)
    {
        lock (_gate)
        {
            if (_disposed || _connection is null) return null;
            try
            {
                Cleanup();
                using var command = Command("SELECT body,path,mime,name,kind FROM quotes WHERE account=$account AND peer=$peer AND id=$id", ("$account", account), ("$peer", peer), ("$id", id));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                var body = reader.GetString(0);
                var path = reader.IsDBNull(1) ? null : reader.GetString(1);
                var mime = reader.IsDBNull(2) ? null : reader.GetString(2);
                var name = reader.IsDBNull(3) ? null : reader.GetString(3);
                var kind = reader.IsDBNull(4) ? "file" : reader.GetString(4);
                if (mime is null && name is null) return new WeixinQuote(id, body, false);
                if (path is null || !File.Exists(path))
                {
                    var label = kind switch { "image" => "图片", "voice" => "语音", "video" => "视频", _ => "附件" };
                    return new WeixinQuote(id, name is null ? $"[引用的{label}已过期]" : $"[引用的{label}已过期: {name}]", true);
                }
                if (copyMediaTo is not null)
                {
                    Directory.CreateDirectory(copyMediaTo);
                    var copy = Path.Combine(copyMediaTo, Guid.NewGuid().ToString("N") + Media.Extension(mime));
                    File.Copy(path, copy);
                    path = copy;
                }
                return new WeixinQuote(id, body, false, new WeixinAttachment(kind, path, mime ?? "application/octet-stream", name, true));
            }
            catch (Exception ex) { _log("引用读取失败：" + ex.GetType().Name); return null; }
        }
    }

    internal void DeleteAccount(string account)
    {
        lock (_gate)
        {
            if (_disposed || _connection is null) return;
            try
            {
                using var command = Command("DELETE FROM quotes WHERE account=$account", ("$account", account));
                command.ExecuteNonQuery();
                PruneOrphans();
            }
            catch (Exception ex) { _log("引用账号清理失败：" + ex.GetType().Name); }
        }
    }

    internal void Cleanup()
    {
        lock (_gate)
        {
            if (_disposed || _connection is null) return;
            try
            {
                var now = _time.GetUtcNow();
                using (var expire = Command("DELETE FROM quotes WHERE created < $cutoff", ("$cutoff", (now - _options.QuoteTextRetention).ToUnixTimeMilliseconds()))) expire.ExecuteNonQuery();
                using (var trim = Command("DELETE FROM quotes WHERE rowid IN (SELECT rowid FROM (SELECT rowid,ROW_NUMBER() OVER(PARTITION BY account ORDER BY created DESC,rowid DESC) AS n FROM quotes) WHERE n>$limit)", ("$limit", _options.QuoteTextLimit))) trim.ExecuteNonQuery();
                var accounts = new List<string>();
                using (var list = Command("SELECT DISTINCT account FROM quotes"))
                using (var reader = list.ExecuteReader()) while (reader.Read()) accounts.Add(reader.GetString(0));
                foreach (var account in accounts) EnforceMediaBudget(account, now);
                PruneOrphans();
            }
            catch (Exception ex) { _log("引用缓存清理失败：" + ex.GetType().Name); }
        }
    }

    private void EnforceMediaBudget(string account, DateTimeOffset now)
    {
        var media = new List<(string Path, long Size, long Created)>();
        using (var command = Command("SELECT path,MAX(size),MAX(created) FROM quotes WHERE account=$account AND path IS NOT NULL GROUP BY path ORDER BY MAX(created)", ("$account", account)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) media.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        var bytes = media.Sum(x => x.Size);
        foreach (var item in media)
        {
            if (item.Created >= (now - _options.QuoteMediaRetention).ToUnixTimeMilliseconds() && bytes <= _options.QuoteMediaBytesLimit) continue;
            using var detach = Command("UPDATE quotes SET path=NULL,size=0 WHERE path=$path", ("$path", item.Path));
            detach.ExecuteNonQuery();
            DeleteOwnedFile(item.Path);
            bytes -= item.Size;
        }
    }

    private void PruneOrphans()
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using (var command = Command("SELECT DISTINCT path FROM quotes WHERE path IS NOT NULL"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) paths.Add(reader.GetString(0));
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(_mediaRoot, "*", options)) if (!paths.Contains(file)) DeleteOwnedFile(file);
    }

    private void DeleteOwnedFile(string path)
    {
        var relative = Path.GetRelativePath(_mediaRoot, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
        try { File.Delete(path); }
        catch (IOException) { _log("引用媒体清理暂时失败，将在下次清理重试。"); }
        catch (UnauthorizedAccessException) { _log("引用媒体无删除权限，将在下次清理重试。"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _connection?.Dispose();
            _connection = null;
        }
    }
}
