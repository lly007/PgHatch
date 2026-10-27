# PgHatch 可靠性加固最后定向复核

2026-10-27，基于 `main` / `f1a23df55d9369299c8b2615748d13f33ae03b4e` 的既有未提交修改。未使用 Computer Use。

## 中文路径：确认的触发条件与尚未确定的机制

固定 PostgreSQL **18.3** Windows x64，复制出 ASCII/中文两套路径；`bin/lib/share` 共 **1,583 个文件 SHA-256 完全一致**。系统 ACP 为 **936**。保持凭据、UTF-8、SCRAM、默认 locale/校验和参数一致，得到：

| 程序路径 | PGDATA 路径 | 直接 initdb | PgHatch InitializeAsync |
| --- | --- | --- | --- |
| ASCII | ASCII | 成功 | 成功 |
| ASCII | 中文 | 成功 | 成功 |
| 中文 | ASCII | post-bootstrap 编码失败 | 相同失败 |
| 中文 | 中文 | post-bootstrap 编码失败 | 相同失败 |

直接调用绕过 `PostgresTools` / `ProcessRunner`，保留原始输出字节与真实退出码。另以 PowerShell 原生调用运算符复核“中文程序/ASCII 数据失败”和“ASCII 程序/中文数据成功”，排除只在 .NET ArgumentList 调用中出现的问题。

进一步保持二进制不变，以 initdb 的 `-L` 单独控制共享文件目录：**中文程序 + ASCII share 成功，ASCII 程序 + 中文 share 失败**；中文默认 share 在 debug 模式及清除 LANG/LC_* 后仍失败。共 **14 个路径实验**，6 个成功、8 个按条件复现失败；这些预期失败不是自动化回归失败。

**已确认**：本机该套 18.3 的中文 `share` 路径是充分触发因素，失败发生在真实 initdb 的 post-bootstrap 阶段。错误字节 `0xd6 0xd0` 与路径中“中”的 CP936 字节相符；首轮 `0xbf` 也与原程序路径中“开”的 CP936 首字节相符。

**已排除**：中文 PGDATA 本身、PgHatch 独有的参数/调用流程、PgHatch 日志解码导致“伪失败”、复制后文件内容不同，以及所观察到的 LANG/LC_* 环境变量作为必要条件。所有矩阵调用使用同一中文工作目录，故中文工作目录本身也不足以触发失败。默认文本搜索配置警告在成功/失败用例中均出现，不能据此判为致命错误原因。

**强线索但未完全证实的代码级机制**：[REL_18_3 initdb.c 的 setup_schema](https://github.com/postgres/postgres/blob/REL_18_3/src/bin/initdb/initdb.c#L1972) 将 `share/sql_features.txt` 路径拼入 `COPY ... FROM E'...'`，经 `fprintf` 写入后端输入。这与 CP936 路径进入 UTF-8 SQL 的实验结果一致。但没有截获实际供应商二进制的后端输入流，也没有匹配其构建符号/补丁，不能断言已证明具体内部转换位置或某个上游提交有缺陷。未加入更改 locale、编码、自动搬移路径或 `-L` 的产品规避逻辑。

证据根目录：`E:\PgHatch-FinalReview-20261027\initdb-paths-165dd27324df4f0cb3c1bef70167d978\`。其中 `evidence.json` 记录全部文件哈希与八组矩阵，`share-probe-754a4b5ca9a8400b90f901170a0336b2` 记录共享目录对照，`native-probe-1ef733c072fd470596995ded3eea3f02` 记录原生调用。debug 输出在落盘前脱敏，临时凭据已清理。18.3 来源仍为首轮记录的 EDB 官方二进制，未重新声称整包 SHA-256 验证。

## 超时：已确认缺陷与修复

复核前通过真实隔离 PostgreSQL 和“发送停止前暂停”的辅助控制器，确认：外层超时后控制器仍活着；服务器另行正常停止后，刷新误报 Unknown，且程序修复可能在控制器退出前获准。前后证据见 `artifacts/dev/final-review-20261027/timeout-before.log`、`timeout-after.log`。

- **控制器追踪与互斥**：`ProcessRunner` 将活控制器、管道与句柄交给可等待的内部完成任务；返回超时不等于进程退出。实例生命周期锁和核心 EXE 保护保留到观察到自然退出，随后排空输出、记录迟到输出/返回码并释放资源。控制器待定期间禁止程序修复、重新关联、移除与配置重读；状态不确定不会解除限制。
- **真实状态刷新**：控制器待定期间允许只读探测。异常登记在有完整兼容工具、pg_ctl 状态及进程扫描共同确认停止时显示 Stopped；无法确认时继续 Unknown。核验用的只读探测不会切换实例关联，也不启用待定控制器的应急按钮。
- **退出等待**：退出管理器会再次检查已接受操作后来转交的控制器任务，避免只等待最初任务快照；实际验证了“先发起退出、后转交控制器”的竞态。普通退出可能继续等待，不强杀 PostgreSQL。
- **被拒绝的重读配置**：忙碌状态下拒绝重读不再把已经有效的配置误标为加载失败；真正读取/解析失败仍沿用原有保护。

另审查了 [REL_18_3 pg_ctl.c](https://github.com/postgres/postgres/blob/REL_18_3/src/bin/pg_ctl/pg_ctl.c#L1027)：stop 先发正常停止信号，之后等待。真实 `pg_ctl stop -m fast -w -t 0` 实测自然退出码为 1，而 PostgreSQL 随后自行停止；这是控制器自身等待到期，不是外层 ProcessRunner 超时。

**仍有边界**：发送前延迟由辅助控制器注入，没有制造真实 pg_ctl 的永久 I/O 挂起。`-t` 约束正常等待阶段，不能保证系统 I/O 永不卡住。无法确认控制器退出时保留阻止，退出观察失败也不自动解锁；外部程序、管理器被强制结束或系统截止时间仍不受应用内锁保证。本轮没有强制终止数据库来解除超时。

## 本次追加修改文件

| 文件 | 目的 |
| --- | --- |
| `src/PGTray.Core/ProcessRunner.cs:45` | 超时控制器完成任务、持续读取、自然退出回收和并发输出快照 |
| `src/PGTray.Core/Models.cs:32` | 内部 PendingCompletion，不改变配置 schema 或四个既有命令结果字段 |
| `src/PGTray.Core/InstanceManager.cs:40`、`:65`、`:305`、`:520` | 退出任务转交等待、忙碌重读保护、真实状态刷新和待定控制器互斥 |
| `src/PGTray.Core/Resources/Strings{,.zh-CN,.zh-TW}.resx` | 三语言“控制器尚未退出，冲突操作被阻止”说明 |
| `tests/PGTray.IntegrationTests/FinalReviewTests.cs`、`Program.cs` | 路径矩阵/共享目录对照、真实服务器配合延迟控制器、真实 pg_ctl 自身超时、双管道及退出竞态测试 |
| `docs/user-guide.md`、`docs/reliability-hardening.md`、本报告 | 补充控制器语义，修正首轮中文路径归因并保留历史记录 |

## 最终验证

Release 构建 **0 警告、0 错误**。新增定向测试 **22 PASS**；必要的完整回归重新执行：配置 75、受控维护夹具 15、下载 78、目录 44、图标/本地化/控件逻辑 443、既有真实 PostgreSQL 75、真实 18.3→18.6 综合回归 58、真实 17.11/18.6 主版本边界 6。合计 **816 PASS、1 SKIP**；其中夹具、控件、故障注入和真实数据库证据按类别区分，不能将总数称为真实 PostgreSQL 测试数量。

符号链接实际创建仍因权限不足跳过，未修改权限或系统设置。其余路径/网络及 SQL 数据保留回归通过。定向超时证据根目录为 `E:\PgHatch-FinalReview-20261027\controller-timeout-991f610fd67247afba8d9591a3747a35\`；完整回归日志及构建记录集中于 `artifacts/dev/final-review-20261027/`。测试仅操作本次新建集群，保留结果目录；结束时无本次 PostgreSQL/辅助控制器进程残留。

未更改配置 schema、版本号、默认数据库行为或正式发布制品；未 push、未创建 Release，未使用 Computer Use。
