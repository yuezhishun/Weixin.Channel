# Weixin.Channel

独立的 .NET 11 微信 iLink 渠道库，包含一个公共 `WeixinChannel` 服务、CLI、Minimal APIs 与测试项目。源码行为基准为 Tencent `openclaw-weixin` 2.4.10，提交 `874637a0dbc24050f72d1385add4fc6170a2d726`。本仓库自带解决方案、SDK 约束、依赖锁与独立状态目录。

历史真机记录覆盖扫码、多账号、回复、附件和引用。原生工具进度和同一上下文长文续段另有失败记录。详见[验收记录](docs/acceptance.md)、[进度诊断](docs/progress-diagnosis.md)和[部署指南](docs/deployment.md)。

仓库的渠道测试计划位于 `docs/testing/channel.md`。

## 构建与打包

需要 .NET SDK `11.0.100-rc.3.26522.148`。Windows x64 与 Linux x64 分别执行，脚本拒绝错误 SDK、测试失败或跳过测试。第一次修改依赖后由维护者执行普通 `dotnet restore Weixin.Channel.slnx` 更新四个 `packages.lock.json`；正常交付只使用 locked restore。

```powershell
# 在本目录；可通过 -Dotnet 指定独立安装的 SDK
./scripts/build.ps1 -Dotnet C:/dotnet/dotnet.exe
```

```bash
WEIXIN_DOTNET=/path/to/dotnet bash scripts/build.sh
```

脚本按锁定还原、Release 构建、测试、自包含发布、发布程序验收、NuGet 打包和独立消费者验证的顺序执行。NuGet 包位于 `artifacts/packages/<RID>/Weixin.Channel.1.0.0-rc.2.nupkg`，平台归档为 `artifacts/Weixin.Channel-<RID>-<时间戳>.zip` 或 `.tar.gz`。日志、TRX 和归档 SHA-256 位于 `artifacts/verification/`。独立消费者通过 `PackageReference` 和隔离包缓存验证 SQLite 状态创建与重开。

## 运行自包含发布包

解压对应平台的归档。Windows 运行 `.\Weixin.Cli\Weixin.Cli.exe status --state .state` 或 `.\Weixin.Web\Weixin.Web.exe --state .state`。Linux 使用 `./Weixin.Cli/Weixin.Cli` 和 `./Weixin.Web/Weixin.Web`。需保留应用目录内的运行时和本机库文件。Web 默认监听 `127.0.0.1:3083`。同一状态目录同一时间只供一个进程使用。

## CLI

以下命令的 `dotnet` 应指向锁定的 SDK。CLI 与 Web 默认均使用系统 LocalApplicationData 下的 `Weixin.Channel`；通过 `--state` 指定自己的目录。请重新扫码，不复制旧 OpenClaw 数据。目录含登录凭据和消息，只交给受信任的当前用户，勿同步到公开仓库。

```powershell
dotnet run --project samples/Weixin.Cli -- login --state .state
dotnet run --project samples/Weixin.Cli -- accounts --state .state
dotnet run --project samples/Weixin.Cli -- allowlist <account> <peer> --state .state
dotnet run --project samples/Weixin.Cli -- listen --state .state
```

`login` 显示 ASCII 二维码及验证码提示，`listen` 显示入站消息，只有加上 `--echo` 才自动原样回复。`listen` 占用状态目录时，在同一控制台直接输入以下命令，不要另起 CLI 进程：

```text
send <peer> "你好" --account <account>
send <peer> "图片说明" --media "D:\images\example.png" --account <account>
reply <deliveryId> "针对该消息的回复"
typing <peer> 10 --account <account>
progress <peer> search --account <account>
inbox
receipts <deliveryId>
status
quit
```

单次命令还支持 `enable`、`disable`、`delete`、`retry`。`retry <deliveryId> --allow-duplicate` 是人工确认 unknown 发送可能重复的显式开关；应先检查微信聊天记录。`allowlist <account>` 清空自定义名单，owner 仍获得默认授权。

## 接入 C# 宿主

```csharp
await using var channel = new WeixinChannel(new WeixinOptions
{
    StateDirectory = Path.Combine(AppContext.BaseDirectory, "weixin-state"),
    BotAgent = "MyAssistant/1.0.0",
    Log = Console.WriteLine
});

// 每个账号只选择一个消费模式。C# handler 模式由库维护租约，成功返回才 Ack。
await channel.RunAsync(accountId, async (message, cancellationToken) =>
{
    // 使用 DeliveryId 对业务副作用去重；AccountId + PeerId 是会话隔离键。
    // Agent 路由、模型执行和聊天历史由你的宿主提供。
    var sent = await message.ReplyAsync("已收到：" + message.Text, cancellationToken: cancellationToken);
    if (sent.Status != "success") throw new IOException("回复未成功，保留投递等待重试或人工核查。");
}, cancellationToken);
```

`LoginAsync` 接收 `LoginCallbacks`，库不会读取 Console；`Updated` 报告二维码及完整状态，`RequestVerificationCode` 异步取得验证码。账号配置、授权和 debug 设置保存在 SQLite。账号缺省时，单账号直接选用，多账号按对端 context 唯一匹配，歧义报错。

`ReplyAsync(deliveryId, ...)` 和消息绑定回复使用该条入站接收时的 context token。`SendAsync` 可带 `DeliveryId`，每段发送保留 client ID 和结果。`BeforeSend` 可修改或取消请求；`AfterSend` 用于通知，hook 异常不改变已经成功的业务结果。

发送响应丢失或重启后，用 `GetSendSegments(deliveryId)`、CLI `receipts <deliveryId>` 或 Web `GET /api/inbox/{deliveryId}/receipts` 查询持久收据；只公开分段标识、结果和脱敏错误。

`InboundMessage.RunId` 等于 `DeliveryId`。在 handler 内优先调用 `message.ProgressAsync(toolCallId, toolName)` 与 `message.ProgressAsync(toolCallId, toolName, "completed")`，它们自动绑定账号、对端、回复 context 和同一 run。独立发送工具进度时自行保持 `RunId`/`ToolCallId` 一致，并在结束时 `await channel.DrainProgressAsync(runId)`。`WithTypingAsync` 会每5秒保活并在 finally 取消；handler 模式已自动启用该生命周期。

手动消费宿主通过 `ReportTimingAsync(deliveryId, operation, elapsed)` 或 Web `/api/messages/timing` 报告处理耗时；这是最佳努力诊断通知，失败不改变业务 Ack 结果。

入站附件路径属于渠道管理的临时资源。handler 成功结束或手动 Ack 后可被清理，需要长期保存时，应在 Ack 前复制到宿主自己的存储。引用缓存采用独立数据库和独立媒体副本，受下述保留策略约束；不要把引用缓存路径当作永久文件地址。

| WeixinOptions 配置 | 默认值 | 用途 |
| --- | --- | --- |
| TextChunkSize | 4000 | 每段UTF-16单元数，不拆surrogate pair |
| EnableProgress | true | 是否发送工具进度 |
| EnableBuiltinCommands | true | 是否在渠道内处理/echo与/toggle-debug；客服宿主关闭以保留业务记录 |
| EnableQuoteCache | true | 是否持久缓存引用 |
| QuoteTextRetention / QuoteTextLimit | 30天 / 10000条 | 引用正文保留上限 |
| QuoteMediaRetention / QuoteMediaBytesLimit | 7天 / 256 MiB | 引用媒体总保留上限 |
| QuoteMediaFileBytesLimit | 25 MiB | 单文件引用缓存上限 |
| LeaseDuration / LeaseRenewInterval | 60秒 / 20秒 | 默认租约与C#自动续租间隔 |
| MediaTimeout | 60秒 | 媒体请求超时 |

账号状态还提供 `LastActivityAt`、`LastInboundAt` 和脱敏 `LastError`，便于宿主区分已停止、服务器暂停和最近错误。参数类型、默认值与完整签名以 `Contracts.cs` 和 OpenAPI 为准。

仅入站保证至少一次：业务成功而 Ack 前崩溃，消息会再次交付同一 `DeliveryId`。不承诺端到端 exactly-once。unknown 发送会保存待核查状态，不应自动重发。caption 成功而媒体失败返回部分结果，调用方必须检查 `SendResult.Status` 与每个分段。

## Web API

```powershell
dotnet run --project samples/Weixin.Web -- --state .state
curl.exe -H "x-weixin-request: 1" http://127.0.0.1:3083/health
curl.exe -X POST -H "x-weixin-request: 1" http://127.0.0.1:3083/api/logins
```

默认绑定 `127.0.0.1:3083`，所有请求（含 health、OpenAPI、QR、SSE）均要求 `x-weixin-request: 1`、loopback 来源、正确 Host 和同源或缺省 Origin，不开放 CORS。不能直接在普通浏览器地址栏打开受保护的 QR 路由，请使用带 header 的 `fetch` 或 curl 保存图片。OpenAPI 位于 `/openapi/v1.json`。

Web 只启用手动 inbox 消费。先启动账号，再 claim；成功执行业务才 ack，20 秒左右主动 renew，失败 retry。SSE 只通知，首次连接给出 pending 摘要；断线后仍从 inbox 领取可靠消息，不能以 SSE 事件作为已经处理的凭据。完整路径、请求体、调用例子见 [接口说明](docs/api.md)。

支持范围、官方源码映射见 [功能映射](docs/feature-map.md)。第三方源码与依赖许可见 [第三方说明](third-party/NOTICE.md)。群聊、撤回、原生语音出站和逐 token 流式输出不在范围内；音频附件以文件出站。

SILK 的官方 SDK/wasm/纯 C# 对照证据单独记录于 [SILK 验证](docs/silk-validation.md)。
