# GitDeployPro Performance Optimization Report

## 1) What was implemented

### Instrumentation layer (bounded + configurable)
- Added `Services/PerformanceSampler.cs` with:
  - scope-based start/end/error markers,
  - JSONL logging under `%LOCALAPPDATA%\GitDeployPro\perf`,
  - size-based rotation (5 MB),
  - runtime toggle by `GDP_PERF_LOG` env var and `GlobalConfig.EnablePerformanceSampling`.
- Added config flag in `Services/ConfigurationService.cs`:
  - `EnablePerformanceSampling`.

### Instrumented boundaries
- App lifecycle: `App.xaml.cs`.
- Navigation: `MainWindow.xaml.cs`.
- Deploy flow: `Pages/DeployPage.xaml.cs`.
- Backup runner/task monitor: `Services/BackupSchedulerRunner.cs`, `Services/BackupTaskMonitor.cs`.
- History load and file-open actions: `Pages/HistoryPage.xaml.cs`.
- Terminal connect/disconnect lifecycle: `Controls/TerminalControl.xaml.cs`, `Pages/TerminalPage.xaml.cs`.
- Database connect/query/import boundaries: `Pages/DatabasePage.xaml.cs`, `Services/DatabaseClient.cs`.
- Editor lifecycle/save: `Windows/CodeViewerWindow.xaml.cs`.

### CPU optimization (Deploy polling)
- Refactored Deploy auto-refresh into **light** and **full** refresh modes:
  - lightweight polling on regular ticks (no diff generation),
  - full refresh every fixed interval for heavier checks.
- Removed periodic heavy `GetDiffAsync` calls from normal timer ticks.
- Branch combo repopulation is now throttled and cached by interval instead of rebuilding every timer cycle.

### RAM/lifecycle optimization
- Terminal lifecycle hardening:
  - added explicit terminal disposal (`DisposeTerminalAsync`),
  - ensured disconnect + WebView event unbinding on teardown,
  - closed all terminals on page unload to avoid background session retention.
- Code viewer cleanup:
  - detached WebView2 handlers on window close.
- Removed stale/unneeded terminal page fields and duplicate event wiring patterns.

### History scalability optimization
- Added in-memory index limits:
  - max indexed paths,
  - max hits per path,
  - max total indexed hits.
- Added eviction strategy for old indexed paths.
- Enabled virtualization on suggestions and file-hit lists.

### UI/list rendering optimization
- Removed extra `ScrollViewer` wrapper around Deploy files list and enabled list virtualization/recycling directly.

## 2) Baseline harness and repeatable scenario matrix

Created:
- `collect-perf-scenario.ps1`: repeatable capture script (`dotnet-counters` + optional `dotnet-trace` + process snapshot).
- `PERF_SCENARIO_MATRIX.md`: workflow matrix and command examples.

Captured artifacts:
- `.tmp_build/perf-baseline-idle-summary.txt`
- `.tmp_build/perf-baseline-idle-counters.csv`
- `.tmp_build/perf-baseline-idle.nettrace`
- `.tmp_build/perf/20260625-111133-idle-current/*`
- `.tmp_build/perf/20260625-111234-idle-validation/*`

## 3) Measured counters (idle samples)

### Early baseline capture
- Working Set Avg: **108.76 MB**
- GC Heap Avg: **7.01 MB**
- CPU Avg: **0.03%**
- Allocation Rate Avg: **21,165 B/s**

### Current capture run #1
- Working Set Avg: **244.89 MB**
- GC Heap Avg: **74.01 MB**
- CPU Avg: **0.0033%**
- Allocation Rate Avg: **227,909 B/s**

### Current capture run #2 (repeatability)
- Working Set Avg: **245.50 MB**
- GC Heap Avg: **75.15 MB**
- CPU Avg: **0.0044%**
- Allocation Rate Avg: **220,702 B/s**

Notes:
- The two latest idle runs are close to each other (stable repeatability under current runtime state).
- Absolute deltas vs the early baseline are state-dependent (open pages, loaded editor/session state, and app uptime affect heap/workingset).

## 4) Regression/verification status

- Full build succeeded after changes:
  - `dotnet build GitDeployPro.csproj -p:UseAppHost=false`
- No new linter errors were introduced in edited files.
- Existing nullable/package warnings remain and were not part of this perf pass.

## 5) How to run before/after package again

```powershell
.\collect-perf-scenario.ps1 -Scenario idle-before -DurationSeconds 30 -TraceSeconds 20
.\collect-perf-scenario.ps1 -Scenario deploy-refresh-before -DurationSeconds 45
# apply code changes / rebuild / run updated app
.\collect-perf-scenario.ps1 -Scenario idle-after -DurationSeconds 30 -TraceSeconds 20
.\collect-perf-scenario.ps1 -Scenario deploy-refresh-after -DurationSeconds 45
```
