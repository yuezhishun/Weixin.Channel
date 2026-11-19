using System.Text.Json.Serialization;

namespace Weixin.Channel;

public sealed record WeixinOptions
{
    public required string StateDirectory { get; init; }
    public Uri ApiBaseUrl { get; init; } = new("https://ilinkai.weixin.qq.com/");
    public Uri CdnBaseUrl { get; init; } = new("https://novac2c.cdn.weixin.qq.com/c2c/");
    public string? BotAgent { get; init; }
    public string? RouteTag { get; init; }
    public HttpClient? HttpClient { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public int TextChunkSize { get; init; } = 4000;
    public bool EnableProgress { get; init; } = true;
    public bool EnableBuiltinCommands { get; init; } = true;
    public bool EnableQuoteCache { get; init; } = true;
    public TimeSpan QuoteTextRetention { get; init; } = TimeSpan.FromDays(30);
    public int QuoteTextLimit { get; init; } = 10000;
    public TimeSpan QuoteMediaRetention { get; init; } = TimeSpan.FromDays(7);
    public long QuoteMediaBytesLimit { get; init; } = 256L * 1024 * 1024;
    public long QuoteMediaFileBytesLimit { get; init; } = 25L * 1024 * 1024;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MediaTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public Action<string>? Log { get; init; }
    public Func<SendRequest, CancellationToken, ValueTask<SendRequest?>>? BeforeSend { get; init; }
    public Func<SendResult, CancellationToken, ValueTask>? AfterSend { get; init; }
}

public sealed record WeixinAccount(string AccountId, string OwnerId, string BaseUrl, bool Enabled,
    bool Debug, IReadOnlyList<string> Allowlist, DateTimeOffset? PausedUntil, bool Running = false,
    DateTimeOffset? LastActivityAt = null, DateTimeOffset? LastInboundAt = null, string? LastError = null);

public sealed record AccountConfiguration(bool? Enabled = null, IReadOnlyList<string>? Allowlist = null, bool? Debug = null);

public sealed record LoginUpdate(string Status, string? QrContent = null, int RefreshCount = 1);

public sealed record LoginCallbacks
{
    public required Func<LoginUpdate, CancellationToken, ValueTask> Updated { get; init; }
    public required Func<bool, CancellationToken, ValueTask<string>> RequestVerificationCode { get; init; }
}

public sealed record LoginResult(WeixinAccount? Account, bool AlreadyConnected);

public sealed record WeixinAttachment(string Kind, string Path, string MimeType, string? Name = null, bool Referenced = false);

public sealed record WeixinQuote(string? MessageId, string Text, bool Missing, WeixinAttachment? Attachment = null);

public sealed record InboundMessage
{
    public required string DeliveryId { get; init; }
    public required string AccountId { get; init; }
    public required string PeerId { get; init; }
    public required string MessageId { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<WeixinAttachment> Attachments { get; init; } = [];
    public WeixinQuote? Quote { get; init; }
    public required string LeaseId { get; init; }
    public required DateTimeOffset LeaseUntil { get; init; }
    public string RunId => DeliveryId;
    [JsonIgnore] internal WeixinChannel? Channel { get; init; }
    public Task<SendResult> ReplyAsync(string? text, string? media = null, CancellationToken cancellationToken = default) =>
        (Channel ?? throw new InvalidOperationException("消息未绑定渠道。")).ReplyAsync(DeliveryId, text, media, cancellationToken);
    public Task ProgressAsync(string toolCallId, string toolName, string? status = null, CancellationToken cancellationToken = default) =>
        (Channel ?? throw new InvalidOperationException("消息未绑定渠道。")).ProgressAsync(new ToolProgress(PeerId, RunId, toolCallId, toolName, status, AccountId, DeliveryId), cancellationToken);
}

public sealed record InboxEntry(string DeliveryId, string AccountId, string PeerId, string MessageId,
    string State, int Attempts, DateTimeOffset AvailableAt, DateTimeOffset? LeaseUntil, string? LastError);

public sealed record SendRequest
{
    public string? AccountId { get; init; }
    public string PeerId { get; init; } = "";
    public string? Text { get; init; }
    public string? Media { get; init; }
    public string? DeliveryId { get; init; }
    public string? RunId { get; init; }
    public bool AllowUnknownRetry { get; init; }
}

public sealed record SendSegment(string ClientId, string? MessageId, string Status, string? Error = null);
public sealed record SendResult(string AccountId, string PeerId, string Status, IReadOnlyList<SendSegment> Segments);
public sealed record ToolProgress(string PeerId, string RunId, string ToolCallId, string ToolName,
    string? Status = null, string? AccountId = null, string? DeliveryId = null);
public sealed record WeixinEvent(string Kind, string? AccountId, string? DeliveryId, string? Detail, DateTimeOffset At);
