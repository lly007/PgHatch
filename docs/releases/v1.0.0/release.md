# PgHatch v1.0.0 — PostgreSQL, the App Way

A portable PostgreSQL instance manager for Windows.

PgHatch 1.0.0 由已验收的 1.0.0-rc.1 晋升，正式包内部版本为 1.0.0。本轮仅更新版本元数据和发布文档，未改变业务逻辑。

## 功能概览

- PostgreSQL 程序目录注册与发现，实例新建、导入、只读扫描及多实例管理。
- 异步初始化、启动、停止和重启，分别显示服务器进程与连接就绪状态，保留日志和工具诊断。
- 便携配置、备份恢复、PGDATA 保护；普通退出保留数据库运行，关机/重启时尽力正常停止已注册实例。
- XML Catalog 在线更新、有效缓存与离线回退。
- 流式下载、取消与保守断点续传，完整 SHA-256 校验、安全解压、程序验证和自动注册。
- 同主版本维护更新：核对工具版本和 PG_VERSION，先停库再明确切换到独立程序目录，保留旧程序和 PGDATA。
- 简体中文、English、繁體中文；原生托盘、正式品牌图标及 PerMonitorV2 DPI 适配。

## 下载与运行时

所有模式功能相同，解压到可写目录后运行 PgHatch.exe，无需 Windows Service 或传统安装器。首次使用建议选择 Standalone。

| 架构 | 模式 / 下载 | 运行要求 |
| --- | --- | --- |
| win-x64 | [PgHatch-win-x64-standalone.zip](https://github.com/lly007/PgHatch/releases/download/v1.0.0/PgHatch-win-x64-standalone.zip) | 自包含 .NET 10 Desktop Runtime，无需预装 |
| win-x64 | [PgHatch-win-x64-framework.zip](https://github.com/lly007/PgHatch/releases/download/v1.0.0/PgHatch-win-x64-framework.zip) | 需要 x64 架构的 .NET 10 Desktop Runtime |
| win-arm64 | [PgHatch-win-arm64-standalone.zip](https://github.com/lly007/PgHatch/releases/download/v1.0.0/PgHatch-win-arm64-standalone.zip) | 自包含 .NET 10 Desktop Runtime，无需预装 |
| win-arm64 | [PgHatch-win-arm64-framework.zip](https://github.com/lly007/PgHatch/releases/download/v1.0.0/PgHatch-win-arm64-framework.zip) | 需要 arm64 架构的 .NET 10 Desktop Runtime |

Framework-dependent 需要对应架构的 **.NET 10 Desktop Runtime**；仅安装普通 .NET Runtime 不足以满足 WinForms 要求。

### ARM64 验证边界

**ARM64 发行包已完成交叉构建和静态检查，尚未在真实 Windows ARM64 设备上验收。** 不宣称已通过 ARM64 实机兼容性测试。PgHatch 通过外部进程管理 PostgreSQL，二者架构不要求相同；当前 Catalog 提供的是 Windows x64 PostgreSQL ZIP，在 ARM64 上运行它们依赖 Windows 的 x64 兼容能力，尚未单独实测。没有收录未经核验的原生 ARM64 PostgreSQL 包。

## 数据与更新边界

- PgHatch 不捆绑 PostgreSQL。可从应用中的 Catalog 下载部署，也可注册已有程序目录。
- 只有用户明确创建新实例才执行 initdb；下载、部署、注册、扫描或导入不会自动初始化已有 PGDATA 或启动数据库。
- 维护版本更新不等于跨主版本升级。PgHatch 不自动迁移已有数据库，不执行 pg_upgrade；切换前必须停库、备份并确认扩展兼容性。
- 普通退出不会自动停止数据库；Windows 关机收尾最多等待 30 秒，强制结束或断电仍无法保证正常停止完成。
- 保留包内 MIT 项目许可证、第三方声明及运行库许可。历史 v0.2.0 制品保持原样。

## 发行校验

四种包均为 Release 单文件，未开启 Trimming、Native AOT 或 ReadyToRun；每包通过 277 项静态检查与 ZIP 解压哈希回验。两个 x64 包从独立解压目录启动、正常退出码 0。完整功能回归及主要人工验收沿用 RC 记录，本轮未重复执行。

下表为四个 ZIP 的 SHA-256；同时提供仅含这四项的 [SHA256SUMS.txt](https://github.com/lly007/PgHatch/releases/download/v1.0.0/SHA256SUMS.txt)。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| PgHatch-win-x64-standalone.zip | 50,085,961 | `E98429923ABBBCB56CF4A38D3D16260FFFF5C60930A6E664E8B15AEBF71EDE39` |
| PgHatch-win-x64-framework.zip | 2,791,129 | `BC47597311AAF335546276A1D9988F38BC943813BD9805325EAA045EF3FD2A5A` |
| PgHatch-win-arm64-standalone.zip | 47,945,044 | `39DDA24114F1735FBBB2563B17F8A8E43BEED19C2C5DBCD4A4440E3967DEC15B` |
| PgHatch-win-arm64-framework.zip | 2,778,053 | `B817E3364470C586F76249D0FF566671B6071C66EED2AD6A4285D6BF01BA4681` |

Windows PowerShell 校验示例：

```powershell
Get-FileHash .\PgHatch-win-x64-standalone.zip -Algorithm SHA256
```

[用户指南](https://github.com/lly007/PgHatch/blob/v1.0.0/docs/user-guide.md) · [CHANGELOG](https://github.com/lly007/PgHatch/blob/v1.0.0/CHANGELOG.md) · [验收与发行记录](https://github.com/lly007/PgHatch/blob/v1.0.0/docs/releases/v1.0.0/validation.md)
