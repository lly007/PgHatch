# Issue #3 修复与验收

基线 `d7b96e2af65674d1fb84861986c68fb54524b6ab`，分支 `fix/issue-3`。2026-11-08 通过 GitHub 连接器读取远端 main，确认与本地基线一致。

## 附近程序菜单

原实现 `BinaryForm` 每次发现都创建无窗体所有者的菜单，在 `Closed` 中同步 Dispose。Issue 的调用栈显示 `OnItemClicked → SetVisibleCore → CreateHandle`，此时 WinForms 仍使用刚刚关闭的菜单，触发 ObjectDisposedException。

`BinaryForm` 现在持有一个可复用菜单，在窗体 Dispose 时释放。更新候选时逐一 Dispose 旧菜单项，释放捕获路径的处理器。关闭事件只关闭菜单。

`BinaryMenuChecks` 通过真实 ToolStrip 鼠标命中、点击及自动关闭流程，重复选择和取消八轮，检查路径、菜单复用、旧项释放及打开菜单时关闭窗体。这里使用原生 WinForms 控件和消息循环，未模拟人工操作整个桌面。

验证结果在各工作单元运行后记录。
`dotnet build PGTray.sln -c Release --nologo` 通过，0 警告、0 错误。完整 IconChecks 552 PASS，其中新增菜单生命周期 25 项。日志位于本地忽略目录 `artifacts/dev/issue3-menu.log`。

## 实例移除

原 `RemoveAsync` 只持有实例锁并删除登记，UI 仅提示保留数据，没有表达数据库继续运行。它先取得实例锁再通过 CommitAsync 取得配置门，与生命周期操作的顺序不一致。

新增显式 `InstanceRemoval` 模式。旧签名仍执行纯解除登记。UI 重新查询状态，确认停止时选择 RequireStopped 并在操作中重验。运行时通过原生 TaskDialog 提供“停止数据库并移除登记”“仅移除登记，保持数据库运行”和取消，取消为默认选择。未知及转换中状态提示数据库可能仍运行，禁用停止并移除，纯解除登记仍受控制器互斥约束。

受控操作统一按照 configurationGate → Lifecycle 顺序取锁。停止并移除在同一次配置门和生命周期操作中，固定工具租约、核验程序版本、PGDATA 唯一身份和 postmaster 进程身份，复用 fast 停止命令。仅在控制器自然退出、命令成功、无超时、实际停止探测与 ConfirmStopped 均通过后保存删除登记。待退出控制器交给既有 RetainPendingController，保持生命周期栅栏。保存失败保留内存和磁盘登记。纯解除登记不执行任何停止命令。

`RemovalTests` 使用独立临时集群，覆盖外部启动/导入、纯解除、停止并解除、过期停止选择、停止失败/假成功/超时/退出未确认、身份不明、真实 warm standby Starting、配置保存失败和并发 start。实际工具为 PostgreSQL 18.6，源目录只读，PGDATA 均在 `E:/PgHatch-issue3-tests/removal-*`。

- Release 构建通过，0 警告、0 错误。
- 新定向回归 42 PASS，日志 `artifacts/dev/issue3-removal-final.log`。
- 既有真实生命周期 75 PASS、round3 82 PASS、round3-controller 15 PASS，分别记录于 `issue3-lifecycle.log`、`issue3-round3.log`、`issue3-controller.log`。
- 完整 IconChecks 552 PASS，日志 `issue3-removal-ui.log`。原生 TaskDialog 的实际按钮文案、默认取消与人工选择仍需 GUI 验收。
