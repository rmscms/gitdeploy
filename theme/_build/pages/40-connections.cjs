// 40-connections.cjs - Connection manager family (Windows/ConnectionManagerWindow, NewConnectionWindow, SavedConnectionsWindow,
// SessionManagerWindow, Controls/SessionManagerControl, SshKeySetupWindow, ProjectFtpTargetWindow, ProjectSetupWizard,
// ProjectPickerWindow, PageHostWindow, ToastWindow)
module.exports = (L) => {
  const ic = L.ic;

  // ------------------------------------------------------------------ shared css
  const COMMON = `
.ctx{position:fixed}
.win{grid-template-columns:minmax(0,1fr)}
.win .wt>span{white-space:nowrap}
.pb{width:24px;height:24px;border-radius:4px;display:grid;place-items:center;color:#fff;font-weight:700;font-size:12px;flex:none;background:var(--success)}
.pb.s{background:var(--accent2)} .pb.d{background:var(--info)}
.demo{display:inline-flex;gap:6px;margin-left:12px;font-size:11px;font-weight:400;align-items:center;color:var(--text-muted)}
.demo a{padding:1px 9px;border:1px dashed var(--border-strong);border-radius:10px;color:var(--text-muted);cursor:pointer}
.demo a:hover{color:var(--accent);border-color:var(--accent)}
.res{font-size:12px;color:var(--text-muted);min-width:0}
.res.ok{color:var(--success)} .res.err{color:var(--error)} .res.inf{color:var(--info)}
.steps{display:flex;flex-direction:column;gap:7px;margin:14px 0 12px;text-align:left}
.step{display:flex;align-items:center;gap:8px;color:var(--text-muted);font-size:12px;font-family:var(--font-mono)}
.step .ic{display:none}
.step .s-wait{display:inline-block;opacity:.35}
.step.run{color:var(--text-primary)} .step.run .s-wait,.step.ok .s-wait,.step.err .s-wait{display:none}
.step.run .s-run{display:inline-block;color:var(--accent)} .step.ok .s-ok{display:inline-block;color:var(--success)} .step.ok{color:var(--text-secondary)}
.step.err .s-err{display:inline-block;color:var(--error)} .step.err{color:var(--error)}
.gb{border-radius:6px;min-width:0}
.gb>.gh{background:var(--accent);color:var(--text-inverse);font-weight:600;padding:4px 10px;border-radius:6px 6px 0 0}
.gb>.gc{border:1px solid var(--border-strong);border-top:0;background:var(--surface-card);padding:12px;border-radius:0 0 6px 6px}
.fr{display:grid;grid-template-columns:120px minmax(0,1fr);align-items:center;gap:8px 0;margin-bottom:16px}
.fr>label{color:var(--text-secondary)}
.fr.hp{grid-template-columns:120px minmax(0,1fr) auto 100px} .fr.hp>.pl{margin:0 8px 0 15px}
.fr .input.c{text-align:center}
.fr.pw{grid-template-columns:120px minmax(0,1fr) auto;gap:8px}
.ov-card{text-align:center}
.ov-card .spin-big{width:44px;height:44px;margin:0 auto 10px;color:var(--accent)}
.ov-card .st{color:var(--text-secondary);font-size:12px;margin:6px 0 10px;font-family:var(--font-mono)}
`;

  // generic "run a test / long job" overlay + message box script (data-run / data-cancel-run)
  const JS_RUN = `<script>(function(){
var T={},CNT={};function g(i){return document.getElementById(i)}
function setRes(d,m,c){var r=g(d.result);if(r){r.textContent=m;r.className='res '+(c||'')}}
function box(kind,title,msg){var m=g('mmb');if(!m)return;m.querySelector('.hero').className='hero '+kind;m.querySelector('use').setAttribute('href','#i-'+({ok:'check-circle',err:'x-circle',warn:'alert'}[kind]));m.querySelector('[data-t]').textContent=title;m.querySelector('[data-m]').textContent=msg;m.classList.add('open')}
window.gdBox=box;
function lbl(b,on){if(!b||!b.querySelector('.bl'))return;var l=b.querySelector('.bl');if(on){b._o=l.textContent;l.textContent='Testing...';b.classList.add('working')}else{if(b._o)l.textContent=b._o;b.classList.remove('working')}}
function stop(id){if(T[id])clearInterval(T[id].t)}
function cancel(id){var o=g(id);stop(id);if(o)o.classList.remove('open');if(T[id]){lbl(T[id].b,false);setRes(T[id].d,T[id].d.cancelMsg||'Connection test cancelled.','');delete T[id]}}
function run(id,d,b){var o=g(id);if(!o)return;stop(id);o.classList.add('open');lbl(b,true);
 var s=[].slice.call(o.querySelectorAll('[data-step]')),st=o.querySelector('[data-status]'),bar=o.querySelector('.progress>i'),el=o.querySelector('[data-el]'),i=-1,t0=Date.now();
 var n=CNT[id]=(CNT[id]||0)+1,fa=(d.failAt!==undefined&&(!d.failFirst||n===1))?+d.failAt:-1;
 s.forEach(function(x){x.className='step'});if(bar)bar.style.width='0%';if(st)st.textContent=d.init||'Initializing test...';
 function fin(ok){o.classList.remove('open');delete T[id];lbl(b,false);setRes(d,ok?d.okMsg:d.failMsg,ok?'ok':'err');box(ok?'ok':'err',ok?(d.okTitle||'Success'):(d.failTitle||'Error'),ok?d.okMsg:d.failMsg)}
 function tick(){if(i>=0&&s[i])s[i].className='step ok';i++;if(el)el.textContent=((Date.now()-t0)/1000).toFixed(1)+'s';
  if(i>=s.length){if(bar)bar.style.width='100%';clearInterval(T[id].t);setTimeout(function(){fin(true)},350);return}
  s[i].className='step run';if(st)st.textContent=s[i].dataset.step+'...';if(bar)bar.style.width=Math.round((i+.5)/s.length*100)+'%';
  if(i===fa){clearInterval(T[id].t);setTimeout(function(){s[i].className='step err';setTimeout(function(){fin(false)},500)},900)}}
 T[id]={d:d,b:b,t:setInterval(tick,850)};tick()}
document.addEventListener('click',function(e){var b=e.target.closest('[data-run]');if(b){e.preventDefault();run(b.dataset.run,b.dataset,b)}
 var c=e.target.closest('[data-cancel-run]');if(c)cancel(c.dataset.cancelRun)});
})()</script>`;

  const MMB = `<div class="scrim" id="mmb"><div class="modal sm"><div class="modal-h"><h3 data-t>Message</h3></div><div class="modal-b" style="text-align:center"><div class="hero ok">${ic('check-circle', 'ic-xl')}</div><div data-m style="white-space:pre-line">Message</div></div><div class="modal-f"><button class="btn primary" data-close>OK</button></div></div></div>`;

  // test overlay (RemoteTestOverlay / DatabaseTestOverlay)
  const overlay = (id, text, steps) => `<div class="scrim" id="${id}"><div class="modal sm ov-card"><div class="modal-b">
${ic('spinner', 'spin spin-big')}<div style="font-weight:600">${text}</div><div class="st" data-status>Initializing test...</div>
<div class="progress busy"><i style="width:0%"></i></div>
<div class="steps">${steps.map((s) => `<div class="step" data-step="${s}">${ic('clock', 's-wait')}${ic('spinner', 'spin s-run')}${ic('check-circle', 's-ok')}${ic('x-circle', 's-err')}<span>${s}</span></div>`).join('')}</div>
<div class="row between"><span class="muted mono" style="font-size:11px">elapsed <b data-el>0.0s</b></span><button class="btn" data-cancel-run="${id}">Cancel Test</button></div></div></div></div>`;

  // ------------------------------------------------------------------ 1. Connection manager
  const conn = (name, host, kind, o = {}) => `<div class="cr list-item ${o.sel ? 'sel' : ''}" data-ctx="ctxConn" title="${kind === 'f' ? 'FTP' : kind === 's' ? 'SFTP (SSH)' : 'Database (tunnel over SSH)'} - ${host}"><span class="pb ${kind === 'f' ? '' : kind}">${kind === 'f' ? 'F' : kind === 's' ? 'S' : 'D'}</span><span class="grow"><b class="ellipsis" style="display:block">${name}</b><small class="muted ellipsis" style="display:block;margin-top:2px">${host}</small></span><button type="button" class="btn icon compact ghost fav ${o.fav ? 'on' : ''}" title="${o.fav ? 'Remove from favorites' : 'Add to favorites (shows near the top of the list)'}">${ic('star')}</button></div>`;

  const cmCss = COMMON + `
.cm{display:grid;grid-template-columns:280px minmax(0,1fr);height:100%;min-height:0}
.cm-left{background:var(--surface-card);border-right:1px solid var(--border-subtle);display:grid;grid-template-rows:auto auto minmax(0,1fr) auto;min-height:0}
.cm-left .hd{display:flex;align-items:center;gap:8px;padding:14px 14px 8px;font-size:14px;font-weight:600}
.cm-left .cnt{background:var(--surface-input);border-radius:6px;padding:2px 6px;font-size:11px;font-weight:600;color:var(--text-muted)}
.cm-left .sr-row{display:flex;gap:8px;padding:0 12px 10px}
.cm-left .sr-row .input{height:34px}
.cm-list{overflow:auto;padding:0 10px 6px;min-height:0}
.cr{background:var(--surface-input);border:1px solid var(--border-subtle);border-radius:8px;padding:10px;margin-bottom:8px;cursor:pointer;gap:10px}
.cr:hover{background:var(--surface-input);border-color:var(--border-strong)}
.cr:last-child{border-bottom:1px solid var(--border-subtle)}.cr.sel,.cr.sel:last-child{border-color:var(--accent);background:var(--surface-raised);box-shadow:none}
.cr .fav{color:var(--text-muted);width:28px;height:28px} .cr .fav.on{color:var(--warning)} .cr .fav.on .ic{fill:currentColor}
.cm-tools{display:flex;align-items:center;gap:6px;background:var(--surface-shell);height:42px;padding:0 8px}
.cm-tools .imp{color:var(--success);margin-left:2px}
.cm-right{position:relative;display:grid;grid-template-rows:auto auto minmax(0,1fr) auto;min-height:0;background:var(--surface-base);min-width:0}
.cm-name{padding:20px 25px 15px;border-bottom:1px solid var(--border-subtle)}
.cm-name .lbl{margin-bottom:5px}
.cm-name .input{height:42px;font-size:16px;font-weight:600}
.cm-tabs{margin:15px 15px 0;padding:0}
.cm-tabs .tab{font-size:14px;padding:0 20px;height:44px}
.cm-scroll{overflow:auto;padding:10px 25px 10px;min-height:0}
.cm-foot{display:flex;gap:15px;padding:14px 25px;border-top:1px solid var(--border-subtle);background:var(--surface-shell)}
.cm-foot .btn.primary{width:140px} .cm-foot .btn.c{width:100px}
details.adv{margin-top:6px;color:var(--text-secondary)} details.adv>summary{cursor:pointer;padding:6px 0;list-style:none;display:flex;align-items:center;gap:8px;font-weight:600}
details.adv>summary::-webkit-details-marker{display:none} details.adv>summary .ic{transition:transform .12s;width:12px;height:12px} details.adv[open]>summary .ic{transform:rotate(90deg)}
details.adv .in{padding:8px 0 0 20px;display:flex;flex-direction:column;gap:10px}
.cm-map-edit{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px;margin-top:14px}
.cm-empty{text-align:center;max-width:380px}
.cm-empty .ic-2xl{margin:0 auto 12px;color:var(--text-muted)}
@media (max-width:1100px){.cm{grid-template-columns:240px minmax(0,1fr)}}
`;

  const cmBody = `<div class="cm">
<aside class="cm-left">
  <div class="hd"><span>Connections</span><span class="cnt">8</span></div>
  <div class="sr-row"><input class="input" placeholder="Search by name, host, db..." data-filter="#cmList"><button class="btn icon ghost" title="Clear search" style="width:34px;height:34px">${ic('x')}</button></div>
  <div class="cm-list" id="cmList" data-selectable>
    ${conn('Production SFTP', '185.12.44.20', 's', { sel: true, fav: true })}
    ${conn('Staging FTP', 'ftp.staging.subtell.app', 'f', { fav: true })}
    ${conn('Prod MySQL (tunnel)', '185.12.44.20', 'd')}
    ${conn('Client - Bourse', 'ssh.bourse-ir.com', 's')}
    ${conn('Legacy cPanel FTP', 'ftp.cartex.shop', 'f')}
    ${conn('Redis cache', '10.0.4.12', 'd')}
    ${conn('Backup server', 'backup.digitalpart.net', 's')}
    ${conn('Laragon dev DB', '127.0.0.1', 'd')}
  </div>
  <div class="cm-tools"><button class="btn icon ghost" title="Add New Connection" data-open="cmEmpty" style="width:34px;height:34px">${ic('plus')}</button><button class="btn icon ghost" title="Remove Connection" data-confirm="Delete 'Production SFTP'?" data-confirm-title="Confirm" style="width:34px;height:34px">${ic('minus')}</button><button class="btn ghost imp" title="Import Navicat export (*.ncx / *.xml)" data-open="cmNavicat">${ic('download')}<span class="bl">Import Navicat</span></button></div>
</aside>

<section class="cm-right" data-tabscope>
  <div class="cm-name"><div class="lbl">Connection Name:</div><input class="input" id="cmName" placeholder="Connection name" value="Production SFTP"></div>
  <div class="tabs cm-tabs"><button class="tab active" data-tab="cmT1">Remote Host (SSH/FTP)</button><button class="tab" data-tab="cmT2">Database ${ic('database', 'ic-sm')}</button><button class="tab" data-tab="cmT3">Path Mappings</button></div>
  <div class="cm-scroll">
   <div class="tabpanel active" id="cmT1">
    <div class="fr"><label>Protocol:</label><div class="row" style="gap:20px"><label class="check"><input type="radio" name="proto">FTP</label><label class="check"><input type="radio" name="proto" checked>SFTP (SSH)</label></div></div>
    <div class="fr hp"><label>Host / IP:</label><input class="input mono" value="185.12.44.20"><label class="pl">Port:</label><input class="input c mono" value="22"></div>
    <div class="gb" style="margin-bottom:20px"><div class="gh">Authentication</div><div class="gc">
      <div class="fr"><label>Username:</label><input class="input mono" value="deploy"></div>
      <div class="fr pw" style="margin-bottom:0"><label>Password:</label><input class="input" id="cmPass" type="password" value="s3cr3t-pass-9"><button class="btn icon" style="width:36px;height:36px" title="Show/Hide password" onclick="var p=document.getElementById('cmPass');p.type=p.type=='password'?'text':'password'">${ic('eye')}</button></div>
      <div class="row" style="margin-top:14px;gap:12px"><button class="btn" id="cmTestBtn" data-run="cmOvRemote" data-result="cmRes" data-ok-msg="Connection successful." data-fail-msg="Connection failed: Permission denied (password)." data-ok-title="Success" data-fail-title="Error">${ic('zap')}<span class="bl">Test Connection</span></button><span class="res ok" id="cmRes">Connection successful.</span></div>
    </div></div>
    <div class="fr" style="grid-template-columns:120px minmax(0,1fr) auto;gap:8px 0;margin-bottom:12px"><label>Root Path:</label><input class="input mono" value="/var/www/myproject"><a class="btn" style="margin-left:10px" href="remote-browser-window.html" title="Browse remote folders">${ic('folder-open')}<span class="bl">Browse</span></a></div>
    <div class="fr" style="margin-bottom:12px"><label>Web URL:</label><input class="input mono" title="Example: http://mysite.com" placeholder="http://mysite.com" value="https://myproject.example.com"></div>
    <div class="fr" style="align-items:start"><label style="padding-top:8px">Startup command(s)</label><div class="stack sm"><textarea class="input mono" rows="3" title="Shell commands run automatically when the SSH terminal connects (one per line). Example: cd /var/www/myproject" placeholder="cd /var/www/myproject">cd /var/www/myproject
source .env</textarea>
      <div class="row wrap"><button class="btn compact" title="Insert a cd command using the Root Path field above (you can edit it afterward).">${ic('terminal')}<span class="bl">Fill cd from Root Path</span></button><label class="check" title="When unchecked, startup commands are saved but not sent to the terminal on connect."><input type="checkbox" checked>Run after SSH connect</label></div></div></div>
    <details class="adv"><summary>${ic('chevron-right')}Advanced Options</summary><div class="in">
      <label class="check"><input type="checkbox" checked>Passive mode</label>
      <label class="check ok" style="font-weight:700"><input type="checkbox" checked>Show hidden files (.env, .htaccess, etc.)</label>
      <div class="row"><label style="margin-right:6px;color:var(--text-secondary)">Keep Alive (sec):</label><input class="input c mono" style="width:80px" value="300"></div>
    </div></details>
   </div>

   <div class="tabpanel" id="cmT2">
    <div class="banner info" style="margin-bottom:18px">${ic('info')}<span>Database connections are tunneled securely through the SSH connection defined in the first tab. No need to open database ports to the public internet.</span></div>
    <div class="fr"><label>Database Type:</label>${L.sel(['None', ['MySQL', 1], 'MariaDB', 'PostgreSQL', 'SQLServer', 'MongoDB', 'Redis', 'SQLite'])}</div>
    <div class="fr hp"><label title="Usually 127.0.0.1 (relative to the server)">Host:</label><input class="input mono" value="127.0.0.1"><label class="pl">Port:</label><input class="input c mono" value="3306"></div>
    <div class="fr"><label>Database Name:</label><input class="input mono" title="The name of the database schema" value="myproject_prod"></div>
    <div class="gb" style="margin-bottom:20px"><div class="gh">Database Authentication</div><div class="gc">
      <div class="fr"><label>Username:</label><input class="input mono" value="root"></div>
      <div class="fr pw" style="margin-bottom:0"><label>Password:</label><input class="input" id="cmDbPass" type="password" value="db-secret-42"><button class="btn icon" style="width:36px;height:36px" title="Show/Hide password" onclick="var p=document.getElementById('cmDbPass');p.type=p.type=='password'?'text':'password'">${ic('eye')}</button></div>
      <div class="row" style="margin-top:14px;gap:12px"><button class="btn" data-run="cmOvDb" data-fail-first="1" data-fail-at="3" data-result="cmDbRes" data-ok-msg="Database connection successful." data-fail-msg="Database test failed: Access denied for user 'root'@'localhost' (using password: YES)" data-ok-title="Success" data-fail-title="Database Error" data-init="Testing database login and route..." data-cancel-msg="Database test cancelled.">${ic('database')}<span class="bl">Test Database Connection</span></button><span class="res" id="cmDbRes"></span></div>
    </div></div>
    <div class="hint">First click fails (Access denied), second click succeeds - same flow as the app (Test -> overlay -> message box).</div>
   </div>

   <div class="tabpanel" id="cmT3">
    <p class="sec" style="margin:4px 0 14px">Route local folders (relative to the project root) to remote directories. Deploy uses ALL mappings (e.g. api&rarr;/ and core&rarr;/core). The first mapping is the Direct Upload default root.</p>
    <div class="card flush"><div class="grid-wrap" style="max-height:230px"><table class="dg" data-select><thead><tr><th style="width:200px">Local Folder</th><th style="width:240px">Remote Path</th></tr></thead><tbody>
      <tr class="sel"><td class="mono">/</td><td class="mono">/public_html</td></tr>
      <tr><td class="mono">api</td><td class="mono">/</td></tr>
      <tr><td class="mono">core</td><td class="mono">/core</td></tr>
      <tr><td class="mono">assets/dist</td><td class="mono">/public_html/assets</td></tr>
    </tbody></table></div></div>
    <div class="cm-map-edit"><div class="field"><label>Local Folder (/ = project root, or site)</label><input class="input mono" placeholder="/" value="/"></div><div class="field"><label>Remote Path (e.g. /public_html)</label><input class="input mono" placeholder="/public_html" value="/public_html"></div></div>
    <div class="row" style="margin-top:12px"><button class="btn primary" data-toast="success" data-title="Mapping saved" data-msg="/ -> /public_html">Save Mapping</button><button class="btn danger" data-confirm="Delete this mapping?">Delete</button></div>
   </div>
  </div>
  <div class="cm-foot"><button class="btn primary" data-toast="success" data-title="Connection saved" data-msg="Production SFTP">Save Connection</button><button class="btn c">Cancel</button></div>

  <div class="scrim" id="cmEmpty" style="background:var(--surface-base);backdrop-filter:none"><div class="cm-empty">${ic('server', 'ic-2xl')}<div style="font-size:18px;font-weight:600">Connection details panel</div><p class="muted">Select a connection from the left list or create a new one to start editing host, auth, database, and path mappings.</p><button class="btn primary" data-close>${ic('plus')}<span class="bl">Create new connection</span></button></div></div>
  ${overlay('cmOvRemote', 'Please wait, testing remote connection...', ['Resolving 185.12.44.20', 'TCP connect :22', 'SSH handshake (curve25519-sha256)', 'Authenticating user deploy', 'Opening SFTP channel', 'Listing /var/www/myproject'])}
  ${overlay('cmOvDb', 'Please wait, testing database connection...', ['Opening SSH tunnel 127.0.0.1:53307', 'Forwarding to 127.0.0.1:3306', 'MySQL handshake', 'Authenticating root@127.0.0.1', 'SELECT 1'])}
</section>
</div>`;

  const cmDialogs = `
<div class="scrim" id="cmNavicat"><div class="modal sm"><div class="modal-h"><h3>Navicat Import</h3></div><div class="modal-b" style="text-align:center"><div class="hero">${ic('info', 'ic-xl')}</div><div style="white-space:pre-line">Imported 5 connection(s) from Navicat.

Warnings:
&bull; Skipped 'Legacy MSSQL' (unsupported type)
&bull; ...</div></div><div class="modal-f"><button class="btn primary" data-close>OK</button></div></div></div>
<div class="ctx" id="ctxConn"><div class="menu"><div class="mi">${ic('copy')}<span>Duplicate</span></div><div class="mi danger" data-confirm="Delete 'Production SFTP'?" data-confirm-title="Confirm">${ic('trash')}<span>Remove</span></div></div></div>
${MMB}`;

  L.window({
    slug: 'connection-manager-window', title: 'Deployment - Connection Manager', icon: 'server', w: 1040, h: 720, flush: true,
    sub: `<span class="demo">states: <a data-open="cmEmpty">nothing selected</a><a  data-run="cmOvRemote" data-result="cmRes" data-ok-msg="Connection successful." data-fail-msg="x">testing</a></span>`,
    css: cmCss + `.win .wfoot{display:none}`, body: cmBody, footer: '<span></span>', dialogs: cmDialogs + JS_RUN,
  });

  // ------------------------------------------------------------------ 2. New connection (database)
  L.window({
    slug: 'new-connection-window', title: 'New Database Connection', icon: 'plus', w: 450, h: 520,
    css: COMMON + `.nc{display:flex;flex-direction:column;gap:14px}.nc .hp{display:grid;grid-template-columns:minmax(0,1fr) 100px;gap:10px}`,
    body: `<div class="nc">
${L.field('Database Type', L.sel([['MySQL', 1], 'MariaDB']))}
<div class="hp">${L.field('Host', L.inp('', '127.0.0.1', 'text', 'mono'))}${L.field('Port', '<input class="input mono" style="text-align:center" value="3306">')}</div>
${L.field('Username', L.inp('', 'root', 'text', 'mono'))}
${L.field('Password', '<input class="input" type="password" value="">')}
${L.field('Database (optional)', L.inp('', '', 'text', 'mono'))}
<div class="res" id="ncRes"></div></div>`,
    footer: `<button class="btn ghost" style="width:90px">Cancel</button><button class="btn primary" id="ncGo" style="width:100px" data-working="Connecting..." data-done="Connected" data-done-msg="MySQL 8.0.36 at 127.0.0.1:3306">Connect</button>`,
  });

  // ------------------------------------------------------------------ 3. Saved connections
  const sc = (kind, name, desc, tag) => `<div class="list-item sc"><span class="lead sc-${kind}">${ic('database')}</span><div class="grow"><div class="t">${name} <span class="badge" style="margin-left:6px">${tag}</span></div><div class="d mono" style="margin-top:2px">${desc}</div></div><button class="btn primary compact" data-toast="info" data-title="Connecting" data-msg="${name}">Connect</button></div>`;
  L.window({
    slug: 'saved-connections-window', title: 'Saved Connections', icon: 'folder', w: 500, h: 500, flush: true,
    sub: `<span class="demo">states: <a data-open="scEmpty">empty</a></span>`,
    css: COMMON + `.sc{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:8px;padding:14px;margin-bottom:8px}.sc:hover{background:var(--surface-card)}
.sc .sc-mysql{background:rgba(76,141,255,.14);color:var(--accent)}.sc .sc-maria{background:var(--info-surface);color:var(--info)}.sc .sc-pg{background:rgba(123,97,255,.14);color:var(--accent2-hover)}.sc .sc-ms{background:var(--warning-surface);color:var(--warning)}.sc:last-child{border-bottom:1px solid var(--border-subtle)}
.sc-list{padding:16px;height:100%;overflow:auto}`,
    body: `<div class="sc-list">
${sc('mysql', 'Localhost', '127.0.0.1:3306 &middot; root', 'MySQL')}
${sc('mysql', 'Prod MySQL (tunnel)', '127.0.0.1:3306 &middot; app_user', 'MySQL')}
${sc('maria', 'Staging MariaDB', '127.0.0.1:3307 &middot; staging', 'MariaDB')}
${sc('pg', 'Analytics PG', '10.0.4.20:5432 &middot; analyst', 'PostgreSQL')}
${sc('ms', 'Legacy SQL Server', '192.168.1.40:1433 &middot; sa', 'SQLServer')}
</div>
<div class="scrim" id="scEmpty" style="background:var(--surface-base);backdrop-filter:none"><div class="empty">${ic('archive')}<div style="font-size:16px">No saved connections</div><div style="font-size:12px">Add database credentials in Connection Manager</div><button class="btn" data-close>Back to list</button></div></div>`,
    footer: `<a class="btn ghost l" style="margin-right:auto" href="connection-manager-window.html">Manage Profiles</a><button class="btn ghost" style="width:80px">Close</button>`,
  });

  // ------------------------------------------------------------------ 4/5. Session manager (window + control)
  const fo = (name, kids, open = true) => `<div class="tn dir ${open ? 'open' : ''}" data-ctx="ctxFolder"><span class="exp">${ic('chevron-right')}</span>${ic('folder', 'fi k-fold')}<span class="nm">${name}</span></div><div class="tchildren">${kids}</div>`;
  const lf = (name, kind, host, active = false) => `<div class="tn leaf" data-ctx="ctxSess" title="${host}"><span class="exp"></span>${active ? '<span class="dot live" title="Active session"></span>' : ''}${ic({ ssh: 'lock', db: 'database', ftp: 'folder-open' }[kind], 'fi k-' + kind)}<span class="nm">${name}</span><span class="ct">(${{ ssh: 'SSH', db: 'database', ftp: 'FTP' }[kind]}) (${host})</span></div>`;
  const sessTree = (id, withActive) => `<div class="tree sm-tree" id="${id}">
${fo('Production (3)', lf('Production SFTP', 'ssh', '185.12.44.20', withActive) + lf('Prod MySQL (tunnel)', 'db', '185.12.44.20') + lf('Legacy cPanel FTP', 'ftp', 'ftp.cartex.shop'))}
${fo('Clients (4)', fo('Bourse (2)', lf('Client - Bourse', 'ssh', 'ssh.bourse-ir.com', withActive) + lf('Bourse DB', 'db', 'ssh.bourse-ir.com')) + lf('DigitalPart backup', 'ssh', 'backup.digitalpart.net') + lf('CartEx staging', 'ftp', 'ftp.staging.cartex.shop'))}
${fo('Local (1)', lf('Laragon dev DB', 'db', '127.0.0.1'), false)}
${lf('Redis cache', 'db', '10.0.4.12')}
</div>`;
  const smCss = COMMON + `
.sm-wrap{display:grid;grid-template-rows:auto minmax(0,1fr);height:100%;min-height:0}
.sm-bar{display:flex;gap:8px;align-items:center;padding:10px;background:var(--surface-card);border-bottom:1px solid var(--border-subtle);flex-wrap:wrap}
.sm-bar .input{flex:1;min-width:160px}
.sm-bar .btn.icon{width:32px;height:32px}
.sm-pane{padding:10px;min-height:0;overflow:auto}
.sm-box{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:10px;padding:10px;height:100%;overflow:auto}
.sm-tree .tn{height:30px;font-size:14px}
.sm-tree .tn .fi{width:18px;height:18px}
.sm-tree .tn .k-fold{color:#FFB74D}.sm-tree .tn .k-ssh{color:#9C27B0}.sm-tree .tn .k-db{color:#2196F3}.sm-tree .tn .k-ftp{color:#4CAF50}
.sm-tree .tn .ct{font-size:11px;color:var(--text-muted);margin-left:6px;font-family:var(--font-mono);flex:none}
.sm-tree .tn .nm{overflow:hidden;text-overflow:ellipsis}
.sm-tree .tn.leaf .dot{margin-right:-2px}
.sm-tree .tn.dir.drop{outline:1px dashed var(--accent)}
.sm-hint{padding:6px 12px;color:var(--text-muted);font-size:11px;border-top:1px solid var(--border-subtle);display:flex;gap:12px;flex-wrap:wrap}
.ib{width:32px;height:32px}
`;
  const SMJS = `<script>(function(){
function filt(inp){var q=inp.value.toLowerCase(),tree=document.getElementById(inp.dataset.tree);if(!tree)return;
 [].forEach.call(tree.querySelectorAll('.tn.leaf'),function(n){n.hidden=!!q&&n.textContent.toLowerCase().indexOf(q)<0});
 var dirs=[].slice.call(tree.querySelectorAll('.tn.dir')).reverse();
 dirs.forEach(function(d){var ch=d.nextElementSibling,any=ch&&ch.querySelector('.tn:not([hidden])');d.hidden=!!q&&!any;if(ch)ch.hidden=d.hidden;if(q&&any)d.classList.add('open')});
 var e=document.getElementById(inp.dataset.empty);if(e)e.hidden=!q||!!tree.querySelector('.tn.leaf:not([hidden])')}
document.addEventListener('input',function(e){if(e.target.matches('[data-tree]'))filt(e.target)});
document.addEventListener('click',function(e){var b=e.target.closest('[data-newfolder-ok]');if(!b)return;var v=document.getElementById('nfName').value||'New Folder';var t=document.querySelector('.sm-tree');
 var d=document.createElement('div');d.innerHTML='<div class="tn dir open"><span class="exp"><svg class="ic"><use href="#i-chevron-right"/></svg></span><svg class="ic fi k-fold"><use href="#i-folder"/></svg><span class="nm"></span></div><div class="tchildren"></div>';d.firstChild.querySelector('.nm').textContent=v+' (0)';t.appendChild(d.firstChild);t.appendChild(d.lastChild);document.getElementById('nfDlg').classList.remove('open');if(window.toast)toast({type:'success',title:'Folder created',msg:v})})
})()</script>`;
  const smDialogs = `
<div class="scrim" id="nfDlg"><div class="modal sm"><div class="modal-h">${ic('folder-plus')}<h3>New Folder</h3></div><div class="modal-b">${L.field('Enter folder name:', '<input class="input" id="nfName" value="New Folder">')}</div><div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary" data-newfolder-ok data-close>OK</button></div></div></div>
<div class="ctx" id="ctxSess"><div class="menu"><div class="mi" data-toast="info" data-title="Connecting" data-msg="Production SFTP">${ic('zap')}<span>Connect</span></div><div class="mi" data-toast="warning" data-title="Disconnected" data-msg="Production SFTP">${ic('stop')}<span>Disconnect</span></div><div class="msep"></div><div class="mi danger" data-confirm="Delete connection 'Production SFTP'?" data-confirm-title="Confirm Delete">${ic('trash')}<span>Remove</span></div></div></div>
<div class="ctx" id="ctxFolder"><div class="menu"><div class="mi" data-open="nfDlg">${ic('folder-plus')}<span>New Folder</span></div><div class="mi" title="Link a connection into this folder">${ic('link')}<span>Link a connection</span></div><div class="msep"></div><div class="mi danger" data-confirm="Delete folder 'Clients' and move its contents to parent?" data-confirm-title="Confirm Delete">${ic('trash')}<span>Delete folder</span></div></div></div>`;
  const ib = (icon, tip, style = '', extra = '') => `<button type="button" class="btn icon ib" title="${tip}" style="${style}" ${extra}>${ic(icon)}</button>`;

  // Windows/SessionManagerWindow.xaml
  L.window({
    slug: 'session-manager-window', title: 'Session Manager', icon: 'layers', w: 900, h: 700, flush: true,
    css: smCss,
    body: `<div class="sm-wrap"><div class="sm-bar"><input class="input" placeholder="Filter by folder/session name..." data-tree="smTree1" data-empty="smEmpty1">
${ib('link', 'Link', '', 'data-open="smLink"')}${ib('paperclip', 'Attach')}${ib('plus', 'Add Folder', 'background:var(--success);border-color:var(--success);color:var(--text-inverse)', 'data-open="nfDlg"')}${ib('minus', 'Remove', 'background:var(--error);border-color:var(--error);color:#fff', 'data-confirm="Delete folder \'Local\' and move its contents to parent?" data-confirm-title="Confirm Delete"')}${ib('folder-plus', 'New Folder', 'background:var(--accent2);border-color:var(--accent2);color:#fff', 'data-open="nfDlg"')}${ib('x', 'Delete', 'background:var(--error);border-color:var(--error);color:#fff', 'data-confirm="Delete connection \'Redis cache\'?" data-confirm-title="Confirm Delete"')}${ib('settings', 'Settings')}${ib('arrow-left', 'Back')}${ib('arrow-right', 'Forward')}</div>
<div class="sm-pane"><div class="sm-box">${sessTree('smTree1', false)}<div class="empty" id="smEmpty1" hidden>${ic('search')}<div>No sessions match the filter</div></div></div></div></div>`,
    dialogs: smDialogs + `<div class="scrim" id="smLink"><div class="modal sm"><div class="modal-h">${ic('link')}<h3>Link</h3></div><div class="modal-b">Opens the <a href="connection-manager-window.html">Connection Manager</a> so you can add or edit connections; the tree reloads when it closes.</div><div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>` + SMJS,
    footer: `<span class="l">Drag a connection onto a folder to move it. Right-click for actions.</span><button class="btn">Close</button>`,
  });

  // Controls/SessionManagerControl.xaml - narrow sidebar-style panel with active (green) indicators
  L.window({
    slug: 'session-manager-control', title: 'SessionManagerControl', sub: 'embedded in terminal / remote windows', icon: 'layers', group: 'Controls', w: 360, h: 640, flush: true,
    css: smCss + `.sm-tree .tn{height:28px;font-size:13px}`,
    body: `<div class="sm-wrap"><div class="sm-bar" style="flex-wrap:nowrap"><input class="input" placeholder="Filter..." title="Filter folders and sessions" data-tree="smTree2" data-empty="smEmpty2">
${ib('link', 'Link a connection into this folder', '', 'data-open="smLink"')}${ib('folder-plus', 'Add a folder', '', 'data-open="nfDlg"')}${ib('minus', 'Remove the selected folder or session', '', 'data-confirm="Delete connection \'Production SFTP\'?" data-confirm-title="Confirm Delete"')}</div>
<div class="sm-pane" style="padding:6px">${sessTree('smTree2', true)}<div class="empty" id="smEmpty2" hidden>${ic('search')}<div>No sessions match</div></div></div></div>`,
    dialogs: smDialogs + `<div class="scrim" id="smLink"><div class="modal sm"><div class="modal-h">${ic('link')}<h3>Link</h3></div><div class="modal-b">Opens the <a href="connection-manager-window.html">Connection Manager</a>; the tree is always reloaded afterwards.</div><div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>` + SMJS,
    footer: `<span class="l"><span class="dot live"></span> active session &middot; double-click a connection to connect</span>`,
  });

  // ------------------------------------------------------------------ 6. SSH key setup
  const skJs = `<script>(function(){
var E=function(i){return document.getElementById(i)},KEY='C:\\\\Users\\\\sharif\\\\.ssh\\\\id_ed25519',PUB='ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIK3q0vE9bYhT7mJx2cL1dR8nWp4uZs6oQfGvA5yHtNkB sharif@gitdeploypro',tests=0,tm;
var DET='Host: github.com\\nssh: C:\\\\Windows\\\\System32\\\\OpenSSH\\\\ssh.exe\\nKeys: ';
function set(s,d,k,p,r,c){E('skStatus').textContent=s;E('skDetails').textContent=d;E('skKey').value=k;E('skPub').value=p;var x=E('skRes');x.textContent=r||'';x.className='res '+(c||'')}
var ST={checking:function(){set('Checking SSH on this PC...','','','','')},
none:function(){set('No default SSH key on this PC.','ssh: C:\\\\Windows\\\\System32\\\\OpenSSH\\\\ssh.exe','','','No SSH private key was found. Add a key from GitHub/GitLab or generate one here.','err')},
found:function(){set('Using key: '+KEY,DET+'id_ed25519',KEY,PUB,'SSH private key found. Git will use it for this remote.','')},
rejected:function(){set('Using key: '+KEY,'git@github.com: Permission denied (publickey).',KEY,PUB,'GitHub/Git rejected the SSH keys on this PC for github.com. Copy the public key below into the host SSH keys page, then Test again.','err')},
passphrase:function(){set('Using key: '+KEY,DET+'id_ed25519',KEY,PUB,'This key has a passphrase. Start the Windows OpenSSH Authentication Agent and load the key, or generate a key without a passphrase.','err')},
ok:function(){set('Using key: '+KEY,'Hi sharif! You have successfully authenticated, but GitHub does not provide shell access.',KEY,PUB,'SSH authenticated to github.com.','ok')},
generated:function(){set('Generated key: '+KEY,'Copy the public key to the host, then Test connection.',KEY,PUB,'Key created. Add it on the host before testing.','inf')}};
function state(n){clearTimeout(tm);E('skGen').hidden=true;busy(false);ST[n]&&ST[n]()}
function busy(b){var t=E('skTest');t.classList.toggle('working',b);t.querySelector('.bl').textContent=b?'Working...':'Test connection';E('skGenBtn').classList.toggle('disabled',b)}
var LINES=['Generating public/private ed25519 key pair.','Your identification has been saved in '+KEY,'Your public key has been saved in '+KEY+'.pub','The key fingerprint is:','SHA256:Qm4t0yH3bC8nVd2xRk9sLw1ZpUa7eFjGo5iTcYhN6Ds sharif@gitdeploypro','The key\\'s randomart image is:','+--[ED25519 256]--+','|        .o+B+Eo  |','|       . .=oo=.o |','|        o o.=oo.+|','+----[SHA256]-----+'];
function gen(){var g=E('skGen'),t=E('skGenLog'),b=E('skGenBar'),i=0;g.hidden=false;t.textContent='$ ssh-keygen -t ed25519 -C "sharif@gitdeploypro" -f '+KEY+' -N ""\\n';E('skGenBtn').classList.add('disabled');E('skStatus').textContent='Generating SSH key...';
 (function nx(){if(i>=LINES.length){b.style.width='100%';tm=setTimeout(function(){state('generated')},600);return}t.textContent+=LINES[i++]+'\\n';t.scrollTop=t.scrollHeight;b.style.width=Math.round(i/LINES.length*100)+'%';tm=setTimeout(nx,320)})()}
function test(){busy(true);E('skRes').textContent='Testing ssh -T git@github.com ...';E('skRes').className='res';tm=setTimeout(function(){busy(false);tests++;if(tests%2===1)ST.rejected();else{ST.ok();if(window.toast)toast({type:'success',title:'SSH key ready',msg:'Window closes (DialogResult = true)'})}},1800)}
document.addEventListener('click',function(e){var s=e.target.closest('[data-sk]');if(s){e.preventDefault();if(s.dataset.sk==='gen')gen();else if(s.dataset.sk==='test')test();else if(s.dataset.sk==='copy'){E('skRes').textContent='Public key copied. Paste it on the host SSH keys page.';E('skRes').className='res ok'}else state(s.dataset.sk)}});
state('found');
})()</script>`;
  L.window({
    slug: 'ssh-key-setup-window', title: 'SSH key for Git', icon: 'key', w: 560, h: 720,
    css: COMMON + `.sk .lbl{margin:12px 0 6px}.sk .box{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:8px;padding:10px 12px}
.sk .det{white-space:pre-wrap;font-family:var(--font-mono);font-size:11px;color:var(--text-muted);margin-top:6px;word-break:break-all}
.sk textarea{height:70px!important;resize:none;word-break:break-all}`,
    body: `<div class="sk">
<span class="demo" style="margin:0 0 6px;flex-wrap:wrap">states: <a data-sk="checking">checking</a><a data-sk="none">no key</a><a data-sk="found">found</a><a data-sk="rejected">rejected</a><a data-sk="passphrase">passphrase</a><a data-sk="ok">authenticated</a></span>
<h3 style="font-size:16px">Connect Git over SSH</h3>
<div class="sec" style="font-size:12px;margin-top:4px">Find an existing key, or generate one and add it on GitHub / GitLab.</div>
<div class="lbl">Status</div>
<div class="box"><div id="skStatus" class="mono" style="font-size:12px;word-break:break-all">Checking SSH on this PC...</div><div id="skDetails" class="det"></div></div>
<div class="lbl">Private key</div>
<div class="row"><input class="input mono" id="skKey" placeholder="C:\\Users\\you\\.ssh\\id_ed25519"><button class="btn" style="width:88px" title="Select SSH private key">Browse</button><button class="btn" id="skGenBtn" style="width:96px" data-sk="gen">${ic('wand')}<span class="bl">Generate</span></button></div>
<div id="skGen" hidden class="worker running" style="margin-top:10px;display:block"><div class="row"><span class="wi" style="width:30px;height:30px;border-radius:8px;display:grid;place-items:center;background:var(--accent-soft);color:var(--accent)">${ic('key')}</span><b>Generating ed25519 key pair</b><span class="badge accent" style="margin-left:auto">ssh-keygen</span></div>
<div class="progress busy" style="margin:8px 0"><i id="skGenBar" style="width:0%"></i></div><div class="terminal" id="skGenLog" style="height:150px;font-size:11px"></div></div>
<div class="lbl">Public key (.pub)</div>
<textarea class="input mono" id="skPub" readonly></textarea>
<div class="row wrap" style="margin-top:8px"><button class="btn" data-sk="copy">${ic('copy')}<span class="bl">Copy public key</span></button><button class="btn ghost">${ic('external')}<span class="bl">Open GitHub SSH keys</span></button></div>
<div class="sec" style="font-size:12px;margin-top:12px">Paste the public key on the host, then Test. Passphrase keys need Windows OpenSSH Authentication Agent, or generate a key without a passphrase.</div>
<div class="mono" style="font-size:11px;color:var(--accent);margin:2px 0 4px">https://github.com/settings/keys</div>
</div>`,
    footer: `<span class="res" id="skRes" style="margin-right:auto;max-width:300px"></span><button class="btn" style="width:90px">Cancel</button><button class="btn primary" id="skTest" style="width:140px" data-sk="test">${ic('zap')}<span class="bl">Test connection</span></button>`,
    dialogs: skJs,
  });

  // ------------------------------------------------------------------ 7. Project FTP target
  const ft = (name, host, def, sel) => `<div class="list-item ft ${sel ? 'sel' : ''}"><div class="grow"><div class="t ellipsis" style="font-size:13px">${name}</div><div class="d mono ellipsis" style="font-size:11px">${host}</div></div>${def ? '<span class="badge success" style="font-size:10px">Default</span>' : ''}</div>`;
  L.window({
    slug: 'project-ftp-target-window', title: 'Choose FTP target', icon: 'server', w: 460, h: 460,
    css: COMMON + `.ft{background:var(--surface-input);border:1px solid var(--border-subtle);border-radius:8px;padding:10px 12px;margin-bottom:8px;cursor:pointer}.ft:hover{background:var(--surface-input);border-color:var(--border-strong)}.ft:last-child{border-bottom:1px solid var(--border-subtle)}.ft.sel,.ft.sel:last-child{border-color:var(--accent);background:var(--surface-raised);box-shadow:none}`,
    body: `<div class="sec" style="margin-bottom:12px">Choose which FTP/SFTP this project should use for Commit and Sync.</div>
<div data-selectable>${ft('Production SFTP', '185.12.44.20', true, true)}${ft('Staging FTP', 'ftp.staging.subtell.app', false)}${ft('Client - Bourse', 'ssh.bourse-ir.com', false)}${ft('Legacy cPanel FTP', 'ftp.cartex.shop', false)}${ft('Backup server', 'backup.digitalpart.net', false)}</div>
<div class="hint" style="margin-top:6px">Only FTP/SFTP profiles are listed (database profiles are hidden). <a href="connection-manager-window.html">Manage connections</a></div>`,
    footer: `<button class="btn primary" style="width:90px;height:32px" data-toast="success" data-title="FTP target set" data-msg="Production SFTP">OK</button><button class="btn" style="width:90px;height:32px">Cancel</button>`,
  });

  // ------------------------------------------------------------------ 8. Project setup wizard
  const wzCss = COMMON + `
.wz{display:grid;grid-template-rows:auto auto minmax(0,1fr);height:100%;min-height:0;position:relative}
.wz-head{display:flex;gap:20px;align-items:flex-start;padding:20px 28px 10px}
.wz-head h1{font-size:26px;font-weight:700;display:flex;align-items:center;gap:10px}.wz-head h1 .ic{color:var(--accent);width:26px;height:26px}
.wz-head .cap{color:var(--accent);font-weight:600;font-size:12px;margin-top:10px}
.wz-lang{min-width:150px}.wz-lang .lbl{margin-bottom:4px}
.wz-steps{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px;padding:6px 28px 12px;border-bottom:1px solid var(--border-subtle)}
.wz-steps .tab.wz-t{height:auto;padding:8px 10px;border:1px solid var(--border-subtle);border-radius:8px;background:var(--surface-card);justify-content:flex-start;text-align:left;cursor:default}
.wz-steps .tab.wz-t.active{border-color:var(--accent);background:var(--accent-soft)}
.wz-steps .tab.wz-t .bd{width:22px;height:22px;border-radius:50%;display:grid;place-items:center;font-size:11px;font-weight:700;background:var(--surface-input);border:1px solid var(--border-subtle);color:var(--text-secondary);flex:none}
.wz-steps .tab.wz-t.active .bd{background:var(--accent);border-color:var(--accent);color:var(--text-inverse)}
.wz-steps .tab.wz-t .tt{font-size:12px;font-weight:600;color:var(--text-secondary)}.wz-steps .tab.wz-t.active .tt{color:var(--text-primary)}
.wz-steps .tab.wz-t small{display:block;font-size:10px;color:var(--text-muted);font-weight:400}
.wz-body{overflow:auto;padding:20px 28px;min-height:0}
.wz-body h2{font-size:18px;font-weight:600;margin-bottom:8px}
.wz-body .desc{color:var(--text-secondary);margin-bottom:16px}
.wz-body .sub-p{margin:8px 0 16px 24px}
.wz-prev{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px;padding:14px;margin-top:12px;display:grid;grid-template-columns:auto minmax(0,1fr);gap:6px 12px}
.wz-prev dt{color:var(--text-muted)}.wz-prev dd{margin:0;font-weight:600;overflow-wrap:anywhere}
.wz-ov{background:rgba(1,4,10,.92);backdrop-filter:none}
.wz-ov .in{width:min(560px,100%);text-align:center;display:flex;flex-direction:column;gap:10px;align-items:center}
.wz-ov .in h3{font-size:18px}
.wz-ov .logb{width:100%;height:150px;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:10px;padding:10px;overflow:auto;text-align:left;font-family:var(--font-mono);font-size:12px;color:var(--text-secondary);white-space:pre-wrap}
.wz-ov .progress{width:240px}
.wz-foot-back{margin-right:auto}
`;
  const wzStep = (n, t, h, active) => `<button type="button" class="tab wz-t ${active ? 'active' : ''}" data-tab="wzP${n}"><span class="bd">${n}</span><span><span class="tt">${t}</span><small>${h}</small></span></button>`;
  const wzJs = `<script>(function(){
var E=function(i){return document.getElementById(i)},cur=1,CAP=['Step 1 of 4 \\u2014 Recovery','Step 2 of 4 \\u2014 Git repository','Step 3 of 4 \\u2014 Deployment target','Step 4 of 4 \\u2014 Branch strategy'],tm,PROF={'Production SFTP':['185.12.44.20','SFTP','deploy','/var/www/myproject'],'Staging FTP':['ftp.staging.subtell.app','FTP','stage_ftp','/public_html'],'Legacy cPanel FTP':['ftp.cartex.shop','FTP','cartex','/public_html/shop']};
function go(n){cur=n;[].forEach.call(document.querySelectorAll('.wz-t'),function(t,i){t.classList.toggle('active',i===n-1)});[].forEach.call(document.querySelectorAll('.wz-body .tabpanel'),function(p,i){p.classList.toggle('active',i===n-1)});E('wzCap').textContent=CAP[n-1];E('wzBack').style.visibility=n>1?'visible':'hidden';E('wzNext').hidden=n===4;E('wzFinish').hidden=n!==4}
function next(){if(cur===2&&E('wzRemote').checked&&!E('wzUrl').value.trim()){gdBox('warn','Project Setup','Please enter a Remote URL, or choose Local Repository.');return}
 if(cur===3&&E('wzFtp').checked&&E('wzProf').selectedIndex===0){gdBox('warn','Project Setup','Please select a connection profile, or disable FTP Deployment.');return}go(cur+1)}
function setup(fail){var o=E('wzOv'),log=E('wzLog'),bar=E('wzBar'),pb=bar.parentNode,sum=E('wzSum'),st=E('wzSt'),cp=E('wzCopy'),sp=E('wzSpin');clearInterval(tm);o.classList.add('open');log.textContent='Initializing...';cp.hidden=true;sp.classList.add('spin');st.style.color='';pb.className='progress lg indeterminate';bar.style.width='0%';sum.textContent='';st.textContent='Starting setup...';
 var remote=fail||E('wzRemote').checked,S=[['Cleaning project setup artifacts...',['Applying project cleanup options...','Removed: .git, .gitdeploy.config']],['Configuring Git repository...',remote?['Connecting over SSH/HTTPS \u2014 live fetch progress will appear below\u2026','remote: Enumerating objects: 412, done.','Receiving objects: 100% (412/412), 1.8 MiB | 3.2 MiB/s','Remote sync finished.']:['Creating .gitignore...','Initializing local repository...']],['Saving project configuration...',['Saving project configuration...']],['Checking for uncommitted changes...',['Checking for changes...','Committing 14 changes...']],['Setup complete!',['Setup Complete!']]],ev=[],k=0,n=S.length-1;
 S.forEach(function(s,si){ev.push([0,si,s[0]]);s[1].forEach(function(l){ev.push([1,si,l])})});
 tm=setInterval(function(){
  if(k>=ev.length){clearInterval(tm);bar.style.width='100%';sum.textContent=n+'/'+n;pb.className='progress lg success';sp.classList.remove('spin');setTimeout(function(){o.classList.remove('open');if(window.toast)toast({type:'success',title:'Project setup finished',msg:'.gitdeploy.config saved'})},1400);return}
  var e=ev[k++];
  if(e[0]===0){st.textContent=e[2];if(e[1]>0)pb.className='progress lg busy';bar.style.width=Math.round(e[1]/n*100)+'%';sum.textContent=e[1]+'/'+n}
  else{log.textContent=(log.textContent==='Initializing...'?'':log.textContent+'\\n')+e[2];log.scrollTop=log.scrollHeight;
   if(fail&&e[1]===1){log.textContent+='\\nSSH key setup needed: GitHub/Git rejected the SSH keys on this PC for github.com.\\nSetup failed: git@github.com: Permission denied (publickey).';log.scrollTop=log.scrollHeight;clearInterval(tm);pb.className='progress lg error';sp.classList.remove('spin');st.textContent='Setup failed';st.style.color='var(--error)';cp.hidden=false}}
 },650)}
document.addEventListener('click',function(e){var t=e.target.closest('.wz-t');if(t){go([].indexOf.call(document.querySelectorAll('.wz-t'),t)+1)}});
document.addEventListener('click',function(e){var b=e.target.closest('[data-wz]');if(!b)return;e.preventDefault();var a=b.dataset.wz;
 if(a==='next')next();else if(a==='back')go(cur-1);else if(a==='finish')setup(false);else if(a==='fail')setup(true);else if(a==='skip'){E('wzSub').textContent='Git is ready, but .gitdeploy.config is missing. Finish setup, or Skip to create a minimal config.';E('wzSkip').hidden=false}
 else if(a==='close'){clearInterval(tm);E('wzOv').classList.remove('open')}});
document.addEventListener('change',function(e){var t=e.target;
 if(t.name==='gitsrc'){E('wzLocalP').hidden=E('wzRemote').checked;E('wzRemoteP').hidden=!E('wzRemote').checked}
 if(t.id==='wzFtp')E('wzFtpP').hidden=!t.checked;
 if(t.id==='wzProf'){var p=PROF[t.value]||['-','-','-','-'];E('wzPh').textContent=p[0];E('wzPp').textContent=p[1];E('wzPu').textContent=p[2];E('wzPr').textContent=p[3]}
 if(t.classList.contains('wzr')){var a=[];if(E('wzR1').checked)a.push('.git');if(E('wzR2').checked)a.push('.gitdeploy.config');E('wzRh').textContent=a.length?'Selected for cleanup: '+a.join(', '):'No reset selected.'}});
go(1);
})()</script>`;
  L.window({
    slug: 'project-setup-wizard', title: 'Project Setup Wizard', icon: 'rocket', w: 820, h: 640, flush: true,
    sub: `<span class="demo">states: <a data-wz="fail">setup failed</a><a data-wz="finish">setup running</a><a data-wz="skip">skip mode</a></span>`,
    css: wzCss,
    body: `<div class="wz" data-tabscope>
<div class="wz-head"><div class="grow"><h1>${ic('rocket')}Project Setup</h1><div class="sec" id="wzSub" style="margin-top:4px">Configure Git and Deployment for your new project.</div><div class="cap" id="wzCap">Step 1 of 4 &mdash; Recovery</div></div>
<div class="wz-lang"><div class="lbl">Language</div>${L.sel([['English', 1], '&#1601;&#1575;&#1585;&#1587;&#1740;'])}</div></div>
<div class="wz-steps">${wzStep(1, 'Recovery', 'Optional reset', true)}${wzStep(2, 'Git', 'Repository')}${wzStep(3, 'Deploy', 'FTP / SFTP')}${wzStep(4, 'Branches', 'Source / target')}</div>
<div class="wz-body">
 <div class="tabpanel active" id="wzP1"><h2>Recovery / Reinitialize</h2><div class="desc">Optional. Use this when setup is broken and you need a clean rebuild for this project folder.</div>
  <div class="stack sm"><label class="check"><input type="checkbox" class="wzr" id="wzR1">Remove .git folder before setup</label><label class="check"><input type="checkbox" class="wzr" id="wzR2">Remove .gitdeploy.config before setup</label></div>
  <div class="hint" id="wzRh" style="margin-top:12px">No reset selected.</div></div>
 <div class="tabpanel" id="wzP2"><h2>Git Repository Source</h2><div class="desc">Choose how this project folder should connect to Git.</div>
  <label class="check"><input type="radio" name="gitsrc" id="wzLocal" checked>Local Repository (Initialize new)</label>
  <div class="sub-p" id="wzLocalP"><div class="lbl" style="margin-bottom:6px;font-weight:400;color:var(--text-secondary)">Initial Branch Name:</div><input class="input mono" style="width:220px" value="master"></div>
  <label class="check"><input type="radio" name="gitsrc" id="wzRemote">Remote Repository (Clone/Connect)</label>
  <div class="sub-p" id="wzRemoteP" hidden><div class="lbl" style="margin-bottom:6px;font-weight:400;color:var(--text-secondary)">Remote URL (HTTPS/SSH):</div><input class="input mono" id="wzUrl" style="max-width:480px" placeholder="git@github.com:user/repo.git"><div class="hint" style="margin-top:6px">SSH remotes may open <a href="ssh-key-setup-window.html">SSH key setup</a>.</div></div></div>
 <div class="tabpanel" id="wzP3"><h2>Deployment Target (FTP/SFTP)</h2><div class="desc">Optional. Enable FTP/SFTP if this project deploys to a remote host.</div>
  <label class="check"><input type="checkbox" id="wzFtp">Enable FTP Deployment</label>
  <div id="wzFtpP" hidden style="margin-top:14px"><div class="lbl" style="margin-bottom:6px;font-weight:400;color:var(--text-secondary)">Select Connection Profile:</div>
   <div class="row"><select class="select" id="wzProf" style="max-width:320px"><option value=""></option><option>Production SFTP</option><option>Staging FTP</option><option>Legacy cPanel FTP</option></select><a class="btn" style="min-width:150px" href="connection-manager-window.html">${ic('plus')}<span class="bl">Manage Profiles</span></a></div>
   <dl class="wz-prev"><dt>Host:</dt><dd id="wzPh" class="mono">-</dd><dt>Protocol:</dt><dd id="wzPp" style="font-weight:400">-</dd><dt>User:</dt><dd id="wzPu" class="mono" style="font-weight:400">-</dd><dt>Root Path:</dt><dd id="wzPr" class="mono ok" style="font-weight:400">-</dd></dl></div></div>
 <div class="tabpanel" id="wzP4"><h2>Branch Strategy</h2><div class="desc">Pick development and production branches. Deploy uses the target branch.</div>
  <div class="lbl" style="margin-bottom:6px;font-weight:400;color:var(--text-secondary)">Source Branch (Development):</div><input class="input mono" list="wzSrc" value="master" style="width:220px;margin-bottom:14px"><datalist id="wzSrc"><option value="master"><option value="main"><option value="beta"></datalist>
  <div class="lbl" style="margin-bottom:6px;font-weight:400;color:var(--text-secondary)">Target Branch (Production/Deploy):</div><input class="input mono" list="wzTgt" value="production" style="width:220px;margin-bottom:14px"><datalist id="wzTgt"><option value="production"><option value="main"><option value="master"></datalist>
  <div class="row"><span class="badge accent">${ic('git-branch', 'ic-sm')} master</span>${ic('arrow-right', 'muted')}<span class="badge success">${ic('git-branch', 'ic-sm')} production</span></div>
  <div class="hint" style="margin-top:10px">Files will be deployed from the Target Branch.</div></div>
</div>
<div class="scrim wz-ov" id="wzOv"><div class="in"><span id="wzSpin" class="spin accent" style="display:inline-flex;width:44px;height:44px">${ic('spinner', 'ic-2xl')}</span><h3>Setting up project...</h3>
<div class="progress lg indeterminate"><i id="wzBar" style="width:0%"></i></div><div class="mono muted" id="wzSum"></div><div class="sec" id="wzSt" style="max-width:560px">Please wait...</div>
<div class="logb" id="wzLog">Initializing...</div><div class="row"><button class="btn" id="wzCopy" hidden data-toast="info" data-title="Copied" data-msg="Error details copied to clipboard">${ic('copy')}<span class="bl">Copy Error Details</span></button><button class="btn ghost" data-wz="close">Dismiss</button></div></div></div>
</div>`,
    footer: `<button class="btn wz-foot-back" id="wzBack" data-wz="back" style="width:100px;visibility:hidden">&larr; Back</button><button class="btn ghost" id="wzSkip" hidden style="width:100px" title="Create a minimal .gitdeploy.config and continue without full setup">Skip</button><button class="btn ghost" style="width:100px">Cancel</button><button class="btn primary" id="wzNext" data-wz="next" style="width:110px">Next &rarr;</button><button class="btn primary" id="wzFinish" data-wz="finish" hidden style="width:150px">Finish &amp; Setup</button>`,
    dialogs: MMB + JS_RUN + wzJs,
  });

  // ------------------------------------------------------------------ 9. Project picker
  const pj = (n, p, c) => `<div class="list-item pj" data-open="pjSoon"><span class="pj-i" style="background:${c}">${n.charAt(0).toUpperCase()}</span><div class="grow"><div class="t ellipsis">${n}</div><div class="mono muted ellipsis" style="font-size:11px">${p}</div></div></div>`;
  const projs = [['myproject', 'C:\\\\laragon\\\\www\\\\myproject', '#3574F0'], ['subtell', 'C:\\\\laragon\\\\www\\\\subtell', '#9B59B6'], ['cartex', 'C:\\\\laragon\\\\www\\\\cartex', '#579A57'], ['bourse', 'C:\\\\laragon\\\\www\\\\bourse', '#E59500'], ['GitDeployPro', 'C:\\\\laragon\\\\www\\\\GitDeployPro', '#00ACC1'], ['digitalpart-backup', 'D:\\\\clients\\\\digitalpart\\\\backup', '#E05555'], ['telegram-bot', 'D:\\\\bots\\\\telegram-bot', '#F06292'], ['wp-shop', 'C:\\\\laragon\\\\www\\\\wp-shop', '#3574F0'], ['api-gateway', 'D:\\\\work\\\\api-gateway', '#579A57'], ['landing-v2', 'D:\\\\work\\\\landing-v2', '#E59500'], ['old-portfolio', 'E:\\\\archive\\\\old-portfolio', '#9B59B6'], ['crm-panel', 'D:\\\\work\\\\crm-panel', '#00ACC1']];
  L.window({
    slug: 'project-picker-window', title: 'All projects', icon: 'folder-open', w: 520, h: 600,
    css: COMMON + `.pj{padding:6px 10px;border-radius:8px;cursor:pointer;border:0}.pj:hover{background:var(--surface-hover)}.pj.sel{background:var(--accent-soft)}
.pj-i{width:28px;height:28px;border-radius:6px;display:grid;place-items:center;color:#fff;font-weight:700;flex:none}
.pj-list{overflow:auto;max-height:100%;border:1px solid var(--border-subtle);border-radius:10px;background:var(--surface-shell);padding:4px}
.pj-w{display:grid;grid-template-rows:auto auto minmax(0,1fr);gap:10px;height:100%;min-height:0}`,
    body: `<div class="pj-w"><div class="sec">Search by folder name or path, then open.</div>
<input class="input" placeholder="Search projects&hellip;" data-filter="#pjList" data-item-sel=".pj" id="pjQ">
<div class="pj-list" id="pjList" data-selectable>${projs.map((p, i) => pj(p[0], p[1].replace(/\\\\/g, '\\'), p[2]).replace('list-item pj', 'list-item pj' + (i === 1 ? ' sel' : ''))).join('')}<div class="empty" id="pjEmpty" hidden style="padding:24px">No matching projects.</div></div></div>
<div class="scrim" id="pjSoon"><div class="modal sm"><div class="modal-h">${ic('folder-open')}<h3>Open project</h3></div><div class="modal-b">Double-click (or Open) switches the active project and returns to the <a href="dashboard.html">Dashboard</a>.</div><div class="modal-f"><button class="btn" data-close>Close</button></div></div></div>
<script>document.addEventListener('input',function(e){if(e.target.id!=='pjQ')return;var any=document.querySelector('#pjList .pj:not([hidden])');document.getElementById('pjEmpty').hidden=!!any})</script>`,
    footer: `<a class="btn primary" style="width:110px;height:32px" href="dashboard.html">Open</a><button class="btn ghost" style="width:90px;height:32px">Cancel</button>`,
  });

  // ------------------------------------------------------------------ 10. Page host window
  L.window({
    slug: 'page-host-window', title: 'Detached View', sub: 'MetroWindow &middot; Frame (navigation UI hidden) hosts any Page', icon: 'external', w: 1200, h: 700, flush: true,
    css: COMMON + `.ph{padding:24px 30px;height:100%;overflow:auto}.ph .tag{display:inline-flex;margin-bottom:14px}`,
    body: `<div class="ph"><div class="page-head"><div><div class="page-title">History</div><div class="page-sub">Detached copy of a page, opened from the main window.</div></div><div class="r"><a class="btn" href="history.html">${ic('arrow-left')}<span class="bl">Open full page</span></a></div></div>
<div class="banner info" style="margin-bottom:14px">${ic('info')}<span>The window owner is resolved through WindowOwnerService; the hosted page keeps its own state, scroll and live workers.</span></div>
<div class="card flush"><div class="grid-wrap"><table class="dg" data-select><thead><tr><th>Date</th><th>Branch</th><th>Commit</th><th>Target</th><th class="num">Files</th><th>Status</th></tr></thead><tbody>
<tr><td class="mono">2026-11-02 00:39</td><td><span class="badge accent">${ic('git-branch', 'ic-sm')} production</span></td><td class="mono">2c3e767</td><td class="mono">Production SFTP</td><td class="num">14</td><td><span class="badge success">Success</span></td></tr>
<tr><td class="mono">2026-11-02 00:21</td><td><span class="badge accent">${ic('git-branch', 'ic-sm')} production</span></td><td class="mono">638d950</td><td class="mono">Production SFTP</td><td class="num">3</td><td><span class="badge success">Success</span></td></tr>
<tr><td class="mono">2026-11-02 00:15</td><td><span class="badge accent">${ic('git-branch', 'ic-sm')} production</span></td><td class="mono">2babf2f</td><td class="mono">Staging FTP</td><td class="num">27</td><td><span class="badge error">Failed</span></td></tr>
<tr><td class="mono">2026-10-16 17:09</td><td><span class="badge accent">${ic('git-branch', 'ic-sm')} production</span></td><td class="mono">3d93978</td><td class="mono">Production SFTP</td><td class="num">8</td><td><span class="badge warning">Partial</span></td></tr>
</tbody></table></div></div></div>`,
    footer: `<span class="l">Detached View</span><button class="btn">Close</button>`,
  });

  // ------------------------------------------------------------------ 11. Toast window (app's own toast popup)
  const tv = (t, m, k, tip) => `<div class="gdt" data-t="${t}" data-m="${m}"><div class="gdt-t">${t}</div><div class="gdt-m">${m}</div></div><div class="gdt-cap">${k}</div>`;
  const variants = [
    ['Backup completed', 'digitalpart-backup finished successfully.', 'BackupSchedulerRunner - success (default sample)'],
    ['Backup finished', 'digitalpart-backup completed.', 'BackupSchedulerPage - manual run'],
    ['Backup completed with validation warning', 'digitalpart-backup: 2 of 120 sampled files differ from source.', 'validation warning'],
    ['Backup failed', 'digitalpart-backup: Connection timed out after 30s (185.12.44.20:22).', 'failure'],
    ['Backup skipped', 'digitalpart-backup is already running.', 'already running'],
    ['Backup skipped', 'Profile missing for nightly-db.', 'profile missing'],
    ['Backup retry scheduled', 'nightly-db: retry in 15 min (Access denied for user).', 'retry'],
    ['myproject', 'Sharif: can you deploy the hotfix to production now?', 'Telegram - new message (title = project name)'],
  ];
  L.raw({
    slug: 'toast-window', title: 'ToastWindow', group: 'Windows',
    css: `.tw{min-height:100vh;padding:28px;background:radial-gradient(900px 500px at 30% 0,rgba(76,141,255,.12),transparent 60%),#000}
.tw h1{font-size:22px}.tw .sub{color:var(--text-muted);margin:4px 0 20px;max-width:760px}
.tw-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,320px));gap:18px 24px;margin-bottom:28px}
.gdt{width:320px;min-height:88px;border-radius:12px;background:var(--surface-card);border:1px solid var(--border-subtle);padding:18px;box-shadow:0 0 16px rgba(0,0,0,.4);cursor:pointer}
.gdt:hover{border-color:var(--border-strong)}
.gdt-t{font-size:16px;font-weight:600;color:var(--text-primary)}.gdt-m{margin-top:8px;color:var(--text-secondary);word-wrap:break-word}
.gdt-cap{font-size:11px;color:var(--text-muted);margin:6px 2px 0;font-family:var(--font-mono)}
.tw-stage{position:relative;height:340px;border:1px solid var(--border-strong);border-radius:14px;overflow:hidden;background:var(--surface-base);max-width:1000px}
.tw-stage .mini{position:absolute;inset:0;display:grid;grid-template-rows:34px 1fr}
.tw-stage .mini .tb{background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);display:flex;align-items:center;gap:8px;padding:0 12px;color:var(--text-muted);font-size:12px}
.tw-stage .mini .ct{padding:18px;display:grid;grid-template-columns:repeat(3,1fr);gap:12px;align-content:start}
.tw-stage .mini .ct i{display:block;height:70px;border-radius:12px;background:var(--surface-card);border:1px solid var(--border-subtle)}
.tw-stack{position:absolute;right:20px;bottom:20px;width:320px;display:flex;flex-direction:column;gap:10px;pointer-events:none}
.tw-stack .gdt{pointer-events:auto;box-shadow:0 0 16px rgba(0,0,0,.6);animation:tin .22s ease-out both;position:relative;overflow:hidden}
.tw-stack .gdt.out{animation:tout .18s ease-in both}
.tw-stack .gdt .life{position:absolute;left:0;bottom:0;height:2px;width:100%;background:var(--accent);transform-origin:left;animation:tbar 4s linear forwards}
.tw-ctl{display:flex;gap:8px;flex-wrap:wrap;margin:0 0 12px}`,
    html: `<div class="tw"><div class="row" style="gap:12px;margin-bottom:6px"><span class="avatar xl">${ic('bell', 'ic-lg')}</span><div><h1>ToastWindow</h1><div class="muted">frameless, transparent, no taskbar entry, 320&times;120, never steals focus</div></div></div>
<p class="sub">The app's own notification popup (<code>NotificationService.ShowToast(title, message)</code>): Surface.Card, 12px radius, 18px padding, 16px semibold title and secondary wrapped message. It appears 20px from the bottom-right corner of the owner window (or screen work area), closes after 4 seconds or on click. Only title/message vary &mdash; there are no severity colors.</p>
<div class="eyebrow" style="margin-bottom:10px">Message variants used by the app (click a card to dismiss-preview it below)</div>
<div class="tw-grid">${variants.map((v) => `<div>${tv(v[0], v[1], v[2])}</div>`).join('')}</div>
<div class="eyebrow" style="margin-bottom:10px">Live placement preview</div>
<div class="tw-ctl"><button class="btn primary" id="twAny">${ic('bell')}<span class="bl">Show next toast</span></button><button class="btn" id="twAll">Show several (stacked)</button><span class="hint" style="align-self:center">Each toast auto-closes after 4s (progress hairline); click a toast to close it now.</span></div>
<div class="tw-stage"><div class="mini"><div class="tb">${ic('layers', 'ic-sm')} GitDeploy Pro &mdash; owner window</div><div class="ct"><i></i><i></i><i></i><i></i><i></i><i></i></div></div><div class="tw-stack" id="twStack"></div></div></div>
<script>(function(){var V=[].map.call(document.querySelectorAll('.tw-grid .gdt'),function(e){return[e.dataset.t,e.dataset.m]}),i=0,st=document.getElementById('twStack');
function show(v){var d=document.createElement('div');d.className='gdt';var a=document.createElement('div');a.className='gdt-t';a.textContent=v[0];var b=document.createElement('div');b.className='gdt-m';b.textContent=v[1];var l=document.createElement('span');l.className='life';d.appendChild(a);d.appendChild(b);d.appendChild(l);st.appendChild(d);var c=function(){if(d.classList.contains('out'))return;d.classList.add('out');setTimeout(function(){d.remove()},180)};d.onclick=c;setTimeout(c,4000);while(st.children.length>3)st.firstChild.remove()}
document.getElementById('twAny').onclick=function(){show(V[i++%V.length])};document.getElementById('twAll').onclick=function(){for(var k=0;k<3;k++)setTimeout(function(){show(V[i++%V.length])},k*350)};
[].forEach.call(document.querySelectorAll('.tw-grid .gdt'),function(e){e.onclick=function(){show([e.dataset.t,e.dataset.m])}});show(V[0])})()</script>`,
  });
};
