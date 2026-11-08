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

## 主窗口 DPI 与布局机制

### 机制审计与证据

| 源码与运行证据 | 诊断及职责 |
| --- | --- |
| `PGTray.csproj` 的 ApplicationHighDpiMode 为 PerMonitorV2，`Program.Main` 调用 ApplicationConfiguration.Initialize | 保留 WinForms 的逐显示器 DPI 感知和自动缩放入口 |
| 基线 `MainForm` 构造函数先设 AutoScaleDimensions/AutoScaleMode，再改 Font，最后才写 ClientSize、构造控件 | Font 引起布局时，自动缩放基准已经推进到当前 DPI，后赋值的尺寸留在物理像素。真实 144 DPI 日志为 ClientSize 1080×700、外边距 20。修复后同机为 1620×1050、30，尺寸均恰好为一次 1.5 倍映射 |
| 原 ClampToWorkingArea 对当前 MinimumSize、Size 仅执行 Math.Min | 约束没有独立的逻辑设计值或用户期望值，工作区变大后无法恢复，反复缩放可能积累舍入和缩小 |
| 原 FitLogo 每次尺寸事件按 DeviceDpi 重写 Logo 大小和 Margin | 与 WinForms 缩放同一控件形成双重职责。现在 Logo 的 Size/MinimumSize/MaximumSize、Margin 只在设计树内赋值，由 WinForms 缩放。运行时仅调整可见性和对应列的占位 |
| 主布局和若干单列面板使用隐式 AutoSize 列，内容区使用隐式 AutoSize 行 | 合成 DPI 往返时记录过 root 宽 1440 而 content 宽 2100，子面板超出父容器。明确填充方向的 Percent 样式后，content 宽 1380，保持 42/58 分列及父子边界 |
| Header.GetPreferredSize 对嵌套百分比列中的工具栏按最小宽度估计换行 | 记录过工具栏实际高 64、Header 高 237，留下多算的一行。改为布局完成后测量实际子控件占用高度，保持原有单次合并的 BeginInvoke 测量，避开最小化的零客户区 |
| 原 DpiChanged 内同步更新测量并另行 BeginInvoke 缩窗，Shown 也调用缩窗 | DPI 事件发生时 WinForms 尚待应用建议矩形和缩放子控件。现在图像资源按新 DPI 选择，几何约束及测量统一排到原生 DPI 处理完成之后；HandleCreated 只选择图标 |

### 统一尺寸约定

- 固定设计树以 96 DPI 逻辑尺寸构造。MainForm 全部构造在 SuspendLayout/ResumeLayout 之间完成，字体、控件固定尺寸、Margin、Padding、最小内容尺寸均只接受 WinForms 自动缩放。
- 沿用现有设计客户区 1080×700。常规窗口最小客户区为 860×500。内容根为 800×540，列表和详情分别至少 300、360 逻辑宽度。最小窗口可以通过外层原生滚动访问完整内容，详情保留独立滚动。此组尺寸是本轮的统一设计选择，仍由维护者真实 GUI 验收。
- `DpiLayout` 只映射工作区窗口边界及 DataGridView 自行测量的行高/列最小宽度和非控件 CellStyle.Padding。审计记录显示 CellStyle.Padding 在 96～192 DPI 合成切换中一直为物理 4px，WinForms 不会自动缩放该非控件对象，现统一映射逻辑 4px。行高取真实字体文本测量加逻辑留白，冷启动验证其未重复缩放。按钮 PNG 和窗口 ICO 的物理资源选择继续使用各自资源缓存，不参与控件几何缩放。
- 外层滚动视口保留最小可用内容，工作区不足时限制外窗尺寸并按实际可见宽度隐藏次要 Logo。工具栏保留 FlowLayoutPanel 正常换行，详情可滚动，填充面板明确 Percent 约束。
- 用户期望客户区单独保存在逻辑尺寸中，只在 WM_SIZING 对应的用户 ResizeEnd 更新。DPI 建议矩形完成后从该逻辑尺寸重新映射，并按工作区裁剪和定位。临时限制不写回期望值或缩小永久最小值。移动窗口、最小化或最大化不会被当成用户改尺寸。最小化期间待处理的约束在恢复后执行。

### 自动化及人工验收边界

`DpiLayoutChecks` 增加冷启动基准、工作区不足与恢复、用户尺寸保持、96～216 DPI 连续步进映射，以及包含 108/168 等中间值的双向合成 WM_DPICHANGED。验证逻辑尺寸、控件边界、列比例、工具栏占用、图片物理尺寸、Logo 隐藏与复原。既有三语言最小化/还原/托盘恢复、详情滚动断言继续执行。预览检查发现的容器溢出及 Header 空行均已补入定向断言。

合成消息没有改变 Windows 显示设置，系统非客户区仍使用实际显示器 DPI。因此它验证 WinForms 事件、设计树和期望外窗映射，不将其客户区差异描述为真实跨屏验收。真实系统 DPI 100%～200%、不同物理工作区及跨屏拖动尚待维护者验收。需逐项观察冷启动、手动缩放后跨屏、工作区缩小再恢复、多轮往返、最小化/最大化恢复、三语言长文字、滚动访问启动/停止/更多操作，以及图标和字体清晰度。

本机真实 144 DPI 的原生窗口构造和回归已执行，尚未完成上述多 DPI 与多显示器人工验收。自动化最终计数在本单元完成后追加。
`dotnet build PGTray.sln -c Release --nologo` 通过，0 警告、0 错误。首轮完整 IconChecks 705 PASS，日志 `artifacts/dev/issue3-dpi-verified.log`，测量和原生控件预览位于 `artifacts/dev/issue3-dpi-verified/`。最后补充 CellStyle.Padding 集中映射与 16 项断言，定向回归见 `artifacts/dev/issue3-final/dpi-cells4.log`，完整计数纳入下方最终联合验收。早期探针失败和诊断日志保留在同一忽略目录，最终计数仅取完整成功运行。

## 子窗口标题栏图标

### 窗体清单与分配

基线只有 MainForm 显式设置正式 Icon。其余自定义窗体未赋值 Icon，ShowIcon 为 WinForms 默认 true，可能显示默认 .NET 图标。About 明确 ShowInTaskbar=false，其余子窗体使用默认值并在业务入口通过 ShowDialog(owner) 打开。LayoutForm 是公共布局基类，当前具体窗口共 11 个类型，创建和导入共用 InstanceForm。

| 窗体 | 本轮标题栏图标 |
| --- | --- |
| MainForm、AboutForm | PgHatch 品牌 |
| InstanceSettingsForm、BinaryForm | Lucide Settings 深蓝齿轮 |
| RenameForm、EmergencyToolForm | Lucide Settings 深蓝齿轮 |
| DownloadForm、CatalogForm | Lucide Settings 深蓝齿轮 |
| InstanceForm 创建/导入、DiscoveryForm | Lucide Database |
| LogForm | Lucide FileText |

LayoutForm 接受语义图标参数，默认品牌用于 About。直接继承 Form 的子窗体通过同一 BindWindow 机制绑定。ShowIcon=true，子窗体 ShowInTaskbar=false。原有模态入口和 owner 继续使用。

### 资源、来源与所有权

ApplicationIcons 静态共享内嵌 ICO 字节，避免每个窗口重复读取资源。每个窗体独立持有图标句柄缓存，构造、HandleCreated、DpiChanged 共用同一选择路径，在 Form.Disposed 事件释放，生命周期晚于原生 HWND 和子控件销毁。关闭其中一个窗口不会释放其他窗口的句柄。主窗口和托盘继续使用各自原有品牌资源与缓存。

新增 `Assets/Ui/Window/settings.ico`、`database.ico`、`file-text.ico`，来自现有 Lucide revision `1ef60af944de207e0d5176905a0759ba12e6ee8d` 的 SVG，统一深蓝 `#1e3a5f`。每个含 16、20、24、32、40、48、64、128、256px 九个 32-bit RGBA 帧，保留透明安全边缘，256px 用 PNG，其余用 DIB 与 AND mask。

离线生成脚本为 `scripts/Generate-WindowIcons.cjs`，使用本机已有 sharp。ICO 编码从现有品牌生成器抽成 `Write-WindowsIco.cjs` 共用。将原有品牌 PNG 帧重新写入临时 ICO，与两份正式品牌 ICO 均逐字节相同，验证编码抽取没有改变原逻辑。资源及许可说明在 `Assets/Ui/Window/README.md` 与 `THIRD-PARTY-NOTICES.md`，完整 Lucide/Feather 许可继续随构建提供。

### 验证与人工观察

`WindowIconChecks` 为 532 PASS，已纳入最终完整 UI 入口。检查每个原生 ICO 帧的尺寸、透明边缘和可见笔画，反射枚举全部窗口类型并要求分配测试覆盖。每种子窗口重复打开、关闭三轮，核对原生 WM_GETICON 大小 HICON 尺寸与 ICO 像素，合成 DPI 事件验证资源切换及返回复用，GetIconInfo 验证窗体关闭后借用的 HICON 已销毁。另逐一执行真正 ShowDialog(owner)，通过原生 owner HWND 和 IsWindowEnabled 核对模态拥有者禁用及恢复。目录/下载窗口使用离线缓存和禁止网络的 HTTP 夹具，实际请求计数为零。

已查看 16～256px 原始与放大帧预览，以及真实 144 DPI 原生控件绘制出的齿轮、数据库和文档标题栏预览。16px/32px 图形可辨、无边缘裁切。主窗口、托盘、EXE 原生品牌图标回归包含在完整 IconChecks 中。其他 DPI 的真实标题栏视觉效果、主题差异及实际跨屏清晰度仍待人工观察。

## 最终联合验收

全部命令使用 Release 构建。PostgreSQL 18.6 工具来自只读 `D:/PostgreSQL/PostgreSQL`，维护与跨主版本用已有 18.3、17.11 官方工具目录作为只读复制源。集群仅在 `E:/PgHatch-issue3-tests/` 下唯一命名的本轮目录中创建和正常停止，保留原始证据。

| 回归入口 | PASS | SKIP | 证据类型 |
| --- | --- | --- | --- |
| IconChecks 完整入口 | 1253 | 0 | 资源、原生窗口、模拟 DPI 和窗口消息 |
| --removal-tests | 46 | 0 | 临时真实集群及受控停止/保存/身份故障 |
| 默认生命周期入口 | 75 | 0 | 临时真实多实例、SQL 和生命周期 |
| --config-tests | 75 | 0 | 配置夹具、真实工具只读检查 |
| --maintenance-tests 串行 | 15 | 0 | 维护版本工具与配置夹具 |
| --round3-tests | 82 | 0 | 临时真实集群及确定性故障 |
| --round3-controller | 15 | 0 | 真实控制器、退出观察和管道故障 |
| --review-timeout | 22 | 0 | 真实 pg_ctl、被动超时及退出协调 |
| --reliability-real | 64 | 1 | 真实同主版本维护及安全故障 |
| --cross-major-real | 6 | 0 | 真实 17.11/18.6 拒绝与 SQL/数据保留 |

业务/安全断言共 400 PASS。唯一 SKIP 为创建真实符号链接权限不足。窗口图标 532 项、菜单 25 项等定向断言已包含在完整 UI 入口中，不重复计数。另执行共享 ICO 写入器对两份品牌文件的逐字节重建检查，2 PASS。

停止并移除的最终复核增加原始登记别名重验。执行停止始终使用先前核验的物理路径，在停止命令前和 ConfirmStopped 之后重新校验 Config 中原始登记路径，盘符或映射变化时保留登记。新夹具在停止命令完成期间注入别名变化，实际只停止本轮临时物理目标，并验证磁盘登记保留。加上外部启动与保留检查，定向断言由 42 增为 46，最终日志为 `artifacts/dev/issue3-final/removal-verified.log`。身份映射故障属于确定性注入，没有将其称为真实跨网络映射验收。

补充维护夹具首轮与其他工具版本测试并行时，ServerIdentity 对临时 `postgres --version` 进程的身份不明做了保守拒绝。产品和测试预期均未为此调整。停止并行版本探针后，全新目录串行复跑 15 PASS。首轮日志保留为 `artifacts/dev/issue3-final/maintenance.log`，成功串行日志为 `maintenance-serial.log`。后续运行此类夹具应与进程身份核验串行执行。

最终构建、联合 UI 和补充安全日志位于 `artifacts/dev/issue3-final/`，前三单元的专用日志位于同级 `issue3-*.log`。早期开发探针的故障日志不计入最终成功断言。全部临时真实服务器由其所属测试正常停止。

### 交付边界

可以进入代码审查。真实多 DPI、不同物理工作区、跨屏拖动、原生移除选择框的人工操作和标题栏视觉验收仍待维护者完成。当前设计保守拒绝未确认身份、退出或并发控制器状态，可能需要先处理诊断再重试。与真实数据库和平台权限相关的限制沿用既有安全边界。
