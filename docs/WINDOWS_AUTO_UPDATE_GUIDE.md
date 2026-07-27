# Windows Desktop Auto-Update Guide (Own HTTP/HTTPS Server)

Reusable guide for **self-hosted automatic updates** on Windows desktop apps (WPF / WinForms / .NET single-file EXE).

This matches the **GitDeployPro** implementation (from **1.4.6** onward) and can be copied into other Windows projects.

---

## 1. Goal

- Publish release files on **your own server**.
- Each **app/product** has its own update URL baked into code (`UpdateOptions.BaseUrl`) — **not** a Settings textbox.
- Checks run:
  1. On startup (if the interval elapsed)
  2. Automatically every **12 hours** (configurable constant)
  3. Manually via **Check for updates now** (About / Settings)
- Support two modes:
  - **Optional update** → Later is allowed
  - **Mandatory update** → user must download/install or Exit
- User can **keep using the app** while the package downloads (footer progress), then **Restart** to apply.

---

## 2. GitDeployPro server mapping (`site/` ↔ host)

| Local repo | Uploaded to server | Public URL |
|------------|--------------------|------------|
| `site/` | `gitdeploy/` | `https://app.nitron.pro/gitdeploy/` |
| `site/latest.json` | `gitdeploy/latest.json` | `https://app.nitron.pro/gitdeploy/latest.json` |
| `site/versions/*` | `gitdeploy/versions/*` | `https://app.nitron.pro/gitdeploy/versions/...` |

- Do **not** create a nested `site/gitdeploy/` folder — `site` already *is* the server root for this product.
- App constant:

```csharp
// Services/Update/UpdateOptions.cs
public const string BaseUrl = "https://app.nitron.pro/gitdeploy";
```

Upload layout example:

```text
gitdeploy/                    ← contents of local site/
  latest.json
  index.html
  versions/
    GitDeployPro-1.4.6.exe
    GitDeployPro-1.4.6.zip
```

---

## 3. `latest.json` contract

```json
{
  "version": "1.4.9",
  "fileName": "GitDeployPro-1.4.9.exe",
  "downloadUrl": "versions/GitDeployPro-1.4.9.exe",
  "sha256": "PUT_HEX_SHA256_HERE",
  "releaseNotes": "- Feature A\n- Fix B",
  "changelog": [
    "Feature A",
    "Fix B"
  ],
  "mandatory": false,
  "publishedUtc": "2026-07-27T10:00:00Z"
}
```

| Field | Required | Description |
|--------|----------|-------------|
| `version` | Yes | `Major.Minor.Patch` |
| `fileName` | Yes | Published distribution EXE name (versioned for the website/feed) |
| `downloadUrl` | Yes | Absolute URL or relative to BaseUrl |
| `sha256` | Yes | SHA-256 of the **download EXE** bytes |
| `releaseNotes` | No | Plain multiline string (legacy / short text). Prefer keeping in sync with `changelog` |
| `changelog` | Yes (for UX) | **Array of bullet strings for THIS version only** — single source of truth |
| `mandatory` | No | Default `false` |
| `publishedUtc` | Yes | ISO-8601 UTC |

### Changelog single source of truth (important)

**Write the release notes once — in `latest.json` only for the current version.**

| Consumer | Reads from |
|----------|------------|
| Website `index.html` (Latest slot) | `latest.json` → `changelog` (fallback: split `releaseNotes`) |
| Desktop app “What’s new” modal | Same `latest.json` payload (cached at download / after apply) |

Rules:

1. **`latest.json` holds only the latest version’s changelog** — do **not** grow a history array of old releases inside it (that bloats the feed).
2. Older versions on the website may keep **optional static** HTML cards for history, but the **live “Latest”** card must always be filled from `latest.json`.
3. On each release, **overwrite** `changelog` / `releaseNotes` with the new version’s bullets (same text you would have pasted into `index.html`).
4. After the user downloads an update and **Restarts**, the app should show a **nice What’s New modal** with that version’s `changelog` items (once per applied version).

```text
Release time:
  edit latest.json.changelog  ──►  site.js fills Latest card
                              └──►  app stores notes with pending update
Restart after apply:
  app shows What’s New modal from stored / fetched changelog
```

### SHA-256 (PowerShell)

```powershell
Get-FileHash -Algorithm SHA256 .\GitDeployPro-1.4.6.exe | Format-List
```

### Optional vs mandatory (UX)

| `mandatory` | Dialog secondary | After “Download update” |
|-------------|------------------|-------------------------|
| `false` | Later | Footer download → Restart when ready |
| `true` | Exit | Same download footer; long-term use requires install |

---

## 4. Stable install home (choosable on first install)

The app is still distributed as a **portable** ZIP/EXE. On **first install** for a Windows user, a dialog lets them pick the install folder (important when C: is full). After that, updates overwrite a **stable** EXE name (no version in the filename):

```text
{InstallDirectory}\GitDeployPro.exe
```

Default `InstallDirectory`:

```text
%LocalAppData%\GitDeployPro
```

User may Browse to another parent (e.g. `D:\Apps`) → install into `D:\Apps\GitDeployPro`.

### Registry source of truth

```text
HKCU\Software\GitDeployPro
  InstallDirectory = C:\Users\...\AppData\Local\GitDeployPro   (or D:\Apps\GitDeployPro)
```

Mirrored in global config for About display after the app runs from the install path.

### How we detect first install vs already installed

| Situation | How we know | What happens |
|-----------|-------------|--------------|
| Already installed and running from it | `ProcessPath` equals `{InstallDirectory}\GitDeployPro.exe` | No dialog |
| Already installed, portable ZIP launched again | Registry `InstallDirectory` set **and** that folder has `GitDeployPro.exe` | No dialog; relaunch installed EXE (copy newer portable over it if version higher) |
| First install | Registry missing/empty **or** saved folder has no EXE | Show install-location dialog |
| User chose **Run portable** | No registry write | Next portable launch still shows the dialog until they Install |
| Dev / debugger / `bin` | Path under `bin`/`obj`/`.tmp_build` or debugger attached | Skip dialog |

Clearing the registry key or deleting the install folder makes the next portable launch look like a first install again.

### First-install dialog buttons

- **Install here** — write registry, copy EXE, Desktop shortcut, refresh Autostart, relaunch from install path
- **Run portable (this time)** — continue without installing; do not write `InstallDirectory`

### Rules

1. Every update **overwrites** `{InstallDirectory}\GitDeployPro.exe`.
2. Website/ZIP may still ship **versioned** names (`GitDeployPro-1.4.6.exe`); updater copies into the stable install name.
3. Do **not** auto-delete old versioned Desktop EXEs; user may delete leftovers manually.
4. Desktop gets a **shortcut only** (`GitDeploy Pro.lnk` → install EXE).
5. v1 has **no** “change install location” after install; reinstall portable once if the user must move.

### Desktop shortcut

| Item | Value |
|------|--------|
| Path | `%USERPROFILE%\Desktop\GitDeploy Pro.lnk` |
| Target | `{InstallDirectory}\GitDeployPro.exe` |
| When | Created/updated on install and after update apply if missing/wrong |

Implementation: `Services/DesktopShortcutService.cs`, `AppInstallPaths.cs`, `AppInstallMigrator.cs`, `Windows/InstallLocationWindow`

### Where is my version?

| Question | Answer |
|----------|--------|
| Where does the running app live? | About → Install path (from registry / chosen folder) |
| Where is the Desktop entry? | `GitDeploy Pro.lnk` shortcut |
| Where does download stage? | `{InstallDirectory}\update\` (`package.exe` + `pending.json`) |
| About page | Shows install path + “Open install folder” |

**Legacy confusion (pre-1.4.6):** updater overwrote `Environment.ProcessPath` in place. Replaced by stable install-directory overwrite.

---

## 5. Windows startup (AutoStart)

- Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GitDeployPro`
- Settings checkbox: Launch when Windows starts
- Settings button: **Use this version** (points Run at current EXE)
- After install / update apply: if Launch-on-startup is enabled (or already registered), code calls `AutoStartService.RefreshToInstallPath(...)` so Run points at the chosen install EXE

Implementation: `Services/AutoStartService.cs`

---

## 6. Update UX flow (background download + footer)

```text
Check latest.json
        │
        ▼
Update available dialog  →  Later / Exit / Download update
        │
        ▼
MainWindow footer: Downloading x%   (app stays usable)
        │
        ▼
Footer: Update ready → Restart now / Later
        │
        ▼
Apply: copy into LocalAppData EXE → refresh shortcut/startup → relaunch
```

### Why Restart is required

Windows cannot safely overwrite a locked running EXE. Download + SHA verify happen while the app runs; replace happens after exit (helper CMD when already running from install path).

### UI pieces

| Piece | Role |
|-------|------|
| `UpdateAvailableWindow` | Prompt; primary = **Download update** |
| `MainWindow` footer bar | Progress / ready / cancel; stays inside window (not under taskbar) |
| `UpdateProgressWindow` | Fallback only if owner is not MainWindow |
| About / Settings | Manual **Check for updates now** |

### Staging files

```text
%LocalAppData%\GitDeployPro\update\
  package.exe      ← verified download
  pending.json     ← version, sha256, notes, mandatory
  apply-update.cmd ← temporary helper (deleted after run)
```

---

## 7. App-side files (GitDeployPro)

```text
Services/Update/
  UpdateOptions.cs
  UpdateManifest.cs
  UpdateCheckResult.cs
  PendingUpdateState.cs
  AppInstallPaths.cs
  AppInstallMigrator.cs
  AppUpdateService.cs          ← check, DownloadOnlyAsync, ApplyPendingAndRestartAsync
  AppUpdateCoordinator.cs      ← dialog + handoff to MainWindow footer
Services/
  DesktopShortcutService.cs
  AutoStartService.cs
Windows/
  UpdateAvailableWindow.xaml(.cs)
  UpdateProgressWindow.xaml(.cs)   ← legacy/fallback
Pages/
  AboutPage.xaml(.cs)              ← version, install path, check now
MainWindow.xaml(.cs)               ← footer bar + migration on Loaded
```

### Persist last check

Store `LastUpdateCheckUtc` in global config. Manual check ignores the 12h gate.

---

## 8. Per-app constant (not Settings)

```csharp
public static class UpdateOptions
{
    // Change per product. Empty / invalid disables update checks.
    public const string BaseUrl = "https://app.nitron.pro/gitdeploy";
    public const double CheckIntervalHours = 12;
}
```

---

## 9. Publishing / release checklist (GitDeployPro)

When the user asks for **نسخه جدید** / **ریلیز کن**:

1. Bump `Version` / `AssemblyVersion` / `FileVersion` in `GitDeployPro.csproj`
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
3. Name artifacts `GitDeployPro-X.Y.Z.exe` + `.zip`
4. Copy into `releases/X.Y.Z/` and `site/versions/`
5. Update `site/latest.json`:
   - `version`, `sha256`, `downloadUrl` (e.g. `versions/GitDeployPro-X.Y.Z.exe`)
   - **`changelog`**: bullet array for **this version only** (overwrite previous)
   - `releaseNotes`: same bullets as a `\n`-joined string (compat)
   - `publishedUtc`
6. Website **Latest** changelog slot reads from `latest.json` (do not duplicate the live latest by hand in HTML)
7. Optional: keep older static HTML cards for archive only — not required for the live latest card
8. Git commit + annotated tag `X.Y.Z` + push commit and tag to GitHub
9. Upload local `site/` contents to server `gitdeploy/`

Website download button reads `latest.json` via `site/assets/site.js` (prefers ZIP under `versions/` for browsers).

### Post-update What’s New (product behavior)

1. User downloads update in background → Restart applies package
2. On first launch of the new version, show a polished **What’s New** modal listing `changelog` items for that version
3. Mark version as “seen” locally so the modal does not spam every startup
4. Content must match what `index.html` shows for Latest (both from `latest.json`)

---

## 10. Copying to another Windows app

1. Copy this guide + `Services/Update/*` + shortcut/autostart helpers + update UI
2. Change `UpdateOptions.BaseUrl` and product folder/EXE names in `AppInstallPaths`
3. Wire MainWindow migration + footer + About check + What’s New modal
4. Host a **separate** server folder per product
5. Keep the **JSON contract identical** across apps (including `changelog` array)

---

## 11. Quick troubleshooting

| Symptom | Likely cause |
|---------|----------------|
| “Where did the new version go?” | Look at About → Install path (LocalAppData), not Desktop EXE name |
| Startup opens old portable | Run Settings → Use this version, or re-enable Launch on startup after update |
| SmartScreen on first run | Unsigned EXE; More info → Run anyway; code signing is separate |
| Download fails quickly | Network / wrong `latest.json` URL / SHA mismatch |
| Footer says ready but Restart fails | Pending package missing under `...\GitDeployPro\update\` |
| Site download shows old version | `latest.json` or `versions/` not uploaded; hard-refresh browser |
| Latest changelog empty on site | `changelog` / `releaseNotes` missing in `latest.json`; JS not loading feed |
| What’s New empty after update | Notes not saved with pending package; or modal mark-seen fired too early |
