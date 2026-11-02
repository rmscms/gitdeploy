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

Chat UI keys in `palette` (Telegram page): `Chat.BubbleIn`, `Chat.BubbleOut`, `Chat.BubbleInText`, `Chat.BubbleOutText`, `Chat.Composer`, `Chat.Unread`, `Chat.ThreadHover`.

## Schema overview

| Section | Purpose |
|---------|---------|
| `schema` | Must be `gitdeploypro-theme/v1` |
| `id` | Stable id (sanitized); avoid `default` / `dark` for customs |
| `displayName` | Picker label |
| `basedOn` | `default` or `dark` — missing keys are filled from that pack |
| `palette` | App brush keys (`Surface.Base`, `Text.Primary`, `Chat.BubbleIn`, …) |
| `deploy.shell` | Page / rails / docks / splitters |
| `deploy.header` | Title / subtitle |
| `deploy.branches` | Branch chip, action, rollback, push badge |
| `deploy.changedFiles` | Status badge colors (NEW / MODIFIED / DELETED) |
| `deploy.directUpload` | Tree selection, icons, git badges, mapped rows |
| `deploy.tree` | Type-to-search name highlight in FTP and Direct Upload trees |
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
| `suggestionGhost` | Terminal autocomplete ghost text color |
| `suggestionListBackground` | Multi-match suggestion list panel background |
| `suggestionListActive` | Highlighted row in suggestion list |
| `suggestionListBorder` | Suggestion list border |
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
| `tabClose` | Close icon on an inactive editor tab |
| `tabCloseOnAccent` | Close icon on the active editor tab (must contrast on `Accent.Primary`, typically white) |
| `actionActiveForeground` / `actionActiveBackground` / `actionActiveBorder` | Editor toolbar icons when armed (dirty save/revert, floated) |

### Useful `deploy.tree` keys

| Key | Purpose |
|-----|---------|
| `searchMatchBackground` | Highlight behind the typed match in file/folder names |
| `searchMatchForeground` | Text color on that highlight (must stay readable in Default and Dark) |

## Color values

- Hex: `#RRGGBB` or `#AARRGGBB`
- Palette ref: `@Status.Success`
- Terminal selection may use `rgba(...)`

## Extended pack features (v2)

### New palette tokens
| Key | Purpose |
|-----|---------|
| `Accent.Soft` | Translucent accent fill (icon tiles, active nav row, state badges) |
| `Accent.Glow` | Border/halo colour of the active or running card |
| `Shell.Glow` | Hairline glow on the window frame (`#00000000` = off) |
| `Shell.AmbientA` / `Shell.AmbientB` | Two soft radial background lights (transparent = off) |

### `basedOn` chain
`basedOn` may be `default`, `dark` **or the id of any other pack** (custom or shipped, up to 5 levels). Missing tokens are inherited.

### `metrics` (optional)
Shape tokens applied at startup, before windows load: `Radius.*` (CornerRadius, 1 or 4 numbers), `Spacing.*` (Thickness, 1/2/4 numbers), `FontSize.*` (number).
```json
"metrics": { "Radius.Card": "16", "Spacing.PageMargin": "24" }
```

### `icons` (optional)
Replace any icon by name (see `Themes/Icons.xaml`; 24x24 stroke icons). Value = WPF path data.
```json
"icons": { "rocket": "M5 15c-1.5 1.3-2 5-2 5s3.7-.5 5-2 ..." }
```
In XAML: `<Path behaviors:AppIcon.Kind="rocket" behaviors:AppIcon.Size="16" TextElement.Foreground="{DynamicResource Accent.Primary}"/>`
(`System.Windows.Shapes.Path` has no `Foreground`; colour comes from `TextElement.Foreground`, inherited from a parent Button/TextBlock.)

### Shipped pack updates
A bundled pack with a higher `version` than the seeded copy in `%AppData%\GitDeployPro\Themes` replaces it on startup. User-made packs keep their own file names and are never touched.

### Theme Builder (CLI)
`Themes/Builder/theme-builder.mjs` generates a full pack from a few lines of JSON (hue, tint, accent, mode, overrides) and reports contrast problems:
```
node Themes/Builder/theme-builder.mjs Themes/Builder/specs/ocean-teal.json   # -> Themes/Builder/out/
node Themes/Builder/theme-builder.mjs --all
node Themes/Builder/theme-builder.mjs Themes/Builder/specs/x.json --install  # copy into Themes/Packs (ships with the app)
node Themes/Builder/theme-builder.mjs --tokens                               # palette tokens (read from ThemeTokenCatalog.cs)
node Themes/Builder/theme-builder.mjs --check Themes/Packs/MyTheme.json
```
Specs live in `Themes/Builder/specs` (examples: midnight-blue, ocean-teal, ember-red, daylight). Icons: edit `Themes/Builder/icons.json`, then run `node Themes/Builder/gen-icons.mjs` to regenerate `Themes/Icons.xaml`.

### Reusable controls
`Controls/WorkerCard` (live job row: Running / Paused / Queued / Done / Failed) and the styles `App.ProgressBar`, `App.Chip`, `App.CardStyle.Glow`.

## Built-in themes

- **Default** — slate look (app default until the user changes it)
- **Dark** — full-black look
- **Midnight Blue** — blue-tinted black with the app accent (default for new installs; shipped pack `MidnightBlue.json`)

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
