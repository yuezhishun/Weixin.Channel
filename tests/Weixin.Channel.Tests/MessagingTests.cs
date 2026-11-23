using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Weixin.Channel;
using Xunit;

namespace Weixin.Channel.Tests;

public sealed class MessagingTests
{
    [Theory]
    [InlineData("##### 标题\n**保留** *中文* ___中文___", "标题\n**保留** 中文 中文")]
    [InlineData("###### tiny\n#### heading\n# title", "tiny\n#### heading\n# title")]
    [InlineData("*English* ***English*** _English_ ___English___", "*English* ***English*** _English_ ___English___")]
    [InlineData("*こんにちは* *안녕하세요* ***你好 world***", "こんにちは 안녕하세요 你好 world")]
    [InlineData("![image](https://cdn.test/a.png) after", " after")]
    [InlineData("![unfinished](url", "![unfinished](url")]
    [InlineData("![not image] rest", "![not image] rest")]
    [InlineData("````\n*中文* ![image](url)\n```\n*中文*", "````\n*中文* ![image](url)\n```\n中文")]
    [InlineData("before\n* * *\n___\n- - -\nafter", "before\n* * *\n___\n- - -\nafter")]
    [InlineData("| A | B |\n|---|---|\n> ~~删除~~", "| A | B |\n|---|---|\n> ~~删除~~")]
    [InlineData("- first\n  * nested\n*unclosed\nnext", "- first\n  * nested\n*unclosed\nnext")]
    [InlineData("**b***i*", "**b***i*")]
    [InlineData("*hello **bold** 中文*", "hello **bold** 中文")]
    [InlineData("*未闭合 ![image](url)", "*未闭合 ![image](url)")]
    [InlineData("*未闭合 ![image](url)\n##### 标题", "*未闭合 ![image](url)\n标题")]
    [InlineData("![one](u)![two](v)", "")]
    public void Official_markdown_keeps_supported_formatting_and_removes_only_unsupported_markers(string input, string expected) => Assert.Equal(expected, Media.FilterMarkdown(input));

    [Theory]
    [InlineData("打开[下载_中文_素材](https://example.com/a_中文_b?source=网页#章节_一_节) *中文*",
        "打开[下载_中文_素材](https://example.com/a_中文_b?source=网页#章节_一_节) 中文")]
    [InlineData(@"[说明\[中文\]\_下载](https://example.com/a(b)?q=_(中)_#锚) ![图](url)",
        @"[说明\[中文\]\_下载](https://example.com/a(b)?q=_(中)_#锚) ")]
    [InlineData(@"[资料 [中文]](<https://example.com/a)_中文_槽> ""标题) _(中文)_"") *中文*",
        @"[资料 [中文]](<https://example.com/a)_中文_槽> ""标题) _(中文)_"") 中文")]
    [InlineData(@"[说明](https://example.com/a\(b\)?x=1#frag) \_中文\_ *中文*",
        @"[说明](https://example.com/a\(b\)?x=1#frag) \_中文\_ 中文")]
    [InlineData("*说明 [下载](https://example.com/a*中*b) 后续*", "说明 [下载](https://example.com/a*中*b) 后续")]
    [InlineData(@"_查看 [资料](https://example.com/a\_中_b) 后续_", @"查看 [资料](https://example.com/a\_中_b) 后续")]
    [InlineData("***查看 [资料](https://example.com/a***中) 后续***", "查看 [资料](https://example.com/a***中) 后续")]
    [InlineData("[查看 ![封面](https://example.com/cover_(1).png)](https://example.com/)", "[查看 ](https://example.com/)")]
    [InlineData("*查看 [封面 ![图](cover)](https://example.com/a*b)*", "查看 [封面 ](https://example.com/a*b)")]
    public void Markdown_filter_preserves_link_labels_escapes_and_destinations(string input, string expected) =>
        Assert.Equal(expected, Media.FilterMarkdown(input));

    [Fact]
    public void Markdown_filter_keeps_long_drafts_with_unclosed_link_syntax_responsive()
    {
        var draft = new string('[', 150_000) + "\n" + string.Concat(Enumerable.Repeat("[资料](", 30_000));
        var timer = Stopwatch.StartNew();
        var result = Media.FilterMarkdown(draft);
        var elapsed = timer.Elapsed;

        Assert.Equal(draft, result);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"发送草稿过滤用时 {elapsed}。");
    }

    [Fact]
    public void Chunking_preserves_emoji_and_the_entire_business_message()
    {
        var text = new string('中', 3999) + "😀" + new string('文', 4000) + "👨‍👧";
        var chunks = Media.Chunk(text, 4000);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => { Assert.InRange(chunk.Length, 1, 4000); Assert.False(char.IsHighSurrogate(chunk[^1])); Assert.False(char.IsLowSurrogate(chunk[0])); });
    }

    [Fact]
    public void Cdn_crypto_matches_aes128_ecb_reference_and_both_wire_key_encodings()
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");
        var encrypted = Media.Encrypt(plain, key);
        Assert.Equal("3ad77bb40d7a3660a89ecaf32466ef97", Convert.ToHexStringLower(encrypted.AsSpan(0, 16)));
        Assert.Equal(32, encrypted.Length);
        Assert.Equal(plain, Media.Decrypt(encrypted, key));
        Assert.Equal(key, Media.AesKey(Convert.ToBase64String(key)));
        Assert.Equal(key, Media.AesKey(Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(key)))));
        Assert.Throws<InvalidDataException>(() => Media.AesKey(Convert.ToBase64String(new byte[17])));
    }

    [Fact]
    public async Task Upload_requires_200_and_download_header_prefers_full_url_and_never_retries_4xx()
    {
        using var harness = new Harness();
        var plain = Encoding.UTF8.GetBytes("业务附件");
        var key = new byte[16];
        harness.Handler.Respond = async (request, _) =>
        {
            Assert.Equal("https://cdn.test/full", request.RequestUri!.ToString());
            Assert.Equal(plain, Media.Decrypt(await request.Content!.ReadAsByteArrayAsync(), key));
            var result = new HttpResponseMessage(HttpStatusCode.OK);
            if (harness.Handler.Calls.Count == 3) result.Headers.Add("x-encrypted-param", "download-param");
            return result;
        };
        var url = new JsonObject { ["upload_full_url"] = "https://cdn.test/full", ["upload_param"] = "ignored" };
        Assert.Equal("download-param", await Media.UploadAsync(harness.Http, new Uri("https://fallback.test/"), url, "file", plain, key, default));
        Assert.Equal(3, harness.Handler.Calls.Count);
        harness.Handler.Calls.Clear();
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<HttpRequestException>(() => Media.UploadAsync(harness.Http, new Uri("https://fallback.test/"), url, "file", plain, key, default));
        Assert.Single(harness.Handler.Calls);
    }

    [Fact]
    public async Task Upload_retries_server_errors_at_most_three_times()
    {
        using var harness = new Harness();
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => Media.UploadAsync(harness.Http, new Uri("https://cdn.test/c2c/"), new JsonObject { ["upload_param"] = "x+y" }, "key", [], new byte[16], default));
        Assert.Equal(3, harness.Handler.Calls.Count);
        Assert.Contains("encrypted_query_param=x%2By", harness.Handler.Calls[0].Uri);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("hex")]
    [InlineData("raw-base64")]
    [InlineData("hex-base64")]
    public async Task Inbound_images_use_full_url_and_accept_every_official_key_shape(string encoding)
    {
        using var harness = new Harness();
        var bytes = new byte[] { 137, 80, 78, 71, 1, 2, 3, 4 };
        var key = Enumerable.Range(0, 16).Select(x => (byte)x).ToArray();
        harness.Handler.Respond = (request, _) =>
        {
            Assert.Equal("https://cdn.test/full", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(encoding == "plain" ? bytes : Media.Encrypt(bytes, key)) });
        };
        var media = new JsonObject { ["full_url"] = "https://cdn.test/full", ["encrypt_query_param"] = "ignored" };
        var image = new JsonObject { ["media"] = media };
        if (encoding == "hex") { image["aeskey"] = Convert.ToHexString(key); media["aes_key"] = "ignored-invalid-key"; }
        if (encoding == "raw-base64") media["aes_key"] = Convert.ToBase64String(key);
        if (encoding == "hex-base64") media["aes_key"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(key)));
        var result = await Media.DownloadAsync(harness.Http, new Uri("https://fallback.test/"), harness.Directory, new JsonObject { ["type"] = 2, ["image_item"] = image }, false, _ => { }, default);
        Assert.NotNull(result);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    public async Task Voice_transcription_avoids_media_download_and_first_media_follows_official_priority()
    {
        using var harness = new Harness();
        var items = JsonNode.Parse("""[{"type":3,"voice_item":{"text":"微信转写","media":{"full_url":"https://cdn.test/voice"}}}]""")!.AsArray();
        harness.AddMessage("1", items);
        harness.Handler.Respond = (_, _) => throw new InvalidOperationException("转写语音不应发出媒体请求");
        await using var channel = harness.Open();
        var message = await channel.ClaimAsync();
        Assert.NotNull(message);
        Assert.Equal("微信转写", message.Text);
        Assert.Empty(message.Attachments);
        Assert.Empty(harness.Handler.Calls);
        var mixed = JsonNode.Parse("""[{"type":3,"voice_item":{"media":{"full_url":"voice"}}},{"type":4,"file_item":{"media":{"full_url":"file"}}},{"type":5,"video_item":{"media":{"full_url":"video"}}},{"type":2,"image_item":{"media":{"full_url":"image"}}}]""")!.AsArray();
        Assert.Equal(2, Media.Number(Media.FirstMedia(mixed)!["type"]));
    }

    [Fact]
    public async Task Invalid_silk_preserves_raw_voice_while_network_failure_keeps_inbox_retryable()
    {
        using var harness = new Harness();
        var key = new byte[16];
        var raw = Encoding.UTF8.GetBytes("invalid silk");
        var item = new JsonObject { ["type"] = 3, ["voice_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://cdn.test/voice", ["aes_key"] = Convert.ToBase64String(key) } } };
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Media.Encrypt(raw, key)) });
        var voice = await Media.DownloadAsync(harness.Http, new Uri("https://cdn.test"), harness.Directory, item, false, _ => { }, default);
        Assert.NotNull(voice);
        Assert.Equal("audio/silk", voice.MimeType);
        Assert.Equal(raw, await File.ReadAllBytesAsync(voice.Path));
        harness.AddMessage("1", new JsonArray(item.DeepClone()));
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("simulated connection reset");
        await using var channel = harness.Open();
        await Assert.ThrowsAsync<HttpRequestException>(() => channel.ClaimAsync());
        Assert.Equal("pending", Assert.Single(channel.GetInbox()).State);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    public void Silk_fixture_decodes_to_24khz_mono_pcm_and_matches_reference_codec(int inputRate)
    {
        var silk = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"tone-{inputRate}.silk"));
        Assert.Equal(2, silk[0]);
        Assert.Equal("#!SILK_V3", Encoding.ASCII.GetString(silk, 1, 9));
        var offset = 10;
        while (offset < silk.Length)
        {
            var packet = BinaryPrimitives.ReadInt16LittleEndian(silk.AsSpan(offset));
            Assert.InRange(packet, 1, silk.Length - offset - 2);
            offset += packet + 2;
        }
        Assert.Equal(silk.Length, offset);
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"tone-{inputRate}.native.pcm"));
        var wav = Media.SilkToWav(silk);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(expected.Length + 44, wav.Length);
        var actual = wav.AsSpan(44).ToArray();
        if (inputRate == 24000) Assert.Equal(expected, actual);
        else
        {
            var best = 0d;
            for (var lag = -100; lag <= 100; lag++)
            {
                double dot = 0, aa = 0, bb = 0;
                for (var i = 200; i < actual.Length / 2 - 200; i++)
                {
                    var a = BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(i * 2));
                    var b = BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan((i + lag) * 2));
                    dot += (double)a * b; aa += (double)a * a; bb += (double)b * b;
                }
                best = Math.Max(best, dot / Math.Sqrt(aa * bb));
            }
            Assert.True(best > 0.999, $"重采样对齐后波形相关性 {best} 未达到参考协议要求。");
        }
    }

    [Fact]
    public void Quote_cache_survives_restart_isolates_peers_and_preserves_uint64_identifiers()
    {
        using var harness = new Harness();
        var id = ulong.MaxValue.ToString();
        using (var quotes = new QuoteStore(harness.Options, _ => { })) quotes.Put("bot", "owner", id, "原始正文", null, harness.Time.GetUtcNow());
        using var reopened = new QuoteStore(harness.Options, _ => { });
        Assert.Equal("原始正文", reopened.Find("bot", "owner", id)!.Text);
        Assert.Null(reopened.Find("bot2", "owner", id));
        Assert.Null(reopened.Find("bot", "other", id));
    }

    [Fact]
    public void Quote_media_expiration_keeps_a_missing_placeholder_and_text_expires_after_thirty_days()
    {
        using var harness = new Harness();
        var source = Path.Combine(harness.Directory, "source.txt");
        File.WriteAllText(source, "附件正文");
        using var quotes = new QuoteStore(harness.Options, _ => { });
        quotes.Put("bot", "owner", "media", "[文件]", new WeixinAttachment("file", source, "text/plain", "账单.txt"), harness.Time.GetUtcNow());
        quotes.Put("bot", "owner", "text", "持久文本", null, harness.Time.GetUtcNow());
        var cached = quotes.Find("bot", "owner", "media")!.Attachment!.Path;
        Assert.NotEqual(source, cached);
        harness.Time.Advance(TimeSpan.FromDays(8));
        Assert.True(quotes.Find("bot", "owner", "media")!.Missing);
        Assert.Contains("账单.txt", quotes.Find("bot", "owner", "media")!.Text);
        Assert.False(File.Exists(cached));
        Assert.Equal("持久文本", quotes.Find("bot", "owner", "text")!.Text);
        harness.Time.Advance(TimeSpan.FromDays(23));
        Assert.Null(quotes.Find("bot", "owner", "text"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Quote_limits_evict_oldest_records_and_media_without_deleting_host_files()
    {
        using var harness = new Harness();
        var options = harness.Options with { QuoteTextLimit = 2, QuoteMediaBytesLimit = 5, QuoteMediaFileBytesLimit = 5 };
        using var quotes = new QuoteStore(options, _ => { });
        var old = Path.Combine(harness.Directory, "old.bin");
        var recent = Path.Combine(harness.Directory, "recent.bin");
        var large = Path.Combine(harness.Directory, "large.bin");
        File.WriteAllBytes(old, [1, 2, 3, 4]); File.WriteAllBytes(recent, [5, 6, 7, 8]); File.WriteAllBytes(large, [1, 2, 3, 4, 5, 6]);
        quotes.Put("bot", "owner", "old", "[文件]", new("file", old, "application/octet-stream"), harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        quotes.Put("bot", "owner", "recent", "[文件]", new("file", recent, "application/octet-stream"), harness.Time.GetUtcNow());
        Assert.True(quotes.Find("bot", "owner", "old")!.Missing);
        Assert.NotNull(quotes.Find("bot", "owner", "recent")!.Attachment);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        quotes.Put("bot", "owner", "large", "[文件]", new("file", large, "application/octet-stream"), harness.Time.GetUtcNow());
        Assert.Null(quotes.Find("bot", "owner", "old"));
        Assert.True(quotes.Find("bot", "owner", "large")!.Missing);
        Assert.All(new[] { old, recent, large }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Replacing_cached_media_immediately_removes_the_previous_unreferenced_file()
    {
        using var harness = new Harness();
        using var quotes = new QuoteStore(harness.Options with { QuoteMediaFileBytesLimit = 4 }, _ => { });
        var first = Path.Combine(harness.Directory, "first.bin");
        var second = Path.Combine(harness.Directory, "second.bin");
        var large = Path.Combine(harness.Directory, "large.bin");
        File.WriteAllBytes(first, [1]); File.WriteAllBytes(second, [2]); File.WriteAllBytes(large, [1, 2, 3, 4, 5]);
        quotes.Put("bot", "owner", "1", "[文件]", new("file", first, "application/octet-stream"), harness.Time.GetUtcNow());
        var previous = quotes.Find("bot", "owner", "1")!.Attachment!.Path;
        quotes.Put("bot", "owner", "1", "[文件]", new("file", second, "application/octet-stream"), harness.Time.GetUtcNow());
        Assert.False(File.Exists(previous));
        var updated = quotes.Find("bot", "owner", "1")!.Attachment!.Path;
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(updated));
        quotes.Put("bot", "owner", "1", "[文件]", new("file", large, "application/octet-stream"), harness.Time.GetUtcNow());
        Assert.True(quotes.Find("bot", "owner", "1")!.Missing);
        Assert.False(File.Exists(updated));
        Assert.True(File.Exists(first)); Assert.True(File.Exists(second)); Assert.True(File.Exists(large));
    }

    [Fact]
    public async Task Corrupt_quote_database_does_not_prevent_core_claim_or_send()
    {
        using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.Directory, "quotes.sqlite"), "not a sqlite database");
        harness.AddMessage("1", JsonNode.Parse("""[{"type":1,"text_item":{"text":"问题"},"ref_msg":{"svr_id":18446744073709551615}}]""")!.AsArray());
        await using var channel = harness.Open();
        var message = await channel.ClaimAsync();
        Assert.NotNull(message);
        Assert.True(message.Quote!.Missing);
        Assert.Equal(ulong.MaxValue.ToString(), message.Quote.MessageId);
        Assert.Equal("success", (await message.ReplyAsync("正常回复")).Status);
    }

    [Fact]
    public async Task Id_only_and_partial_quotes_restore_content_from_the_same_conversation()
    {
        using var harness = new Harness();
        const string body = "第一段 开始重复结束 第二段 开始选择结束";
        const string selection = "开始选择结束";
        using (var quotes = new QuoteStore(harness.Options, _ => { })) quotes.Put("bot", "owner", "18446744073709551615", body, null, harness.Time.GetUtcNow());
        var partial = new JsonObject { ["start"] = "开始", ["end"] = "结束", ["startindex"] = 1, ["endindex"] = 1, ["quotemd5"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(selection))) };
        harness.AddMessage("1", new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = "这个呢" }, ["ref_msg"] = new JsonObject { ["svr_id"] = "18446744073709551615", ["partial_text"] = partial } }));
        await using var channel = harness.Open();
        var message = await channel.ClaimAsync();
        Assert.Equal(selection, message!.Quote!.Text);
        var invalid = partial.DeepClone().AsObject();
        invalid["quotemd5"] = "incorrect";
        Assert.Null(Media.PartialQuote(body, invalid));
    }

    [Fact]
    public async Task Inline_quoted_media_download_is_marked_as_reference()
    {
        using var harness = new Harness();
        harness.AddMessage("1", JsonNode.Parse("""[{"type":1,"text_item":{"text":"看这张"},"ref_msg":{"title":"相片","message_item":{"type":2,"image_item":{"media":{"full_url":"https://cdn.test/photo"}}}}}]""")!.AsArray());
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([137, 80, 78, 71]) });
        await using var channel = harness.Open();
        var message = await channel.ClaimAsync();
        Assert.True(Assert.Single(message!.Attachments).Referenced);
        Assert.Equal("相片 | [图片]", message.Quote!.Text);
    }

    [Fact]
    public async Task Caption_chunks_precede_media_and_each_send_keeps_its_independent_receipt()
    {
        using var harness = new Harness();
        harness.AddMessage("1", []);
        var file = Path.Combine(harness.Directory, "report.pdf");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var wire = new List<JsonObject>();
        harness.Handler.Respond = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getuploadurl"))
            {
                var upload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                Assert.Equal(3, Media.Number(upload["media_type"]));
                Assert.Equal(Convert.ToHexStringLower(MD5.HashData([1, 2, 3, 4])), Media.Text(upload["rawfilemd5"]));
                Assert.Equal(4, Media.Number(upload["rawsize"])); Assert.Equal(16, Media.Number(upload["filesize"]));
                return Json("""{"upload_full_url":"https://cdn.test/upload"}""");
            }
            if (request.RequestUri.Host == "cdn.test")
            {
                var result = new HttpResponseMessage(HttpStatusCode.OK); result.Headers.Add("x-encrypted-param", "uploaded"); return result;
            }
            wire.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["msg"]!.AsObject());
            return Json($$"""{"message_id":"{{wire.Count}}","ret":0}""");
        };
        await using var channel = harness.Open(o => o with { TextChunkSize = 4 });
        var message = Assert.IsType<InboundMessage>(await channel.ClaimAsync());
        var result = await channel.SendAsync(new SendRequest { DeliveryId = message.DeliveryId, Text = "**hi**😀", Media = new Uri(file).AbsoluteUri });
        Assert.Equal("success", result.Status);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(3, result.Segments.Select(p => p.ClientId).Distinct().Count());
        Assert.All(wire, msg => Assert.Matches(@"\Aweixin-[0-9a-f]{32}\z", Media.Text(msg["client_id"])!));
        Assert.Equal(new[] { 1, 1, 4 }, wire.Select(msg => Media.Number(msg["item_list"]![0]!["type"])));
        Assert.Equal("**hi**😀", string.Concat(wire.Take(2).Select(msg => Media.Text(msg["item_list"]![0]!["text_item"]!["text"]))));
        Assert.Equal("report.pdf", Media.Text(wire[2]["item_list"]![0]!["file_item"]!["file_name"]));
    }

    [Fact]
    public async Task Caption_success_and_media_failure_reports_partial_and_keeps_caption_receipt()
    {
        using var harness = new Harness();
        await using var channel = harness.Open();
        var result = await channel.SendAsync(new SendRequest { PeerId = "owner", Text = "附件说明", Media = Path.Combine(harness.Directory, "missing.pdf") });
        Assert.Equal("partial", result.Status);
        Assert.Equal(new[] { "success", "failed" }, result.Segments.Select(p => p.Status));
        Assert.Single(harness.Handler.Calls);
    }

    [Fact]
    public async Task Unknown_delivery_send_is_persisted_and_blocks_automatic_repetition()
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray(new JsonObject { ["type"] = 1, ["text_item"] = new JsonObject { ["text"] = "ping" } }));
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("connection closed after request body");
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        Assert.Equal("unknown", (await message.ReplyAsync("可能已送达")).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => message.ReplyAsync("不可自动重复"));
        Assert.Single(harness.Handler.Calls);
        await channel.RetryAsync(message.DeliveryId, message.LeaseId, true);
        harness.Handler.Respond = (_, _) => Task.FromResult(Json("""{"ret":0,"message_id":18446744073709551615}"""));
        var result = await channel.ReplyAsync(message.DeliveryId, "人工核查后重试");
        Assert.Equal("success", result.Status);
        Assert.Equal(ulong.MaxValue.ToString(), Assert.Single(result.Segments).MessageId);
    }

    [Fact]
    public async Task Hook_can_modify_or_cancel_and_hook_failures_cannot_erase_send_success()
    {
        using var harness = new Harness();
        await using (var channel = harness.Open(o => o with
        {
            BeforeSend = (request, _) => ValueTask.FromResult<SendRequest?>(request with { Text = "修改后" }),
            AfterSend = (_, _) => throw new InvalidOperationException("宿主 hook 失败")
        }))
        {
            Assert.Equal("success", (await channel.SendAsync(new SendRequest { PeerId = "owner", Text = "原文" })).Status);
            Assert.Equal("修改后", Media.Text(JsonNode.Parse(harness.Handler.Calls.Single().Body)?["msg"]?["item_list"]?[0]?["text_item"]?["text"]));
        }
        harness.Handler.Calls.Clear();
        await using var cancelled = harness.Open(o => o with { BeforeSend = (_, _) => ValueTask.FromResult<SendRequest?>(null) });
        Assert.Equal("cancelled", (await cancelled.SendAsync(new SendRequest { PeerId = "owner", Text = "取消" })).Status);
        Assert.Empty(harness.Handler.Calls);
    }

    [Fact]
    public async Task Reply_matches_official_client_id_and_uses_original_delivery_context()
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray(), "old-context");
        harness.AddMessage("2", new JsonArray(), "new-context");
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        var result = await channel.SendAsync(new SendRequest { DeliveryId = message.DeliveryId, Text = "回复第一条" });
        var wire = JsonNode.Parse(Assert.Single(harness.Handler.Calls).Body)!["msg"]!;
        Assert.Equal("old-context", Media.Text(wire["context_token"]));
        Assert.Null(wire["run_id"]);
        var clientId = Assert.Single(result.Segments).ClientId;
        Assert.Equal(clientId, Media.Text(wire["client_id"]));
        Assert.Matches($$"""\Aopenclaw-weixin:{{harness.Time.GetUtcNow().ToUnixTimeMilliseconds()}}-[0-9a-f]{8}\z""", clientId);
    }

    [Fact]
    public async Task Typing_reuses_ticket_and_always_cancels_when_host_work_fails()
    {
        using var harness = new Harness();
        harness.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("getconfig") ? Json("""{"ret":0,"typing_ticket":"ticket"}""") : Json("""{"ret":0}"""));
        await using var channel = harness.Open();
        await Assert.ThrowsAsync<IOException>(() => channel.WithTypingAsync("owner", _ => throw new IOException("handler failed")));
        Assert.Single(harness.Handler.Calls, call => call.Uri.EndsWith("getconfig"));
        var typings = harness.Handler.Calls.Where(call => call.Uri.EndsWith("sendtyping")).Select(call => JsonNode.Parse(call.Body)!).ToArray();
        Assert.Equal(new[] { 1, 2 }, typings.Select(item => Media.Number(item["status"])));
        Assert.All(typings, item => Assert.Equal("ticket", Media.Text(item["typing_ticket"])));
    }

    [Fact]
    public async Task Typing_config_failure_uses_backoff_instead_of_hammering_server()
    {
        using var harness = new Harness();
        harness.Handler.Respond = (_, _) => Task.FromResult(Json("""{"ret":-1}"""));
        await using var channel = harness.Open();
        await channel.TypingAsync("owner");
        await channel.TypingAsync("owner");
        Assert.Single(harness.Handler.Calls);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await channel.TypingAsync("owner");
        Assert.Equal(2, harness.Handler.Calls.Count);
        harness.Time.Advance(TimeSpan.FromSeconds(3));
        await channel.TypingAsync("owner");
        Assert.Equal(2, harness.Handler.Calls.Count);
    }

    [Fact]
    public async Task Missing_config_ret_is_failure_and_empty_successful_typing_body_is_accepted()
    {
        using var harness = new Harness();
        harness.Handler.Respond = (_, _) => Task.FromResult(Json("{}"));
        await using var channel = harness.Open();
        await channel.TypingAsync("owner");
        Assert.Single(harness.Handler.Calls);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        harness.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("getconfig")
            ? Json("""{"ret":0,"typing_ticket":"ticket"}""") : new HttpResponseMessage(HttpStatusCode.NoContent));
        await channel.TypingAsync("owner");
        Assert.Equal(3, harness.Handler.Calls.Count);
    }

    [Fact]
    public async Task Typing_keeps_alive_every_five_seconds_during_host_work_and_cancels_at_end()
    {
        using var harness = new Harness();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Respond = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getconfig")) return Task.FromResult(Json("""{"ret":0,"typing_ticket":"ticket"}"""));
            if (harness.Handler.Calls.Count(call => call.Uri.EndsWith("sendtyping")) == 2) second.TrySetResult();
            return Task.FromResult(Json("""{"ret":0}"""));
        };
        await using var channel = harness.Open();
        var work = channel.WithTypingAsync("owner", _ => finish.Task);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        finish.TrySetResult();
        await work;
        Assert.Equal(new[] { 1, 1, 2 }, harness.Handler.Calls.Where(call => call.Uri.EndsWith("sendtyping")).Select(call => Media.Number(JsonNode.Parse(call.Body)!["status"])));
    }

    [Theory]
    [InlineData("download")]
    [InlineData("remote")]
    [InlineData("upload")]
    public async Task Hung_media_request_has_bounded_deadline_and_cannot_hold_inbox_forever(string operation)
    {
        using var harness = new Harness();
        harness.Handler.Respond = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); };
        Task request = operation switch
        {
            "download" => Media.DownloadAsync(harness.Http, new Uri("https://cdn.test/"), harness.Directory,
                JsonNode.Parse("""{"type":2,"image_item":{"media":{"full_url":"https://cdn.test/media"}}}""")!, false, _ => { }, default, TimeSpan.FromSeconds(2), harness.Time),
            "remote" => Media.ResolveFileAsync(harness.Http, "https://cdn.test/media", harness.Directory, default, TimeSpan.FromSeconds(2), harness.Time),
            _ => Media.UploadAsync(harness.Http, new Uri("https://cdn.test/"), new JsonObject { ["upload_param"] = "param" }, "file", [], new byte[16], default, TimeSpan.FromSeconds(2), harness.Time)
        };
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Single(harness.Handler.Calls);
    }

    [Fact]
    public void Silk_packet_budget_prevents_unbounded_decompression_allocation()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(2); stream.Write("#!SILK_V3"u8);
        for (var i = 0; i <= Media.MaximumBytes / 4800; i++) stream.Write([1, 0, 0]);
        var exception = Assert.Throws<InvalidDataException>(() => Media.SilkToWav(stream.ToArray()));
        Assert.Contains("100 MiB", exception.Message);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("invalid-packet")]
    [InlineData("trailing-garbage")]
    public async Task Damaged_wechat_silk_container_keeps_the_original_attachment(string damage)
    {
        using var harness = new Harness();
        var silk = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone-24000.silk"));
        var broken = damage switch
        {
            "truncated" => silk[..^1],
            "trailing-garbage" => silk.Concat(new byte[] { 1 }).ToArray(),
            _ => (byte[])silk.Clone()
        };
        if (damage == "invalid-packet") { broken[10] = 0xff; broken[11] = 0x7f; }
        var key = new byte[16];
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Media.Encrypt(broken, key)) });
        var item = new JsonObject { ["type"] = 3, ["voice_item"] = new JsonObject { ["media"] = new JsonObject { ["full_url"] = "https://cdn.test/voice", ["aes_key"] = Convert.ToBase64String(key) } } };
        var result = await Media.DownloadAsync(harness.Http, new Uri("https://cdn.test/"), harness.Directory, item, false, _ => { }, default);
        Assert.Equal("audio/silk", result!.MimeType);
        Assert.Equal(broken, File.ReadAllBytes(result.Path));
    }

    [Theory]
    [InlineData("photo.png", 1, 2, "path")]
    [InlineData("movie.mp4", 2, 5, "path")]
    [InlineData("audio.mp3", 3, 4, "path")]
    [InlineData("photo.png", 1, 2, "file")]
    [InlineData("photo.png", 1, 2, "http")]
    [InlineData("photo.png", 1, 2, "https")]
    public async Task Outbound_sources_keep_original_bytes_and_choose_the_official_media_wire_types(string filename, int uploadType, int itemType, string scheme)
    {
        using var harness = new Harness();
        byte[] original = [1, 2, 3];
        var remote = scheme is "http" or "https";
        var file = Path.Combine(harness.Directory, filename);
        if (!remote) File.WriteAllBytes(file, original);
        var source = remote ? $"{scheme}://source.test/{filename}" : scheme == "file" ? new Uri(file, UriKind.Absolute).AbsoluteUri : file;
        byte[]? key = null;
        string? rawMd5 = null;
        string? fileKey = null;
        byte[]? uploaded = null;
        harness.Handler.Respond = async (request, _) =>
        {
            if (request.RequestUri!.Host == "source.test")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(original) };
            if (request.RequestUri.AbsolutePath.EndsWith("getuploadurl"))
            {
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal(uploadType, Media.Number(payload["media_type"]));
                rawMd5 = Media.Text(payload["rawfilemd5"]);
                fileKey = Media.Text(payload["filekey"]);
                key = Convert.FromHexString(Media.Text(payload["aeskey"])!);
                return Json("""{"upload_full_url":"https://cdn.test/upload"}""");
            }
            if (request.RequestUri.Host == "cdn.test")
            {
                uploaded = await request.Content!.ReadAsByteArrayAsync();
                var result = new HttpResponseMessage(HttpStatusCode.OK); result.Headers.Add("x-encrypted-param", "uploaded"); return result;
            }
            Assert.Equal(itemType, Media.Number(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["msg"]!["item_list"]![0]!["type"]));
            return Json("""{"ret":0,"message_id":"1"}""");
        };
        await using var channel = harness.Open();
        Assert.Equal("success", (await channel.SendAsync(new SendRequest { PeerId = "owner", Media = source })).Status);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(original)), rawMd5);
        Assert.Equal(16, Convert.FromHexString(fileKey!).Length);
        Assert.Equal(original, Media.Decrypt(uploaded!, key!));
        Assert.Equal(remote ? 4 : 3, harness.Handler.Calls.Count);
        if (remote) Assert.Equal(source, harness.Handler.Calls[0].Uri);
    }

    [Fact]
    public async Task Best_effort_progress_unknown_does_not_block_the_required_business_reply()
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray());
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("progress connection closed");
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        await channel.ProgressAsync(new ToolProgress("owner", "different-run", "tool", "search", DeliveryId: message.DeliveryId));
        await channel.DrainDeliveryProgressAsync(message.DeliveryId);
        harness.Handler.Respond = (_, _) => Task.FromResult(Json("""{"ret":0}"""));
        Assert.Equal("success", (await message.ReplyAsync("最终结果")).Status);
        await channel.AcknowledgeAsync(message.DeliveryId, message.LeaseId);
        Assert.Empty(channel.GetInbox());
    }

    [Fact]
    public async Task Explicit_unknown_retry_approval_clears_the_barrier_only_after_duplicate_risk_is_accepted()
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray());
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        harness.Handler.Respond = (_, _) => throw new HttpRequestException("after send");
        Assert.Equal("unknown", (await message.ReplyAsync("reply")).Status);
        harness.Handler.Respond = (_, _) => Task.FromResult(Json("""{"ret":0}"""));
        Assert.Equal("success", (await channel.SendAsync(new SendRequest { DeliveryId = message.DeliveryId, Text = "人工重试", AllowUnknownRetry = true })).Status);
        Assert.Equal("success", (await message.ReplyAsync("后续回复")).Status);
        await channel.AcknowledgeAsync(message.DeliveryId, message.LeaseId);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("config")]
    [InlineData("typing")]
    public async Task Expired_session_from_any_media_or_typing_endpoint_pauses_the_account_for_one_hour(string endpoint)
    {
        using var harness = new Harness();
        harness.Handler.Respond = (request, _) => Task.FromResult(endpoint == "typing" && request.RequestUri!.AbsolutePath.EndsWith("getconfig")
            ? Json("""{"ret":0,"typing_ticket":"ticket"}""") : Json("""{"ret":-14}"""));
        await using var channel = harness.Open();
        if (endpoint == "upload")
        {
            var file = Path.Combine(harness.Directory, "media.png"); File.WriteAllBytes(file, [1, 2, 3]);
            Assert.Equal("failed", (await channel.SendAsync(new SendRequest { PeerId = "owner", Media = file })).Status);
        }
        else if (endpoint == "typing") await Assert.ThrowsAsync<InvalidDataException>(() => channel.TypingAsync("owner"));
        else await channel.TypingAsync("owner");
        Assert.Equal(harness.Time.GetUtcNow().AddHours(1), Assert.Single(channel.GetAccounts()).PausedUntil);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(new SendRequest { PeerId = "owner", Text = "登录态失效时不继续发送" }));
    }

    [Fact]
    public async Task Progress_is_serial_and_drain_preserves_shared_run_and_tool_identifiers()
    {
        using var harness = new Harness();
        harness.AddMessage("1", [], context: "original-context");
        harness.AddMessage("2", [], context: "newer-context");
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Respond = async (_, ct) =>
        {
            if (harness.Handler.Calls.Count == 1) { firstSent.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Json("""{"ret":0}""");
        };
        await using var channel = harness.Open();
        var message = Assert.IsType<InboundMessage>(await channel.ClaimAsync());
        var start = message.ProgressAsync("call", "工具");
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var end = message.ProgressAsync("call", "工具", "completed");
        var drained = channel.DrainProgressAsync(message.RunId);
        Assert.False(drained.IsCompleted);
        Assert.Single(harness.Handler.Calls);
        release.TrySetResult();
        await Task.WhenAll(start, end, drained);
        Assert.Equal("success", (await message.ReplyAsync("最终正文")).Status);
        var messages = harness.Handler.Calls.Select(call => JsonNode.Parse(call.Body)!["msg"]!).ToArray();
        Assert.Equal(new[] { 11, 12, 1 }, messages.Select(msg => Media.Number(msg["item_list"]![0]!["type"])));
        Assert.All(messages, msg =>
        {
            Assert.Equal(message.RunId, Media.Text(msg["run_id"]));
            Assert.Equal("original-context", Media.Text(msg["context_token"]));
            Assert.Matches(@"\Aweixin-[0-9a-f]{32}\z", Media.Text(msg["client_id"])!);
        });
        Assert.Equal("call", Media.Text(messages[0]["item_list"]![0]!["tool_call_start_item"]!["tool_call_id"]));
        Assert.Equal("call", Media.Text(messages[1]["item_list"]![0]!["tool_call_result_item"]!["tool_call_id"]));
        Assert.Equal("completed", Media.Text(messages[1]["item_list"]![0]!["tool_call_result_item"]!["status"]));
        await message.ProgressAsync("late", "late");
        Assert.Equal(3, harness.Handler.Calls.Count);
    }

    [Theory]
    [InlineData(400, "failed")]
    [InlineData(403, "failed")]
    [InlineData(429, "failed")]
    [InlineData(500, "unknown")]
    [InlineData(502, "unknown")]
    [InlineData(504, "unknown")]
    public async Task Send_http_server_failure_cannot_prove_non_delivery_and_must_block_automatic_repetition(int code, string expected)
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray());
        harness.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        Assert.Equal(expected, (await message.ReplyAsync("可能已经送达")).Status);
        if (expected == "unknown")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => message.ReplyAsync("不能自动重复"));
            Assert.Single(harness.Handler.Calls);
        }
        else
        {
            harness.Handler.Respond = (_, _) => Task.FromResult(Json("""{"ret":0}"""));
            Assert.Equal("success", (await message.ReplyAsync("明确拒绝后可以再试")).Status);
        }
    }

    [Fact]
    public async Task Proven_preflight_rejection_is_failed_instead_of_unknown()
    {
        using var harness = new Harness();
        harness.Handler.Respond = (_, _) =>
        {
            var error = new InvalidOperationException("协议明确尚未发起请求");
            error.Data["WeixinRequestNotSent"] = true;
            throw error;
        };
        await using var channel = harness.Open();
        Assert.Equal("failed", (await channel.SendAsync(new SendRequest { PeerId = "owner", Text = "内容" })).Status);
    }

    [Fact]
    public async Task Quoted_attachment_survives_eviction_by_the_current_message_before_claim_returns()
    {
        using var harness = new Harness();
        var options = harness.Options with { QuoteTextLimit = 1 };
        var source = Path.Combine(harness.Directory, "quote-source.txt");
        File.WriteAllText(source, "引用文件内容");
        string cached;
        using (var quotes = new QuoteStore(options, _ => { }))
        {
            quotes.Put("bot", "owner", "old", "[文件]", new WeixinAttachment("file", source, "text/plain", "账单.txt"), harness.Time.GetUtcNow());
            cached = quotes.Find("bot", "owner", "old")!.Attachment!.Path;
        }
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        harness.AddMessage("new", JsonNode.Parse("""[{"type":1,"text_item":{"text":"请读取引用"},"ref_msg":{"svr_id":"old"}}]""")!.AsArray());
        await using var channel = harness.Open(_ => options);
        var message = (await channel.ClaimAsync())!;
        Assert.False(File.Exists(cached));
        var delivered = Assert.Single(message.Attachments);
        Assert.Equal(delivered.Path, message.Quote!.Attachment!.Path);
        Assert.Equal("引用文件内容", File.ReadAllText(delivered.Path));
        await channel.AcknowledgeAsync(message.DeliveryId, message.LeaseId);
        Assert.False(File.Exists(delivered.Path));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Cached_current_media_is_a_delivery_copy_that_survives_later_cache_cleanup()
    {
        using var harness = new Harness();
        var source = Path.Combine(harness.Directory, "source.png");
        var content = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        File.WriteAllBytes(source, content);
        string cached;
        using (var quotes = new QuoteStore(harness.Options, _ => { }))
        {
            quotes.Put("bot", "owner", "1", "[图片]", new WeixinAttachment("image", source, "image/png"), harness.Time.GetUtcNow());
            cached = quotes.Find("bot", "owner", "1")!.Attachment!.Path;
        }
        harness.AddMessage("1", JsonNode.Parse("""[{"type":2,"image_item":{"media":{"full_url":"https://cdn.test/unused"}}}]""")!.AsArray());
        harness.Handler.Respond = (_, _) => throw new InvalidOperationException("缓存命中不应再次下载");
        await using var channel = harness.Open();
        var message = (await channel.ClaimAsync())!;
        var delivered = Assert.Single(message.Attachments);
        Assert.NotEqual(cached, delivered.Path);
        using (var quotes = new QuoteStore(harness.Options, _ => { })) quotes.DeleteAccount("bot");
        Assert.False(File.Exists(cached));
        Assert.Equal(content, File.ReadAllBytes(delivered.Path));
        Assert.Empty(harness.Handler.Calls);
        await channel.AcknowledgeAsync(message.DeliveryId, message.LeaseId);
        Assert.False(File.Exists(delivered.Path));
    }

    [Fact]
    public async Task Slow_account_typing_configuration_does_not_block_another_account()
    {
        using var harness = new Harness();
        using (var store = new CoreStore(harness.Directory, harness.Time)) store.SaveAccount("bot2", "owner2", "synthetic-token2", "https://api2.test/");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.Respond = async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.test" && request.RequestUri.AbsolutePath.EndsWith("getconfig"))
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return request.RequestUri.AbsolutePath.EndsWith("getconfig") ? Json("""{"ret":0,"typing_ticket":"ticket"}""") : Json("""{"ret":0}""");
        };
        await using var channel = harness.Open();
        var first = channel.TypingAsync("owner", accountId: "bot");
        await entered.Task;
        try
        {
            await channel.TypingAsync("owner2", accountId: "bot2").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(harness.Handler.Calls, call => call.Uri == "https://api2.test/ilink/bot/sendtyping");
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_of_the_same_delivery_can_emit_progress_after_a_drained_or_cancelled_attempt(bool cancelDrain)
    {
        using var harness = new Harness();
        harness.AddMessage("1", new JsonArray());
        await using var channel = harness.Open();
        var first = (await channel.ClaimAsync())!;
        using var cancellation = new CancellationTokenSource();
        if (cancelDrain)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Handler.Respond = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Json("""{"ret":0}"""); };
            var progress = first.ProgressAsync("first-call", "first-tool");
            await entered.Task;
            var drain = channel.DrainDeliveryProgressAsync(first.DeliveryId, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
            release.TrySetResult();
            await progress;
        }
        else
        {
            await first.ProgressAsync("first-call", "first-tool");
            await channel.DrainDeliveryProgressAsync(first.DeliveryId);
        }
        await channel.RetryAsync(first.DeliveryId, first.LeaseId);
        var second = (await channel.ClaimAsync())!;
        Assert.Equal(first.DeliveryId, second.DeliveryId);
        await second.ProgressAsync("retry-call", "retry-tool");
        Assert.Equal(2, harness.Handler.Calls.Count);
        Assert.Equal("retry-call", Media.Text(JsonNode.Parse(harness.Handler.Calls[1].Body)!["msg"]!["item_list"]![0]!["tool_call_start_item"]!["tool_call_id"]));
        await channel.DrainDeliveryProgressAsync(second.DeliveryId);
        await channel.AcknowledgeAsync(second.DeliveryId, second.LeaseId);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("filtered")]
    [InlineData("hook-empty")]
    [InlineData("hook-cancel")]
    public async Task Empty_or_cancelled_content_reports_cancelled_without_a_send_receipt_or_success_notification(string mode)
    {
        using var harness = new Harness();
        var hookNotifications = 0;
        await using var channel = harness.Open(options => options with
        {
            AfterSend = (_, _) => { hookNotifications++; return ValueTask.CompletedTask; },
            BeforeSend = mode == "hook-empty" ? (request, _) => ValueTask.FromResult<SendRequest?>(request with { Text = "", Media = null }) :
                mode == "hook-cancel" ? (_, _) => ValueTask.FromResult<SendRequest?>(null) : null
        });
        using var cancellation = new CancellationTokenSource();
        await using var events = channel.EventsAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var result = await channel.SendAsync(new SendRequest { PeerId = "owner", Text = mode == "empty" ? "" : mode == "filtered" ? "![图片](url)" : "hook 删除的原文" });
        Assert.Equal("cancelled", result.Status);
        Assert.Empty(result.Segments);
        Assert.Empty(harness.Handler.Calls);
        Assert.Equal(0, hookNotifications);
        var notification = events.MoveNextAsync().AsTask();
        Assert.False(notification.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
    }

    [Fact]
    public async Task Startup_removes_completed_deleted_and_outgoing_temporary_media_but_keeps_unacknowledged_files()
    {
        using var harness = new Harness();
        harness.AddMessage("done", new JsonArray());
        harness.AddMessage("pending", new JsonArray());
        string done;
        string pending;
        using (var store = new CoreStore(harness.Directory, harness.Time))
        {
            var row = store.Claim("bot", TimeSpan.FromSeconds(60))!;
            done = row.DeliveryId;
            store.Complete(row.DeliveryId, row.LeaseId);
            pending = Assert.Single(store.Inbox(null, false)).DeliveryId;
        }
        string AddTemporary(string delivery)
        {
            var directory = Path.Combine(harness.Directory, "media", "inbox", QuoteStore.AccountDirectory(delivery));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "attachment.txt"); File.WriteAllText(file, delivery); return file;
        }
        var doneFile = AddTemporary(done);
        var pendingFile = AddTemporary(pending);
        var deletedFile = AddTemporary("deleted-delivery");
        var outgoing = Path.Combine(harness.Directory, "media", "outgoing"); Directory.CreateDirectory(outgoing);
        var outgoingFile = Path.Combine(outgoing, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(outgoingFile, [1, 2, 3]);
        await using var channel = harness.Open();
        Assert.False(File.Exists(doneFile)); Assert.False(File.Exists(deletedFile)); Assert.False(File.Exists(outgoingFile));
        Assert.Equal(pending, File.ReadAllText(pendingFile));
        Assert.Equal(pending, Assert.Single(channel.GetInbox()).DeliveryId);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Harness : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "weixin-messaging-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        internal CaptureHandler Handler { get; } = new();
        internal HttpClient Http { get; }
        internal WeixinOptions Options => new() { StateDirectory = Directory, HttpClient = Http, TimeProvider = Time };
        internal Harness()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var store = new CoreStore(Directory, Time);
            store.SaveAccount("bot", "owner", "synthetic-token", "https://api.test/");
        }
        internal void AddMessage(string id, JsonArray items, string context = "context")
        {
            using var store = new CoreStore(Directory, Time);
            store.CommitBatch("bot", new JsonArray(new JsonObject
            {
                ["message_id"] = id, ["from_user_id"] = "owner", ["message_type"] = 1,
                ["create_time_ms"] = Time.GetUtcNow().ToUnixTimeMilliseconds(), ["context_token"] = context, ["item_list"] = items
            }), "cursor");
        }
        internal WeixinChannel Open(Func<WeixinOptions, WeixinOptions>? change = null) => new(change?.Invoke(Options) ?? Options);
        public void Dispose() { Http.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<(string Uri, string Body)> Calls { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Json("""{"ret":0,"message_id":"123"}"""));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.ToString(), body));
            return await Respond(request, cancellationToken);
        }
    }
}
