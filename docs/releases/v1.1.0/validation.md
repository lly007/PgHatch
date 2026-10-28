# PgHatch v1.1.0 候选验收

本版本处于发行准备阶段，尚未创建正式标签或 GitHub Release。

- 合并 PR：https://github.com/lly007/PgHatch/pull/1。
- Merge Commit：`aed6945b56d462cfe01a60bd8ccc1432d171b7b8`；父提交保留 main 和已审查的 `dd2214f399485035e58757ea9e05ac5a328597f7`，三个可靠性提交完整保留。
- 发行准备分支：`release/v1.1.0`。仅修改版本元数据和文档，业务代码、测试、发布脚本、Catalog、配置 schema 2 和默认初始化参数与合并后的 main 一致。
- 当前准备版本：1.1.0；正式发布仍为 1.0.1。
- 本轮完整回归、四配置候选及 x64 启动结果正在执行，将在发行验收完成后更新；本段不是通过声明。

ARM64 无真实设备，实际符号链接受 Windows 权限限制，PostgreSQL 18.3 中文 share 路径的既有问题保留为限制；不能用静态检查、故障注入或 ASCII 路径成功来声称这些边界已通过。历史发行包、用户 PGDATA 和全局系统权限保持不变。
