using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Weixin.Channel;

internal static class OwnerMedia
{
    private const int FileLimit = 100 * 1024 * 1024;
    private const int FormOverhead = 1024 * 1024;
    public const int RequestLimit = FileLimit + FormOverhead;
    internal const string DownloadMediaType = "application/octet-stream";
    internal static readonly string[] InlineMediaTypes =
    [
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp",
        "audio/wav", "audio/mpeg", "audio/ogg", "video/mp4", "video/webm", "video/quicktime"
    ];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static FileStreamHttpResult Download(WeixinAttachment attachment)
    {
        var mime = attachment.MimeType;
        var inline = attachment.Kind is "image" or "voice" or "video" && InlineMediaTypes.Contains(mime);
        var name = inline ? null : SafeName(attachment.Name ?? "附件" + Path.GetExtension(attachment.Path));
        try
        {
            var stream = new FileStream(attachment.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return TypedResults.File(stream, inline ? mime : DownloadMediaType, name);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new KeyNotFoundException("附件已不存在。");
        }
    }

    public static async Task<Ok<SendResult>> SendAsync(WeixinChannel channel, string accountId, HttpRequest request,
        string uploads, ILogger log, CancellationToken ct)
    {
        if (request.ContentLength is > RequestLimit) throw TooLarge();
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) throw InvalidUpload();
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).ToString();
        if (boundary.Length is 0 or > 128) throw InvalidUpload();

        var reader = new MultipartReader(boundary, request.Body);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var staging = Path.Combine(uploads, Guid.NewGuid().ToString("N"));
        string? path = null;
        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var part)) throw InvalidUpload();
                var field = HeaderUtilities.RemoveQuotes(part.Name).ToString();
                if (part.IsFileDisposition() && field == "file" && path is null)
                {
                    var fileName = HeaderUtilities.RemoveQuotes(part.FileNameStar.HasValue ? part.FileNameStar : part.FileName);
                    path = Path.Combine(staging, SafeName(fileName.ToString()));
                    Directory.CreateDirectory(staging);
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                    if (await CopyBoundedAsync(section.Body, file, FileLimit, ct) == 0) throw InvalidUpload();
                    continue;
                }
                if (!part.IsFormDisposition() || field is not ("ownerId" or "text" or "replyDeliveryId") || fields.ContainsKey(field))
                    throw InvalidUpload();
                using var value = new MemoryStream();
                var length = await CopyBoundedAsync(section.Body, value, FormOverhead, ct);
                fields.Add(field, Utf8.GetString(value.GetBuffer(), 0, (int)length));
            }

            if (path is null || !fields.TryGetValue("ownerId", out var ownerId)) throw InvalidUpload();
            return TypedResults.Ok(await channel.SendOwnerMessageAsync(accountId, ownerId, fields.GetValueOrDefault("text"),
                path, fields.GetValueOrDefault("replyDeliveryId"), ct));
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        {
            throw InvalidUpload();
        }
        finally
        {
            if (path is not null) RemoveStaging(staging, log);
        }
    }

    public static void CleanUploads(string uploads, ILogger log)
    {
        try
        {
            if (!Directory.Exists(uploads)) return;
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(uploads, "*", options))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) RemoveStaging(directory, log);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("媒体上传暂存清理未完成：{ErrorType}", error.GetType().Name);
        }
    }

    private static void RemoveStaging(string staging, ILogger log)
    {
        try
        {
            if (!Directory.Exists(staging)) return;
            foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
            Directory.Delete(staging);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("媒体上传暂存清理未完成：{ErrorType}", error.GetType().Name);
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream target, int limit, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = 0L;
        while (await source.ReadAsync(buffer, ct) is var count && count > 0)
        {
            total += count;
            if (total > limit) throw TooLarge();
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return total;
    }

    private static string SafeName(string name)
    {
        var leaf = Path.GetFileName(name.Replace('\\', '/'));
        var safe = string.Concat(leaf.Select(ch => char.IsControl(ch) || "<>:\"/\\|?*".Contains(ch) ? '_' : ch)).Trim().TrimEnd('.');
        if (safe.Length == 0) return "附件.bin";
        if (Utf8.GetByteCount(safe) > 200)
        {
            var extension = Path.GetExtension(safe);
            return extension.Length is > 0 and <= 12 ? "附件" + extension : "附件.bin";
        }
        var stem = safe.Split('.')[0];
        return stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsNumber(stem[3])) ? "_" + safe : safe;
    }

    private static BadHttpRequestException InvalidUpload() => new("表单需要 ownerId 与唯一的非空 file。");
    private static BadHttpRequestException TooLarge() => new("单个附件不能超过 100 MiB。", StatusCodes.Status413PayloadTooLarge);
}

internal sealed record OwnerMediaRequest(string OwnerId, IFormFile File, string? Text = null, string? ReplyDeliveryId = null);
