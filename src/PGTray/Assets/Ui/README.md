# 功能图标

采用 Lucide 的十个 SVG（Plus、Database、Refresh、Ellipsis、Play、Square、Rotate、FileText、Settings、FolderOpen），原始来源/固定版本见 `Lucide/SOURCE.txt`，完整 ISC 及适用的 Feather/MIT 许可见 `Lucide/LICENSE`。SVG 未修改；PNG 仅改变笔画颜色和输出尺寸。

源图统一为 24×24 viewBox、2 单位线宽、圆角端点。运行时用内嵌 PNG，不解析 SVG、不联网、不依赖 Node 或第三方 UI 库。16/20/24/32 像素对应 100%/125%/150%/200%，另有 48/64 像素。普通控件使用 #1e293b，强调按钮使用白色；禁用时统一换用深色源图后交给原生 Button / ToolStrip 灰化，避免白色强调图在禁用后仍然发亮。重新启用会恢复原色。

`UiIcons` 缓存图片、按 DPI 设置像素尺寸，标准四档直接复制对应栅格；中间档从更大图片下采样。显式处理 DPI 元数据，避免把 PNG 的 72dpi 解释为另一物理尺寸。菜单使用 ImageScaling.None，避免二次放大。图片归窗口所有，控件只借用，窗口与菜单销毁后释放。

仅修改图标时才需要重新生成：安装/提供开发工具 sharp，然后运行 `node scripts/Generate-UiIcons.cjs`。生成器不访问网络；普通 .NET 构建直接使用已提交的 PNG。完整许可证既内嵌，也复制到开发输出的 LICENSE-LUCIDE.txt。
