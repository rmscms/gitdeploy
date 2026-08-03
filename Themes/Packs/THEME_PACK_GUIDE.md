# GitDeployPro Theme Pack Guide

This file ships with the app and is copied to `%AppData%\GitDeployPro\Themes\THEME_PACK_GUIDE.md` on first seed/update.

Theme packs are JSON (`gitdeploypro-theme/v1`). They control the **app-wide** palette and Deploy-specific surfaces (Direct Upload, FTP tree, terminal, logs, editors, diffs).

Use this document when creating custom themes, debugging colors, or when an AI/assistant needs the theme contract without the source repo.

## Quick start

1. Open the Themes folder (Settings → Themes → **Open folder**), or use:
   - `%AppData%\GitDeployPro\Themes\`
   - Bundled templates next to the EXE: `Themes\Packs\Default.json` / `Dark.json`
2. Copy `Default.json` or `Dark.json` as a starting point.
3. Edit `id` (unique) and `displayName` (label in Settings / Deploy pickers).
4. Change colors under `palette` and `deploy.*`.
5. Settings → Themes → **Import theme…**, then select the theme and **Apply**.
6. The same list appears in the Deploy Theme combo. Choice is **app-wide** and persists after restart.

Palette references use `@Key` form, for example `"@Accent.Primary"`.

## Schema overview

| Section | Purpose |
|---------|---------|
| `schema` | Must be `gitdeploypro-theme/v1` |
| `id` | Stable id (sanitized); avoid `default` / `dark` for customs |
| `displayName` | Picker label |
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
| `deploy.terminal` | Host bg, status LEDs, presets drawer chrome, xterm theme, text presets |

### Useful `deploy.terminal` drawer keys

| Key | Purpose |
|-----|---------|
| `presetsDrawerBackground` | Saved-commands drawer panel |
| `presetsDrawerBorder` | Drawer bottom/edge border |
| `presetsHeaderForeground` | “Saved commands” title |
| `presetsChipBackground` | Chip around preset combo / actions |
| `deploy.editor` | WebView2 / Monaco / AvalonEdit / SQL completion & syntax |
| `deploy.diff` | Diff line backgrounds |

### Useful `deploy.editor` keys

| Key | Purpose |
|-----|---------|
| `webviewBackground` | Editor WebView host |
| `monacoTheme` | Monaco theme name (string, e.g. `vs-dark`) |
| `fallbackBackground` / `fallbackForeground` | Non-WebView fallback |
| `completionBackground` / `completionListBackground` / `completionBorder` | SQL completion popup |
| `syntaxComment` / `syntaxString` / `syntaxKeyword` / `syntaxFunction` / `syntaxNumber` / `syntaxDataType` | SQL highlighting |

## Color values

- Hex: `#RRGGBB` or `#AARRGGBB`
- Palette ref: `@Status.Success`
- Terminal selection may use `rgba(...)`

## Built-in themes

- **Default** — slate look (app default until the user changes it)
- **Dark** — full-black look

Built-ins cannot be deleted. Export them from Settings → Themes for a complete editable template.

## Storage & persistence

| Item | Location |
|------|----------|
| Custom packs + this guide | `%AppData%\GitDeployPro\Themes\` |
| Bundled templates + guide | `{InstallDir}\Themes\Packs\` |
| Active theme id | `global_config.json` → `AppThemeId` (mirrored to legacy `DeployThemeId`) |

Theme persists across pages and app restarts. Default remains `default` until the user selects another theme.

## Extending (developers / AI)

When adding tokens, update:

1. `Services/Theme/ThemeTokenCatalog.cs` (defaults + known keys)
2. `Themes/Packs/Default.json` and `Dark.json`
3. Consumers that read `ThemeService.Instance.CurrentTokens`
4. This guide (keep shipped MD in sync)

Register packs at runtime via Settings import or `ThemeService.Instance.ImportThemeFile(path)`.

## Related source (repo only)

- `docs/DEPLOY_THEME_PACK_GUIDE.md` — same guide for the repository
- `Services/Theme/*` — loader, validator, live palette
- `Themes/Controls.xaml` / `Themes/Colors.xaml` — shared Material chrome
