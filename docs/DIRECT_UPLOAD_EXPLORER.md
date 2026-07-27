# Direct Upload explorer

How the Direct Upload file tree behaves for path mapping, `.gitignore`, and git status.

## Path mapping highlight

When the active connection has a path mapping:

- The tree always shows the **full project** (not only the mapped folder).
- The mapped local folder row uses a **green background** and a `mapped` badge.
- That folder is expanded by default (ancestors too).

### Upload destinations

| Local file location | Relative path from | Remote base |
|---------------------|--------------------|-------------|
| Under mapped local folder | Mapped local root | Profile `RemotePath` + mapping `RemotePath` |
| Outside mapped folder | Project root | Profile `RemotePath` only |

## Hard hide vs soft ignore

**Always hidden** (never listed):

- `.git`, `.vs`, `Desktop.ini`, `Thumbs.db`
- `.gitdeploy.config`, `.gitdeploy.session`, `.gitdeploy.history`
- OS hidden attribute files

**Soft-shown** (PhpStorm-like): names matching root `.gitignore` patterns appear with muted text and an `ignored` badge. They remain selectable for upload.

## Git working-tree colors

When the project is a git repository, rows get Tortoise-inspired name colors from `git status` / `git ls-files`:

| State | Color / badge |
|-------|----------------|
| Clean (tracked, unchanged) | Soft green name |
| Modified / deleted / renamed | Red name + `M` |
| Untracked | Blue name + `?` |
| Ignored | Muted name + `ignored` |
| Conflicted | Warning color + `!` |
| No git repo | Neutral secondary text |

Folders inherit the “dirtiest” child state (conflict → modified → untracked → clean → ignored).

Mapped highlight is **background**; git status is **name color** — both can apply on the same row.

## Context menus

Right-click a folder or file:

- **New Folder** — create a local folder, then refresh the tree (empty folders stay visible)
- **New File** — ask for name + extension, create an empty file, open the in-app editor
- **Edit** (files) — open the local AvalonEdit panel; Save writes to disk only

On the Deploy page dock, the local editor hosts in the center overlay (same area as FTP remote edit).

Note: Git does not track empty folders. An empty new folder appears in the tree (often as untracked) but git status colors for content appear after you add a file inside it.

