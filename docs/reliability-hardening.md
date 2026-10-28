# v1.0.1 基线可靠性加固验收报告

日期：2026-10-27（Asia/Shanghai）。本报告对应本地未提交修改，不是新的发行版本。

本文保留首轮加固验收记录。随后针对中文路径与超时控制器的最后复核、追加修复和最新结果见 [最后定向复核报告](final-reliability-review.md)。

## 基线与设计审查

执行前工作区干净，分支 `main`，HEAD `f1a23df55d9369299c8b2615748d13f33ae03b4e`，标签 `v1.0.1`，与 `origin/main` 一致。版本号仍为 1.0.1，未修改标签、配置 schema 或正式制品，没有 push、Release 或发布操作。

原模型已经将 `BinaryInstallation.Id` 与 `InstanceConfig.BinaryId` 分开。程序按规范化路径登记，不按主版本或完整版本合并；因此本轮复用 schema 2 和稳定 ID。原来的 `RepairBinaryAsync` 已支持同主版本更新元数据及原子保存，但异常登记会在生命周期入口统一阻断停止；重新关联主要只检查选中实例；停止确认也需要补充真实进程身份检查。

复用原有生命周期协调器、配置保存锁、文件指纹冲突检测、原子替换和备份机制。未修改 `ConfigStore.cs`、`Directory.Build.props` 或数据库默认行为，未新增第三方运行时依赖。

| 状态/操作 | 前置条件及结果 |
| --- | --- |
| 健康登记 | 普通启动、停止、重启沿用原流程；没有应急入口 |
| 登记异常，身份不明/无完整兼容工具 | 保留 Unknown 与诊断，禁用应急停止；启动/重启仍禁用 |
| 登记异常，真实进程身份核验通过 | 优先原路径；原工具不完整/混装时列出实际核验的已登记备用工具，由用户选择完整版本及路径 |
| 应急停止 | 再次核验工具、PG_VERSION、路径、进程身份；仅 fast/wait 正常停止；命令超时不杀控制器或数据库 |
| 命令失败/超时 | 保留复查得到的实际状态和诊断，不自动更新登记或切换程序 |
| 已停止但程序异常 | 仍保留异常登记，独立执行程序重新核验；停止成功不代表程序健康 |
| 同路径、同主版本恢复 | 所有关联实例均确认停止，重新核验完整工具后更新实际版本；保持 ID 与实例配置，不启动 |
| 改路径/改关联 | 用户明确指定；核验受影响程序关联的全部实例停止；修改登记路径和修改单个实例关联是不同操作 |
| 跨主版本/修复失败/配置冲突 | 拒绝提交，保留 PGDATA、原关联与有效配置 |

真实 Windows 身份检查同时使用 PID 文件中的数据路径和启动时间、进程创建时间、实际可执行文件路径、命令行绝对 `-D`/`--pgdata`，并排查其他 postgres 服务器进程。无法读取的活进程、无法定位 PGDATA 的其他服务器进程或相对数据路径会使检查保守失败。已知 fork 子进程和只读版本/配置查询不被当成另一台服务器。执行停止期间以只读共享句柄固定四个核心 EXE，阻止它们在核验和使用之间被写入或替换。

## 修改文件与源码位置

下列路径相对仓库根目录；行号为本轮最终源码的入口位置。

| 文件/入口 | 修改目的 |
| --- | --- |
| `src/PGTray.Core/InstanceManager.cs`：`RepairBinaryCoreAsync`、`ReassociateAsync`、`RequireStoppedAsync` | 全部受影响实例互斥核验；停止确认加入真实进程扫描；目标完整版本必须已经核验；保留独立登记 |
| 同文件：`GetEmergencyCandidatesAsync`、`EmergencyCandidatesCoreAsync`、`EmergencyStopAsync` | 原目录优先、备用显式选择、执行前再次核验；只停止，不修复或启动；保留失败后的真实状态 |
| 同文件：`RefreshCoreAsync`、`OperateCoreAsync` | 异常入口资格缓存受轮询 epoch 限制；普通启动/重启拒绝未接受的维护版本变化 |
| 同文件：`AddInstanceAsync` | 区分 initdb 后验收失败与登记失败；报告完整保留路径及导入恢复方式，继续使用原子保存与冲突检测 |
| `src/PGTray.Core/ServerIdentity.cs:13`、`:28` | 新增受限的 Windows 运行/停止身份核验，无强杀、删除 PID 或配置修改 |
| `src/PGTray.Core/PostgresTools.cs:18`、`:167` | 核心 EXE 使用期间固定；临时凭据的两阶段清理和密码诊断脱敏；初始化成功后失败独立分类；小型内部故障注入点 |
| `src/PGTray.Core/SecretCleanup.cs:5` | 文件删除失败仍尝试目录清理；通过属性检查核实残留，访问失败不能被 File.Exists 的 false 隐藏 |
| `src/PGTray.Core/ProcessRunner.cs:8` | 增加超时不终止控制器的执行选项，应急停止使用该选项；其他调用保持既有行为 |
| `src/PGTray.Core/PathSafety.cs:24`、`:42`、`:64` | Windows 驱动器类型检查，映射盘展开为 UNC；重解析点目标继续展开映射；网络和未知类型拒绝初始化 |
| `src/PGTray.Core/InstanceDiscovery.cs` | 对识别出的网络存储候选附加风险说明，不改写数据 |
| `src/PGTray.Core/Models.cs`：`InitializedClusterException` | 初始化已完成但后续未完成的专用错误，携带 PGDATA 路径；无配置格式改变 |
| `src/PGTray.Core/AssemblyInfo.cs` | 仅向既有集成测试程序集开放小型内部故障注入点 |
| `src/PGTray/MainForm.cs`：`UpdateDetails`、`OperateAsync`、`RefreshVersionsAsync`、`AddAsync` | 复用停止按钮显示应急资格；确认版本/路径；刷新恢复列出全部受影响实例；网络导入提示 |
| `src/PGTray/Dialogs.cs`：`EmergencyToolForm`、程序修复按钮 | 备用程序无默认选择，显示完整版本与路径；修复前列出关联实例；未改变主窗口布局 |
| `src/PGTray/InstanceSettingsForm.cs` | 显式关联前列出原/目标程序受影响实例，取消不会提交 |
| `src/PGTray.Core/Resources/Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx` | 全部新增用户文本进入现有三语言资源，格式占位符一致 |
| `tests/PGTray.IntegrationTests/ReliabilityTests.cs` | 新增 `RunAsync`、`InitializationFailures`、`RunCrossMajorAsync`、`PathChecks`，真实服务器、SQL、程序替换和可重复故障注入 |
| `tests/PGTray.IntegrationTests/NetworkPathTests.cs:6` | 实际 SMB 映射盘、UNC 身份与符号链接权限测试；只创建/移除本轮临时映射，不创建共享或修改 SMB 服务 |
| `tests/PGTray.IntegrationTests/Program.cs` | 新测试入口与自然退出的超时辅助进程 |
| `tests/PGTray.IconChecks/EmergencyBindingChecks.cs:10`、同目录 `Program.cs` | 三语言下普通/异常/核验通过/操作中按钮绑定与备用程序无默认选择检查，不使用 Computer Use |
| `docs/user-guide.md` | 维护更新/主升级边界、恢复步骤、初始化与凭据异常、网络路径及官方排序规则排查说明 |
| 本报告 | 汇总证据、复现命令、跳过项目和限制 |

## 最终构建与测试汇总

`dotnet build PGTray.sln -c Release --nologo`：**0 警告，0 错误**。本地日志：`artifacts/dev/reliability-20261027/build.log`。

| 测试入口/证据日志（同一 artifacts 目录） | 性质 | 结果 |
| --- | --- | --- |
| `--config-tests` / `config-final-serial.log` | 既有配置与冲突/备份/关联回归 | 75 PASS |
| `--download-tests` / `download.log` | 既有回环下载、部署、损坏包与保存故障 | 78 PASS |
| `--catalog-tests` / `catalog.log` | 既有目录解析、缓存与回环服务 | 44 PASS |
| `--maintenance-tests` / `controlled-final.log` | 受控工具夹具、版本/架构元数据 | 15 PASS，**不是**真实 PostgreSQL 维护更新 |
| `PGTray.IconChecks` / `icons-final.log` | 既有图标、资源、本地化、控件、窗口/关机逻辑；含新增应急绑定 | 443 PASS，含新增 15 项，**不是**桌面实机操作 |
| 既有 PostgreSQL 集成入口 / `existing-real-final.log` | 真实 18.6 生命周期、SQL、多实例、扫描及故障路径 | 75 PASS |
| `--reliability-real` / `reliability-real.log` | 真实 18.3 → 18.6、SQL、应急停止、故障注入及路径组合 | 58 PASS，1 SKIP |
| `--cross-major-real` / `cross-major-real.log` | 真实 17.11 与 18.6 的集群/程序主版本边界、原数据恢复 | 6 PASS |

合计 **794 个断言通过，1 项跳过**。这不是 794 项真实数据库测试；各类证据按上表区分。下载/目录的既有测试在相关实现未变后没有重复执行；生命周期、配置、维护更新和控件绑定在最终实现上复核。

新增重点断言名称包括：

- `Complete compatible original path is preferred; other programs are never chosen by version`
- `Injected stop timeout cannot enter program recovery`
- `Duplicate registered PGDATA identity rejects emergency stop of a real server`
- `Explicitly chosen older maintenance tools stop the real server even when higher versions are registered`
- `Failed program recovery preserves PGDATA, external config and previous registration metadata`
- `Registration failure reports full preserved PGDATA and import guidance; external JSON is not overwritten`
- `Directory cleanup still runs and removes credentials when individual password-file deletion fails`
- `Passive controller timeout reports uncertainty without Process.Kill or terminating its process`
- `Actual mapped drive and its UNC representation have identical resolved identities`
- `Real initialized PostgreSQL 17 cluster refuses PostgreSQL 18 association`

完整逐项名称见测试结果日志，包含：健康状态无应急入口、禁用启动/重启、混装和缺工具拒绝、备用选择后再次检查、错误 PID/数据主版本拒绝、全部关联实例停止要求、停止失败/超时不恢复、同路径 ID 与设置保留、实际 SQL 数据、配置保存冲突、重复新建拒绝、已有集群重新导入、原始 initdb 错误保留、实际残留提示及密码不出现在诊断。

## 真实程序来源与隔离数据证据

| 程序 | 实际来源及核验 |
| --- | --- |
| PostgreSQL **18.3** Windows x64，四工具均 18.3 | `https://get.enterprisedb.com/postgresql/postgresql-18.3-1-windows-x64-binaries.zip`；本轮下载至 `artifacts/dev/reliability-20261027/official-18.3/pgsql` |
| PostgreSQL **18.6** Windows x64，四工具均 18.6 | 只读复制本机既有 `D:\PostgreSQL\PostgreSQL`；仓库既有 catalog 记录的发行来源为 EDB `postgresql-18.6-5-windows-x64-binaries.zip`，本轮未重新验证该安装与整个供应商 ZIP 的一一对应 |
| PostgreSQL **17.11** Windows x64，四工具均 17.11 | `https://get.enterprisedb.com/postgresql/postgresql-17.11-5-windows-x64-binaries.zip`；本轮下载至 `artifacts/dev/reliability-20261027/official-17.11/pgsql` |

18.3 和 17.11 为节省传输，通过官方 HTTPS ZIP 的 Range 请求读取原始 `bin/lib/share` 文件，并按 ZIP CRC 校验每项、记录文件 SHA-256。**未下载/校验整个供应商 ZIP 的 SHA-256，不能将其描述为完整发行包供应链校验通过。** 来源/方法/逐文件哈希见 `official-18.3-source.json`、`official-17.11-source.json`；四个核心工具的实际输出及哈希见 `binaries-evidence.json`。

最终维护更新测试根目录：

```text
E:\PgHatch-Reliability-20261027\reliability-2377bd84f83a4b8596c2eaf70bc39a0f\
    data-first\
    data-second\
    program-original\
    program-new\
    program-independent\
    program-older-fallback\
    results.txt
```

在真实 18.3 中初始化 `data-first`，启动并在 `postgres` 数据库创建 `reliability_evidence` 表，插入 `preserved-before-maintenance`。正常停止全部关联实例后，把真实 18.6 完整程序复制到**同一原程序路径**，保留原登记版本以检测真实差异；外部启动更新后的服务器，核验原路径工具可安全应急停止。分别注入停止命令失败和超时，确认真实服务器仍运行、不能修复。正常应急停止后再单独恢复登记，保留 ID、全部实例配置与停止时 PGDATA 文件哈希；显式启动 18.6 查询得到原行。

随后显式切换到另一 18.6 路径，启动后 `SELECT count(*)` 返回 1。对另一个真实运行的 18.3 集群仅替换未在使用的控制工具，制造真实混装；排除原目录，明确选择较旧的 18.3 备用程序正常停止，即使同时登记了较高的 18.6 也不自动选择。停止后先验证混装修复拒绝，再验证外部配置修改导致的修复保存失败和数据保留，最后恢复完整程序并重新核验原登记，不自动启动。

最终跨主版本根目录：`E:\PgHatch-Reliability-20261027\cross-major-cffdd4ca6b4442b699fd69c15b282cbe\`，`data17`、`data18` 分别由真实 17.11 和 18.6 初始化。双向重关联和 17 原路径被完整 18.6 覆盖后的修复都拒绝；两套数据哈希不变。恢复 17.11 后查询原表返回 17。此项不是仅修改 PG_VERSION 的模拟测试；综合维护测试里另外保留了 PG_VERSION 故障注入，两者独立报告。

既有真实回归根目录：`E:\PgHatch-Reliability-20261027\existing-final\run-20261027-225907-dd019f96\`。所有测试根目录有独立归属标记，结果和数据保留供审查。结束后本轮程序目录下剩余 PostgreSQL 进程数为 **0**，成功运行的 `results.txt` 均记录 `All owned servers stopped: True`。没有初始化、停止、升级、删除或清理用户既有数据库。

临时 SMB 映射仅指向本轮唯一测试根目录，真实 Windows `DriveType.Network`、初始化拒绝、映射/UNC 身份一致及导入风险识别均通过；映射已移除，没有创建共享、修改服务或留下持久映射。

## 复现命令

以下路径必须按环境替换为真实程序来源；测试父目录必须是独立的新测试位置。程序会在其下新建 GUID 子目录，不使用现有用户 PGDATA。维护测试内部复制程序，原程序仅供读取。不要将维护测试与其他 initdb 同时启动，以免身份排查期间遇到无法定位 PGDATA 的短暂 bootstrap 进程而保守拒绝。

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --reliability-real '<18.3 root/bin>' '<18.6 root/bin>' 'E:\New-PgHatch-Test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --cross-major-real '<17.11 root/bin>' '<18.6 root/bin>' 'E:\New-PgHatch-Test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- '<18.6 root/bin>' 'E:\New-PgHatch-Test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --config-tests '<18.6 root/bin>' 'artifacts/dev/new-config-test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --maintenance-tests 'artifacts/dev/new-controlled-test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --download-tests 'artifacts/dev/new-download-test'
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --catalog-tests catalog/postgresql-catalog.xml 'artifacts/dev/new-catalog-test'
dotnet run --project tests/PGTray.IconChecks -c Release --no-build -- 'artifacts/dev/new-icon-test'
```

## 跳过项目、观察到的限制及后续建议

1. **实际目录符号链接：SKIP**。Windows 返回错误 1314（客户端没有所需特权）；未提升权限或改变系统设置。实际 junction、映射盘、UNC 和驱动器未知/不可用类型断言通过。后续可在已有符号链接权限的机器运行同一测试。
2. 18.3 首次在工作区中文路径初始化时，真实 initdb 返回 UTF-8 字节序列错误。最后定向复核已确认：中文 PGDATA 本身可成功；中文 `share` 路径触发失败，直接 initdb 与 PgHatch 一致。不能据首轮现象直接归因于“中文 PGDATA”或泛称“上游问题”；确切结论及尚未证实的内部编码位置见最后复核报告。未更改产品默认 locale、collation provider 或校验和策略。
3. 一次并行执行配置测试时，与另一个测试 initdb 的 bootstrap 进程重叠，严格身份检查保守拒绝了修复；等待初始化完成后，最终配置测试 75 项通过。保留 `config-final.log` 与 `config-final-serial.log`，没有削弱安全边界来消除该拒绝。真实环境遇到无法读取/定位的服务器进程时同样应先处理不确定性再重试。
4. Windows 进程命令行查询依赖系统 API；不可用、权限不足或服务器使用相对/不可定位 PGDATA 时会拒绝应急停止。没有模拟成“已停止”。外部程序仍能在检查后独立启动数据库；应用锁只能串行化 PgHatch 自身操作，不能作为跨进程维护锁。维护时应协调外部服务/脚本。核心 EXE 固定也不等于第三方 DLL、扩展和存储的完整健康证明。
5. 映射盘能展开为 Windows 报告的 UNC，不能全面证明服务器别名、DFS 或不同共享入口指向同一物理资源。未知状态拒绝初始化，不声明完整网络路径等价判断。
6. 未执行 Computer Use、桌面代理、人工点击或系统缩放验证。新增关键按钮权限和选择逻辑已自动检查；若用户希望人工复核，可仅检查：异常实例显示“应急停止”、候选显示版本/路径且未默认选择、停止后仍提示程序待修复、修复/切换确认列出关联实例、恢复后仍停止。无需以人工 GUI 验证作为本轮交付前置条件。
7. 未执行真实 ARM64 PostgreSQL 或第三方扩展兼容性验证，未执行实际 REINDEX/排序规则元数据刷新。官方排序规则排查与风险说明已集中更新于用户指南，未增加自动诊断功能。
8. 本地证据位于忽略的 `artifacts/dev/reliability-20261027/` 与上述隔离根目录，随源码交付时应另外保存所需日志和来源清单。测试资源保留以供核对；不自动删除数据库目录。

## 发布制品与最终保护声明

执行前后对 `artifacts/releases` 和既有 `artifacts/candidates` 内 **114 个 ZIP/EXE** 核对 SHA-256，**0 个变化**；证据为 `release-hashes-before.json`、`release-hashes-after.json`。历史根目录制品未作为任何输出目标。版本号、标签和 HEAD 未修改，未创建 Release，未发布或 push。

本轮没有修改现有正式发布制品。没有引入 `latest` 或按最大版本号选择，没有合并不同程序路径的登记，没有自动迁移实例，没有数据库主版本自动升级、强制终止、删除 PGDATA 或自动重新初始化。
