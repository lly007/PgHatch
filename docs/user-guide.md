# PgHatch

**PostgreSQL, the App Way.**

A portable PostgreSQL instance manager for Windows.

**v0.2.0** · C# / .NET 10 / Windows Forms · Windows x64 / ARM64 候选发行 · PerMonitorV2。使用用户提供的 PostgreSQL ZIP 程序，不捆绑 PostgreSQL，不安装 Windows Service，不修改系统 PATH，不要求管理员权限。没有第三方 NuGet 依赖。ARM64 已交叉构建和静态检查，真实 ARM64 运行尚未验证。

## 运行与升级

统一发布命令：`pwsh -NoProfile -File scripts/Publish.ps1`。一次生成 x64 / ARM64 × standalone / framework 四个 Release 单文件候选包，位于全新的 `artifacts/candidates/run-<UTC>-<id>/`。也可通过 `-Architecture x64 -Mode framework` 选择子集。所有配置显式禁用裁剪、Native AOT 和 ReadyToRun。完整命令、包结构和验收方法见 [发行工程说明](releasing.md)。

Standalone 无需预装 .NET；Framework-dependent 需要与包架构对应的 **.NET 10 Desktop Runtime**，仅普通 .NET Runtime 不足以运行 WinForms。解压到当前用户可写目录运行，保留随包许可文件。原生库提取取决于实际 .NET 宿主与 bundle 内容；本轮 .NET 10 宿主集成运行库，x64 实测没有 bundle 文件提取，不能一概假设首次运行必然解压。

旧封版 `artifacts/PgHatch-v0.2.0-win-x64/` 及其 ZIP 保持原样，不作为本轮候选输出目录。当前版本仍为 0.2.0，没有覆盖历史发行。

PGTray 正式更名为 PgHatch。升级时先退出旧管理器，把 `PgHatch.exe` 放到原目录，保留 `pgtray.json`、备份和日志。首次读取会验证旧配置，生成 schema 2 的 `pghatch.json`，原文件原样保留；不会启停数据库。新旧文件同时存在时只使用新文件，不合并、不反复迁移。新文件损坏时进入恢复界面，不自动回退到旧文件。旧版与新版共用原单实例 Mutex / 唤醒事件，避免同时操作；如旧版正在运行，请先退出再启动新版。

## 使用

1. **程序管理**：注册 PostgreSQL 根目录或 `bin`，检测 pg_ctl、postgres、initdb、pg_isready 和版本。可注册多套程序。“发现附近程序”只提供候选路径，必须点击“注册程序”确认；启动时不再自动注册。
2. **扫描已有实例**：选择有限范围的根目录，执行只读扫描。查看路径、主版本、端口、监听地址、兼容程序和说明，勾选后导入。默认不勾选；有多套兼容程序时必须明确选择。已注册、不兼容、标识不完整或疑似备份的目录不会由向导直接导入。
3. **导入实例**：手动选择完整 PGDATA，读取真实配置端口；登记值不会覆盖原始设置。导入不启动数据库。
4. **新建实例**：默认 PGDATA 为 `AppContext.BaseDirectory/data/<实例名>`，初始名称为 `development`。目录名会处理非法字符和 Windows 保留名称；修改实例名会同步更新自动路径，手动选择或编辑过的路径不再被覆盖。现有非空目录或集群会被拒绝，不会覆盖；应用目录不可写时请明确改选可写位置。填写独立端口和初始超级用户凭据，默认 UTF-8、SCRAM-SHA-256、`127.0.0.1` 监听。初始化后默认停止。
5. 左侧选择实例，右侧查看版本、路径、监听地址、端口、进程状态与连接状态，独立启动/停止/重启。日志、数据目录和实例设置入口位于详情区。

例如以下布局可发现程序，并通过扫描找到已有集群：

```text
PostgreSQL/
    PgHatch.exe
    PostgreSQL/bin/
    PGData/main/
    PGData/test/
```

扫描最多向下 3 层、检查 256 个目录，遇到集群便停止进入其内部；不跟随目录链接。默认附近范围仅加入常见 PGData/data 目录，手动选择范围时只扫描所选根目录。扫描不初始化、不改写配置、不注册、不启停。备份识别属于保守的名称提示，仍需使用者核对目录用途。

空列表提供创建、导入、扫描引导，采用内嵌界面，不在每次启动弹出向导。

创建期间，表单返回主窗口后显示“正在初始化 PostgreSQL 数据目录……”，冲突的配置操作暂时禁用，窗口仍可重绘和移动。没有初始化取消按钮；退出管理器会等待 initdb 和配置保存结束，超时则由进程执行器终止并回收初始化子进程。失败时显示官方工具诊断并保留目标目录供检查，不自动清空。导入、扫描及已有实例的 PGDATA 不会套用新默认规则。

程序管理窗口按内容和显示器工作区调整大小；常规屏幕默认完整显示主要控件，极小工作区保留滚动回退。下载窗口使用普通状态标签，没有日志式空白滚动区；来源 URL 与 SHA-256 仍可完整换行、选择和复制，失败详情通过原生错误提示显示。

开发验证可在 Release 构建后运行：

```powershell
dotnet run --project tests/PGTray.IconChecks -c Release --no-build -- artifacts/dev/ui-checks
dotnet run --project tests/PGTray.IconChecks -c Release --no-build -- --creation-check 'D:\PostgreSQL\PostgreSQL\bin' artifacts/dev/creation-checks
```

第一项包含三种语言 × 100%/125%/150%/200% 的控件和字体比例布局检查，以及小工作区回退；它不等同于四档系统缩放与跨屏实机验收。第二项使用隔离 PGDATA 运行真实 initdb，通过原生消息循环心跳检查响应，同时验证退出等待、双输出管道和超时回收，不启动服务器。检查保留有归属标记的测试目录供核对，切勿把真实 PGDATA 用作测试根目录。

## 界面语言与品牌展示

支持简体中文（zh-CN）、English（en-US）、繁體中文（zh-TW）。默认跟随 Windows 显示语言，未支持的语言使用英语。在 **更多 → 语言** 中选择后，下次启动生效；语言名称始终使用各自语言显示。

语言偏好单独保存在程序同目录的 `pghatch.ui.json`，不写入 PostgreSQL 实例配置，不改变 `pghatch.json` 的 schema。旧用户无需迁移或补充字段。偏好文件不存在时直接跟随系统，不自动生成文件；文件损坏时提示并保留原文件，修复后重启。配置目录入口也可用于找到这个文件。

主窗口右上角使用内嵌的现有透明高分辨率 Logo，通常显示为 100 个逻辑像素；较窄窗口中缩小或隐藏。PostgreSQL 原始日志、工具输出、路径、命令参数和技术标识符保留原文。Windows 自带文件选择器及系统消息框按钮继续遵循 Windows 自身的语言设置。

资源维护及验证方式见 [本地化说明](localization.md)；本轮执行结果与人工验收清单见 [验证报告](releases/v0.2.0/localization.md)。

## 启动配置和状态语义

**启动与重启都沿用 PGDATA 的原始配置，不传入覆盖端口和监听地址的 `-o` 参数。** 新建实例的本机默认值由 initdb 后生成的配置提供。导入实例原有的 `postgresql.conf`、`postgresql.auto.conf`、include 配置保持原样。

“实例设置”可修改显示名称、重新关联已注册程序（核对实际工具版本与 PG_VERSION），也可主动打开原始配置。手动编辑 port/listen_addresses 会影响下次启动或重启；开放非本机监听可能扩大可连接范围，同时受 pg_hba.conf 与防火墙约束。PgHatch 不自动编辑这些设置。

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

`pghatch.json` 位于 EXE 旁，旧 `pgtray.json` 的路径语义保持不变。同盘路径保存为相对路径，跨盘保留绝对形式；整体迁移需保留相对布局。不可写时明确报错，不静默改存到其他位置。配置字段兼容旧 PascalCase 和 camelCase；新写入仍使用 PascalCase。

- 缺少或 null 的 `Binaries`、`Instances` 视为空列表；缺失 SchemaVersion 按旧 schema 1 处理，可选 AutoStart / CreatedByPGTray 缺失或 null 默认为 false。空数组正常加载。
- 未知字段保留（包括程序、实例记录内的未知字段）。字段类型错误、重复字段/ID、无效端口、重要字段缺失和不支持的未来 SchemaVersion 会明确报错，禁止正常写入；不会悄悄丢弃记录。
- 实例引用丢失或程序目录失效时保留所有元数据，显示程序未关联/不可用，禁用启停。在“程序管理”注册合适版本，然后在“实例设置”重新关联；也可修复现有注册路径，保留其 ID。修复路径要求原主版本兼容。
- 保存先在内存序列化和验证，写同目录临时文件并 flush，再通过 Windows `File.Replace` 原子替换。保存按配置路径串行化；上一份有效配置保存为 `pghatch.json.bak`。异常时保留原配置，损坏配置不会覆盖有效备份。
- 保存前核对加载时的 SHA-256 指纹。外部编辑、另一配置存储对象的陈旧快照都不会被静默覆盖；请点击“重读配置”后重新操作。不要同时用外部编辑器和管理器保存配置。

配置读取失败时主窗口保留“打开配置文件”“配置目录”“重新读取”“从 .bak 恢复”“保留原文件并新建空配置”入口。只有明确确认才恢复；先验证备份语法、结构、版本和引用，再保留原文为 `*.preserved-*` 并替换。备份也损坏或含失联引用时报告原因，不循环恢复。若损坏的是尚未迁移的旧文件，恢复入口对应 `pgtray.json.bak`，旧文件仍然保留。选择空白配置也会先保存原文，不触及程序、PGDATA 或数据库进程。

程序管理中的“取消注册程序”只解除登记，不删除 bin/lib/share、数据、日志，也不停止服务器。有实例引用时拒绝操作并列出名称；先重新关联这些实例。失效且无引用的目录也可取消注册。取消后不会在下次启动自动加回，仍可发现并手动重新注册。

日志默认位于 EXE 旁 `logs/<instance-id>.log`。GUI 查看最近 64 KiB，保留本次会话最近操作的标准输出、错误输出和返回码。已有 logging_collector 的日志继续位于原配置位置，不自动合并或清理。

- 关闭主窗口：隐藏到托盘。双击托盘或再次运行 EXE 恢复窗口。
- “退出” / Ctrl+Q / 托盘“退出 PgHatch”：等待当前操作安全完成、取消轮询，只退出管理器，数据库继续运行。
- 托盘“停止全部已注册实例并退出”：确认后停止所有已注册实例，包括外部启动的已注册实例；任何停止失败会留在管理器中供检查。
- Windows 确认关机或重启时，PgHatch 会先等待当前实例操作结束，再并行尝试正常停止所有已注册实例（包括由外部启动的已注册实例），使用 `pg_ctl stop -m fast`，不强杀数据库、不删除 PGDATA。未注册的实例不会处理；配置无效或无法确认进程身份时不会冒险停止。普通退出仍让数据库继续运行，注销仅等待操作结束，不额外停止实例。
- 关机询问阶段不停止实例；如果关机被取消，管理器继续工作。确认关机后最多等待 30 秒收尾，并向 Windows 提供等待原因；系统强制结束、断电或时间不足仍可能打断，因此不能保证每次正常停止。最近的状态和失败诊断追加到配置根目录下 `logs/pghatch-session-end.log`，不在关机期间弹出确认框或错误框。

AutoStart 元数据继续保留，当前不执行开机或实例自动启动。

## 开发和真实集成测试

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project src/PGTray
dotnet run --project tests/PGTray.IntegrationTests -c Release -- --config-tests 'D:\PostgreSQL\PostgreSQL' 'artifacts/dev/config-tests'
dotnet run --project tests/PGTray.IntegrationTests -c Release -- 'D:\PostgreSQL\PostgreSQL' 'D:\PGTray-Test'
# 也可执行 .\scripts\Publish.ps1 生成带说明和校验值的 ZIP
dotnet publish src/PGTray -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/dev/manual-publish
```

集成测试仅在测试父目录下新建有唯一标识的目录，使用空闲端口和专用凭据，结束后只停止本次创建的集群，保留结果与日志，不递归删除。不要把真实 PGDATA 或其父目录作为测试目录。真实操作依赖现有 PostgreSQL 18 程序，不使用 Mock 替代这些测试；另有端点查询不可用的失败注入测试，报告中单独注明。

工程延续原分层：`PGTray` 原生 UI，`PGTray.Core` 配置/路径/工具/生命周期/发现，`PGTray.IntegrationTests` 真实回归。没有更换 GUI 框架或引入调度、DI、ORM 框架。

详细变更见 [CHANGELOG](../CHANGELOG.md)，本轮证据和待人工步骤见 [v0.2.0 验证记录](releases/v0.2.0/validation.md)。四档 DPI、跨屏拖动、托盘所有点击路径以及没有 Runtime 的 Windows 10/11 干净环境仍需逐项验证，不能以本机运行替代这些验收。当前完整真实版本测试为 PostgreSQL 18.6；17/18 主版本拒绝测试使用测试元数据，尚未安装第二套真实版本。

PostgreSQL 程序管理窗口提供[软件包目录及下载部署](postgresql-catalog.md)入口。目录联网失败时回退有效缓存或发行 XML；“下载 PostgreSQL”允许选择版本和空目录，依次下载、核对整个 ZIP 的 SHA-256、安全解压、验证并注册程序。可取消并安全重试；成功后自行选择是否创建实例。下载部署不会自动初始化或启动数据库，也不会覆盖已有程序目录。

不实现 SQL 编辑器、备份恢复、数据库升级或远程管理。

v0.2.0 验证记录与人工步骤见 [验证记录](releases/v0.2.0/validation.md)。应用、窗口和托盘已接入正式 PgHatch 品牌图标；主窗口额外内嵌高分辨率透明 Logo。内部目录和命名空间沿用 PGTray，JSON 的 CreatedByPGTray 字段与旧版协调对象名称有意保留，以兼容历史配置和运行行为。



