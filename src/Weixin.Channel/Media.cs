using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SilkCodec.NET;

namespace Weixin.Channel;

internal static class Media
{
    internal const int MaximumBytes = 100 * 1024 * 1024;
    internal static string? Text(JsonNode? node) => node?.ToString();
    internal static int Number(JsonNode? node) => int.TryParse(Text(node), out var value) ? value : 0;
    internal static string Label(int type) => type switch { 2 => "[图片]", 3 => "[语音]", 4 => "[文件]", 5 => "[视频]", _ => "" };
    internal static string Body(IEnumerable<JsonNode?> items)
    {
        foreach (var item in items)
        {
            var type = Number(item?["type"]);
            if (type == 1 && item?["text_item"]?["text"] is { } text) return text.ToString();
            if (type == 3 && Text(item?["voice_item"]?["text"]) is { Length: > 0 } transcript) return transcript;
            if (type is 2 or 3 or 4 or 5) return Label(type);
        }
        return "";
    }

    internal static JsonObject? Reference(JsonNode? item) => Number(item?["type"]) switch
    {
        2 => item?["image_item"] as JsonObject,
        3 => item?["voice_item"] as JsonObject,
        4 => item?["file_item"] as JsonObject,
        5 => item?["video_item"] as JsonObject,
        _ => null
    };

    internal static bool Downloadable(JsonNode? item)
    {
        var reference = Reference(item)?["media"];
        return !string.IsNullOrEmpty(Text(reference?["full_url"])) || !string.IsNullOrEmpty(Text(reference?["encrypt_query_param"]));
    }

    internal static JsonNode? FirstMedia(JsonArray items)
    {
        foreach (var type in new[] { 2, 5, 4, 3 })
            foreach (var item in items)
                if (Number(item?["type"]) == type && Downloadable(item) &&
                    (type != 3 || string.IsNullOrEmpty(Text(item?["voice_item"]?["text"])))) return item;
        return null;
    }

    internal static byte[] AesKey(string value)
    {
        var key = Convert.FromBase64String(value);
        if (key.Length == 16) return key;
        if (key.Length == 32 && key.All(c => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F'))
            return Convert.FromHexString(Encoding.ASCII.GetString(key));
        throw new InvalidDataException("CDN AES 密钥长度或编码无效。");
    }

    internal static byte[] Encrypt(byte[] bytes, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(bytes, PaddingMode.PKCS7);
    }

    internal static byte[] Decrypt(byte[] bytes, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(bytes, PaddingMode.PKCS7);
    }

    internal static byte[] SilkToWav(byte[] silk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = silk.Length > 0 && silk[0] == 2 ? 1 : 0;
        if (silk.Length - offset < 9 || !silk.AsSpan(offset, 9).SequenceEqual("#!SILK_V3"u8)) throw new InvalidDataException("SILK 文件头无效。");
        offset += 9;
        var packets = 0;
        while (offset < silk.Length)
        {
            if (silk.Length - offset < 2) throw new InvalidDataException("SILK 包长度截断。");
            var length = BinaryPrimitives.ReadInt16LittleEndian(silk.AsSpan(offset));
            offset += 2;
            if (length == -1 && offset == silk.Length) break;
            if (length <= 0 || length > silk.Length - offset) throw new InvalidDataException("SILK 包长度无效。");
            offset += length;
            // 一个 SILK packet 最多五个 20ms 帧；先限制潜在 PCM 膨胀，再交给 decoder。
            if ((long)++packets * 5 * 480 * 2 > MaximumBytes) throw new InvalidDataException("SILK 解码输出可能超过 100 MiB 限制。");
        }
        var pcm = new SilkDecoder(new SilkDecoderOptions { SampleRate = 24000 }).Decode(silk);
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > MaximumBytes) throw new InvalidDataException("SILK 解码结果为空、过大或格式无效。");
        var wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 24000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 48000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    internal static string Mime(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".txt" => "text/plain", ".csv" => "text/csv", ".zip" => "application/zip", ".tar" => "application/x-tar", ".gz" => "application/gzip",
        ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".wav" => "audio/wav", ".silk" => "audio/silk",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        _ => "application/octet-stream"
    };

    internal static string Extension(string? mime) => mime?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", "image/bmp" => ".bmp",
        "video/mp4" => ".mp4", "video/quicktime" => ".mov", "video/webm" => ".webm", "video/x-matroska" => ".mkv", "video/x-msvideo" => ".avi",
        "audio/mpeg" => ".mp3", "audio/ogg" => ".ogg", "audio/wav" => ".wav", "audio/silk" => ".silk", "application/pdf" => ".pdf", "application/zip" => ".zip",
        "application/x-tar" => ".tar", "application/gzip" => ".gz", "text/plain" => ".txt", "text/csv" => ".csv", _ => ".bin"
    };

    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumBytes + 16) throw new InvalidDataException("媒体超过 100 MiB 限制。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaximumBytes + 16) throw new InvalidDataException("媒体超过 100 MiB 限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static async Task<WeixinAttachment?> DownloadAsync(HttpClient http, Uri cdnBase, string directory,
        JsonNode item, bool referenced, Action<string> log, CancellationToken ct, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60), time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = linked.Token;
        var type = Number(item["type"]);
        var reference = Reference(item);
        var media = reference?["media"];
        if (!Downloadable(item) || media is null) return null;
        var url = Text(media["full_url"]);
        if (string.IsNullOrWhiteSpace(url)) url = cdnBase.ToString().TrimEnd('/') + "/download?encrypted_query_param=" + Uri.EscapeDataString(Text(media["encrypt_query_param"]) ?? "");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await ReadBoundedAsync(response, ct);
        byte[]? key = null;
        if (type == 2 && Text(reference?["aeskey"]) is { Length: > 0 } hex) key = Convert.FromHexString(hex);
        else if (Text(media["aes_key"]) is { Length: > 0 } encoded) key = AesKey(encoded);
        if (key is not null) bytes = Decrypt(bytes, key);
        else if (type != 2) throw new InvalidDataException("加密媒体缺少 AES 密钥。");
        var kind = type switch { 2 => "image", 3 => "voice", 4 => "file", 5 => "video", _ => "file" };
        var name = type == 4 ? Text(reference?["file_name"]) : null;
        var mime = type switch { 2 => DetectImage(bytes), 3 => "audio/silk", 5 => "video/mp4", _ => Mime(name ?? "file.bin") };
        if (type == 3)
        {
            try { bytes = SilkToWav(bytes, ct); mime = "audio/wav"; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { log("SILK 解码失败，保留原始语音：" + exception.GetType().Name); }
        }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + Extension(mime));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new WeixinAttachment(kind, path, mime, name, referenced);
    }

    internal static string DetectImage(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 })) return "image/png";
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.AsSpan().StartsWith("GIF"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.AsSpan().StartsWith("BM"u8)) return "image/bmp";
        return "image/*";
    }

    internal static async Task<string> ResolveFileAsync(HttpClient http, string source, string directory, CancellationToken ct, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60), time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = linked.Token;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            var path = uri?.IsFile == true ? uri.LocalPath : Path.GetFullPath(source);
            if (!File.Exists(path)) throw new FileNotFoundException("待发送媒体文件不存在。", path);
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("媒体超过 100 MiB 限制。");
            return path;
        }
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("媒体仅支持本地路径、file:// 和 HTTP(S)。", nameof(source));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await ReadBoundedAsync(response, ct);
        var extension = Extension(response.Content.Headers.ContentType?.MediaType);
        if (extension == ".bin" && Mime(uri.AbsolutePath) != "application/octet-stream") extension = Path.GetExtension(uri.AbsolutePath);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
        await File.WriteAllBytesAsync(target, bytes, ct);
        return target;
    }

    internal static async Task<string> UploadAsync(HttpClient http, Uri cdnBase, JsonObject response, string fileKey,
        byte[] plaintext, byte[] key, CancellationToken ct, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60), time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = linked.Token;
        var url = Text(response["upload_full_url"])?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            var parameter = Text(response["upload_param"]);
            if (string.IsNullOrEmpty(parameter)) throw new InvalidDataException("CDN 未返回上传地址。");
            url = cdnBase.ToString().TrimEnd('/') + "/upload?encrypted_query_param=" + Uri.EscapeDataString(parameter) + "&filekey=" + Uri.EscapeDataString(fileKey);
        }
        var ciphertext = Encrypt(plaintext, key);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var body = new ByteArrayContent(ciphertext);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var upload = await http.PostAsync(url, body, ct);
                if ((int)upload.StatusCode is >= 400 and < 500) throw new HttpRequestException("CDN 拒绝上传。", null, upload.StatusCode);
                if (upload.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("CDN 上传未成功。", null, upload.StatusCode);
                if (upload.Headers.TryGetValues("x-encrypted-param", out var values) && values.FirstOrDefault() is { Length: > 0 } parameter) return parameter;
                throw new InvalidDataException("CDN 上传响应缺少 x-encrypted-param。");
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested &&
                ex is HttpRequestException or InvalidDataException or TaskCanceledException &&
                ex is not HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError }) { }
        }
    }

    internal static IReadOnlyList<string> Chunk(string text, int size)
    {
        if (size < 2) throw new ArgumentOutOfRangeException(nameof(size), "文本分块必须至少容纳一个 surrogate pair。");
        var chunks = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(size, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
            chunks.Add(text.Substring(offset, count));
            offset += count;
        }
        return chunks;
    }

    // 与官方 one-shot 状态机一致；本库不提供逐 token 流式输出。
    internal static string FilterMarkdown(string input)
    {
        var output = new StringBuilder();
        var position = 0;
        var sol = true;
        var fence = false;
        while (position < input.Length)
        {
            var remaining = input.AsSpan(position);
            if (sol && remaining.StartsWith("```"))
            {
                var newline = input.IndexOf('\n', position + 3);
                if (newline < 0) { output.Append(remaining); break; }
                output.Append(input, position, newline + 1 - position);
                position = newline + 1;
                fence = !fence;
                continue;
            }
            if (fence)
            {
                var end = input.IndexOf('\n', position);
                if (end < 0) { output.Append(remaining); break; }
                output.Append(input, position, end + 1 - position);
                position = end + 1;
                sol = true;
                continue;
            }
            if (sol)
            {
                if (input[position] == '#')
                {
                    var end = position;
                    while (end < input.Length && input[end] == '#') end++;
                    if (end - position is 5 or 6 && end < input.Length && input[end] == ' ') position = end + 1;
                }
                if (position < input.Length && input[position] is '-' or '*' or '_')
                {
                    var ch = input[position];
                    var end = position;
                    var count = 0;
                    while (end < input.Length && (input[end] == ch || input[end] == ' ')) { if (input[end] == ch) count++; end++; }
                    if (count >= 3 && (end == input.Length || input[end] == '\n'))
                    {
                        if (end < input.Length) end++;
                        output.Append(input, position, end - position);
                        position = end;
                        continue;
                    }
                }
                sol = false;
                if (position == input.Length) break;
                remaining = input.AsSpan(position);
            }
            if (remaining.StartsWith("!["))
            {
                var close = input.IndexOf(']', position + 2);
                if (close < 0) { output.Append(remaining); break; }
                if (close + 1 < input.Length && input[close + 1] == '(')
                {
                    var parenthesis = input.IndexOf(')', close + 2);
                    if (parenthesis < 0) { output.Append(remaining); break; }
                    position = parenthesis + 1;
                    continue;
                }
                output.Append(input, position, close + 1 - position);
                position = close + 1;
                continue;
            }
            if (input[position] is '*' or '_')
            {
                var ch = input[position];
                var length = remaining.Length >= 3 && remaining[1] == ch && remaining[2] == ch ? 3 : remaining.Length >= 2 && remaining[1] == ch ? 2 : 1;
                if (length == 2) { output.Append(ch, 2); position += 2; continue; }
                if (length == 3 || position + 1 < input.Length && input[position + 1] is not (' ' or '\n'))
                {
                    var start = position + length;
                    var end = -1;
                    var marker = new string(ch, length);
                    if (length == 3) end = input.IndexOf(marker, start, StringComparison.Ordinal);
                    else
                    {
                        for (var j = start; j < input.Length; j++)
                        {
                            if (input[j] == '\n') break;
                            if (input[j] != ch) continue;
                            if (j + 1 < input.Length && input[j + 1] == ch) { j++; continue; }
                            end = j; break;
                        }
                    }
                    if (end >= 0)
                    {
                        var content = input.Substring(start, end - start);
                        var cjk = content.Any(c => c is >= '\u2e80' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' or >= '\uf900' and <= '\ufaff');
                        if (!cjk) output.Append(marker);
                        output.Append(content);
                        if (!cjk) output.Append(marker);
                        position = end + length;
                        continue;
                    }
                    if (length == 3) { output.Append(remaining); break; }
                    var newline = input.IndexOf('\n', start);
                    if (newline < 0) { output.Append(remaining); break; }
                    output.Append(input, position, newline + 1 - position);
                    position = newline + 1;
                    sol = true;
                    continue;
                }
            }
            var current = input[position++];
            output.Append(current);
            if (current == '\n') sol = true;
        }
        return output.ToString();
    }

    internal static string? PartialQuote(string body, JsonObject partial)
    {
        var startText = Text(partial["start"]) ?? "";
        var endText = Text(partial["end"]) ?? "";
        if (string.IsNullOrEmpty(startText) || string.IsNullOrEmpty(endText)) return null;
        if (!int.TryParse(Text(partial["startindex"]), out var startIndex) || startIndex < 0 ||
            !int.TryParse(Text(partial["endindex"]), out var endIndex) || endIndex < 0) return null;
        static int Nth(string body, string text, int count, int offset)
        {
            if (count < 0) return -1;
            for (var i = 0; i <= count; i++)
            {
                var found = body.IndexOf(text, offset, StringComparison.Ordinal);
                if (found < 0 || i == count) return found;
                offset = found + text.Length;
            }
            return -1;
        }
        var start = Nth(body, startText, startIndex, 0);
        if (start < 0) return null;
        var expected = Text(partial["quotemd5"]);
        foreach (var offset in new[] { 0, start + startText.Length })
        {
            var end = Nth(body, endText, endIndex, offset);
            if (end < start) continue;
            var candidate = body.Substring(start, end + endText.Length - start);
            if (string.IsNullOrEmpty(expected) || Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(candidate))).Equals(expected, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }
}
