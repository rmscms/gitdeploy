# Deploy Theme Pack Guide

> **Shipped copy (install + AppData):** [`Themes/Packs/THEME_PACK_GUIDE.md`](../Themes/Packs/THEME_PACK_GUIDE.md)  
> That file is copied next to the EXE and into `%AppData%\GitDeployPro\Themes\` so users and tools can open it without the repo.  
> Prefer editing the Themes/Packs file; keep this docs copy aligned when the contract changes.

GitDeployPro themes are JSON packs (`gitdeploypro-theme/v1`) that control the live palette **and** Deploy-specific surfaces (Direct Upload, FTP tree, terminal, logs, editors, diffs). Selection is **app-wide** via `AppThemeId`.

## Quick start

1. Copy `%AppData%\GitDeployPro\Themes\Default.json` or `Dark.json` as a starting point  
   (also shipped in the app as `Themes/Packs/Default.json` / `Dark.json`).
2. Edit `displayName` (this is what appears in Settings / Deploy theme pickers).
3. Change colors under `palette` and `deploy.*`.
4. Settings → **Themes** → **Import theme…**
5. Select the theme in Settings (Apply) or the Deploy Theme combo — it applies **app-wide**.

Palette references use `@Key` form, for example `"@Accent.Primary"`.

Open the full guide from Settings → Themes → **Open guide**, or the file `THEME_PACK_GUIDE.md` in the Themes folder.

## Schema overview

| Section | Purpose |
|---------|---------|
| `schema` | Must be `gitdeploypro-theme/v1` |
| `id` | Stable id (sanitized); avoid `default` / `dark` for customs |
| `displayName` | ComboBox label |
| `basedOn` | `default` or `dark` — missing keys are filled from that pack |
| `palette` | App brush keys (`Surface.Base`, `Text.Primary`, …) |
| `deploy.shell` | Page / rails / docks / splitters |
| `deploy.header` | Title / subtitle |
| `deploy.branches` | Branch chip, action, rollback, push badge |
| `deploy.changedFiles` | Status badge colors (NEW / MODIFIED / DELETED) |
| `deploy.directUpload` | Tree selection, icons, git badges, mapped rows |
| `deploy.ftp.chrome` | Remote tree selection, overlays, skeletons |
| `deploy.ftp.fileTypes` | Per-type icon/badge colors (`php`, `js`, `directory`, …) |
| `deploy.logs` | Log accents |
| `deploy.terminal` | Host bg, status LEDs, xterm theme, text presets |
| `deploy.editor` | WebView2 / Monaco / AvalonEdit / SQL completion & syntax |
| `deploy.diff` | Diff line backgrounds |

## Color values

- Hex: `#RRGGBB` or `#AARRGGBB`
- Palette ref: `@Status.Success`
- Terminal selection may use `rgba(...)`

## Built-in themes

- **Default** — current slate look  
- **Dark** — full-black look  

Built-ins cannot be deleted. Export them from Settings → Themes to get a complete editable template.

## Storage

- Custom packs + guide live in `%AppData%\GitDeployPro\Themes\`
- Bundled templates + guide: `{InstallDir}\Themes\Packs\`
- Active app theme id is stored in `global_config.json` as `AppThemeId` (mirrored to legacy `DeployThemeId`)
- Theme persists across pages and app restarts (default remains `default` until the user changes it)

## Extending

New tokens should be added to:

1. `Services/Theme/ThemeTokenCatalog.cs` (defaults)  
2. `Themes/Packs/Default.json` and `Dark.json`  
3. Consumers under Deploy controls that read `ThemeService.Instance.CurrentTokens`
4. `Themes/Packs/THEME_PACK_GUIDE.md` (and this docs file)

Register additional packs at runtime via `ThemeService.Instance.ImportThemeFile(path)` or Settings import.
