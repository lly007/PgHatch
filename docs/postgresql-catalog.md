# PostgreSQL 软件包目录

本阶段只提供 ZIP 元数据，不下载、解压、注册程序，也不启动数据库。PgHatch 的 ARM64 版本同样展示 Windows **x64** PostgreSQL 包；这不表示存在已确认的 Windows ARM64 PostgreSQL 原生包。

## 正式母版与来源

唯一母版是 [catalog/postgresql-catalog.xml](../catalog/postgresql-catalog.xml)，在线地址为：

<https://raw.githubusercontent.com/lly007/PgHatch/main/catalog/postgresql-catalog.xml>

2026-10-25 核对了 [EDB 二进制页面](https://www.enterprisedb.com/download-postgresql-binaries)及官方仓库公开回复。四个条目均为 revision **5**，仅收录稳定版 Windows x64 **binaries.zip**：

| PostgreSQL | EDB 页面文件 ID | 供应商 SHA-256 确认 |
| --- | --- | --- |
| 18.6 | 1260609 | [EDB #746](https://github.com/EnterpriseDB/edb-installers/issues/746#issuecomment-6283764485) |
| 17.11 | 1260616 | [EDB #731](https://github.com/EnterpriseDB/edb-installers/issues/731#issuecomment-6014410619) |
| 16.15 | 1260623 | [EDB #731](https://github.com/EnterpriseDB/edb-installers/issues/731#issuecomment-6014410619) |
| 15.19 | 1260630 | [EDB #749](https://github.com/EnterpriseDB/edb-installers/issues/749#issuecomment-6334213046) |

逐项对 EDB 页面公开的 `sbp.enterprisedb.com/getfile.jsp?fileid=...` 链接发送 HEAD，核实其重定向至 XML 中对应的 `get.enterprisedb.com` ZIP 地址，最终返回 200 / application/zip。没有下载 ZIP，也没有把安装 EXE 的校验值用于 ZIP。15.19 的校验值已与公开回复逐字符核对；14.24 缺少本轮已核实的供应商 ZIP 校验值，暂不收录。完整 SHA-256 和确认链接保存在母版中。

维护时必须重新核对 EDB 当前稳定版本、页面实际链接、完整文件名和打包修订号、官方校验来源，不能按文件名模板猜测新地址或枚举修订号。本文记录核验时的事实，不是自动爬取页面的规则。

## 使用与回退

在“PostgreSQL 程序”窗口打开“PostgreSQL 软件包目录”。进入时按本地 XML 的 `intervalHours` 检查；“刷新在线目录”可显式立即检查。程序启动及其他管理操作不会因此联网。

- `PostgresCatalogService` 接收发行 XML 路径和现有 `ConfigStore`，缓存位于 `ConfigStore.Root/cache/`。当前便携应用的配置根目录与 EXE 目录相同；缓存独立放入其子目录，不覆盖 EXE 旁的发行 XML，不另建全局配置体系。
- 有效在线 XML 原子保存为 `postgresql-catalog-<来源 URL 的 SHA-256>.xml`。不同本地更新源使用不同缓存，改源后不会误用之前来源的缓存。
- 同名 `.checked` 零字节时间戳记录最近检查，用于跨重启限制自动检查频率；它不是有效目录，也不会替代有效缓存。手动刷新可绕过间隔。
- 在线失败或数据无效时，按“有效缓存 → 发行 XML”回退，并在窗口显示来源与原因。缓存损坏不会导致发行 XML 被清空。有效在线数据但缓存写入失败时，可在本次会话展示在线数据并提示缓存错误。
- 本地发行 XML 是来源信任锚。在线 XML 中的 Update 不得改变更新地址或检查间隔；返回对象也保留本地设置。本地 XML 缺失或无效时明确报错，不能从缓存中自行选择更新源。
- XML 上限 1 MiB、HTTPS 请求总时限 15 秒、最多 5 次重定向；禁止 HTTP 降级、凭据 URL、DTD 和外部实体。严格检查 schema 1、字段、稳定版本、修订号、x64 / EDB、精确匹配的 ZIP 地址、64 位十六进制 SHA-256 以及重复条目。
- 软件包地址仅接受 `get.enterprisedb.com` 默认 HTTPS 端口上的匹配 ZIP 路径。未来下载器必须在每次请求及重定向前调用 `PostgresPackage.ValidateDownloadLocation`，并再次核对最终地址，禁止系统自动跟随到其他域名。目录更新 URL 则由用户控制的本地 XML 指定，可以使用其他 HTTPS 目录服务。

SHA-256 用于将未来下载的 ZIP 与目录记录比对，**不构成 XML 的独立可信签名**。在线 XML 的维护权限由 GitHub 仓库控制；校验来源链接便于人工审计。

## 可复用接口与验证

`PostgresCatalog.Parse(byte[])` 严格解析；`PostgresCatalogService.GetAsync(forceRefresh, allowOnline, cancellationToken)` 返回目录、来源和可选告警。`Catalog.Packages` 提供 Version、Revision、Architecture、Provider、DownloadUrl、SHA256、ChecksumSource；列表只读。原有实例和 JSON 配置格式不变。

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --catalog-tests catalog/postgresql-catalog.xml artifacts/dev/catalog-tests
# XML 提交到 GitHub 后，实际读取 Raw 并与母版逐字节比较，再验证离线缓存回退：
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --catalog-online catalog/postgresql-catalog.xml artifacts/dev/catalog-online
```

专项测试使用隔离目录和可控 HTTP 传输验证正常读取、离线回退、无效数据、来源固定、缓存原子替换失败、中断、超时、大小限制和检查间隔。第二条在线测试实际通过 HTTPS 读取 GitHub Raw；不以 Mock 结果替代在线发布验证。GUI 的三语言标题和资源检查复用现有图标/本地化测试。真实桌面的缩放、键盘导航、关闭窗口取消请求仍需人工验收。

四种发布模式都会在 EXE 旁复制同一母版，并将其纳入发行校验清单及 ZIP 解压回验。该文件故意保持外部形式，必须随发行包保留。当前历史 v0.2.0 Release 不重打包、不替换；本轮代码和 XML 提交到 main，新候选包在独立目录生成。
