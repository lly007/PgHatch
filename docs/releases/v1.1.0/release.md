# PgHatch v1.1.0

PostgreSQL, the App Way.

## 主要变化

- 同主版本 PostgreSQL 维护更新后，显式核验并恢复程序登记与原实例关联，保留 PGDATA、端口、名称和稳定 ID，不自动启动。
- 程序异常时提供严格身份核验的应急 fast stop；备用工具由用户明确选择，控制器超时期间保持安全互斥，不强杀 PostgreSQL。
- 配置重读拒绝外部改变已有实例的 PGDATA 目标、删除登记或改变可信映射；失败保留旧配置供查看，阻止保存和危险操作。
- 加固映射盘/UNC 身份、reparse 路径、停止确认关键文件、初始化后登记失败恢复和临时凭据清理。
- About 改为原生模态对话框，保留版本、产品标语、社区声明及 Lucide/Feather 说明，新增可点击 GitHub 链接与三语言错误提示。

## 下载与兼容性

Windows x64 / ARM64 各有 standalone 和 framework 两种单文件包。Standalone 自带 .NET；Framework 需要对应架构的 .NET 10 Desktop Runtime。四个 ZIP 与 SHA256SUMS.txt 已在 [v1.1.0 Release](https://github.com/lly007/PgHatch/releases/tag/v1.1.0) 提供；准确字节数和校验值以正式发布资产及[验收记录](validation.md)为准。

配置 schema 2、默认初始化参数和历史配置语义保持不变。升级时保留原配置和 PGDATA。同主版本维护更新须先停止所有受影响实例并检查扩展；本版本不提供 PostgreSQL 跨主版本升级或自动数据迁移，不自动选择 latest/最大版本，不合并不同路径的程序登记。

## 已知限制

- ARM64 已交叉构建和静态检查，尚未在真实 Windows ARM64 设备运行验证；不扩大 x64 PostgreSQL 在 ARM64 上的兼容承诺。
- 实际符号链接测试因 Windows 特权不足跳过；junction、映射及确定性属性检查的通过不等于真实符号链接或关键文件 ACL 验证。
- 在既有 ACP936 测试中，PostgreSQL 18.3 的中文 share 路径触发真实 initdb post-bootstrap UTF-8 错误，直接调用及 PgHatch 均复现；中文 PGDATA 本身已排除为原因。具体供应商二进制转换点仍未完全确定，未擅改编码/locale 或加入规避逻辑。详见[调查记录](../../final-reliability-review.md)。
- 核心工具一致不代表第三方扩展兼容；网络数据库运行、DFS/服务器别名的全面物理等价、真实系统关机与全部 DPI/跨屏组合不增加支持承诺。

维护者已完成人工 GUI 验收并实际确认 About 链接打开系统默认浏览器。程序化测试、实际运行冒烟和人工结果分别记录，完整核验见[发行验收](validation.md)。

## 正式构建与校验

构建输入 / v1.1.0 标签：`0f412a1680b3340ad1262880ad3c1feca67d9c16`。四种包均从该干净 main 全新构建，各通过 277 项静态检查、ZIP CRC 和包内全部 SHA-256 核验，均包含 About 补丁。x64 两种实际解压包的主窗、1.1.0、原生 About 与 GitHub 链接通过冒烟，正常退出码均为 0；Framework 使用 Desktop Runtime 10.0.14，Standalone 未加载全局 .NET 模块。

维护者已完成人工 GUI 验收，并明确确认实际点击 About 链接打开系统默认浏览器。既有 969 PASS / 2 SKIP 业务回归继续有效；本轮未新增业务源码。

| ZIP | 字节数 | SHA-256 |
| --- | ---: | --- |
| PgHatch-win-x64-standalone.zip | 50116375 | `87513345E597BC07B13CCD984CB093AB06AB25F0874CA5C5B9B52EAA888C2A9D` |
| PgHatch-win-x64-framework.zip | 2818126 | `95A7056386A4BBD6927876F2CD3D886BBC6CE251E6EB99EBAC4910927B3E4051` |
| PgHatch-win-arm64-standalone.zip | 47970662 | `EED7172A94226D30FECC3B4AE633F725D40EC46CE696EEF2B8BBFC672338D431` |
| PgHatch-win-arm64-framework.zip | 2806183 | `6D058080530B83D8D6758639D0E856218D86230404AD00479E94E0DCDBA64189` |

随附 SHA256SUMS.txt 只列四个正式 ZIP。请在解压前核对校验值，保留包内许可证。历史版本与制品保持不变。
