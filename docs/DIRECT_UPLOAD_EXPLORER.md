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

## Legend

The explorer shows a short legend above the tree:

`green name = clean · red = changed · blue = untracked · grey = ignored · green background = path mapping`
