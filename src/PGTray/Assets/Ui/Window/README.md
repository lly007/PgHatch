# 子窗口标题栏图标

`settings.ico`、`database.ico`、`file-text.ico` 从相邻 `Lucide/` 中同名 SVG 离线生成。来源为 Lucide revision `1ef60af944de207e0d5176905a0759ba12e6ee8d`，完整 ISC / Feather MIT 版权和许可在 `../Lucide/LICENSE`，发行时沿用 `LICENSE-LUCIDE.txt`。

统一深蓝色 `#1e3a5f`，保留 SVG 几何和描边，透明背景与边缘安全留白。每个 Windows ICO 含 16、20、24、32、40、48、64、128、256px 九个 32-bit RGBA 帧。256px 为 PNG，其余为带 AND mask 的原生 DIB。主窗口、托盘的品牌资源继续由 `Assets/Brand/` 提供。

开发时使用本地 Node.js 与 sharp 运行 `node scripts/Generate-WindowIcons.cjs`。sharp 通过本地模块搜索路径提供，脚本无需网络。生成器复用 `Write-WindowsIco.cjs`，预览与 PNG 帧写入忽略目录 `artifacts/dev/issue3-window-icons/`。普通 .NET 构建仅内嵌已提交的 ICO。

`ApplicationIcons.BindWindow` 绑定子窗体的语义和 DPI 事件。资源字节共享，每个窗体独立持有 HICON 缓存，在原生窗口和子控件销毁后的 Disposed 事件统一释放。跨 DPI 选择缓存帧。子窗体显示标题栏图标，保留模态 owner，不显示额外任务栏按钮。
