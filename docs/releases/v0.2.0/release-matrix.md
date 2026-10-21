# PgHatch v0.2.0 四配置候选发行验证

结论：**统一脚本可一次生成并验证四种包；x64 两模式已从实际 ZIP 解压启动并正常退出；ARM64 仅完成构建和静态检查，真实运行未验证。** 没有改动应用业务代码、UI 布局、图标、配置格式或版本号。

最终交付目录：`artifacts/candidates/run-20261018-154218-ea705d61/`。这是 2026-10-18 的候选快照，不含随后加入的本地化和顶部间距修复；更新源码后应另行生成候选，不能覆盖这组文件。该目录的 COMPLETE.json 表示所选矩阵的构建/打包/静态检查完成，不表示 ARM64 或全部 GUI 场景已经通过。

## 使用与产物

```powershell
pwsh -NoProfile -File scripts/Publish.ps1
# 可选子集
pwsh -NoProfile -File scripts/Publish.ps1 -Architecture x64 -Mode framework
```

脚本、包结构和复验步骤见 [RELEASING.md](../../releasing.md)。宿主为 Windows x64，SDK 10.0.402，已安装 x64 Desktop Runtime 10.0.13。

| 包 | EXE 字节 | ZIP 字节 | PE 架构 | 构建 / 静态检查 |
| --- | ---: | ---: | --- | --- |
| [x64 standalone](../../../artifacts/candidates/run-20261018-154218-ea705d61/PgHatch-win-x64-standalone.zip) | 117232697 | 48175000 | AMD64 / 0x8664 | 通过 / 272 项 |
| [x64 framework](../../../artifacts/candidates/run-20261018-154218-ea705d61/PgHatch-win-x64-framework.zip) | 1295263 | 868316 | AMD64 / 0x8664 | 通过 / 272 项 |
| [ARM64 standalone](../../../artifacts/candidates/run-20261018-154218-ea705d61/PgHatch-win-arm64-standalone.zip) | 130176075 | 45989704 | ARM64 / 0xAA64 | 通过 / 272 项 |
| [ARM64 framework](../../../artifacts/candidates/run-20261018-154218-ea705d61/PgHatch-win-arm64-framework.zip) | 1274787 | 856813 | ARM64 / 0xAA64 | 通过 / 272 项 |

四个 publish 日志均无 warning/error 诊断，完整解决方案编译 0 警告 / 0 错误。所有版本是 Release / Single-file；SelfContained 分别显式 true / false；Trimming、Native AOT、ReadyToRun 均显式 false。只发布 GUI csproj，检查器和其他测试程序未进入包内。

### SHA-256

| 版本 | EXE SHA-256 | ZIP SHA-256 |
| --- | --- | --- |
| x64 standalone | `82220215BD250AE26460DB68F108589DD8B8D71910881952AEA1D4E9603CCFBF` | `E91EC07EDEA8FB7CE9F1507C46CAE17310D705A14FA4481676B127D56351EC14` |
| x64 framework | `4A7148565864BCA9488FC3EBAE2B26E0FCC6ED938B6B7489CF89EDC6448DCDB1` | `E72CC20902C1DEA7485FC72545A536DC30011657B7A3FA3F6C561B75BB485D60` |
| ARM64 standalone | `3DF51FE8198FB8C09754816E7B5B124ABB0C7CA68915813DE8D8333ABA0DF295` | `72B56F1730C1970C033D4792EC3406A2DC50C84BCE10782420E2D4FAB0831B2C` |
| ARM64 framework | `03B9AD252B2AF1C0C37E246918851011B4B9071563FEE560D62CCD30BB5DC3FD` | `999BE767F4ACC4524430D39AEEA20438C5C74178E44FCB5539412ECB61760083` |

机器可读数据见候选目录的 `RELEASES.json`、`SIZES-AND-HASHES.csv`、`SHA256SUMS.txt`。ZIP 的时间戳会影响哈希，流程可重复执行不等于承诺每次 ZIP 字节完全相同。

## 验证覆盖

- 实际解析四个 EXE 的原生 PE Machine、GUI subsystem、版本、单文件 bundle v6、runtimeconfig 和 deps RID。
- standalone：实际包含 CoreLib 与运行时，runtimeconfig 声明 includedFrameworks；framework：不捆绑 CoreLib/运行时，声明 .NET 10 Core + WindowsDesktop 的 frameworks。不能仅用命令参数推断发布模式。
- 逐字节核对两套内嵌品牌 ICO、所有功能 PNG、Lucide/Feather 许可；原生 EXE 的九帧 RT_ICON 数据与正式 ICO 一致，应用 manifest 完整。
- 验证应用 IL 无 ReadyToRun 原生头。检查器只静态读取 PE/metadata，不执行 ARM64 EXE 或加载 ARM64 DLL。
- 全部保留 clean publish 输出；四包许可文件完整，自包含包另附实际 .NET Core / Desktop 运行库许可。无 PostgreSQL 二进制、用户配置、PGDATA 或测试程序。
- 四包均实际解压并核对全部文件数量及 SHA-256，ZIP 回读一致。
- 可选 `x64/framework` 子集已在 `D:\PGTray-Test\release-matrix-20261018\候选 输出 [校验]&` 成功发布。该测试发现并修复 PowerShell 日志重定向对 `[]` 的通配符展开；改用 Out-File -LiteralPath。日志（结论已归并）。
- 错误 RID、错误模式、历史发行目录作为输出根目录均被拒绝。负向测试（结论已归并）。开发期间的失败会话保留 FAILED/INCOMPLETE，无 COMPLETE 成功标记；未冒充成功发布。

## x64 实际 GUI 与运行时行为

通过 computer-use 技能操作从最终 ZIP 解压出的独立目录，未在源码 bin 目录运行替代发行验收。

| 项目 | 实际结果 |
| --- | --- |
| standalone 启动 | 成功；标题栏品牌图标、主窗口和功能图标可见。子进程 DOTNET_ROOT 指向测试空路径，宿主使用内部 hostpolicy、CoreCLR path 为空，未加载全局 dotnet/shared 模块。不是卸载 Runtime 后的干净虚拟机测试 |
| standalone 程序注册 | GUI 注册官方工具目录，识别 PostgreSQL 18.6，写入本次解压目录的隔离 pghatch.json；没有添加/启停真实 PGDATA |
| standalone 隐藏与重复启动 | 关闭主窗口后窗口隐藏，PID 6016 继续；重复启动同一 EXE 恢复原窗口，仍只有一个管理器 |
| standalone 退出 | 正常退出，实际退出码 0 |
| framework 启动 | 成功；host trace 明确解析 Microsoft.NETCore.App 与 Microsoft.WindowsDesktop.App 10.0.13 |
| framework 配置/刷新 | 读取专用测试配置中的两个已停止集群；手动刷新仍显示停止，127.0.0.2 与端口 50736 保持原样，启停按钮禁用状态符合停止状态；未执行 GUI 启停 |
| 跨模式单实例恢复 | framework 隐藏后运行 standalone，恢复的是既有 framework 窗口，仍只有 PID 8396；没有创建第二个管理器 |
| framework 退出 | 正常退出，实际退出码 0 |
| bundle 提取行为 | 两种 x64 包在独立 DOTNET_BUNDLE_EXTRACT_BASE_DIR 下都没有产生提取文件；当前 standalone 宿主集成原生运行库，不能要求必须存在独立 coreclr.dll bundle 条目 |

当时的程序注册、主窗口、状态刷新、退出码和宿主跟踪结论已归入上表；实际运行 EXE 哈希与最终交付 ZIP 一致。逐轮截图和临时宿主跟踪文件不再单独保留。

测试目录为 `D:\PGTray-Test\release-matrix-20261018\standalone` 与 `framework`。宿主 trace / 模块记录来自启动辅助脚本，只设置子进程环境；没有修改全局 PATH、卸载 Runtime 或更改系统设置。

**人工待验**：通知区域图标的实际外观和双击托盘恢复（本轮验证的是关闭隐藏、重复启动恢复，不冒充托盘双击）；不同 DPI/任务栏主题；全新无 .NET 的 x64 虚拟机。工具未暴露可可靠操作的任务栏/通知区域，不能用静态图片替代这些场景。

## 原有回归

- 图标与资源：**219 PASS**，日志（结论已归并）。
- 配置管理/迁移：**75 PASS**，日志（结论已归并）。
- 真实 PostgreSQL 18.6：**66 PASS**，日志（结论已归并）、测试结果（结论已归并）。
- PostgreSQL 测试集群只建立在 `D:\PGTray-Test\run-20261018-233406-d85091b5`，结果记录 `All owned clusters stopped: True`。GUI 只读取这些本轮专用集群；结束又以 pg_ctl status 只读核对两个集群均无服务器运行。

## ARM64 边界与源代码审查

**构建成功：是。静态检查通过：是。真实 ARM64 Windows 运行：未验证，当前没有 ARM64 设备。** 不宣称 ARM64 完全兼容，两个 ARM64 包均需在对应设备上完成启动、托盘、配置、状态、启停和退出验收；x64 PostgreSQL 的 ARM64 仿真运行另测。

应用自有 P/Invoke 为 iphlpapi GetExtendedTcpTable、kernel32 GetACP。指针使用 IntPtr，TCP 表字段为固定 DWORD/字节数组，不依赖 x64 指针大小；没有自带第三方原生 DLL。进程用 ArgumentList，路径/环境设置无 CPU 限制；窗口、托盘、单实例与恢复使用 .NET/WinForms API。图标资源与架构无关。没有发现必须改动业务代码的架构问题，也没有添加 PgHatch/PostgreSQL 架构必须一致的限制。

## 历史与数据保护

脚本每次核对历史 PgHatch/PGTray 发行目录和归档。最终候选的 history-before / after / final 一致，历史 v0.2.0 EXE、ZIP、校验文件均未覆盖。

当时的独立保护核对 对 **189 个**现有应用源码/资源、封版文件及真实 PostgreSQL 配置进行前后 SHA-256 比较，全部不变。真实 `D:\PostgreSQL\PGData\main` 未初始化、未改配置、未启停；没有清理真实数据或日志。

仅变更发行脚本、检查/测试工程、解决方案测试登记和文档。中间失败会话未被计为成功发行，后续整理只保留最新完整矩阵，本次交付以 `run-20261018-154218-ea705d61` 为准。
