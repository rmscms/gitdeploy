// Dashboard + History pages and the developer "components" gallery.
module.exports = (L) => {
  const { ic, btn, iconBtn, field, inp, sel, chk, sw, badge, worker, stream, node, progress, avatar, esc } = L;
  const solid = (c, t) => `<span class="sbadge ${c}">${t}</span>`;

  // =====================================================================
  // DASHBOARD  (Pages/DashboardPage.xaml)
  // =====================================================================
  const liveBackup = (name, pct, stage, summary, speed) => `<div class="worker running" data-worker style="grid-template-columns:30px minmax(0,1fr) auto">
<div class="wi" style="width:30px;height:30px">${ic('spinner')}</div>
<div class="row"><b class="ellipsis">${name}</b></div>
<b class="accent" data-pct>${pct}%</b>
<div class="wp"><div class="progress busy"><i data-progress="${pct}" data-speed="${speed}" style="width:${pct}%"></i></div></div>
<div class="wp row between" style="gap:10px"><span class="muted" style="font-size:12px">${stage}</span><span class="badge info">${summary}</span></div></div>`;

  const histItem = (ok, name, db, msg, at, rel) => `<div class="card dense dash-hist">
<div class="row top between" style="gap:12px"><div class="grow stack sm" style="gap:4px">
<div class="row wrap" style="gap:10px"><span class="badge ${ok ? 'success' : 'warning'}">${ic(ok ? 'check-circle' : 'alert', 'ic-sm')}${ok ? 'Success' : 'Failed'}</span><b>${name}</b></div>
<div class="muted mono" style="font-size:11px">DB: ${db}</div>
<div class="muted" style="font-size:12px">${msg}</div>
<div class="muted" style="font-size:11px;opacity:.75">at ${at}</div></div>
<div style="text-align:right;flex:none"><b>${rel}</b><div class="muted" style="font-size:11px;opacity:.75">backup event</div></div></div>
${ok ? '' : `<div class="row" style="margin-top:8px">${btn('refresh', 'Refresh', 'primary compact', 'data-working="Retrying..." data-done="Retry Backup" data-done-msg="Retry started successfully. You can track progress in Recent Activity." title="Retry this failed backup"')}<a class="btn ghost compact" href="backup.html" title="Open the backup schedule">${ic('edit')}<span class="bl">Edit</span></a></div>`}</div>`;

  const dashCss = `
.dash{max-width:1100px;margin:0 auto}
.dash-head{display:flex;align-items:center;gap:12px;padding:10px 14px;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px;margin-bottom:8px}
.dash-head .hi{width:34px;height:34px;border-radius:8px;display:grid;place-items:center;background:var(--surface-raised);border:1px solid var(--border-subtle);color:var(--accent);flex:none}
.dash-head b{display:block;font-size:16px}.dash-head small{color:var(--text-muted);font-size:12px}
.dash h4{font-size:14px;font-weight:600}
.dash .sec-t{font-size:14px;font-weight:600;margin:0 0 8px}
.dash .mb{margin-bottom:12px}
.dash .panel{background:var(--surface-raised);border:1px solid var(--border-subtle);border-radius:var(--r-card);padding:12px;min-width:0}
.sbadge{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:6px;font-size:12px;font-weight:600;border:1px solid transparent}
.sbadge.warning{background:var(--warning-surface);color:var(--warning);border-color:rgba(230,184,77,.3)}
.sbadge.success{background:var(--success-surface);color:var(--success);border-color:rgba(61,220,132,.3)}
.sbadge.error{background:var(--error-surface);color:var(--error);border-color:rgba(255,92,106,.3)}
.sbadge.info{background:var(--info-surface);color:var(--info);border-color:rgba(91,184,255,.3)}
.dash .big{font-size:26px;font-weight:700;text-align:center;line-height:1.2}
.dash .cap{font-size:11px;color:var(--text-muted);text-align:center;margin-bottom:4px}
.dash .qa{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}
.dash .qa .btn{min-width:148px;height:34px}
.dash-hist+.dash-hist,.dash .stack>.worker+.worker{margin-top:0}
.state-ref .card{background:var(--surface-card)}
.state-ref .sr-t{font-size:10px;letter-spacing:.12em;text-transform:uppercase;color:var(--text-muted);font-weight:700;margin-bottom:8px}
`;

  const dashBody = `<div class="dash">
<div class="dash-head"><div class="hi">${ic('activity', 'ic-lg')}</div><div><b>Dashboard</b><small>At-a-glance project status, changes, and shortcuts.</small></div></div>

<div class="card mb"><h4 class="sec-t">Project Information</h4>
<div style="margin:2px 0">Path: <span class="mono">C:\\laragon\\www\\myproject</span></div>
<div style="margin:2px 0">Current Branch: <span class="chip active mono" style="height:22px">${ic('git-branch')}master</span></div>
<div style="margin-top:8px"><span class="sbadge warning" id="pushBadge">${ic('cloud', 'ic-sm')}Push pending: 3 commit(s)</span></div></div>

<div class="card mb"><h4 class="sec-t">Setup Status</h4>
<div class="grid auto-fit" style="grid-template-columns:repeat(auto-fit,minmax(260px,1fr))">
<div class="panel"><span class="sbadge success">Git: Configured</span><div class="muted" style="margin-top:8px">Remote: <span class="mono">https://github.com/acme...my-project.git</span></div></div>
<div class="panel"><span class="sbadge success">FTP/SFTP: Configured</span><div class="muted" style="margin-top:8px">Profile: <span class="mono">Production (SFTP)</span></div></div></div></div>

<div class="grid cols-3 mb" style="grid-template-columns:repeat(auto-fit,minmax(180px,1fr))">
<div class="card dense"><div class="cap">Changed Files</div><div class="big accent">14</div></div>
<div class="card dense"><div class="cap">Total Commits</div><div class="big ok">1284</div></div>
<div class="card dense"><div class="cap">Last Deploy</div><div class="big" style="font-size:18px;font-weight:600;color:var(--text-primary);margin-top:4px">11/02 00:39</div></div></div>

<h4 class="sec-t">Quick Actions</h4>
<div class="qa">
<a class="btn primary" href="deploy.html" title="Start Deploy">${ic('rocket')}<span class="bl">Start Deploy</span></a>
${btn('refresh', 'Refresh Git', '', 'data-working="Refreshing..." data-done="Success" data-done-msg="Dashboard data refreshed!"')}
<a class="btn" href="terminal-window.html">${ic('terminal')}<span class="bl">Local Terminal</span></a>
<a class="btn" href="project-setup-wizard.html">${ic('wand')}<span class="bl">Run Setup</span></a>
<a class="btn" href="settings.html">${ic('settings')}<span class="bl">Settings</span></a></div>

<h4 class="sec-t">Recent Activity</h4>
<div class="card"><div class="stack">
<div class="panel" id="runningBackups"><h4 class="sec-t" style="margin:0">Live Backups (2)</h4><div class="muted" style="margin:2px 0 8px">Active backup operations with live progress.</div>
<div class="stack sm">${liveBackup('Nightly DB - production', 42, 'Dumping table orders (3/18)', '3/18 tables - 124 MB', 1.1)}${liveBackup('Uploads folder to S3', 78, 'Uploading storage/app/public/2026/10.zip', '1.2 GB / 1.6 GB', 0.7)}</div></div>

<div class="panel" id="backupHistory"><h4 class="sec-t" style="margin:0">Last 5 Full Backup History</h4><div class="muted" style="margin:2px 0 8px">Newest backup runs from full history.</div>
<div class="stack sm">
${histItem(true, 'Nightly DB - production', 'myproject_prod', 'Backup completed: myproject_prod_2026-11-02_0200.sql.gz (48.2 MB)', '2026/11/02 02:00', '10 hours ago')}
${histItem(false, 'Weekly full - staging', 'myproject_staging', 'mysqldump failed: Access denied for user &#39;backup&#39;@&#39;localhost&#39; (using password: YES)', '2026/11/01 03:00', 'yesterday')}
${histItem(true, 'Nightly DB - production', 'myproject_prod', 'Backup completed: myproject_prod_2026-11-01_0200.sql.gz (47.9 MB)', '2026/11/01 02:00', '1 day ago')}
${histItem(true, 'Nightly DB - production', 'myproject_prod', 'Backup completed: myproject_prod_2026-10-31_0200.sql.gz (47.6 MB)', '2026/10/31 02:00', '2 days ago')}
${histItem(true, 'Weekly full - staging', 'myproject_staging', 'Backup completed: myproject_staging_2026-10-25_0300.sql.gz (12.4 MB)', '2026/10/25 03:00', '8 days ago')}
</div></div></div></div>

<div class="section-title" style="margin-top:28px">${ic('layers')}State reference <span class="badge">not on the real page at the same time</span></div>
<div class="state-ref grid" style="grid-template-columns:repeat(auto-fit,minmax(280px,1fr))">
<div class="card dense"><div class="sr-t">Loading (initial text)</div><span class="sbadge warning">Git: Checking...</span><div class="muted" style="margin-top:8px">Checking git repository and remote...</div><hr class="divider" style="margin:10px 0"><span class="sbadge warning">FTP/SFTP: Checking...</span><div class="muted" style="margin-top:8px">Checking connection profile...</div><div class="row" style="margin-top:10px;gap:20px"><div class="big accent">-</div><div class="big ok">-</div><div class="big" style="font-size:18px">-</div></div></div>
<div class="card dense"><div class="sr-t">Not configured</div><span class="sbadge warning">Git: Not Configured</span><div class="muted" style="margin:8px 0 10px">Repository not initialized (.git missing).</div><span class="sbadge warning">FTP/SFTP: Not Configured</span><div class="muted" style="margin-top:8px">No connection profile or legacy FTP host configured.</div><div class="muted" style="margin-top:10px">Other texts: <i>Remote origin is not configured.</i> / <i>No active project selected.</i> / <i>Legacy host: ftp.example.com</i></div></div>
<div class="card dense"><div class="sr-t">Not a Git repository / no project</div><div>Current Branch: <b>Not a Git Repository</b></div><div class="row" style="margin-top:10px;gap:20px"><div class="big accent">N/A</div><div class="big ok">N/A</div><div class="big" style="font-size:18px">No Project</div></div><div class="muted" style="margin-top:10px">Path: Please select or setup a project. Last Deploy shows <b>Never</b> when no deploy exists.</div></div>
<div class="card dense"><div class="sr-t">Empty recent activity</div><div class="panel" style="padding:10px;text-align:center"><span class="muted">${ic('clipboard')} No backup activity yet.</span></div><div class="sr-t" style="margin-top:14px">Push status badge</div><span class="sbadge warning">${ic('cloud', 'ic-sm')}Push pending: 1 commit(s)</span> <span class="muted">(hidden when nothing is ahead)</span></div>
</div></div>`;

  L.page({ slug: 'dashboard', title: 'Dashboard', nav: 'dashboard', group: 'Pages', css: dashCss, body: dashBody });

  // =====================================================================
  // HISTORY  (Pages/HistoryPage.xaml)
  // =====================================================================
  const commits = [
    ['a3f9c21', 'feat(deploy): add resumable SFTP upload queue', '2026/11/02 00:39', 14, 'master', true, 'e91b7d04c3a2f5518d0a4ab7c1b3e7f2a3f9c21d'],
    ['7be40d9', 'fix(git): handle detached HEAD when reading branch', '2026/11/02 00:21', 2, 'master', true],
    ['c10e8a5', 'refactor: split FtpService into transfer and listing', '2026/11/02 00:15', 9, 'master', true],
    ['5d2f7b3', 'chore: bump MahApps.Metro to 2.4.10', '2026/11/02 00:10', 3, 'master', true],
    ['09ab4ee', 'style(ui): unify card paddings on Settings page', '2026/10/16 17:09', 6, 'master', true],
    ['f4c61d8', 'fix: backup scheduler skipped runs after DST change', '2026/10/15 22:48', 4, 'master', true],
    ['2e8d0b6', 'feat(telegram): send deploy summary with changed files', '2026/10/14 11:02', 7, 'master', true],
    ['b7731fa', 'docs: update README with SFTP key auth example', '2026/10/12 09:30', 1, 'master', true],
    ['1c9e5a0', 'perf(history): index changed files lazily', '2026/10/10 18:12', 5, 'master', true],
    ['8ad2c47', 'fix(db): quote identifiers in mysqldump command', '2026/10/09 14:55', 2, 'master', true],
  ];
  const commitItem = (c) => `<div class="hc-item"><div class="hc-av ok">${ic('check', 'ic-lg')}</div>
<div class="hc-main"><div class="row between" style="gap:10px"><b class="ellipsis" style="font-size:15px"><span class="mono muted" style="font-weight:500">${c[0]}</span> - ${c[1]}</b><span class="sbadge-solid ok">Success</span></div>
<div class="muted mono" style="font-size:12px;margin:6px 0 10px">Date: ${c[2].replace(' ', ' - ')} | Files: ${c[3]} | Branch: ${c[4]}</div>
<div class="row"><button type="button" class="btn ghost" style="height:34px;font-size:12px" data-open="dlgDetails">Details</button><button type="button" class="btn ghost" style="height:34px;font-size:12px" data-open="dlgRollback">Rollback</button></div></div></div>`;
  const hit = (sum, kind, date, hash, can = true) => `<div class="list-item hit" style="flex-direction:column;align-items:stretch;gap:0"><div class="ellipsis" style="font-weight:600">${sum}</div>
<div class="row" style="margin:6px 0 8px"><span class="sbadge-solid ${kind === 'NEW' ? 'ok' : kind === 'DELETED' ? 'err' : 'warn'}" style="padding:3px 8px">${kind}</span><span class="muted">${date}</span><span class="muted mono">${hash}</span></div>
<div class="row"><button type="button" class="btn primary compact" style="height:32px" ${can ? 'data-open="dlgContent"' : 'disabled'}>Open content</button><button type="button" class="btn ghost compact" style="height:32px" ${can ? 'data-open="dlgDiff"' : 'disabled'}>View diff</button></div></div>`;
  const sug = (p, t) => `<div class="list-item" style="padding:8px 12px"><span class="mono">${p}</span><span class="muted r" style="margin-left:auto;font-size:11px">${t}</span></div>`;

  const histCss = `
.hs{max-width:1500px;margin:0 auto}
.sbadge-solid{display:inline-flex;align-items:center;padding:4px 10px;border-radius:6px;font-size:11px;font-weight:700;color:var(--text-inverse);flex:none}
.sbadge-solid.ok{background:var(--success)}.sbadge-solid.warn{background:var(--warning)}.sbadge-solid.err{background:var(--error)}
.hs-cols{display:grid;grid-template-columns:minmax(0,7fr) minmax(0,5fr);gap:12px;align-items:start}
.hs-list{overflow:auto;max-height:max(380px,calc(100vh - 360px));padding-right:4px}
.hc-item{display:flex;gap:12px;padding:16px;margin-bottom:10px;background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:10px;transition:border-color .12s}
.hc-item:hover{border-color:var(--border-strong)}
.hc-av{width:44px;height:44px;border-radius:50%;display:grid;place-items:center;flex:none;color:var(--text-inverse)}
.hc-av.ok{background:var(--success)}.hc-av.bad{background:var(--error)}
.hc-main{min-width:0;flex:1}
.hit{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:8px;margin-bottom:8px;padding:10px 12px}
.hs-sug{max-height:220px;overflow:auto}
.hs-skel{display:flex;gap:12px;padding:16px;margin-bottom:10px;background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:10px}
@media (max-width:1099.98px){.hs-cols{grid-template-columns:minmax(0,1fr)}.hs-list{max-height:520px}}
`;

  const histBody = `<div class="hs">
<div class="page-head"><div class="row" style="gap:15px"><div style="font-size:40px;line-height:1;color:var(--accent)">${ic('history', 'ic-2xl')}</div><div><h1 class="page-title">Commit History</h1><div class="page-sub">Fast loading with file search and snapshot preview.</div></div></div>
<div class="r" style="flex-direction:column;align-items:flex-end;gap:8px"><span class="muted mono" id="loadedSummary">Loaded: 10 / 1284</span>${btn('refresh', 'Refresh', 'primary', 'style="height:40px;padding:0 18px" data-working="Refreshing..." data-done="History" data-done-msg="History refreshed."')}</div></div>

<div class="card" style="margin-bottom:16px"><div class="section-title" style="margin-bottom:0">File search (loaded commits)</div>
<div class="muted" style="margin:4px 0 12px">Type a file path to get live suggestions and open snapshots from commit history.</div>
<div class="row"><div class="input-icon grow">${ic('search')}<input class="input mono" id="fileSearch" data-filter="#sugList" placeholder="Search changed files..." value="Services/"></div>${iconBtn('x', 'Clear search', '', 'data-clear="#fileSearch"')}</div>
<div class="card" id="sugBox" style="margin-top:10px;padding:10px"><div class="muted" style="margin-bottom:8px">Suggestions</div>
<div class="list hs-sug" id="sugList" data-selectable>
${sug('Services/FtpService.cs', '6 commits')}${sug('Services/GitService.cs', '11 commits')}${sug('Services/HistoryService.cs', '4 commits')}${sug('Services/Backup/BackupScheduler.cs', '9 commits')}${sug('Services/Localization/Loc.cs', '2 commits')}
</div></div></div>

<div class="hs-cols">
<div class="card"><div style="margin-bottom:12px"><div class="section-title" style="margin-bottom:0">Commits</div><div class="muted">Latest commits are loaded in batches of 50.</div></div>
<div class="hs-list" id="commitList">${commits.map(commitItem).join('')}</div>
<div class="row" style="margin-top:10px">${btn('', 'Load more', 'toolbar', 'style="height:36px;padding:0 16px" data-working="Loading..." data-done="History" data-done-msg="Loaded 50 more commits."')}<span class="muted" hidden>No commits available.</span></div></div>

<div class="card"><div style="margin-bottom:12px"><div class="section-title" style="margin-bottom:0">File matches</div><div class="muted"><span class="mono">Services/FtpService.cs</span> (6 hits)</div></div>
<div class="hs-list" id="hitList">
${hit('feat(deploy): add resumable SFTP upload queue', 'MODIFIED', '2026/11/02 00:39', 'a3f9c21')}
${hit('refactor: split FtpService into transfer and listing', 'MODIFIED', '2026/11/02 00:15', 'c10e8a5')}
${hit('feat(ftp): keep-alive for idle FTP sessions', 'MODIFIED', '2026/09/28 16:20', '3b6e2f1')}
${hit('feat: add FtpService with passive mode support', 'NEW', '2026/08/14 10:05', '0d4c9a7')}
${hit('chore: remove legacy FtpService.cs (moved)', 'DELETED', '2026/07/02 13:41', 'e2a7710', false)}
</div></div></div>

<div class="section-title" style="margin-top:28px">${ic('layers')}State reference <span class="badge">other states of the same page</span></div>
<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(280px,1fr))">
<div class="card dense"><div class="eyebrow" style="margin-bottom:8px">Loading first batch</div><div class="muted mono" style="margin-bottom:8px">Loaded: 0</div><div class="hs-skel"><div class="skeleton" style="width:44px;height:44px;border-radius:50%"></div><div class="grow stack sm"><div class="skeleton" style="width:70%"></div><div class="skeleton" style="width:45%"></div></div></div><div class="progress indeterminate"><i></i></div></div>
<div class="card dense"><div class="eyebrow" style="margin-bottom:8px">Empty / end of list</div><div class="row">${btn('', 'No more commits', 'toolbar', 'disabled')}<span class="muted">No commits available.</span></div><hr class="divider" style="margin:10px 0"><div class="muted">No matching file found in loaded commits.</div><div class="muted" style="margin-top:6px">No commit hit available for this file.</div><div class="muted" style="margin-top:6px">Select a suggested file to see commits.</div></div>
<div class="card dense"><div class="eyebrow" style="margin-bottom:8px">Failed record</div><div class="hc-item" style="margin:0"><div class="hc-av bad">${ic('x', 'ic-lg')}</div><div class="hc-main"><div class="row between"><b>Deploy to production</b><span class="sbadge-solid err">Failed</span></div><div class="muted mono" style="font-size:12px;margin-top:6px">Date: 2026/10/30 - 21:14 | Files: 8 | Branch: master</div></div></div></div>
</div></div>`;

  const histDialogs = `
<div class="scrim" id="dlgDetails"><div class="modal"><div class="modal-h">${ic('history')}<h3>Commit details</h3><span class="r"><button class="btn icon ghost compact" data-close>${ic('x')}</button></span></div>
<div class="modal-b"><dl class="kv"><dt>Commit</dt><dd class="mono">a3f9c21 (e91b7d04c3a2f5518d0a4ab7c1b3e7f2a3f9c21d)</dd><dt>Message</dt><dd>feat(deploy): add resumable SFTP upload queue</dd><dt>Date</dt><dd>2026/11/02 - 00:39</dd><dt>Branch</dt><dd><span class="chip active mono" style="height:22px">${ic('git-branch')}master</span></dd><dt>Files</dt><dd>14</dd></dl>
<div class="tree" style="margin-top:12px">${node('Services', { dir: true, open: true, children: node('FtpService.cs', { git: 'M' }) + node('UploadQueue.cs', { git: 'A' }) })}${node('Pages', { dir: true, open: true, children: node('DeployPage.xaml', { git: 'M' }) + node('DeployPage.xaml.cs', { git: 'M' }) })}</div></div>
<div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>
<div class="scrim" id="dlgRollback"><div class="modal sm"><div class="modal-h">${ic('undo')}<h3>Confirm rollback</h3></div><div class="modal-b" style="text-align:center"><div class="hero warn">${ic('alert', 'ic-xl')}</div><div style="text-align:left">Are you sure you want to rollback this commit?<div class="mono" style="margin:10px 0;color:var(--text-secondary)">Commit: a3f9c21 - feat(deploy): add resumable SFTP upload queue</div>This creates a new revert commit.</div></div>
<div class="modal-f"><button class="btn" data-close>No</button><button class="btn primary" data-close data-toast="success" data-title="Rollback" data-msg="Rollback successful. A new revert commit has been created.">Yes, rollback</button></div></div></div>
<div class="scrim" id="dlgContent"><div class="modal lg"><div class="modal-h">${ic('file-code')}<h3 class="mono" style="font-size:14px">Services/FtpService.cs <span class="muted">@ a3f9c21</span></h3><span class="r"><button class="btn icon ghost compact" data-close>${ic('x')}</button></span></div>
<div class="modal-b" style="padding:0"><div class="code" style="border:0;border-radius:0;max-height:50vh"><div class="gut">1<br>2<br>3<br>4<br>5<br>6<br>7</div><pre><span class="tok-k">public async</span> Task&lt;<span class="tok-k">bool</span>&gt; <span class="tok-f">UploadAsync</span>(<span class="tok-k">string</span> local, <span class="tok-k">string</span> remote)
{
    <span class="tok-c">// resumable: continue from the remote size</span>
    <span class="tok-k">var</span> offset = <span class="tok-k">await</span> <span class="tok-f">GetRemoteSizeAsync</span>(remote);
    <span class="tok-k">await using var</span> fs = File.<span class="tok-f">OpenRead</span>(local);
    fs.Seek(offset, SeekOrigin.Begin);
    <span class="tok-k">return await</span> _client.<span class="tok-f">AppendAsync</span>(remote, fs);
}</pre></div></div><div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>
<div class="scrim" id="dlgDiff"><div class="modal lg"><div class="modal-h">${ic('diff')}<h3 class="mono" style="font-size:14px">Services/FtpService.cs <span class="badge warning">MODIFIED</span></h3><span class="r"><button class="btn icon ghost compact" data-close>${ic('x')}</button></span></div>
<div class="modal-b" style="padding:0"><div class="diff" style="border:0;border-radius:0;max-height:50vh">
<div class="dl hunk"><span></span><span></span><span>@@ -41,7 +41,9 @@ public async Task&lt;bool&gt; UploadAsync</span></div>
<div class="dl"><span>41</span><span>41</span><span>{</span></div>
<div class="dl del"><span>42</span><span></span><span>await using var fs = File.OpenRead(local);</span></div>
<div class="dl add"><span></span><span>42</span><span>var offset = await GetRemoteSizeAsync(remote);</span></div>
<div class="dl add"><span></span><span>43</span><span>await using var fs = File.OpenRead(local);</span></div>
<div class="dl add"><span></span><span>44</span><span>fs.Seek(offset, SeekOrigin.Begin);</span></div>
<div class="dl"><span>43</span><span>45</span><span>return await _client.AppendAsync(remote, fs);</span></div></div></div>
<div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>`;

  L.page({ slug: 'history', title: 'History', nav: 'history', group: 'Pages', css: histCss, body: histBody, dialogs: histDialogs });

  // =====================================================================
  // COMPONENTS gallery
  // =====================================================================
  const tokens = [
    ['Surfaces', [
      ['--surface-base', 'Surface.Base', '#000000'], ['--surface-shell', 'Surface.Shell', '#0A0A0A'], ['--surface-raised', 'Surface.Raised', '#111111'], ['--surface-card', 'Surface.Card', '#141414'], ['--surface-input', 'Surface.Input', '#0D0D0D'],
      ['--surface-hover', 'State.Hover', '#14FFFFFF'], ['--surface-pressed', 'State.Pressed', '#22FFFFFF'], ['--surface-overlay', 'Surface.Overlay', '#CC000000'], ['--border-subtle', 'Border.Subtle', '#222222'], ['--border-strong', 'Border.Strong', '#333333']]],
    ['Text', [['--text-primary', 'Text.Primary', '#E8E8E8'], ['--text-secondary', 'Text.Secondary', '#C8C8C8'], ['--text-muted', 'Text.Muted', '#8A8A8A'], ['--text-inverse', 'Text.Inverse', '#000000']]],
    ['Accent', [['--accent', 'Accent.Primary', '#4C8DFF'], ['--accent-hover', 'Accent.PrimaryHover', '#6AA0FF'], ['--accent2', 'Accent.Secondary', '#7B61FF'], ['--accent2-hover', 'Accent.SecondaryHover', '#9683FF']]],
    ['Status', [['--success', 'Status.Success', '#3DDC84'], ['--warning', 'Status.Warning', '#E6B84D'], ['--error', 'Status.Error', '#FF5C6A'], ['--info', 'Status.Info', '#5BB8FF'],
      ['--success-surface', 'Status.SuccessSurface', '#0A1A12'], ['--warning-surface', 'Status.WarningSurface', '#1A1408'], ['--error-surface', 'Status.ErrorSurface', '#1A0A0C'], ['--info-surface', 'Status.InfoSurface', '#0A121A']]],
    ['Chat / tree', [['--chat-in', 'Chat.BubbleIn', '#1A1A1A'], ['--chat-out', 'Chat.BubbleOut', '#4C8DFF'], ['--chat-composer', 'Chat.Composer', '#0D0D0D'], ['--accent', 'Chat.Unread', '#4C8DFF'], ['--warning', 'Tree.SearchMatchBackground', '#E6B84D']]],
  ];
  const swatches = tokens.map(([g, list]) => `<div class="eyebrow" style="margin:14px 0 8px">${g}</div><div class="grid" style="grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:10px">${list.map(([v, k, hex]) => `<div class="sw"><span class="sw-c" style="background:linear-gradient(var(${v}),var(${v})),repeating-conic-gradient(#333 0 25%,#111 0 50%) 0 0/10px 10px"></span><span class="sw-t"><b class="mono">${v}</b><small class="mono">${k}</small><small class="mono">WPF dark ${hex}</small></span></div>`).join('')}</div>`).join('');

  const sec = (id, title, icon, inner, sub = '') => `<section id="${id}" class="cg-sec"><div class="section-title">${ic(icon)}${title}${sub ? `<span class="muted" style="font-size:12px;font-weight:400">${sub}</span>` : ''}</div>${inner}</section>`;
  const demo = (label, inner) => `<div class="cg-demo"><div class="eyebrow">${label}</div><div class="row wrap" style="gap:10px">${inner}</div></div>`;

  const compCss = `
.cg{max-width:1500px;margin:0 auto}
.cg-nav{position:sticky;top:-14px;z-index:5;display:flex;flex-wrap:wrap;gap:6px;padding:8px 0 12px;background:linear-gradient(var(--surface-base) 80%,transparent)}
.cg-sec{margin-bottom:34px;scroll-margin-top:60px}
.cg-demo{margin-bottom:14px;display:flex;flex-direction:column;gap:8px}
.sw{display:flex;gap:10px;align-items:center;padding:8px;border:1px solid var(--border-subtle);border-radius:10px;background:var(--surface-card)}
.sw-c{width:44px;height:44px;border-radius:8px;border:1px solid var(--border-strong);flex:none}
.sw-t{display:flex;flex-direction:column;min-width:0}.sw-t b{font-size:12px}.sw-t small{color:var(--text-muted);font-size:10.5px;overflow:hidden;text-overflow:ellipsis}
.cg .box{border:1px solid var(--border-subtle);border-radius:10px;overflow:hidden;background:var(--surface-card);min-width:0}
.cg .vtabs{display:grid;grid-template-columns:180px minmax(0,1fr);gap:16px}
.cg .tip-wrap{position:relative;display:inline-block}
.cg .tip-wrap .tip{position:absolute;left:50%;bottom:calc(100% + 6px);transform:translateX(-50%);opacity:0;pointer-events:none;transition:.12s;white-space:nowrap;z-index:3}
.cg .tip-wrap:hover .tip{opacity:1}
.cg .tip-static{position:static}
.cg .skeleton{background:linear-gradient(90deg,var(--border-subtle) 25%,var(--border-strong) 50%,var(--border-subtle) 75%);background-size:200% 100%}
.cg .spec{font-family:var(--font-mono);font-size:11px;color:var(--text-muted)}
.cg .menu-demo{display:flex;gap:20px;flex-wrap:wrap;align-items:flex-start}
.cg .ctx-zone{padding:26px;border:1px dashed var(--border-strong);border-radius:10px;color:var(--text-muted);min-width:260px;text-align:center}
.cg .pad-demo{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.cg .shape{display:grid;place-items:center;background:var(--surface-card);border:1px solid var(--border-strong);color:var(--text-muted);font-size:10px;font-family:var(--font-mono)}
@media (max-width:1099.98px){.cg .vtabs{grid-template-columns:1fr}}
`;

  const tabsSet = (scope, cls, extra = '') => `<div data-tabscope class="box" style="padding:0 0 12px"><div class="tabs ${cls}"><button class="tab active" data-tab="${scope}1">${ic('rocket')}Deploy</button><button class="tab" data-tab="${scope}2">History ${badge('12', 'accent')}</button><button class="tab" data-tab="${scope}3">Settings</button><button class="tab" disabled style="opacity:.4">Disabled</button></div>
<div style="padding:12px 14px"><div class="tabpanel active" id="${scope}1">Panel one: deploy options.</div><div class="tabpanel" id="${scope}2">Panel two: commit history list.</div><div class="tabpanel" id="${scope}3">Panel three: app settings.</div></div></div>`;

  const body = `<div class="cg">
<div class="page-head"><div><h1 class="page-title">Components</h1><div class="page-sub">Developer reference of every shared control in <span class="mono">core.css</span>, with matching WPF resource keys.</div></div><div class="r">${badge('core.css', 'accent')}${badge('Midnight Blue')}</div></div>
<nav class="cg-nav">${[['c-colors', 'Colors'], ['c-type', 'Type & spacing'], ['c-btn', 'Buttons'], ['c-input', 'Inputs'], ['c-sel', 'Checks'], ['c-badge', 'Badges'], ['c-prog', 'Progress'], ['c-tabs', 'Tabs'], ['c-grid', 'Grid'], ['c-list', 'List/Tree'], ['c-term', 'Terminal/Diff'], ['c-work', 'Workers'], ['c-banner', 'Banners'], ['c-modal', 'Modals'], ['c-toast', 'Toasts'], ['c-menu', 'Menus'], ['c-misc', 'Misc']].map(([h, t]) => `<a class="chip" href="#${h}">${t}</a>`).join('')}</nav>

${sec('c-colors', 'Color tokens', 'palette', swatches, 'CSS variable - WPF resource key (Themes/Colors.Dark.xaml). The HTML theme is a blue-tinted variant of the neutral WPF dark values.')}

${sec('c-type', 'Typography, radius & spacing', 'edit', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(320px,1fr))"><div class="card stack sm">
<div style="font-size:var(--fs-title);font-weight:700;line-height:1.1">Page title 32 <span class="spec">App.PageTitleTextStyle / FontSize.PageTitle</span></div>
<div style="font-size:var(--fs-section);font-weight:600">Section title 18 <span class="spec">App.SectionTitleTextStyle</span></div>
<div style="font-size:var(--fs-body-lg)">Body large 14 <span class="spec">FontSize.BodyLarge / PageSubtitle</span></div>
<div>Body 13 <span class="spec">App.BodyTextStyle</span></div>
<div class="muted">Muted 13 <span class="spec">App.MutedTextStyle</span></div>
<div class="muted" style="font-size:var(--fs-caption)">Caption 11 <span class="spec">App.CaptionTextStyle</span></div>
<div class="mono">Mono 13 - src/Services/FtpService.cs <span class="spec">FontFamily.Mono</span></div>
<div><kbd class="badge">Ctrl</kbd> + <kbd class="badge">Shift</kbd> + <kbd class="badge">P</kbd> <kbd class="badge">Esc</kbd></div></div>
<div class="card stack"><div class="pad-demo"><span class="shape" style="width:80px;height:48px;border-radius:12px">card 12</span><span class="shape" style="width:80px;height:36px;border-radius:8px">input 8</span><span class="shape" style="width:80px;height:36px;border-radius:8px">btn 8</span><span class="shape" style="width:80px;height:26px;border-radius:999px">chip 999</span></div>
<table class="dg" style="min-width:0"><thead><tr><th>CSS</th><th>WPF</th><th class="num">Value</th></tr></thead><tbody>
<tr><td class="mono">--page-margin</td><td class="mono">Spacing.PageMargin</td><td class="num mono">30</td></tr><tr><td class="mono">--card-pad</td><td class="mono">Spacing.CardPadding</td><td class="num mono">16</td></tr><tr><td class="mono">--card-pad-dense</td><td class="mono">Spacing.CardPaddingDense</td><td class="num mono">12</td></tr><tr><td class="mono">--gap (card margin)</td><td class="mono">Spacing.CardMargin</td><td class="num mono">0,0,0,12</td></tr><tr><td class="mono">--r-card</td><td class="mono">Radius.Card</td><td class="num mono">12</td></tr><tr><td class="mono">--r-input / --r-btn</td><td class="mono">Radius.Input / Radius.Button</td><td class="num mono">8</td></tr><tr><td class="mono">--r-chip</td><td class="mono">Radius.Chip</td><td class="num mono">999</td></tr></tbody></table></div></div>`)}

${sec('c-btn', 'Buttons', 'zap', `
${demo('.btn variants <span class="spec">App.Button.*</span>', `${btn('', 'Default')}${btn('rocket', 'Primary', 'primary')}${btn('', 'Secondary', 'secondary')}${btn('', 'Ghost', 'ghost')}${btn('trash', 'Danger', 'danger')}${btn('trash', 'Danger solid', 'danger solid')}${btn('check', 'Success', 'success')}${btn('', 'Toolbar', 'toolbar')}${btn('', 'Pill', 'pill')}${btn('', 'Disabled', 'primary', 'disabled')}`)}
${demo('compact (28px) / sizes', `${btn('refresh', 'Compact', 'compact')}${btn('', 'Primary', 'primary compact')}${btn('', 'Ghost', 'ghost compact')}${btn('', 'Danger', 'danger compact')}${btn('play', 'Start', 'primary', 'style="height:40px;padding:0 18px"')}`)}
${demo('icon buttons <span class="spec">App.IconButton</span>', `${iconBtn('settings', 'Settings')}${iconBtn('x', 'Close', 'ghost')}${iconBtn('trash', 'Delete', 'danger')}${iconBtn('refresh', 'Refresh', 'compact')}${iconBtn('play', 'Play', 'compact ghost')}${iconBtn('pause', 'Pause', 'compact ghost')}${iconBtn('more', 'More', 'compact ghost')}`)}
${demo('working state (click them) - data-working', `${btn('rocket', 'Deploy now', 'primary', 'data-working="Deploying..." data-done="Deployed" data-done-msg="14 files uploaded to production"')}${btn('refresh', 'Refresh', '', 'data-working="Refreshing..." data-done="Refreshed"')}${btn('refresh', 'Working (static)', 'primary working')}${btn('', 'Block button', 'block', 'style="max-width:220px"')}`)}
${demo('button group / split / input group', `<div class="btn-group" data-single><button class="btn active">Staged</button><button class="btn">Unstaged</button><button class="btn">All</button></div><div class="btn-group">${btn('rocket', 'Deploy', 'primary')}<button class="btn primary icon" title="More deploy options">${ic('chevron-down')}</button></div><div class="input-group" style="width:300px">${inp('Search commits...')}<button class="btn" type="button">${ic('search')}</button></div>`)}`)}

${sec('c-input', 'Inputs', 'edit', `<div class="form-grid">
${field('Text input', inp('e.g. production', ''), '', 'Hint text under the field')}
${field('With value (mono)', inp('', '/var/www/html/app', 'text', 'mono'))}
${field('Invalid', inp('', 'ftp..example', 'text', 'invalid'), '', '<span class="err">Host is not valid.</span>')}
${field('Disabled', '<input class="input" disabled value="Locked value">')}
${field('Select', sel(['master', ['develop', true], 'release/2.4', 'hotfix/ftp-timeout']))}
${field('Select (compact)', sel(['SFTP', 'FTP', 'FTPS'], 'sm'))}
${field('Password', `<div class="input-group">${inp('', 'hunter2secret', 'password')}<button class="btn icon" style="height:36px" title="Show / hide">${ic('eye')}</button></div>`)}
${field('Search with icon', `<div class="input-icon">${ic('search')}${inp('Search changed files...')}</div>`)}
${field('Number', inp('', '21', 'number'))}
${field('Textarea (mono)', '<textarea class="input mono" rows="3">vendor/\nnode_modules/\n.env</textarea>', 'full')}
${field('Range', '<input class="range" type="range" value="60">')}
</div>`)}

${sec('c-sel', 'Checkbox, radio, switch', 'check-circle', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(240px,1fr))"><div class="card stack sm"><div class="eyebrow">Checkbox</div>${chk('Unchecked')}${chk('Checked', true)}<label class="check"><input type="checkbox" disabled checked>Disabled checked</label><label class="check"><input type="checkbox" id="indet">Indeterminate</label><script>document.getElementById('indet').indeterminate=true</script></div>
<div class="card stack sm"><div class="eyebrow">Radio</div>${chk('Upload changed files', true, 'radio', 'r1')}${chk('Upload all files', false, 'radio', 'r1')}${chk('Dry run', false, 'radio', 'r1')}</div>
<div class="card stack sm"><div class="eyebrow">Switch</div>${sw('Auto-refresh git status', true)}${sw('Create backup before deploy', false)}<label class="switch"><input type="checkbox" disabled checked>Disabled</label></div></div>`)}

${sec('c-badge', 'Badges, chips, dots', 'tag', `
${demo('.badge', `${badge('Default')}${badge('Accent', 'accent')}${badge('Success', 'success')}${badge('Warning', 'warning')}${badge('Error', 'error')}${badge('Info', 'info')}${badge('Violet', 'violet')}`)}
${demo('git status badges', `${badge('NEW', 'new')}${badge('MODIFIED', 'modified')}${badge('DELETED', 'deleted')}<span class="gb ok mono" style="font-weight:700">A</span><span class="gb warn mono" style="font-weight:700">M</span><span class="gb err mono" style="font-weight:700">D</span><span class="sbadge-solid" style="background:var(--success);color:#000;padding:4px 10px;border-radius:6px;font-size:11px;font-weight:700">NEW</span><span class="spec">solid variant used in History</span>`)}
${demo('chips (data-chip toggles)', `<span class="chip" data-chip>${ic('git-branch')}develop</span><span class="chip active" data-chip>${ic('git-branch')}master</span><span class="chip" data-chip>${ic('tag')}v2.4.0</span><span class="chip" data-chip>${ic('filter')}*.php</span><span class="chip active" data-chip>.js</span>`)}
${demo('dots', `<span class="dot"></span> idle <span class="dot ok"></span> ok <span class="dot warn"></span> warn <span class="dot err"></span> error <span class="dot info"></span> info <span class="dot live"></span> live <span class="dot live blue"></span> busy <span class="equalizer"><i></i><i></i><i></i></span> equalizer <span class="pulse">pulse text</span> ${ic('spinner', 'spin')} spinner`)}`)}

${sec('c-prog', 'Progress', 'activity', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(260px,1fr))"><div class="card stack">
<div class="spec">default 40%</div>${progress(40)}<div class="spec">lg</div>${progress(65, 'lg')}<div class="spec">success</div>${progress(100, 'success')}<div class="spec">warning</div>${progress(55, 'warning')}<div class="spec">error</div>${progress(30, 'error')}</div>
<div class="card stack"><div class="spec">busy (striped, animated, live)</div><div class="progress busy lg"><i data-progress="20" data-speed="1.5" data-loop style="width:20%"></i></div><div class="spec">indeterminate</div><div class="progress indeterminate"><i></i></div><div class="spec">step-bar</div><div class="step-bar"><span class="done"></span><span class="done"></span><span class="cur"></span><span></span></div>
<div class="spec">elapsed counter</div><div class="mono" data-elapsed="754">00:12:34</div></div></div>`)}

${sec('c-tabs', 'Tabs', 'layers', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(340px,1fr))"><div><div class="eyebrow" style="margin-bottom:6px">underline <span class="spec">App.TabItemStyle</span></div>${tabsSet('tu', '')}</div><div><div class="eyebrow" style="margin-bottom:6px">pills</div>${tabsSet('tp', 'pills')}</div>
<div><div class="eyebrow" style="margin-bottom:6px">vertical</div><div data-tabscope class="box vtabs" style="padding:10px"><div class="tabs vertical"><button class="tab active" data-tab="tv1">${ic('settings')}General</button><button class="tab" data-tab="tv2">${ic('server')}Connections</button><button class="tab" data-tab="tv3">${ic('palette')}Appearance</button></div><div><div class="tabpanel active" id="tv1">General settings.</div><div class="tabpanel" id="tv2">Connection profiles.</div><div class="tabpanel" id="tv3">Theme options.</div></div></div></div></div>`)}

${sec('c-grid', 'Data grid', 'table', `<div class="box"><div class="grid-wrap" style="max-height:300px"><table class="dg" data-select><thead><tr><th class="chk"><label class="check"><input type="checkbox"></label></th><th>Status</th><th>File</th><th>Type</th><th class="num">Size</th><th>Modified</th></tr></thead><tbody>
<tr class="sel"><td class="chk"><label class="check"><input type="checkbox" checked></label></td><td>${badge('MODIFIED', 'modified')}</td><td class="mono">app/Http/Controllers/DeployController.php</td><td>PHP</td><td class="num mono">12.4 KB</td><td class="mono">2026/11/02 00:39</td></tr>
<tr><td class="chk"><label class="check"><input type="checkbox" checked></label></td><td>${badge('NEW', 'new')}</td><td class="mono">resources/js/deploy-queue.js</td><td>JS</td><td class="num mono">3.1 KB</td><td class="mono">2026/11/02 00:21</td></tr>
<tr><td class="chk"><label class="check"><input type="checkbox"></label></td><td>${badge('DELETED', 'deleted')}</td><td class="mono">public/css/legacy.css</td><td>CSS</td><td class="num mono">48.0 KB</td><td class="mono">2026/10/30 18:02</td></tr>
<tr class="dim"><td class="chk"><label class="check"><input type="checkbox"></label></td><td>${badge('Ignored')}</td><td class="mono">storage/logs/laravel.log</td><td>LOG</td><td class="num mono">1.2 MB</td><td class="mono">2026/11/02 00:40</td></tr></tbody></table></div></div>`, 'click a row to select; .dim = ignored')}

${sec('c-list', 'List, tree, breadcrumbs', 'folder', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(300px,1fr));align-items:start">
<div class="box"><div class="list" data-selectable><div class="list-item sel"><div class="lead">${ic('server')}</div><div class="grow"><div class="t">Production</div><div class="d mono">sftp://deploy@203.0.113.10:22</div></div>${badge('SFTP', 'accent')}</div><div class="list-item"><div class="lead">${ic('server')}</div><div class="grow"><div class="t">Staging</div><div class="d mono">ftp://staging.example.com:21</div></div>${badge('FTP')}</div><div class="list-item"><div class="lead">${ic('cloud')}</div><div class="grow"><div class="t">Backup bucket</div><div class="d">Not tested</div></div><span class="dot warn"></span></div></div></div>
<div class="box" style="padding:8px"><div class="crumbs" style="margin-bottom:8px"><a href="#">/</a>${ic('chevron-right')}<a href="#">var</a>${ic('chevron-right')}<a href="#">www</a>${ic('chevron-right')}<a href="#">html</a></div>
<div class="tree" data-ctx="ctxTree">${node('app', { dir: true, open: true, children: node('Http', { dir: true, open: true, children: node('DeployController.php', { git: 'M', meta: '12 KB', sel: true }) + node('Kernel.php', { meta: '4 KB' }) }) + node('Models', { dir: true }) })}${node('resources', { dir: true, open: true, children: node('deploy-queue.js', { git: 'A', meta: '3 KB' }) + node('app.css', { meta: '9 KB' }) + node('index.html', { meta: '2 KB' }) })}${node('config.json', { meta: '1 KB' })}${node('MainWindow.xaml', { git: 'M' })}${node('App.xaml.cs', {})}${node('logo.png', { meta: '18 KB' })}${node('README.md', {})}${node('legacy.php', { git: 'D' })}</div><div class="spec" style="margin-top:6px">right-click the tree for a context menu</div></div></div>
<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(260px,1fr));margin-top:12px"><div class="box"><div class="empty">${ic('folder-open')}<b>Nothing here yet</b><span>Empty state with icon and hint text.</span>${btn('', 'Add item', 'primary compact')}</div></div><div class="box" style="padding:14px"><div class="eyebrow" style="margin-bottom:8px">skeleton (loading)</div><div class="stack sm"><div class="skeleton" style="width:80%"></div><div class="skeleton" style="width:60%"></div><div class="skeleton" style="width:70%"></div></div></div></div>`)}

${sec('c-term', 'Terminal, log, code, diff', 'terminal', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(340px,1fr))">
<div class="box"><div class="pane-h">${ic('terminal')}Terminal (live)</div>${stream([['p', 'PS C:\\laragon\\www\\myproject> git status -sb'], ['i', '## master...origin/master [ahead 3]'], ['w', ' M app/Http/Controllers/DeployController.php'], ['u', ' A resources/js/deploy-queue.js'], ['p', 'PS C:\\laragon\\www\\myproject> git push origin master'], ['m', 'Enumerating objects: 14, done.'], ['u', 'To github.com:acme/my-project.git   a3f9c21..7be40d9  master -> master'], ['e', 'fatal: unable to access remote: Connection timed out']], { cls: 'terminal', interval: 1100, style: 'height:200px;border:0;border-radius:0' })}<div class="term-input"><span class="ps">PS&gt;</span><input placeholder="Type a command..."></div></div>
<div class="box"><div class="pane-h">${ic('list')}Log (live)</div>${stream([['i', 'Connecting to 203.0.113.10:22 (SFTP)'], ['ok', 'Authenticated with key id_ed25519'], ['i', 'Uploading app/Http/Controllers/DeployController.php (12.4 KB)'], ['w', 'Retrying resources/js/deploy-queue.js (attempt 2/3)'], ['e', 'Permission denied: /var/www/html/storage/cache'], ['ok', 'Deploy finished: 14 files, 1.2 MB, 00:08']], { cls: 'log', interval: 1000, style: 'height:200px;border:0;border-radius:0' })}</div>
<div class="code" style="max-height:220px"><div class="gut">1<br>2<br>3<br>4<br>5<br>6</div><pre><span class="tok-k">function</span> <span class="tok-f">deploy</span>(files, opts = {}) {
  <span class="tok-c">// upload changed files only</span>
  <span class="tok-k">const</span> queue = files.<span class="tok-f">filter</span>(f =&gt; f.changed);
  <span class="tok-k">return</span> <span class="tok-f">upload</span>(queue, <span class="tok-s">'/var/www/html'</span>, <span class="tok-n">3</span>);
}
<span class="tok-v">$env</span>:<span class="tok-f">DEPLOY_ENV</span> = <span class="tok-s">"production"</span></pre></div>
<div class="diff" style="max-height:220px"><div class="dl hunk"><span></span><span></span><span>@@ -10,6 +10,8 @@ class DeployController</span></div><div class="dl"><span>10</span><span>10</span><span>public function run()</span></div><div class="dl del"><span>11</span><span></span><span>$this-&gt;ftp-&gt;upload($files);</span></div><div class="dl add"><span></span><span>11</span><span>$this-&gt;backup-&gt;create();</span></div><div class="dl add"><span></span><span>12</span><span>$this-&gt;ftp-&gt;upload($files, true);</span></div><div class="dl"><span>12</span><span>13</span><span>return $this-&gt;done();</span></div></div></div>`)}

${sec('c-work', 'Workers (live activity)', 'cpu', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(340px,1fr))">
${worker({ icon: 'upload', name: 'Uploading to Production', desc: 'app/Http/Controllers/DeployController.php', pct: 35, speed: 1.2, state: 'running', loop: true, stats: ['<b>5/14</b> files', '1.2 MB/s', 'ETA <span data-elapsed="6">00:00:06</span>'] })}
${worker({ icon: 'database', name: 'Database backup - myproject_prod', desc: 'Dumping table orders', pct: 58, speed: 0, state: 'paused', stats: ['<b>124 MB</b>', 'paused 00:42'] })}
${worker({ icon: 'cloud', name: 'Sync backups to S3', desc: 'Waiting for the upload slot', pct: 0, speed: 0, state: 'queued', stats: ['position 2 in queue'] })}
${worker({ icon: 'package', name: 'Deploy completed', desc: '14 files, 1.2 MB', pct: 100, speed: 0, state: 'done', stats: ['<b>00:08</b> elapsed'] })}
${worker({ icon: 'upload', name: 'Upload failed', desc: 'Permission denied: /var/www/html/storage/cache', pct: 71, speed: 0, state: 'failed', stats: ['<b>10/14</b> files', 'attempt 3/3'] })}</div>`, 'running / paused / queued / done / failed (pause button and finish are live)')}

${sec('c-banner', 'Banners', 'info', `<div class="stack sm"><div class="banner info">${ic('info', 'ic-lg')}<div class="grow"><b>Info</b> - Fetching remote branches in the background.</div>${btn('', 'Details', 'compact')}</div><div class="banner warn">${ic('alert', 'ic-lg')}<div class="grow"><b>Warning</b> - Push pending: 3 commit(s).</div>${btn('', 'Push', 'compact primary')}</div><div class="banner error">${ic('x-circle', 'ic-lg')}<div class="grow"><b>Error</b> - FTP connection timed out after 30s.</div>${btn('refresh', 'Retry', 'compact')}</div><div class="banner success">${ic('check-circle', 'ic-lg')}<div class="grow"><b>Success</b> - Deploy finished without errors.</div></div><div class="banner">${ic('clipboard', 'ic-lg')}<div class="grow">Neutral banner.</div></div></div>`)}

${sec('c-modal', 'Modals', 'layers', `${demo('open with data-open (Esc / backdrop closes, shared confirm uses data-confirm)', `${btn('info', 'Info modal', '', 'data-open="mdInfo"')}${btn('trash', 'Danger modal', 'danger', 'data-open="mdDanger"')}${btn('edit', 'Form modal', 'primary', 'data-open="mdForm"')}${btn('alert', 'Shared confirm', '', 'data-confirm="Discard 3 uncommitted changes?" data-confirm-title="Discard changes"')}`)}`)}

${sec('c-toast', 'Toasts', 'bell', demo('data-toast (appear bottom-right of the shell)', `${btn('check-circle', 'Success', 'success', 'data-toast="success" data-title="Deploy finished" data-msg="14 files uploaded in 8s"')}${btn('info', 'Info', '', 'data-toast="info" data-title="Fetching" data-msg="Checking origin for new commits"')}${btn('alert', 'Warning', '', 'data-toast="warning" data-title="Push pending" data-msg="3 commit(s) are not pushed"')}${btn('x-circle', 'Error', 'danger', 'data-toast="error" data-title="Connection failed" data-msg="Timed out connecting to 203.0.113.10:22"')}${btn('bookmark', 'Sticky', '', 'data-toast="info" data-title="Sticky" data-msg="No auto close" data-duration="0"')}`))}

${sec('c-menu', 'Menus, popups, tooltips', 'list', `<div class="menu-demo"><div><div class="eyebrow" style="margin-bottom:6px">.menu (context menu)</div><div class="menu"><a class="mi">${ic('eye')}Open<span class="k">Enter</span></a><a class="mi">${ic('copy')}Copy path<span class="k">Ctrl+C</span></a><a class="mi">${ic('diff')}Compare<span class="k">Ctrl+D</span></a><div class="msep"></div><a class="mi danger">${ic('trash')}Delete<span class="k">Del</span></a></div></div>
<div><div class="eyebrow" style="margin-bottom:6px">right-click zone (data-ctx)</div><div class="ctx-zone" data-ctx="ctxTree">Right-click here</div></div>
<div><div class="eyebrow" style="margin-bottom:6px">tooltip (.tip) - hover</div><div class="row" style="gap:20px"><span class="tip tip-static">Open in Explorer</span><span class="tip-wrap">${iconBtn('folder-open', 'Open in Explorer')}<span class="tip">Hover tip</span></span></div></div>
<div><div class="eyebrow" style="margin-bottom:6px">popup (titlebar menus)</div><div class="row">${btn('menu', 'Use the hamburger / project button above', 'ghost compact', 'disabled')}</div></div></div>`)}

${sec('c-misc', 'Avatars, wizard steps, stats, chat, key-value', 'users', `<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(300px,1fr))">
<div class="card stack"><div class="eyebrow">avatars</div><div class="row">${avatar('M')}${avatar('S', 'lg', '#7b61ff')}${avatar('C', 'xl', '#3ddc84')}${avatar('B', 'lg', '#e6b84d')}</div>
<div class="eyebrow">wizard steps</div><div class="wizard-steps"><span class="ws done"><i>${ic('check', 'ic-sm')}</i>Project</span><span class="line"></span><span class="ws cur"><i>2</i>Git</span><span class="line"></span><span class="ws"><i>3</i>FTP</span><span class="line"></span><span class="ws"><i>4</i>Finish</span></div></div>
<div class="grid" style="grid-template-columns:1fr 1fr;align-content:start">${L.stat('upload', '14', 'Changed files')}${L.stat('check-circle', '1284', 'Commits', 'success')}${L.stat('alert', '3', 'Warnings', 'warning')}${L.stat('x-circle', '1', 'Errors', 'error')}${L.stat('info', '2', 'Queued', 'info')}${L.stat('cloud', '7', 'Remote', 'violet')}</div>
<div class="card stack sm"><div class="eyebrow">chat bubbles / thread</div><div class="bubble in">Deploy finished OK<small>00:39</small></div><div class="bubble out">/status<small>00:40</small></div><div class="thread sel">${avatar('P', 'lg')}<div class="grow"><b>Production bot</b><div class="muted ellipsis">Deploy finished OK</div></div><span class="unread">3</span></div></div>
<div class="card"><div class="eyebrow" style="margin-bottom:8px">key / value</div><dl class="kv"><dt>Host</dt><dd class="mono">203.0.113.10</dd><dt>Port</dt><dd class="mono">22</dd><dt>Remote path</dt><dd class="mono">/var/www/html</dd><dt>Status</dt><dd>${badge('Connected', 'success')}</dd></dl></div>
<div class="card flush"><div class="card-head">${ic('server')}<h3>Card with header</h3><div class="r">${btn('plus', 'Add', 'compact')}</div></div><div style="padding:14px" class="muted">.card.flush with .card-head; .card.glow / .card.raised variants exist.</div></div></div>`)}
</div>`;

  const dialogs = `
<div class="ctx menu" id="ctxTree"><a class="mi">${ic('eye')}Open<span class="k">Enter</span></a><a class="mi">${ic('diff')}View diff</a><a class="mi">${ic('copy')}Copy path<span class="k">Ctrl+C</span></a><div class="msep"></div><a class="mi" data-toast="info" data-title="Rename" data-msg="Rename dialog would open">${ic('edit')}Rename<span class="k">F2</span></a><a class="mi danger" data-confirm="Delete this item?">${ic('trash')}Delete<span class="k">Del</span></a></div>
<div class="scrim" id="mdInfo"><div class="modal sm"><div class="modal-h">${ic('info')}<h3>Information</h3></div><div class="modal-b" style="text-align:center"><div class="hero">${ic('info', 'ic-xl')}</div>History refreshed.</div><div class="modal-f"><button class="btn primary" data-close>OK</button></div></div></div>
<div class="scrim" id="mdDanger"><div class="modal sm"><div class="modal-h">${ic('alert')}<h3>Delete connection</h3></div><div class="modal-b" style="text-align:center"><div class="hero err">${ic('trash', 'ic-xl')}</div>Delete the profile <b>Production</b>? This cannot be undone.</div><div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn danger solid" data-close data-toast="success" data-title="Deleted" data-msg="Profile removed">Delete</button></div></div></div>
<div class="scrim" id="mdForm"><div class="modal"><div class="modal-h">${ic('server')}<h3>New connection</h3><span class="r"><button class="btn icon ghost compact" data-close>${ic('x')}</button></span></div><div class="modal-b"><div class="form-grid">${field('Name', inp('e.g. Production'))}${field('Protocol', sel(['SFTP', 'FTP', 'FTPS']))}${field('Host', inp('example.com', '', 'text', 'mono'))}${field('Port', inp('', '22', 'number'))}${field('Remote path', inp('/var/www/html', '', 'text', 'mono'), 'full')}${sw('Use SSH key', true)}</div></div><div class="modal-f"><span class="l">Esc to cancel</span><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="success" data-title="Saved" data-msg="Connection created">Save</button></div></div></div>`;

  L.page({ slug: 'components', title: 'Components', nav: '', group: 'Pages', css: compCss + histCss.split('\n').filter((l) => l.startsWith('.sbadge-solid')).join('\n'), body, dialogs });
};
