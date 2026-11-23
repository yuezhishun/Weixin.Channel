# 微信渠道库测试计划

真机收发可复用[固定媒体与长文本样本](../../tests/fixtures/live/)。图片、视频、WAV、TXT 与 8034 个 UTF-16 单元的长文本均为合成数据，原件哈希见 [SHA256SUMS](../../tests/fixtures/live/SHA256SUMS)。微信录音另用手机发送。Linux 在样本目录可运行 `sha256sum -c SHA256SUMS`，Windows 可用 `Get-FileHash -Algorithm SHA256` 核对。

环境与构建入口见[项目说明](../../README.md)。自动化使用 `Weixin.Channel.Tests`、可推进时钟、受控微信 HTTP/CDN 和临时 SQLite。真机准备 A／B Android 微信账号、CLI 与渠道 Web，Web 默认 `127.0.0.1:3083`。同账号的完整手机矩阵执行完后，在另一平台复跑扫码、文本／附件、停止与重启。

从仓库根目录运行 Windows `./scripts/build.ps1` 或 Linux `bash scripts/build.sh`。使用 `WEIXIN_DOTNET` 指向锁定 SDK，保留脚本输出的目录内构建日志、TRX 和独立消费者结果。Web 端点与请求头以[渠道 API](../api.md)为准。

## 自动化

| 用例 | 前置与步骤 | 可核验的通过标准 | 入口与证据 |
| --- | --- | --- | --- |
| C-A01 登录状态与协议 | 注入等待、已扫、确认、过期、验证码重输或阻断、IDC 重定向、已绑定及取消。用 11 个以上账号发起扫码，再取消等候中的验证码或轮询。 | 状态回调对应。过期换码，IDC 跳转到预期地址，已绑定保留原凭据。取消后不继续轮询，只携带最近 10 个 token，无凭据泄漏。 | `LoginProtocolTests`，状态与脱敏请求日志。 |
| C-A02 账号、隔离与授权 | 建立 A／B，给相同 peer 制造发送歧义，再用 DeliveryId 绑定原账号，尝试显式错配。排除 A 的 owner，注入命令、图片和引用。模拟 A 的 owner 换绑到新 bot。 | 歧义及错配不发送。未授权不执行命令和不下载媒体。换绑清除 A 的旧游标、上下文和缓存，B 不受影响。 | `CoreTests`、`LifecycleTests`，发送计数、账户与 Inbox。 |
| C-A03 长轮询与错误 | 捕获请求头、版本、BotAgent、RouteTag、启停通知。注入服务端超时调整、连续失败、`-14`，再取消在途轮询。 | 默认 35 秒并应用服务端调整。前两次失败间隔 2 秒，连续第三次起退避 30 秒。仅错误账号暂停 1 小时，取消及时停止。 | `CoreTests`、`LoginProtocolTests`，时钟、请求时间及账号状态。 |
| C-A04 去重、事务及无损 ID | 下发高于 JS 安全整数的 ID 和 `uint64` 最大值。在写入批次时注入 SQLite 失败；恢复后重复投递该批。 | ID 逐字保留，失败不推进游标和上下文且无部分批次。成功后每条一份 Inbox，重投 DeliveryId 不变。 | `CoreTests`，事务断言及 Inbox。 |
| C-A05 FIFO、租约和恢复 | 领取 A 队首后分别领 A 下一条和 B。推进时钟，续租、过期重领、旧租约 Ack、新租约 Ack 与重复 Ack。分别在提交后和业务已执行但未 Ack 时重启。 | A 后序受阻，B 独立。默认租约 60 秒，handler 每 20 秒续租。重领 ID 一致且租约轮换，旧租约不能确认，业务按 DeliveryId 去重。 | `CoreTests`、`LifecycleTests`，租约、重启状态、副作用计数。 |
| C-A06 媒体与 CDN | 注入图、视频、文件、有效／损坏语音及混合 item。覆盖转写有无、引用媒体优先级、URL、两种 key 编码、明文图、错误 MD5、缺头和 4xx／5xx。 | 媒体选择符合协议，转写优先，无转写走 WAV，坏包保留 SILK。解密和字节正确，故障按规则重试或报告，可恢复请求可重领。 | `MessagingTests`，回放、媒体哈希及实际请求次数。 |
| C-A07 引用与缓存 | 测入站／出站引用、仅 ID、内联和局部。变更 MD5、时间、数量和容量，尝试跨对端恢复，最后损坏引用库。 | 内容及附件按预期恢复，局部只在 MD5 匹配时生效，缺失显示占位。淘汰限额和隔离生效，损坏缓存不影响普通收发。 | `MessagingTests`，缓存命中、淘汰及媒体哈希。 |
| C-A08 回复、分块与收据 | 连收两条后回复第一条，发送跨 4000 UTF-16 单元的 emoji 文本、Markdown、caption 和三种来源媒体。注入传输失败、响应丢失及前后 hook 故障，重启查收据。 | 原 context 被使用。分块无断裂，caption 在前。`success`、`failed`、`partial`、`unknown` 可区分，重启可查，取消不发送，通知失败不重复业务，unknown 不自动重发。 | `MessagingTests`、`CoreTests`，每段 client ID 和状态。 |
| C-A09 typing、工具进度与命令 | 捕获 ticket 获取／刷新／复用、保活与结束。中途取消或失败。让同一 run 的 11／12 与正文队列排空，再重放同一条内置命令。 | typing 每 5 秒保活并结束，工具按 run/tool-call 顺序调用。命令副作用一次，debug 重启保留，诊断失败不影响确认。 | `MessagingTests`、`CoreTests`，请求顺序、最终状态。 |
| C-A10 CLI、Web 和 NuGet | 通过 `verify-web` 测首页、HTTP、OpenAPI、登录探针、聊天历史、发送校验、租约与收据。连接断开 SSE，再让 CLI 竞争相同目录。仓库外消费者装 NuGet，打开同目录两次。 | 首页可打开且 API 保持本机边界。历史分页无重漏，按账号和扫码人隔离，Ack 后仍可读。浏览历史和连接 SSE 均不改变 Inbox，错误发送不写入记录，第二进程拒绝占用。外部项目独立运行并恢复状态。 | `scripts/build.ps1`／`build.sh`、Web 输出及消费者记录。 |

定向回归可按 xUnit 方法名过滤。C-A04 对应 `Failed_batch_commit_keeps_cursor_context_and_inbox_unchanged_to_allow_redelivery`，C-A06 对应 `Outbound_sources_keep_original_bytes_and_choose_the_official_media_wire_types`，C-A09 对应 `Progress_is_serial_and_drain_preserves_shared_run_and_tool_identifiers`。C-A10 的 Web 登录探针仅覆盖错误或缺失会话，扫码确认由真机 C-L01 覆盖。

在 `samples/Weixin.Web/editor` 依次运行 `npm ci`、`npx playwright install chromium` 和 `npm run test:web`。浏览器回归使用合成 API 响应和 WAV 样本，覆盖历史轮询时播放不中断、白名单变化时保留草稿和附件，以及缺少消息 ID 时的送达提示。

## 微信真机

| 用例 | 前置与步骤 | 可核验的通过标准 | 当轮证据 |
| --- | --- | --- | --- |
| C-L01 登录与多账号 | A／B 分别创建登录，取 QR 扫码并手机确认。A 再扫，分别取消一次并等待另一次换码。两个会话各发标记，依次领取并回复旧消息。 | 两机实际收到各自对应正文，未串号，扫码状态一致。验证码、IDC 及同 owner 新 bot 换绑独立留证，未出现记未触发。 | 登录状态、账户别名、原 delivery、客户端截图。 |
| C-L02 文本与媒体往返 | 手机发送中文、emoji、Markdown 样本、图、视频、TXT 和录音。以本地路径、`file://` 和 HTTP(S) 媒体回复，长文覆盖分块边界。 | 长文与 emoji 完整，客户端顺序和 caption 正确。每种媒体可打开或播放，以原文件送达的样本校验哈希。转写语音和无转写 SILK 各自记录。 | 手机截图、音频播放结果、逐段收据及哈希。 |
| C-L03 引用与 typing | 引用前轮文本和图片，尝试仅 ID、内联、局部和缺缓存引用。在测试消息处理中每 5 秒发 typing 保活并结束。 | 引用正文、选中片段、原图和缺失占位对应。手机看见 typing 出现、持续、消失。微信未触发的引用类型单独记录。 | 安全截取的原消息 ID、引用内容、见证时序。 |
| C-L04 原生工具进度 | 领取一条新消息，复用 claim 返回的 run ID 和原 delivery。以同一 tool-call ID 发 type 11 开始、type 12 完成、同 run 最终正文，排空进度。 | 手机确实显示原生工具进度并与正文关联。HTTP 200 空体、Web 204 或仅能看到正文不算通过。 | 脱敏响应类型、状态、字节数及手机截图。 |
| C-L05 授权、租约与发送故障 | 将 A 的 owner 临时排出 allowlist，发送命令、附件和引用，恢复配置再试。领取未 Ack 即重启，等租约过期重领。发送 caption 后让附件失败，并在隔离测试链路丢弃正文出站响应。 | 恢复前无下载与副作用，恢复后可接待。旧租约无效、新租约可 Ack。部分成功和 unknown 可查，手机和收据均未出现自动重复发送。不可安全注入的真机分支记阻塞。 | Inbox／收据、outbox 波形或日志、手机可见结果。 |

手机核验以[历史渠道验收记录](../acceptance.md)作为对照。原生工具进度以前未通过，依本轮手机观察单列签收。
