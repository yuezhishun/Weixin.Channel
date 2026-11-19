# 官方功能映射

基准：Tencent openclaw-weixin 2.4.10，提交 `874637a0dbc24050f72d1385add4fc6170a2d726`。以下以源码为协议依据；本库增加 SQLite inbox/lease 持久消费语义，Agent 引擎由宿主提供。

| 官方源码 | .NET 文件/入口 | 移植内容 |
| --- | --- | --- |
| `src/api/api.ts`、`types.ts` | `ProtocolClient.cs` | 九接口、鉴权、版本、BotAgent、RouteTag、无损JSON标识 |
| `src/auth/login-qr.ts` | `WeixinChannel.Login.cs` 的 LoginAsync | 全部二维码状态、验证码、IDC redirect、刷新/TTL/取消、最近token |
| `src/auth/accounts.ts` | `CoreStore.cs`、账号公共操作 | 多账号、规范化、持久授权、同owner换绑清理 |
| `src/monitor/monitor.ts` | `WeixinChannel.cs` 的 RunAsync | long-poll、服务端timeout、退避、暂停、通知、取消 |
| `src/cdn/*` | `Media.cs` | AES-128-ECB/PKCS#7、MD5、key格式、完整URL、CDN重试 |
| `src/media/*` | `Media.cs` | 媒体优先级、转写、SILK纯C#解码、WAV、失败raw回退 |
| `src/messaging/inbound.ts`、`partial-quote.ts` | `WeixinChannel.Messaging.cs`、`QuoteStore.cs` | 正文、附件、引用、局部引用MD5及缺失占位 |
| `src/messaging/quote-store.ts` | `QuoteStore.cs` | 独立SQLite引用缓存、TTL、数量/容量限制、坏库隔离 |
| `src/messaging/send.ts`、`send-media.ts`、`markdown-filter.ts` | `WeixinChannel.Messaging.cs` | Markdown过滤、文本分块、caption先发、媒体类型、分段结果 |
| `src/api/config-cache.ts`、`messaging/typing.ts` | `WeixinChannel.Messaging.cs` | typing ticket缓存与刷新、保活/取消 |
| `src/messaging/reply-progress-sender.ts` | `WeixinChannel.Messaging.cs` | type11/12、runId/toolCallId、串行进度 |
| `src/messaging/authorization.ts`、`slash-commands.ts`、`debug-mode.ts` | `WeixinChannel.cs` | owner/allowlist先授权、内置命令、持久debug、handler交接 |
| `src/messaging/outbound-hooks.ts`、`util/redact.ts` | `WeixinChannel.Messaging.cs`、核心日志 | before-send修改/取消、发送后通知、日志脱敏 |
| 本库增加 | `CoreStore.cs`、Claim/Ack/Retry/Renew | 接收事务、原始inbox、FIFO、租约、崩溃恢复、发送unknown保护 |
| 本库宿主 | `samples/Weixin.Cli`、`samples/Weixin.Web` | CLI同进程收发、Web手动inbox、SSE通知、本机安全边界 |

不移植 OpenClaw 的 Agent 路由、模型、工具引擎、会话历史、插件配置框架；这些由接入方提供。不提供群聊、撤回、原生语音出站、逐 token 流式回复与管理前端。

功能表是实现定位，不替代验证。逐项实际验收状态见 [验收记录](acceptance.md)。
