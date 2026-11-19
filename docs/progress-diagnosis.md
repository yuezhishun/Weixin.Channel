# 工具进度空响应调查

日期：2026-11-07。官方基准：2.4.10，提交`874637a0dbc24050f72d1385add4fc6170a2d726`。

## 已确认的边界

正常文本、typing与原生工具进度使用同一已登录账号和新入站上下文。直接执行官方`src/api/api.ts`的`sendMessage`，也能复现原生工具进度的空响应。因此这不是.NET JSON解析器制造的空响应，也不能通过把HTTP 200改判成功修复客户端显示。

| 请求 | HTTP | Content-Type | Content-Length | 解析/结果 |
| --- | --- | --- | --- | --- |
| 官方type 11开始 | 200 | application/octet-stream | 0 | 官方JSON解析抛出SyntaxError |
| 官方type 12结束 | 200 | application/octet-stream | 0 | 官方JSON解析抛出SyntaxError |
| 同一run最终文本 | 200 | application/octet-stream | 34 | 对象JSON，返回无损message_id |
| 独立普通文本对照 | 200 | application/octet-stream | 34 | 对象JSON，返回无损message_id |

以上响应无重定向，host为`ilinkai.weixin.qq.com`。工具开始、结束和最终正文共用标准UUID RunId及同一context，工具开始/结束共用tool_call_id；请求使用官方ClientId格式、默认OpenClaw BotAgent及无charset的`application/json`请求头。由此排除了此前.NET调用的RunId格式、ClientId格式、BotAgent和charset差异作为该现象的独有原因。响应的Content-Type也不能作为失败依据，因为成功文本使用相同类型。

服务端没有返回ret、errcode或errmsg。当前证据只能定位到原生type 11/12响应路径，不能进一步断言是未开放能力、灰度条件、客户端限制或服务端缺陷；这些需要官方服务端信息才能确认。用户已提供客户端版本Android微信8.0.79，并确认官方完整对照流程仍只显示最终文本，没有原生工具进度。

## 参照执行方式

官方API源文件逐字节复制，SHA-256为`2241d6c8ecb7f648a706b3de4c92ab7b5be6299bd29d25439140c04ddfcab54f`，未修改请求、鉴权或响应解析代码。独立诊断入口构造与官方发送器一致的item和msg，并依次调用开始、结束、正文。仅配置读取和日志模块使用隔离stub：不读取OpenClaw数据，不把凭据写入日志；官方package.json提供应用ID和版本。诊断专用Node不成为渠道库或NuGet的运行依赖。

原始账号数据仅在本机只读读取，通过子进程标准输入传递；诊断证据只保留HTTP状态、响应长度、内容类型、正文哈希和异常类型。脚本及本地证据位于ignored的`artifacts/live/progress-investigation`，不随包发布。

## 最新代码与发布包复核

2026-11-07重新通过`git ls-remote`与GitHub提交API交叉确认：官方`main`最新提交仍为`874637a0dbc24050f72d1385add4fc6170a2d726`，提交时间为2026-10-13 03:31:48 UTC。npm的latest仍为2.4.10。下载的官方发布包已通过registry公布的SHA-512 integrity校验；对比API、类型定义、send、process-message、reply-progress-sender、random及package.json，统一CRLF/LF后全部相同。

协议未变：开始消息使用`type=11`、`is_completed=false`和`tool_call_start_item`；结束使用`type=12`、`is_completed=true`和`tool_call_result_item`。两者共有`create_time_ms`，工具名称及同一`tool_call_id`；外层都是`message_type=2`、`message_state=2`、单元素`item_list`、绑定入站的`context_token`及同一`run_id`。源码没有附加session_id、msg_id或独立run开始/结束请求。

需要区分的是调用顺序：官方sender只串行化11/12；正文的deliver不等待该队列，process-message在dispatcher结束后的finally才调用finalize。finalize只停止后续入队并等待队列，不发送额外HTTP请求。因此11→12→正文与11→正文→12均可能出现，不能把“结束必须先于正文”当成官方硬性协议要求。

本轮直接执行npm包中未修改的`dist/src/messaging/reply-progress-sender.js → send.js → api.js`，通过真实onItemEvent驱动，使用`tool:call-…`事件ID、工具名read、标准UUID run、官方生成的client ID。隔离loader仅替换宿主配置读取、日志、引用缓存及此流程不调用的媒体标签模块。先调用官方getConfig，开始后等待20秒，再派发结束事件和最终正文，最后finalize。请求在fetch前记录，响应到达顺序不冒充发送顺序。

| 真机实验（北京时间） | 唯一有意协议变更 | 11 / 12响应 | 最终正文 | 用户观察 |
| --- | --- | --- | --- | --- |
| 05:52:44–05:53:04，官方发布包 | 无 | 均200、Content-Length:0 | 200、34字节、有message_id | Android 8.0.79仅显示最终正文 |
| 05:54:29–05:54:49，状态假设 | 仅开始消息message_state从2改为1 | 均200、Content-Length:0 | 200、34字节、有message_id | 用户未看到，要求重跑 |
| 05:55:44–05:56:04，按用户要求重跑 | 同上一轮，使用全新run/client ID | 均200、Content-Length:0 | 200、34字节、有message_id | 用户确认仍无原生进度，正文正常 |

三轮getConfig均返回`ret=0`和非空typing_ticket，响应没有其他能力字段。三轮实际请求发起顺序为11→正文→12，进度内部仍为11→12；没有重放旧unknown请求。状态假设已被实测否定，不应用到生产代码。用于本轮测试的新入站消息已Ack=204。

离线校验验证了官方包的请求包装、上下文/run关联、工具ID关联、状态和值；单变量状态对照也通过了对应的离线请求检查。这些检查仅证明诊断脚本按预期构造请求，不代表原生功能通过。初次诊断启动器因Node loader的Windows绝对路径格式退出，未进入发送；修正为file URL并通过register加载后才执行真机流程。生产代码未修改，因此没有重跑或更新既有144例源码回归结果，也未重新打包NuGet。

本机证据：`latest-verification.json`、`npm-latest/provenance.json`、`npm-latest/source-parity.json`、`package-probe-summary.json`及各轮`package-probe-live-*.json`，均位于上述ignored诊断目录。

## 可提交给上游的复现要点（尚未提交）

建议标题：`[Bug] 官方2.4.10原生工具进度type 11/12返回HTTP 200空体，同run正文正常（Android 8.0.79）`。

1. 使用已登录账号，从一条新入站消息取得context_token；保持Android微信8.0.79会话窗口打开。正常文字和typing均已单独验证。
2. 调用官方getConfig确认ret=0且取得typing_ticket。凭据、ticket和context_token不应放入公开Issue。
3. 实例化官方WeixinReplyProgressSender；传入新UUID runId、收件人、账号及上述上下文。派发`{kind:"tool", phase:"start", name:"read", itemId:"tool:call-<新ID>", status:"running"}`。
4. 等待20秒，派发同itemId的`phase:"end", status:"completed"`；使用官方sendMessageWeixin发送同runId的最终正文，finally调用sender.finalize。
5. 预期原生工具开始/结束状态可见；实际只有最终正文可见。11/12均200空体，官方JSON解析抛出SyntaxError并被进度队列捕获；正文200且返回message_id。无需.NET代码即可复现。

需要维护者确认：此appid下11/12是否已开放、是否存在已公开的客户端/账号能力前置条件、空响应是否为预期契约，以及原生进度与正文是否还有源码未表达的关联要求。现有证据不足以在客户端实现可靠修复；不得将空响应改判成功或用普通文本代替原生进度来关闭本问题。

## 修复约束

- 原生空响应必须继续保留unknown，不自动重发，不伪造message_id或成功回执。
- 进度为最佳努力，失败不能让已经成功的业务回复重跑。
- 用户明确只接受原生进度，不采用文本兼容模式，也不把原生unknown追加为另一条文本发送。
- 对修复后的客户端可见效果重新验收，普通文本兼容通过不等同于原生工具卡片通过。

## GitHub Issue核查

2026-11-07查询官方仓库`Tencent/openclaw-weixin`的公开Issue和PR，分别使用`进度`、`JSON`两组关键词，返回27项和79项（含重叠）。OpenCLI当前没有GitHub适配器，因此使用GitHub公开API检索并核对原始页面、相关评论和PR代码。以下状态为本次查询时的状态。

| 条目 | 状态 | 与本次故障的关系 |
| --- | --- | --- |
| [Issue #182：多步任务中间回复无法送达](https://github.com/Tencent/openclaw-weixin/issues/182) | Open | 报告中间状态不可见、最终回复可见，现象相近；未提供type 11/12请求或零字节响应，作者的BatchSession根因分析明确为推测。 |
| [PR #184：汇总工具结果避免限流](https://github.com/Tencent/openclaw-weixin/pull/184) | Open，未合并 | #182评论关联的社区方案。代码缓冲工具结果，最终发送合并后的普通文本，不是原生工具进度修复，不符合本项目当前要求。限流原因不能据此套用于本次故障。 |
| [Issue #310：ret:0但无message_id且正文不可见](https://github.com/Tencent/openclaw-weixin/issues/310) | Open | 报告typing正常而普通文本不可见，评论包含2.4.10复现；响应是非空JSON。本次普通文本返回message_id且用户可见，仅type 11/12为空，不能视为同一已确认缺陷。 |

在上述检索结果及所核查评论中，未找到精确覆盖“原生type 11/12 HTTP 200、Content-Length:0、同run正文正常”的已确认根因或已合并修复。检索覆盖范围有限，不代表仓库不存在其他相关报告。本轮未修改生产代码，也未提交Issue或评论；原生进度修复与真机验收仍未完成。

源码参照：[官方API](https://github.com/Tencent/openclaw-weixin/blob/874637a0dbc24050f72d1385add4fc6170a2d726/src/api/api.ts)、[官方进度发送器](https://github.com/Tencent/openclaw-weixin/blob/874637a0dbc24050f72d1385add4fc6170a2d726/src/messaging/reply-progress-sender.ts)。
