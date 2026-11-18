# GitDeploy Pro — Agent API

> 🇮🇷 **خلاصه:** GitDeploy یک API محلی دارد تا یک ایجنت (Claude Code، Cursor، اسکریپت و ...) بتواند مثل ربات تلگرام تغییرات پروژه را ببیند، دیپلوی کند، فایل آپلود کند و ترمینال SSH بزند — **بدون اینکه تو دکمه بزنی**. فقط روی `127.0.0.1` گوش می‌دهد، با کلید API محافظت می‌شود و برای کارهای خطرناک پیش‌فرض از تو تأیید می‌گیرد.

The Agent API lets an AI agent (or any script) use GitDeploy the same way the Telegram bot does:
see changed files, deploy, upload specific files, and run SSH commands.

- **Local only** — listens on `127.0.0.1` (never on the network). Browser requests (`Origin` header) are rejected.
- **Off by default** — enable it in *Settings → Agent API*.
- **Key based** — every request needs `Authorization: Bearer <key>`. Keys are shown once; only a SHA-256 hash is stored.
- **Least privilege** — each key has scopes (`read`, `deploy`, `upload`, `terminal`) and an optional project allow-list.
- **Human in the loop** — with `approval = ask` (default) a popup in GitDeploy asks you before each deploy / upload / terminal session.
- **Audit log** — `%AppData%\GitDeployPro\agent_api_audit.log` (time, key, tool, project, outcome).

---

## 1. Quick start (3 steps)

1. **Enable**: *Settings → Agent API* → tick *Enable the local Agent API* → **Save**. Default port `47821`.
2. **Create a key**: same page → name, scopes, approval → **Create key**. Copy it immediately (shown once).
3. **Connect your agent**:

   **Claude Code (MCP):**
   ```bash
   claude mcp add --transport http gitdeploy http://127.0.0.1:47821/mcp --header "Authorization: Bearer YOUR_KEY"
   ```
   Then ask: *"list my GitDeploy projects and show the changed files of `trade`"*.

   **Any HTTP client:**
   ```bash
   curl -H "Authorization: Bearer YOUR_KEY" http://127.0.0.1:47821/v1/projects
   ```

Revoke a key at any time in the same page; access ends immediately.

---

## 2. Scopes

| Scope | Allows |
|---|---|
| `read` | `gitdeploy_status`, `list_projects`, `get_changes`, `get_job`, `list_jobs` |
| `deploy` | `deploy` (commit + push + upload changed files, same pipeline as the Telegram *Deploy* button) |
| `upload` | `upload` (specific files/folders through the project's FTP/SFTP profile and path mappings) |
| `terminal` | `terminal_connect`, `terminal_run`, `terminal_close` |

A key with a project allow-list can only touch those projects. Projects must already be known to GitDeploy (recent projects).

## 3. Approval

| Mode | Behaviour |
|---|---|
| `ask` *(default)* | A popup in GitDeploy shows what the agent wants to do. **Allow** runs it, **Deny** cancels (job state `denied`). Applies to `deploy`, `upload` and `terminal_connect`. |
| `auto` | No popup. Use only for keys you fully trust. |

Long operations are **asynchronous jobs**: the call returns `202` with a `jobId`; poll `get_job` until `state` is `done`, `failed` or `denied`.
States: `awaiting_approval` → `running` → `done` | `failed` | `denied`.

---

## 4. REST endpoints

Base URL `http://127.0.0.1:<port>`. All except `health` and `openapi.json` need the Bearer key.
Machine-readable spec: **`GET /v1/openapi.json`**.

| Method | Path | Scope | Body / query | Returns |
|---|---|---|---|---|
| GET | `/v1/health` | — | | `{ok:true}` |
| GET | `/v1/openapi.json` | — | | OpenAPI 3 document |
| GET | `/v1/status` | read | | version, active project |
| GET | `/v1/projects` | read | | `{projects:[{path,name,supportsDeploy,kind}]}` |
| GET | `/v1/changes` | read | `project` (optional) | `{count, files:[{path,status,oldPath}]}` |
| POST | `/v1/deploy` | deploy | `{project?}` | `202 {jobId}` |
| POST | `/v1/upload` | upload | `{project?, paths:[…], overwrite?}` | `202 {jobId}` |
| POST | `/v1/terminal/connect` | terminal | `{project?, profileId?}` | `202 {jobId}` |
| POST | `/v1/terminal/run` | terminal | `{command, timeoutSeconds?}` | `{output, cwd, stillRunning}` |
| POST | `/v1/terminal/close` | terminal | | `{closed:true}` |
| GET | `/v1/job` | read | `id` | job (state, summary, result) |
| GET | `/v1/jobs` | read | | recent jobs of the key |

`project` is the full path from `list_projects`; omit it to use the active project in GitDeploy.

Errors are `{ "error": "…" }` with status `400/401/403/404/409/422/429/504`.
Rate limit: 120 requests / minute / key.

### Examples

```bash
KEY=gdp_xxxxxxxx
API=http://127.0.0.1:47821

# what would a deploy send?
curl -H "Authorization: Bearer $KEY" "$API/v1/changes?project=C:/laragon/www/trade"

# deploy (a human approves in the app unless the key is `auto`)
JOB=$(curl -s -H "Authorization: Bearer $KEY" -d '{"project":"C:/laragon/www/trade"}' $API/v1/deploy | jq -r .jobId)
curl -H "Authorization: Bearer $KEY" "$API/v1/job?id=$JOB"

# upload two paths (relative to the project root)
curl -H "Authorization: Bearer $KEY" -d '{"paths":["app/Http","public/index.php"]}' $API/v1/upload

# terminal
curl -H "Authorization: Bearer $KEY" -d '{}' $API/v1/terminal/connect      # wait until the job is done
curl -H "Authorization: Bearer $KEY" -d '{"command":"ls -la"}' $API/v1/terminal/run
curl -H "Authorization: Bearer $KEY" -X POST $API/v1/terminal/close
```

### Terminal safety

`terminal_run` uses the same filter as the Telegram terminal: single-line commands only (max 500 chars) and a deny-list of dangerous categories
(e.g. destructive deletes, disk/format tools, shutdown). Blocked commands return `422`. Build tools such as `php artisan`, `go build`, `npm run` stay allowed.
Each key has its own SSH session; close it when done.

---

## 5. MCP

`POST /mcp` speaks MCP (JSON-RPC 2.0 over HTTP, `Authorization: Bearer` header).
Supported: `initialize`, `ping`, `tools/list` (only tools the key's scopes allow), `tools/call`. Tool names equal the table above
(`list_projects`, `get_changes`, `deploy`, `upload`, `terminal_connect`, `terminal_run`, `terminal_close`, `get_job`, `list_jobs`, `gitdeploy_status`).
Tool results are JSON text; errors set `isError: true`.

Generic MCP client config:

```json
{
  "mcpServers": {
    "gitdeploy": {
      "type": "http",
      "url": "http://127.0.0.1:47821/mcp",
      "headers": { "Authorization": "Bearer YOUR_KEY" }
    }
  }
}
```

---

## 6. Recommended agent workflow

1. `list_projects` → pick the project path.
2. `get_changes` → review what would be sent.
3. `deploy` (or `upload` for a few files) → poll `get_job`.
4. If a job is `denied`, tell the user — do not retry in a loop.
5. Use the terminal only when needed; close it afterwards.

## 7. Security notes

- Treat the key like a password. One key per agent; revoke it when the agent is retired.
- Prefer `approval = ask` and a project allow-list. Give `terminal` only to keys that really need it.
- The server accepts only `127.0.0.1` / `localhost` Host headers and rejects any request with an `Origin` header (blocks web pages and DNS rebinding).
- The API is intentionally not exposed to the LAN. If you need remote use, put your own authenticated tunnel in front and keep `approval = ask`.
- Keys file: `%AppData%\GitDeployPro\agent_api_keys.json` (hashes only). Audit log: `agent_api_audit.log` next to it.
