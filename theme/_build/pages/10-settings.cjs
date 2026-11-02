// 10-settings.cjs  Settings (re-designed), About, update windows, terminal suggestions, CLI help windows.
module.exports = (L) => {
  const { ic, btn, badge, inp, sel, esc } = L;

  /* ---------------------------------------------------------------- helpers */
  const swt = (on = false, extra = '') => `<label class="switch"><input type="checkbox" ${on ? 'checked' : ''} ${extra}></label>`;
  const rad = (label, on, name) => `<label class="check"><input type="radio" name="${name}" ${on ? 'checked' : ''}>${label}</label>`;
  const chk = (label, on = false, extra = '') => `<label class="check"><input type="checkbox" ${on ? 'checked' : ''} ${extra}>${label}</label>`;
  // one settings row: title + description on the left, control on the right
  const row = (title, desc, ctl, { kw = '', cls = '', ctlCls = '' } = {}) =>
    `<div class="form-row srow ${cls}" data-k="${esc(kw)}"><div class="st-txt"><div class="t">${title}</div>${desc ? `<div class="d">${desc}</div>` : ''}</div><div class="ctl ${ctlCls}">${ctl}</div></div>`;
  // card (section block)
  const card = (icon, title, hint, inner, { right = '', cls = '', id = '' } = {}) =>
    `<section class="card scard ${cls}" ${id ? `id="${id}"` : ''}><div class="card-head">${ic(icon)}<h3>${title}</h3>${hint ? `<span class="muted card-hint">${hint}</span>` : ''}${right ? `<div class="r">${right}</div>` : ''}</div>${inner}</section>`;
  const note = (t, cls = '') => `<div class="hint ${cls}">${t}</div>`;
  const status = (id, t, cls = '') => `<div class="st-status mono ${cls}" id="${id}">${t}</div>`;
  const badgeRow = (...b) => `<span class="row" style="gap:6px">${b.join('')}</span>`;

  /* ---------------------------------------------------------------- theme palette data */
  const KEYS = ['Surface.Base', 'Surface.Shell', 'Surface.Raised', 'Surface.Card', 'Surface.Input', 'Surface.Overlay', 'Border.Subtle', 'Border.Strong', 'Text.Primary', 'Text.Secondary', 'Text.Muted', 'Text.Inverse', 'Accent.Primary', 'Accent.PrimaryHover', 'Accent.Secondary', 'Accent.SecondaryHover', 'Status.Success', 'Status.Warning', 'Status.Error', 'Status.Info', 'Status.SuccessSurface', 'Status.WarningSurface', 'Status.ErrorSurface', 'Status.InfoSurface', 'State.Hover', 'State.Pressed', 'State.DisabledOverlay', 'Shadow.Color', 'Log.Success', 'Tree.SearchMatchBackground', 'Tree.SearchMatchForeground', 'Chat.BubbleIn', 'Chat.BubbleOut', 'Chat.BubbleInText', 'Chat.BubbleOutText', 'Chat.Composer', 'Chat.Unread', 'Chat.ThreadHover'];
  const GROUPS = [['Surfaces', 0, 6], ['Borders', 6, 8], ['Text', 8, 12], ['Accent', 12, 16], ['Status', 16, 24], ['State & misc', 24, 31], ['Chat (Telegram)', 31, 38]];
  const midnight = '#03060C #070B13 #0B111C #0E1523 #080D17 #C701040A #1A2538 #2A3A55 #E8EEFB #C3CEE3 #7F8FA9 #020510 #4C8DFF #6AA0FF #7B61FF #9683FF #3DDC84 #E6B84D #FF5C6A #5BB8FF #07170F #1A1507 #1A090C #071524 #1478AAFF #2478AAFF #AA070B13 #99000000 #5CFFB0 #E6B84D #000000 #131C2C #4C8DFF #E8EEFB #FFFFFF #080D17 #4C8DFF #1478AAFF'.split(' ');
  const dark = '#000000 #0A0A0A #111111 #141414 #0D0D0D #CC000000 #222222 #333333 #E8E8E8 #C8C8C8 #8A8A8A #000000 #4C8DFF #6AA0FF #7B61FF #9683FF #3DDC84 #E6B84D #FF5C6A #5BB8FF #0A1A12 #1A1408 #1A0A0C #0A121A #14FFFFFF #22FFFFFF #AA0A0A0A #99000000 #5CFFB0 #E6B84D #000000 #1A1A1A #4C8DFF #E8E8E8 #FFFFFF #0D0D0D #4C8DFF #14FFFFFF'.split(' ');
  const deflt = '#171A1F #1E232C #232A35 #29313D #202733 #B5181D25 #36404F #4B5A70 #F4F7FF #D4DEEE #95A3BA #10131A #5C8DFF #78A0FF #7B61FF #9683FF #4CD27E #FFBF47 #FF6C7A #70C2FF #20382C #3A301F #3A2228 #1F2F43 #1FFFFFFF #2FFFFFFF #AA2D3442 #66000000 #65FFB2 #FFBF47 #10131A #29313D #5C8DFF #F4F7FF #F4F7FF #202733 #5C8DFF #1FFFFFFF'.split(' ');
  const solar = midnight.slice();
  Object.entries({ 0: '#002B36', 1: '#073642', 2: '#0A3F4D', 3: '#0D4857', 4: '#06303B', 6: '#12515F', 7: '#1D6577', 8: '#EEE8D5', 9: '#CFCAB8', 10: '#839496', 12: '#2AA198', 13: '#3CC0B6', 14: '#6C71C4', 15: '#8A8FDB', 32: '#2AA198', 36: '#2AA198' }).forEach(([i, v]) => (solar[i] = v));
  const THEMES = [
    { id: 'midnight-blue', name: 'Midnight Blue', kind: 'built-in', p: midnight, active: true },
    { id: 'default', name: 'Default', kind: 'built-in', p: deflt },
    { id: 'dark', name: 'Dark', kind: 'built-in', p: dark },
    { id: 'solarized-night', name: 'Solarized Night', kind: 'custom', p: solar },
  ];
  const css8 = (h) => (h.length === 9 ? `rgba(${parseInt(h.slice(3, 5), 16)},${parseInt(h.slice(5, 7), 16)},${parseInt(h.slice(7, 9), 16)},${(parseInt(h.slice(1, 3), 16) / 255).toFixed(2)})` : h);
  const swatchGrid = (t) => `<div class="pal" data-pal="${t.id}" ${t.active ? '' : 'hidden'}>${GROUPS.map(([g, a, b]) => `<div class="pal-g"><div class="eyebrow">${g}</div><div class="pal-row">${KEYS.slice(a, b).map((k, i) => `<div class="sw" title="${k}  ${t.p[a + i]}"><i style="background:${css8(t.p[a + i])}"></i><span class="mono"><b>${k.split('.').slice(1).join('.')}</b><small>${t.p[a + i]}</small></span></div>`).join('')}</div></div>`).join('')}</div>`;
  const themeItem = (t) => `<div class="theme-item ${t.active ? 'sel' : ''}" data-theme="${t.id}" data-kind="${t.kind}" data-name="${t.name}" tabindex="0">
    <div class="theme-prev" style="background:${css8(t.p[0])};border-color:${css8(t.p[7])}"><i style="background:${css8(t.p[1])}"></i><b style="background:${css8(t.p[3])};border-color:${css8(t.p[6])}"><em style="background:${css8(t.p[12])}"></em><em style="background:${css8(t.p[8])};opacity:.5"></em></b><u style="color:${css8(t.p[8])}">Aa</u></div>
    <div class="grow"><div class="t">${t.name} <span class="muted">(${t.kind})</span> <span class="badge accent act" ${t.active ? '' : 'hidden'}>${ic('check', 'ic-sm')} active</span></div><div class="d mono">${t.id}  ·  gitdeploypro-theme/v1  ·  ${KEYS.length} keys</div></div>
    <div class="theme-dots">${[0, 3, 12, 14, 16, 17, 18].map((i) => `<i style="background:${css8(t.p[i])}"></i>`).join('')}</div></div>`;

  /* ---------------------------------------------------------------- shared inline script (terminal suggestions filter) */
  const TS_SCRIPT = `<script>(function(){
    var f=document.querySelector('[data-scopefilter]'),s=document.getElementById('tsSearch'),body=document.getElementById('tsBody'),st=document.getElementById('tsStatus');
    if(!body)return;
    function run(){var q=(s.value||'').toLowerCase(),sc=f?f.value:'All',n=0;
      [].forEach.call(body.rows,function(r){var ok=(sc==='All'||r.dataset.scope===sc)&&(!q||r.textContent.toLowerCase().indexOf(q)>-1);r.hidden=!ok;if(ok)n++;});
      if(st)st.textContent=n+' command(s)';var em=document.getElementById('tsEmpty');if(em)em.hidden=n>0;}
    if(s)s.addEventListener('input',run);if(f)f.addEventListener('change',run);run();
    document.addEventListener('click',function(e){var r=e.target.closest('#tsBody tr');if(r){[].forEach.call(body.rows,function(x){x.classList.remove('sel')});r.classList.add('sel');}});
  })();</script>`;

  /* ---------------------------------------------------------------- TerminalSuggestionsSettingsPanel (shared) */
  const sugRows = [
    ['git status', 'Show working tree status', 'Global', 'Git', 1], ['git log --oneline --graph -20', 'Compact commit graph', 'Global', 'Git', 1],
    ['docker compose up -d', 'Start containers in background', 'Global', 'Docker', 1], ['tail -f storage/logs/laravel.log', 'Follow Laravel log', 'This project', 'Laravel', 1],
    ['php artisan migrate --force', 'Run pending migrations', 'This project', 'Laravel', 1], ['npm run build', 'Production asset build', 'This project', 'Node', 0],
    ['systemctl restart php8.2-fpm', 'Restart PHP-FPM', 'Other projects', 'Linux', 1], ['pm2 reload ecosystem.config.js', 'Zero-downtime reload', 'Other projects', 'Node', 1],
  ];
  const scopeBadge = (s) => (s === 'Global' ? badge('Global', 'info') : s === 'This project' ? badge('This project', 'success') : badge('Other project', ''));
  const tsPanel = (idp = '') => `<div class="ts-panel">
    <div class="muted mono ts-cur" style="font-size:11px">Current project: <span class="sec">subtell</span> · C:\\laragon\\www\\subtell</div>
    <div class="ts-switches">
      ${row('Enable terminal autocomplete', 'Ghost text appears while typing; accept with Tab.', swt(true), { kw: 'autocomplete ghost text tab' })}
      ${row('Dictionary mode', 'Highlight unknown commands (red while typing).', swt(false), { kw: 'dictionary unknown commands red' })}
    </div>
    <div class="row wrap" style="gap:14px;margin:4px 0 12px"><span class="muted" style="font-size:11px"><kbd class="kbd">Tab</kbd> accept · <kbd class="kbd">↑</kbd><kbd class="kbd">↓</kbd> cycle · <kbd class="kbd">→</kbd> accept</span><span class="grow"></span>${L.stream([['p', '$ git sta'], ['m', '  ↳ git status                       Show working tree status'], ['p', '$ docker co'], ['m', '  ↳ docker compose up -d             Start containers in background']], { interval: 1700, style: 'height:78px;max-width:520px;flex:1 1 320px;padding:8px 12px' })}</div>
    <div class="row" style="margin-bottom:8px"><div class="input-icon grow">${ic("search")}<input class="input sm" id="tsSearch" placeholder="Search commands, description, scope, category…" style="height:30px"></div><select class="select sm" data-scopefilter style="width:150px;height:30px"><option>All</option><option>Global</option><option>This project</option><option>Other projects</option></select></div>
    <div class="row wrap" style="gap:6px;margin-bottom:8px">${btn('plus', 'Add', 'primary compact', 'data-open="dlgTermSug"')}${btn('edit', 'Edit', 'compact', 'data-open="dlgTermSug"')}${btn('trash', 'Delete', 'compact ghost', 'data-confirm="Delete selected terminal command?"')}${btn('copy', 'Duplicate', 'compact ghost', 'data-toast="success" data-title="Duplicated" data-msg="git status (copy) added to This project"')}${btn('globe', 'Make global', 'compact ghost', 'data-toast="success" data-title="Scope changed" data-msg="Command is now available in all projects"')}${btn('refresh', 'Restore built-in packs', 'compact ghost', 'data-working="Restoring…" data-done="Built-in packs restored" data-done-msg="Git, Docker, Linux, npm, Composer packs re-added"')}</div>
    <div class="card flush" style="border-radius:8px"><div class="grid-wrap" style="max-height:330px;min-height:220px"><table class="dg"><thead><tr><th>Command</th><th>Description</th><th>Scope</th><th>Category</th><th style="text-align:center">On</th></tr></thead><tbody id="tsBody">
      ${sugRows.map((r, i) => `<tr data-ctx="tsCtx" data-scope="${r[2]}" class="${i === 3 ? 'sel' : ''}"><td class="mono">${r[0]}</td><td class="muted">${r[1]}</td><td>${scopeBadge(r[2])}</td><td>${r[3]}</td><td style="text-align:center"><label class="check"><input type="checkbox" ${r[4] ? 'checked' : ''}></label></td></tr>`).join('')}
    </tbody></table><div class="empty" id="tsEmpty" hidden>${ic('search')}<div>No commands match this filter.</div></div></div></div>
    <div class="row between" style="margin:6px 0 12px"><span class="muted mono" id="tsStatus" style="font-size:11px">8 command(s)</span><span class="muted" style="font-size:11px">Right-click a row for Edit… / Delete / Duplicate / Make global</span></div>
    <div class="card dense raised"><div class="t" style="font-weight:600;font-size:13px">Import from JSON</div><div class="muted" style="font-size:11px;margin:4px 0 10px">Bulk-import commands from a JSON file (supports thousands of entries).</div>
      <div class="form-grid" style="gap:10px 14px">
        <div class="field"><label>Category name</label><input class="input sm mono" style="height:30px" value="Linux" title="Required — e.g. Linux, Docker, Git"></div>
        <div class="field"><label>JSON file</label><div class="input-group"><input class="input sm mono" style="height:30px" readonly value="C:\\Users\\sharif\\Downloads\\linux-commands.json"><button class="btn compact" type="button" style="height:30px">Browse…</button></div></div>
        <div class="field"><label>Import scope</label><div class="row" style="gap:16px;height:30px">${rad('Global', true, 'impScope' + idp)}${rad('This project', false, 'impScope' + idp)}</div></div>
      </div>
      <div style="margin:10px 0">${chk('Skip duplicate commands (same command + scope)', true)}</div>
      <div class="row wrap" style="gap:8px">${btn('upload', 'Import commands', 'primary compact', 'data-working="Importing…" data-done="Import finished" data-done-msg="Imported 1,842 command(s), skipped 472 duplicate(s)."')}${btn('file-code', 'Open sample JSON', 'compact ghost', 'data-toast="info" data-title="Sample opened" data-msg="TerminalSuggestionsImport.sample.json"')}</div>
      <div style="margin-top:12px">${L.worker({ icon: 'upload', name: 'Importing linux-commands.json', desc: 'Category “Linux” · scope Global · skip duplicates', pct: 38, speed: 1.1, stats: ['<b data-pct>38%</b> parsed', '1,842 added', '472 skipped', '<span data-elapsed="14">00:00:14</span>'] })}</div>
      ${status('tsImportStatus', 'Select a JSON file to import.  ·  (idle message shown before a file is chosen)', 'muted')}
    </div></div>`;
  const tsCtx = `<div class="ctx" id="tsCtx"><div class="menu"><a class="mi" data-open="dlgTermSug">${ic('edit')}Edit…</a><a class="mi danger" data-confirm="Delete this terminal command?">${ic('trash')}Delete</a><a class="mi" data-toast="success" data-title="Duplicated" data-msg="Command copied">${ic('copy')}Duplicate</a><a class="mi" data-toast="success" data-title="Scope changed" data-msg="Command is now global">${ic('globe')}Make global</a></div></div>`;
  // Terminal command dialog fields (TerminalSuggestionDialog)
  const sugDialogBody = `<div class="stack" style="gap:16px">
      <div><div style="font-weight:600;font-size:13px;margin-bottom:8px">Scope for this command</div>
        <div class="stack sm">${rad('Global — available in all projects', true, 'sugScope')}${rad('This project only — subtell', false, 'sugScope')}<div class="hint" style="margin-left:24px">Stored for: <span class="mono">C:\\laragon\\www\\subtell</span></div></div>
        <div class="hint" style="margin:6px 0 0 24px">When no project is open: <span class="warn">Open a project first to scope a command to one project.</span> (radio disabled)</div></div>
      <div class="field"><label>Command</label><input class="input mono" id="sugCmd" value="php artisan migrate --force" placeholder="e.g. docker compose up -d"><div class="hint err" id="sugCmdErr" hidden>Command is required.</div></div>
      <div class="field"><label>Description</label><input class="input" value="Run pending migrations"></div>
      <div class="field"><label>Category</label><div class="input-group"><input class="input" list="sugCats" value="Laravel"><datalist id="sugCats"><option>Git</option><option>Docker</option><option>Laravel</option><option>Linux</option><option>Node</option></datalist><button class="btn icon" type="button" tabindex="-1" style="height:36px">${ic('chevron-down')}</button></div><div class="hint">Editable: pick an existing category or type a new one.</div></div></div>`;
  const sugModal = `<div class="scrim" id="dlgTermSug"><div class="modal" style="width:min(520px,100%)"><div class="modal-h">${ic('terminal')}<h3>Terminal command</h3></div><div class="modal-b">${sugDialogBody}</div><div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close>Save</button></div></div></div>`;

  /* ================================================================ SETTINGS PAGE */
  const SECTIONS = [
    ['general', 'folder', 'General', 'Project path, startup, updates, and danger zone.'],
    ['server', 'cloud', 'Server', 'FTP/SFTP connection profile and setup recovery.'],
    ['git', 'git-branch', 'Git', 'Remote, branches, deploy automation, and ignore patterns.'],
    ['terminal', 'terminal', 'Terminal', 'Terminal autocomplete commands and scopes.'],
    ['telegram', 'chat', 'Telegram', 'Bot token, allowed user ids, and polling.'],
    ['ai-agent', 'cpu', 'AI agent', 'Cursor and Claude Code engines for Telegram project chats.'],
    ['vpn', 'globe', 'VPN', 'OpenVPN Connect (default) or Community GUI — connect on startup and auto-reconnect.'],
    ['themes', 'palette', 'Themes', 'Import Deploy theme packs and manage custom skins.'],
  ];
  let NAVB = { general: badge('3', 'accent'), server: badge('2 FTP'), git: badge('1 push', 'warning'), terminal: badge('8'), telegram: badge('on', 'success'), 'ai-agent': badge('Cursor', 'violet'), vpn: badge('off'), themes: badge('Midnight', 'accent') };
  NAVB.general = '';

  /* ---- GENERAL ---- */
  const pGeneral = `
  <div class="card dense scard ws-card" data-k="workspace folder path browse"><div class="row"><div class="stat" style="gap:10px"><div class="si" style="width:34px;height:34px">${ic('folder-open')}</div></div><div class="grow"><div class="muted" style="font-size:11px">Workspace</div><div class="mono sec ellipsis" id="wsHint" style="font-size:12px">C:\\laragon\\www\\subtell</div></div>${btn('folder-open', 'Browse', 'ghost', 'title="Select the folder GitDeploy Pro manages"')}</div></div>
  ${card('folder-open', 'Local project', 'Select the folder GitDeploy Pro manages', row('Project folder', 'The folder GitDeploy Pro watches, commits and deploys from.', `<div class="input-group"><input class="input mono" id="localPath" value="C:\\laragon\\www\\subtell" data-mirror="#wsHint"><button class="btn" type="button">${ic('folder-open')}<span class="bl">Browse</span></button></div><div class="hint ok" style="margin-top:6px">${ic('check-circle', 'ic-sm')} Folder exists · git repository detected · branch <span class="mono">master</span></div><div class="hint err vh">${ic('alert', 'ic-sm')} Folder does not exist.</div>`, { kw: 'project folder local path', ctlCls: 'wide' }), { id: 'c-local' })}
  ${card('wand', 'Configuration file', 'Git ignore helper for project metadata', row('Add config file to .gitignore', 'Keeps <span class="mono">.gitdeploy.config</span> out of commits.', `<div class="row" style="gap:12px">${swt(true)}${btn('plus', 'Add Now', 'compact ghost', 'data-toast="success" data-title=".gitignore updated" data-msg="Added .gitdeploy.config"')}</div>`, { kw: 'gitignore config file add now' }))}
  ${card('layers', 'Project type', 'How Telegram and deploy shortcuts treat this project.', row('Project type', 'Web/FTP projects show Deploy on Telegram. Windows desktop or agent-only projects hide it.', sel([['Web / FTP deploy', 1], 'Windows desktop app', 'No deploy (agent only)']), { kw: 'project type web ftp windows desktop no deploy agent', ctlCls: '' }))}
  ${card('globe', 'Language', 'Tooltip language for button descriptions. Default is English.', row('Language', 'Persian translates button tooltips only. Menus and layout stay English (LTR).', sel([['English', 1], 'فارسی (Persian)']), { kw: 'language persian english tooltip' }) + `<div class="hint" style="padding:0 0 4px">${ic('info', 'ic-sm')} Some labels refresh immediately; reopen pages if needed.</div>`)}
  ${card('file-text', 'Editor', 'How files open on the Deploy page (all projects)', row('Open files', 'Choose where the editor appears when you open a file from the Deploy tree.', `<div class="stack sm">${rad('Docked in Deploy (current)', true, 'editorMode')}${rad('Float window', false, 'editorMode')}</div>`, { kw: 'editor dock float window deploy open mode' }))}
  ${card('monitor', 'Startup &amp; tray', '', row('Launch GitDeploy Pro when Windows starts', 'Adds a Run entry for the current exe (see the startup audit below).', swt(true), { kw: 'launch startup windows' }) + row('Show localhost warning in Backup Scheduler', 'Warn when a scheduled backup targets localhost.', swt(true), { kw: 'localhost warning backup scheduler' }) + row('Minimize and close to system tray', 'The window hides to the tray instead of exiting.', swt(false), { kw: 'minimize close system tray' }))}
  ${card('upload', 'Software updates', '', `<dl class="kv" style="margin-bottom:12px"><dt>Version</dt><dd class="mono">Current version: v2.4.0</dd><dt>Last check</dt><dd class="mono muted">Last automatic check: 2026-11-02 00:39</dd></dl><div class="row wrap">${btn('refresh', 'Check for updates now', 'ghost', 'data-working="Checking…" data-done="You are up to date" data-done-msg="v2.4.0 is the latest version"')}<a class="btn ghost" href="update-status-window.html">${ic('info')}<span class="bl">Update window preview</span></a></div>`, { right: badge(ic('check', 'ic-sm') + ' up to date', 'success') })}
  ${card('settings', 'Windows startup audit', '', `<div class="stack sm"><div class="row"><span class="dot ok"></span><span id="auditSum">1 GitDeploy startup entry found — points to the current exe.</span></div>
      <div class="mono muted" style="font-size:12px">Current exe: C:\\Program Files\\GitDeployPro\\GitDeployPro.exe</div><div class="mono muted" style="font-size:12px">Primary startup value: "C:\\Program Files\\GitDeployPro\\GitDeployPro.exe" --minimized</div>
      <div class="lbl">Detected GitDeploy startup entries:</div><textarea class="input mono" readonly rows="3" style="font-size:11px;min-height:72px;max-height:110px">HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\GitDeployPro = "C:\\Program Files\\GitDeployPro\\GitDeployPro.exe" --minimized
HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\GitDeployPro_old = "D:\\Tools\\GitDeploy\\GitDeployPro.exe"  (stale)</textarea>
      <div class="row wrap" style="margin-top:4px">${btn('refresh', 'Refresh', 'ghost', 'data-working="Scanning registry…" data-done="Startup audit refreshed"')}${btn('check', 'Use this version', 'primary', 'data-toast="success" data-title="Startup updated" data-msg="Run entry now points to the current exe"')}${btn('trash', 'Remove startup entries', 'ghost', 'data-confirm="Remove all GitDeploy startup entries from Windows?"')}</div></div>`, { right: badge('1 stale', 'warning') })}
  <section class="card scard dz" data-k="danger zone remove project"><div class="card-head">${ic('alert')}<h3>Danger zone</h3><div class="r">${badge('irreversible in app', 'error')}</div></div>
    <div class="dz-box"><div class="sec" style="font-size:12px">Remove this project from GitDeploy Pro only. Files stay on disk.</div><div class="mono muted" style="font-size:11px;margin:6px 0 10px" id="dzPath">C:\\laragon\\www\\subtell</div><div class="muted" style="font-size:11px;margin-bottom:10px">(Empty state: “No project selected.”)</div>${btn('trash', 'Remove project from app', 'danger', 'data-confirm="Remove “subtell” from GitDeploy Pro? Files stay on disk." data-confirm-title="Remove project"')}</div></section>`;

  /* ---- SERVER ---- */
  const prof = (n, host, def, proto) => `<div class="prof" data-ctx="profCtx"><div class="grow"><div class="row"><b class="ellipsis">${n}</b>${def ? badge('Default', 'success') : ''}${badge(proto, 'info')}</div><div class="mono muted ellipsis" style="font-size:11px;margin-top:2px">${host}</div></div><button class="btn icon compact ghost" type="button" title="Set as the default FTP for this project" ${def ? 'disabled' : ''} data-toast="success" data-title="Default FTP changed" data-msg="${n} is now the default">★</button><button class="btn icon compact ghost" type="button" title="Remove this FTP from the project" data-remove>−</button></div>`;
  const pServer = `
  ${card('cloud', 'Connection profile', 'Assign one or more FTP/SFTP profiles. The default is used by Commit and Sync.', `<div class="profs" id="profList">${prof('Production — cPanel', 'deploy@ftp.example.com:21 · /public_html', true, 'FTP')}${prof('Staging', 'staging@staging.example.com:22 · /var/www/staging', false, 'SFTP')}${prof('Backup VPS', 'root@203.0.113.24:22 · /srv/backups', false, 'SFTP')}</div>
    <div class="empty-note" data-empty="#profList" hidden>No FTP/SFTP profile assigned to this project.</div>
    <div class="row wrap" style="margin-top:12px"><select class="select" style="flex:1 1 220px;width:auto"><option>Select a profile to add…</option><option>Client B — Hetzner (SFTP)</option><option>Legacy shared host (FTP)</option></select>${btn('plus', 'Add', 'ghost', 'title="Assign an FTP/SFTP profile to this project" data-toast="success" data-title="Profile assigned" data-msg="Client B — Hetzner added to this project"')}<a class="btn primary" href="connection-manager-window.html">${ic('server')}<span class="bl">Manage profiles</span></a></div>`, { right: badge('3 assigned', 'accent'), id: 'c-ftp' })}
  ${card('key', 'Default SSH (Deploy + Telegram)', '', row('SSH profile', 'SSH profile used first in Deploy terminal and Telegram. Falls back to default FTP when it uses SSH.', sel([['Staging (staging@staging.example.com:22)', 1], 'Backup VPS (root@203.0.113.24:22)', '— none —']), { kw: 'default ssh profile deploy telegram terminal' }))}
  ${card('shield', 'Server config backup', 'Export or restore FTP, SSH, and database profiles as an encrypted JSON file.', row('Backup password', 'Used to encrypt the export and to decrypt an import.', `<input class="input" type="password" value="hunter2-secret" id="bkPw">`, { kw: 'backup password encrypt' }) + `<div class="row wrap" style="margin:10px 0 12px">${btn('download', 'Export…', 'ghost', 'data-working="Encrypting…" data-done="Server config exported" data-done-msg="gitdeploy-servers-2026-11-02.gdbak (6.4 KB)"')}${btn('upload', 'Import…', 'primary', 'data-working="Decrypting…" data-done="Server config imported" data-done-msg="3 FTP, 1 SSH, 2 database profiles restored"')}</div>
    <div class="stack sm">${L.worker({ icon: 'download', name: 'Exporting server config', desc: 'AES-256 · gitdeploy-servers-2026-11-02.gdbak', pct: 62, speed: 2, stats: ['<b data-pct>62%</b>', '3 FTP · 1 SSH · 2 DB profiles'] })}${L.worker({ icon: 'upload', name: 'Import server config', desc: 'Wrong password — could not decrypt file', pct: 18, state: 'failed', stats: ['Failed', 'Check the backup password and try again'] })}</div>`)}
  ${card('eye', 'Active profile preview', 'Read-only summary of the default profile', `<dl class="kv"><dt>Host</dt><dd class="mono">ftp.example.com</dd><dt>Protocol</dt><dd class="mono">FTP (explicit TLS)</dd><dt>User</dt><dd class="mono">deploy</dd><dt>Root path</dt><dd class="mono">/public_html</dd></dl><div class="hint" style="margin-top:8px">With no profile: all four values show <span class="mono">-</span>.</div>`)}
  ${card('wand', 'Setup wizard &amp; recovery', '', `<div class="row wrap"><a class="btn ghost" href="project-setup-wizard.html">${ic('wand')}<span class="bl">Re-run setup wizard</span></a></div>
    <div class="dz-box info" style="margin-top:12px"><div style="font-weight:600;color:var(--info);font-size:13px">Reinitialize project setup</div><div class="sec" style="font-size:11px;margin:4px 0 10px">Remove setup artifacts, then run the wizard cleanly.</div><div class="stack sm">${chk('Remove .git folder')}${chk('Remove .gitdeploy.config')}</div><div style="margin-top:12px">${btn('refresh', 'Reset and run wizard', 'danger', 'data-confirm="Delete the selected setup artifacts and run the setup wizard again?" data-confirm-title="Reset project setup"')}</div></div>`, { cls: 'collapsible' })}`;

  /* ---- GIT ---- */
  const ign = (p) => `<div class="ign"><span class="mono ok ellipsis grow">${p}</span><button class="btn compact ghost" type="button" data-remove style="min-width:78px">Remove</button></div>`;
  const pGit = `
  ${card('git-branch', 'Git configuration', 'Remote, branches, and deploy automation', `
    ${row('Remote repository URL (origin)', 'Used for push / pull and the GitHub page.', `<input class="input mono inf" value="git@github.com:sharif/subtell.git" style="color:var(--info)">`, { kw: 'remote repository url origin', ctlCls: 'wide' })}
    ${row('SSH private key', 'For git@ remotes.', `<div class="input-group"><input class="input mono" value="C:\\Users\\sharif\\.ssh\\id_ed25519"><button class="btn" type="button" style="border-radius:0">Browse</button><button class="btn" type="button" data-working="Testing…" data-say="#sshRes" data-say-pre="ssh -T git@github.com …" data-say-ok="Hi sharif! You've successfully authenticated, but GitHub does not provide shell access.">Test</button></div><div class="hint ok mono vh2" id="sshRes">Hi sharif! You've successfully authenticated, but GitHub does not provide shell access.</div><div style="margin-top:8px">${btn('key', 'Open SSH key helper', 'compact ghost', 'title="Generate or register an SSH key"')}</div>`, { kw: 'ssh private key browse test helper', ctlCls: 'wide' })}
    <div class="banner warn" style="margin:12px 0 4px">${ic('cloud')}<b style="color:var(--warning)">Pending push</b><span class="muted">2 commits ahead of origin/master</span><span class="grow"></span><a class="btn compact" href="git.html">${ic('git-branch')}<span class="bl">Open GitHub page</span></a></div>
    ${row('Default source branch', 'Branch deploys are built from.', sel([['master', 1], 'main', 'develop', 'feature/telegram-presets']), { kw: 'default source branch' })}
    ${row('Default target branch', 'Branch commits are merged into.', sel(['master', ['main', 1], 'develop', 'feature/telegram-presets']), { kw: 'default target branch' })}
    <div class="row" style="padding:10px 0">${btn('refresh', 'Refresh branches', 'compact ghost', 'data-working="Fetching…" data-done="Branches refreshed" data-done-msg="4 local · 7 remote branches"')}<span class="muted mono" style="font-size:11px">git branch -a</span></div>
    ${row('Auto-init Git if not exists', 'Runs <span class="mono">git init</span> on first deploy.', swt(true), { kw: 'auto-init git' })}
    ${row('Auto-commit before deploy', 'Commits pending changes first.', swt(true), { kw: 'auto-commit before deploy' })}
    ${row('Auto-push after deploy', 'Pushes to origin after a successful upload.', swt(false), { kw: 'auto-push after deploy' })}
    ${row('Default upload workers (Commit + Deploy)', 'Used automatically on deploy upload. No worker prompt. Direct Upload still asks each time.', `<div class="row"><input class="input" style="width:52px;text-align:center;padding:0" maxlength="1" value="4" data-range="1,9"><span class="muted" style="font-size:11px">1–9 parallel connections</span></div><div class="hint err vh">Enter a number from 1 to 9.</div>`, { kw: 'default upload workers deploy commit', ctlCls: 'sm' })}`, { id: 'c-gitcfg' })}
  ${card('x-circle', 'Ignored files', "This project's .gitignore only — Add/Remove from Direct Upload stays in sync", `<div class="ignlist" id="ignList">${['vendor/', 'node_modules/', '.env', 'storage/logs/*.log', '*.zip', '.gitdeploy.config'].map(ign).join('')}</div>
    <div class="empty-note" data-empty="#ignList" hidden>No ignore lines in this project.</div>
    <div class="lbl" style="margin:12px 0 6px">Edit .gitignore (saved when the box loses focus)</div><textarea class="input mono ok" rows="8" style="min-height:160px;color:var(--log-success);font-size:12px" spellcheck="false">vendor/
node_modules/
.env
storage/logs/*.log
*.zip
.gitdeploy.config</textarea>`, { right: badge('6 patterns', 'accent') })}`;

  /* ---- TERMINAL ---- */
  const pTerminal = `
  ${card('sliders', 'Appearance', 'Font size and text color for every terminal. Same for all projects.', row('Font size', 'Applies to every terminal in every project.', sel(['10', '12', ['14', 1], '16', '18', '20']).replace('<select class="select ', '<select id="termFont" class="select '), { kw: 'terminal font size' }) + row('Text color', 'Default terminal foreground color.', `<select id="termColor" class="select"><option value="#D4D4D4" selected>Gray</option><option value="#FFFFFF">White</option><option value="#00FF00">Green</option><option value="#00FFFF">Cyan</option><option value="#FFFF00">Yellow</option></select>`, { kw: 'terminal text color gray white green cyan yellow' }) + `<div class="lbl" style="margin:12px 0 6px">Preview</div><div class="terminal" id="termPrev" style="font-size:14px;color:#D4D4D4;height:120px"><span class="p">sharif@subtell</span>:<span class="c">~/subtell</span>$ git status
On branch master
Your branch is ahead of 'origin/master' by 2 commits.
<span class="p">sharif@subtell</span>:<span class="c">~/subtell</span>$ <span class="cursor"></span></div>`, { id: 'c-termapp' })}
  ${card('terminal', 'Terminal suggestions', 'Autocomplete commands with ghost text · Global or per-project scope', tsPanel('s'), { right: `<a class="btn compact ghost" href="terminal-suggestions-settings-panel.html">${ic('external')}<span class="bl">Open alone</span></a>`, id: 'c-termsug' })}`;

  /* ---- TELEGRAM ---- */
  const pTelegram = `
  ${card('chat', 'Telegram bot', 'Long-poll while GitDeploy is running. Each project is a separate chat.', `
    ${row('Enable Telegram polling', 'The bot only answers while GitDeploy Pro is open.', swt(true), { kw: 'enable telegram polling' })}
    ${row('Bot token', 'From @BotFather. Stored encrypted.', `<input class="input mono" type="password" value="7312345678:AAH-example-token-hidden" id="tgTok"><div class="hint ok" id="tgTokHint" style="margin-top:6px">${ic('lock', 'ic-sm')} Token saved (ends …hidden). Type a new token to replace it.</div>`, { kw: 'bot token botfather', ctlCls: 'wide' })}
    ${row('Allowed user ids', 'Only these Telegram user ids can send. Get yours from @userinfobot.', `<input class="input mono" value="123456789, 987654321" placeholder="123456789, 987654321">`, { kw: 'allowed user ids userinfobot', ctlCls: 'wide' })}
    <div class="row wrap" style="margin-top:12px">${btn('wifi', 'Test connection', 'ghost', 'style="min-width:120px" data-working="Testing…" data-say="#tgStatus" data-say-pre="Calling getMe…" data-say-ok="Connected as @subtell_deploy_bot (id 7312345678)."')}${btn('save', 'Save Telegram', 'primary', 'style="min-width:120px" data-toast="success" data-title="Telegram saved" data-msg="Polling restarted"')}<a class="btn ghost" href="telegram.html">${ic('chat')}<span class="bl">Open chat page</span></a></div>
    ${status('tgStatus', '<span class="dot live"></span> Polling · last update 4 s ago · 2 allowed users')}`, { right: badge('<span class="dot live" style="width:6px;height:6px"></span> polling', 'success'), id: 'c-tgbot' })}
  ${card('terminal', 'Telegram terminal presets', 'Project-scoped inline buttons (trusted commands with &amp;&amp;, sudo, restart).', `<div class="muted mono" style="font-size:11px;margin-bottom:8px">Current project: subtell</div><div class="muted" style="font-size:12px;margin-bottom:10px">Trusted one-line commands shown as inline buttons while Telegram SSH terminal is open. Free-text lines still use the safety guard.</div>
    <div class="row wrap" style="gap:6px;margin-bottom:8px">${btn('plus', 'Add preset', 'primary compact', '')}${btn('code', 'Go build template', 'compact ghost', '')}${btn('edit', 'Edit', 'compact ghost', '')}${btn('trash', 'Delete', 'compact ghost', 'data-confirm="Delete selected preset?"')}</div>
    <div class="card flush" style="border-radius:8px"><div class="grid-wrap"><table class="dg" data-select><thead><tr><th>Label</th><th>Command</th><th>Notify</th></tr></thead><tbody><tr class="sel"><td>Restart PHP</td><td class="mono">sudo systemctl restart php8.2-fpm</td><td>${badge('On', 'success')}</td></tr><tr><td>Deploy pull</td><td class="mono">cd /var/www/subtell &amp;&amp; git pull origin master</td><td>${badge('On', 'success')}</td></tr><tr><td>Clear cache</td><td class="mono">php artisan optimize:clear</td><td>${badge('Off')}</td></tr><tr><td>Queue restart</td><td class="mono">php artisan queue:restart</td><td>${badge('On', 'success')}</td></tr></tbody></table></div></div>`)}`;

  /* ---- AI AGENT ---- */
  const chatRow = (p, c, o, l, on) => `<tr><td class="chk"><label class="check"><input type="checkbox" ${on ? 'checked' : ''}></label></td><td class="mono">${p}</td><td class="num">${c}</td><td class="num ${o > 100 ? 'warn' : ''}">${o}</td><td class="mono muted">${l}</td></tr>`;
  const pAi = `
  ${card('cpu', 'Default engine', 'Telegram /engine can override this anytime.', row('Engine', 'Which CLI answers real chat messages from Telegram.', sel([['Cursor', 1], 'Claude Code']), { kw: 'default engine cursor claude telegram /engine' }) + row('Telegram quiet progress', 'Applies to all engines (Cursor and Claude). Off = tool/thinking lines on Telegram. On = only a short “Still working” ping every 30 seconds. In-app chat still shows full progress.', swt(false), { kw: 'telegram quiet progress still working 30s' }), { id: 'c-engine' })}
  ${card('code', 'Cursor agent', 'Forward real chat messages to Cursor CLI (agent -p --force --trust).', `
    ${row('Enable Cursor bridge for project chats', 'Guide: <a href="cursor-cli-help-window.html">cursor.com/docs/cli/overview</a>', swt(true), { kw: 'enable cursor bridge' })}
    ${row('Agent executable (optional)', 'On Windows GitDeploy can install Cursor CLI for you, or paste the full path to agent.exe.', `<input class="input mono" placeholder="Leave empty to auto-detect agent / cursor-agent" value="C:\\Users\\sharif\\AppData\\Local\\cursor-agent\\agent.cmd">`, { kw: 'cursor agent executable path', ctlCls: 'wide' })}
    ${row('Model (optional)', '', `<input class="input mono" placeholder="e.g. composer-2.5" value="composer-2.5">`, { kw: 'cursor model composer' })}
    ${row('Turn timeout (minutes)', 'How long one Cursor turn (Build / chat) may run before GitDeploy stops it. Default 30. Allowed 5–180.', `<input class="input" style="width:120px" placeholder="30" value="30" data-range="5,180"><div class="hint err vh">Allowed 5–180 minutes.</div>`, { kw: 'cursor turn timeout minutes', ctlCls: 'sm' })}
    <div class="row wrap" style="margin:12px 0 8px">${btn('download', 'Install CLI', 'primary', 'style="min-width:120px" title="Runs: irm \'https://cursor.com/install?win32=true\' | iex" data-working="Installing…" data-reveal="#curInstall"')}${btn('search', 'Detect agent', 'ghost', 'style="min-width:120px" data-working="Detecting…" data-say="#cursorStatus" data-say-pre="Looking for agent / cursor-agent …" data-say-ok="Found: C:\\Users\\sharif\\AppData\\Local\\cursor-agent\\agent.cmd"')}${btn('save', 'Save Cursor', 'ghost', 'style="min-width:120px" data-toast="success" data-title="Cursor settings saved"')}<a class="btn ghost" style="min-width:120px" title="Opens Cursor CLI login + connection test" href="cursor-cli-help-window.html">${ic('key')}<span class="bl">Login &amp; Test</span></a></div>
    ${status('cursorStatus', 'Found: C:\\Users\\sharif\\AppData\\Local\\cursor-agent\\agent.cmd · logged in as dev@subtell.ir')}
    <div id="curInstall" style="margin-top:10px" hidden>${L.worker({ icon: 'download', name: 'Installing Cursor CLI', desc: "irm 'https://cursor.com/install?win32=true' | iex", pct: 31, speed: 1.4, stats: ['PowerShell window', '<span data-elapsed="9">00:00:09</span>'] })}${L.stream([['p', "PS> irm 'https://cursor.com/install?win32=true' | iex"], ['i', 'Downloading cursor-agent 2026.10.28 (win32-x64)…'], ['m', '  ########            34%'], ['m', '  ################    68%'], ['ok', 'Extracted to %LOCALAPPDATA%\\cursor-agent'], ['ok', 'agent.cmd added to PATH']], { interval: 1100, style: 'height:120px;margin-top:8px' })}</div>`, { right: badge('enabled', 'success'), id: 'c-cursor' })}
  ${card('trash', 'Cursor disk cleanup', 'Safe frees backup DB + old agent CLI + UI caches.', `<div class="mono sec" style="font-size:11px" id="curDisk">Backup DB 4.2 GB · old agent CLI 1.1 GB · UI caches 640 MB · live state.vscdb 18.7 GB (kept) · reclaimable ≈ 5.9 GB</div><div class="hint" style="margin:6px 0 10px">Safe frees backup DB + old agent CLI + UI caches. Never deletes live state.vscdb (chats). Also available in Telegram: <span class="mono">/cursorcache</span></div>
    <div class="row wrap">${btn('refresh', 'Refresh', 'ghost', 'style="min-width:100px" data-working="Scanning…"')}${btn('trash', 'Safe clear', 'primary', 'style="min-width:120px" title="Deletes backup DB, old agent CLI builds, UI caches. Never deletes live state.vscdb." data-confirm="Safe clear: delete backup DB, old agent CLI builds and UI caches? Live chats are kept."')}${btn('alert', 'Aggressive', 'ghost', 'style="min-width:110px" title="Safe + snapshots + local edit History. Still keeps live chats DB." data-confirm="Aggressive clear also deletes snapshots and local edit History. Live chats DB is kept."')}</div>
    <div class="hint ok" style="margin-top:8px">Freed 5.9 GB (backup DB, 2 old CLI builds, caches).</div>`, { right: badge('≈ 5.9 GB', 'warning') })}
  ${card('chat', 'Cursor chats by project', 'Only deletes old chat rows (composerHeaders + chat KV). Never touches login / cursorAuth / secrets in ItemTable.', `<div class="mono sec" style="font-size:11px;margin-bottom:10px">4 project(s) · 697 chats · 549 older than 7 days</div>
    <div class="row wrap" style="gap:12px;margin-bottom:10px"><span class="muted">Delete older than</span><select class="select sm" style="width:110px"><option selected>7 days</option><option>14 days</option><option>30 days</option><option>90 days</option></select>${chk('Compact DB (VACUUM)', false, 'title="Optional. Shrinks state.vscdb after delete. Slow on large DBs; needed to reclaim freelist GB."')}${chk('Purge orphan blobs', true, 'title="Deletes agentKv:blob / composer.content hashes not referenced anymore. This is usually most of the 10–20GB bloat."')}</div>
    <div class="card flush" style="border-radius:8px"><div class="grid-wrap" style="max-height:220px"><table class="dg"><thead><tr><th class="chk"><label class="check"><input type="checkbox" data-checkall title="Select / clear all"></label></th><th>Project</th><th class="num">Chats</th><th class="num">Old</th><th>Last activity</th></tr></thead><tbody>
      ${chatRow('GitDeployPro', 301, 244, '2026-11-02 00:31', 1)}${chatRow('subtell', 128, 94, '2026-10-28 21:14', 1)}${chatRow('bourse', 212, 180, '2026-09-30 18:02', 0)}${chatRow('cartex', 56, 31, '2026-10-19 09:47', 1)}</tbody></table></div></div>
    <div class="row wrap" style="margin-top:10px">${btn('refresh', 'Refresh chats', 'ghost', 'style="min-width:110px" data-working="Reading state.vscdb…"')}${btn('trash', 'Delete old chats', 'primary', 'style="min-width:140px" title="Deletes selected projects\' chats older than the chosen age. Cursor must be closed. Backup first. Login/auth is never deleted." data-confirm="Delete chats older than 7 days for 3 selected projects? Cursor must be closed. A backup is made first."')}</div>
    <div style="margin-top:10px">${L.worker({ icon: 'trash', name: 'Deleting old chats — GitDeployPro', desc: 'Backup made · purging orphan blobs · VACUUM pending', pct: 47, speed: 1.2, loop: true, stats: ['<b data-pct>47%</b>', '114 / 244 chats', '<span data-elapsed="31">00:00:31</span>'] })}</div>
    <div class="hint" style="margin-top:6px">(Progress bar + text are collapsed until a delete is running.)  ·  Result: <span class="ok">Deleted 349 chats, freed 11.3 GB.</span></div>`, { right: badge('549 old', 'warning') })}
  ${card('calendar', 'Auto-clean daily', 'Runs once per day at the time below (next run shown under Run now). Enabling does NOT run immediately.', `
    ${row('Auto-clean daily (while GitDeployPro is open)', 'Skips if Cursor is open unless Force-quit is on — Force-quit closes Cursor only at the scheduled time.', swt(true), { kw: 'auto-clean daily cursor' })}
    <div class="row wrap" style="gap:14px;padding:12px 0"><span class="muted">Older than</span><select class="select sm" style="width:100px"><option>7 days</option><option>14 days</option><option selected>30 days</option><option>90 days</option></select><span class="muted">Time</span><input class="input sm mono" style="width:64px;text-align:center" value="03:30" data-range-time>${chk('Orphans', true)}${chk('VACUUM', true)}${chk('Cache + backups', true)}${chk('Force-quit Cursor', false)}</div>
    <div class="row wrap">${btn('play', 'Run now', 'ghost', 'style="min-width:90px" data-working="Cleaning…" data-done="Auto-clean finished" data-done-msg="Freed 2.4 GB"')}<span class="mono muted" style="font-size:11px">Next run: 2026-11-03 03:30 · last run 2026-11-02 03:30 — freed 2.4 GB</span></div>`)}
  ${card('cpu', 'Claude Code agent', 'Forward real chat messages to Claude Code CLI (claude -p, stream-json).', `
    ${row('Enable Claude Code bridge for project chats', 'Install with npm, run <span class="mono">claude</span> once in a terminal to log in, then pick Claude as the engine above. Guide: <a href="claude-cli-help-window.html">Claude Code docs</a>', swt(false), { kw: 'enable claude code bridge' })}
    ${row('Claude executable (optional)', '', `<input class="input mono" placeholder="Leave empty to auto-detect claude">`, { kw: 'claude executable path', ctlCls: 'wide' })}
    ${row('Model (optional)', '', `<input class="input mono" placeholder="sonnet | opus | haiku — empty = Claude Code default" value="sonnet">`, { kw: 'claude model sonnet opus haiku' })}
    ${row('Permission mode', 'Bypass lets Claude edit and run commands unattended.', sel([['Bypass permissions (full access, unattended)', 1], 'Accept edits (file edits only)']), { kw: 'claude permission mode bypass accept edits' })}
    ${row('Max minutes per turn (5-180)', '', `<input class="input" style="width:120px" value="30" data-range="5,180"><div class="hint err vh">Allowed 5–180 minutes.</div>`, { kw: 'claude max minutes per turn timeout', ctlCls: 'sm' })}
    <div class="row wrap" style="margin-top:12px">${btn('download', 'Install CLI', 'primary', 'style="min-width:110px" title="Opens a terminal and runs npm install -g @anthropic-ai/claude-code" data-working="Installing…" data-done="Claude Code installed"')}${btn('search', 'Detect Claude', 'ghost', 'style="min-width:110px" data-working="Detecting…" data-say="#claudeStatus" data-say-pre="Looking for claude …" data-say-ok="Found: C:\\Users\\sharif\\AppData\\Roaming\\npm\\claude.cmd (2.1.4)"')}${btn('save', 'Save Claude', 'ghost', 'style="min-width:110px" data-toast="success" data-title="Claude settings saved"')}<a class="btn ghost" style="min-width:120px" title="Opens Claude Code login + connection test" href="claude-cli-help-window.html">${ic('key')}<span class="bl">Login &amp; Test</span></a></div>
    ${status('claudeStatus', 'Claude Code bridge is disabled.', 'muted')}`, { right: badge('disabled'), id: 'c-claude' })}`;

  /* ---- VPN ---- */
  const pVpn = `
  ${card('globe', 'VPN keep-alive', 'OpenVPN Connect (default) or Community GUI — connect on startup and auto-reconnect.', `
    ${row('Enable VPN keep-alive', 'Monitors the tunnel and reconnects when it drops.', swt(false), { kw: 'enable vpn keep-alive' })}
    ${row('Provider', '', sel([['OpenVPN Connect', 1], 'OpenVPN GUI (Community)']), { kw: 'vpn provider openvpn connect gui community' })}
    ${row('Executable path', '', `<div class="input-group"><input class="input mono" placeholder="C:\\Program Files\\OpenVPN Connect\\OpenVPNConnect.exe" value="C:\\Program Files\\OpenVPN Connect\\OpenVPNConnect.exe"><button class="btn" type="button" data-working="…" data-say="#vpnStatus" data-say-pre="Searching installed VPN clients…" data-say-ok="Found: C:\\Program Files\\OpenVPN Connect\\OpenVPNConnect.exe">Detect</button></div><div class="hint">Not found: “VPN executable was not found. Install OpenVPN Connect / GUI or browse to it.”</div>`, { kw: 'vpn executable path detect', ctlCls: 'wide' })}
    ${row('Profile', 'Not found: “No VPN profiles found. Import a profile in OpenVPN Connect first.”', `<div class="input-group"><select class="select" style="border-radius:8px 0 0 8px"><option>office-tehran (udp/1194)</option><option>home-lab</option></select><button class="btn" type="button" style="border-radius:0 8px 8px 0">Refresh</button></div>`, { kw: 'vpn profile refresh' })}
    ${row('Connect on app startup', '', swt(true), { kw: 'connect on app startup vpn' })}
    ${row('Auto-reconnect when dropped', '', swt(true), { kw: 'auto-reconnect vpn dropped' })}
    ${row('Health check interval (seconds)', '', `<input class="input" style="width:120px" value="30" data-range="5,3600"><div class="hint err vh">Enter 5–3600 seconds.</div>`, { kw: 'health check interval seconds', ctlCls: 'sm' })}
    ${row('Max reconnect attempts (0 = unlimited)', '', `<input class="input" style="width:120px" value="0" placeholder="0 = unlimited">`, { kw: 'max reconnect attempts', ctlCls: 'sm' })}
    ${row('Health probe host (optional)', 'A host reachable only through the tunnel.', `<input class="input mono" placeholder="optional e.g. 10.8.0.1">`, { kw: 'health probe host' })}
    <div class="row wrap" style="margin-top:14px">${btn('play', 'Connect', 'primary', 'style="min-width:96px" data-working="Connecting…" data-say="#vpnStatus" data-say-pre="OpenVPN Connect: connecting to office-tehran …" data-say-ok="OpenVPN Connect: connected (10.8.0.14)"')}${btn('stop', 'Disconnect', 'ghost', 'style="min-width:96px" data-say="#vpnStatus" data-say-pre="Disconnecting…" data-say-ok="OpenVPN Connect: disconnected"')}${btn('refresh', 'Reconnect', 'ghost', 'style="min-width:96px"')}${btn('save', 'Save VPN', 'secondary', 'style="min-width:96px" data-toast="success" data-title="VPN settings saved." data-msg="VPN settings saved."')}</div>
    ${status('vpnStatus', 'VPN keep-alive idle.', 'muted')}
    <div style="margin-top:12px">${L.worker({ icon: 'globe', name: 'VPN health check', desc: 'ping 10.8.0.1 · every 30 s · office-tehran', pct: 40, speed: 3, loop: true, stats: ['next check in <span data-elapsed="12">00:00:12</span>', 'latency 38 ms', '0 drops today'] })}</div>`, { right: badge('off'), id: 'c-vpn' })}`;

  /* ---- THEMES ---- */
  const pThemes = `
  ${card('palette', 'App theme', 'Applies across the whole app and persists after restart', `
    <div class="banner info" style="margin-bottom:14px">${ic('book')}<div class="grow"><b>Theme pack guide</b><div class="muted" style="font-size:12px">THEME_PACK_GUIDE.md ships with the app and lives in your Themes folder for users and tools.</div></div>${btn('book', 'Open guide', 'compact', 'title="Open THEME_PACK_GUIDE.md" data-toast="info" data-title="THEME_PACK_GUIDE.md" data-msg="Opened in the default editor"')}</div>
    ${row('Active theme', 'Pick a pack and press Apply.', `<div class="row"><select class="select" id="themeSel">${THEMES.map((t) => `<option value="${t.id}" ${t.active ? 'selected' : ''}>${t.name}</option>`).join('')}</select>${btn('check', 'Apply', 'primary', 'id="themeApply"')}</div>`, { kw: 'app theme apply combo', ctlCls: 'wide' })}
    <div class="lbl" style="margin:14px 0 6px">Theme packs <span class="muted">(double-click to apply)</span></div>
    <div class="theme-list" id="themeList">${THEMES.map(themeItem).join('')}</div>
    <div class="row wrap" style="margin-top:12px">${btn('download', 'Import theme…', 'secondary', 'data-working="Importing…" data-done="Theme imported" data-done-msg="solarized-night.gdtheme.json added to Themes folder"')}${btn('upload', 'Export selected…', 'ghost', 'data-toast="success" data-title="Theme exported" data-msg="midnight-blue.json"')}${btn('trash', 'Delete custom', 'ghost', 'id="themeDel"')}${btn('folder-open', 'Open folder', 'ghost', 'data-toast="info" data-title="Themes folder" data-msg="Opened in Explorer"')}${btn('refresh', 'Refresh', 'ghost', 'data-working="Scanning…"')}</div>
    <div class="st-status mono" id="themeStatus" style="margin-top:10px">Active: midnight-blue  ·  Guide: THEME_PACK_GUIDE.md  ·  Folder: C:\\Users\\sharif\\AppData\\Roaming\\GitDeployPro\\Themes  ·  4 theme(s)</div>`, { right: badge('Midnight Blue', 'accent'), id: 'c-theme' })}
  ${card('eye', 'Palette', 'Color keys of the selected theme pack', `<div class="row" style="margin-bottom:10px"><b id="palName">Midnight Blue</b><span class="muted mono" id="palCount">38 keys</span></div>${THEMES.map(swatchGrid).join('')}`, { id: 'c-pal' })}`;

  const panels = { general: pGeneral, server: pServer, git: pGit, terminal: pTerminal, telegram: pTelegram, 'ai-agent': pAi, vpn: pVpn, themes: pThemes };

  const css = `
  .st { height: 100%; display: grid; grid-template-rows: auto minmax(0, 1fr) auto; background: var(--surface-base); }
  .st-head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 10px 16px; background: var(--surface-shell); border-bottom: 1px solid var(--border-subtle); }
  .st-head .ico { width: 34px; height: 34px; border-radius: 9px; display: grid; place-items: center; background: var(--surface-raised); border: 1px solid var(--border-subtle); color: var(--accent); flex: none; }
  .st-head h1 { font-size: 16px; } .st-head .sub { color: var(--text-muted); font-size: 11px; }
  .st-head .search { margin-left: auto; width: min(320px, 100%); position: relative; } .st-head .search kbd { position: absolute; right: 8px; top: 50%; transform: translateY(-50%); }
  .kbd { font-family: var(--font-mono); font-size: 10px; padding: 1px 6px; border: 1px solid var(--border-strong); border-bottom-width: 2px; border-radius: 5px; background: var(--surface-input); color: var(--text-secondary); }
  .st-body { display: grid; grid-template-columns: 250px minmax(0, 1fr); min-height: 0; }
  .st-nav { overflow: auto; padding: 12px 8px; background: var(--surface-shell); border-right: 1px solid var(--border-subtle); display: flex; flex-direction: column; gap: 2px; }
  .st-nav .eyebrow { padding: 0 10px 8px; }
  .st-nav button.ni { display: flex; align-items: center; gap: 10px; height: 38px; padding: 0 10px; border: 0; border-left: 2px solid transparent; border-radius: 0 8px 8px 0; background: none; color: var(--text-secondary); text-align: left; position: relative; }
  .st-nav button.ni:hover { background: var(--surface-hover); color: var(--text-primary); }
  .st-nav button.ni.on { background: linear-gradient(90deg, var(--accent-soft), transparent); border-left-color: var(--accent); color: var(--text-primary); font-weight: 600; } .st-nav button.ni.on > .ic { color: var(--accent); }
  .st-nav button.ni > .ic { color: var(--text-muted); } .st-nav .lb { flex: 1; } .st-nav .badge { height: 18px; padding: 0 6px; font-size: 10px; }
  .st-nav .hit { display: none; } .searching .st-nav .hit { display: inline-flex; } .searching .st-nav .nb { display: none; } .st-nav .ni.nohit { opacity: .35; }
  .st-nav .dirty-dot { position: absolute; left: 24px; top: 8px; width: 7px; height: 7px; border-radius: 50%; background: var(--warning); box-shadow: 0 0 6px var(--warning); display: none; } .ni.is-dirty .dirty-dot { display: block; }
  .st-nav .grow { flex: 1; } .st-nav .tip-foot { padding: 10px; color: var(--text-muted); font-size: 11px; border-top: 1px solid var(--border-subtle); margin-top: 8px; }
  .st-main { overflow: auto; padding: 18px clamp(14px, 2.4vw, 36px) 28px; }
  .st-inner { max-width: 980px; margin: 0 auto; } .stpanel { display: none; flex-direction: column; gap: 14px; } .stpanel.on, .searching .stpanel { display: flex; }
  .sh { display: none !important; } .searching .stpanel:not(.has-hit) { display: none; } .stpanel > h2.ph { display: none; font-size: 14px; color: var(--text-muted); text-transform: uppercase; letter-spacing: .1em; } .searching .stpanel > h2.ph { display: flex; align-items: center; gap: 8px; margin-top: 6px; }
  .scard .card-hint { font-size: 11px; } .card-head { flex-wrap: wrap; }
  .scard .card-head .r { margin-left: auto; }
  .form-row.srow { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 24px; }
  .srow .st-txt { flex: 1 1 260px; min-width: 0; } .srow .ctl { flex: 0 1 340px; min-width: min(100%, 200px); } .srow .ctl.wide { flex: 0 1 520px; } .srow .ctl.sm { flex: 0 0 auto; min-width: 0; }
  .srow .ctl .stack.sm { gap: 8px; } .srow .d { max-width: 62ch; } .srow .ctl:has(> .switch) { display: flex; justify-content: flex-end; }
  .st-status { margin-top: 10px; padding: 8px 10px; border-radius: 8px; background: var(--surface-input); border: 1px solid var(--border-subtle); font-size: 11px; color: var(--text-secondary); display: flex; align-items: center; gap: 8px; word-break: break-all; } .st-status.muted { color: var(--text-muted); }
  .ws-card .stat .si { background: var(--accent-soft); }
  .dz { border-color: rgba(255, 92, 106, .4); background: linear-gradient(180deg, var(--error-surface), var(--surface-card) 80%); } .dz .card-head .ic, .dz .card-head h3 { color: var(--error); }
  .dz-box { border: 1px solid var(--border-subtle); border-radius: 8px; padding: 12px; background: var(--surface-input); } .dz-box.info { border-color: rgba(91, 184, 255, .3); background: var(--info-surface); }
  .profs { display: flex; flex-direction: column; gap: 8px; } .prof { display: flex; align-items: center; gap: 6px; padding: 8px 10px; background: var(--surface-input); border: 1px solid var(--border-subtle); border-radius: 8px; } .prof:hover { border-color: var(--border-strong); }
  .prof .btn.icon { font-size: 15px; } .empty-note { margin: 8px 0; padding: 12px; text-align: center; color: var(--text-muted); font-size: 11px; border: 1px dashed var(--border-strong); border-radius: 8px; }
  .ignlist { display: flex; flex-direction: column; gap: 6px; } .ign { display: flex; align-items: center; gap: 8px; padding: 4px 6px 4px 10px; border: 1px solid var(--border-subtle); border-radius: 8px; background: var(--surface-input); }
  .srow .ctl .hint { margin-top: 6px; }
  input.invalid ~ .vh, input.invalid + .vh { display: block; } .vh { display: none; }
  .theme-list { display: flex; flex-direction: column; gap: 8px; max-height: 340px; overflow: auto; }
  .theme-item { display: flex; align-items: center; gap: 14px; padding: 10px 12px; background: var(--surface-input); border: 1px solid var(--border-subtle); border-radius: 10px; } .theme-item:hover { border-color: var(--border-strong); background: var(--surface-raised); }
  .theme-item.sel { border-color: var(--accent); background: var(--accent-soft); box-shadow: 0 0 0 3px var(--accent-soft); } .theme-item .t { font-weight: 600; } .theme-item .d { color: var(--text-muted); font-size: 11px; }
  .theme-prev { width: 76px; height: 48px; border: 1px solid; border-radius: 8px; position: relative; overflow: hidden; flex: none; } .theme-prev i { position: absolute; left: 0; top: 0; bottom: 0; width: 14px; } .theme-prev b { position: absolute; left: 20px; top: 8px; right: 6px; height: 24px; border: 1px solid; border-radius: 5px; padding: 4px; display: flex; gap: 3px; flex-direction: column; } .theme-prev b em { height: 3px; border-radius: 2px; width: 60%; } .theme-prev b em + em { width: 85%; } .theme-prev u { position: absolute; right: 7px; bottom: 3px; text-decoration: none; font-size: 11px; font-weight: 700; }
  .theme-dots { display: flex; gap: 3px; flex: none; } .theme-dots i { width: 14px; height: 14px; border-radius: 50%; border: 1px solid var(--border-strong); }
  .pal-g { margin-bottom: 14px; } .pal-g .eyebrow { margin-bottom: 6px; } .pal-row { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 6px; }
  .sw { display: flex; align-items: center; gap: 8px; padding: 5px 8px 5px 5px; border: 1px solid var(--border-subtle); border-radius: 8px; background: var(--surface-input); min-width: 0; }
  .sw i { width: 28px; height: 28px; border-radius: 6px; flex: none; border: 1px solid rgba(255, 255, 255, .15); background-clip: padding-box; } .sw span { display: flex; flex-direction: column; min-width: 0; font-size: 11px; line-height: 1.25; } .sw b { font-weight: 600; color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; } .sw small { color: var(--text-muted); font-size: 10px; }
  .st-bar { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 10px 16px; background: var(--surface-shell); border-top: 1px solid var(--border-subtle); position: relative; z-index: 3; box-shadow: 0 -10px 24px rgba(0, 0, 0, .35); }
  .st-bar .msg { color: var(--text-muted); font-size: 12px; } .st-bar .dirty { display: inline-flex; align-items: center; gap: 8px; font-size: 12px; font-weight: 600; color: var(--text-muted); } .st-bar.is-dirty .dirty { color: var(--warning); } .st-bar .dirty .dot { background: var(--success); } .st-bar.is-dirty .dirty .dot { background: var(--warning); animation: pulse 1.2s infinite; }
  .st-bar .grow { flex: 1; } .st-empty { display: none; } .searching.none .st-empty { display: grid; }
  .ts-panel .check { font-size: 12px; } .ts-switches .form-row { padding: 8px 0; } .ts-panel .input-icon .input { padding-left: 34px; }
  .scard.collapsible { }
  @media (max-width: 1099.98px) { .st-body { grid-template-columns: 64px minmax(0, 1fr); } .st-nav .lb, .st-nav .badge, .st-nav .eyebrow, .st-nav .tip-foot { display: none; } .st-nav button.ni { justify-content: center; padding: 0; border-radius: 8px; } .st-nav .dirty-dot { left: 36px; } .st-head .search { margin-left: 0; width: 100%; order: 5; } }
  @media (max-height: 700px) { .st-head { padding: 6px 14px; } .st-bar { padding: 6px 14px; } }
  `;

  const script = `<script>(function(){
  var st=document.getElementById('st'),main=document.getElementById('stMain'),q=document.getElementById('stSearch'),bar=document.getElementById('stBar'),sub=document.getElementById('stSub'),none=document.getElementById('stNone');
  var dirty=new Set(),orig=new Map();
  var SUB=${JSON.stringify(Object.fromEntries(SECTIONS.map((s) => [s[0], s[3]])))};
  var $=function(s,r){return (r||document).querySelector(s)},$$=function(s,r){return [].slice.call((r||document).querySelectorAll(s))};
  function show(id){$$('.stpanel').forEach(function(p){p.classList.toggle('on',p.id==='sp-'+id)});$$('.ni').forEach(function(n){n.classList.toggle('on',n.dataset.sec===id)});if(sub)sub.textContent=SUB[id]||'';main.scrollTop=0;if(history.replaceState)history.replaceState(null,'','#'+id);}
  $$('.ni').forEach(function(n){n.addEventListener('click',function(){q.value='';search();show(n.dataset.sec);});});
  var h=(location.hash||'').slice(1);show(SUB[h]?h:'general');
  // ---- search settings
  function search(){var v=q.value.trim().toLowerCase();st.classList.toggle('searching',!!v);var total=0;
    function sh(el,hide){el.classList.toggle('sh',!!hide);}
    $$('.stpanel').forEach(function(p){var hits=0;
      $$('.scard',p).forEach(function(c){var head=(($('.card-head',c)||{}).textContent||'').toLowerCase(),cm=!v||head.indexOf(v)>-1,rows=$$('.srow',c),others=$$(':scope > :not(.card-head):not(.srow)',c),vis=false;
        rows.forEach(function(r){var t=((r.dataset.k||'')+' '+r.textContent).toLowerCase(),ok=cm||t.indexOf(v)>-1;sh(r,!ok);if(ok){vis=true;hits++;}});
        others.forEach(function(o){var ok=cm||o.textContent.toLowerCase().indexOf(v)>-1;if(rows.length)sh(o,!ok);else sh(o,false);if(ok&&!vis){vis=true;hits++;}});
        if(!rows.length&&!others.length)vis=cm;
        sh(c,!!v&&!vis);});
      p.classList.toggle('has-hit',!v||hits>0);total+=hits;var n=$('.ni[data-sec="'+p.id.slice(3)+'"]');if(n){$('.hit',n).textContent=hits;n.classList.toggle('nohit',!!v&&!hits);}});
    st.classList.toggle('none',!!v&&!total);
    if(sub)sub.textContent=v?(total+' match(es) for “'+q.value.trim()+'”'):SUB[($('.ni.on')||{dataset:{}}).dataset.sec]||'';}
  q.addEventListener('input',search);
  document.addEventListener('keydown',function(e){if(e.key==='/'&&document.activeElement.tagName!=='INPUT'&&document.activeElement.tagName!=='TEXTAREA'&&document.activeElement.tagName!=='SELECT'){e.preventDefault();q.focus();}
    if(e.key==='Escape'&&document.activeElement===q){q.value='';search();q.blur();}
    if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='s'){e.preventDefault();save();}});
  // ---- dirty tracking
  $$('#stMain input,#stMain select,#stMain textarea').forEach(function(el){orig.set(el,el.type==='checkbox'||el.type==='radio'?el.checked:el.value);});
  function upd(){var secs={};dirty.forEach(function(el){var p=el.closest('.stpanel');if(p)secs[p.id.slice(3)]=1;});
    $$('.ni').forEach(function(n){n.classList.toggle('is-dirty',!!secs[n.dataset.sec]);});
    bar.classList.toggle('is-dirty',dirty.size>0);$('#stDirtyTxt').textContent=dirty.size?(dirty.size+' unsaved change'+(dirty.size>1?'s':'')+' in '+Object.keys(secs).length+' section(s)'):'All changes saved';
    $('#stSave').disabled=!dirty.size;$('#stReset').disabled=!dirty.size;$('#stSave').classList.toggle('disabled',!dirty.size);$('#stReset').classList.toggle('disabled',!dirty.size);}
  function cur(el){return el.type==='checkbox'||el.type==='radio'?el.checked:el.value;}
  function chg(e){var el=e.target;if(!orig.has(el)||el.id==='stSearch'||el.hasAttribute('data-nodirty'))return;if(el.type==='radio'){$$('input[type=radio][name="'+el.name+'"]').forEach(function(r){if(cur(r)===orig.get(r))dirty.delete(r);else dirty.add(r);});}else if(cur(el)===orig.get(el))dirty.delete(el);else dirty.add(el);upd();}
  main.addEventListener('input',chg);main.addEventListener('change',chg);
  function save(){if(!dirty.size)return;if($('#stMain input.invalid')){toast({type:'error',title:'Fix highlighted fields',msg:'Some values are out of range.'});return;}
    orig.forEach(function(v,el){orig.set(el,cur(el));});dirty.clear();upd();toast({type:'success',title:'Settings saved',msg:'Applied to the current project and global preferences.'});}
  function reset(){orig.forEach(function(v,el){if(el.type==='checkbox'||el.type==='radio')el.checked=v;else el.value=v;el.classList.remove('invalid');});dirty.clear();upd();toast({type:'info',title:'Changes discarded',msg:'Settings reloaded from disk.'});}
  $('#stSave').addEventListener('click',save);$('#stReset').addEventListener('click',reset);upd();
  // ---- validation  data-range="min,max"
  main.addEventListener('input',function(e){var el=e.target,r=el.dataset&&el.dataset.range;if(!r)return;var a=r.split(','),v=parseInt(el.value,10);el.classList.toggle('invalid',!(v>=+a[0]&&v<=+a[1]));});
  // ---- mirror project path
  main.addEventListener('input',function(e){var m=e.target.dataset&&e.target.dataset.mirror;if(m){$(m).textContent=e.target.value||'—';$('#dzPath').textContent=e.target.value||'No project selected.';e.target.classList.toggle('invalid',!e.target.value);var v=e.target.closest('.ctl').querySelector('.vh');if(v)v.hidden=!!e.target.value;}});
  // ---- remove rows (profiles / ignore patterns)
  main.addEventListener('click',function(e){var b=e.target.closest('[data-remove]');if(!b)return;var r=b.closest('.prof,.ign'),l=r.parentNode;r.remove();var em=$('[data-empty="#'+l.id+'"]');if(em)em.hidden=!!l.children.length;
    var x=document.createElement('span');dirty.add(l);upd();});
  // ---- select-all
  main.addEventListener('change',function(e){if(e.target.hasAttribute('data-checkall'))$$('tbody input[type=checkbox]',e.target.closest('table')).forEach(function(c){c.checked=e.target.checked;});});
  // ---- reveal blocks / say status
  document.addEventListener('click',function(e){var r=e.target.closest('[data-reveal]');if(r){var t=$(r.dataset.reveal);if(t)t.hidden=false;}
    var s=e.target.closest('[data-say-ok]');if(s){var o=$(s.dataset.say);if(o){o.innerHTML='<svg class="ic ic-sm spin"><use href="#i-spinner"/></svg> '+(s.dataset.sayPre||'Working…');setTimeout(function(){o.textContent=s.dataset.sayOk;},1600);}}
    var s2=e.target.closest('[data-say]');if(s2&&s2.dataset.say.charAt(0)!=='#'){}
  });
  // ---- terminal appearance preview
  var tf=$('#termFont'),tc=$('#termColor'),tp=$('#termPrev');function tpv(){tp.style.fontSize=tf.value+'px';tp.style.color=tc.value;}
  if(tf){tf.addEventListener('change',tpv);tc.addEventListener('change',tpv);tpv();}
  // ---- themes
  var tl=$('#themeList'),ts=$('#themeSel');
  function pick(id){$$('.theme-item',tl).forEach(function(i){i.classList.toggle('sel',i.dataset.theme===id)});ts.value=id;var it=$('.theme-item[data-theme="'+id+'"]');$$('.pal').forEach(function(p){p.hidden=p.dataset.pal!==id});$('#palName').textContent=it.dataset.name;$('#themeDel').classList.toggle('disabled',it.dataset.kind!=='custom');}
  tl.addEventListener('click',function(e){var i=e.target.closest('.theme-item');if(i)pick(i.dataset.theme);});
  ts.addEventListener('change',function(){pick(ts.value)});
  function apply(){var id=ts.value,it=$('.theme-item[data-theme="'+id+'"]');$$('.theme-item .act').forEach(function(a){a.hidden=true});$('.act',it).hidden=false;$('#themeStatus').textContent=$('#themeStatus').textContent.replace(/Active: [^ ]+/,'Active: '+id);
    $$('.ni[data-sec="themes"] .nb').forEach(function(b){b.textContent=it.dataset.name.split(' ')[0]});toast({type:'success',title:'Theme applied',msg:it.dataset.name+' is now the app theme (kept after restart).'});}
  $('#themeApply').addEventListener('click',apply);tl.addEventListener('dblclick',function(e){var i=e.target.closest('.theme-item');if(i){pick(i.dataset.theme);apply();}});
  pick('midnight-blue');
  })();</script>`;

  const settingsBody = `<div class="st" id="st">
  <div class="st-head"><span class="ico">${ic('settings', 'ic-lg')}</span><div><h1>Settings</h1><div class="sub" id="stSub">Project path, startup, updates, and danger zone.</div></div>
    <div class="search input-icon">${ic('search')}<input class="input" id="stSearch" placeholder="Search settings…" style="padding-right:36px" autocomplete="off" data-nodirty><kbd class="kbd">/</kbd></div></div>
  <div class="st-body">
    <nav class="st-nav" aria-label="Settings sections"><div class="eyebrow">Sections</div>
      ${SECTIONS.map(([id, icon, label]) => `<button type="button" class="ni ${id === 'general' ? 'on' : ''}" data-sec="${id}" title="${label}">${ic(icon)}<span class="lb">${label}</span><span class="nb">${NAVB[id]}</span><span class="badge hit accent">0</span><i class="dirty-dot" title="Unsaved changes"></i></button>`).join('')}
      <div class="grow"></div><div class="tip-foot">Press <kbd class="kbd">/</kbd> to search · <kbd class="kbd">Ctrl</kbd>+<kbd class="kbd">S</kbd> to save<br><a href="about.html">About GitDeploy Pro</a></div></nav>
    <div class="st-main" id="stMain"><div class="st-inner">
      ${SECTIONS.map(([id, icon, label]) => `<div class="stpanel ${id === 'general' ? 'on' : ''}" id="sp-${id}"><h2 class="ph">${ic(icon)}${label}</h2>${panels[id]}</div>`).join('')}
      <div class="empty st-empty">${ic('search')}<b>No settings match your search</b><span>Try “token”, “tray”, “branch” or “timeout”.</span></div>
    </div></div>
  </div>
  <div class="st-bar" id="stBar"><span class="dirty"><span class="dot"></span><span id="stDirtyTxt">All changes saved</span></span><span class="msg">Changes apply to the current project and global preferences.</span><span class="grow"></span>
    <button class="btn" type="button" id="stReset" disabled>${ic('undo')}<span class="bl">Discard</span></button><button class="btn primary" type="button" id="stSave" style="min-width:140px" disabled>${ic('save')}<span class="bl">Save settings</span><kbd class="kbd" style="background:rgba(0,0,0,.25);border-color:rgba(255,255,255,.25);color:#fff">Ctrl+S</kbd></button></div>
  </div>${script}${TS_SCRIPT}`;

  const profCtx = `<div class="ctx" id="profCtx"><div class="menu"><a class="mi" data-toast="success" data-title="Default FTP changed">${ic('star')}Set as default</a><a class="mi" href="connection-manager-window.html">${ic('edit')}Edit profile…</a><div class="msep"></div><a class="mi danger" data-confirm="Remove this FTP from the project?">${ic('trash')}Remove from project</a></div></div>`;

  L.page({ slug: 'settings', title: 'Settings', nav: 'settings', group: 'Pages', flush: true, css, body: settingsBody, dialogs: tsCtx + profCtx + sugModal });

  /* ================================================================ TerminalSuggestionsSettingsPanel (control) */
  L.page({
    slug: 'terminal-suggestions-settings-panel', title: 'Terminal Suggestions Settings Panel', nav: 'settings', group: 'Controls',
    css: `.ts-panel .check{font-size:12px}.ts-switches .form-row{display:flex;flex-wrap:wrap;gap:8px 24px;align-items:center;justify-content:space-between}.ts-switches .st-txt{flex:1 1 240px}.ts-panel .input-icon .input{padding-left:34px}.kbd{font-family:var(--font-mono);font-size:10px;padding:1px 6px;border:1px solid var(--border-strong);border-bottom-width:2px;border-radius:5px;background:var(--surface-input);color:var(--text-secondary)}.st-status{margin-top:10px;padding:8px 10px;border-radius:8px;background:var(--surface-input);border:1px solid var(--border-subtle);font-size:11px;color:var(--text-muted)}.srow .t{font-weight:600}.srow .d{color:var(--text-muted);font-size:12px}`,
    body: `<div class="page-inner"><div class="page-head"><div><div class="eyebrow">Controls · embedded in Settings → Terminal</div><h1 class="page-title">Terminal suggestions</h1><div class="page-sub">Autocomplete commands with ghost text · Global or per-project scope</div></div><div class="r"><a class="btn" href="settings.html#terminal">${ic('settings')}<span class="bl">Back to Settings</span></a></div></div>
      <div class="card" style="max-width:980px">${tsPanel('p')}</div></div>${TS_SCRIPT}`,
    dialogs: tsCtx + sugModal,
  });

  /* ================================================================ Terminal suggestion dialog (window) */
  L.window({ slug: 'terminal-suggestion-dialog', title: 'Terminal command', icon: 'terminal', w: 520, h: 540, group: 'Windows',
    css: `.hint.err[hidden]{display:none}`,
    body: sugDialogBody,
    footer: `<button class="btn primary" style="width:90px" data-toast="success" data-title="Command saved" data-msg="php artisan migrate --force">Save</button><button class="btn ghost" style="width:90px">Cancel</button>` });

  /* ================================================================ ABOUT */
  const abState = (ico, t, d, cls) => `<div class="list-item"><div class="lead" style="${cls}">${ico}</div><div class="grow"><div class="t">${t}</div><div class="d">${d}</div></div></div>`;
  L.page({ slug: 'about', title: 'About', nav: 'about', group: 'Pages',
    css: `.ab-grid{display:grid;grid-template-columns:minmax(0,560px) minmax(0,1fr);gap:14px;align-items:start}@media(max-width:1199.98px){.ab-grid{grid-template-columns:minmax(0,1fr)}}.ab-logo{width:56px;height:56px;border-radius:15px;display:grid;place-items:center;font-size:26px;font-weight:800;color:#fff;background:linear-gradient(135deg,var(--accent),var(--accent2));box-shadow:0 8px 28px -8px var(--accent-glow);flex:none}.ab-card .mono{font-size:12px}`,
    body: `<div class="page-inner"><div class="page-head"><div><h1 class="page-title" style="font-size:20px">About</h1><div class="page-sub" style="font-size:12px">App version and software updates.</div></div></div>
    <div class="ab-grid"><div class="stack">
      <section class="card ab-card"><div class="row" style="gap:14px;margin-bottom:12px"><span class="ab-logo">G</span><div><div style="font-size:18px;font-weight:600">GitDeploy Pro</div><div class="muted" style="font-size:12px">Git + FTP/SFTP deploy for programmers</div></div></div>
        <div style="margin-top:8px">Version: <b class="mono">2.4.0</b> ${badge('stable', 'success')}</div>
        <div class="muted" style="font-size:11px;margin-top:4px" id="lastChk">Last automatic check: 2026-11-02 00:39</div>
        <div class="sec mono" style="font-size:11px;margin-top:10px;word-break:break-all">Install path: C:\\Program Files\\GitDeployPro</div>
        <div class="muted mono" style="font-size:11px;margin-top:4px;word-break:break-all">Running from: C:\\Program Files\\GitDeployPro\\GitDeployPro.exe</div>
        <div class="row wrap" style="margin-top:16px"><button class="btn primary" type="button" style="min-width:180px" id="abChk">${ic('refresh')}<span class="bl">Check for updates now</span></button><button class="btn ghost" type="button" style="min-width:140px" data-toast="info" data-title="Install folder" data-msg="Opened C:\\Program Files\\GitDeployPro in Explorer">${ic('folder-open')}<span class="bl">Open install folder</span></button></div>
        <div id="abRes" style="margin-top:14px" hidden></div></section></div>
      <section class="card"><div class="card-head">${ic('refresh')}<h3>Update check states</h3><div class="r">${badge('what the buttons lead to', '')}</div></div><div class="list">
        ${abState(ic('spinner', 'spin'), 'Checking…', 'Contacting the update server · <span class="mono">https://updates.gitdeploy.pro/manifest.json</span>', 'background:var(--accent-soft);color:var(--accent)')}
        ${abState(ic('check-circle'), 'Up to date', 'v2.4.0 is the latest version.', 'background:var(--success-surface);color:var(--success)')}
        ${abState(ic('download'), 'Update available — v2.5.0', 'Opens the <a href="update-status-window.html">Software Update window</a>; the download runs in the background.', 'background:var(--accent-soft);color:var(--accent)')}
        ${abState(ic('alert'), 'Critical update required', 'Mandatory update — Later becomes Exit. See <a href="update-available-window.html">Update available</a>.', 'background:var(--warning-surface);color:var(--warning)')}
        ${abState(ic('x-circle'), 'Update check failed', 'Could not reach the update server (timeout after 15 s).', 'background:var(--error-surface);color:var(--error)')}
        ${abState(ic('star'), "What's new", 'Shown once after an update — <a href="whats-new-window.html">release notes</a>.', 'background:var(--success-surface);color:var(--success)')}
        ${abState(ic('hard-drive'), 'Install location', 'First run only — <a href="install-location-window.html">choose install folder</a>.', 'background:var(--info-surface);color:var(--info)')}</div></section></div></div>
    <script>(function(){var b=document.getElementById('abChk'),r=document.getElementById('abRes');b.addEventListener('click',function(){if(b.classList.contains('working'))return;b.classList.add('working');r.hidden=false;r.innerHTML='<div class="banner info"><svg class="ic spin"><use href="#i-spinner"/></svg><span>Checking for updates…</span></div>';setTimeout(function(){b.classList.remove('working');r.innerHTML='<div class="banner success"><svg class="ic"><use href="#i-check-circle"/></svg><span>You are up to date — v2.4.0.</span></div>';document.getElementById('lastChk').textContent='Last automatic check: '+new Date().toISOString().slice(0,10)+' '+new Date().toTimeString().slice(0,5);},1800);});})();</script>` });

  /* ================================================================ UPDATE STATUS WINDOW (with state switcher) */
  const changelog = ['Terminal suggestions: import thousands of commands from JSON', 'Settings page redesigned with search and dirty-state indicator', 'Faster FTP uploads: default 4 parallel workers', 'Fix: Telegram long-poll no longer stops after VPN reconnect', 'Cursor chats cleanup: purge orphan blobs'];
  const clList = (items) => `<div class="stack sm">${items.map((x) => `<div class="row top" style="gap:10px"><b class="accent" style="font-size:14px;line-height:1.3">•</b><span style="font-size:13px">${x}</span></div>`).join('')}</div>`;
  L.window({ slug: 'update-status-window', title: 'Software Update', icon: 'download', w: 520, h: 520, group: 'Windows',
    css: `.us-ico{width:48px;height:48px;border-radius:14px;display:grid;place-items:center;font-size:22px;font-weight:600;background:var(--success-surface);color:var(--accent);flex:none}.us-sw{display:flex;gap:6px;flex-wrap:wrap;margin-bottom:14px;padding:8px;border:1px dashed var(--border-strong);border-radius:10px}.us-sw .eyebrow{width:100%}`,
    body: `<div class="us-sw"><div class="eyebrow">Preview state (the real window switches by itself)</div>${['available', 'critical', 'downloading', 'ready', 'failed'].map((s, i) => `<button type="button" class="chip ${i === 0 ? 'active' : ''}" data-st="${s}">${s}</button>`).join('')}</div>
    <div class="row" style="gap:14px"><div class="us-ico" id="usIco">⬆</div><div><div style="font-size:22px;font-weight:600" id="usTitle">Update available</div><div class="sec" id="usVer" style="font-size:13px;margin-top:4px">Version 2.4.0  →  2.5.0</div></div></div>
    <div class="sec" id="usDetail" style="margin-top:14px;font-size:13px">Download runs in the background — keep using the app, then Restart when ready.</div>
    <div class="card" style="margin-top:16px;border-radius:12px;padding:12px 14px"><div class="muted" style="font-size:12px;font-weight:600;margin-bottom:8px">What's included</div><div style="max-height:150px;overflow:auto">${clList(changelog)}</div></div>
    <div id="usProg" style="margin-top:16px" hidden><div class="progress lg busy" id="usBar"><i style="width:0%"></i></div><div class="muted" id="usPt" style="margin-top:8px;font-size:12px">Starting download…</div><div class="muted mono" id="usStats" style="margin-top:4px;font-size:11px">GitDeployPro-2.5.0-win-x64.zip · 0 / 48.2 MB</div></div>
    <script>(function(){var S={available:['⬆','Update available','Version 2.4.0  →  2.5.0','Download runs in the background — keep using the app, then Restart when ready.','Later','Download update',0],critical:['⬆','Critical update required','Version 2.4.0  →  2.5.0','Download in the background, then Restart to install. This update should be applied before you continue long-term.','Exit','Download update',0],downloading:['↓','Downloading update','Version 2.5.0','You can keep using GitDeploy Pro while this downloads.','Cancel','',1],ready:['✓','Update ready to install','Version 2.5.0','Restart to install into your install folder. You can keep working until then.','Later','Restart now',2],failed:['!','Update download failed','Version 2.5.0','Could not download GitDeployPro-2.5.0-win-x64.zip: The operation timed out.','Dismiss','',3]};
    var $=function(i){return document.getElementById(i)},pv=0,tm;function set(k){var s=S[k];clearInterval(tm);$('usIco').textContent=s[0];$('usIco').style.background=k==='failed'?'var(--error-surface)':'var(--success-surface)';$('usIco').style.color=k==='failed'?'var(--error)':k==='ready'?'var(--success)':'var(--accent)';$('usTitle').textContent=s[1];$('usVer').textContent=s[2];$('usDetail').textContent=s[3];$('usSec').querySelector('.bl').textContent=s[4];var p=$('usPri');p.hidden=!s[5];if(s[5])p.querySelector('.bl').textContent=s[5];
      $('usProg').hidden=!(s[6]===1||s[6]===2);var bar=$('usBar');bar.classList.toggle('busy',s[6]===1);bar.classList.toggle('success',s[6]===2);
      if(s[6]===2){bar.firstChild.style.width='100%';$('usPt').textContent='Download complete';$('usStats').textContent='GitDeployPro-2.5.0-win-x64.zip · 48.2 / 48.2 MB · sha256 verified';}
      if(s[6]===1){pv=0;$('usPt').textContent='Starting download…';tm=setInterval(function(){pv=Math.min(100,pv+1+Math.random()*3);bar.firstChild.style.width=pv+'%';$('usPt').textContent=pv<2?'Starting download…':'Downloading… '+Math.floor(pv)+'%';$('usStats').textContent='GitDeployPro-2.5.0-win-x64.zip · '+(48.2*pv/100).toFixed(1)+' / 48.2 MB · '+(2.1+Math.random()).toFixed(1)+' MB/s';if(pv>=100){clearInterval(tm);set('ready');}},500);}}
    document.querySelector('.us-sw').addEventListener('click',function(e){var b=e.target.closest('[data-st]');if(!b)return;[].forEach.call(document.querySelectorAll('.us-sw .chip'),function(c){c.classList.toggle('active',c===b)});set(b.dataset.st);});
    document.addEventListener('click',function(e){var p=e.target.closest('#usPri');if(p&&$('usPri').querySelector('.bl').textContent==='Download update')document.querySelector('[data-st=downloading]').click();});set('available');})();</script>`,
    footer: `<span class="l mono" id="usFoot">v2.4.0 → v2.5.0</span><button class="btn ghost" id="usSec" style="min-width:110px;height:36px"><span class="bl">Later</span></button><button class="btn primary" id="usPri" style="min-width:130px;height:36px"><span class="bl">Download update</span></button>` });

  /* ================================================================ UPDATE AVAILABLE */
  L.window({ slug: 'update-available-window', title: 'Software Update', icon: 'download', w: 480, h: 380, group: 'Windows',
    body: `<div style="font-size:20px;font-weight:600">Update available</div><div class="sec" style="margin-top:10px;font-size:13px">Version 2.5.0 is available (you have 2.4.0).</div>
    <div class="card" style="margin-top:14px;border-radius:10px;padding:12px;max-height:190px;overflow:auto;background:var(--surface-card)"><div class="sec" style="font-size:12px;white-space:pre-line">v2.5.0 — 2026-11-02
- Terminal suggestions: bulk import from JSON
- Settings: search, dirty-state, theme palette preview
- Faster FTP uploads (default 4 workers)
- Fix: Telegram long-poll after VPN reconnect
- Fix: Cursor chats cleanup crash on empty DB</div></div>`,
    footer: `<button class="btn ghost" style="min-width:110px;height:34px">Later</button><a class="btn primary" style="min-width:120px;height:34px" href="update-progress-window.html">Update now</a>` });

  /* ================================================================ UPDATE PROGRESS */
  L.window({ slug: 'update-progress-window', title: 'Downloading update', icon: 'download', w: 420, h: 250, group: 'Windows',
    body: `<div style="font-size:16px;font-weight:600">Downloading update...</div><div class="sec" id="upSt" style="margin-top:10px;font-size:12px">Preparing download...</div>
    <div class="progress lg busy indeterminate" id="upBar" style="margin-top:16px;height:10px"><i style="width:35%"></i></div>
    <div class="row between mono muted" style="margin-top:10px;font-size:11px"><span id="upSz">0.0 / 48.2 MB</span><span id="upSp">—</span><span>elapsed <span data-elapsed="0">00:00:00</span></span></div>
    <script>(function(){var b=document.getElementById('upBar'),i=b.firstChild,v=0,t=setTimeout(function(){b.classList.remove('indeterminate');setInterval(function(){v=Math.min(100,v+1+Math.random()*4);i.style.width=v+'%';document.getElementById('upSt').textContent=v>=100?'Download complete':'Downloading… '+Math.floor(v)+'%';document.getElementById('upSz').textContent=(48.2*v/100).toFixed(1)+' / 48.2 MB';document.getElementById('upSp').textContent=v>=100?'done':(2+Math.random()).toFixed(1)+' MB/s';if(v>=100){b.classList.remove('busy');b.classList.add('success');}},500);},2200);})();</script>`,
    footer: `<span class="l">Indeterminate while preparing, then real percent</span><button class="btn ghost" data-confirm="Cancel the update download?">Cancel</button>` });

  /* ================================================================ WHAT'S NEW */
  L.window({ slug: 'whats-new-window', title: "What's New", icon: 'star', w: 520, h: 460, group: 'Windows',
    body: `<div class="row" style="gap:12px"><div style="width:42px;height:42px;border-radius:12px;display:grid;place-items:center;background:var(--success-surface);color:var(--success);flex:none">${ic('star', 'ic-lg')}</div><div><div style="font-size:22px;font-weight:600">What's New</div><div class="ok" style="font-size:13px;margin-top:2px">Version 2.5.0</div></div></div>
    <div class="sec" style="margin-top:14px;font-size:13px">Thanks for updating. Here’s what changed in this release:</div>
    <div class="card" style="margin-top:14px;border-radius:12px;padding:12px 14px;max-height:230px;overflow:auto">${clList(changelog)}</div>`,
    footer: `<button class="btn primary" style="min-width:130px;height:36px">Got it</button>` });

  /* ================================================================ INSTALL LOCATION */
  L.window({ slug: 'install-location-window', title: 'Install GitDeploy Pro', icon: 'hard-drive', w: 520, h: 380, group: 'Windows',
    css: `.il-low .input{border-color:var(--warning)}`,
    body: `<div style="font-size:20px;font-weight:600">Choose install location</div><div class="sec" style="margin-top:8px;font-size:13px">Pick a folder for GitDeploy Pro (useful when C: is full). Updates will always overwrite the same EXE in this folder.</div>
    <div class="row" style="margin-top:16px"><input class="input mono" id="ilPath" readonly value="D:\\Apps\\GitDeployPro" style="height:34px"><button class="btn ghost" type="button" id="ilBrowse" style="min-width:96px;height:34px">Browse…</button></div>
    <div class="muted" id="ilFree" style="margin-top:10px;font-size:12px">Free space on this drive: 214.6 GB</div>
    <div class="warn" id="ilStat" style="margin-top:10px;font-size:12px" hidden></div>
    <div class="row" style="margin-top:14px;gap:8px">${badge('~85 MB needed', 'info')}${badge('EXE: GitDeployPro.exe')}</div>
    <script>(function(){var d=[['D:\\\\Apps\\\\GitDeployPro','Free space on this drive: 214.6 GB',0],['C:\\\\Program Files\\\\GitDeployPro','Free space on this drive: 41 MB (need ~120 MB)',1],['E:\\\\Tools\\\\GitDeployPro','Free space: unknown',0]],i=0;document.getElementById('ilBrowse').addEventListener('click',function(){i=(i+1)%d.length;var f=document.getElementById('ilFree');document.getElementById('ilPath').value=d[i][0].replace(/\\\\\\\\/g,'\\\\');f.textContent=d[i][1];f.style.color=d[i][2]?'var(--warning)':'';document.getElementById('ilInstall').classList.toggle('disabled',!!d[i][2]);});})();</script>`,
    footer: `<span class="l">Click Browse… to cycle through the drive states</span><button class="btn ghost" style="min-width:120px;height:34px" data-toast="info" data-title="Portable mode" data-msg="Running from the current folder, nothing installed">Run portable</button><button class="btn primary" id="ilInstall" style="min-width:120px;height:34px" data-toast="success" data-title="Installed" data-msg="Copied to D:\\Apps\\GitDeployPro">Install here</button>` });

  /* ================================================================ CLAUDE CLI HELP */
  const testBox = (hint) => `<div class="card raised" style="margin-top:12px;border-radius:10px;padding:10px 12px"><div class="sec mono" id="hlpRes" style="font-size:11px;min-height:20px;white-space:pre-wrap">${hint}</div></div>`;
  const helpScript = (okMsg, failMsg, hint, testing) => `<script>(function(){var r=document.getElementById('hlpRes'),b=document.getElementById('hlpTest'),n=0;
    document.getElementById('hlpLogin').addEventListener('click',function(){r.style.color='var(--info)';r.textContent=${JSON.stringify(okMsg.login)};});
    b.addEventListener('click',function(){if(b.classList.contains('working'))return;b.classList.add('working');r.style.color='var(--text-muted)';r.innerHTML='<svg class="ic ic-sm spin"><use href="#i-spinner"/></svg> '+${JSON.stringify(testing)};
      setTimeout(function(){b.classList.remove('working');if(n++%2===0){r.style.color='var(--success)';r.textContent=${JSON.stringify(okMsg.ok)};}else{r.style.color='var(--error)';r.textContent=${JSON.stringify(failMsg)};}},1800);});})();</script>`;
  L.window({ slug: 'claude-cli-help-window', title: 'Claude Code CLI', icon: 'cpu', w: 600, h: 560, group: 'Windows',
    body: `<div class="row top" style="gap:12px"><div style="width:42px;height:42px;border-radius:12px;display:grid;place-items:center;background:var(--info-surface);color:var(--info);flex:none">${ic('cpu', 'ic-lg')}</div><div><div style="font-size:20px;font-weight:600">Claude Code CLI</div><div class="muted" style="font-size:12px;margin-top:4px">Sign in once on this PC, then test the connection.</div></div></div>
    <div class="card" style="margin-top:16px;border-radius:12px;padding:12px 14px;max-height:200px;overflow:auto"><div class="sec" style="font-size:12px;line-height:20px;white-space:pre-line">1) Install CLI (Settings → Install CLI) — needs Node.js/npm.
2) Click Login: a PowerShell window opens and your browser asks you to sign in with your Claude account.
3) Close the PowerShell window, then click Test.
4) Enable the bridge, pick Claude as the engine and Save.</div></div>
    <div class="code" style="margin-top:12px"><div class="gut">1</div><pre><span class="tok-f">npm</span> install -g @anthropic-ai/claude-code</pre></div>
    ${testBox('Click Login first (if you have not signed in), then Test.')}
    ${helpScript({ login: 'Login window opened — sign in in the browser, then click Test.', ok: 'Claude Code CLI OK — claude 2.1.4\nAccount: dev@subtell.ir\nReply: hello' }, 'Not logged in to Claude Code. Click Login to sign in.', '', 'Testing Claude Code CLI…')}`,
    footer: `<button class="btn primary" id="hlpLogin" style="min-width:120px;height:36px">Login</button><button class="btn secondary" id="hlpTest" style="min-width:100px;height:36px">Test</button><button class="btn ghost" style="min-width:90px;height:36px" data-toast="info" data-title="Docs" data-msg="docs.claude.com/en/docs/claude-code/overview">Docs</button><button class="btn ghost" style="min-width:100px;height:36px">Close</button>` });

  /* ================================================================ CURSOR CLI HELP */
  const cSec = (t, b, cmd) => `<div class="cs"><div style="font-size:14px;font-weight:600;margin-bottom:6px">${t}</div><div class="sec" style="font-size:12px;line-height:18px">${b}</div>${cmd ? `<div class="code" style="margin-top:8px"><div class="gut">$</div><pre style="white-space:pre-wrap;word-break:break-all">${esc(cmd)}</pre></div>` : ''}</div>`;
  L.window({ slug: 'cursor-cli-help-window', title: 'Cursor CLI', icon: 'info', w: 640, h: 680, group: 'Windows',
    css: `.cs + .cs{margin-top:16px}`,
    body: `<div class="row top" style="gap:12px"><div style="width:42px;height:42px;border-radius:12px;display:grid;place-items:center;background:var(--info-surface);color:var(--info);flex:none">${ic('info', 'ic-lg')}</div><div class="grow"><div style="font-size:20px;font-weight:600">Cursor CLI setup guide</div><div class="muted" style="font-size:12px;margin-top:4px">For operators: install CLI, sign in once, then enable the bridge in GitDeploy.</div></div><div style="width:150px"><div class="muted" style="font-size:10px;margin-bottom:4px">Guide language</div><select class="select sm" style="height:32px"><option selected>English</option><option>فارسی</option></select></div></div>
    <div class="card" style="margin-top:16px;border-radius:12px;padding:14px;max-height:300px;overflow:auto">
      ${cSec('1) Install Cursor CLI', 'In GitDeploy click Install CLI (opens a visible PowerShell window), or run the command below manually. Official docs: cursor.com/docs/cli/overview', "irm 'https://cursor.com/install?win32=true' | iex")}
      ${cSec('2) First login (one time) — important', 'Prefer the Login to Cursor button below (opens PowerShell + browser). Manual equivalent if you need it:', '& "$env:LOCALAPPDATA\\cursor-agent\\agent.cmd" login')}
      ${cSec('3) If PowerShell blocks scripts', 'Error: running scripts is disabled. Either always use agent.cmd (recommended), or allow scripts for your user:', 'Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned')}
      ${cSec('4) Workspace trust', 'After login you may see Workspace Trust Required. Press a (Trust this workspace) so headless runs from GitDeploy work. GitDeploy already passes --trust --force for project chats.', '')}
      ${cSec('5) GitDeploy settings', 'Enable Cursor bridge, click Detect agent (should find agent.cmd), Save Cursor. Telegram must be enabled with your user id. GitDeploy must stay open while messages arrive.', '')}
      ${cSec('6) Test', 'Send a real message (not /start or menu buttons) to the bot for the active project. You should see Cursor agent started… in the chat, then a reply.', 'agent -p "say hello" --force --trust --workspace C:\\path\\to\\project')}</div>
    ${testBox('Use Login to Cursor once, then Test CLI — no PowerShell needed.')}
    ${helpScript({ login: 'Login window opened — sign in in the browser, then click Test CLI.', ok: 'CLI OK — agent: C:\\Users\\sharif\\AppData\\Local\\cursor-agent\\agent.cmd\nWorkspace: C:\\laragon\\www\\subtell\nReply: hello' }, 'CLI test timed out. Click Login to Cursor, finish browser sign-in, then Test again.', '', 'Testing Cursor CLI… checking login, then a short reply (up to 2 minutes).')}`,
    footer: `<button class="btn primary" id="hlpLogin" style="min-width:140px;height:36px">Login to Cursor</button><button class="btn secondary" id="hlpTest" style="min-width:110px;height:36px">Test CLI</button><button class="btn ghost" style="min-width:100px;height:36px" data-toast="info" data-title="Docs" data-msg="cursor.com/docs/cli/overview">Docs</button><button class="btn ghost" style="min-width:120px;height:36px">Close</button>` });
};
