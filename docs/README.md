# 文档索引

- [user-guide.md](user-guide.md)：当前用户指南，包括使用方法、数据保护、配置、升级和状态语义。
- [releasing.md](releasing.md)：构建、四配置打包、校验和 GUI 发行验收流程。
- [localization.md](localization.md)：语言资源和独立 UI 偏好的维护方式。
- `releases/`：正式版本的历史说明、验证结论与发行矩阵。

## 版本记录

| 版本 | 说明 | 验证 |
| --- | --- | --- |
| v0.1.0 | [历史 README](releases/v0.1.0/README.md) | [验证记录](releases/v0.1.0/validation.md) |
| v0.1.1 | [历史 README](releases/v0.1.1/README.md) | [验证记录](releases/v0.1.1/validation.md) |
| v0.2.0 | [CHANGELOG](../CHANGELOG.md) | [封版及后续源码验证](releases/v0.2.0/validation.md)、[四配置发行矩阵](releases/v0.2.0/release-matrix.md)、[本地化验证](releases/v0.2.0/localization.md) |

历史文档描述当时交付状态，不代表所有后续源码改动已经进入封版 EXE。当前功能以用户指南及源码为准。构建日志、临时预览、候选 ZIP 等生成物放在 Git 忽略的 `artifacts/`，不写入普通文档目录。
