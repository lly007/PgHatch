# PgHatch UI localization

The current source and release version is **1.0.1**, a window-layout maintenance update with no changes to shared UI resources, preferences or business logic. This document describes the current shared UI resources and preferences. The window-restore checks cover all three languages; see [1.0.1 release notes](releases/v1.0.1/release.md). Historical localization checks remain under `releases/v0.2.0/`, and baseline feature acceptance is recorded in [V1.0 validation](releases/v1.0.0/validation.md).

## Resources

The shared, dependency-free `PGTray.Core.L10n` helper uses the native `ResourceManager`. Resource files live in `src/PGTray.Core/Resources/`:

- `Strings.resx`: neutral English (`en-US`).
- `Strings.zh-CN.resx`: Simplified Chinese.
- `Strings.zh-TW.resx`: Traditional Chinese, using Taiwan desktop terminology.

Resources cover the main window and dialogs, toolbars, menus, tray, scanning, configuration recovery, downloading/deployment, initialization, maintenance-version switching and shutdown messages. Application-authored messages use these resources. Raw PostgreSQL output, logs, file paths, identifiers and configuration keys are not translated. Native Windows dialog chrome/buttons can remain in the Windows language. Displayed versions and HTTP user agents use the build version from `Directory.Build.props` through `AppVersion`.

Use semantic resource keys and `L10n.Format` for messages with arguments. Preserve placeholder indexes in every translation. English instance/process counts have explicit singular and plural resources. Language names deliberately use identical native names in all resource sets. Preserve CRLF as `&#13;` followed by a newline in multiline resources intended for native text controls.

## Preference and startup

`pghatch.ui.json` is a separate portable preference file next to the EXE:

```json
{ "Language": "en-US" }
```

Accepted values: `null` (follow Windows), `zh-CN`, `en-US`, `zh-TW`. A missing file or field follows Windows without writing anything. Simplified Chinese regions/scripts map to zh-CN; TW/HK/MO/Hant map to zh-TW; other languages use English. The chosen UI culture flows to asynchronous work without changing the user's numeric/date culture.

More → Language saves the preference and explains that a restart is required. Current windows keep their language. The menu remains usable when PostgreSQL configuration cannot load. Unknown preference properties are retained; invalid preference files are reported and preserved, and external edits block a stale save. Repair a damaged preference file through the configuration folder and restart. The PostgreSQL `pghatch.json` file, schema 2 and legacy migration are unchanged.

## Logo and layout

`Assets/Brand/Masters/PgHatch.transparent.png` is embedded as `PgHatch.Brand.Header.png`. The original asset and ICOs are unchanged. A noninteractive PictureBox owns a cloned image and releases it after the form is disposed. It uses Zoom, a square 100-logical-pixel area, 80 pixels at intermediate widths, and hides below the available-width threshold. It has no border or keyboard stop. Existing EXE/window/tray ICO handling is unchanged.

DPI scaling stays PerMonitorV2. Long welcome text is measured against available width, headers can wrap and retain sufficient minimum width, and shared layout dialogs fit their measured content to the monitor work area. Scrolling remains available when the work area is too small. No language-dependent business branches or new UI framework are introduced.

## Checks

Run from the repository root:

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IconChecks -c Release -- <new-output-directory>
dotnet run --project tests/PGTray.IntegrationTests -c Release -- --config-tests D:\PostgreSQL\PostgreSQL artifacts/dev/config-tests
dotnet run --project tests/PGTray.IntegrationTests -c Release -- D:\PostgreSQL\PostgreSQL D:\PGTray-Test
```

The existing icon checks now include lightweight aggregate localization checks: resource key/placeholder parity, valid formatting, missing references, hardcoded Chinese UI text, fallback, language preference persistence and error preservation, plural counts, all form constructors, tray/menu text and logo bounds. They do not replace real desktop DPI or interactive dialog acceptance tests. Integration tests create their own clusters; never point their test parent inside real PGDATA.

The existing publish script includes the culture satellite assemblies through standard .NET single-file bundling. Do not delete satellite assemblies from ordinary multi-file build output. Sealed release files must remain untouched.
