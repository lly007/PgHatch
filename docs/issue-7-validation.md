# Issue #7 — 程序路径预警与初始化诊断

验证日期为 2026-11-15，分支为 `fix/issue-7-initdb-path-diagnostics`，基线为 main `d3a223e3b993d3a582f6b6f1702fcf2c26b302d8`。本轮交付源码修复，供维护者审查及验收。

## 根因与证据范围

在本轮 Windows x64 环境中，同一份 PostgreSQL 18.6 的中文程序路径可触发真实 initdb post-bootstrap 字符编码失败。ASCII 程序路径配合中文 PGDATA 初始化成功。退出码 1 是 PostgreSQL 的实际失败结果，未发生外层等待超时。仅根据残留 `global/pg_control` 存在无法判断初始化成功。

本轮二进制输出的完整版本为 `postgres (PostgreSQL) 18.6`，中文致命错误为 `无效的 "UTF8" 编码字节顺序: 0xd6 0xd0`。维护者提供的英文等价错误为 `invalid byte sequence for encoding "UTF8"`。失败前输出 `performing post-bootstrap initialization ...`。

此前 PostgreSQL 18.3 实验通过 `initdb -L` 交换 share 路径，已观察到中文 share 路径与同类失败直接相关。这些证据支持 Windows PostgreSQL 程序及共享资源路径存在兼容性风险，具体供应商构建内部的字节转换位置仍待核查。风险会随版本、构建及路径字符变化。此前的实验记录保留在原验证文档中。[此前真实路径实验](final-reliability-review.md#中文路径确认的触发条件与尚未确定的机制)

`Chinese (Simplified)_China.936` 和 text search 配置回退到 `simple` 的提示也出现在本轮成功用例中。分类始终先依据进程退出状态。

## 创建前预警

- 检查创建表单真正选中的 PostgreSQL bin 路径，覆盖中文、日文、韩文、重音字符和其他非 ASCII 字符。验证期间冻结表单输入，保证确认的选择与后续创建使用的选择一致。
- 可选地执行同目录 `pg_config.exe --version --bindir --sharedir`。仅在版本完全匹配、报告的 bin 身份匹配、share 为绝对路径且存在 `postgres.bki` 时确认 share 路径。工具缺失、查询失败、超时、版本或 bin 不匹配均保留“无法确认实际 share 资源路径”状态。
- Windows 的 pg_config 可能报告 8.3 名称。只为风险检测将其展开成长路径，执行路径与 initdb 参数仍使用用户选定路径。share 检查保留报告路径的实际拼写。
- 风险提示使用原生 TaskDialog，提供“继续创建”和“返回修改”，默认返回修改。继续后只执行一次初始化，返回时留在编辑表单。取消确认期间只做只读校验与可选 pg_config 查询。
- PGDATA 和系统 locale 不参与此风险判断。导入、扫描、程序关联、启动及停止沿用各自入口。

## 失败分类与诊断

| 观测证据 | 用户提示 |
| --- | --- |
| initdb 超时 | 超时提示，保留真实退出码及超时标志 |
| initdb 非零退出，精确匹配 UTF8 非法字节错误，具有 post-bootstrap 上下文，且 bin 或已确认 share 含非 ASCII 字符 | 字符编码失败，说明当前程序资源路径可能存在兼容性问题，建议检查程序目录及使用新的空数据目录 |
| initdb 非零退出，匹配 UTF8 非法字节错误，但路径或阶段证据不足 | 字符编码失败，将程序资源路径列为待核查原因 |
| 其他 initdb 非零退出或调用异常 | 通用初始化失败提示，保留技术诊断 |
| initdb 返回 0 且未超时 | 继续配置写入与验收，locale 和 text search 提示不触发失败分类 |
| initdb 已成功，随后配置写入、验收、凭据清理或实例登记失败 | 继续使用 `InitializedClusterException`，提示已初始化目录及登记恢复信息 |

错误匹配包含英文特征、本机实测的简体中文旧译，以及 PostgreSQL 翻译目录中的简体、繁体、日文和韩文对应特征。匹配采用精确错误短语，其他本地化形式或被混合编码破坏的短语会进入通用诊断。当前更明确的路径建议要求英文 `post-bootstrap` 或补充核查的中文“正在执行启动脚本后续初始化”上下文。中文旧译由本轮真实运行确认，其他译文已核对 PostgreSQL 翻译目录。[PostgreSQL 翻译源](https://github.com/postgres/postgres/tree/REL_18_STABLE/src/backend/po)

`InitializationFailedException.Message` 只包含简短用户提示。`Diagnostic` 单独保留实际退出码、超时标志、PGDATA、bin、share 的确认状态及捕获输出。主窗口针对该异常显示原生错误提示，通过“查看技术诊断”打开可选择、滚动和复制完整捕获内容的 WinForms 窗口。技术诊断保留在本次异常的内存属性中。

诊断和异常消息会脱敏初始化密码的原文及 SQL 引号、反斜线转义形式。异常不保留可能含密码的内部异常，临时密码文件仍走既有 ACL 和清理流程。凭据清理失败保留实际残留位置提示。PGDATA 失败目录保留，产品没有自动删除、覆盖或重试行为。

## 控制台解码核查

先核查既有 `ProcessRunner` 原始字节缓冲区和 `TextDecoding.Decode()`，并使用已知字节样本验证。

| 样本 | 本机 ACP936 结果 |
| --- | --- |
| UTF-8 编码的中文、日文、韩文及 emoji | 严格 UTF-8 解码正确 |
| CP936 字节 `D6 D0 CE C4` | UTF-8 校验失败后回退 ACP936，得到“中文” |
| 同一流包含 UTF-8 中文与 CP936 中文 | 整流回退 ACP936，UTF-8 片段仍可能乱码 |
| 混合字节流包含英文 UTF8 致命错误，真实辅助进程退出 1 | stdout、stderr、退出码 1 及未超时状态保留，已知错误仍可分类 |

本轮保留现有通用解码实现，仅增加回归。逐片段猜测编码需要更广的输出机制设计和兼容性验证。尚存边界包括混合编码的本地化文字、当前系统 ANSI 代码页之外的纯字节输出，以及被乱码破坏的本地化错误特征。原有每流约 1 MiB 捕获上限也继续适用，诊断窗口复制全部已捕获内容。解码表现不参与成功判定，也不作为数据库损坏证据。

## 真实 PostgreSQL 隔离矩阵

操作系统为 `Microsoft Windows NT 10.0.28000.0`，Windows ANSI/OEM 代码页均为 936，测试控制台输出代码页为 65001。源程序来自本机既有 `D:\PostgreSQL\PostgreSQL`，只读核验了四个核心工具的实际版本。两套复制程序的 `bin/lib/share` 共 1,583 个文件 SHA-256 均与源逐项一致，实验后再次核对源和两套副本仍一致。本轮没有重新核验供应商整包 ZIP 的来源摘要。

关键文件摘要如下。

| 文件 | SHA-256 |
| --- | --- |
| `bin/initdb.exe` | `D59423EA6106821670EA7289B6D5476FEB8741D5FDC8537DE25F22DDA1F13532` |
| `bin/postgres.exe` | `8B8BF5D400487159E3E117053D3F5A89FAD0C740B310A3AC69E5720CE4F818E1` |

最终证据根目录为 `E:\PgHatch-Issue7-20261115\real-9309f79dec9b4871bb33ac923e514d96`，下表路径均相对此目录。每种组合分别执行直接 initdb 与 PgHatch 常规 `InitializeAsync`，各用独立的新 PGDATA。直接调用的真实退出码见表，PgHatch 结果一致，失败异常保留退出码 1 及 `TimedOut=false`。

| bin | 已确认 share | 直接调用 PGDATA | 真实退出码 | 超时 | 结果 |
| --- | --- | --- | --- | --- | --- |
| `program-ascii\bin` | `program-ascii\share` | `data-ascii\ascii-program-ascii-data-direct` | 0 | false | 成功 |
| `program-中文\bin` | `program-中文\share` | `data-ascii\nonascii-program-ascii-data-direct` | 1 | false | post-bootstrap UTF8 非法字节错误 |
| `program-ascii\bin` | `program-ascii\share` | `数据目录\ascii-program-nonascii-data-direct` | 0 | false | 成功 |

对应 PgHatch 数据目录使用相同父路径与用例名，将末尾 `-direct` 换为 `-pghatch`。`evidence.json` 记录完整路径、版本、环境代码页、全部文件哈希、真实退出码和结果，文本日志在写入磁盘前脱敏。

矩阵只在该新目录内创建数据文件，初始化参数沿用 UTF8、SCRAM、密码文件和 `--no-clean`，保留默认 locale。未向本轮矩阵加入 `-L`。本组没有启动服务器，所有失败目录保留，临时凭据已清理。

## 原始三个修复提交的回归结果

| 检查 | 结果 | 类型及范围 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | 独立安装并校验 SHA-512 的 .NET SDK 10.0.404，项目目标 .NET 10 |
| `--initialization-tests` | 74 PASS | 模拟工具及 CommandResult 夹具，包含路径查询核验、三语言分类、密码脱敏、导入、警告成功、配置写入失败、调用前异常及失败保留 |
| `--decoding-tests` | 8 PASS | 已知字节及辅助进程，验证混合编码边界和退出码保留 |
| `--initialization-checks` | 103 PASS | 原生 WinForms / TaskDialog、表单确认及既有三语言检查，诊断窗口含 96/144/192 模拟 DPI 和超过 1 MiB 的内容复制检查 |
| `--initialization-real` | 30 PASS | 上述 6 次真实路径初始化及哈希核对，同时复用既有初始化验收、登记冲突和凭据清理故障注入 |
| 既有集成入口 | 75 PASS | 真实 PostgreSQL 18.6 的创建、导入、SQL、生命周期、目录安全及失败处理回归 |

30 PASS 中的故障注入明确包含真实 initdb 成功后的外部配置编辑、验收失败、凭据删除失败及非空目录导致的真实 initdb 失败。这些测试与自然发生的中文程序路径复现分别记录。模拟夹具、字节样本和 UI 检查的数量也分别记录。

本地日志位于 `artifacts/dev/issue7-{build,diagnostics,decoding,ui,real}-final.log` 和 `artifacts/dev/issue7-regression.log`。UI 预览位于 `artifacts/dev/issue7-ui-final/`，原生提示截图及大诊断窗口已检查。既有真实集成目录为 `E:\PgHatch-Issue7-20261115\run-20261115-101652-d38f832e`。本轮矩阵与集成测试的 PostgreSQL 及辅助进程均已结束。

复跑入口如下，测试父目录应为本轮实验专用目录。

```powershell
dotnet build PGTray.sln -c Release
tests/PGTray.IntegrationTests/bin/Release/net10.0/PGTray.IntegrationTests.exe --initialization-tests E:\PgHatch-Issue7-Tests
tests/PGTray.IntegrationTests/bin/Release/net10.0/PGTray.IntegrationTests.exe --decoding-tests
tests/PGTray.IconChecks/bin/Release/net10.0-windows/PGTray.IconChecks.exe --initialization-checks artifacts/dev/issue7-ui
tests/PGTray.IntegrationTests/bin/Release/net10.0/PGTray.IntegrationTests.exe --initialization-real D:\PostgreSQL\PostgreSQL E:\PgHatch-Issue7-Tests
```

## 修改文件

| 文件 | 内容 |
| --- | --- |
| `src/PGTray.Core/InitializationPaths.cs` | bin/share 风险状态及只读长路径展开 |
| `src/PGTray.Core/PostgresTools.cs` | 可选资源路径查询和初始化结果、用户提示、技术诊断分离 |
| `src/PGTray.Core/InitializationFailedException.cs` | 专用异常、谨慎分类和密码脱敏 |
| `src/PGTray.Core/Resources/Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx` | 英文、简体、繁体文案 |
| `src/PGTray/Dialogs.cs` | 创建确认流程及选择稳定性 |
| `src/PGTray/InitializationDialogs.cs` | 原生路径确认、简短错误提示与诊断窗口 |
| `src/PGTray/MainForm.cs` | 初始化异常的专用展示入口 |
| `tests/PGTray.IconChecks/InitializationPathChecks.cs`、`Program.cs` | 三语言路径确认、错误提示、诊断复制及原生截图 |
| `tests/PGTray.IntegrationTests/InitializationDiagnosticsTests.cs` | 模拟工具和异常分类回归 |
| `tests/PGTray.IntegrationTests/TextDecodingChecks.cs` | 已知字节及真实辅助进程退出结果回归 |
| `tests/PGTray.IntegrationTests/RealInitializationChecks.cs` | 同源哈希核验、真实 PostgreSQL 路径矩阵与失败阶段回归 |
| `tests/PGTray.IntegrationTests/Program.cs` | 新测试入口及模拟工具入口 |
| `tests/PGTray.IntegrationTests/FinalReviewTests.cs`、`ReliabilityTests.cs` | 复用既有隔离复制与初始化失败检查，调整方法可见性 |
| `CHANGELOG.md`、`docs/README.md`、`docs/issue-7-validation.md` | 开发中变更说明、索引及本验证记录 |

## 维护者验收

维护者可在目标用户 Windows 环境确认预警默认按钮、继续与返回操作、真实初始化失败提示及技术诊断复制。当前版本元数据仍为 1.1.1，后续正式发布再推进版本号及制品生成。管理员密码修改与恢复另行开发。已有正式发行及历史文档继续保留。

## 发布前补充 — 程序注册阶段预警

本补充以维护者已审查的 `e5476d5db18b3023a134067a055eb9cc803e0fe9` 为基线，开发及验证日期为 2026-11-15。维护者已确认原修复范围的真实 GUI 验收通过，本补充另行交付最后验收。正式发布流程等待补充验收完成后恢复。

### 入口与确认时机

| 入口 | 检查与返回行为 |
| --- | --- |
| BinaryForm 手动注册 | 先核验所选程序并读取实际 bin/share，再显示注册提醒。返回后保留已有登记，继续时提交已经确认的 bin |
| BinaryForm 修复路径 | 检查替换程序的 bin/share，再执行既有修复确认和核心关联核验。返回后原登记文件字节保持一致 |
| DiscoveryForm 扫描后导入 | 在批次保存前，针对实际将新增登记的程序预先确认。同一程序供多行使用时只确认一次，返回时结束整批导入。已有登记的程序继续走既有导入机制 |
| DownloadForm 下载部署 | 下载前检查目标目录。准备包后，在文件提交和自动登记前，核验实际 bin 以及可映射到最终位置的 share。下载前已经确认的目标风险覆盖本次操作，避免相同风险重复提示 |

上述检查共用 `InitializationPaths.HasNonAscii`、`InspectInitializationPathsAsync()` 和原生风险提示页面。注册、部署分别使用相应的三语言说明及“继续注册”“继续下载和部署”按钮，默认选择“返回修改”。实际 share 无法确认时继续明确显示未知状态。拟部署目录在下载前以“计划部署 PostgreSQL 的目标目录”展示。

下载入口通过 `PackageDeployment.DeployAsync()` 新增的可选确认回调，在原有校验和提交步骤之间完成只读检查。回调看到的路径指向最终目标。已确认的暂存 share 只有位于本次 payload 内时才映射到目标位置，其他情况保持未知。拒绝回调时清理本次创建的暂存文件，已存在的空目标仍保留原位置。既有后台调用可省略回调，继续使用原来的核心部署流程。

UI 在异步检查期间冻结有关选择，并使用已经捕获或检查过的路径提交。扫描导入在等待前捕获全部选中行和程序选择。下载同时捕获目标和包版本，后续控件内容变化无法重定向此次部署。

附近候选列表、只读扫描、已登记程序刷新和现有实例启动停止各自沿用原入口。注册预警只在显式注册、修复或实际新增程序登记时运行。创建实例前的确认继续独立存在，PGDATA 字符不参与程序路径风险判断。

### 中文阶段最小修复

原实现仅识别英文 `post-bootstrap` 阶段，以下给定样本的阶段条件为 false，因而只能得到普通 Encoding 分类。

```text
正在执行启动脚本后续初始化 ...
无效的 "UTF8" 编码字节顺序: 0xd6 0xd0
```

补充识别准确的中文阶段短语“正在执行启动脚本后续初始化”。同时具备非零退出、该阶段、已知 UTF8 错误及非 ASCII bin 或已确认 share 时，分类为 `ProgramPathEncoding`。ASCII 程序配合中文 PGDATA、阶段短语不完整或缺少已知错误时，仍按原证据条件保守处理。

该样本由维护者提供，本补充使用受控结果进行定向验证。此前真实 18.6 路径矩阵继续作为实际 PostgreSQL 复现证据。

### 补充验证

| 检查 | 结果 | 验证范围 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | .NET SDK 10.0.404，项目目标 .NET 10 |
| `--registration-checks` | 63 PASS | 实际 WinForms 注册、修复、扫描、导入及下载按钮路径，使用模拟 PostgreSQL 工具与受控 HTTP 响应 |
| `--initialization-tests` | 89 PASS | 包含新增 15 项中文阶段正反例，原有异常分类、脱敏及保留目录检查继续通过 |
| `--initialization-checks` | 103 PASS | 既有创建预警、技术诊断窗口及三语言完整性检查 |
| `--download-tests` | 78 PASS | 既有真实环回 HTTP 下载和部署夹具，覆盖校验、取消、目录保护、登记失败及暂存清理 |
| 既有真实 PostgreSQL 集成入口 | 75 PASS | PostgreSQL 18.6 真实创建、导入、SQL、启动停止、刷新、配置保护及初始化失败处理 |

原生注册与部署提示分别检查英文、简体和繁体文案，并观察实际 TaskDialog 截图。手动注册的三语言按钮流程均覆盖 ASCII、返回和继续。修复流程运行了既有原生修复 MessageBox。下载测试核实中文目标返回时包请求数为零，继续后只有一次提醒。另用包内部中文目录验证目标为 ASCII 时也能在提交前提醒，并保留正确的最终 bin/share 路径。控件值在提示回调中被刻意修改，提交仍使用已确认路径。

63 项 UI 检查使用 PE 程序夹具执行正常版本、配置读取及停止状态响应，通过 HTTP 处理器提供受控 Catalog 和 ZIP 字节，运行生产下载校验、解压及配置保存代码。它们属于原生 UI 与受控工具验证。75 项集成检查使用真实 PostgreSQL 18.6，其真实 SQL、启动和停止结果单独记录。

本地日志如下。

- `artifacts/dev/issue7-registration-build.log`
- `artifacts/dev/issue7-chinese-stage.log`
- `artifacts/dev/issue7-registration-ui.log`
- `artifacts/dev/issue7-registration-existing-ui.log`
- `artifacts/dev/issue7-registration-download.log`
- `artifacts/dev/issue7-registration-real-regression.log`

UI 证据目录为 `E:\PgHatch-Issue7-Registration-20261115\ui-registration-f91764c3f7fc4613a12f80ce2cb7572a`。真实集成证据目录为 `E:\PgHatch-Issue7-Registration-20261115\run-20261115-121739-790dbe6b`。新增测试仅操作各自新建的隔离目录。

### 补充修改文件及边界

- `src/PGTray/Dialogs.cs`、`DiscoveryForm.cs`、`DownloadForm.cs` 接入程序注册及部署预警，捕获选择并冻结相关输入。
- `src/PGTray/InitializationDialogs.cs` 复用提示页面，增加注册和部署文案上下文。三套 `Strings*.resx` 增加对应资源。
- `src/PGTray.Core/PackageDeployment.cs` 增加提交前的可选路径确认回调，沿用既有下载校验、目录提交和登记步骤。
- `src/PGTray.Core/InitializationFailedException.cs` 增加准确中文阶段短语识别。
- `tests/PGTray.IconChecks/RegistrationPathChecks.cs`、`Program.cs` 以及 `InitializationPathChecks.cs` 的截图复用入口覆盖新增 UI 路径。
- `tests/PGTray.IntegrationTests/RegistrationFixture.cs`、`Program.cs` 和 `InitializationDiagnosticsTests.cs` 提供工具夹具及中文阶段定向回归。

本补充的原生界面检查在当前 Windows x64 桌面完成，新增注册流程的真实 ARM64 设备操作和其他系统代码页仍待平台验收。供应商 ZIP 的真实在线下载由此前记录支撑，本次新增部署 UI 使用受控包验证。下载前只能确认目标目录，包内部路径须待包准备后识别。混合 UTF-8/CP936 输出及其他本地化阶段短语继续适用前文记录的边界。
