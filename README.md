# PgHatch

**PostgreSQL, the App Way.**

A portable PostgreSQL instance manager for Windows.

PgHatch 使用 C# / .NET 10 / 原生 WinForms，管理用户自行提供的 PostgreSQL ZIP 程序和本地数据目录。解压即可使用，无需传统安装向导、Windows Service、Docker/WSL 或修改系统 PATH。

## 核心功能

- 注册多套 PostgreSQL 程序，共享程序目录、独立管理多个实例。
- 新建、只读扫描和导入已有集群，保留导入实例原始启动配置。
- 异步启动、停止、重启；分别显示进程状态与连接就绪状态。
- 便携 JSON 配置、有效备份、损坏恢复和兼容旧版迁移。
- 可在线维护的 [PostgreSQL ZIP 目录与下载部署](docs/postgresql-catalog.md)，支持离线目录、安全续传、SHA-256、安全解压及自动注册。
- 原生托盘、日志入口、HiDPI、简体中文 / English / 繁體中文及正式品牌图标。

## 选择发行模式

当前源码候选为 **1.0.0-rc.1**，正式 GitHub Release 仍为 **v0.2.0**。V1.0 尚未正式发布。四种发行包管理功能相同，均为 Release 单文件应用：

| 架构 | Standalone | Framework-dependent |
| --- | --- | --- |
| Windows x64 | 自带 .NET，无需另装运行时 | 需要 x64 .NET 10 Desktop Runtime |
| Windows ARM64 | 自带 .NET，无需另装运行时 | 需要 ARM64 .NET 10 Desktop Runtime |

普通 .NET Runtime 不一定满足 WinForms 要求。ARM64 已交叉构建和静态检查，真实 ARM64 Windows 运行仍待验证。PgHatch 不捆绑 PostgreSQL，也不要求管理器与外部 PostgreSQL 程序具有相同架构。

## 开始使用

从 [GitHub Releases](https://github.com/lly007/PgHatch/releases) 下载适合自己架构和运行时环境的发行包。

1. 将发行包解压到可写目录，运行 `PgHatch.exe`，保留随包许可证。
2. 在“PostgreSQL 程序”中注册程序根目录或 `bin`。
3. 创建独立新实例，或扫描 / 导入已有 PGDATA。扫描和导入不会自动启动数据库。

关闭主窗口会隐藏至托盘；普通退出只关闭管理器，数据库继续运行。“停止全部并退出”是另一个需要确认的操作。

最新源码也支持从 Catalog 下载并安全部署 PostgreSQL 程序；下载、注册和导入不会自动创建或启动实例。同主版本维护更新必须先停止实例，再明确切换到独立新程序目录；不进行跨主版本数据迁移。Windows 确认关机或重启时会尽力正常停止已注册实例，强制关机和断电仍无法保证收尾完成。

## 文档与开发

- [用户指南](docs/user-guide.md)：使用、安全边界、配置、升级和状态语义。
- [发行流程](docs/releasing.md)：一套 PowerShell 脚本生成 x64 / ARM64 四种候选包。
- [文档与版本验证记录](docs/README.md)：历史版本验证、发行矩阵和人工验收边界。
- [版本历史](CHANGELOG.md) · [第三方声明](THIRD-PARTY-NOTICES.md)。
- [V1.0 候选验收](docs/releases/v1.0.0/validation.md) · [MIT 许可证](LICENSE)。

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project src/PGTray
pwsh -NoProfile -File scripts/Publish.ps1
```

GitHub 现有 v0.2.0 Release 发布于 2026-10-21，包含多语言和界面修复。后续加入的在线目录及下载部署功能目前以最新源码和独立本地候选包为准，尚未替换正式 Release。早期候选的验证范围见[发行矩阵记录](docs/releases/v0.2.0/release-matrix.md)；历史封版文件保持原样，后续发布一律使用新的独立候选目录。

内部工程名、命名空间和历史配置字段继续保留 PGTray，以维持兼容性。
