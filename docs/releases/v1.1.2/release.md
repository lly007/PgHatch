# PgHatch v1.1.2

PostgreSQL, the App Way.

PgHatch v1.1.2 是 Issue #7 对应的维护版本。维护者已验收修复提交 `8286b3095dfd216fdeecbacd3e22ccd2f411c27f` 的真实 Windows GUI 表现，并授权正式发行。

## 本版修复

- 在手动注册 PostgreSQL、修复已登记程序路径、扫描导入中的新增程序登记及下载部署时，检查实际 bin 和可确认的 share 路径。发现非 ASCII 字符时提供原生兼容性提醒，默认返回修改，允许用户继续。
- 下载前检查部署目标，包准备完成后在提交文件和自动登记前核验实际程序资源路径。
- 创建实例前保留独立的程序路径确认。中文 PGDATA 可正常使用，风险提示根据 PostgreSQL 程序资源路径判断。
- initdb 失败显示简短原因和文件保留说明，通过独立窗口查看、选择和复制脱敏技术诊断，保留退出码、超时状态及 PGDATA 路径。
- 结合已知 UTF8 非法字节错误、非零退出、程序路径及英文或准确中文 post-bootstrap 阶段谨慎分类。证据不足时给出编码待查或通用提示。

## 数据与兼容性

部分 Windows PostgreSQL 构建在非 ASCII 程序资源路径下可能遇到编码兼容性问题。实际 PostgreSQL 18.6 实验中，ASCII 程序路径配合英文或中文 PGDATA 均成功，中文程序路径配合英文 PGDATA 在 post-bootstrap 阶段退出 1。风险随版本、构建和路径字符变化。[Issue #7 验证记录](../../issue-7-validation.md)

初始化沿用 UTF8、默认 locale、SCRAM、密码文件及 `--no-clean` 参数。配置 schema 2、既有数据库身份核验、生命周期互斥、登记机制和 PGDATA 安全策略继续适用。失败文件保留，程序部署位置由用户明确选择。

## 发行包

Windows x64 和 ARM64 各提供 Standalone、Framework 两种 ZIP，并提供外部 SHA256SUMS.txt。Standalone 自带 .NET，Framework 需要对应架构的 .NET 10 Desktop Runtime。升级时退出旧管理器，保留配置、PGDATA、日志及许可文件。[v1.1.2 Release](https://github.com/lly007/PgHatch/releases/tag/v1.1.2)

四包采用同一正式版本提交全新构建，实际构建输入、包大小、SHA-256、最终 x64 冒烟和服务端发布结果由独立发行验收记录说明。[发行验收](validation.md)

## 验证与边界

修复阶段通过注册 WinForms 检查 63 项、初始化分类检查 89 项、创建及本地化检查 103 项、下载部署回归 78 项及真实 PostgreSQL 18.6 集成 75 项。维护者已确认最终修复的真实 Windows GUI 验收通过，具体 DPI 和显示器组合按维护者原始记录确认。

ARM64 Windows 实机、其他代码页和第三方扩展兼容性仍需对应环境验证。混合 UTF-8/CP936 日志可能出现部分文字乱码，已知错误识别及真实退出状态按验证范围保留。强制关机、断电和网络数据目录的既有支持边界继续适用。
