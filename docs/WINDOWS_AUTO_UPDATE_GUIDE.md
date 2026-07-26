# Windows Desktop Auto-Update Guide (Own HTTP/HTTPS Server)

Reusable guide for adding **self-hosted automatic updates** to Windows desktop apps (WPF / WinForms / .NET single-file EXE).

This matches the GitDeployPro implementation and is meant to be copied into other Windows projects.

---

## 1. Goal

- Publish release files on **your own server**.
- Each **app/product** has its own update URL baked into code (`UpdateOptions.BaseUrl`) — **not** a Settings textbox.
- Different apps → different folders/domains; users never edit the URL.
- Checks run:
  1. On startup (if the interval elapsed)
  2. Automatically every **12 hours** (configurable constant)
  3. Manually via **Check for updates now**
- Support two modes:
  - **Optional update** → user can choose Later
  - **Mandatory update** → user must Update or Exit

---

## 2. What you host on the server

Per-app base URL (compile-time constant), for example:

```text
https://updates.example.com/gitdeploypro/
https://updates.example.com/other-app/
```

Upload:

```text
gitdeploypro/
  latest.json
  GitDeployPro-1.4.4.exe
```

### 2.1 `latest.json` contract

```json
{
  "version": "1.4.4",
  "fileName": "GitDeployPro-1.4.4.exe",
  "downloadUrl": "GitDeployPro-1.4.4.exe",
  "sha256": "PUT_HEX_SHA256_HERE",
  "releaseNotes": "- Critical fix\n- UI polish",
  "mandatory": false,
  "publishedUtc": "2026-07-26T10:00:00Z"
}
```

| Field | Required | Description |
|--------|----------|-------------|
| `version` | Yes | `Major.Minor.Patch` |
| `fileName` | Yes | Published EXE name |
| `downloadUrl` | Yes | Absolute URL or relative to BaseUrl |
| `sha256` | Yes | SHA-256 of EXE bytes |
| `releaseNotes` | No | Shown in dialog |
| `mandatory` | No | Default `false`; `true` forces update |

### 2.2 Optional vs mandatory

| `mandatory` | Buttons | Can keep working? |
|-------------|---------|-------------------|
| `false` | Update now / Later | Yes |
| `true` | Update now / Exit | No |

### 2.3 SHA-256 (PowerShell)

```powershell
Get-FileHash -Algorithm SHA256 .\GitDeployPro-1.4.4.exe | Format-List
```

---

## 3. App-side architecture

```text
UpdateOptions.BaseUrl  (per app, in code)
        │
        ▼
Startup + 12h timer + Check now
        │
        ▼
GET {BaseUrl}/latest.json
        │
   optional → Update / Later
   mandatory → Update / Exit
        │
Download → SHA-256 → helper replaces EXE → relaunch
```

### 3.1 Recommended files

```text
Services/Update/
  UpdateOptions.cs
  UpdateManifest.cs
  UpdateCheckResult.cs
  AppUpdateService.cs
Windows/
  UpdateAvailableWindow.xaml(.cs)
  UpdateProgressWindow.xaml(.cs)
```

### 3.2 Persist last check

Store `LastUpdateCheckUtc` in global app config so reopening within 12 hours does not spam the network. Manual check ignores this gate.

---

## 4. Per-app constant (not Settings)

```csharp
public static class UpdateOptions
{
    // Change per product. Empty string disables update checks.
    public const string BaseUrl = "https://updates.example.com/gitdeploypro";
    public const double CheckIntervalHours = 12;
}
```

Settings UI only needs:

- Current version label
- Last checked time (optional)
- **Check for updates now** button

---

## 5. Periodic check

- Persist `LastUpdateCheckUtc` after every successful check attempt (or completed fetch).
- Startup: check only if `UtcNow - LastUpdateCheckUtc >= 12h` (or never checked).
- While running: `DispatcherTimer` every 15–30 minutes; when due, run the same check.
- Manual Check now: always fetch.

---

## 6. Apply update (Windows single-file)

1. Download to `%TEMP%\<AppName>-update\<fileName>`
2. Verify SHA-256
3. Write CMD helper that waits for PID exit, copies over `Environment.ProcessPath`, relaunches
4. Start helper, then exit app

Never overwrite the running EXE in-process.

---

## 7. Publishing checklist

1. Bump `.csproj` version
2. `dotnet publish` single-file win-x64
3. Compute SHA-256
4. Upload EXE + update `latest.json`
5. Set `mandatory: true` only for critical releases
6. Verify with an older build

---

## 8. Copying to another Windows app

1. Copy this guide + `Services/Update/*` + update windows
2. Change only `UpdateOptions.BaseUrl` (and file name prefix)
3. Wire startup timer + Check now
4. Host a separate folder on your server for that product

Keep the **JSON contract identical** across apps.
