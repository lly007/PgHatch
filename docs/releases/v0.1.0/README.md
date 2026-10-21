# PGTray

A lightweight, portable PostgreSQL instance manager for Windows.

C# / .NET 10 / Windows Forms，面向 Windows 10/11 x64。使用用户提供的 PostgreSQL ZIP 程序，不安装服务，不修改全局 PATH。

## 开发

```powershell
dotnet build PGTray.sln
dotnet run --project src/PGTray
dotnet publish src/PGTray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish
```

配置保存在 EXE 同目录。请放在当前用户可写的目录中。关闭主窗口隐藏到托盘；退出管理器保留数据库运行。

项目结构：`src/PGTray` 原生 UI，`src/PGTray.Core` 配置与 PostgreSQL 操作，`tests/PGTray.IntegrationTests` 独立集群实测。无第三方 NuGet 依赖。

## v0.1.0 首个可运行版本

发布程序：`artifacts/PGTray-v0.1.0-win-x64/PGTray.exe`。该目录只需一个 EXE；发布包不包含 PostgreSQL，也不包含开发机的实例配置。

已实现 Phase 1、Phase 2 闭环，并实现 Phase 3 的新建、多实例、兼容性检查和日志。Phase 4 已完成发布和基础布局，完整平台验收仍在进行，见 [验证记录](validation.md)。

1. 将 EXE 放到当前用户可写的目录，双击运行。
2. “程序 / 设置”中填写 PostgreSQL 根目录或 `bin` 目录，点确定。重复操作可注册多个版本。
3. “导入实例”选择包含 `PG_VERSION` 和 `global/pg_control` 的 PGDATA。填写名称，选择匹配的程序，可点“读取端口”。导入只读检查，不启停数据库，也不修改原集群配置。
4. 或选“新建实例”，填写空目录、空闲端口、超级用户名和密码。初始化完成后默认处于停止状态，需要单独点击“启动”。
5. 选择实例，使用“启动 / 停止 / 重启”。下方显示实时诊断；“查看日志”显示最近操作及最近 64 KiB 的服务器日志。
6. “移除配置”只移除注册，不删除数据、不停止数据库。

复制到 PostgreSQL 根目录、`bin` 或相邻常见布局时，首次运行会尝试自动发现；其他目录需要手动选择。开发机可选 `D:\PostgreSQL\PostgreSQL`，这一路径没有硬编码进程序。

## 退出与托盘

- 关闭窗口隐藏到托盘；双击托盘图标或右键“打开管理界面”恢复。
- 再次启动 EXE 会恢复已运行的管理窗口，同一 Windows 会话仅运行一个管理器。
- 托盘“退出 PGTray（数据库继续运行）”或主窗口 `Ctrl+Q`：只退出管理器。
- 托盘“停止全部已注册实例并退出”：确认后依次执行 fast stop。范围是所有已注册实例，包括外部启动的已注册实例。任何停止失败会保留管理器供检查。
- Windows 注销/关机不属于“退出管理器后继续运行”的保障范围。

## 数据和配置

`pgtray.json` 位于 EXE 旁边；原子替换保存，并保留上一个版本 `pgtray.json.bak`。不可写或损坏时明确报错，不会静默改存到 AppData。相同磁盘可相对定位的路径保存为相对路径；跨盘路径保留绝对形式。整体迁移应保留 EXE、程序和数据的相对布局；只移动 EXE 时需同时检查配置路径。

实时状态与最近操作诊断只保存在内存，启动后重新查询。`AutoStart` 元数据为后续扩展保留，当前版本不执行自动启动。

新建集群默认 SCRAM-SHA-256、UTF-8、仅监听 `127.0.0.1`。初始密码只通过当前用户 ACL 保护的临时文件交给 initdb，正常结束或失败后删除，不写入 JSON、命令行或日志。应用不保留密码；请自行妥善保存。

启动使用 `pg_ctl start -D ... -l ... -o "-p PORT -h 127.0.0.1" -w -t 30`。即使导入的原配置允许远程访问，经 PGTray 新启动的进程也限定本机及登记端口；原文件不被改写。已在外部运行的实例显示其 PID 文件中的实际端口和监听地址。此版就绪探测使用 loopback，外部仅绑定其他本机地址的实例可能持续显示 Starting，需结合详情判断。

停止使用 `pg_ctl stop -m fast -w -t 30`。进程调用有独立超时，之后重新检查集群状态；不会杀 PostgreSQL 进程，不删除 `postmaster.pid`。状态来源为 `pg_ctl status -D`，就绪检查使用 `pg_isready`；每 4 秒异步轮询，同实例操作互斥。

PGTray 启动输出默认写入 EXE 旁 `logs/<instance-id>.log`。导入集群已有的 `logging_collector` 日志保留在原配置位置；本版本不合并、轮换或清理这些日志。中文 Windows 工具输出支持 UTF-8 / 系统 ANSI 编码识别。

## 测试

```powershell
dotnet run --project tests/PGTray.IntegrationTests -c Release -- 'D:\PostgreSQL\PostgreSQL' 'D:\PGTray-Test'
```

该程序使用真实 PostgreSQL 18 工具，在测试父目录下创建带唯一标识的新目录、随机空闲端口和专用凭据。结束时只停止本次创建的集群，不递归删除目录；结果在测试目录的 `results.txt`。不得把真实 PGDATA 作为测试父目录。测试凭据不保留，后续可用 GUI 进行启停与状态检查。

当前仅真实验证 PostgreSQL 18.6。17/18 不兼容拒绝逻辑使用测试元数据验证，尚未用完整 PostgreSQL 17 程序及集群做交叉测试。

## 当前范围与后续验收

浅色原生 WinForms 界面，启用 PerMonitorV2，TableLayoutPanel/FlowLayoutPanel 自动布局。当前显示环境已做实际 GUI 检查；100%、125%、150%、200% 和跨显示器拖动仍需逐档人工检查。未实现深色模式、开机启动、数据库升级、下载、SQL 编辑、备份恢复或远程管理。

Self-contained Single-file 发布已成功并运行；未开启裁剪或 Native AOT。框架原生库首次运行会解压到 .NET 管理的临时缓存，仍需要临时目录可写。开发机已安装 .NET；无 Runtime 的干净 Windows 10/11 环境仍待验证。完整 PostgreSQL ZIP 自身的系统依赖也由其发行包决定。

## 官方参考

- [Microsoft：WinForms 项目配置与 DPI](https://learn.microsoft.com/dotnet/desktop/winforms/whats-new/net60#project-level-application-settings)
- [Microsoft：单文件发布](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
- [PostgreSQL 18：pg_ctl](https://www.postgresql.org/docs/18/app-pg-ctl.html)
- [PostgreSQL 18：initdb](https://www.postgresql.org/docs/18/app-initdb.html)
- [PostgreSQL 18：pg_isready](https://www.postgresql.org/docs/18/app-pg-isready.html)
