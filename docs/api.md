# 接口说明

## 本机边界与错误

默认地址 `http://127.0.0.1:3083`。浏览器可直接请求验证页 `/` 和静态资源 `/app.js`、`/app.css`。API 请求（含 health、OpenAPI、QR 和 SSE）必须带 `x-weixin-request: 1`，JSON 请求另带 `Content-Type: application/json`。服务检查每个请求的客户端 IP、Host、端口和 Origin，不信任代理转发 header，也不开放跨域。除登录二维码内容外，响应不包含 bot token、验证码、AES key 或 context token。错误采用 `application/problem+json`，不返回协议原始响应、堆栈和密钥。

常见状态：400 参数错误；403 本机请求边界检查失败；404 资源不存在；409 账号/登录/租约/unknown 发送状态冲突；429 同时进行的登录会话过多；500 渠道操作失败。正常发送可返回 200 与业务 `failed`、`partial` 或 `unknown` 结果，因此必须检查 JSON 业务结果。

## 路由

| 方法与路径 | 请求 | 结果 |
| --- | --- | --- |
| GET `/` | 可省略专用请求头 | 本机验证页 |
| GET `/app.js`、`/app.css` | 可省略专用请求头 | 验证页静态资源 |
| GET `/health` | 无 | 进程状态与账号数 |
| GET `/openapi/v1.json` | 无 | OpenAPI 文档 |
| POST `/api/logins` | 无 | 202，登录 id、查询与 QR 路径 |
| GET `/api/logins/{id}` | 无 | 状态、是否等待验证码、二维码是否可用、脱敏结果 |
| DELETE `/api/logins/{id}` | 无 | 取消在途登录，204 |
| GET `/api/logins/{id}/qr` | 无 | `image/png`；未生成/已结束返回409 |
| POST `/api/logins/{id}/code` | `{"code":"手机显示的数字"}` | 204；验证码不进入后续查询 |
| GET `/api/accounts` | 无 | 账号、owner、allowlist、enabled、debug、暂停和运行状态、可选备注 `remark` |
| PUT `/api/accounts/{id}` | `{"enabled":true,"allowlist":["peer"],"debug":false,"remark":"测试号","expectedOwnerId":"peer"}`；字段均可省略 | 204 |
| POST `/api/accounts/{id}/start` | 无 | 202，启动轮询，消费模式为 manual-inbox |
| POST `/api/accounts/{id}/stop` | 无 | 204，中止在途轮询 |
| DELETE `/api/accounts/{id}` | 无 | 204，停止并删除账号及相关持久状态 |
| GET `/api/accounts/{id}/messages?before=可选` | 上一页的 `nextBefore` | 该账号与当前扫码人的收发历史 |
| POST `/api/accounts/{id}/messages` | `{"ownerId":"扫码人 ID","text":"正文","replyDeliveryId":"可选"}` | 以指定账号发送文本或 Markdown，返回逐段结果 |
| POST `/api/accounts/{id}/messages/media` | `multipart/form-data`，必填 `ownerId`、`file`，可选 `text`、`replyDeliveryId` | 向当前扫码人发送一个附件及可选说明，返回逐段结果 |
| GET `/api/accounts/{id}/messages/{direction}/{messageId}/attachments/{attachmentId}` | 历史中的账号、方向、消息及附件标识 | 授权附件的二进制内容。文件以下载响应返回 |
| POST `/api/messages` | SendRequest，见下文 | 全部分段结果 |
| POST `/api/messages/reply` | `{"deliveryId":"…","text":"…","media":"可选路径或URL"}` | 使用该 delivery 接收时的 context 回复 |
| POST `/api/messages/typing` | `{"peerId":"…","active":true,"accountId":"可选","deliveryId":"可选"}` | 204；调用者按5秒保活、结束发送active=false |
| POST `/api/messages/progress` | `{"peerId":"…","runId":"…","toolCallId":"…","toolName":"search","status":null,"accountId":"可选","deliveryId":"可选"}` | status省略为工具开始；结果带状态，204 |
| POST `/api/messages/progress/{runId}/complete` | 无 | 执行结束后关闭该run进度，等待已排队进度排空，204 |
| POST `/api/messages/timing` | `{"deliveryId":"…","operation":"检索","elapsedMilliseconds":123.5}` | 最佳努力上报宿主耗时，204；上报失败不改变业务Ack |
| GET `/api/inbox/pending?accountId=可选` | 无 | 未完成 inbox 摘要，不领取 |
| GET `/api/inbox/{deliveryId}/receipts` | 无 | 该delivery全部持久发送分段；仅clientId/messageId/status/error，重启后可恢复查询 |
| POST `/api/inbox/claim?accountId=可选` | 无 | 200含消息与 leaseId/leaseUntil；暂无可领取消息204 |
| POST `/api/inbox/{deliveryId}/ack` | `{"leaseId":"…"}` | 204；仅当前有效租约可确认 |
| POST `/api/inbox/{deliveryId}/retry` | `{"leaseId":"可选","allowUnknownSend":false}` | 重新排队；显式允许unknown时返回重复风险提示 |
| POST `/api/inbox/{deliveryId}/renew` | `{"leaseId":"…"}` | 新 leaseUntil |
| GET `/api/events` | 无 | `text/event-stream`，只通知，不claim或ack |

SendRequest：

```json
{
  "accountId": "可选，歧义时必填",
  "peerId": "收件人",
  "text": "正文或媒体 caption",
  "media": "可选：本地路径、file:// 或 HTTP(S)",
  "deliveryId": "可选：绑定入站消息",
  "runId": "可选：同次执行的关联ID",
  "allowUnknownRetry": false
}
```

文本默认按 4000 UTF-16 单元拆分，caption 各块先于媒体发送，音频按文件发送。每个分段含 `clientId`、可选 `messageId`、`status` 和脱敏 `error`。在 `unknown` 情况下先人工核查消息是否送达，再使用显式允许重试选项；这可能产生重复消息。

若HTTP响应丢失或服务重启，可调用 `/api/inbox/{deliveryId}/receipts` 恢复持久化分段结果；该查询不返回原始协议请求、context token、验证码或密钥。OpenAPI明确包含SendResult/SendSegment、Claim的200与204、lease响应和ProblemDetails schema。

过滤和 hook 处理后没有有效文本或媒体时返回 `cancelled`，分段为空，不发送消息或触发发送后通知。持久收据还可能显示在途的 `sending`，或人工接受重复风险后的 `retry-approved`；这两种状态不表示微信已确认送达。

## 验证页的账号聊天接口

`remark` 是当前绑定的本地备注，最多 40 个 UTF-16 单元。省略或传 `null` 保留现有备注，空白字符串清除。发送 `expectedOwnerId` 时，扫码人已变更会返回 409。

历史响应包含 `ownerId`、`items`、`nextBefore` 和布尔字段 `hasSendContext`。每页最多 50 条，从新到旧。需要更早消息时，把返回的 `nextBefore` 原值作为下一次请求的 `before`，末页没有后续游标。每条消息保留原 `id`、`direction`、`at`、`text`、`attachments`、`status`。入站 ID 为 `deliveryId`，出站 ID 为分段的 `clientId`。`parts` 按原顺序列出正文、语音转写与媒体。`quote` 提供引用摘要、缺失状态和可用附件。`canReply` 表示该入站具备可用的原会话上下文。

`awaitingDeliveryConfirmation` 为 `true` 表示微信已受理该出站分段，回执缺少 `messageId`，需要在手机上核对送达。

附件提供 `kind`、可选 `name` 及可选 `id`。有 `id` 时，按同一账号、方向、消息 ID 和附件 ID 请求二进制接口，继续带 `x-weixin-request: 1`。文件首次成功读取后长期保存于状态目录。历史和已保存的媒体可跨重启查询，删除或实际换绑会移除对应历史和媒体。未授权、缺失或 CDN 已失效的附件返回 404。读取历史或附件不领取、续租或确认消息，也不公开服务器文件路径和媒体解密参数。

发送时使用历史返回的 `ownerId`。扫码后需先开始接收，由扫码微信向机器人发一条单聊消息。收到包含会话上下文的授权单聊后，`hasSendContext` 才为 `true`。省略 `replyDeliveryId` 时使用会话现有上下文，传入时使用该条授权入站的上下文。账号或目标消息不存在时返回 404，扫码人已变更或缺少上下文时返回 409，空白正文或回复目标归属不符时返回 400。上传的 `file` 必须非空，单文件上限 100 MiB，超限返回 413。带说明文字时先发文字，再发媒体。音频按文件发送。发送接口返回 `SendResult`，调用方需检查总体及逐段状态。`partial` 或 `unknown` 应先核查手机实际收到的内容，再决定是否补发。

网页定向回复的分段记入聊天历史，原入站消息继续由 Inbox 独立领取和确认。

## 可靠消费顺序

1. `POST /api/accounts/{id}/start`，让服务持久化入站和游标。
2. `POST /api/inbox/claim?accountId={id}`；保留 `deliveryId`、`leaseId`。默认租约60秒。
3. 以 `deliveryId` 对业务副作用做幂等处理；账号内 FIFO，不要越过当前消息。
4. 处理超过20秒时，以同一 leaseId 调用 renew；旧租约失效后不得 ack 新领取。
5. 需要回复时，使用该 delivery 的回复接口；正文与附件路径来自 claim 响应。附件需长期保存时先复制到宿主存储，Ack 后临时文件可被清理。
6. 业务成功后 ack；可恢复错误 retry。客户端崩溃时等待租约到期，其他客户端可重新领取相同 delivery。

`AccountId + PeerId` 是宿主会话与 Agent 历史的隔离键。不要仅用 peerId 在多账号环境共享会话。handler 和手动消费不能同时消费同一账号。

claim 响应的 `runId` 与 `deliveryId` 相同，可直接用于该消息的工具进度。工具开始省略 status，结束以同一 runId/toolCallId 发送 completed、failed 或 blocked；最终正文也应沿用同一 runId。若自行指定 runId，正文使用 `/api/messages` 显式传入；`/api/messages/reply` 固定使用 deliveryId 作为 runId。一轮执行结束后调用 `/api/messages/progress/{runId}/complete` 等待进度排空。进度接口的204仅表示最佳努力调用结束，不证明微信已确认或显示，需结合渠道日志和客户端核对。Web 请求不会自动持续保活 typing，应按5秒重发并在 finally 请求 active=false。

手动消费的宿主可在处理后调用 `/api/messages/timing`，以毫秒报告该 delivery 的业务处理耗时。该通知是最佳努力；消息业务成功后仍正常 Ack，不因诊断上报失败重复执行业务。

## curl 示例

```bash
curl -X POST -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/logins
curl -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/logins/LOGIN_ID/qr -o login.png
curl -X POST -H 'x-weixin-request: 1' -H 'Content-Type: application/json' \
  -d '{"code":"123456"}' http://127.0.0.1:3083/api/logins/LOGIN_ID/code
curl -X POST -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/accounts/ACCOUNT_ID/start
curl -H 'x-weixin-request: 1' -F 'ownerId=OWNER_ID' -F 'text=图片说明' \
  -F 'replyDeliveryId=DELIVERY_ID' -F 'file=@photo.png' http://127.0.0.1:3083/api/accounts/ACCOUNT_ID/messages/media
curl -X POST -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/inbox/claim
curl -X POST -H 'x-weixin-request: 1' -H 'Content-Type: application/json' \
  -d '{"deliveryId":"DELIVERY_ID","text":"已收到"}' http://127.0.0.1:3083/api/messages/reply
curl -X POST -H 'x-weixin-request: 1' -H 'Content-Type: application/json' \
  -d '{"leaseId":"LEASE_ID"}' http://127.0.0.1:3083/api/inbox/DELIVERY_ID/ack
curl -N -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/events
```

SSE 首帧为 `event: pending`，数据包含当前待办总数和各账号计数；之后为 `event: channel`，间隔有 keepalive 注释。原生 EventSource 不能设置自定义请求 header，可使用 `fetch` 流读取或支持自定义 header 的客户端。连接、断开、重连不会改变 inbox 状态。

客户端应在启动、重连及收到通知时尝试 claim，并定期查询 inbox 或再次 claim。租约到期、重试等待结束和慢客户端遗漏通知时，不应只等待下一条 SSE 事件；持久 inbox 才是可交付消息的依据。

## 登录状态

官方状态保持 `wait`、`scaned`、`confirmed`、`expired`、`need_verifycode`、`verify_code_blocked`、`scaned_but_redirect`、`binded_redirect`；Web 还显示 `starting`、`need_verifycode_retry`、`cancelled`、`failed`。`binded_redirect` 表示已连接，不签发新凭据。二维码刷新后重新取得 PNG；验证码请求只接收当前等待中的值。
