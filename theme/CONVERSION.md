# GitDeploy Pro — "Midnight Blue" theme (HTML → WPF)

Visual spec for the WPF app in `C:\laragon\www\GitDeployPro`. Black surfaces with a blue tint, the app's `#4C8DFF` accent kept.
Open `index.html` for every screen. Rebuild after edits: `node _build/build.cjs`.

## Structure
| Path | What |
|---|---|
| `assets/css/core.css` | whole design system (tokens, controls, shell, workers, modals) |
| `assets/css/pages/<slug>.css` | page-specific styles (one file per screen) |
| `assets/js/app.js` | demo behaviour only (tabs, popups, modals, toasts, live workers) — not part of the WPF port |
| `assets/icons/sprite.svg` | local stroke icons (replace the app's emoji icons) |
| `_build/lib.cjs` `icons.cjs` | shell + helpers; `_build/pages/*.cjs` = one generator per screen group |

Slug rule: `kebab-case(XAML file name)`; main pages: `deploy dashboard direct-upload database git backup history telegram settings about`.

## Tokens → `Themes/Colors.Dark.xaml` keys
| CSS var | WPF key | Value (new) | Was |
|---|---|---|---|
| `--surface-base` | `Surface.Base` | `#03060C` | `#000000` |
| `--surface-shell` | `Surface.Shell` | `#070B13` | `#0A0A0A` |
| `--surface-raised` | `Surface.Raised` | `#0B111C` | `#111111` |
| `--surface-card` | `Surface.Card` | `#0E1523` | `#141414` |
| `--surface-input` | `Surface.Input` | `#080D17` | `#0D0D0D` |
| `--border-subtle` / `--border-strong` | `Border.Subtle` / `Border.Strong` | `#1A2538` / `#2A3A55` | `#222` / `#333` |
| `--text-primary/secondary/muted` | `Text.Primary/Secondary/Muted` | `#E8EEFB` / `#C3CEE3` / `#7F8FA9` | `#E8E8E8` / `#C8C8C8` / `#8A8A8A` |
| `--accent` / `--accent-hover` | `Accent.Primary` / `PrimaryHover` | `#4C8DFF` / `#6AA0FF` | same |
| `--accent2` | `Accent.Secondary` | `#7B61FF` | same |
| `--success/warning/error/info` | `Status.*` | `#3DDC84 #E6B84D #FF5C6A #5BB8FF` | same |
| `--*-surface` | `Status.*Surface` | `#07170F #1A1507 #1A090C #071524` | `#0A1A12 …` |
| `--surface-hover` / `--surface-pressed` | `State.Hover` / `State.Pressed` | `#14789AFF`-like (white→blue tint) | `#14FFFFFF` / `#22FFFFFF` |
| `--chat-in` | `Chat.BubbleIn` | `#131C2C` | `#1A1A1A` |

Spacing/shape/typography are unchanged from `Spacing.xaml` / `Typography.xaml` (page margin 30, card radius 12, input/button radius 8, button height 36, body 13, page title 32).

## New in this theme (to port)
- Shell: 1px blue glow on the window border, radial blue/violet ambient background.
- Cards: `glow` variant for the active/primary card; hover states use the blue-tinted `State.Hover`.
- Workers: `Worker` card (icon, name, state badge, striped progress, speed/ETA, pause/cancel) with states running/paused/queued/done/failed.
- Settings: left section list + search, `title + description | control` rows, sticky save bar.
- Icons: stroke icons (`sprite.svg`) → `Path` geometries (`Data` = the `d` of each path, 24×24, stroke 1.8, round caps).
- Dialogs use one shared confirm modal (no `MessageBox`/`alert`).

## Adaptive window sizes
| Window width | Behaviour |
|---|---|
| ≥ 1700 | larger body font (14), page margin 40, wider max content |
| 1280–1699 | base |
| 1100–1279 | side panes narrow, right docks collapse under content |
| 960–1099 | two-column grids; title-bar extras hidden; form rows stack |
| < 960 | window `MinWidth=960` / `MinHeight=600` (matches `.app` min size) |
