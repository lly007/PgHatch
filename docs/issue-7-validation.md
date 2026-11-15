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

错误匹配包含英文特征、本机实测的简体中文旧译，以及 PostgreSQL 翻译目录中的简体、繁体、日文和韩文对应特征。匹配采用精确错误短语，其他本地化形式或被混合编码破坏的短语会进入通用诊断。当前更明确的路径建议要求可识别的 `post-bootstrap` 上下文。中文旧译由本轮真实运行确认，其他译文已核对 PostgreSQL 翻译目录。[PostgreSQL 翻译源](https://github.com/postgres/postgres/tree/REL_18_STABLE/src/backend/po)

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

## 回归结果

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
