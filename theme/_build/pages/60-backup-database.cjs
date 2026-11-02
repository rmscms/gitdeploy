// Backup Scheduler (+ History / Artifacts / Tasks / Integrity / Progress windows) and Database Studio page.
module.exports = (L) => {
  const { ic, badge, btn, iconBtn, field, inp, sel, chk, card } = L;
  const lnk = (icon, label, href, cls = '', attrs = '') => `<a class="btn ${cls}" href="${href}" ${attrs}>${icon ? ic(icon) : ''}${label ? `<span class="bl">${label}</span>` : ''}</a>`;
  const LV = { i: 'INFO', ok: 'OK', w: 'WARN', e: 'ERROR' };
  const stream = (lines, o, pre = 5) => L.stream(lines, o).replace('<span class="cursor"></span>', lines.slice(0, pre).map((l, i) => `<div class="ln"><span class="ts">02:30:${String(4 + i * 2).padStart(2, '0')}</span><span class="lv ${l[0]}">${LV[l[0]]}</span><span>${L.esc(l[1])}</span></div>`).join('') + '<span class="cursor"></span>');
  const okI = ic('check-circle', 'ok');
  const noI = ic('x-circle', 'err');
  const tick = (v) => (v === true ? `<span class="ok" title="Yes">${ic('check')}</span>` : v === false ? `<span class="err" title="No">${ic('x')}</span>` : `<span class="muted">—</span>`);
  const mono = (t) => `<span class="mono">${t}</span>`;
  const cap = (t) => `<div class="hint" style="margin-top:2px">${t}</div>`;
  const resultBox = (cls, t) => `<div class="bk-note ${cls}">${t}</div>`;

  // ---------------------------------------------------------------- shared css
  const sharedCss = `
.bk-note{padding:8px 10px;border-radius:6px;font-size:11px;background:var(--success-surface);color:var(--success);margin-top:8px;overflow-wrap:anywhere}
.bk-note.err{background:var(--error-surface);color:var(--error)}.bk-note.info{background:var(--info-surface);color:var(--info)}.bk-note.warn{background:var(--warning-surface);color:var(--warning)}
.bk-widget{background:linear-gradient(135deg,var(--surface-card),var(--surface-raised));border:1px solid var(--border-subtle);border-radius:var(--r-card);padding:16px;margin-bottom:14px}
.bk-widget.in{background:var(--surface-input);margin:12px 0}.bk-widget h4{font-size:var(--fs-section);font-weight:600}.bk-widget .cap{color:var(--text-muted);font-size:12px;margin-top:2px}
.bk-widget .sub{font-size:12px;color:var(--text-muted);font-weight:600;margin-bottom:8px}
.ck-ico{width:16px;height:16px}
`;

  // ---------------------------------------------------------------- BACKUP SCHEDULER PAGE
  const schedules = [
    ['Production nightly', 'Daily', true, '11/3/2026 2:30 AM', true, 'running'],
    ['Staging hourly', 'CustomInterval', true, '11/2/2026 1:00 AM', false, ''],
    ['CartEx weekly archive', 'Weekly', true, '11/7/2026 3:00 AM', false, ''],
    ['Monthly cold copy', 'Monthly', false, '12/1/2026 4:15 AM', false, ''],
    ['Pre-migration snapshot', 'Once', false, '—', false, ''],
  ];
  const schedList = schedules.map(([n, f, on, next, sl, st]) => `<div class="list-item ${sl ? 'sel' : ''}"><div class="grow"><div class="row between"><span class="t ellipsis">${n}</span>${on ? badge('ON', 'success') : ''}</div><div class="sec" style="font-size:12px;margin-top:3px">${f}</div><div class="inf" style="font-size:11px;margin-top:2px">Next: ${next}${st ? ` <span class="bk-live"><i class="dot live blue"></i>running</span>` : ''}</div></div>${iconBtn('folder-open', 'Open saved backups folder', '', 'data-toast="info" data-title="Explorer" data-msg="D:\\Backups\\myproject\\db"')}</div>`).join('');

  const nextRuns = [
    ['Staging hourly', 'in 18 min', '1:00 AM', 8, 'info'],
    ['Production nightly', 'in 1 h 48 min', '2:30 AM', 30, ''],
    ['CartEx weekly archive', 'in 4 d 2 h', 'Sat 3:00 AM', 100, ''],
    ['Monthly cold copy', 'disabled', 'Dec 1, 4:15 AM', 100, 'muted'],
  ].map(([n, c, t, p, k]) => `<div class="bk-tl ${k}"><span class="bk-tl-dot"></span><div class="grow"><div class="ellipsis" style="font-weight:600">${n}</div><div class="muted mono" style="font-size:11px">${t}</div></div><span class="badge ${k === 'info' ? 'accent' : k === 'muted' ? '' : 'info'}">${ic('clock', 'ic-sm')}${c}</span></div>`).join('');

  const stepBtn = (n, label, tab, cls = '') => `<button type="button" class="tab bk-step ${cls}" data-tab="${tab}">${n} ${label}</button>`;

  const readiness = [
    ['Pass', 'Database', 'myproject_prod', 'Database is selected.'],
    ['Pass', 'Database reachability', 'Connection established successfully.', 'Server and credentials look valid.'],
    ['Pass', 'SSH tunnel', 'Tunnel path is usable (ssh deploy@203.0.113.24:22).', 'SSH path is ready.'],
    ['Pass', 'Database validity', 'Database exists on server.', 'Ready for backup.'],
    ['Pass', 'Output folder', 'Writable folder: D:\\Backups\\myproject\\db', 'Free space 182.4 GB.'],
    ['Pass', 'Validation artifact source', 'Backup mode provides local artifact for restore validation.', 'Validation import can run after backup.'],
    ['Fail', 'Tool availability', 'mysqldump not found on this machine.', 'Install MySQL client tools or switch backup mode.'],
  ].map(([s, t, d, h]) => `<tr><td><b class="${s === 'Pass' ? 'ok' : 'err'}">${s === 'Pass' ? ic('check') : ic('x')} ${s}</b></td><td>${t}</td><td class="muted">${d}</td><td class="muted">${h}</td></tr>`).join('');

  const runLog = [
    ['i', "Starting backup 'Production nightly' (myproject_prod) …"],
    ['ok', 'VPN ready: office-prod'],
    ['i', "Connecting to database 'myproject_prod' …"],
    ['i', 'Analyzing database structure (38 tables) …'],
    ['i', 'Backing up table users (1/38) …'],
    ['ok', 'Finished table users.'],
    ['i', 'Backing up table orders (2/38) …'],
    ['ok', 'Finished table orders.'],
    ['i', 'Backing up table order_items (3/38) …'],
    ['w', 'Table audit_log is large (2,914,220 rows) — using chunked reads.'],
    ['ok', 'Finished table order_items.'],
    ['i', 'Backing up table sessions (4/38) …'],
    ['ok', 'Finished table sessions.'],
    ['ok', 'Backup completed (412.8 MB) → D:\\Backups\\myproject\\db\\myproject_prod_26_11_02_02_30.sql.zip'],
    ['ok', 'Health check passed.'],
    ['i', 'Running localhost restore validation …'],
    ['ok', 'Localhost validation test passed.'],
  ];

  const artRows = [
    ['26_11_02_02_30', 'myproject_prod', 'myproject_prod_26_11_02_02_30_verify.sql.zip', 'ZIP', true, '412.8 MB', '11/2/2026 2:41 AM'],
    ['26_11_01_02_30', 'myproject_prod', 'myproject_prod_26_11_01_02_30_verify.sql.zip', 'ZIP', true, '409.1 MB', '11/1/2026 2:40 AM'],
    ['26_10_31_02_30', 'myproject_prod', 'myproject_prod_26_10_31_02_30.sql.zip', 'ZIP', false, '407.6 MB', '10/31/2026 2:39 AM'],
  ];
  const miniArt = artRows.map(([c, d, f, t, v, s, m]) => `<tr><td class="mono muted">${c}</td><td class="mono" title="D:\\Backups\\myproject\\db\\${f}">${f}</td><td>${t}</td><td>${tick(v)}</td><td class="num mono">${s}</td></tr>`).join('');

  const bkCss = `${sharedCss}
.bk-body{display:grid;grid-template-columns:minmax(250px,320px) minmax(0,1fr);gap:20px;align-items:start}
.bk-left{display:flex;flex-direction:column;gap:14px;position:sticky;top:0}
.bk-icon{width:48px;height:48px;border-radius:14px;background:var(--info-surface);color:var(--info);display:grid;place-items:center;flex:none}
.bk-toolbar{display:flex;align-items:center;gap:2px;padding:4px;background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:12px;flex-wrap:wrap}
.bk-toolbar .btn{background:transparent;border-color:transparent;color:var(--text-secondary);min-width:0}.bk-toolbar .btn:hover{background:var(--surface-hover);color:var(--text-primary)}
.bk-toolbar .sep{width:1px;height:20px;background:var(--border-subtle);margin:0 6px}
.bk-count{background:var(--surface-input);border-radius:6px;padding:2px 8px;color:var(--success);font-weight:700;margin-left:10px}
.bk-live{display:inline-flex;align-items:center;gap:5px;margin-left:6px;color:var(--accent)}
.bk-listtools{display:flex;padding:2px;background:var(--surface-input);border:1px solid var(--border-subtle);border-radius:10px}
.bk-listtools .btn{background:none;border-color:transparent;width:34px;height:34px}
.bk-listbox .list-item{border:1px solid transparent;border-radius:10px;margin-bottom:4px;border-bottom:1px solid transparent}
.bk-listbox .list-item.sel{border-color:rgba(76,141,255,.4)}
.bk-tl{display:flex;align-items:center;gap:10px;padding:8px 0;border-bottom:1px solid var(--border-subtle)}.bk-tl:last-child{border:0}
.bk-tl-dot{width:10px;height:10px;border-radius:50%;border:2px solid var(--info);flex:none}.bk-tl.info .bk-tl-dot{background:var(--accent);box-shadow:0 0 10px var(--accent)}.bk-tl.muted .bk-tl-dot{border-color:var(--border-strong)}.bk-tl.muted{opacity:.55}
.bk-editor{padding:18px}
.bk-steps{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:8px;margin-top:12px;border:0}
.bk-step{height:38px;border:1px solid var(--border-subtle);border-radius:var(--r-btn);background:var(--surface-input);color:var(--text-primary);justify-content:center;padding:0 6px;font-size:12px}
.bk-step:hover{border-color:var(--accent);background:var(--surface-input);color:var(--text-primary)}
.bk-step.done{background:var(--success-surface);border-color:var(--success)}
.bk-step.tab.active{background:var(--accent);border-color:var(--accent);color:var(--text-inverse);border-bottom-color:var(--accent)}
.bk-grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px 20px}
.bk-grid4{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px 8px;align-items:end}
.bk-chips{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:4px}
.bk-chips label{cursor:pointer}.bk-chips input{position:absolute;opacity:0}
.bk-chips span{display:flex;justify-content:center;padding:6px 12px;border-radius:18px;background:var(--surface-input);border:1px solid var(--border-subtle);color:var(--text-secondary);font-size:12px}
.bk-chips input:checked+span{background:var(--success);color:var(--text-inverse);border-color:var(--success)}.bk-chips label:hover span{border-color:var(--accent)}
.bk-num{display:inline-flex;align-items:center;border:1px solid var(--border-subtle);border-radius:var(--r-input);background:var(--surface-input);height:36px;overflow:hidden}
.bk-num button{width:30px;height:100%;border:0;background:var(--surface-raised);color:var(--text-secondary)}.bk-num input{width:64px;text-align:center;border:0;background:none;outline:0;height:100%}
.bk-cond{position:relative}.bk-cond>.badge{position:absolute;top:10px;right:12px}
.bk-foot{display:flex;align-items:center;gap:8px;margin-top:14px;padding-top:14px;border-top:1px solid var(--border-subtle);flex-wrap:wrap}
.bk-foot .btn.unsaved{background:var(--warning);border-color:var(--warning);color:var(--text-inverse);min-width:132px}
.bk-sum{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:10px 24px}.bk-sum dt{color:var(--text-muted);font-size:11px;text-transform:uppercase;letter-spacing:.06em}.bk-sum dd{margin:2px 0 0;font-weight:600;overflow-wrap:anywhere}
.bk-health{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px}.bk-health .card{padding:12px}
.bk-health .hk{display:flex;align-items:center;gap:8px;font-weight:700}.bk-health small{display:block;color:var(--text-muted);margin-top:4px;font-size:11px}
.bk-vpn{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px 12px;border-radius:10px;background:rgba(123,97,255,.1);border:1px solid rgba(123,97,255,.3)}
.bk-pill{display:inline-flex;gap:8px;align-items:center}
@media(max-width:1100px){.bk-body{grid-template-columns:minmax(230px,260px) minmax(0,1fr);gap:14px}.bk-steps .bk-step{font-size:11px}}
`;

  const backupBody = `
<div class="page-head">
  <div class="row" style="gap:14px"><div class="bk-icon">${ic('calendar', 'ic-xl')}</div><div><div class="page-title">Backup Scheduler</div><div class="page-sub">Short wizard, optional OpenVPN per plan, and import/export between machines.</div></div></div>
  <div class="r"><div class="bk-toolbar">
    ${btn('upload', 'Export plan', 'compact', 'title="Export selected backup plan + linked connections as JSON" data-toast="success" data-title="Plan exported" data-msg="production-nightly.gdbackup.json"').replace('<svg class="ic ">', '<svg class="ic inf">')}
    ${btn('download', 'Import plan', 'compact', 'title="Import a backup plan JSON from another GitDeploy machine" data-toast="info" data-title="Import plan" data-msg="Choose a .gdbackup.json file"').replace('<svg class="ic ">', '<svg class="ic ok">')}
    <span class="sep"></span>
    ${lnk('history', 'History', 'backup-history-window.html', 'compact', 'title="Backup history"')}
    ${lnk('list', 'Active tasks (2)', 'backup-tasks-window.html', 'compact', '').replace('<svg class="ic ">', '<svg class="ic accent">')}
    ${lnk('shield', 'Integrity', 'backup-integrity-sampling-window.html', 'compact', 'title="Integrity sampling"')}
    <span class="sep"></span>
    ${iconBtn('external', 'Pop-out', 'ghost', 'data-toast="info" data-title="Pop-out" data-msg="Opened in a separate window"')}
  </div></div>
</div>

<div class="banner warn" style="margin-bottom:16px">${ic('alert')}<div class="grow sec" style="font-size:12px">Localhost is not connected (127.0.0.1:3306 / root / empty password).</div>${iconBtn('x', 'Dismiss warning', 'ghost compact', 'data-toast="info" data-title="Dismissed"')}</div>

<div class="bk-body">
  <div class="bk-left">
    ${card('', `
      <div class="row between" style="margin-bottom:10px"><div class="row"><b style="font-size:16px">Schedules</b><span class="bk-count">5</span></div>
        <div class="bk-listtools">${iconBtn('plus', 'New schedule', '', 'data-toast="success" data-title="New schedule" data-msg="Untitled schedule created"').replace('class="ic "', 'class="ic ok"').replace('<svg class="ic ">', '<svg class="ic ok">')}${iconBtn('copy', 'Duplicate schedule', '', 'data-toast="info" data-title="Duplicated" data-msg="Production nightly (copy)"').replace('<svg class="ic ">', '<svg class="ic accent">')}${iconBtn('trash', 'Delete schedule', '', 'data-confirm="Delete schedule \'Production nightly\'? Stored backups are kept on disk." data-confirm-title="Delete schedule"').replace('<svg class="ic ">', '<svg class="ic err">')}</div></div>
      <div class="bk-listbox" data-selectable>${schedList}</div>`, { cls: '' })}
    ${card('Next runs', `<div>${nextRuns}</div>`, { icon: 'clock', right: badge('timeline', 'info'), cls: 'dense' })}
  </div>

  <div class="card bk-editor" data-tabscope>
    <div class="bk-widget" style="margin-bottom:12px;padding:14px 16px">
      <div class="row between"><h4>Step 6 of 6</h4>${badge(`<i class="dot live blue"></i>Backup running`, 'accent')}</div>
      <div class="cap">Short steps: basics → local DB → strategy → VPN → readiness → run.</div>
      <div class="tabs bk-steps">${stepBtn(1, 'Basics', 'bkS1', 'done')}${stepBtn(2, 'Local DB', 'bkS2', 'done')}${stepBtn(3, 'Strategy', 'bkS3', 'done')}${stepBtn(4, 'VPN', 'bkS4', 'done')}${stepBtn(5, 'Ready', 'bkS5', 'done')}${stepBtn(6, 'Run', 'bkS6', 'active')}</div>
    </div>

    <div class="tabpanel" id="bkS1"><div class="bk-widget"><h4>Basics</h4><div class="cap">Name, connection profile, and remote database.</div>
      <div class="bk-grid2" style="margin-top:12px">
        <div class="stack sm">${field('Schedule name', inp('', 'Production nightly'))}${chk('Enabled', true)}</div>
        <div class="stack sm">
          ${field('Connection profile', `<div class="row"><select class="select"><option selected>prod-web-01 (deploy@203.0.113.24:3306)</option><option>staging-db (app@10.0.4.12:3306)</option><option>Localhost (root@127.0.0.1:3306)</option></select>${lnk('plus', '', 'connection-manager-window.html', 'primary icon', 'title="Create/manage profiles"')}</div>`)}
          ${field('Database name', `<div class="row"><select class="select"><option selected>myproject_prod</option><option>myproject_staging</option><option>information_schema</option><option>mysql</option></select>${btn('refresh', 'Refresh', 'compact', 'data-working="Loading…" data-done="Databases loaded" data-done-msg="4 databases found"')}</div>`)}
          ${resultBox('', 'Connected · 4 databases found. Selected database exists on server.')}
        </div>
      </div></div></div>

    <div class="tabpanel" id="bkS2"><div class="bk-widget"><h4>Local DB</h4><div class="cap">Optional: after backup, import into a temporary localhost database to verify recoverability.</div>
      <div style="margin-top:10px">${chk('Enable localhost restore validation (warning-only if it fails)', true)}</div>
      <div class="bk-grid4" style="margin-top:10px">
        ${field('IP / Host', `<input class="input" title="Localhost only" value="127.0.0.1">`)}${field('Port', `<input class="input mono" title="Example: 3306" value="3306">`)}${field('Database name', `<input class="input mono" title="Auto-created if missing" value="myproject_verify">`)}${field('Username', `<input class="input" title="Database username" value="root">`)}
      </div>
      <div class="bk-grid4" style="margin-top:8px">
        ${field('Password', `<input class="input" title="Database password" value="">`)}${field('Charset', `<input class="input mono" title="Use auto for server-compatible utf8mb4" value="auto">`)}${field('Collation', `<input class="input mono" title="Use auto for server-compatible collation" value="auto">`)}
        ${btn('database', 'Test + create DB', '', 'data-working="Testing…" data-done="Localhost validation test passed." data-done-msg="myproject_verify ready"')}
      </div>
      ${resultBox('err', 'Localhost is not connected: Unable to connect to any of the specified MySQL hosts (127.0.0.1:3306).')}
    </div></div>

    <div class="tabpanel" id="bkS3"><div class="bk-widget"><h4>Strategy</h4><div class="cap">Timing, backup mode, output path, and retention.</div>
      <div class="bk-widget in"><div class="sub">Schedule timing</div>
        <div class="bk-grid2">${field('Frequency', sel(['Once', ['Daily', 1], 'Weekly', 'Monthly', 'CustomInterval']))}${field('Run time', `<div class="input-icon">${ic('clock')}<input class="input mono" value="02:30"></div>`)}</div></div>
      <div class="bk-widget in bk-cond">${badge('Frequency = Weekly', '')}<div class="sub">Days of week</div>
        <div class="bk-chips">${['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map((d, i) => `<label><input type="checkbox" ${[0, 2, 5].includes(i) ? 'checked' : ''}><span>${d}</span></label>`).join('')}</div></div>
      <div class="bk-widget in bk-cond">${badge('Frequency = Monthly', '')}<div class="sub">Day of month</div>
        <div class="bk-num"><button type="button">−</button><input value="1"><button type="button">+</button></div></div>
      <div class="bk-widget in bk-cond">${badge('Frequency = CustomInterval', '')}<div class="sub">Custom interval</div>
        <div class="sec" style="font-size:11px;margin-bottom:8px">Use hour and minute inputs; value is converted to total minutes in background.</div>
        <div class="row" style="align-items:flex-end"><div class="field" style="width:90px"><label>Hour</label><input class="input mono" value="1"></div><b style="padding-bottom:8px">:</b><div class="field" style="width:90px"><label>Minute</label><input class="input mono" value="0"></div></div>
        <div class="inf" style="font-size:11px;margin-top:8px">Applied interval: 60 min (1h 0m)</div></div>
      <div class="bk-widget in"><div class="sub">Backup strategy</div><div class="sec" style="font-size:11px;margin-bottom:8px">Choose how backup should be produced.</div>
        ${field('Backup mode', sel(['Standard (safe)', 'Fast (bulk)', 'External (mysqldump)', 'SSH Remote mysqldump (stream)', ['SSH Remote file build (server-side)', 1]]))}
        <div class="bk-widget" style="margin:12px 0 0;padding:12px"><div class="sub">Remote file mode options</div>
          <div class="stack sm">
            ${field('Remote output directory (Linux)', inp('/var/backups/gitdeploypro', '/var/backups/gitdeploypro', 'text', 'mono'), '', 'Example: /var/backups/gitdeploypro')}
            ${field('Download policy', sel(['Manual (keep on server, save reference)', ['Auto download via SFTP after build', 1]]), '', 'Manual (default): only save remote artifact reference. Auto: download artifact after remote build.')}
            ${chk('After verified download, safely delete ONLY generated remote artifact file', true)}
            <div class="hint">Safety mode: no wildcard, no directory delete, no recursive delete. Only exact artifact file path is allowed.</div>
          </div></div>
      </div>
      <div class="bk-widget in"><div class="sub">Output &amp; retention</div>
        ${field('Local output directory', `<div class="row"><input class="input mono" value="D:\\Backups\\myproject\\db">${btn('', 'Browse', '', 'data-toast="info" data-title="Browse" data-msg="Folder picker"')}${btn('', 'Open', '', '')}</div>`, '', 'Used for local artifacts and optional auto-download destination.')}
        <div class="inf" style="font-size:12px;margin:8px 0">Stored backups: 14/14</div>
        <div class="row wrap" style="gap:20px">${field('Retention copies', `<div class="bk-num"><button type="button">−</button><input value="14"><button type="button">+</button></div>`)}${chk('Compress backup', true)}${chk('Encrypt backup at rest (Windows user scope)', false)}<div style="min-width:180px">${sel(['ZIP (.zip)', 'Tarball (.tar.gz)'])}</div></div>
      </div>
    </div></div>

    <div class="tabpanel" id="bkS4"><div class="bk-widget"><h4>VPN</h4><div class="cap">Optional OpenVPN for this schedule. GitDeploy connects before backup and disconnects after only if it opened the tunnel.</div>
      <div style="margin:10px 0">${chk('This backup requires VPN', true)}</div>
      <div class="bk-grid2">${field('OpenVPN client', sel([['OpenVPN Connect', 1], 'OpenVPN GUI (Community)']))}${field('Profile', `<div class="row"><select class="select"><option selected>office-prod</option><option>office-staging</option><option>client-bourse</option></select>${iconBtn('refresh', 'Refresh VPN profiles', '', 'data-toast="info" data-title="VPN" data-msg="3 profiles found"')}</div>`)}</div>
      <div class="hint" style="margin-top:8px">Configure OpenVPN paths in Settings → VPN if profiles do not appear.</div>
      <div class="bk-vpn" style="margin-top:12px">${badge(ic('shield', 'ic-sm') + 'VPN scope', 'violet')}<span class="mono">OpenVpnConnect · office-prod</span><span class="grow"></span><span class="ok" style="font-size:12px"><i class="dot live"></i> tunnel up · opened by GitDeploy</span></div>
    </div></div>

    <div class="tabpanel" id="bkS5"><div class="bk-widget"><div class="row between"><div><h4>Readiness</h4><div class="cap">Checks must pass before backup starts.</div></div>${btn('refresh', 'Re-check now', '', 'data-working="Checking…" data-done="Readiness check completed" data-done-msg="1 blocking issue"')}</div>
      <div class="grid-wrap" style="margin-top:12px;border:1px solid var(--border-subtle);border-radius:10px;max-height:340px"><table class="dg"><thead><tr><th style="width:90px">Status</th><th>Check</th><th>Details</th><th>Fix hint</th></tr></thead><tbody>${readiness}</tbody></table></div>
      <div class="bk-note err" style="margin-top:10px">Readiness check completed with blocking issues.</div>
    </div></div>

    <div class="tabpanel active" id="bkS6"><div class="bk-widget"><h4>Confirm</h4><div class="cap">Review configuration, then run or pause.</div>
      <dl class="bk-sum" style="margin-top:12px">
        <div><dt>Backup mode</dt><dd>Standard</dd></div><div><dt>Frequency</dt><dd>Daily</dd></div><div><dt>Run time</dt><dd class="mono">02:30</dd></div><div><dt>Retention</dt><dd>14 copies</dd></div>
        <div style="grid-column:1/-1"><dt>Local output</dt><dd class="mono">D:\\Backups\\myproject\\db</dd></div>
        <div><dt>Remote path</dt><dd class="mono">/var/backups/gitdeploypro</dd></div><div><dt>Remote download policy</dt><dd>AutoDownload</dd></div><div><dt>Remote cleanup</dt><dd>Enabled: True</dd></div>
      </dl>
      <div class="bk-widget in" style="padding:10px 12px"><div class="sub" style="margin:0 0 4px">Localhost validation</div><div class="mono sec" style="font-size:12px">Host: 127.0.0.1 &nbsp;|&nbsp; Port: 3306 &nbsp;|&nbsp; Database: myproject_verify<br>Charset: auto &nbsp;|&nbsp; Collation: auto</div></div>
      <div class="bk-vpn">${badge(ic('shield', 'ic-sm') + 'VPN scope', 'violet')}<span class="mono">OpenVpnConnect · office-prod</span><span class="grow"></span><span class="ok" style="font-size:12px"><i class="dot live"></i> connected for this run only</span></div>

      <div style="margin-top:14px">${L.worker({ icon: 'database', name: 'Production nightly · myproject_prod', desc: 'Backing up table orders (14/38) …', pct: 36, speed: 0.8, stats: ['<b>14/38</b> tables', '<b>2,418,900</b> rows', '<b>96 MB/s</b>', `elapsed <b data-elapsed="187">00:03:07</b>`, `${badge('VPN', 'violet')}`] })}</div>
      <div class="row between" style="margin:12px 0 6px"><span class="eyebrow">Run log</span><span class="muted" style="font-size:11px">live</span></div>
      ${stream(runLog, { cls: 'log', interval: 1100, style: 'height:200px' })}
      <div class="row between" style="margin-top:6px"><span class="sec" style="font-size:12px">14/38 tables</span><span class="muted" style="font-size:12px">Backing up table orders (14/38) …</span></div>

      <div class="divider" style="margin:14px 0"></div>
      <div class="sub" style="font-weight:600">Last health report</div>
      <div class="bk-health">
        <div class="card"><div class="hk ok">${ic('check-circle')}Health passed</div><small>Structure validated.</small></div>
        <div class="card"><div class="hk ok">${ic('check-circle')}Restore validation</div><small>Imported 38 tables into myproject_verify.</small></div>
        <div class="card"><div class="hk warn">${ic('alert')}Integrity sample</div><small>2 tables differ by &gt; 0.1 % rows (warning only).</small></div>
        <div class="card"><div class="hk">${ic('lock')}Encrypted</div><small>No · ZIP · 412.8 MB</small></div>
      </div>

      <div class="divider" style="margin:14px 0"></div>
      <div class="sub" style="font-weight:600">Project backup artifacts</div>
      <div class="mono sec" style="font-size:12px">Path: D:\\Backups\\myproject\\db</div>
      <div class="muted" style="font-size:12px;margin:2px 0 8px">3 backup file(s) found.</div>
      <div class="grid-wrap" style="border:1px solid var(--border-subtle);border-radius:10px;max-height:150px"><table class="dg"><thead><tr><th>Captured</th><th>File</th><th>Type</th><th>Verify</th><th class="num">Size</th></tr></thead><tbody>${miniArt}</tbody></table></div>
      <div style="margin-top:10px">${lnk('archive', 'Open backup files window', 'backup-artifacts-window.html', '')}</div>

      <div class="row wrap" style="margin-top:14px;gap:8px">
        ${btn('pause', 'Pause', '', 'data-confirm="Pause the running backup? It can be resumed at any time." data-confirm-title="Pause backup"')}
        ${btn('play', 'Run now', 'primary', 'data-working="Starting…" data-done="Backup started" data-done-msg="Production nightly · myproject_prod"')}
        ${lnk('', 'Backup progress window', 'backup-progress-window.html', 'ghost compact')}
      </div>
    </div></div>

    <div class="bk-foot">
      ${btn('arrow-left', 'Previous', 'ghost')}
      <span class="muted grow" style="font-size:12px">Confirm summary and run backup.</span>
      ${btn('', '⚠ Save changes', 'unsaved ghost', 'data-toast="success" data-title="Schedule saved" data-msg="Production nightly"')}
      ${btn('', 'Next →', 'primary disabled')}
    </div>
  </div>
</div>`;

  L.page({ slug: 'backup', title: 'Backup Scheduler', nav: 'backup', group: 'Pages', css: bkCss, body: backupBody });

  // ---------------------------------------------------------------- BACKUP HISTORY WINDOW
  const hist = [
    ['11/2/2026 2:30:04 AM', 'Production nightly', 'myproject_prod', true, true, true, true, 'ok', 'Manual run (412.8 MB) · ZIP verified · Health passed.'],
    ['11/2/2026 12:00:02 AM', 'Staging hourly', 'myproject_staging', true, true, true, false, 'ok', 'Scheduled (38.2 MB) · Health passed · Validation warning: 2 tables differ.'],
    ['11/1/2026 11:00:01 PM', 'Staging hourly', 'myproject_staging', true, true, true, null, 'ok', 'Scheduled (38.1 MB) · Health passed.'],
    ['11/1/2026 2:30:03 AM', 'Production nightly', 'myproject_prod', true, true, true, true, 'ok', 'Scheduled (409.1 MB) · ZIP verified · Health passed.'],
    ['10/31/2026 2:30:02 AM', 'Production nightly', 'myproject_prod', true, true, false, null, 'ok', 'Scheduled (407.6 MB) · Health FAILED: Archive has no readable backup entry.'],
    ['10/31/2026 1:00:00 AM', 'Staging hourly', 'myproject_staging', false, false, null, null, 'none', 'Backup failed: Unable to connect to any of the specified MySQL hosts.'],
    ['10/30/2026 3:00:05 AM', 'CartEx weekly archive', 'cartex_shop', true, true, true, true, 'ok', 'Remote artifact downloaded (1.6 GB) · Remote cleanup done.'],
    ['10/30/2026 2:30:00 AM', 'Production nightly', 'myproject_prod', false, false, null, null, 'none', 'Canceled by user.'],
    ['10/29/2026 2:30:02 AM', 'Production nightly', 'myproject_prod', true, false, null, null, 'none', 'Remote artifact kept on server /var/backups/gitdeploypro/myproject_prod_26_10_29_02_30.sql.gz (manual policy).'],
  ];
  const histRows = hist.map(([s, sch, db, conn, file, health, ver, imp, msg], i) => `<tr ${i === 0 ? 'class="sel"' : ''}><td class="mono">${s}</td><td>${sch}</td><td class="mono">${db}</td><td>${tick(conn)}</td><td>${tick(file)}</td><td>${tick(health)}</td><td>${ver === null ? tick(null) : ver ? tick(true) : `<span class="warn" title="Validation warning">${ic('alert')}</span>`}</td><td>${btn('', 'Import/Validate', 'compact' + (imp === 'ok' ? '' : ' disabled'), imp === 'ok' ? 'data-toast="info" data-title="Import/Validate" data-msg="Opening integrity sampling…"' : 'disabled')}</td><td class="wrap muted" title="${msg}" style="max-width:420px;min-width:260px"><span class="ellipsis" style="display:block;max-width:420px">${msg}</span></td></tr>`).join('');
  L.window({
    slug: 'backup-history-window', title: 'Backup history', icon: 'history', w: 980, h: 460, group: 'Windows',
    css: sharedCss + '.bh-wrap{border:1px solid var(--border-subtle);border-radius:10px;height:100%}',
    body: `<div class="stack" style="height:100%"><div class="row between"><div class="section-title" style="margin:0">Backup history ${badge('9', 'accent')}</div>${btn('trash', 'Clear backup history', 'danger', 'data-confirm="Clear the whole backup history? Backup files on disk are not deleted." data-confirm-title="Clear backup history"')}</div>
      <div class="card flush grow" style="min-height:0"><div class="grid-wrap bh-wrap" style="height:100%;border:0"><table class="dg" data-select><thead><tr><th>Started</th><th>Schedule</th><th>Database</th><th>Conn</th><th>File</th><th>Health</th><th>Verify</th><th>Import</th><th style="width:100%">Details</th></tr></thead><tbody>${histRows}</tbody></table></div></div></div>`,
    footer: `<span class="l">Click a row to select · hover Details for the full message</span><button class="btn" data-close>Close</button>`.replace(' data-close', ''),
  });

  // ---------------------------------------------------------------- BACKUP ARTIFACTS WINDOW
  const arts = [
    ['11/2/2026 2:30 AM', 'myproject_prod', 'myproject_prod_26_11_02_02_30_verify.sql.zip', 'ZIP', true, '412.8 MB', '11/2/2026 2:41:12 AM', 'e3b0c44298fc1c149afbf4c8996fb924'],
    ['11/1/2026 2:30 AM', 'myproject_prod', 'myproject_prod_26_11_01_02_30_verify.sql.zip', 'ZIP', true, '409.1 MB', '11/1/2026 2:40:55 AM', '9f86d081884c7d659a2feaa0c55ad015'],
    ['10/31/2026 2:30 AM', 'myproject_prod', 'myproject_prod_26_10_31_02_30.sql.zip', 'ZIP', false, '407.6 MB', '10/31/2026 2:39:48 AM', '2c26b46b68ffc68ff99b453c1d304134'],
    ['10/30/2026 3:00 AM', 'cartex_shop', 'cartex_shop_26_10_30_03_00_verify.sql.tar.gz', 'TAR.GZ', true, '1.62 GB', '10/30/2026 3:28:03 AM', 'fcde2b2edba56bf408601fb721fe9b5c'],
    ['10/29/2026 2:30 AM', 'myproject_prod', 'myproject_prod_26_10_29_02_30.sql.gz.protected', 'ENCRYPTED', false, '188.4 MB', '10/29/2026 2:36:20 AM', '4e07408562bedb8b60ce05c1decfe3ad'],
    ['10/28/2026 11:00 PM', 'myproject_staging', 'myproject_staging_26_10_28_23_00.sql', 'SQL', false, '96.0 MB', '10/28/2026 11:00:44 PM', '6b86b273ff34fce19d6b804eff5a3f57'],
  ];
  const artTr = arts.map(([c, d, f, t, v, s, m], i) => `<tr ${i === 0 ? 'class="sel"' : ''}><td class="mono">${c}</td><td class="mono">${d}</td><td title="D:\\Backups\\myproject\\db\\${f}"><span class="row" style="gap:6px">${ic('archive', 'ic-sm accent')}<span class="mono">${f}</span></span></td><td>${badge(t, t === 'ENCRYPTED' ? 'violet' : 'info')}</td><td>${tick(v)}</td><td class="num mono">${s}</td><td class="mono">${m}</td></tr>`).join('');
  L.window({
    slug: 'backup-artifacts-window', title: 'Project backup artifacts', icon: 'archive', w: 1140, h: 560, group: 'Windows',
    css: sharedCss,
    body: `<div class="stack" style="height:100%">
      <div class="row between wrap"><div class="section-title" style="margin:0">Project backup artifacts</div><div class="row">${btn('refresh', 'Refresh', '', 'data-working="Scanning…" data-done="Artifacts refreshed" data-done-msg="6 backup file(s) found."')}${btn('folder-open', 'Open File Explorer', '', 'data-toast="info" data-title="Explorer" data-msg="D:\\Backups\\myproject\\db"')}</div></div>
      <div><div class="mono sec" style="font-size:12px">Path: D:\\Backups\\myproject\\db</div><div class="muted" style="font-size:12px;margin-top:2px">6 backup file(s) found.</div></div>
      <div class="card flush grow" style="min-height:0"><div class="grid-wrap" style="height:100%"><table class="dg" data-select><thead><tr><th>Captured time</th><th>Database</th><th style="width:100%">File</th><th>Type</th><th>Verify</th><th class="num">Size</th><th>Last modified</th></tr></thead><tbody>${artTr}</tbody></table></div></div>
      <div class="card dense"><div class="row wrap" style="gap:10px 16px"><span class="eyebrow">Selected</span><span class="mono">myproject_prod_26_11_02_02_30_verify.sql.zip</span><span class="mono muted" title="SHA-256">sha256 e3b0c442…52b855</span><span class="grow"></span>${lnk('shield', 'Validate', 'backup-integrity-sampling-window.html', 'compact')}${btn('undo', 'Restore', 'compact', 'data-confirm="Restore this artifact into the active connection? Existing data in the target database may be overwritten." data-confirm-title="Restore backup"')}${btn('folder-open', 'Show in folder', 'compact ghost', 'data-toast="info" data-title="Explorer"')}</div></div>
    </div>`,
    footer: `<span class="l">Hover the File column for the full path</span><button class="btn">Close</button>`,
  });

  // ---------------------------------------------------------------- BACKUP TASKS WINDOW
  const run = (sched, db, mode, started, stage, pct, speed, sum, cur, rows, stopOk = true) => `<tr data-worker class="running"><td>${sched}</td><td class="mono">${db}</td><td>${mode}</td><td class="mono">${started}</td><td>${stage}</td><td style="min-width:160px"><div class="progress busy"><i data-progress="${pct}" data-speed="${speed}" style="width:${pct}%"></i></div><div style="font-size:11px;margin-top:3px"><span data-pct>${pct}%</span> · ${sum}</div><div class="muted mono" style="font-size:10px">${rows}</div></td><td class="mono">${cur}</td><td class="mono muted">${rows}</td><td>${btn('stop', 'Stop', 'compact danger' + (stopOk ? '' : ' disabled'), stopOk ? 'data-confirm="Stop this backup task?" data-confirm-title="Stop backup"' : '')}</td></tr>`;
  const recent = [
    ['11/2/2026 2:41:12 AM', 'Production nightly', 'Standard', 'Backup completed (412.8 MB)', ''],
    ['11/2/2026 12:00:09 AM', 'Staging hourly', 'Fast', 'Backup completed (38.2 MB)', ''],
    ['11/1/2026 1:00:03 AM', 'Staging hourly', 'Fast', 'Backup failed: Unable to connect to any of the specified MySQL hosts.', 'err'],
    ['10/31/2026 2:30:44 AM', 'Production nightly', 'Standard', 'Canceled by user.', 'warn'],
    ['10/30/2026 3:28:03 AM', 'CartEx weekly archive', 'SshRemoteFileBuild', 'Remote artifact downloaded (1.62 GB)', ''],
  ].map(([w, s, m, r, k]) => `<tr><td class="mono">${w}</td><td>${s}</td><td>${m}</td><td class="${k}">${k === 'err' ? ic('x-circle') : k === 'warn' ? ic('alert') : ic('check-circle', 'ok')} ${r}</td></tr>`).join('');
  L.window({
    slug: 'backup-tasks-window', title: 'Active Backups', icon: 'list', w: 900, h: 560, group: 'Windows', css: sharedCss,
    body: `<div class="stack" style="height:100%;gap:14px">
      <div class="section-title" style="margin:0">Running tasks ${badge('<i class="dot live blue"></i>3 active', 'accent')}</div>
      <div class="card flush" style="flex:none"><div class="grid-wrap" style="max-height:230px"><table class="dg"><thead><tr><th>Schedule</th><th>Database</th><th>Mode</th><th>Started</th><th>Stage</th><th>Progress</th><th>Current table</th><th>Row progress</th><th>Actions</th></tr></thead><tbody>
        ${run('Production nightly', 'myproject_prod', 'Standard', '2:30:04 AM', 'Backing up table orders (14/38) …', 36, 0.8, '14/38 tables', 'orders', '1,204,330 / 1,284,903 rows')}
        ${run('CartEx weekly archive', 'cartex_shop', 'SshRemoteFileBuild', '3:00:05 AM', 'Remote artifact ready. Starting SFTP download …', 48, 0.6, '784.2 MB / 1.62 GB', '—', 'no rows')}
        <tr><td>Staging hourly</td><td class="mono">myproject_staging</td><td>Fast</td><td class="mono">1:00:00 AM</td><td class="warn">Paused — waiting to resume …</td><td style="min-width:160px"><div class="progress warning"><i style="width:61%"></i></div><div style="font-size:11px;margin-top:3px">61% · 23/38 tables</div></td><td class="mono">invoices</td><td class="mono muted">88,012 / 140,200 rows</td><td>${btn('play', 'Resume', 'compact', 'data-toast="info" data-title="Resumed"')}</td></tr>
        <tr class="dim"><td>Monthly cold copy</td><td class="mono">archive_2025</td><td>Standard</td><td class="mono">—</td><td>Queued — waiting for VPN slot</td><td><div class="progress"><i style="width:0"></i></div><div style="font-size:11px;margin-top:3px">0%</div></td><td class="mono">—</td><td class="mono muted">—</td><td>${btn('stop', 'Stop', 'compact danger', 'data-confirm="Remove from queue?"')}</td></tr>
      </tbody></table></div></div>
      <div class="section-title" style="margin:0">Recent activity</div>
      <div class="card flush grow" style="min-height:0"><div class="grid-wrap" style="height:100%"><table class="dg"><thead><tr><th>When</th><th>Schedule</th><th>Mode</th><th style="width:100%">Result</th></tr></thead><tbody>${recent}</tbody></table></div></div>
    </div>`,
    footer: `<span class="l">Auto-refresh every second</span><button class="btn">Close</button>`,
  });

  // ---------------------------------------------------------------- BACKUP PROGRESS WINDOW
  L.window({
    slug: 'backup-progress-window', title: 'Backup progress', icon: 'database', w: 520, h: 340, group: 'Windows', css: sharedCss,
    body: `<div class="stack" style="height:100%;gap:10px">
      <div class="row between"><div class="section-title" style="margin:0">Backup progress</div><span class="badge accent"><i class="dot live blue"></i><span>Standard (safe)</span></span></div>
      <div class="eyebrow">Overall progress</div>
      <div class="progress lg busy"><i data-progress="36" data-speed="0.8" style="width:36%"></i></div>
      <div class="row between"><span class="sec" style="font-size:12px">14/38 tables</span><span class="muted mono" style="font-size:11px">elapsed <b data-elapsed="187">00:03:07</b></span></div>
      <div class="row between" style="margin-top:2px"><div><div class="eyebrow">Current table</div><div class="mono" style="font-size:13px">orders (14/38)</div></div><span class="muted mono" style="font-size:12px">1,204,330 / 1,284,903 rows</span></div>
      ${stream([['i', 'Connecting to database \'myproject_prod\' …'], ['i', 'Analyzing database structure (38 tables) …'], ['i', 'Backing up table users (1/38) …'], ['ok', 'Finished table users.'], ['i', 'Backing up table orders (2/38) …'], ['ok', 'Finished table orders.'], ['i', 'Backing up table order_items (3/38) …'], ['ok', 'Finished table order_items.']], { cls: 'log', interval: 1000, style: 'flex:1;min-height:90px' })}
    </div>`,
    footer: `<span class="l">Waiting for backup … · Awaiting table …</span><button class="btn">Close</button>`,
  });

  // ---------------------------------------------------------------- BACKUP INTEGRITY SAMPLING WINDOW
  const tbls = [['orders', '1,284,903', '412.8 MB', true], ['order_items', '3,902,114', '361.2 MB', false], ['audit_log', '2,914,220', '288.9 MB', false], ['sessions', '582,009', '96.4 MB', false], ['users', '48,377', '24.1 MB', false], ['products', '12,904', '18.7 MB', false], ['invoices', '140,200', '14.0 MB', false], ['coupons', '3,114', '0.6 MB', false]]
    .map(([n, r, b, s]) => `<div class="list-item ${s ? 'sel' : ''}" style="padding:8px 12px"><div class="grow"><div class="mono" style="font-weight:600">${n}</div><div class="muted" style="font-size:11px">${r} rows · <span class="mono">${b}</span></div></div>${s ? '' : '<span class="muted" style="font-size:10px">click to load row</span>'}</div>`).join('');
  const rowKv = [['id', '1284903'], ['user_id', '48371'], ['status', 'paid'], ['total', '149.90'], ['currency', 'USD'], ['payment_ref', 'ch_3Q8xK2LkdIwHu7ix'], ['created_at', '2026-11-02 02:29:51'], ['updated_at', '2026-11-02 02:29:58'], ['notes', 'NULL']].map(([k, v]) => `<tr><td class="mono">${k}</td><td class="mono ${v === 'NULL' ? 'muted' : ''}">${v}</td></tr>`).join('');
  L.window({
    slug: 'backup-integrity-sampling-window', title: 'Backup integrity sampling', icon: 'shield', w: 1080, h: 640, group: 'Windows',
    css: sharedCss + '.is-split{display:grid;grid-template-columns:minmax(230px,300px) minmax(0,1fr);gap:14px;min-height:0;flex:1}.is-split>.card{min-height:0;display:flex;flex-direction:column}',
    body: `<div class="stack" style="height:100%;gap:12px">
      <div class="card dense"><div class="row top wrap" style="gap:14px"><div class="grow" style="min-width:260px"><div class="section-title" style="margin-bottom:4px">Backup integrity sampling</div>
        <div class="sec" style="font-size:12px">Schedule: Production nightly • Database: myproject_prod</div>
        <div class="sec" style="font-size:12px">Latest successful imported backup: 11/2/2026 2:41:12 AM</div>
        <div class="muted" style="font-size:12px">Captured at: 11/2/2026 2:43:30 AM</div>
        <div class="muted" style="font-size:12px;margin-top:4px">Snapshot generated from latest localhost validation import.</div></div>
        <div style="min-width:260px;flex:1"><div class="sec" style="font-size:12px">Latest available backup file: <span class="mono">myproject_prod_26_11_02_02_30_verify.sql.zip</span></div><div class="muted mono" style="font-size:11px;overflow-wrap:anywhere">D:\\Backups\\myproject\\db\\myproject_prod_26_11_02_02_30_verify.sql.zip</div></div>
        <div class="row">${btn('download', 'Import latest backup', 'primary', 'data-working="Importing..." data-done="Import/validate completed" data-done-msg="38 tables imported"')}${btn('refresh', 'Refresh', '', 'data-working="Refreshing…" data-done="Refreshed" data-done-msg="Refreshed from `myproject_verify` (8 table sample(s))."')}</div></div>
        <div style="margin-top:10px">${L.worker({ icon: 'download', name: 'Import / validate', desc: 'Importing backup into localhost database...', pct: 42, speed: 1.1, loop: true, controls: false, stats: ['<b>ValidationImport</b>', 'elapsed <b data-elapsed="41">00:00:41</b>'] })}</div>
        <div class="muted" style="font-size:12px;margin-top:8px">${ic('info', 'ic-sm')} Ready to import latest backup into configured localhost database.</div></div>
      <div class="is-split">
        <div class="card flush"><div class="pane-h">${ic('table')}All tables <span class="badge" style="margin-left:6px">8 tables</span></div><div class="grid-wrap" data-selectable style="flex:1">${tbls}</div></div>
        <div class="card"><div class="section-title" style="margin-bottom:2px">Latest row snapshot • orders</div>
          <div class="muted" style="font-size:12px">≈1,284,903 rows • 412.8 MB • <span class="mono">InnoDB</span></div><div class="muted mono" style="font-size:12px;margin-bottom:10px">Primary key: id</div>
          <div class="grid-wrap" style="border:1px solid var(--border-subtle);border-radius:10px;flex:1"><table class="dg"><thead><tr><th style="width:200px">Column</th><th>Value</th></tr></thead><tbody>${rowKv}</tbody></table></div>
          <div class="ok" style="font-size:12px;margin-top:8px">${ic('check-circle')} Latest row loaded from localhost.</div></div>
      </div></div>`,
    footer: `<button class="btn">Close</button>`,
  });

  // ---------------------------------------------------------------- DATABASE PAGE (Database Studio)
  const dbCss = `${sharedCss}
.db-wrap{display:flex;flex-direction:column;min-height:100%}
.db-state{padding:6px 12px;display:flex;align-items:center;gap:10px;background:#000;border-bottom:1px dashed var(--border-strong);font-size:11px}
.db-state .tabs{border:0}.db-state .tab{height:26px;font-size:12px}
.db-top{background:var(--surface-card);border-bottom:1px solid var(--border-subtle)}
.db-bar1{display:flex;align-items:center;gap:8px;padding:8px 12px;background:var(--surface-shell);flex-wrap:wrap}
.db-bar1 .status{flex:1;display:flex;justify-content:center;align-items:center;gap:8px;min-width:200px;color:var(--text-muted)}
.db-bar1 .status b{color:var(--accent)}
.db-bar2{display:flex;align-items:center;gap:10px;padding:8px 12px;flex-wrap:wrap}
.db-bar2 .sel1{width:220px}.db-bar2 .sel2{width:240px}
.db-main{padding:16px;flex:1;min-height:0}
.db-editor{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px;padding:6px}
.db-editor .lbl2{padding:4px 8px 6px;font-size:13px;font-weight:600;color:var(--text-muted)}.db-editor .lbl2 small{font-weight:400;font-size:11px;margin-left:8px}
.db-editor .code{height:150px;border-radius:6px;background:var(--surface-input)}
.db-actions{display:flex;align-items:center;gap:10px;margin:10px 0;flex-wrap:wrap}
.db-results{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:10px;overflow:hidden}
.db-results .grid-wrap{max-height:300px}
.db-two{display:grid;grid-template-columns:minmax(0,1.05fr) minmax(0,.95fr);gap:24px;margin-top:18px}
@media(max-width:1250px){.db-two{grid-template-columns:minmax(0,1fr)}}
.db-lbl{display:grid;grid-template-columns:130px minmax(0,1fr);align-items:center;gap:8px;margin-bottom:8px}.db-lbl>span{color:var(--text-secondary)}
.db-lbl .input,.db-lbl .select{height:30px;max-width:260px}
.db-hero{max-width:500px;margin:0 auto;text-align:center;padding:60px 16px}
.db-hero .big{width:84px;height:84px;margin:0 auto 18px;border-radius:24px;display:grid;place-items:center;background:var(--accent-soft);color:var(--accent)}
.db-hero h2{font-size:28px;font-weight:600;margin-bottom:12px}.db-hero p{color:var(--text-muted);font-size:14px;line-height:22px;margin:0}
.db-hero .card{text-align:left;margin-top:32px;padding:20px}.db-hero .btn{height:44px;width:100%;justify-content:flex-start;font-size:14px;margin-bottom:10px}
.db-load{position:absolute;inset:0;z-index:20;display:grid;place-items:center;background:var(--surface-overlay);text-align:center}
.db-stage{position:relative;min-height:480px}
.db-opts{display:flex;flex-wrap:wrap;gap:8px 20px;margin-top:12px}
`;
  const connectedBar = (state) => `<div class="db-top">
    <div class="db-bar1">
      ${lnk('plus', 'New Connection', 'new-connection-window.html', 'primary')}<span class="vdivider" style="height:22px"></span>
      ${lnk('folder', `Saved <span class="muted">(${state === 'dis' ? 4 : 4})</span>`, 'saved-connections-window.html', 'ghost')}
      ${btn('home', 'Localhost', 'success', 'title="Quick connect to localhost MySQL" data-toast="success" data-title="Connected" data-msg="127.0.0.1:3306"')}
      <div class="status">${state === 'dis' ? `<span class="dot"></span><span>Not Connected</span>` : `<span class="dot ok"></span><span class="sec">Connected to 203.0.113.24:3306</span><b>· myproject_prod</b>`}</div>
      ${iconBtn('refresh', 'Refresh Schema', 'ghost' + (state === 'dis' ? ' disabled' : ''), state === 'dis' ? '' : 'data-toast="info" data-title="Schema refreshed" data-msg="38 tables"')}
      ${lnk('settings', '', 'connection-manager-window.html', 'ghost icon', 'title="Connection Manager"')}
      ${btn('power', 'Disconnect', 'danger' + (state === 'dis' ? ' disabled' : ''), 'data-confirm="Disconnect from 203.0.113.24:3306?" data-confirm-title="Disconnect"')}
      ${iconBtn('external', 'Open in new window', 'ghost', 'data-toast="info" data-title="Database Studio" data-msg="Opened in new window"')}
    </div>
    ${state === 'dis' ? '' : `<div class="db-bar2">
      <span class="muted">${ic('database')} Database:</span><div class="sel1">${sel([['myproject_prod', 1], 'myproject_staging', 'information_schema', 'mysql', 'performance_schema'], 'sm')}</div>
      <span class="muted">${ic('table')} Table:</span><div class="stack sm sel2"><input class="input sm" placeholder="Search tables"><select class="select"><option>audit_log</option><option>coupons</option><option>invoices</option><option selected>orders</option><option>order_items</option><option>products</option><option>sessions</option><option>users</option></select></div>
      <button type="button" class="btn ghost icon" title="Table actions (right-click)" data-ctx="dbTableCtx" style="font-size:18px">${ic('more-v')}</button>
      ${lnk('activity', 'SHOW PROCESSLIST', 'process-monitor-window.html', 'ghost', 'title="Open the server process monitor"')}
      <span class="grow"></span>
      ${state === 'busy' ? '' : `<div class="progress busy" style="width:150px"><i data-progress="30" data-speed="2.4" data-loop style="width:30%"></i></div>`}
    </div>`}
  </div>`;

  const sqlCode = `<div class="code"><div class="gut">1<br>2<br>3<br>4<br>5<br>6</div><pre><span class="tok-c">-- Table Preview · orders</span>
<span class="tok-k">SELECT</span> o.id, o.user_id, o.status, o.total, u.email
  <span class="tok-k">FROM</span> orders o
  <span class="tok-k">JOIN</span> users u <span class="tok-k">ON</span> u.id = o.user_id
 <span class="tok-k">WHERE</span> o.status <span class="tok-k">IN</span> (<span class="tok-s">'paid'</span>, <span class="tok-s">'refunded'</span>) <span class="tok-k">AND</span> o.created_at &gt;= <span class="tok-s">'2026-11-01'</span>
 <span class="tok-k">ORDER BY</span> o.id <span class="tok-k">DESC</span> <span class="tok-k">LIMIT</span> <span class="tok-n">100</span>;</pre></div>`;
  const resRows = [[1284903, 48371, 'paid', '149.90', 'USD', '2026-11-02 02:29:51'], [1284902, 12044, 'paid', '39.00', 'USD', '2026-11-02 02:29:12'], [1284901, 30918, 'refunded', '212.45', 'EUR', '2026-11-02 02:27:40'], [1284900, 777, 'pending', '18.20', 'USD', '2026-11-02 02:26:03'], [1284899, 51002, 'paid', '1,020.00', 'GBP', '2026-11-02 02:25:48'], [1284898, 9, 'failed', '75.00', 'USD', '2026-11-02 02:24:31']]
    .map((r) => `<tr><td class="num mono">${r[0]}</td><td class="num mono">${r[1]}</td><td>${badge(r[2], r[2] === 'paid' ? 'success' : r[2] === 'failed' ? 'error' : r[2] === 'pending' ? 'warning' : 'info')}</td><td class="num mono">${r[3]}</td><td class="mono">${r[4]}</td><td class="mono">${r[5]}</td></tr>`).join('');

  const connectedMain = (importing) => `<div class="db-main">
    <div class="db-editor"><div class="lbl2">Table Preview · orders<small>· Ctrl+Space for autocomplete</small></div>${sqlCode}</div>
    <div class="db-actions">${btn('play', 'Run Query', 'primary', 'data-working="Running…" data-done="Query executed" data-done-msg="Returned 100 rows."')}${btn('', 'Clear', 'ghost', 'data-toast="info" data-title="Query editor cleared."')}
      <span class="muted" style="margin-left:10px">Timeout:</span><div style="width:120px">${sel(['30 seconds', ['60 seconds', 1], '120 seconds', '5 minutes', '10 minutes'], 'sm')}</div></div>
    <div class="db-results"><div class="grid-wrap"><table class="dg" data-select><thead><tr><th class="num">id</th><th class="num">user_id</th><th>status</th><th class="num">total</th><th>currency</th><th>created_at</th></tr></thead><tbody>${resRows}</tbody></table></div></div>
    <div class="muted" style="margin-top:10px">Showing first 100 rows from orders</div>
    <div class="db-two">
      ${card('', `<div class="row" style="margin-bottom:4px"><b style="font-size:16px">Create database</b><span class="badge" style="background:var(--accent);color:#fff;border-color:var(--accent)">New</span></div>
        <div class="muted" style="margin-bottom:12px">Provision a schema on the connected server with your preferred charset + collation.</div>
        <div class="db-lbl"><span>Database name</span><input class="input" placeholder="e.g. project_live" value="project_live"></div>
        <div class="db-lbl"><span>Charset</span><select class="select"><option selected>utf8mb4 · UTF-8 (4-byte)</option><option>utf8 · UTF-8 (3-byte)</option><option>latin1 · Western (latin1)</option></select></div>
        <div class="db-lbl"><span>Collation</span><select class="select"><option selected>utf8mb4_unicode_ci</option><option>utf8mb4_general_ci</option><option>utf8mb4_0900_ai_ci</option><option>utf8mb4_bin</option></select></div>
        <div class="row" style="margin-top:12px">${btn('', 'Create database', 'primary', 'style="width:170px" data-working="Creating…" data-done="Database created" data-done-msg="Created \'project_live\' (utf8mb4/utf8mb4_unicode_ci)."')}<span class="inf">Created 'project_live' (utf8mb4/utf8mb4_unicode_ci).</span></div>
        <div class="muted" style="margin-top:8px;font-size:12px">Failed state: <span class="err">Failed: Can't create database 'project_live'; database exists</span> · Empty state: <span class="inf">Enter a database name.</span></div>`, { cls: '' })}
      ${card('', `<div class="row" style="margin-bottom:4px"><b style="font-size:16px">Database import</b><span class="badge" style="background:var(--accent2);color:#fff;border-color:var(--accent2)">Beta</span></div>
        <div class="muted" style="margin-bottom:12px">Import .sql / .zip / .tar.gz backups directly into the active connection.</div>
        <div class="row"><input class="input mono" value="D:\\Backups\\myproject\\db\\myproject_prod_26_11_02_02_30_verify.sql.zip">${btn('', 'Browse...', 'primary', 'style="width:110px" data-toast="info" data-title="Browse" data-msg="Select .sql / .zip / .tar.gz"')}</div>
        <div class="db-lbl" style="margin-top:10px"><span>Target database</span><select class="select"><option selected>myproject_staging</option><option>myproject_prod</option><option>project_live</option></select></div>
        <div class="db-lbl"><span>Import mode</span><select class="select"><option>Standard</option><option selected>Fast (disable checks)</option></select></div>
        <div class="db-opts">${chk('Drop &amp; recreate database before import', false)}${chk('Disable foreign key checks', true)}${chk('Continue on errors', false)}</div>
        <div class="row" style="margin-top:14px">${btn('', 'Start import', 'success', 'style="width:150px"' + (importing ? ' disabled' : ''))}${btn('', 'Cancel', 'danger', 'style="width:120px" data-confirm="Cancel the running import?" data-confirm-title="Cancel import"')}</div>
        <div class="progress busy" style="margin-top:14px"><i data-progress="44" data-speed="0.9" data-loop style="width:44%"></i></div>
        <div class="sec" style="margin-top:6px">184.2 MB / 412.8 MB (44.6%)</div>
        ${stream([['i', 'Starting import of myproject_prod_26_11_02_02_30_verify.sql.zip → myproject_staging'], ['i', 'Preparing file...'], ['w', '⚠️ Skipping DEFINER on view v_sales_daily'], ['i', 'Executed 12,000 statements'], ['i', 'Executed 48,000 statements'], ['w', '⚠️ Duplicate entry \'1042\' for key \'PRIMARY\' (continuing)'], ['i', 'Executed 96,000 statements'], ['ok', '✅ Import completed.']], { cls: 'log', interval: 900, style: 'height:120px;margin-top:12px' })}`, { cls: '' })}
    </div></div>`;

  const disconnectedMain = `<div class="db-hero"><div class="big">${ic('database', 'ic-2xl')}</div><h2>Database Studio</h2><p>Connect to MySQL/MariaDB databases, browse tables, run queries and manage your data with ease.</p>
    <div class="card"><div style="font-size:16px;font-weight:600;margin-bottom:16px">Quick Start</div>
      ${btn('home', 'Connect to Localhost (127.0.0.1)', 'primary', 'data-working="Connecting…" data-done="Connected" data-done-msg="127.0.0.1:3306"')}
      ${lnk('plus', 'New Custom Connection', 'new-connection-window.html', 'ghost')}
      ${lnk('folder', 'Open Saved Connection', 'saved-connections-window.html', 'ghost')}</div></div>`;

  const ctxMenu = `<div class="ctx" id="dbTableCtx"><div class="menu"><a class="mi danger" href="#" data-confirm="TRUNCATE TABLE \`orders\`? All rows are removed and AUTO_INCREMENT is reset. This cannot be undone." data-confirm-title="Truncate table">${ic('trash')}Truncate table</a><a class="mi danger" href="#" data-confirm="DELETE all rows from \`orders\`? Rows are removed one by one; AUTO_INCREMENT is kept." data-confirm-title="Empty table">${ic('trash')}Empty table (DELETE all rows)</a></div></div>`;

  const dbBody = `<div class="db-wrap" data-tabscope>
    <div class="db-state"><span class="eyebrow">Screen state</span><div class="tabs pills"><button class="tab active" data-tab="dbConn">Connected</button><button class="tab" data-tab="dbDis">Not connected</button><button class="tab" data-tab="dbLoad">Connecting…</button></div><span class="muted">(spec switcher — not part of the app)</span></div>
    <div class="tabpanel active" id="dbConn" style="flex:1">${connectedBar('on')}${connectedMain(true)}</div>
    <div class="tabpanel" id="dbDis">${connectedBar('dis')}${disconnectedMain}</div>
    <div class="tabpanel" id="dbLoad"><div class="db-stage">${connectedBar('busy')}<div class="db-load"><div><div class="spin" style="display:inline-block">${ic('spinner', 'ic-2xl accent')}</div><div style="font-size:18px;font-weight:600;margin-top:12px">Connecting to 203.0.113.24:3306…</div><div class="muted" style="margin-top:6px">Opening SSH tunnel · <span class="mono">deploy@203.0.113.24:22</span></div></div></div></div></div>
  </div>`;

  L.page({ slug: 'database', title: 'Database Studio', nav: 'database', group: 'Pages', css: dbCss, body: dbBody, flush: true, dialogs: ctxMenu });
};
