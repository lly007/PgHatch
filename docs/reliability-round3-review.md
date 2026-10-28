# Reliability Recovery 第三轮定向修复

2026-10-28。分支 `hardening/reliability-recovery`，基线 `0248de4615e42d03f8c15fdb5643293ed2d773c3`。开始时工作区和暂存区干净，本地 HEAD 与远程分支一致。未切换、重置或覆盖已有工作。

## 根因、修复与源码位置

| 审计问题 | 实际根因 | 修复位置和行为 |
| --- | --- | --- |
| P1：外部 PGDATA 变更后重读 | `LoadAsync()` 先让 ConfigStore 接受候选指纹，再替换 Config；身份捕获遇到不同原始路径会重新建立信任。外部移除登记也未与旧配置比较 | `InstanceManager.LoadAsync()` / `PrepareDataIdentities()`：接受前逐个比较旧 ID，拒绝删除、改变目标或无法确认身份的变更；旧路径和候选别名均须通过无 reparse 的目标核验。候选身份先放在局部字典，全部接受后才安装。`ConfigStore.LoadAsync(Action<AppConfig>?)` 在更新指纹和旧文件迁移前调用内部校验 |
| P1：停止确认关键标记 | 读取 PG_VERSION 前未核验文件类型和 reparse；未单独核验 global；pg_control 仅排除目录，未排除链接或确认可读 | `ServerIdentity.ConfirmStopped()` / `RequireMarker()`：PG_VERSION、global、global/pg_control 分别检查存在、类型、reparse/device 和属性访问错误；版本文件实际读取，控制文件实际以只读方式打开。保留 PGDATA、PID 明确不存在及其他 postgres 进程扫描。只检查身份必需路径，不递归禁止表空间链接 |
| P2：应急候选路径漂移 | 忽略 EnsureUniqueData 的返回值，身份确认和工具读取继续使用可重新解析的盘符路径 | `GetEmergencyCandidatesAsync()`、`EmergencyCandidatesCoreAsync()`、`EnsureUniqueData()`、`EmergencyStopAsync()`：候选读取在配置门内，使用核验后的规范目标构造快照；其他登记也按各自可信身份排重。工具/配置检查后、真正停止前和命令返回后再核验原登记映射，最终进程身份检查保留。持久化路径不改写 |
| P2：输出管道无限等待 | 一秒排空超时后仅取消读取，CollectOutput 和 Dispose 仍无界等待同一读取任务；取消不保证 native/custom reader 返回 | `ProcessRunner.CollectOutputAsync()`、`CompletedResultAsync()`、`BeginCleanup()` / `ReclaimAsync()`；`CommandResult.ResourceCleanup`；`InstanceManager.RetainOutputCleanup()`：分别记录 OS 退出、输出和资源清理结果，前台有限等待，后台任务保有仍被读取任务使用的缓冲区，迟到清理异常补入操作诊断 |

## 配置接受与冲突语义

拒绝候选时，内存 Config 对象、实例状态、程序关联、程序问题记录及可信 PGDATA 身份不替换、不清空。旧配置仅用于查看；`configurationLoaded=false` 阻止正常保存、初始化、启停、修复、关联和移除。已有实例时也禁止借“恢复为空配置”绕过冲突。即使旧实例已停止，也不静默接受另一个 PGDATA 或外部删除登记，须先恢复原配置/映射并重读，再通过受控登记操作处理。

ConfigStore 保留上一次接受的 `expectedHash` 和 `observed`，不会接受被拒候选的指纹，并设置保存阻止标记。外部磁盘内容不改写；即使直接恢复原字节，保存仍被阻止，直到成功加载并接受配置。首次加载损坏配置的既有明确备份恢复流程保持可用，原子保存、冲突检测及备份机制保留。配置 schema、公开 LoadAsync API 不变。

同目标的合法映射盘/UNC 别名可以接受，必须核验旧映射仍指向可信目标且新别名不经过 reparse。未经解析的旧登记只可原样保留，继续禁止生命周期操作，不能通过重读重新建立未知身份的信任。映射历史仍只在本管理器进程内保存，不增加网络数据库运行支持。

## 输出管道与回收语义

控制器退出已确认后，先等待最多一秒收集输出。超时后独立取消读取并关闭本进程的两个输入管道句柄，再等待读取最多一秒；资源清理最多再前台等待一秒。取消回调、管道关闭及同步 Dispose 都通过后台任务执行，单条管道或同步清理卡住不逃逸前台等待边界。这里是各等待阶段的期限，不是对 Windows 调度或进程创建耗时的硬实时保证。

若读取仍不响应，返回已收集输出和不完整警告，`ProcessExitConfirmed=true`，资源完成任务可以仍为 Pending。后台任务等待并观察所有读取结果，之后才释放目的缓冲区、流、进程对象及令牌源；清理异常逐项记录，不覆盖已确认的进程退出。控制器的 PendingCompletion 不再依赖资源彻底回收，管理器据退出确认释放生命周期锁，资源晚到的异常仍记录到操作诊断。

控制器仍存活时继续被动观察、保持互斥；退出观察失败也不释放生命周期锁。未更改正常启动/停止/初始化原有的超时终止策略，应急停止始终 `terminateOnTimeout:false`，没有强杀 PostgreSQL 或控制器。极端情况下，若底层读取/关闭永不返回，对应资源将继续由后台任务持有直至其返回或管理器进程退出；不能为了声称清理完成而释放仍在使用的缓冲区，也不会无限阻塞已确认退出控制器的生命周期。

## 修改文件与测试入口

- `src/PGTray.Core/InstanceManager.cs`、`ConfigStore.cs`：配置接受事务、保存阻止、可信目标快照、候选/停止复核及迟到清理诊断。
- `src/PGTray.Core/ServerIdentity.cs`：停止确认关键标记检查。
- `src/PGTray.Core/ProcessRunner.cs`、`Models.cs`：有限输出等待、独立资源回收完成状态与诊断。
- `src/PGTray.Core/Resources/Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx`：配置重读冲突、输出回收延迟两项三语言提示。
- `tests/PGTray.IntegrationTests/RecoveryRound3Tests.cs`：新增 `--round3-tests`，真实集群的运行/停止目标替换、登记删除、冲突后的保存与恢复、SQL 数据保留；映射注入配合真实本地 PostgreSQL 的候选/停止目标核验；三项关键标记六类属性故障；真实继承管道子进程、不响应取消和同步清理卡住的确定性测试。
- `tests/PGTray.IntegrationTests/FinalReviewTests.cs`：`ControllerCaptureAsync(..., includeDelayedReader:true)` / `--round3-controller` 增加真实 PG 配合延迟输出清理、锁释放和迟到清理异常的回归；保留读取故障、存活及退出确认失败的测试。
- `tests/PGTray.IntegrationTests/NetworkPathTests.cs`：真实稳定映射到 UNC 的重读；映射变化改为明确拒绝加载。
- `tests/PGTray.IntegrationTests/RecoveryRound2Tests.cs`：测试 PID 写完后原子发布，消除“文件已出现但写句柄未关”的就绪竞态。FinalReview 和新辅助进程共用此逻辑，不涉及产品 PID 文件。
- `tests/PGTray.IntegrationTests/Program.cs`：新测试及隔离辅助进程入口。
- `docs/user-guide.md`、本报告：冲突处理、等待边界、关键标记及验证限制。

## 最终验证

以下全部基于最终生产源码执行，七个入口退出码均为 0。

| 实际入口 | PASS | FAIL | SKIP | 证据性质 |
| --- | ---: | ---: | ---: | --- |
| `--round3-tests` | 82 | 0 | 0 | 真实本地 18.6 集群/SQL/正常停止，实际辅助进程与管道，以及确定性映射/属性/读取/清理故障注入 |
| `--round3-controller` | 15 | 0 | 0 | 真实 PostgreSQL 18.3 初始化、同路径 18.6 工具、实际控制器辅助进程，读取和退出观察故障注入 |
| `--round2-tests` | 43 | 0 | 1 | 既有路径/PID/输出/隔离辅助进程回归；真实 SMB 映射和 UNC 别名（没有网络数据库启停） |
| `--config-tests` | 75 | 0 | 0 | 配置解析、原子保存、冲突、恢复及关联 |
| `--maintenance-tests` | 15 | 0 | 0 | 受控工具/版本夹具，不能视为真实维护更新测试 |
| 既有真实 PostgreSQL 集成入口 | 75 | 0 | 0 | 18.6 生命周期、多实例、SQL、junction、配置及初始化权限失败 |
| `--review-timeout` | 22 | 0 | 0 | 实际控制器被动超时、迟到输出、真实 pg_ctl 超时、锁与退出协调 |
| **合计** | **327** | **0** | **1** | 不把故障注入或元数据夹具宣称为真实数据库验证 |

`dotnet build PGTray.sln -c Release --nologo`：0 警告、0 错误；`git diff --check` 通过。源码、配置格式及产品版本差异检查无越界修改。已复核 114 个既有 ZIP/EXE 的 SHA-256 均未变化。

本地忽略证据位于 `artifacts/dev/recovery-round3-20261028/`：`summary.json`、`build.log`、七组日志、`binaries.json` 和发行文件哈希检查。隔离父目录 `E:\PgHatch-Round3-20261028`，每次入口新建唯一子目录；最终关键结果：

- 新定向测试：`round3-ac26c35504fb4e96bfab7162572f0de8`。
- 控制器：`round2-controller-79dcc179c51b4852833de14ab477e3fb`。
- 映射/PID：`round2-50bac9080f75448ba60b013be32a62ab`。
- 真实生命周期：`lifecycle-final/run-20261028-200707-a91833f3`。
- 既有超时：`controller-timeout-590372a59e0f4be6bf5db095eb77d3ca`。

实际版本为 `postgres (PostgreSQL) 18.3` 和 `18.6`。18.3 沿用首轮官方来源文件的只读副本，18.6 来自本机 `D:\PostgreSQL\PostgreSQL` 的既有工具，控制器组复制到本轮隔离程序目录。18.3 供应商整包 SHA-256 未核验的限制沿用[首轮报告](reliability-hardening.md)，本轮记录了实际 postgres.exe 哈希，不扩大来源认证结论。没有操作用户既有数据库。

首次既有回归曾触发上述测试 PID 就绪竞态，原始失败日志保留在本地；修复原子发布后，最终全部相关入口重新运行通过，没有将失败当作通过。日志、测试集群、密码、二进制和实验产物均不纳入提交。

## 限制与合并判断

- SKIP 1：实际符号链接创建缺少 Windows 特权，未提权或修改系统权限。reparse 关键判断通过属性注入，已有 junction 回归通过。
- 关键标记的访问拒绝/I/O 错误使用确定性属性接缝，不能宣称真实标记 ACL 拒绝测试通过；已有真实初始化权限失败测试另外通过。
- 无法主动制造真正永不返回的 Windows 驱动 I/O；用不响应取消/关闭的读取器与同步清理阻塞稳定验证前台边界和后续回收。真实继承管道由本轮隔离父子辅助进程验证，必要的强制清理只允许用于明确归属的辅助进程，不用于 PostgreSQL。
- 映射的历史不会跨管理器重启持久化，不认证服务器别名/DFS 的物理等价性，也未扩大网络运行支持。规范目标快照防止后续工具调用被盘符别名重定向；本轮没有实现操作系统级文件系统/映射事务或抵抗任意恶意并发替换的全局锁。
- 没有使用 Computer Use 或 GUI 实测；未无条件重跑全部 816 项，下载、GUI、ARM64 和第三方扩展等未变更范围没有新增验证结论。没有重新进行整套中文 share 路径调查。

本轮四项问题均已修复，选定回归无最终失败，未发现新的阻止合并问题；仍需后续独立 Code Review。没有修改配置 schema、产品版本、初始化默认参数或正式发行包，没有合并 main、创建标签/Release 或重写历史。
