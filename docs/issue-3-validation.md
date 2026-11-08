# Issue #3 修复与验收

基线 `d7b96e2af65674d1fb84861986c68fb54524b6ab`，分支 `fix/issue-3`。2026-11-08 通过 GitHub 连接器读取远端 main，确认与本地基线一致。

## 附近程序菜单

原实现 `BinaryForm` 每次发现都创建无窗体所有者的菜单，在 `Closed` 中同步 Dispose。Issue 的调用栈显示 `OnItemClicked → SetVisibleCore → CreateHandle`，此时 WinForms 仍使用刚刚关闭的菜单，触发 ObjectDisposedException。

`BinaryForm` 现在持有一个可复用菜单，在窗体 Dispose 时释放。更新候选时逐一 Dispose 旧菜单项，释放捕获路径的处理器。关闭事件只关闭菜单。

`BinaryMenuChecks` 通过真实 ToolStrip 鼠标命中、点击及自动关闭流程，重复选择和取消八轮，检查路径、菜单复用、旧项释放及打开菜单时关闭窗体。这里使用原生 WinForms 控件和消息循环，未模拟人工操作整个桌面。

验证结果在各工作单元运行后记录。
`dotnet build PGTray.sln -c Release --nologo` 通过，0 警告、0 错误。完整 IconChecks 552 PASS，其中新增菜单生命周期 25 项。日志位于本地忽略目录 `artifacts/dev/issue3-menu.log`。
