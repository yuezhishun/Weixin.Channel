# 验收记录

日期：2026-11-07。规格：`../SPEC.md`。官方基准：2.4.10 / `874637a0dbc24050f72d1385add4fc6170a2d726`。

## 自动化与环境

| 项目 | 当前状态 | 证据位置/说明 |
| --- | --- | --- |
| Windows SDK | 环境已确认 | SDK 11.0.100-rc.3.26522.148，win-x64 |
| Linux SDK | 环境已确认 | WSL superagent-test，Ubuntu24.04，SDK同版本，linux-x64 |
| Windows locked restore/build/test/pack | 通过，144/144，无跳过，0警告/0错误 | `artifacts/verification/windows-20261107-041620`，含build.log、weixin.trx和result.txt |
| Linux locked restore/build/test/pack | 通过，144/144，无跳过，0警告/0错误 | `artifacts/verification/linux-20261107-041705`；在WSL Ubuntu真实运行，非仅跨RID编译 |
| 独立 nupkg 消费 | 两平台通过 | 两平台脚本在仓库外创建临时项目，隔离NuGet.Config和包缓存，仅PackageReference；实际打开、关闭和重开SQLite |
| Web安全/路由/健康/OpenAPI/SSE | Windows/Linux通过 | `artifacts/verification/web-20261107-041626`、`web-linux-20261107-041736`；header/Host/Origin、ProblemDetails、响应schema、非空SSE、claim/lease、收据脱敏及进程目录锁 |
| 微信封装SILK golden fixture | 两平台通过 | silk-wasm生成的0x02微信容器；四种采样率与C SDK参考PCM比较，含坏包回退；合成样本不替代真机语音 |

SILK 专项对比另见 [SILK 验证](silk-validation.md)，与真实微信语音验收分开记录。

初次 restore 观察到旧 SQLitePCLRaw 与 Microsoft.OpenApi 高危告警；没有禁用 NuGet 审计或忽略告警，已更新依赖并通过最终locked restore。SDK的NETSDK1057预览版提示与用户固定RC SDK一致，不是被忽略的构建警告。Unix权限断言在Linux实跑；Windows不适用该权限模型。

集成检查点：Windows四项目Release构建已运行且为0警告/0错误。`windows-20261107-034316` 全量测试为70例、9失败、61通过、0跳过，因此当轮脚本明确停止，未执行pack；主要发现活动SQLite事务内查询未绑定事务，以及原始JSON转义的断言问题。修复后必须重跑并以更新的完整TRX替换验收结论，不把这轮记录当作通过证据。

后续保留的失败记录：稳定包1.0.0依赖SQLite RC触发NU5104，改为`1.0.0-rc.1`，未抑制警告；独立消费者初次来源参数错误已改为隔离NuGet.Config。审查修复轮142例中1例发现typing取消后重新获取配置导致停止阻塞，已改为停止只使用已有ticket；最终144例包含该回归。JSON断言从搜索原始转义串改为解析字段，预期中文内容未改变；耗时context断言移到异常捕获范围之外，避免假通过。

三层独立审查及后续复核的逐项结论见`../SPEC.md`的Review Triage Log。已处理unknown/5xx、引用副本、关闭排空、账号生命周期互斥、跨账号typing、重试进度、命令顺序、异常协议字段、非空SSE、长任务续租和收据查询等问题，无已知审查修复项延期。最后一次源码验证使用上述144例记录；此后只补本文验收记录并重新打包文档。

## 故障矩阵

核心测试项目覆盖事务回滚、提交后崩溃、未Ack重启恢复、重复轮询/uint64、旧lease拒绝、重复Ack、引用坏库、媒体失败/raw回退、部分发送、unknown保护、重复命令、状态目录互斥。生命周期测试验证跨60秒续租、关闭期间在途操作和锁的保留、取消claim后立即恢复、取消回调异常以及账号启停删除竞争；HTTP工具在合成非空inbox上验证SSE断开、原DeliveryId领取、租约校验和持久收据。以上已在两平台实际运行。

## 真机验收（部分通过，工具进度未通过）

用户于2026-11-07要求开始真机验收。在Windows运行Web样例，使用新的`.state/live-20261107`目录，不迁移既有OpenClaw数据；两个不同owner真实登录confirmed。测试接收方均为用户控制的扫码owner，媒体使用自行生成的样本及用户明确录制的验收语音。以下逐项以实际观察记录，不能以既有自动化结果代替。源码未因本轮验收改变，记录及本地证据位于`docs`与ignored的`artifacts/live`；真实账号、二维码和录音不随NuGet发布。

| 验收项 | 操作与成功标准 | 状态 |
| --- | --- | --- |
| 新二维码与验证码 | 独立目录扫码、需要时提交手机验证码、确认owner授权 | 两个不同owner扫码confirmed及默认owner授权通过；A再次扫码返回binded_redirect/AlreadyConnected，用户直接进入既有会话；验证码未触发 |
| 取消/过期/IDC redirect | 取消立即结束请求，二维码刷新可继续；跨IDC可完成 | 已观察自动刷新至第2张，旧会话取消、新会话登录成功；IDC未触发 |
| 多账号与同owner换绑 | 账号隔离，收件人歧义拒绝，换绑无旧游标/context串用 | 两个不同owner扫码并同时运行通过；A队首租用时A后续claim=204、B仍可完成；A释放后严格FIFO。用户确认未串号，回复context/账号/peer与各自入站一致；按peer自动选账号通过，无匹配账号返回409，错配账号与DeliveryId返回400。同peer多候选歧义未触发；再次扫码仅返回已绑定，未生成新bot ID，实际同owner换绑清理未触发 |
| 文本/长文本/Markdown | 对端实际收到正确分块、emoji未拆分、caption在媒体前 | 中文及😀🚀入站无损、绑定回复通过；8034个UTF-16单元发送3段，用户确认emoji完整；Markdown截图确认标题、粗体、链接、代码块正常渲染，中文斜体视觉效果未单独确定 |
| 图片/视频/文件/音频出站 | 本地、file://、HTTP(S)来源均可发送；音频按文件 | 本地PNG/TXT/WAV、file:// MP4、HTTP TXT及HTTPS公开许可证均发送成功，用户确认可用且说明先于附件 |
| 图片/视频/文件/语音入站 | 授权后下载；转写优先；真实微信SILK可转24kHz WAV | PNG、MP4、TXT往返SHA-256与原样本一致，视频完整解码通过；手机语音转写优先通过。另以现有Media方法下载同条真实语音并解码为8.66秒/24kHz/mono/PCM16，Windows与Linux输出哈希一致，用户确认播放正常；此独立验证不冒充无转写入站路径已触发 |
| 内联/ID-only/局部引用 | 正文、MD5、媒体恢复和缺失占位符合微信表现 | ID-only出站文本和图片引用通过：ID位于message_item.msg_id，无内联正文/媒体，从缓存恢复正文、emoji及原图；引用图片SHA-256与原样本一致。内联、局部引用未触发 |
| typing/工具进度 | typing持续/结束清理，type11/12共享run与toolCall关联 | typing保活与结束通过，用户确认可见且已消失。工具进度未通过：A的4条type11/12为unknown；新run单次type11诊断得到HTTP200、application/octet-stream、零字节响应，官方JSON.parse同样无法解析。初次演示结束正文RunId不一致；排除该变量后，B使用标准工具名search、开始/完成/最终正文全程同一RunId，结果仍为unknown/unknown/success，用户确认无进度。未重发旧unknown，未把Web204或上游200空体视为送达 |
| debug/命令授权 | 未授权不执行/下载；重复投递不重复翻转debug | owner的/echo和/toggle-debug回执已由用户截图确认；debug开启后跨重启保留。显式allowlist排除A owner时，A的命令及图片引用均denied/unauthorized，command_result为空、无发送记录或delivery媒体目录，debug未改变；恢复默认owner授权后echo再次发送成功，最终两账号debug均关闭。未授权直接媒体附件及真实重复命令投递未触发 |
| 重启与租约 | 未Ack同DeliveryId恢复；旧lease不能确认新领取 | 真实未Ack消息经进程重启保留；账号与配置恢复，原租约未到期claim=204，到期后同DeliveryId、新lease；旧lease Ack=409，新lease及重复Ack=204；回复发送成功 |
| SSE与进程锁 | SSE断线不消费，CLI/Web拒绝同时占用同一目录 | 含3条真实待处理消息时连接SSE收到摘要，断开后全部状态/attempts/租约不变；Web运行时CLI明确报目录被占用并退出1 |
| 部分失败/unknown人工核查 | 部分成功可见，unknown不自动重发，重试风险提示 | 真实文本发送成功后以不存在的测试附件触发partial；两个分段及client ID成功落盘并由receipts返回。工具进度unknown保留未重发；普通正文发送结果未知的故障注入尚未执行 |

本轮结束前待处理inbox为0，两账号均已恢复默认owner授权、debug关闭。停止测试进程后保留独立状态目录，便于后续恢复登录继续验收。

未完成项包括：工具进度的服务端响应与客户端显示问题；验证码、跨IDC、实际同owner新bot换绑清理；同peer多候选歧义、内联/局部引用、无转写的完整入站回退、未授权直接媒体附件、真实重复命令以及普通正文发送结果未知的故障注入。已有自动化覆盖仍有效，但不能替代这些真实分支。当前结论为部分通过，不得宣称“完整真机验收通过”。

## 原生工具进度追加排查（仍未通过）

2026-11-07再次联网确认官方main仍为上述提交，npm latest仍为2.4.10；校验发布包integrity后，相关源码仅有换行符差异。直接执行官方发布包的进度发送器、发送函数和HTTP API，使用真实onItemEvent、tool:call形式ID、同run/context及官方正文后finalize顺序，11/12仍HTTP200空体，正文返回message_id。用户在Android微信8.0.79上确认只看到正文。

另将开始消息message_state单独改为协议定义的1（生成中）进行诊断，并按用户要求以新run/client ID重跑；两轮11/12仍为空，最终用户确认原生进度仍不可见。该假设未成立，未将此变更应用到库，也未增加文本降级。新测试入站已Ack=204，未知进度回执未自动重发。生产代码及NuGet未变；既有144例回归结果不是本轮真机通过的证据。完整脱敏证据与待上游确认的问题见[进度诊断](progress-diagnosis.md)。

## 2026-11-19 Android 8.0.80 复测

- 同一 run 的原生工具进度 type 11 和 type 12 均返回 HTTP 200 和 0 字节。最终正文返回 HTTP 200 和 34 字节，带成功收据。Android 微信可见「正在输入」和最终正文，原生进度未显示。公开问题见 [Tencent/openclaw-weixin #392](https://github.com/Tencent/openclaw-weixin/issues/392)。
- 在一个回复上下文中，前 10 段发送成功，随后长文第三段收到业务错误 `-2`，对该段单独补发仍失败。换用新入站上下文后 Markdown 正常显示。现有证据不足以确认服务端拒绝原因或固定限额。
