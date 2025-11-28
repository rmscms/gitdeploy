# gitdeploy pro 🚀

> fluent-design wpf toolkit for comparing local git branches, reviewing commits, and deploying filtered files over ftp/sftp without leaving desktop workflows.

## 🎯 why gitdeploy pro?

- single-pane control center for dashboard, deploy, settings, history, upcoming git page
- dynamic action workflow (`review & commit` → `compare` → `push to github`) driven by repo state
- per-project settings, history, and recent-project switcher (phpstorm-inspired)
- mica/blur dark ui, custom window chrome, avatar-based project selector

## 🧩 tech stack

| layer | details |
| --- | --- |
| ui | wpf (.net 8) + mahapps.metro + custom windowchrome |
| git | `git` cli via `gitservice` (status, diff, commit, init, push, merge, tags, etc.) |
| deploy | fluentftp (ftp/sftp select-all or per-file deploy flow + simulated progress) |
| config | newtonsoft.json storing `.gitdeploy.config` & `.gitdeploy.history` in repo root |
| dialogs | custom modern modal windows (commit, init git, diff checklist) |

## ✨ hero features

- **modern ui** – acrylic-like panels, custom project avatar menu, responsive tabs
- **smart git workflow** – detects uncommitted files, missing initial commits, remote push needs
- **branch compare & selective deploy** – checkboxes in diff window + deploy selected button
- **auto git assist** – optional auto-init, auto-commit, auto-push, branch sync after deploy
- **ftp/sftp tooling** – connection tester, remote browser placeholder, encrypted credentials
- **history & rollback-ready** – per-project `.gitdeploy.history` records with plan for revert
- **project switching** – hash-colored avatar, “open / recent projects” popup like phpstorm 2024
- **instant clone/connect** – git tab button crafts https/ssh urls, picks a default path, and clones without leaving the UI
- **tortoisegit friendly** – hides `.git` folder after git ops to restore overlay icons

## 📁 repository layout

```
GitDeployPro/
├── App.xaml / App.xaml.cs        # mahapps + global exception hooks
├── MainWindow.xaml(.cs)          # shell, navigation, project selector
├── Pages/
│   ├── DashboardPage             # project stats + push badge
│   ├── DeployPage                # action button, diff grid, logs
│   ├── HistoryPage               # deployment records (per project)
│   └── SettingsPage              # tabbed general / ftp / git panels
├── Services/
│   ├── GitService                # all git processes + status parsing
│   ├── ConfigurationService      # global + project configs
│   └── HistoryService            # per-project history storage
├── Windows/
│   ├── CommitWindow              # review & commit modal
│   ├── DiffWindow                # checkbox diff w/ deploy
│   └── InitGitWindow             # select branches + remote
└── Models/                       # viewmodels, config contracts
```

## 🛠 prerequisites

- windows 10/11
- .net 8.0 sdk (`winget install Microsoft.DotNet.SDK.8`)
- git cli (added to `PATH`)
- optional: tortoisegit for overlay testing

## 🚀 run in development

```powershell
cd C:\laragon\www\rms2\develop-ftp\GitDeployPro
dotnet restore
dotnet run
```

> the window launches with dashboard; use the top-left avatar menu to pick/open projects. every significant change should be paired with `dotnet run` to catch ui/generics compile issues immediately (recommended workflow from user feedback).

## 🧭 workflow guide

### dashboard
- shows project path + current branch + changed files + commit count
- **push-needed badge** appears when local branch is ahead of remote
- quick actions jump to deploy, refresh git info, or open settings

### deploy page
1. **review & commit** – triggered automatically if untracked/modified files or zero commits
2. **compare** – pick source/target, open diff window with selectable files (all checked by default)
3. **deploy** – uploads (simulated placeholder) + logs + saves history
4. **sync & auto-push** – merges source→target locally, then optional push; `.git` folder rehides each time
5. **push badge** – shows pending commit count next to action button whenever branches are equal but remote lagging

### settings (git tab)
- remote url management (`SetRemoteAsync`), default branches, auto-init/commit/push toggles
- “Add config to .gitignore” button ensures `.gitdeploy.config` stays private
- newly added push badge mirrors dashboard/deploy states

### project switching
- phpstorm-like avatar button (colored by deterministic hash) + popup list of recents
- switching projects reloads config, git service, history, pages, and settings fields

## 📦 configuration & history files

| file | scope | purpose |
| --- | --- | --- |
| `.gitdeploy.config` | per project (root) | encrypted ftp creds, git defaults, toggles |
| `.gitdeploy.history` | per project | serialized deployment records |
| `%appdata%/GitDeployPro/global.config` | global | remembers recent projects + last path |

> tip: config file is auto-hidden via `.gitignore`; ensure repo root allows hidden file visibility when debugging.

## 📤 publishing single exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

- output: `bin\Release\net8.0-windows\win-x64\publish\GitDeployPro.exe`
- ensure `icon.ico` stays copied to root (already embedded via project file)

## 🧪 testing checklist

- `dotnet run` after every notable xaml/cs change (catches binding name mismatches)
- deploy workflow (commit → compare → deploy → push) using sample repo with two branches
- ftp “test connection” against staging server or mock (FluentFTP)
- tortoisegit overlay refresh by confirming `.git` hidden attribute after init/push/deploy

## 🧭 troubleshooting

| issue | fix |
| --- | --- |
| **action button stuck on push disabled** | set remote url in settings and reload |
| **tortoisegit icons not updating** | `attrib +h .git` already triggered post git ops; if needed, restart `TGitCache.exe` |
| **mc3074 / xaml tag errors** | clean `bin/obj`, re-run `dotnet run` (MahApps works with net8) |
| **commit window empty** | ensure git repo initialized; settings auto-init helper available |
| **history mixing projects** | make sure to switch via avatar popup; each project has isolated `.gitdeploy.history` |
| **ssh pull/push failing** | use the new Clone / Connect button so the app can auto-load `%USERPROFILE%\.ssh\id_*` keys into `ssh-agent`, or switch the remote to HTTPS |

## 🛣 roadmap ideas

- git page with push/pull/tag manager (todo list items already staged)
- rollback integration calling `RevertCommitAsync`
- real ftp/sftp upload pipeline with FluentFTP’s streaming + remote browser
- telemetry/log sharing toggle

---

made with ❤️‍🔥 by combining fluent design, git wizardry, and relentless dotnet run cycles. enjoy shipping confident deploys! 🛰

