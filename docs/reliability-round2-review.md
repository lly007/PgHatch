# Reliability Recovery 第二轮定向修复

2026-10-28。分支 `hardening/reliability-recovery`，基线 `d6b5b8a56646358c9c335badf181ae7cd4489abd`。执行前工作区/暂存区干净，本地与远程均为该提交；未切换、重置或覆盖用户修改。

## 根因与修复

| 问题 | 根因 | 本轮最小修复 |
| --- | --- | --- |
| A：旧映射盘配置误报路径改变 | 完整 Identity 会展开映射为 UNC，但生命周期将结果与原始盘符 Normalize 比较；ServerIdentity 的另一项比较又把两边都 Identity 化，成为无效的自比较 | 新增内部 UnlinkedDataIdentity：只展开映射别名，单独检查各级 reparse point；管理器记录登记 ID、原路径及已核验身份，用于生命周期、修复与刷新。当前工具调用固定使用核验后的身份，不再传递可变盘符别名；旧配置不改写。映射改变后即使重读同一记录也拒绝接受。去重继续使用原 Identity。替换 ConfirmRunning 的恒真检查，保留 PID、启动时间、进程路径、命令行和其他进程的交叉核验 |
| B：进程已退出却因读取错误永久锁住 | PendingCompletion 将退出等待和管道排空视为一个成功条件；读取异常使 manager 无法区分真正的退出确认失败 | CommandResult 内部分别记录 ProcessExitConfirmed 和 OutputReadError；退出后读取失败仍返回退出确认、已有输出与读取诊断，清理并观察两个读取任务后回收资源。管理器按退出确认决定解锁；已完成的完成任务也保留最终诊断。真正的退出观察失败仍保持 Unknown 和生命周期阻止 |
| C：PID 访问错误被当作不存在 | File.Exists 会把部分访问/I/O 错误折叠为 false | ConfirmStopped 使用可区分缺失/错误的 Attributes，要求数据目录存在、无 reparse point、PG_VERSION 可读且格式有效、pg_control 标记存在，PID 属性明确不存在，再执行原有其他 postgres 进程扫描。访问错误直接拒绝，不删除 PID、不忽略错误 |
| 测试直接启动 initdb 的超时清理 | Dispose(Process) 不会结束仍活着的进程 | 路径实验共用小型 RunOwnedInitializationAsync：异常/超时仅终止本次直接创建的 initdb/辅助进程树并等待退出，取消和观察输出读取后再清理凭据；失败保留 PID、原因及已收集输出。无法确认清理完成时明确报错并保留凭据位置供人工处理 |

公开方法签名、四个既有 CommandResult 构造字段、配置 schema、版本号、数据库主版本边界及默认 locale/排序规则/校验和参数均未改变。新增状态与故障注入接缝均为内部成员；不引入依赖或第二套管理架构。

## 映射与网络政策边界

新建实例仍拒绝 UNC、映射网络盘和未知磁盘类型。已有网络实例仍按既有风险提示只读检查、明确导入；本轮仅使盘符和对应 UNC 使用相同的身份检查，不增加或认证网络数据库启停/运行支持。真实映射测试没有初始化或启动网络数据库。

可信映射保留在管理器进程内，同一登记的普通重读不能重置它；配置明确变更路径时仍须走既有兼容性及停止检查。schema 没有持久化旧映射，因此重启前未保存的映射关系、服务器别名/DFS 的物理等价性无法推断，须人工核对。失效或不安全的单个路径保留元数据并阻止相关操作，不使其他有效配置整体失效。

## 修改文件

- `src/PGTray.Core/PathSafety.cs`、`ServerIdentity.cs`、`InstanceManager.cs`：映射/重解析点分离、会话身份固定、PID fail-closed 及控制器锁释放判断。
- `src/PGTray.Core/ProcessRunner.cs`、`Models.cs`：退出确认与输出收集分离，异常诊断和异步资源清理。
- `src/PGTray.Core/Resources/Strings.resx`、`Strings.zh-CN.resx`、`Strings.zh-TW.resx`：三语言的诊断输出不完整提示。
- `tests/PGTray.IntegrationTests/RecoveryRound2Tests.cs`、`FinalReviewTests.cs`、`NetworkPathTests.cs`、`Program.cs`：确定性接缝、真实父子进程清理、控制器读取故障/退出观察故障，以及可跳过的真实映射验证。
- `docs/user-guide.md`、本报告：边界、恢复与验证说明。

## 最终验证结果

`dotnet build PGTray.sln -c Release --nologo`：**0 警告、0 错误**；`git diff --check` 通过。

| 本轮实际执行 | PASS | SKIP | 证据类型 |
| --- | ---: | ---: | --- |
| `--round2-tests` | 42 | 1 | 旧配置/映射变化/reparse 确定性检查、PID 属性访问故障注入、实际控制器输出、真实父子进程回收、三语言资源、真实 SMB 映射 |
| `--round2-controller` | 9 | 0 | 真实 PostgreSQL 配合辅助控制器；注入 EOF 读取异常与退出观察失败，检查锁释放或继续阻止 |
| 既有 `--review-timeout` | 22 | 0 | 实际进程、真实 pg_ctl 自身超时、迟到输出、双管道及退出协调回归 |
| 既有 `--config-tests` | 75 | 0 | 配置原子保存、冲突、恢复及关联回归 |
| 既有 `--maintenance-tests` | 15 | 0 | 受控版本/工具夹具，不能当作真实维护更新测试 |
| 既有 PostgreSQL 集成入口 | 75 | 0 | 真实 PostgreSQL 18.6 生命周期、SQL、多实例、路径与故障恢复 |
| **合计** | **238** | **1** | 无最终失败，不将夹具/故障注入计为真实数据库集成数量 |

证据集中于本地忽略目录 `artifacts/dev/recovery-round2-20261028/`（`build.log`、六组日志、`summary.json`），隔离数据和结果位于本轮唯一创建的 `PgHatch-Round2-20261028` 子目录。真实程序使用首轮取得的 18.3 和只读复制的本机 18.6；来源与整包校验限制沿用上一轮报告。六组测试均针对最后固定操作身份后的最终生产源码执行。

真实映射测试验证稳定旧配置、UNC 去重、映射改变、同记录重读仍拒绝及刷新 Unknown。Windows 缓存元数据句柄曾使非强制断开失败；测试仅对本轮独占盘符及隔离目录连接执行强制断开，未终止数据库或更改 SMB 服务。最终映射已移除。

## 跳过项与限制

- **SKIP 1**：实际目录符号链接创建缺少 Windows 特权；未提权或修改系统权限。核心 reparse 拒绝通过确定性属性注入验证，既有真实集成中的 junction 检查通过。
- PID/目录访问拒绝通过内部属性接缝稳定注入，**未声称实际 PID ACL 拒绝测试通过**。真实生命周期中的已有权限失败回归另外通过。
- 输出异常为内部读取检查点注入，退出观察失败为内部等待接缝注入，底层控制器与目标 PostgreSQL 为真实进程；没有依赖偶然系统竞态或伪称制造了真实 pg_ctl 永久挂起。
- 初始化清理的成功、超时和 I/O 异常分支使用本测试真实拥有的辅助进程/子进程验证，路径实验复用同一清理函数；未重复整套 14 组中文路径调查。正常数据库生命周期不使用这种强制清理方式。
- 没有重跑全部 816 项：选择了受影响的配置、控制器、路径、维护关联及真实生命周期组；下载、目录服务、GUI 布局、ARM64/第三方扩展等未改动范围不作为本轮新增验证结论。未使用 Computer Use。

没有修改正式 ZIP/EXE、版本号、标签或配置格式，没有操作用户既有 PGDATA。测试产物与凭据不纳入提交；原始失败的探索性测试结果保留在本地，最终验证均为上述通过结果。
