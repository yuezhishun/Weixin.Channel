# 验收与服务器部署

本指南说明独立 Weixin.Web 手动网关的部署。目标平台为 Windows x64 和 Ubuntu 24.04 x64，构建 SDK 固定为正式版 `11.0.100`。

历史真机结果及未通过项见[验收记录](acceptance.md)和[进度诊断](progress-diagnosis.md)。本次交付的自动化结果以构建目录中的 TRX 为准。

## 交付验收

| 层次 | 执行与检查 | 通过条件 |
| --- | --- | --- |
| 构建与独立性 | 在Windows、Linux分别运行build脚本 | locked restore、Release build、全部测试、HTTP smoke、pack及独立NuGet消费者均成功；0失败、0跳过 |
| 目标服务器部署 | 启动发布程序，检查health、账号、状态目录和重启 | 健康检查200，SQLite可写；账号启动后running=true；重启后状态保留并重新启动账号 |
| 业务闭环 | 微信发唯一文本，业务程序领取、处理、回复、确认 | 接收方/账号正确，实际看到回复；SendResult为success；成功后Ack，inbox无残留 |
| 可靠性 | 未Ack重启、租约到期重领、旧租约Ack、SSE断连 | 同一DeliveryId恢复，旧租约拒绝；业务用DeliveryId幂等；SSE断开不丢待办 |
| 媒体与交互 | 图片/视频/文件/语音、引用、长文本、typing及原生进度 | 实际客户端可打开/播放、内容与顺序正确；原生进度需用户实际看到，不能用HTTP200/204代替 |

在本目录运行自动化，测试使用独立目录；HTTP smoke要求本机3083端口空闲：

```powershell
./scripts/build.ps1 -Dotnet C:/dotnet/dotnet.exe
```

```bash
WEIXIN_DOTNET=/path/to/dotnet bash scripts/build.sh
```

脚本不执行真实扫码；其输出的“真机验收：未执行”表示本次脚本未执行，不覆盖人工真机记录。日志、TRX位于`artifacts/verification/`，NuGet 包位于 `artifacts/packages/<RID>/`。平台归档包含可直接运行的 Weixin.Cli 和 Weixin.Web 子目录，拷贝相应应用时保留完整目录。

## 选择部署方式

已有C# Agent宿主时，引用`Weixin.Channel` NuGet并通过`RunAsync(accountId, handler)`接入；宿主负责模型、会话历史和业务幂等。库负责领取、续租、handler成功后的Ack及typing生命周期。README提供接入示例。

其他语言或独立业务进程可部署`Weixin.Web`作为微信网关，业务程序消费HTTP inbox。Web示例没有模型依赖，也不会自动回复普通消息。`Weixin.Channel.nupkg`是给其他项目引用的库，不能直接作为服务运行。

以下部署Web网关。它通过出站HTTP长轮询微信，不需要公网Webhook或域名；服务器需能访问微信API/CDN及业务所需的模型、媒体地址。API当前仅接受本机访问；跨机器联调使用SSH隧道。固定请求头是本机请求边界检查，不是面向公网的身份认证。

## 发布Linux x64自包含程序

在构建机的独立发布工作副本中执行，使用锁定SDK。自包含目录包括.NET运行时和SQLite本机依赖，服务器无需安装.NET SDK或共享.NET运行时；操作系统仍需满足.NET的系统库依赖。已验证环境为Ubuntu24.04 x64，不将其视为其他发行版或ARM的验证。

当前仓库统一声明`win-x64;linux-x64`，标准`packages.lock.json`已包含两套目标图，可直接执行locked发布：

```bash
# 在仓库根目录，dotnet 指向 11.0.100
dotnet --version
dotnet restore Weixin.Channel.slnx --locked-mode

dotnet publish samples/Weixin.Web/Weixin.Web.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:RestoreLockedMode=true \
  -o artifacts/publish/linux-x64

tar -C artifacts/publish/linux-x64 \
  -czf artifacts/weixin-web-linux-x64.tgz .
```

两平台连续locked restore/publish已验证标准锁不被覆盖，只有明确更新依赖时才重新生成。首次引入RID曾出现NU1004，已通过统一目标图修正。

Windows使用相同SDK，目标改为`win-x64`，使用PowerShell的续行语法；输出程序为Weixin.Web.exe。本文不把Linux网关发布探测当作Windows网关自包含发布验证，客服宿主的双平台产物另行记录。

## 安装与systemd托管

将完整发布目录上传到服务器，不能只复制Weixin.Web或DLL。示例路径：代码在`/opt/weixin/app`，持久状态在`/var/lib/weixin`。初次创建运行用户和目录：

```bash
sudo useradd --system --home-dir /var/lib/weixin \
  --shell /usr/sbin/nologin weixin
sudo install -d -o root -g root -m 755 /opt/weixin/app
sudo install -d -o weixin -g weixin -m 700 /var/lib/weixin
sudo tar -xzf /tmp/weixin-web-linux-x64.tgz -C /opt/weixin/app
sudo chmod +x /opt/weixin/app/Weixin.Web
```

将以下内容保存为`/etc/systemd/system/weixin.service`：

```ini
[Unit]
Description=Weixin channel gateway
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=weixin
Group=weixin
WorkingDirectory=/opt/weixin/app
ExecStart=/opt/weixin/app/Weixin.Web --state /var/lib/weixin
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=5
TimeoutStopSec=60
UMask=0077

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now weixin
sudo systemctl status weixin
sudo journalctl -u weixin -f
curl -fsS -H 'x-weixin-request: 1' http://127.0.0.1:3083/health
```

首次扫码可在自己电脑建立隧道；先确保电脑本地3083端口空闲：

```bash
ssh -N -L 3083:127.0.0.1:3083 user@server
```

另一个本地终端执行以下请求，用创建登录返回的id替换LOGIN_ID：

```bash
curl -X POST -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/logins
curl -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/logins/LOGIN_ID
# 查询到二维码可用后取PNG，在自己电脑上打开并用微信扫码。
curl -f -H 'x-weixin-request: 1' \
  http://127.0.0.1:3083/api/logins/LOGIN_ID/qr -o weixin-login.png
curl -H 'x-weixin-request: 1' http://127.0.0.1:3083/api/accounts
curl -X POST -H 'x-weixin-request: 1' \
  http://127.0.0.1:3083/api/accounts/ACCOUNT_ID/start
```

二维码过期时重新查询并获取刷新后的PNG；需要验证码时调用`/api/logins/LOGIN_ID/code`。具体请求和错误见[API说明](api.md)。

**进程恢复不等于账号自动启动。** 当前Web不会在启动时自动运行持久账号。扫码后，使用实际ACCOUNT_ID在`[Service]`中增加下行，让systemd每次启动网关后启动该账号；多个账号可分别增加一条ExecStartPost：

```ini
ExecStartPost=/usr/bin/curl --fail --silent --show-error --retry 20 --retry-connrefused --retry-delay 1 --retry-max-time 30 --request POST --header "x-weixin-request: 1" http://127.0.0.1:3083/api/accounts/ACCOUNT_ID/start
```

先确认服务器已安装curl，账号已登录且enabled=true，再应用配置。执行`systemctl daemon-reload`和`systemctl restart weixin`后，查询`/api/accounts`确认running=true。禁用或删除账号时同步调整此启动配置；也可以由业务宿主统一管理账号启动。

## 接入业务与上线检查

Web模式的业务消费者必须完成以下闭环，不能只连接SSE：

1. 按账号claim，保存deliveryId和leaseId；以DeliveryId实现业务幂等。
2. 执行Agent/模型或业务逻辑；长任务约每20秒renew，默认租约60秒。
3. 通过绑定deliveryId的reply发送，检查所有分段及业务status。HTTP200可能包含failed、partial或unknown。
4. 业务成功才Ack；失败Retry。unknown先人工核查，不自动重复发送。

示例流程：

```bash
curl -X POST -H 'x-weixin-request: 1' \
  http://127.0.0.1:3083/api/inbox/claim
curl -X POST -H 'x-weixin-request: 1' -H 'Content-Type: application/json' \
  -d '{"deliveryId":"DELIVERY_ID","text":"已收到"}' \
  http://127.0.0.1:3083/api/messages/reply
# 确认业务及回复成功后，使用claim返回的当前leaseId确认。
curl -X POST -H 'x-weixin-request: 1' -H 'Content-Type: application/json' \
  -d '{"leaseId":"LEASE_ID"}' \
  http://127.0.0.1:3083/api/inbox/DELIVERY_ID/ack
```

每账号只使用一种消费模式。一个状态目录只能有一个渠道进程；CLI与Web不能同时打开它，也不适合多个容器共享目录做横向扩容。业务会话按AccountId+PeerId隔离。

目标服务器签收至少检查：扫码成功；账号running=true；发送唯一中文/emoji文本并收到业务回复；收发图片/视频/文件/语音；待办可正确Ack；停服务后重启、重新启动账号，验证账号与未确认消息保留。health仅证明进程可用，账号running和业务闭环需另外检查。原生进度仍是未通过项目，部署成功不改变其验收状态。

代码与状态目录分开升级。停止服务后备份整个`/var/lib/weixin`目录，再替换发布文件并启动；不要只在运行中复制core.sqlite而遗漏WAL。迁移或回滚先停止旧进程，避免同一账号多处轮询；状态含登录凭据和聊天数据，只交给服务用户。首次服务器部署可使用新状态目录重新扫码，不复制OpenClaw数据。

## 本次发布探测证据

2026-11-07在隔离副本使用锁定SDK完成linux-x64自包含发布、独立RID锁及locked发布。WSL Ubuntu24.04实际启动发布的可执行文件；进程PATH不含dotnet，DOTNET_ROOT指向不存在路径，health/空账号查询/缺失header拒绝/SQLite本机库与数据库创建检查通过，未调用真实微信。

证据目录：`artifacts/verification/deploy-20261107-224158`。保留了初次WSL路径传参失败、原锁新增RID导致NU1004的失败日志，以及修正后的restore/publish/smoke结果。辅助锁文件数量检查首次将Web输出目录的复制件也计入而失败；随后按两个项目的输入路径分别核对RID目标和原锁字节一致性，结果见final-checks.json，未改动源码测试或业务断言。本次未在真实远程服务器安装systemd服务，未重跑源码144例测试，也未重新进行扫码；既有源码和真机验收结论分别引用原记录。

发布模式参照：[Microsoft .NET部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。systemd管理方式参照：[ASP.NET Core Linux托管说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx)。本示例采用loopback+SSH隧道访问，不照搬公网反向代理配置。
