# GitDeployPro Perf Scenario Matrix

Use `collect-perf-scenario.ps1` to gather repeatable RAM/CPU baselines without changing app features.

## Quick start

1. Start `GitDeployPro`.
2. Open PowerShell in repo root.
3. Run:

```powershell
.\collect-perf-scenario.ps1 -Scenario idle
```

Artifacts are stored under `.tmp_build/perf/<timestamp>-<scenario>`.

## Scenario checklist

- `startup-warm`: launch app and wait until dashboard is ready.
- `idle-10m`: keep dashboard open for 10 minutes.
- `navigation-all-pages`: navigate through Dashboard, Deploy, Direct Upload, FTP Explorer, Database, Terminal, Backup Scheduler, Git, History, Settings.
- `deploy-refresh`: stay on Deploy page for 2 minutes with auto-refresh active.
- `deploy-compare-sync`: compare two branches and review file list.
- `backup-manual`: run one manual backup from Backup Scheduler.
- `history-initial-load`: open History page and wait for first 50 commits.
- `history-load-more-search`: click load more 3 times and run file search.
- `terminal-single-session`: open one local terminal and run simple command.
- `terminal-multi-session`: open 3 terminals (local/SSH mix if available).
- `database-connect-query-import`: connect, run a query, then import a medium SQL file.

## Recommended commands

```powershell
.\collect-perf-scenario.ps1 -Scenario deploy-refresh -DurationSeconds 45 -TraceSeconds 25
.\collect-perf-scenario.ps1 -Scenario history-load-more-search -DurationSeconds 45
.\collect-perf-scenario.ps1 -Scenario terminal-multi-session -DurationSeconds 45 -TraceSeconds 30
```

Set `-SkipTrace` when only counters are needed:

```powershell
.\collect-perf-scenario.ps1 -Scenario idle-10m -DurationSeconds 60 -SkipTrace
```
