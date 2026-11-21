# 第三方来源与许可

本实现根据 Tencent 官方渠道源码的协议与业务行为移植为 C#，保留其 MIT 声明。没有复制 OpenClaw Agent 引擎、Node 运行时或第三方原生 SILK 二进制。

| 组件 | 版本/依据 | 用途 | 许可文件 |
| --- | --- | --- | --- |
| Tencent openclaw-weixin | 2.4.10 / `874637a0dbc24050f72d1385add4fc6170a2d726` | 协议/渠道业务源码参考 | [Tencent-MIT.txt](Tencent-MIT.txt) |
| DrAbc.SilkCodec | 1.0.0 / `fed50705d363e28786c6f98b1fe9a9b11c15664a` | 纯 C# SILK 解码 | [SilkCodec-MIT.txt](SilkCodec-MIT.txt) |
| Microsoft.Data.Sqlite/Core | 11.0.0 | SQLite持久化 | [Microsoft-MIT.txt](Microsoft-MIT.txt) |
| SQLitePCLRaw bundle/config/core/provider | 3.0.5 / `ed046114d5a30534e13294d94d78eb73de896ad4` | SQLite平台加载 | [SQLitePCLRaw-Apache-2.0.txt](SQLitePCLRaw-Apache-2.0.txt) |
| SQLite | 3.53.4 | Windows/Linux原生SQLite | [SQLite.txt](SQLite.txt)，Public Domain |
| QRCoder | 1.8.1 / `d050cc7c0bc986f14cfedf24b947e0ec5c1a7736` | CLI ASCII / Web PNG二维码 | [QRCoder-MIT.txt](QRCoder-MIT.txt) |
| Microsoft.AspNetCore.OpenApi | 11.0.0 | Web OpenAPI文档 | [Microsoft-MIT.txt](Microsoft-MIT.txt) |
| Microsoft.OpenApi | 3.11.0 | OpenAPI模型/序列化 | [Microsoft-OpenAPI-MIT.txt](Microsoft-OpenAPI-MIT.txt) |

完整解析版本、依赖和内容哈希以各项目 `packages.lock.json` 为准。Microsoft 运行时与 SDK 使用其自身分发中的许可。测试项目另使用 Microsoft.NET.Test.Sdk、xUnit、xunit.runner.visualstudio、Microsoft.Extensions.TimeProvider.Testing；它们仅参与验证，不随 Weixin.Channel nupkg 作为运行时依赖分发。

SQLitePCLRaw copyright：Copyright 2014-2025 SourceGear, LLC。QRCoder 与 Microsoft 版权声明保留在对应许可文件内。所列许可文本来自锁定 NuGet 包或其官方仓库；仅将上游 TypeScript 行为移植为 C#，未修改分发的第三方 NuGet 包。

官方资料：[Tencent源码](https://github.com/tencent-weixin/openclaw-weixin)、[SilkCodec.NET](https://github.com/DrAbcOfficial/SilkCodec.NET)、[SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)、[SQLite](https://sqlite.org/copyright.html)、[QRCoder](https://github.com/Shane32/QRCoder)、[Microsoft.OpenAPI.NET](https://github.com/microsoft/OpenAPI.NET)。
