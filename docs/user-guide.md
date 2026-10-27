# PgHatch

**PostgreSQL, the App Way.**

A portable PostgreSQL instance manager for Windows.

**当前版本号 / 正式发布版本：1.0.1。** 发行状态见 [v1.0.1 Release](https://github.com/lly007/PgHatch/releases/tag/v1.0.1)。工作区的可靠性加固尚未发布，未修改正式制品或版本号；旧 v0.2.0 不包含后续全部功能。

C# / .NET 10 / Windows Forms · Windows x64 / ARM64 候选发行 · PerMonitorV2。可下载部署 PostgreSQL，也可使用用户提供的程序；不捆绑 PostgreSQL，不安装 Windows Service，不修改系统 PATH，不要求管理员权限。没有第三方 NuGet 依赖。ARM64 已交叉构建和静态检查，真实 ARM64 运行尚未验证。

## 运行与升级

统一发布命令：`pwsh -NoProfile -File scripts/Publish.ps1`。一次生成 x64 / ARM64 × standalone / framework 四个 Release 单文件候选包，位于全新的 `artifacts/candidates/run-<UTC>-<id>/`。也可通过 `-Architecture x64 -Mode framework` 选择子集。所有配置显式禁用裁剪、Native AOT 和 ReadyToRun。完整命令、包结构和验收方法见 [发行工程说明](releasing.md)。

Standalone 无需预装 .NET；Framework-dependent 需要与包架构对应的 **.NET 10 Desktop Runtime**，仅普通 .NET Runtime 不足以运行 WinForms。解压到当前用户可写目录运行，保留随包许可文件。原生库提取取决于实际 .NET 宿主与 bundle 内容；本轮 .NET 10 宿主集成运行库，x64 实测没有 bundle 文件提取，不能一概假设首次运行必然解压。

旧封版 `artifacts/PgHatch-v0.2.0-win-x64/`、v1.0.0 及其 ZIP 保持原样，不作为 1.0.1 输出目录。正式包在独立目录重新构建，下载和校验信息见 [本轮发行说明](releases/v1.0.1/release.md)。

PGTray 正式更名为 PgHatch。升级时先退出旧管理器，把 `PgHatch.exe` 放到原目录，保留 `pgtray.json`、备份和日志。首次读取会验证旧配置，生成 schema 2 的 `pghatch.json`，原文件原样保留；不会启停数据库。新旧文件同时存在时只使用新文件，不合并、不反复迁移。新文件损坏时进入恢复界面，不自动回退到旧文件。旧版与新版共用原单实例 Mutex / 唤醒事件，避免同时操作；如旧版正在运行，请先退出再启动新版。

## 使用

1. **程序管理**：通过“下载 PostgreSQL”选择 Catalog 版本和空的独立目录，依次下载 ZIP、校验完整 SHA-256、安全解压、验证工具并自动注册；目前 Catalog 仅提供 Windows x64 PostgreSQL 包。也可手动注册程序根目录或 `bin`，检查 pg_ctl、postgres、initdb、pg_isready 的版本及可执行文件架构一致性。下载、部署和注册不创建或启动实例。可注册多套程序；“发现附近程序”只提供候选路径，必须点击“注册程序”确认，启动时不会自动注册。
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

资源维护及验证方式见 [本地化说明](localization.md)；本轮三语言布局验证见 [1.0.1 发行说明](releases/v1.0.1/release.md)，基础功能验收见 [1.0.0 验收记录](releases/v1.0.0/validation.md)。[v0.2.0 本地化验证](releases/v0.2.0/localization.md) 保留为历史记录。

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

- 初始化拒绝已有内容、已有或疑似 PGDATA 的任何子目录，以及经过符号链接/junction、尾随空格/句点、设备路径等无法明确判定的路径。通过 Windows 驱动器类型拒绝 UNC、映射网络盘及未知或不可用磁盘，新建集群限可确认的本机固定或可移动磁盘目录。
- 导入目录按真实路径去重；初始化验证在链接解析前执行，避免规范化掩盖风险。
- “移除配置”只移除登记，数据、日志和数据库运行状态不变；没有删除数据库文件的入口。
- 初始密码只通过当前用户 ACL 保护的临时文件交给 initdb，不写入 JSON、日志或命令参数。单独删除密码文件失败时仍尝试删除专用临时目录；确有残留时报告路径及人工清理方式，同时保留原始初始化错误。请限制残留位置的访问并处理提示，不要分享文件内容。
- initdb 失败保留专用目标目录供检查，不自动重置或清理。
- initdb 成功但集群验收、临时凭据清理或实例登记失败时，单独报告“已创建但尚未登记”及完整 PGDATA 路径。解决错误后重读配置，再导入现有集群；重复新建不会覆盖已有数据，保存冲突不会覆盖外部配置。
- 已有网络存储实例可以只读检查并明确导入，界面提示存储风险。映射盘在 Windows 能提供映射关系时解析为 UNC 来比较身份；不同服务器别名、DFS、不同共享入口的物理等价性无法全面证明，不应靠不同路径表示重复登记同一资源。
- 旧配置中的稳定映射盘路径与相应 UNC 按同一身份规则核验，符号链接/junction 则单独拒绝。管理器保留相同登记在本进程中的可信映射，重读相同记录不会静默接受映射改变；重启前未保存的映射关系无法推断，需人工核对。此兼容修复不增加或认证网络数据库启停/运行支持，新建网络集群的限制保持不变。
- 本轮沿用 initdb 的数据校验和默认值与系统区域设置/排序规则提供程序，不追加改变这些默认行为的参数；具体结果以所用 PostgreSQL 版本的 initdb 输出为准。
- 为避免错误操作其他集群，当前版本拒绝配置中的 data_directory 指向所选 PGDATA 之外的分离布局。

## 配置、日志与退出

### 同主版本维护更新与程序切换

18.4 → 18.5 属于同主版本维护更新，17.x → 18.x 则是主版本升级。PgHatch 只帮助完成前一种程序关联，不提供 `pg_upgrade`、数据迁移或扩展迁移工具。

推荐先下载并注册到独立的新程序目录。下载成功不会改变已有实例的关联；先停止原程序及目标程序关联的所有受影响实例，在“实例设置”选择兼容的新程序，再明确保存，随后自行启动验证。PGDATA 不移动、不重新初始化，旧程序不自动删除。切换前应做好数据备份，并查阅发行说明，确认所用扩展 DLL 与新维护版本兼容；工具版本一致不等于所有第三方扩展兼容。

程序登记使用稳定 ID，实例保存对该 ID 的引用。同主版本、甚至完整版本相同但路径不同的程序始终独立；不合并登记，不按最大版本或 `latest` 自动选择，不在某一路径失效时静默迁移实例。改变程序登记的路径会影响引用该登记的全部实例；仅在实例设置重新关联则只改变明确选择的那个实例，但仍要核验相关程序的其他实例均已停止。

如果手动整体替换原路径的程序，必须先停止所有使用该目录的实例。替换完成后点主窗口“刷新”，或在程序管理中选中登记项后“修复所选路径”。程序会检查 postgres、pg_ctl、initdb、pg_isready 的实际版本和可执行文件架构，再持久保存版本元数据，保留登记 ID 和实例引用。定时轮询会发现关键程序文件变化并提示重新检查；它不会静默接受版本变化。缺文件、混装版本、主版本不符或无法确认实例已停止时拒绝更新。

### 程序异常时的受限停止与恢复

不要在数据库运行期间覆盖程序文件。若已意外覆盖，启动和重启保持禁用。仅当登记异常且服务器身份已核验运行时，原“停止”按钮显示“应急停止”。身份不明或没有完整兼容工具时入口保持禁用，状态详情给出人工排查原因。

应急停止检查四个核心工具的完整版本及架构一致性、PG_VERSION、原始 PGDATA 身份，并交叉核对真实 Windows PID、进程启动时间、程序完整路径及绝对 `-D` 数据目录参数，检查其他 postgres 进程是否使用同一数据目录。无法访问的进程或相对数据路径不能被安全排除时会拒绝操作；仅 PID 文件、端口占用和相同进程名均不足以授权停止。

优先使用原路径的完整兼容工具。原目录缺失或混装时，仅列出已登记且重新实际核验的兼容程序，显示完整版本及路径，由用户明确选择；即使只有一个备用目录也不自动选中。执行前重新检查。完整但跨主版本的原目录替换会直接拒绝。备用工具只用于本次停止，不改变实例关联。

停止只执行 `pg_ctl stop -m fast -w`，超时不强杀服务器或控制器，不删除 PID 文件，不修改数据库配置。命令结果和实际状态分别检查，失败、超时或无法确认时不继续自动恢复。应急停止成功后，程序登记仍保持异常；请停止同一程序关联的全部实例，再单独执行“刷新”或“修复所选路径”。同路径、同主版本且工具完整一致时更新实际版本，保留登记 ID、PGDATA、名称、端口及其他配置；有任一关联实例仍运行或状态不明时拒绝修复。程序路径变更必须明确选择目标，跨主版本始终不能直接重关联。

恢复不会自动启动。用户确认程序版本和路径后自行启动，再用真实 SQL 核验数据库、表及原有数据。PG_VERSION 和核心文件检查仅确认基本兼容性，不代表完整数据库健康检查。修复或保存失败保留数据及原有有效配置；先按错误提示处理，再重读配置后重试或导入已有集群。程序无法恢复或身份不明时，请由管理员按 PostgreSQL 官方流程核对并正常停止，不删除 postmaster.pid，不使用强制终止。

如果外层停止请求已超时但 pg_ctl 控制器尚未退出，PgHatch 保留该实例的操作锁并继续观察控制器自然退出、排空输出和回收句柄。此时可以只读刷新实际服务器状态，但不能修复程序、重新关联或移除该实例；停止状态也不会绕过仍在执行的控制器。普通退出会等待这些已接受的操作收尾。控制器自身的 `-t` 等待到期通常会自然退出，但遇到系统 I/O 卡住时不能保证退出时间；无法确认控制器退出时保持阻止，不强杀 PostgreSQL 来解除限制。

控制器的操作系统退出确认与诊断输出收集分别记录。进程已确认退出后，输出读取失败只报告诊断不完整，保留已收集内容并回收资源，不单独永久锁住实例；无法确认进程退出时仍保守阻止。确认数据库停止还要求数据目录和版本标记可读、PID 文件明确不存在以及其他进程检查通过，权限或 I/O 错误不会当作“PID 文件不存在”。

18.4 → 18.5 原则上可继续使用原数据目录；17 → 18 必须保留原 PGDATA，并恢复兼容程序或另行规划正式的 `pg_upgrade` / 导出导入流程。替换程序或改写登记都不能完成主版本升级。PgHatch 不自动升级真实实例，也不自动恢复未知数据目录。

### 排序规则版本不匹配的人工排查

依据 PostgreSQL 官方的 [Collation Support](https://www.postgresql.org/docs/current/collation.html)、[ALTER COLLATION](https://www.postgresql.org/docs/current/sql-altercollation.html) 与 [ALTER DATABASE](https://www.postgresql.org/docs/current/sql-alterdatabase.html)。以下说明面向提供相关函数的现代 PostgreSQL 版本，其他版本应查阅对应版本文档。

排序规则的**实际版本**来自当前操作系统或 ICU 等提供程序，**记录版本**保存在数据库系统目录中。系统或依赖库更新后，两者可能不同，排序和比较规则变化会使依赖旧排序结果的索引或其他对象存在风险。警告并不证明索引已经损坏；没有看到警告也不能证明没有风险，尤其是提供程序不能报告版本的情况。Windows 的版本报告能力也受排序规则名称和提供程序限制，不能套用 macOS 版本判断。

先备份并确认目标数据库、提供程序、系统/ICU 变更历史。可由具备权限的管理员在每个相关数据库中执行以下只读检查：

```sql
SELECT oid::regcollation AS collation, collprovider,
       collversion AS recorded_version,
       pg_collation_actual_version(oid) AS actual_version
FROM pg_collation
WHERE collversion IS DISTINCT FROM pg_collation_actual_version(oid);

SELECT datname, datcollversion AS recorded_version,
       pg_database_collation_actual_version(oid) AS actual_version
FROM pg_database
WHERE datcollversion IS DISTINCT FROM pg_database_collation_actual_version(oid);
```

NULL 表示缺少可用版本信息等情况，不能将 NULL 当成“已证明兼容”。`pg_collation` 的检查范围是当前数据库；数据库默认排序规则也要单独检查。可参考官方 `ALTER COLLATION` 文档的 `pg_depend` 查询列出显式排序规则的依赖对象，并进一步核对索引定义、数据库默认排序规则及其他受影响对象；单一依赖查询不能保证枚举所有间接影响。

确认影响范围、锁和停机要求后，按官方建议重建所有受影响对象，索引通常使用经过评估的 `REINDEX` 方案；其他对象按实际依赖处理。完成重建并核对结果后，才考虑由管理员执行相应 `ALTER COLLATION ... REFRESH VERSION` 或 `ALTER DATABASE ... REFRESH COLLATION VERSION`。这两个命令只刷新记录的版本，**不会重建索引，也不会验证对象是否已正确重建**。不要先刷新元数据来掩盖未解决的问题。

PgHatch 不自动识别日志中的排序规则警告，不执行 REINDEX 或刷新版本元数据。对复杂扩展、默认排序规则依赖或无法确定影响范围的数据库，应先由数据库管理员制定维护方案。

`pghatch.json` 位于 EXE 旁，旧 `pgtray.json` 的路径语义保持不变。同盘路径保存为相对路径，跨盘保留绝对形式；整体迁移需保留相对布局。不可写时明确报错，不静默改存到其他位置。配置字段兼容旧 PascalCase 和 camelCase；新写入仍使用 PascalCase。

- 缺少或 null 的 `Binaries`、`Instances` 视为空列表；缺失 SchemaVersion 按旧 schema 1 处理，可选 AutoStart / CreatedByPGTray 缺失或 null 默认为 false。空数组正常加载。
- 未知字段保留（包括程序、实例记录内的未知字段）。字段类型错误、重复字段/ID、无效端口、重要字段缺失和不支持的未来 SchemaVersion 会明确报错，禁止正常写入；不会悄悄丢弃记录。
- 实例引用丢失或程序目录失效时保留所有元数据，显示程序未关联/不可用，禁用普通启动和重启。仍在运行的实例仅在满足上文全部核验条件时提供受限应急停止，无法确认则拒绝。在“程序管理”注册合适版本，然后在“实例设置”明确重新关联；也可修复现有注册路径，保留其 ID。修复路径要求原主版本兼容。
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

详细变更见 [CHANGELOG](../CHANGELOG.md)，可靠性加固的证据、限制和人工步骤见 [可靠性验收报告](reliability-hardening.md)。历史证据见 [v0.2.0 验证记录](releases/v0.2.0/validation.md)。四档系统 DPI、跨屏拖动、托盘所有点击路径以及没有 Runtime 的 Windows 10/11 干净环境仍需按需验证，不能以控件自动化或本机运行替代这些验收。

PostgreSQL 程序管理窗口提供[软件包目录及下载部署](postgresql-catalog.md)入口。目录联网失败时回退有效缓存或发行 XML；“下载 PostgreSQL”允许选择版本和空目录，依次下载、核对整个 ZIP 的 SHA-256、安全解压、验证并注册程序。可取消并安全重试；成功后自行选择是否创建实例。下载部署不会自动初始化或启动数据库，也不会覆盖已有程序目录。

不实现 SQL 编辑器、备份恢复、数据库升级或远程管理。

v0.2.0 验证记录与人工步骤见 [验证记录](releases/v0.2.0/validation.md)。应用、窗口和托盘已接入正式 PgHatch 品牌图标；主窗口额外内嵌高分辨率透明 Logo。内部目录和命名空间沿用 PGTray，JSON 的 CreatedByPGTray 字段与旧版协调对象名称有意保留，以兼容历史配置和运行行为。



