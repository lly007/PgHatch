# PGTray

A lightweight, portable PostgreSQL instance manager for Windows.

**v0.1.1** · C# / .NET 10 / Windows Forms · Windows 10/11 x64 · PerMonitorV2。使用用户提供的 PostgreSQL ZIP 程序，不捆绑 PostgreSQL，不安装 Windows Service，不修改系统 PATH，不要求管理员权限。没有第三方 NuGet 依赖。

## 运行与升级

发布文件位于 `artifacts/PGTray-v0.1.1-win-x64/PGTray.exe`；ZIP 另附说明、变更记录与校验值。把 EXE 放在当前用户可写的目录中运行即可。Self-contained / Single-file，不启用裁剪或 Native AOT；原生运行库首次启动会解压到 .NET 的临时缓存。

从 v0.1.0 升级时，先退出旧管理器，保留同目录的 `pgtray.json`、备份和日志，再替换 EXE。数据库可以继续运行。配置保持 schema 1，兼容旧登记项，不会根据旧记录猜测或改写监听配置。

## 使用

1. **程序管理**：注册 PostgreSQL 根目录或 `bin`，检测 pg_ctl、postgres、initdb、pg_isready 和版本。可注册多套程序。常见相邻目录会自动发现。
2. **扫描已有实例**：选择有限范围的根目录，执行只读扫描。查看路径、主版本、端口、监听地址、兼容程序和说明，勾选后导入。默认不勾选；有多套兼容程序时必须明确选择。已注册、不兼容、标识不完整或疑似备份的目录不会由向导直接导入。
3. **导入实例**：手动选择完整 PGDATA，读取真实配置端口；登记值不会覆盖原始设置。导入不启动数据库。
4. **新建实例**：填写空目录、独立端口和初始超级用户凭据。默认 UTF-8、SCRAM-SHA-256、`127.0.0.1` 监听。初始化后默认停止。
5. 左侧选择实例，右侧查看版本、路径、监听地址、端口、进程状态与连接状态，独立启动/停止/重启。日志、数据目录和实例设置入口位于详情区。

例如以下布局可发现程序，并通过扫描找到已有集群：

```text
PostgreSQL/
    PGTray.exe
    PostgreSQL/bin/
    PGData/main/
    PGData/test/
```

扫描最多向下 3 层、检查 256 个目录，遇到集群便停止进入其内部；不跟随目录链接。默认附近范围仅加入常见 PGData/data 目录，手动选择范围时只扫描所选根目录。扫描不初始化、不改写配置、不注册、不启停。备份识别属于保守的名称提示，仍需使用者核对目录用途。

空列表提供创建、导入、扫描引导，采用内嵌界面，不在每次启动弹出向导。

## 启动配置和状态语义

**启动与重启都沿用 PGDATA 的原始配置，不传入覆盖端口和监听地址的 `-o` 参数。** 新建实例的本机默认值由 initdb 后生成的配置提供。导入实例原有的 `postgresql.conf`、`postgresql.auto.conf`、include 配置保持原样。

“实例设置”只直接修改显示名称，可主动打开原始配置。手动编辑 port/listen_addresses 会影响下次启动或重启；开放非本机监听可能扩大可连接范围，同时受 pg_hba.conf 与防火墙约束。PGTray 不自动编辑这些设置。

| 信息 | 含义 |
| --- | --- |
| Stopped | pg_ctl status 对指定 PGDATA 确认无服务器进程。 |
| Starting / Stopping | 正在执行操作，或 PID 文件报告启动/备用恢复过渡状态。 |
| Running | pg_ctl 确认服务器进程运行，不等同于连接就绪。 |
| Unknown / Error | 无法确认或发生路径、版本、工具错误；不会猜测为运行或停止。 |
| Ready | pg_isready 接受连接，并核对本机监听端点属于该 PID；探测前后 PID 文件身份未变。 |
| Not Ready | 服务器进程存在，属于它的端点暂不接受连接，例如恢复期间。 |
| Connection Unknown | 服务器进程存在，但本机端点归属或连接就绪无法确认；不因此显示为 Stopped。 |

支持非默认本机地址、IPv4/IPv6 和通配监听；只探测已核对归属的本地端点，不扩展远程管理。端点查询不可用时保留独立的进程状态。Windows PID 文件及工具输出兼容 UTF-8 / 系统 ANSI 编码。

每 4 秒异步轮询。手动启停会取消旧轮询并使其结果失效；生命周期操作同实例互斥，不同实例可并行。正常停止使用 `pg_ctl stop -m fast -w`。启停等待超时后再次检查实际进程，不杀数据库进程、不清理 PID 文件。

## 数据保护

- 初始化拒绝已有内容、已有或疑似 PGDATA 的任何子目录，以及经过符号链接/junction、尾随空格/句点、设备路径等无法明确判定的路径。新建集群限本机磁盘目录。
- 导入目录按真实路径去重；初始化验证在链接解析前执行，避免规范化掩盖风险。
- “移除配置”只移除登记，数据、日志和数据库运行状态不变；没有删除数据库文件的入口。
- 初始密码只通过当前用户 ACL 保护的临时文件交给 initdb，结束后删除，不写入 JSON、日志或命令参数。应用不保存密码。
- initdb 失败保留专用目标目录供检查，不自动重置或清理。
- 为避免错误操作其他集群，当前版本拒绝配置中的 data_directory 指向所选 PGDATA 之外的分离布局。

## 配置、日志与退出

`pgtray.json` 位于 EXE 旁，原子替换保存，上一版保存为 `.bak`。同盘可相对定位的路径保存为相对路径，跨盘保留绝对形式；整体迁移需保留相对布局。不可写时明确报错，不静默改存到其他位置。

配置损坏时停止正常配置写入，主窗口保留“配置目录”“重新读取”“从 .bak 恢复”“保留原文件并新建空配置”入口。只有明确选择恢复才替换配置，并先另存原文件为 `pgtray.json.preserved-*`。不会自动用空配置覆盖损坏文件。

日志默认位于 EXE 旁 `logs/<instance-id>.log`。GUI 查看最近 64 KiB，保留本次会话最近操作的标准输出、错误输出和返回码。已有 logging_collector 的日志继续位于原配置位置，不自动合并或清理。

- 关闭主窗口：隐藏到托盘。双击托盘或再次运行 EXE 恢复窗口。
- “退出” / Ctrl+Q / 托盘“退出 PGTray”：等待当前操作安全完成、取消轮询，只退出管理器，数据库继续运行。
- 托盘“停止全部已注册实例并退出”：确认后停止所有已注册实例，包括外部启动的已注册实例；任何停止失败会留在管理器中供检查。
- Windows 注销/关机不属于数据库继续运行的保障范围。

AutoStart 元数据继续保留，当前不执行开机或实例自动启动。

## 开发和真实集成测试

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project src/PGTray
dotnet run --project tests/PGTray.IntegrationTests -c Release -- 'D:\PostgreSQL\PostgreSQL' 'D:\PGTray-Test'
dotnet publish src/PGTray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/PGTray-v0.1.1-win-x64
```

集成测试仅在测试父目录下新建有唯一标识的目录，使用空闲端口和专用凭据，结束后只停止本次创建的集群，保留结果与日志，不递归删除。不要把真实 PGDATA 或其父目录作为测试目录。真实操作依赖现有 PostgreSQL 18 程序，不使用 Mock 替代这些测试；另有端点查询不可用的失败注入测试，报告中单独注明。

工程延续原分层：`PGTray` 原生 UI，`PGTray.Core` 配置/路径/工具/生命周期/发现，`PGTray.IntegrationTests` 真实回归。没有更换 GUI 框架或引入调度、DI、ORM 框架。

详细变更见 [CHANGELOG](../../../CHANGELOG.md)，本轮证据和待人工步骤见 [v0.1.1 验证记录](validation.md)。四档 DPI、跨屏拖动、托盘所有点击路径以及没有 Runtime 的 Windows 10/11 干净环境仍需逐项验证，不能以本机运行替代这些验收。当前完整真实版本测试为 PostgreSQL 18.6；17/18 主版本拒绝测试使用测试元数据，尚未安装第二套真实版本。

不实现 SQL 编辑器、备份恢复、升级、自动下载或远程管理。
