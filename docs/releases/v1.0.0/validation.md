# PgHatch 1.0.0-rc.1 候选审计与验收

## 2026-10-27 最终源码重建（当前候选清单）

本节为现行发布状态；下方前轮审计、测试数字及旧候选信息保留为历史记录，不能当作本轮重新执行的结果。

- 版本：源码 / 本地候选 **1.0.0-rc.1**，正式 GitHub Release 仍为 **v0.2.0**。未创建或修改正式 Release。
- 构建输入：提交 `40bacb15f4352889313ff1f5c751ad6ebc63e4f3`，发布开始时工作区干净，无未跟踪文件。空 CatalogForm.resx 已不存在、未跟踪，无显式构建引用，未重复修改或删除其他资源。现有脚本为四配置分别建立全新临时构建输出，不复用旧候选或源目录的 bin/obj。
- 文档：统一 README、CHANGELOG、用户指南、本地化说明中的版本表述和当前入口，修正旧版残留说明。版本号和产品代码没有改变。
- 维护者在本次任务中确认已人工验证下载部署、实例创建、同主版本更新、Windows 关机、DPI 和托盘等核心行为。此为维护者人工验收结论，与下列本轮代理执行的构建/冒烟分开记录；没有推定具体旧维护版本或未提供的设备组合。
- 本轮不重跑完整功能回归、不下载 PostgreSQL ZIP、不创建或启停数据库实例。

新候选目录：`artifacts/candidates/run-20261027-014816-3c355019/`。SDK **10.0.403**，应用版本不变。此前候选均保留，不再作为最新交付。

重建后仅补记本报告和外部制品清单中的验证结果，没有再修改应用源码、版本或随包文档输入；事后追加的验收记录提交不影响上述构建对应关系。

| 包 | EXE 字节 | ZIP 字节 | 静态 / ZIP 校验 | 本轮启动 |
| --- | ---: | ---: | --- | --- |
| PgHatch-win-x64-standalone.zip | 119,312,516 | 50,085,862 | 277 项 / 通过 | 通过，正常退出码 0 |
| PgHatch-win-x64-framework.zip | 3,375,082 | 2,790,985 | 277 项 / 通过 | 通过，正常退出码 0 |
| PgHatch-win-arm64-standalone.zip | 132,255,894 | 47,944,873 | 277 项 / 通过 | 未执行，无 ARM64 主机 |
| PgHatch-win-arm64-framework.zip | 3,354,606 | 2,777,918 | 277 项 / 通过 | 未执行，无 ARM64 主机 |

四包均为 Release 单文件，自包含 / 运行时依赖配置、原生 PE 架构、多语言与品牌资源均由现有发行检查器验证；ZIP 全文件解压回验通过。发行 XML 与源母版逐字节一致，MIT 项目许可、Lucide/Feather 许可及第三方声明齐全；自包含版含运行库许可证。发布日志无构建警告或错误。

完整 SHA-256（同时保存在候选目录的 SHA256SUMS.txt、SIZES-AND-HASHES.csv；RELEASES.json 附源码提交和启动结果）：

```text
19B2CC591E0D282E193D99A7CD9D17988D9A5F28023AE7E5AC65C6BFDEFF7CB3  PgHatch-win-x64-standalone.zip
A7A2643A85AF0BC4F09B9F8BBB1C9999F77C77A597968FAB53524D24E83AF133  packages/PgHatch-win-x64-standalone/PgHatch.exe
CCF7814954B80A9ECA3A0ABAD3DAA853F8E762B2C6E6E4D3C7C63F43B0FD192A  PgHatch-win-x64-framework.zip
9CFD78FBA359B5BCE4BD945DCFE0BE9245B13F8FFF387F92B274D26DACD2DD05  packages/PgHatch-win-x64-framework/PgHatch.exe
A8369F60CF5EEF9DA9393D16EDD684CE9A5992CFF678EC13CF5973B0BCC09512  PgHatch-win-arm64-standalone.zip
EB5378D595CAA1E12AE7B1445D40F17BF583081D86CF1D9B587D25B042FF4511  packages/PgHatch-win-arm64-standalone/PgHatch.exe
FE1E7644F572D692D603F8A10630D7B445690EF859BA6B694772CE0D285F5B23  PgHatch-win-arm64-framework.zip
0D927747B4A5369F554896E36901F13D2415E79D1A1579CC79033D81865DB9BC  packages/PgHatch-win-arm64-framework/PgHatch.exe
```

启动证据位于 `artifacts/dev/rc-final-smoke/run-20261027-014816-3c355019/`。两个 x64 ZIP 均在独立、有归属标记的解压目录运行，主窗显示 1.0.0-rc.1 和空实例列表；均经 Ctrl+Q 正常退出，退出码 0。Standalone 的子进程 DOTNET_ROOT 指向空位置，未加载全局 dotnet/shared 模块；Framework-dependent 实际加载本机 Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 10.0.14。未改动系统运行时或全局环境。

本机为 Windows x64，没有可用的真实 Windows ARM64 测试主机。ARM64 两包仅完成交叉构建、PE/资源/运行时声明和压缩包静态检查；不宣称 ARM64 实机验证通过，也不把管理器 ARM64 当作原生 ARM64 PostgreSQL 下载支持。

**当前状态：未发现新的代码、文档或制品发布阻塞。空模板与源码不一致问题已消除，核心人工行为按维护者确认通过；ARM64 实机仍是已披露的验证缺口，正式声称其已实测前需要补齐。本轮只交付 RC，不自动创建正式 Release。**

发布脚本前后核对的 31 个历史制品文件 SHA-256 一致。前轮文件与测试结果不改写，旧路径不再表示当前最新候选。

---

## 前轮审计与验收（历史记录，原样保留）

验收日期：2026-10-27。目标是进入 V1.0 正式发布流程，尚未创建正式 Release。当前正式 GitHub Release 仍是 2026-10-21 的 v0.2.0。

## 审计结论

| 范围 | 源码与验证结论 |
| --- | --- |
| 异步进程 / 初始化 | 保留已有 WaitForExitAsync、并发管道读取及初始化预检的异步实现；未发现需要重写的进程框架。真实 initdb 的 UI 心跳结果见下方验收。 |
| PGDATA | 默认 AppContext.BaseDirectory/data/安全实例名；手动修改路径后改名不会覆盖；不迁移导入路径。复用已有保护和回归。 |
| 程序检测 | 原刷新只检查存在性，注册只读取 postgres 版本，无法可靠识别同路径维护更新及工具混装；已修复。 |
| 程序切换 | 原实现允许运行中重新关联；现按维护更新要求先确认停止，再提交关联，保留 PGDATA 与旧程序。 |
| 下载 / 解压 | 保留现有流式下载、有效完成标记、严格 Range、重定向及 SHA-256；部署保留私有暂存、路径/链接检查、CRC、体积和目标保护。 |
| 关机 | 保留已确认结束会话后的有界收尾，不因询问或取消关机停库；不将模拟消息和正常停库测试冒充实际 Windows 关机。 |
| 代码质量 | 收敛重复的版本来源，避免程序集、界面和请求头分叉；程序版本检查共用实现，定时轮询用文件指纹避免反复启动完整工具集；实机发现的新建/设置表单固定高度和说明宽度问题收敛到原有 LayoutForm 内容适配，不迁移设计器。未发现有依据的大规模结构重构需求。 |
| 路径与状态职责 | PathSafety 保护 PGDATA，DeploymentPaths 处理不可信 ZIP 与部署边界，职责不同，保留。GUI 的操作禁用状态与管理器的生命周期锁用途不同，未机械合并。 |
| 许可证 | 维护者明确选择 MIT，新增 LICENSE，保留 Lucide ISC / Feather MIT 及自包含运行库许可。 |

保持 schema 2、历史内部名称、单实例协调对象和 pgtray.json 迁移兼容性。候选构建时工作区含有未跟踪的 `src/PGTray/CatalogForm.resx` 空模板；构建完成后，维护者确认它由 Visual Studio 自动生成并要求清理。确认没有实际资源条目和代码引用后已删除，三语言 Strings 资源不受影响。

## 自动化与真实环境证据

- Release 构建：零警告、零错误。
- 配置回归：75 项通过。
- Catalog：44 项通过；实际 GitHub Raw HTTPS 文件与本地母版逐字节相同，验证过有效缓存离线回退，没有下载新 ZIP。
- 下载 / 部署：78 项通过，覆盖无 Content-Length 已完成缓存复用、Range、不支持续传、超时、取消、哈希、恶意 ZIP 和注册失败保留。
- 受控维护更新：15 项通过，测试替身模拟 18.4 → 18.5、同路径重检、持久化、混装/缺文件/混合架构拒绝、运行中拒绝、PG_VERSION 和 PGDATA 字节保护。
- 真实 PostgreSQL：使用已有官方 18.6-5 ZIP，SHA-256 为 `e2246ba91d22345bc3d017586c09ede52d9df180b1eeb480f050445f1cad84e2`，没有重新下载。完整解压/CRC、全部工具实际版本、注册、隔离创建、启动、正常停止和日志通过。真实切换为原 18.6 → 独立部署的 18.6；切换本身不改变 PGDATA 文件和字节。未取得真实 18.4 / 18.5 包，不能宣称真实旧维护版本切换已验证。
- 真实切换专项：7 项通过，结束后确认自有集群停止，仅清理本次有标记的测试目录；源 ZIP、现有程序和用户真实 PGDATA 未修改。
- 多实例完整回归：75 项通过，包含原有状态、连接、端口、导入、扫描、数据保护及新增关机/停库切换测试；结束确认所有自有集群已停止。
- 真实 initdb 窗口响应：初始化 5675 ms，同步前缀 4 ms，181 次 UI 心跳，最大间隔 37 ms；初始化/配置提交、退出等待、双管道压力及超时回收通过。没有启动该测试集群。
- 三语言资源、图标、窗口布局和会话结束模拟：347 项通过。控件字体/比例的四档模拟不等同于真实切换系统 DPI。

本轮测试与生成物位于 Git 忽略的 `artifacts/dev/v1-audit/`，不把逐轮日志或截图放入 docs。

## 四模式候选制品

最终候选：`artifacts/candidates/run-20261027-004356-01e32e86/`，编译代码提交 `28540f6`。前两轮候选保留但已被本轮替代，以下仅列最终包。

四包均为 1.0.0-rc.1、Release 单文件，分别显式设置 SelfContained；未开启 Trimming、AOT、ReadyToRun。每包 277 项静态检查、ZIP 全文件解压哈希回验通过；核对原生 x64/ARM64 PE、运行时声明、Core/GUI 版本、品牌九帧 ICO、功能图标、Logo PNG、繁简中文卫星资源与外部 XML。英文默认资源同时由资源完整性测试覆盖。

四包均含项目 MIT LICENSE、Lucide/Feather 完整许可、第三方声明；自包含包另有实际随附的 .NET 运行库许可。没有 PostgreSQL ZIP、PGDATA、用户配置或测试程序混入。`COMPLETE.json.SelectedBuilds=4`，仅证明构建及静态回验完成；不是正式发布许可。


| 架构 / 模式 | EXE 字节 | ZIP 字节 | 静态检查 |
| --- | ---: | ---: | ---: |
| win-x64 / standalone | 119,312,516 | 50,086,077 | 277 |
| win-x64 / framework | 3,375,082 | 2,790,961 | 277 |
| win-arm64 / standalone | 132,255,894 | 47,944,630 | 277 |
| win-arm64 / framework | 3,354,606 | 2,777,900 | 277 |

SHA-256（同时保存在候选目录的 SHA256SUMS.txt 和 SIZES-AND-HASHES.csv）：

```text
F3C6D53C23698F173E1C7AD23B104824B110A7FACA6B8394294019890E757BFA  PgHatch-win-x64-standalone.zip
0892972B0B4574EC9387AF62FB8BAAC4427444058F9027BD163B64EF23194F8C  packages/PgHatch-win-x64-standalone/PgHatch.exe
5DD941A8BB472BAEC7D42765800213266A1432FEB9D0F64C28E7D631C36393CA  PgHatch-win-x64-framework.zip
DAFBE8C86F8B0FFE346AEDEBDFC3D3C5676BCF92EF1CEBAC2D21FC7926B95343  packages/PgHatch-win-x64-framework/PgHatch.exe
40D3ACA65CE9CCAEB3A0D904B98324348A0FC95EBDC832FF0DCB7A15FD5EFB92  PgHatch-win-arm64-standalone.zip
2FAE8D7D127A18C539AC617B9A897138AF4D07F08882D61C284B3253B26C5260  packages/PgHatch-win-arm64-standalone/PgHatch.exe
91300F0138ABFDB80EA222828E8168345E4712D7A3B1F1E74A5C6C5CD7148891  PgHatch-win-arm64-framework.zip
570C7641CA1CD9A5EADFCE8A88802460FB63A612AD6CA78772BFD2BEDCCD45F5  packages/PgHatch-win-arm64-framework/PgHatch.exe
```

## x64 实际 GUI 与运行库验收

最终两个 x64 ZIP 均经现有 Test-ReleaseGui.ps1 验证 SHA-256 并解压到各自全新、带归属标记的目录，从解压目录启动，正常 Ctrl+Q 退出码均为 0：

- Standalone：未加载全局 dotnet/shared 模块；子进程 DOTNET_ROOT 指向空目录仍能运行；本轮未产生 bundle 提取文件。没有卸载任何运行时或改变全局环境。
- Framework-dependent：实际加载本机 Microsoft.NETCore.App / Microsoft.WindowsDesktop.App 10.0.14；宿主跟踪、运行模块与 Desktop Runtime 声明一致。
- 当前显示器缩放下，简体、英语和繁体主窗口实际显示正确；英语程序管理、下载与实例设置、繁体新建及程序管理检查过主要控件和按钮。最后一轮英语设置表单无初始滚动条，帮助文字用满内容宽度。
- 在隔离自包含配置注册已有只读程序，新建 v1_gui，实际 PGDATA 为 EXE/data/v1_gui、独立端口 55624；观察到初始化状态和冲突按钮禁用，完成显示 Stopped。没有启动该 GUI 测试集群。
- 用已有完整官方 ZIP 的隔离缓存验证实际校验/解压阶段关闭下载窗口：安全取消，最终目标目录不存在、暂存目录为零、登记数未增加；没有为 GUI 测试重新下载大型文件。真实网络传输/Range/无长度取消等由本地 HTTP 专项测试覆盖，未声称重新实测完整 EDB 网络传输。
- 实测关闭主窗隐藏到托盘，进程仍响应；重复启动恢复原窗，无第二管理器；普通退出不影响数据库的语义由真实双实例回归另行验证。未把此结果替代托盘图标双击与所有菜单的完整人工验收。
- 使用英语 More → Language，通过键盘选择简体中文，看到下次启动生效提示；确认 pghatch.ui.json 保存 zh-CN，重启实际显示简体。自动测试另外覆盖三种手动偏好及系统语言映射。
- 最终首次启动的繁体包实测无程序时匯入提示，确认后进入程序管理，取消后没有导入/启动实例。

以上是当前显示环境的实际抽查，并非三语言 × 四种系统 DPI × 所有窗体的完整人工矩阵。测试目录中的 GUI 集群均无 postmaster.pid，所有测试应用已正常退出。

## 人工验收与发布门槛

- 真实 ARM64 Windows 上两种模式、x64 PostgreSQL 仿真兼容性：当前主机无法验证。
- 100% / 125% / 150% / 200% 四档系统 DPI 和跨屏行为：自动比例检查之外需补完整实机验收。
- Windows 真正关机、重启、强制结束及较慢磁盘下的检查点收尾：未操作本机系统电源，需人工验收。
- 第三方扩展与新维护版本的兼容性不能仅凭基础工具版本证明，用户需确认扩展要求并做好备份。
- 托盘图标双击、跨屏与完整三语言/四 DPI 手工矩阵尚需补齐；当前只记录以上实际做过的抽查。
- CatalogForm.resx 的归属已由维护者确认并清理，不再作为待决事项。上述候选包仍包含构建时的空资源，未重新打包或修改哈希；后续从清理后的源码构建时，二进制哈希可能不同。

没有已知未修复的高优先级数据安全缺陷时允许生成候选；上述实机验收仍是正式 V1.0 发布门槛，不自动创建 GitHub Release。

## 历史制品、提交与工作区保护

正式 GitHub v0.2.0 Release 未创建、替换或重新上传；历史本地文件在每轮发布前后均经脚本核对全部 SHA-256。两个封版文件仍为：

- PgHatch-v0.2.0-win-x64.zip：`003B047D64902BDF6E86964096532868DE7A18D6DDDD887ADC43F17CE504C045`
- PgHatch-v0.2.0-win-x64/PgHatch.exe：`EF06FB7CFBDF7DADFC5FAD20CA6E0724BC3C2E7B71EFA0B60786A39A84D04A81`

本轮按功能与质量改动分开提交：`cc650ba`（维护更新与安全切换）、`cdad4fe`（版本、许可、发行校验）、`3d30249`（实机新建布局修复）、`28540f6`（共用布局收敛及最终提示）。没有强推、重写历史、修改实例 schema 或删除用户文件。

没有已知未解决的高优先级代码缺陷；候选具备继续验收条件。**正式 V1.0 Release 仍需上述人工门槛，不应将候选四包或自动化计数等同于完整发布批准。**
