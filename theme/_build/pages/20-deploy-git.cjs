// Deploy + Git screens: DeployPage, DeployRemoteWorkspaceControl, DiffViewer, GitPage and the git/deploy windows.
module.exports = (L) => {
  const { ic, esc } = L;

  // ------------------------------------------------------------------ small shared script (state switchers)
  const SCRIPT = `<script>(function(){
var q=function(s){return Array.prototype.slice.call(document.querySelectorAll(s));};
function apply(spec){spec.split(';').forEach(function(one){var p=one.split('|');q(p[0]).forEach(function(t){t.setAttribute(p[1],p[2]);});});}
document.addEventListener('click',function(e){
 var b=e.target.closest('[data-set]');
 if(b){apply(b.dataset.set);var g=b.closest('[data-grp]');if(g){Array.prototype.forEach.call(g.children,function(x){x.classList.toggle('active',x===b);});}
  if(b.dataset.revert){var r=b.dataset.revert.split('~');setTimeout(function(){apply(r[1]);},+r[0]);}}
 var f=e.target.closest('[data-flip]');
 if(f){var s=f.dataset.flip.split('|');q(s[0]).forEach(function(t){t.setAttribute(s[1],t.getAttribute(s[1])===s[2]?s[3]:s[2]);});}
});
document.addEventListener('dblclick',function(e){var d=e.target.closest('[data-dbl]');if(d)apply(d.dataset.dbl);});
window.addEventListener('load',function(){location.hash.slice(1).split('&').forEach(function(kv){var p=kv.split('=');q('.dp,.rw').forEach(function(e){if(p[0]&&e.hasAttribute('data-'+p[0]))e.setAttribute('data-'+p[0],p[1]);});});if(innerWidth<1180&&location.hash.indexOf('remote=')<0){q('.dp').forEach(function(d){d.setAttribute('data-remote','0');});}});
})();</script>`;

  // ------------------------------------------------------------------ shared CSS
  const FX = { php: 'php', js: 'js', ts: 'js', css: 'css', scss: 'css', html: 'html', json: 'json', md: 'md', png: 'img', jpg: 'img', svg: 'img', cs: 'cs', xaml: 'xaml' };
  const fx = (name) => FX[(name.split('.').pop() || '').toLowerCase()] || 'other';
  const BASE_CSS = `
.fx-php{color:#8892bf}.fx-js{color:#f0db4f}.fx-css{color:#5bb8ff}.fx-html{color:#ff8a4c}.fx-json{color:#5cffb0}.fx-md{color:#c3cee3}.fx-img{color:#ff7ac6}.fx-cs{color:#b48bff}.fx-xaml{color:#4c8dff}.fx-other{color:var(--text-muted)}
.fn{font-family:var(--font-mono);font-size:12px;min-width:0}.fn .fd{color:var(--text-muted)}
.frow{padding:6px 10px;gap:8px;border:1px solid var(--border-subtle);border-radius:8px;margin-bottom:4px;background:var(--surface-raised)}
.frow:last-child{border-bottom:1px solid var(--border-subtle)}.frow.sel{border-color:rgba(76,141,255,.55)}.frow .fn{flex:0 1 auto}.frow .sp{flex:1}
.frow .vs{height:22px;min-width:0;padding:0 7px;font-size:10px;letter-spacing:.04em}
.frow .btn.icon{width:28px;min-width:28px;height:28px}
.frow.del .fn{text-decoration:line-through;opacity:.75}
.fl{display:flex;flex-direction:column;min-height:0;overflow:auto;padding-right:2px}
.diff{overflow:auto}.diff .dbody{min-width:max-content}
.dv{display:flex;flex-direction:column;min-width:0;min-height:0;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px;padding:15px}
.dv-h{display:flex;flex-direction:column;gap:2px}.dv-h b{font-size:var(--fs-body-lg)}.dv-h span{font-size:12px;color:var(--text-muted);font-family:var(--font-mono)}
.dv-sep{height:1px;background:var(--border-subtle);margin:8px 0 10px}.dv-body{flex:1;min-height:0;position:relative;display:flex;flex-direction:column}
.dv-body .diff{flex:1;min-height:160px}.dv-empty{flex:1;min-height:160px;display:grid;place-items:center;text-align:center;color:var(--text-muted);gap:6px;align-content:center}.dv-empty .ic{width:34px;height:34px;opacity:.5}
.split{display:grid;grid-template-columns:minmax(260px,5fr) minmax(0,7fr);gap:14px;min-height:0;flex:1}
.fill{height:100%;min-height:0;display:flex;flex-direction:column;gap:14px}
.wh{display:flex;align-items:center;gap:12px}.wh .ic{color:var(--accent)}.wh h2{font-size:22px;font-weight:700}.wh small{display:block;color:var(--text-muted);font-size:13px;font-weight:400}
.ctx{position:fixed}.shell .ctx{position:absolute}
mark.m{background:#e6b84d;color:#000;border-radius:3px;padding:0 2px}
.statebar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:6px 10px;border:1px dashed var(--border-strong);border-radius:8px;background:var(--surface-shell);font-size:11px;color:var(--text-muted)}
.statebar .btn-group{flex-wrap:wrap}.statebar .btn-group .btn{height:24px;min-width:0;padding:0 9px;font-size:11px}
`;

  // ------------------------------------------------------------------ data helpers
  const ST = {
    M: '<span class="badge modified">MODIFIED</span>',
    A: '<span class="badge new">NEW</span>',
    D: '<span class="badge deleted">DELETED</span>',
  };
  const CHANGES = [
    ['src/Services/PaymentService.php', 'M'],
    ['config/payment.php', 'A'],
    ['database/migrations/2026_11_02_000100_create_coupons_table.php', 'A'],
    ['resources/js/checkout.js', 'M'],
    ['public/css/app.css', 'M'],
    ['resources/views/cart/index.blade.php', 'M'],
    ['public/img/legacy-banner.png', 'D'],
    ['composer.json', 'M'],
  ];
  const fileIc = (name) => ic(fx(name) === 'img' ? 'image' : fx(name) === 'other' ? 'file-text' : 'file-code', `fx-${fx(name)}`);
  const pathHtml = (p) => { const i = p.lastIndexOf('/'); return i < 0 ? esc(p) : `<span class="fd">${esc(p.slice(0, i + 1))}</span>${esc(p.slice(i + 1))}`; };
  const frow = (p, st, o = {}) => `<div class="list-item frow ${st === 'D' ? 'del' : ''} ${o.sel ? 'sel' : ''}" ${o.r ? `data-r="${o.r}"` : ''} ${o.dbl ? `ondblclick="location.href='${o.dbl}'"` : ''}>
${o.status === 'first' ? ST[st] : ''}${o.chk === false ? '' : `<label class="check"><input type="checkbox" ${o.on === false ? '' : 'checked'}></label>`}${fileIc(p)}<span class="fn ellipsis" title="${esc(p)}">${pathHtml(p)}</span>
<button type="button" class="btn compact vs" title="Open this file in Visual Studio / configured editor">VS</button><span class="sp"></span>${o.extra || ''}${o.status === 'first' ? '' : (o.rs || '') + ST[st]}</div>`;

  // unified diff builder: '@@ ...' hunk, '+x' add, '-x' del, ' x' context
  const mkdiff = (text, o = 1, n = 1) => {
    const rows = text.split('\n').map((l) => {
      if (l.startsWith('@@')) { const m = /@@ -(\d+)\S* \+(\d+)/.exec(l); if (m) { o = +m[1]; n = +m[2]; } return `<div class="dl hunk"><span></span><span></span><span>${esc(l)}</span></div>`; }
      const c = l[0], t = esc(l.slice(1));
      if (c === '+') return `<div class="dl add"><span></span><span>${n++}</span><span>${t}</span></div>`;
      if (c === '-') return `<div class="dl del"><span>${o++}</span><span></span><span>${t}</span></div>`;
      return `<div class="dl"><span>${o++}</span><span>${n++}</span><span>${t}</span></div>`;
    });
    return `<div class="diff"><div class="dbody">${rows.join('')}</div></div>`;
  };
  const DIFF_PHP = mkdiff(`@@ -38,12 +38,24 @@ final class PaymentService
     public function charge(Order $order, ?Coupon $coupon = null): PaymentResult
     {
         $amount = $order->total();
+
+        if ($coupon !== null) {
+            $amount = $this->coupons->apply($coupon, $amount);
+            $this->log->info('coupon applied', ['code' => $coupon->code, 'amount' => $amount]);
+        }

         $response = $this->gateway->createCharge([
             'amount'   => $amount,
-            'currency' => 'USD',
+            'currency' => config('payment.currency', 'USD'),
             'order_id' => $order->id,
         ]);
@@ -71,7 +83,9 @@ final class PaymentService
     private function assertConfigured(): void
     {
-        if (empty(env('PAYMENT_KEY'))) {
+        if (empty(config('payment.key'))) {
             throw new RuntimeException('Payment gateway is not configured.');
         }
+
+        $this->log->debug('gateway ready');
     }`, 38, 38);
  const DIFF_JS = mkdiff(`@@ -12,7 +12,10 @@ export function initCheckout(form) {
   const total = form.querySelector('[data-total]');
-  form.addEventListener('submit', () => spinner.show());
+  form.addEventListener('submit', (e) => {
+    if (!validate(form)) { e.preventDefault(); return; }
+    spinner.show();
+  });
   coupon.addEventListener('change', recalc);`, 12, 12);

  const dv = ({ title = 'Diff preview', status = '', body = '', empty = 'Select a file to see the diff', cls = '' }) => `<div class="dv ${cls}"><div class="dv-h"><b>${title}</b>${status ? `<span>${status}</span>` : ''}</div><div class="dv-sep"></div><div class="dv-body">${body || `<div class="dv-empty">${ic('diff')}<div>${empty}</div></div>`}</div></div>`;

  // streaming helper with prefilled lines (terminal look)
  const tline = (cls, t, msg) => `<div><span class="m">${t}</span> <span class="${cls}">${esc(msg)}</span></div>`;
  const stream = (initial, lines, interval = 1100, style = '') =>
    L.stream(lines, { interval, style }).replace('<span class="cursor"></span>', initial.map(([c, t, m]) => tline(c, t, m)).join('') + '<span class="cursor"></span>');

  // ------------------------------------------------------------------ transfer monitor (TransferMonitorPanel)
  const monitor = (cls = '') => `<div class="card dense raised tm ${cls}" style="box-shadow:var(--shadow-card);border-color:var(--border-strong)">
<div class="row"><b>Transfer</b><span class="badge accent"><span class="dot live blue"></span> Uploading</span><span class="grow"></span><button class="btn compact ghost" type="button">Hide</button></div>
<div class="mono muted" style="font-size:11px;margin:6px 0 2px">3/4 workers · 3/8 files · Uploading</div>
<div class="mono" style="font-size:11px;color:var(--text-secondary)">18.4 MB / 52.9 MB (34.8%)</div>
${L.progress(35, 'lg busy', 'data-progress="35" data-speed="1.2" data-loop')}
<div class="eyebrow" style="margin:10px 0 6px">Workers</div>
<div class="stack sm">
${[['#1', 'Uploading', 'public/css/app.css', 62, 'accent'], ['#2', 'Uploading', 'resources/js/checkout.js', 40, 'accent'], ['#3', 'Uploading', 'src/Services/PaymentService.php', 18, 'accent'], ['#4', 'Idle', 'waiting for next file', 0, '']].map(([id, s, f, p, c]) => `<div class="row" style="gap:8px"><span class="mono muted" style="width:22px">${id}</span><span class="badge ${c}" style="width:76px;justify-content:center">${s}</span><span class="mono ellipsis grow" style="font-size:11px" title="${f}">${f}</span><span class="progress" style="width:60px"><i ${p ? `data-progress="${p}" data-speed="2" data-loop` : ''} style="width:${p}%"></i></span></div>`).join('')}
</div>
${stream([['u', '14:02:11', 'connected sftp://deploy@203.0.113.45:22'], ['u', '14:02:12', 'uploaded config/payment.php (1.2 KB)'], ['w', '14:02:13', 'retry 1/3 public/img/hero.jpg (timeout)']], [['u', 'uploaded public/css/app.css (212 KB)'], ['u', 'uploaded database/migrations/2026_11_02_000100_create_coupons_table.php'], ['i', 'verify size ok: resources/js/checkout.js'], ['w', 'slow worker #3, 180 KB/s']], 1500, 'height:92px;margin-top:10px;font-size:11px')}
<div class="hint" style="margin-top:6px">Hide only closes this card. Transfer keeps running.</div></div>`;

  // =================================================================================================
  //  DeployRemoteWorkspaceControl
  // =================================================================================================
  const tmap = (n) => { const k = fx(n); return k === 'other' ? '' : 't-' + k; };
  const rn = (name, o = {}) => {
    const { dir = false, open = false, sel = false, msel = false, meta = '', kids = '', hit = false, anc = false, chk = false, mark = '' } = o;
    const ext = !dir && name.includes('.') ? name.split('.').pop().toUpperCase().slice(0, 4) : '';
    const nm = mark ? esc(name).replace(new RegExp('(' + mark + ')', 'i'), '<mark class="m hl">$1</mark>') : esc(name);
    const tn = `<div class="tn rn ${dir ? 'dir' : 'file'} ${open ? 'open' : ''} ${tmap(name)} ${sel ? 'sel' : ''} ${msel ? 'msel' : ''} ${hit ? 'hit' : ''}" data-ctx="${dir ? 'rwCtxDir' : (msel ? 'rwCtxMulti' : 'rwCtxFile')}" ${dir ? '' : `data-dbl=".rw|data-state|editor"`}><span class="exp">${dir ? ic('chevron-right') : ''}</span><label class="check cb"><input type="checkbox" ${chk ? 'checked' : ''}></label>${ic(dir ? (open ? 'folder-open' : 'folder') : fx(name) === 'img' ? 'image' : fx(name) === 'other' ? 'file-text' : 'file-code', 'fi')}${ext ? `<span class="xb">${ext}</span>` : ''}<span class="nm">${nm}</span>${meta ? `<span class="meta">${meta}</span>` : ''}</div>`;
    return dir ? `<div class="tg ${anc ? 'anc' : ''}">${tn}<div class="tchildren">${kids}</div></div>` : tn;
  };
  const rtree = () => rn('public_html', {
    dir: true, open: true, anc: true, kids:
      rn('app', { dir: true, open: true, anc: true, kids:
        rn('Http', { dir: true, open: true, anc: true, kids:
          rn('Controllers', { dir: true, open: true, anc: true, kids:
            rn('CartController.php', { meta: '4.2 KB' }) + rn('PaymentController.php', { meta: '6.8 KB', hit: true, mark: 'payment', chk: true }) + rn('OrderController.php', { meta: '9.1 KB' })
          }) + rn('Middleware', { dir: true, kids: rn('Authenticate.php', { meta: '0.9 KB' }) })
        }) +
        rn('Services', { dir: true, open: true, anc: true, kids:
          rn('InvoiceService.php', { meta: '3.1 KB' }) + rn('PaymentService.php', { meta: '11.4 KB', sel: true, hit: true, mark: 'payment', chk: true })
        })
      }) +
      rn('config', { dir: true, open: true, anc: true, kids: rn('app.php', { meta: '8.0 KB' }) + rn('payment.php', { meta: '1.2 KB', hit: true, mark: 'payment', msel: false, chk: true }) }) +
      rn('public', { dir: true, open: true, kids:
        rn('css', { dir: true, kids: rn('app.css', { meta: '212 KB' }) }) + rn('js', { dir: true, kids: rn('checkout.js', { meta: '18.6 KB' }) }) +
        rn('index.php', { meta: '1.7 KB' }) + rn('.htaccess', { meta: '0.4 KB' }) + rn('logo.png', { meta: '34 KB' })
      }) +
      rn('storage', { dir: true, kids: rn('logs', { dir: true }) }) +
      rn('vendor', { dir: true, kids: rn('autoload.php', { meta: '0.8 KB' }) }) +
      rn('composer.json', { meta: '2.4 KB' }) + rn('.env', { meta: '0.6 KB' }) + rn('README.md', { meta: '1.1 KB' })
  });

  const RW_LOG = [['u', '14:01:58', 'Connected to Production (203.0.113.45).'], ['i', '14:01:59', 'Loaded /public_html (14 items).'], ['i', '14:02:07', 'Expanded /public_html/app/Services.']];
  const phpCode = `<span class="tok-k">&lt;?php</span>

<span class="tok-k">declare</span>(strict_types=<span class="tok-n">1</span>);

<span class="tok-k">namespace</span> App\\Services;

<span class="tok-k">final class</span> <span class="tok-f">PaymentService</span>
{
    <span class="tok-k">public function</span> <span class="tok-f">charge</span>(Order $order, ?Coupon $coupon = <span class="tok-k">null</span>): PaymentResult
    {
        $amount = $order-&gt;<span class="tok-f">total</span>();

        <span class="tok-k">if</span> ($coupon !== <span class="tok-k">null</span>) {
            $amount = $this-&gt;coupons-&gt;<span class="tok-f">apply</span>($coupon, $amount);
        }

        $response = $this-&gt;gateway-&gt;<span class="tok-f">createCharge</span>([
            <span class="tok-s">'amount'</span>   =&gt; $amount,
            <span class="tok-s">'currency'</span> =&gt; <span class="tok-f">config</span>(<span class="tok-s">'payment.currency'</span>, <span class="tok-s">'USD'</span>),
        ]);
        <span class="tok-c">// TODO: store gateway reference</span>
        <span class="tok-k">return</span> PaymentResult::<span class="tok-f">from</span>($response);
    }
}`;
  const codeBox = (src, lines = 23) => `<div class="code" style="flex:1;min-height:0"><div class="gut">${Array.from({ length: lines }, (_, i) => i + 1).join('<br>')}</div><pre>${src}</pre></div>`;

  const remote = ({ standalone = false } = {}) => `<div class="rw" data-state="browse" data-log="open">
${standalone ? `<div class="statebar" style="margin-bottom:10px"><span>Preview state</span><div class="btn-group" data-grp>
<button type="button" class="btn active" data-set=".rw|data-state|browse">Connected</button><button type="button" class="btn" data-set=".rw|data-state|connecting">Connecting</button><button type="button" class="btn" data-set=".rw|data-state|idle">Disconnected</button><button type="button" class="btn" data-set=".rw|data-state|empty">Empty folder</button><button type="button" class="btn" data-set=".rw|data-state|checks">Checkbox mode</button><button type="button" class="btn" data-set=".rw|data-state|search">Type-to-search</button><button type="button" class="btn" data-set=".rw|data-state|download">Downloading</button><button type="button" class="btn" data-set=".rw|data-state|editor">Editor</button></div><span>double-click a file to open it in the editor · right-click tree rows for the real context menus</span></div>` : ''}
<div class="rw-box">
<div class="rw-conn">
 <div class="rw-tools">
  <button type="button" class="btn icon compact ghost" title="Change the default FTP used by Commit and Sync" data-toast="success" data-title="Default FTP" data-msg="Production is the default FTP for this project">${ic('star')}</button>
  <a class="btn icon compact ghost" title="Edit connections" href="connection-manager-window.html">${ic('more')}</a>
  <button type="button" class="btn icon compact ghost" title="Refresh the selected folder only (does not reset the FTP tree)" data-set=".rw|data-state|connecting" data-revert="1800~.rw|data-state|browse">${ic('refresh')}</button>
  <button type="button" class="btn icon compact ghost rw-cbm" title="Checkbox mode: pick files/folders from different branches, then download together (mapping respected)." data-flip=".rw|data-state|checks|browse">${ic('check-circle')}</button>
  <button type="button" class="btn icon compact ghost rw-ck" title="Select all in remote tree">${ic('check-circle')}</button>
  <button type="button" class="btn icon compact ghost rw-ck" title="Clear all checkboxes">${ic('x-square')}</button>
  <button type="button" class="btn icon compact ghost rw-ck" title="Download checked items" data-set=".rw|data-state|download">${ic('download')}</button>
  <a class="btn icon compact ghost" title="Pull files from the shared sync manifest on FTP (.gitdeploy.sync.json)" href="sync-preview-window.html">${ic('swap')}</a>
  <button type="button" class="btn icon compact ghost rw-bolt" title="Connect to remote host" data-set=".rw|data-state|connecting" data-revert="2600~.rw|data-state|browse">${ic('zap')}</button>
  <button type="button" class="btn icon compact ghost rw-disc" title="Disconnect from remote host" data-set=".rw|data-state|idle">${ic('power')}</button>
 </div>
 <select class="select sm"><option selected>Production — 203.0.113.45 (SFTP)</option><option>Staging — staging.example.com (FTP)</option><option>Client cPanel — ftp.client.io (FTPS)</option></select>
 <div class="rw-status muted">
  <span class="s s-browse"><span class="dot ok"></span> Connected to Production (203.0.113.45).</span>
  <span class="s s-connecting"><svg class="ic spin"><use href="#i-spinner"/></svg> Connecting to Production (203.0.113.45)...</span>
  <span class="s s-idle">Remote workspace is idle.</span>
  <span class="s s-empty"><span class="dot ok"></span> Nothing to download (empty folder).</span>
  <span class="s s-checks"><span class="dot ok"></span> 3 item(s) checked.</span>
  <span class="s s-search"><span class="dot ok"></span> 3 match(es) for "payment".</span>
  <span class="s s-download"><span class="dot live blue"></span> Downloading 3 item(s)...</span>
  <span class="s s-editor"><span class="dot ok"></span> Connected to Production (203.0.113.45).</span>
 </div>
</div>
<div class="rw-main">
 <div class="rw-browser">
  <div class="rw-bh"><b>Remote files</b>
   <div class="banner info rw-map">${ic('link')}<span>Path mapping is active: local <code>src</code> → remote <code>/public_html/app</code> (browsing <code>/home/deploy/public_html</code>).</span></div></div>
  <div class="rw-search"><div class="input-icon">${ic('search')}<input class="input sm mono" value="payment" placeholder="Type to search…"></div><button type="button" class="btn icon compact ghost" title="Clear" data-set=".rw|data-state|browse">${ic('x')}</button></div>
  <div class="rw-treewrap">
   <div class="tree rw-tree" data-selectable>${rtree()}</div>
   <div class="rw-empty">${ic('folder-open')}<b>No files loaded yet</b><span class="s-a">Connect to load remote files.</span><span class="s-b">Remote directory is empty or waiting for refresh.</span></div>
   <div class="rw-loading"><div class="card raised" style="width:260px">
     <div class="row">${ic('refresh', 'spin accent')}<b>Connecting to remote...</b></div>
     <div class="muted" style="margin:6px 0 10px">Please wait while we fetch files.</div>
     ${L.progress(0, 'indeterminate', '')}
     <button type="button" class="btn compact block" style="margin-top:10px" title="Stop this connection attempt" data-set=".rw|data-state|idle">${ic('stop')}<span class="bl">Stop</span></button>
     <div class="stack sm" style="margin-top:12px"><div class="skeleton" style="height:6px"></div><div class="skeleton" style="height:6px;width:200px;max-width:80%"></div><div class="skeleton" style="height:6px;width:170px;max-width:70%"></div></div></div></div>
  </div>
  <div class="rw-dl dlp running" data-worker>
   <div class="row"><span class="mono ellipsis grow" style="font-size:12px" title="app/Services/PaymentService.php">${ic('download', 'accent')} app/Services/PaymentService.php</span><b class="mono" data-pct>42%</b>
   <button type="button" class="btn compact ghost" title="Open the download report in a floating window">Report</button><button type="button" class="btn compact ghost" data-set=".rw|data-state|browse">Cancel</button></div>
   <div class="progress busy" style="margin-top:6px"><i data-progress="42" data-speed="1.6" data-loop style="width:42%"></i></div>
   <div class="mono muted" style="font-size:11px;margin-top:4px">Item 1 of 3 · 4.8 MB / 11.4 MB · 1.2 MB/s</div>
  </div>
 </div>
 <div class="rw-editor">
  <div class="rw-eh">
   <div class="row"><button type="button" class="btn icon compact ghost" title="Close editor" data-set=".rw|data-state|browse">${ic('x')}</button>
    <span class="mono ellipsis grow" style="font-size:12px" title="/public_html/app/Services/PaymentService.php">/public_html/app/Services/PaymentService.php</span>
    <button type="button" class="btn icon compact ghost" title="Reload this file from FTP" data-confirm="This file has unsaved changes. Refresh will reload it from FTP and discard those changes. Continue?" data-confirm-title="Refresh editor">${ic('refresh')}</button>
    <button type="button" class="btn icon compact ghost" title="Revert unsaved editor changes">${ic('undo')}</button>
    <button type="button" class="btn icon compact ghost" title="Float — open this file in a separate window">${ic('external')}</button>
    <button type="button" class="btn icon compact" style="border-color:var(--warning);color:var(--warning)" title="Save and upload the current file" data-working="Uploading…" data-done="Uploaded" data-done-msg="PaymentService.php → /public_html/app/Services/">${ic('upload')}</button></div>
   <div class="muted" style="font-size:12px;margin-top:6px">Loaded 11.4 KB · UTF-8 · LF · modified locally (unsaved)</div>
   <div class="rw-ups"><div class="row"><span class="mono" style="font-size:12px">Uploading PaymentService.php…</span><span class="muted ellipsis grow" style="font-size:11px">11.4 KB · 1 worker · verify size</span></div>
    <div class="progress busy" style="margin-top:5px"><i data-progress="55" data-speed="1.5" data-loop style="width:55%"></i></div></div>
  </div>
  <div class="rw-etabs"><span class="etab on mono">PaymentService.php ${ic('x')}</span><span class="etab mono">config/payment.php ${ic('x')}</span><span class="etab mono">.env ${ic('x')}</span></div>
  <div class="rw-ebody">${codeBox(phpCode, 23)}</div>
 </div>
</div>
<div class="rw-log">
 <div class="row" style="padding:0 8px;height:30px"><b style="font-size:12px">Remote log</b><span class="grow"></span><button type="button" class="btn compact ghost" style="height:24px;font-size:11px" title="Show or hide the FTP/SFTP activity log" data-flip=".rw|data-log|open|closed"><span class="t-show">Show log</span><span class="t-hide">Hide log</span></button></div>
 <div class="rw-logbody">${stream(RW_LOG, [['i', 'LIST /public_html/storage'], ['u', 'Loaded /public_html/storage (3 items).'], ['i', 'STAT /public_html/.env'], ['w', 'Permission denied: /public_html/storage/logs/laravel.log']], 2400, 'max-height:120px;font-size:11px;border-radius:0;border:0;background:transparent')}</div>
</div>
</div>
<div class="rw-mon">${monitor()}</div>
</div>`;

  const RW_CSS = `
.rw{position:relative;height:100%;display:flex;flex-direction:column;min-height:0;min-width:0}
.rw-box{flex:1;min-height:0;display:flex;flex-direction:column;background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:10px;overflow:hidden}
.rw-conn{padding:8px;border-bottom:1px solid var(--border-subtle);background:var(--surface-shell);display:flex;flex-direction:column;gap:6px}
.rw-tools{display:flex;gap:2px;justify-content:flex-end;flex-wrap:wrap}
.rw-status{font-size:11px;min-height:18px}.rw-status .s{display:none;align-items:center;gap:6px}
.rw[data-state=browse] .s-browse,.rw[data-state=connecting] .s-connecting,.rw[data-state=idle] .s-idle,.rw[data-state=empty] .s-empty,.rw[data-state=checks] .s-checks,.rw[data-state=search] .s-search,.rw[data-state=download] .s-download,.rw[data-state=editor] .s-editor{display:flex}
.rw-main{flex:1;min-height:0;display:flex;flex-direction:column;position:relative}
.rw-browser,.rw-editor{flex:1;min-height:0;display:flex;flex-direction:column}
.rw-editor{display:none}.rw[data-state=editor] .rw-editor{display:flex}.rw[data-state=editor] .rw-browser{display:none}
.rw-bh{padding:8px 10px 0;display:flex;flex-direction:column;gap:6px}.rw-bh b{font-size:13px}
.rw-map{padding:6px 10px;font-size:11px;line-height:1.35;align-items:flex-start}.rw-map code{font-size:11px;color:var(--accent)}
.rw-search{display:none;gap:4px;padding:6px 10px 0}.rw[data-state=search] .rw-search{display:flex}.rw-search .input-icon{flex:1}
.rw-treewrap{flex:1;min-height:0;position:relative;overflow:auto;padding:6px}
.rw-tree .cb{display:none}.rw[data-state=checks] .rw-tree .cb,.rw[data-state=download] .rw-tree .cb{display:inline-flex}.rw-tree .cb input{width:14px;height:14px}
.rw-tree .tn{font-size:12px;height:26px}.rw-tree .tn.msel{background:var(--surface-pressed);color:var(--text-primary)}
.xb{font-size:9px;font-weight:700;letter-spacing:.04em;padding:1px 4px;border-radius:4px;border:1px solid var(--border-strong);background:var(--surface-input);color:var(--text-muted);font-family:var(--font-mono)}
.rw-tree .tn .meta{font-size:10px}
.rw-tree .hl{background:none;color:inherit;padding:0}.rw[data-state=search] .rw-tree .hl{background:#e6b84d;color:#000;padding:0 2px}
.rw[data-state=search] .rw-tree .tn.file:not(.hit){display:none}.rw[data-state=search] .rw-tree .tg:not(.anc){display:none}
.rw-empty{position:absolute;inset:0;display:none;place-items:center;align-content:center;text-align:center;gap:6px;color:var(--text-muted);background:var(--surface-card)}
.rw-empty .ic{width:42px;height:42px;opacity:.5}.rw-empty b{color:var(--text-secondary)}.rw-empty .s-b{display:none}
.rw[data-state=idle] .rw-empty,.rw[data-state=empty] .rw-empty{display:grid}.rw[data-state=empty] .rw-empty .s-a{display:none}.rw[data-state=empty] .rw-empty .s-b{display:inline}
.rw-loading{position:absolute;inset:0;display:none;place-items:center;background:rgba(3,6,12,.72);backdrop-filter:blur(2px)}.rw[data-state=connecting] .rw-loading{display:grid}
.rw-dl{display:none;padding:8px 10px;border-top:1px solid var(--border-subtle);background:var(--surface-shell)}.rw[data-state=download] .rw-dl{display:block}
.rw-mon{display:none;position:absolute;right:10px;bottom:52px;width:min(360px,calc(100% - 20px));z-index:25;max-height:420px;overflow:auto}.rw[data-state=download] .rw-mon{display:block}
.rw-eh{padding:8px 10px;border-bottom:1px solid var(--border-subtle);background:var(--surface-shell)}
.rw-ups{margin-top:8px;padding:6px 8px;border:1px solid rgba(76,141,255,.35);background:var(--accent-soft);border-radius:8px}
.rw-etabs{display:flex;gap:4px;padding:6px 8px;overflow-x:auto;border-bottom:1px solid var(--border-subtle)}
.etab{display:inline-flex;align-items:center;gap:6px;height:26px;padding:0 8px;border:1px solid var(--border-subtle);border-radius:6px;font-size:11px;color:var(--text-secondary);white-space:nowrap}.etab .ic{width:11px;height:11px}
.etab.on{background:var(--accent);border-color:var(--accent);color:#fff}
.rw-ebody{flex:1;min-height:0;display:flex;padding:8px}.rw-ebody .code{width:100%}
.rw-log{border-top:1px solid var(--border-subtle);background:var(--surface-shell)}.rw-logbody{display:none}.rw[data-log=open] .rw-logbody{display:block}
.rw .t-hide{display:none}.rw[data-log=open] .t-hide{display:inline}.rw[data-log=open] .t-show{display:none}
.rw-ck,.rw-disc{display:none}.rw[data-state=checks] .rw-ck,.rw[data-state=download] .rw-ck{display:inline-grid}.rw[data-state=checks] .rw-cbm,.rw[data-state=download] .rw-cbm{color:var(--accent);background:var(--accent-soft)}
.rw[data-state=browse] .rw-disc,.rw[data-state=checks] .rw-disc,.rw[data-state=search] .rw-disc,.rw[data-state=download] .rw-disc,.rw[data-state=empty] .rw-disc,.rw[data-state=editor] .rw-disc{display:inline-grid}
.rw[data-state=browse] .rw-bolt,.rw[data-state=checks] .rw-bolt,.rw[data-state=search] .rw-bolt,.rw[data-state=download] .rw-bolt,.rw[data-state=empty] .rw-bolt,.rw[data-state=editor] .rw-bolt{display:none}
.rw[data-state=idle] .rw-tools .btn[title^=Refresh],.rw[data-state=idle] .rw-cbm{opacity:.4}
.rw-dl .progress{height:6px}
`;
  const ctxMenus = `
<div class="ctx" id="rwCtxFile"><div class="menu">
 <div class="mi">${ic('folder-open')}Open</div><div class="mi">${ic('folder-plus')}New Folder</div><div class="mi">${ic('file-plus')}New File</div><div class="mi">${ic('download')}Download File</div>
 <div class="msep"></div><div class="mi">${ic('edit')}Rename</div><div class="mi">${ic('arrow-right')}Move</div><div class="mi danger" data-confirm="Delete '/public_html/app/Services/PaymentService.php' from the server?" data-confirm-title="Delete">${ic('trash')}Delete</div>
 <div class="msep"></div><div class="mi">${ic('lock')}Permissions</div><div class="mi">${ic('info')}Properties</div></div></div>
<div class="ctx" id="rwCtxDir"><div class="menu">
 <div class="mi">${ic('refresh')}Refresh</div><div class="mi" style="opacity:.4">${ic('folder-open')}Open</div><div class="mi">${ic('folder-plus')}New Folder</div><div class="mi">${ic('file-plus')}New File</div><div class="mi">${ic('download')}Download Folder</div>
 <div class="msep"></div><div class="mi">${ic('edit')}Rename</div><div class="mi">${ic('arrow-right')}Move</div><div class="mi danger" data-confirm="Delete folder '/public_html/app/Services' and everything inside it?" data-confirm-title="Delete">${ic('trash')}Delete</div>
 <div class="msep"></div><div class="mi">${ic('swap')}Mapping</div>
 <div class="msep"></div><div class="mi">${ic('lock')}Permissions</div><div class="mi">${ic('info')}Properties</div>
 <div class="msep"></div><div class="mi">${ic('download')}Download checked items (3)</div></div></div>
<div class="ctx" id="rwCtxMulti"><div class="menu">
 <div class="mi">${ic('download')}Download (3 items)</div><div class="msep"></div><div class="mi danger" data-confirm="Delete 3 selected items from the server?" data-confirm-title="Delete">${ic('trash')}Delete (3 items)</div></div></div>`;

  L.page({
    slug: 'deploy-remote-workspace-control', title: 'DeployRemoteWorkspaceControl', nav: 'deploy', group: 'Controls', flush: false,
    css: BASE_CSS + RW_CSS + `.rwp{display:grid;grid-template-columns:minmax(0,520px) minmax(0,1fr);gap:20px;height:calc(100vh - 150px);min-height:540px}.rwp .rw{min-height:0}@media(max-width:1200px){.rwp{grid-template-columns:minmax(0,1fr)}}.legend{align-self:start}`,
    body: `<div class="page-head"><div><h1 class="page-title">FTP / Remote Host</h1><div class="page-sub">DeployRemoteWorkspaceControl — remote file tree, checkbox download mode, inline editor and operation log. Docked on the right of the Deploy page.</div></div>
<div class="r"><a class="btn" href="deploy.html">${ic('rocket')}<span class="bl">Back to Deploy</span></a></div></div>
<div class="rwp">${remote({ standalone: true })}
<div class="stack legend">${L.card('Context menus', `<div class="stack sm"><div class="muted">Right-click any row in the tree. The menu changes with the selection:</div>
<div class="row wrap"><span class="chip">File</span><span class="chip">Folder</span><span class="chip">Multi-selection</span></div>
<div class="hint">Root folder: Rename / Move / Delete are disabled. Folder with checked items adds “Download checked items (n)”.</div></div>`, { icon: 'list' })}
${L.card('Workflow', `<div class="stack sm"><div class="row">${ic('zap', 'accent')}<span>Connect → tree loads lazily per folder</span></div><div class="row">${ic('check-circle', 'accent')}<span>Checkbox mode → pick items from different folders → Download</span></div><div class="row">${ic('swap', 'accent')}<span>Sync manifest → <a href="sync-preview-window.html">Sync preview</a> / <a href="sync-manifest-editor-window.html">Edit manifest</a></span></div><div class="row">${ic('edit', 'accent')}<span>Double-click a file → inline editor, Save and upload ${ic('upload')}</span></div></div>`, { icon: 'info' })}</div>
</div>${ctxMenus}${SCRIPT}`,
  });

  // =================================================================================================
  //  DeployPage
  // =================================================================================================
  const acts = [
    ['compare', 'search', 'COMPARE', 'warning'],
    ['commit', 'file-text', 'COMMIT &amp; REVIEW', 'secondary'],
    ['pull', 'download', 'PULL &amp; DEPLOY (2)', 'secondary'],
    ['push', 'cloud', 'PUSH (3)', 'shell'],
    ['connect', 'cloud', 'CONNECT + PUSH', 'shell'],
    ['nopush', 'check-circle', 'NOTHING TO PUSH', 'ok'],
    ['idle', 'clock', 'NOTHING TO COMMIT', 'off'],
    ['synced', 'check-circle', 'SYNCED', 'ok'],
    ['busy', 'spinner', 'Processing...', 'busy'],
  ];
  const actMap = { compare: ['compare', 'review'], commit: ['commit'], pull: ['pull'], push: ['push'], connect: ['connect'], nopush: ['nopush'], idle: ['idle'], synced: ['synced'], busy: ['deploying'] };
  const actBtn = ([k, i, label, tone]) => {
    const attrs = { commit: 'href="commit-window.html"', pull: 'data-confirm="Remote has 2 new commit(s) (~6 file(s)). Pull and deploy only those files to the server?" data-confirm-title="Pull &amp; Deploy"', push: 'data-working="Pushing..." data-done="Pushed to GitHub" data-done-msg="origin/master is up to date"', compare: 'data-set=".dp|data-act|review"', connect: 'data-working="Connecting..."' }[k] || '';
    const inner = k === 'commit'
      ? `<span class="bl t1">${ic(i)} COMMIT &amp; REVIEW</span><span class="bl t2">${ic('zap')} DEPLOY → COMMIT + PUSH</span>`
      : `${ic(i, k === 'busy' ? 'spin' : '')}<span class="bl">${label}</span>`;
    const tag = k === 'commit' ? 'a' : 'button';
    return `<${tag} ${tag === 'button' ? 'type="button"' : ''} class="btn act a-${k} tone-${tone}" ${attrs} ${['nopush', 'idle', 'synced', 'busy'].includes(k) ? 'disabled' : ''}>${inner}</${tag}>`;
  };
  const vis = (sel, states, disp = 'inline-flex') => `${states.map((s) => `.dp[data-act="${s}"] ${sel}`).join(',')}{display:${disp}}`;
  const stateCss = acts.map(([k]) => vis(`.a-${k}`, actMap[k])).join('\n') + '\n' +
    vis('.st-compare', ['compare'], 'block') + vis('.st-review', ['review'], 'block') + vis('.st-commit', ['commit'], 'block') + vis('.st-pull', ['pull'], 'block') + vis('.st-push', ['push', 'connect'], 'block') +
    vis('.st-nopush', ['nopush'], 'block') + vis('.st-idle', ['idle'], 'block') + vis('.st-synced', ['synced'], 'block') + vis('.st-busy', ['deploying'], 'block') + '\n' +
    vis('.deploybtn', ['review', 'deploying']) + vis('.pushbadge', ['push', 'connect'], 'flex') + vis('.skipwrap', ['commit']) + vis('.fileslist', ['review', 'commit', 'deploying'], 'flex') + vis('.noFiles', ['compare', 'pull', 'push', 'connect', 'nopush', 'idle', 'synced'], 'grid') + vis('.dp-mon', ['deploying'], 'block') + vis('.pc-idle', ['compare', 'review', 'commit', 'pull', 'push', 'connect', 'nopush', 'idle', 'synced'], 'block') + vis('.pc-run', ['deploying'], 'block');

  const DP_CSS = BASE_CSS + RW_CSS + stateCss + `
.dp{position:relative;display:grid;grid-template-columns:52px var(--lw,0px) minmax(280px,1fr) var(--rw,0px) 52px;grid-template-rows:minmax(0,1fr) auto;height:100%;min-height:0;background:var(--surface-base)}
.dp-rail{background:var(--surface-shell);border-right:1px solid var(--border-subtle);display:flex;flex-direction:column;align-items:center;padding-top:10px;gap:6px;z-index:5}
.dp-rail{grid-column:1;grid-row:1}.dp-left{grid-column:2;grid-row:1}.dp-center{grid-column:3;grid-row:1}.dp-remote{grid-column:4;grid-row:1}
.dp-rail.r{grid-column:5;border-right:0;border-left:1px solid var(--border-subtle)}
.dp-rail .btn.icon{width:36px;height:36px;color:var(--text-secondary);background:transparent;border:0}
.dp-rail .btn.icon:hover{background:var(--surface-hover);color:var(--text-primary)}
.dp[data-left="1"] .rb-up,.dp[data-remote="1"] .rb-rm,.dp[data-btab="logs"][data-bottom="open"] .rb-lg,.dp[data-btab="term"][data-bottom="open"] .rb-tm{background:var(--accent-soft)!important;color:var(--accent)!important}
.dp[data-left="1"]{--lw:clamp(240px,22vw,320px)}.dp[data-remote="1"]{--rw:clamp(340px,30vw,460px)}
.dp-left{display:none;min-width:0;overflow:hidden;background:var(--surface-card);border-right:1px solid var(--border-subtle);min-height:0;flex-direction:column}
.dp[data-left="1"] .dp-left{display:flex}
.dp-left .lh{display:flex;align-items:center;gap:4px;padding:6px 8px;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);flex-wrap:wrap}
.dp-left .lh b{margin-right:auto;font-size:13px}.dp-left .lb{flex:1;min-height:0;overflow:auto;padding:8px;display:flex;flex-direction:column;gap:8px}
.dp-center{min-width:0;min-height:0;display:flex;flex-direction:column;padding:6px 10px 8px;gap:8px;overflow:hidden}
.dp-remote{display:none;min-width:0;min-height:0;padding:6px 6px 8px 0;border-left:0}
.dp[data-remote="1"] .dp-remote{display:block}
.dp-tb{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:8px;padding:8px 10px;flex:none}
.dp-tb-top{display:flex;flex-wrap:wrap;gap:8px 16px;align-items:center;justify-content:space-between}
.dp-tb-left{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.dp-tb-right{display:flex;flex-direction:column;align-items:flex-end;gap:6px;margin-left:auto}
.dp-util{display:flex;align-items:center;gap:6px}
.dp-util .btn.icon{width:28px;height:28px;min-width:28px}
.brsum{height:32px;padding:0 10px;gap:8px;font-size:12px}.brsum .mono{font-weight:600}.brsum .chev{transition:transform .12s}.dp[data-branches="1"] .brsum .chev{transform:rotate(180deg)}
.brdetails{display:none;margin-top:8px;padding:8px;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:6px;grid-template-columns:minmax(120px,1fr) auto minmax(120px,1fr);align-items:center;gap:10px}
.dp[data-branches="1"] .brdetails{display:grid}.brdetails .bf{display:flex;align-items:center;gap:8px;min-width:0}.brdetails .bf label{font-size:11px;font-weight:600;color:var(--text-muted)}
.brdetails .select{height:32px;font-family:var(--font-mono);font-size:12px}
.tone-warning{background:var(--warning);border-color:var(--warning);color:#1a1200}.tone-warning:hover{background:#f3c95f;border-color:#f3c95f}
.tone-secondary{background:var(--accent2);border-color:var(--accent2);color:#fff}.tone-secondary:hover{background:var(--accent2-hover);border-color:var(--accent2-hover)}
.tone-shell{background:var(--surface-shell);border-color:var(--border-strong)}.tone-ok{background:var(--success);border-color:var(--success);color:#03150b}
.tone-off{background:var(--surface-input);color:var(--text-muted)}.tone-busy{background:var(--accent2);border-color:var(--accent2);color:#fff;opacity:.75}
.act{min-width:168px;height:34px;font-size:12px;display:none}.act .bl{display:inline-flex;align-items:center;gap:8px}.act[disabled]{opacity:.7;pointer-events:none}.act.tone-off[disabled]{opacity:.5}
.a-commit .t2{display:none}.dp[data-skip="1"] .a-commit .t1{display:none}.dp[data-skip="1"] .a-commit .t2{display:inline-flex}
.deploybtn{min-width:168px;height:34px;font-size:12px;display:none}.dp[data-act="deploying"] .deploybtn{opacity:.5;pointer-events:none}
.pushbadge{display:none;width:100%;justify-content:center;gap:6px;align-items:center;padding:3px 8px;border:1px solid rgba(230,184,77,.3);border-radius:6px;background:var(--warning-surface);color:var(--warning);font-size:11px;font-weight:600}
.skipwrap{display:none;align-items:center;gap:6px}.skipwrap .check{font-size:11px;font-weight:600;color:var(--text-secondary)}
.dp-scroll{flex:1;min-height:0;overflow:auto;display:flex;flex-direction:column;gap:10px;padding-bottom:2px}
.filescard{flex:1;min-height:220px;display:flex;flex-direction:column}
.filescard .ft{display:flex;align-items:center;gap:12px;flex-wrap:wrap}.filescard .ft h3{font-size:var(--fs-section)}
.st{display:none;font-size:12px;color:var(--text-muted);margin:4px 0 8px}.st-review,.st-commit,.st-pull,.st-push{color:var(--warning)}.st-nopush,.st-synced{color:var(--text-muted)}
.fileslist{display:none;flex:1;min-height:0}
.noFiles{display:none;flex:1;min-height:120px;place-items:center;align-content:center;color:var(--text-muted);gap:6px;text-align:center}.noFiles .ic{width:34px;height:34px;opacity:.45}
.rs{display:inline-flex;align-items:center}.rsi{display:none;align-items:center;gap:4px;font-size:11px}
.frow[data-r=done] .rsi.d,.frow[data-r=run] .rsi.r,.frow[data-r=queue] .rsi.q,.frow[data-r=fail] .rsi.f{display:none}
.dp[data-act=deploying] .frow[data-r=done] .rsi.d,.dp[data-act=deploying] .frow[data-r=run] .rsi.r,.dp[data-act=deploying] .frow[data-r=queue] .rsi.q,.dp[data-act=deploying] .frow[data-r=fail] .rsi.f{display:inline-flex}
.rsi.d{color:var(--success)}.rsi.r{color:var(--accent)}.rsi.q{color:var(--text-muted)}.rsi.f{color:var(--error)}
.dp-mon{display:none;position:absolute;right:calc(52px + 8px);bottom:8px;width:min(380px,92%);z-index:20;max-height:70%;overflow:auto;box-shadow:var(--shadow-card)}
.dp[data-bottom="open"] .dp-mon{bottom:calc(clamp(160px,28vh,300px) + 10px)}
.pcard .pt{font-size:12px;color:var(--text-muted);margin-bottom:6px}.pc-idle,.pc-run{display:none}.pc-run{color:var(--text-primary);font-family:var(--font-mono)}
.dp-bottom{grid-column:1 / -1;grid-row:2;background:var(--surface-card);border-top:1px solid var(--border-strong);display:flex;flex-direction:column;position:relative;z-index:6}
.dp[data-bottom="open"] .dp-bottom{height:clamp(160px,28vh,300px)}.dp[data-bottom="min"] .dp-bottom{height:30px}
.grip{position:absolute;left:0;right:0;top:-4px;height:8px;cursor:ns-resize;z-index:3}.grip:hover{background:var(--accent-soft)}
.bd-h{height:30px;flex:none;display:flex;align-items:center;gap:2px;padding:0 6px;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);position:relative}
.bd-h .lbl2{font-weight:600;font-size:12px;color:var(--text-primary);margin:0 10px 0 4px}
.dtab{height:28px;padding:0 10px;font-size:12px;font-weight:400;display:inline-flex;align-items:center;gap:6px;background:none;border:0;border-bottom:2px solid transparent;color:var(--text-muted);margin-right:2px}
.dtab:hover{color:var(--text-primary);background:var(--surface-hover)}.dtab.on{color:var(--accent);border-bottom-color:var(--accent)}
.dtab .ic{width:12px;height:12px}.bd-h .sp{flex:1}
.bd-b{flex:1;min-height:0;display:flex;padding:8px}.dp[data-bottom="min"] .bd-b{display:none}
.bd-b > div{flex:1;min-height:0;display:none;width:100%}.dp[data-btab="logs"] .bp-logs{display:flex}.dp[data-btab="term"] .bp-term{display:flex}
.bd-b .terminal{flex:1;min-height:0;width:100%}
.tpick{display:none;position:absolute;left:150px;bottom:34px;width:min(420px,calc(100% - 20px));max-height:360px;background:var(--surface-raised);border:1px solid var(--border-strong);border-radius:10px;padding:8px;box-shadow:0 14px 40px rgba(0,0,0,.65);z-index:30}
.tpick.open{display:block}
.tpick .it{display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:6px}.tpick .it:hover,.tpick .it.on{background:var(--accent-soft)}.tpick .it small{display:block;color:var(--text-muted);font-family:var(--font-mono);font-size:10px}
.tpick .it .star{margin-left:auto;color:var(--text-muted)}.tpick .it .star.on{color:var(--warning)}
.dp-left .tree{font-size:12px}.dp-left .tn{height:24px}
@media(max-height:760px){.dp[data-bottom="open"] .dp-bottom{height:clamp(120px,24vh,300px)}}
`;

  const bf = (label, opts, extra = '') => `<div class="bf"><label>${label}</label><select class="select">${opts.map((o, i) => `<option ${i === 0 ? 'selected' : ''}>${o}</option>`).join('')}</select>${extra}</div>`;
  const dpFiles = [
    [CHANGES[0], 'done'], [CHANGES[1], 'done'], [CHANGES[2], 'done'], [CHANGES[3], 'run'], [CHANGES[4], 'run'], [CHANGES[5], 'queue'], [CHANGES[6], 'queue'], [CHANGES[7], 'fail'],
  ];
  const rsHtml = `<span class="rs"><span class="rsi d">${ic('check-circle')}uploaded</span><span class="rsi r">${ic('spinner', 'spin')}uploading</span><span class="rsi q">${ic('clock')}queued</span><span class="rsi f">${ic('x-circle')}failed</span></span>`;

  const dpLogInit = [
    ['m', '14:00:12', 'Compare ready: 8 file(s) loaded in current page.'],
    ['i', '14:00:41', 'Step 1/2: Deploying 8 file(s)...'],
    ['i', '14:00:41', 'Connecting to 203.0.113.45:22 via SFTP...'],
    ['u', '14:00:42', 'Connected!'],
    ['c', '14:00:42', 'Multi path mappings:'],
    ['m', '14:00:42', '   - src -> /public_html/app'],
    ['m', '14:00:42', '   - resources -> /public_html/resources'],
    ['i', '14:00:43', 'Uploading 8 files with 3 live worker(s)...'],
    ['u', '14:00:44', 'Uploaded config/payment.php -> /public_html/config/payment.php'],
    ['u', '14:00:45', 'Uploaded src/Services/PaymentService.php -> /public_html/app/Services/PaymentService.php'],
    ['w', '14:00:46', 'Deleting remote public/img/legacy-banner.png -> /public_html/public/img/legacy-banner.png'],
    ['e', '14:00:47', 'Upload failed composer.json: Permission denied (550)'],
  ];
  const dpLogLive = [['i', 'Uploading resources/js/checkout.js (18.6 KB)'], ['u', 'Uploaded resources/js/checkout.js -> /public_html/resources/js/checkout.js'], ['u', 'Uploaded public/css/app.css -> /public_html/public/css/app.css'], ['w', 'Retry 1/3 resources/views/cart/index.blade.php (timeout)'], ['u', 'Uploaded resources/views/cart/index.blade.php'], ['u', 'All files uploaded successfully!'], ['i', 'Step 2/2: Deploy succeeded, committing and pushing...'], ['u', 'Commit completed.'], ['i', 'Pushing changes to GitHub...'], ['u', 'Successfully pushed to GitHub!']];
  const termInit = [['p', '14:03:20', 'PS C:\\laragon\\www\\myproject> git status -sb'], ['u', '14:03:20', '## master...origin/master [ahead 3]'], ['w', '14:03:20', ' M src/Services/PaymentService.php'], ['u', '14:03:20', '?? config/payment.php'], ['p', '14:03:31', 'PS C:\\laragon\\www\\myproject> npm run build']];
  const termLive = [['i', '> vite build'], ['m', 'vite v5.4.2 building for production...'], ['m', 'transforming (214) resources/js/checkout.js'], ['u', 'built in 3.41s'], ['p', 'PS C:\\laragon\\www\\myproject>']];

  const pickItems = [['Project', 'Local — PowerShell', 'C:\\laragon\\www\\myproject', true], ['', 'Production', 'deploy@203.0.113.45:22', true], ['', 'Staging', 'deploy@staging.example.com:22', false], ['', 'Client cPanel', 'client@ftp.client.io:2222', false]];

  L.page({
    slug: 'deploy', title: 'Deploy', nav: 'deploy', flush: true, css: DP_CSS,
    body: `<div class="dp" data-act="review" data-remote="1" data-left="0" data-branches="1" data-bottom="open" data-btab="logs" data-skip="0">
<aside class="dp-rail">
 <button type="button" class="btn icon rb-up" title="Direct Upload — upload local files to the remote host" data-flip=".dp|data-left|1|0">${ic('upload', 'ic-lg')}</button>
 <button type="button" class="btn icon rb-tm" title="Terminal — open the bottom console terminal" data-set=".dp|data-btab|term;.dp|data-bottom|open">${ic('terminal', 'ic-lg')}</button>
 <button type="button" class="btn icon rb-lg" title="Deploy Logs — show deployment log output" data-set=".dp|data-btab|logs;.dp|data-bottom|open">${ic('clipboard', 'ic-lg')}</button>
</aside>
<section class="dp-left">
 <div class="lh"><b>Direct Upload</b><button type="button" class="btn icon compact ghost" title="Upload actions — start upload / more options">${ic('rocket')}</button><button type="button" class="btn icon compact ghost" title="Refresh from disk — rescan local files">${ic('refresh')}</button><button type="button" class="btn icon compact ghost" title="Collapse Direct Upload panel" data-set=".dp|data-left|0">${ic('x')}</button></div>
 <div class="lb">
  <div class="tree" data-selectable>${L.node('myproject', { dir: true, open: true, children: L.node('src', { dir: true, open: true, children: L.node('Services', { dir: true, open: true, children: L.node('PaymentService.php', { git: 'M', sel: true, meta: '11.4 KB' }) + L.node('InvoiceService.php', { meta: '3.1 KB' }) }) }) + L.node('config', { dir: true, open: true, children: L.node('payment.php', { git: 'A', meta: '1.2 KB' }) + L.node('app.php', { meta: '8.0 KB' }) }) + L.node('resources', { dir: true, open: true, children: L.node('js', { dir: true, children: L.node('checkout.js', { git: 'M' }) }) }) + L.node('composer.json', { git: 'M', meta: '2.4 KB' }) + L.node('legacy-banner.png', { git: 'D' }) })}</div>
  <div class="hint">Legend: green = clean · red = changed · blue = untracked · grey = ignored · green background = mapped</div>
  ${L.worker({ icon: 'upload', name: 'Direct Upload', desc: '3 files · 52.9 MB · 2 workers', pct: 22, speed: 1, stats: ['<b>1.4 MB/s</b>', 'ETA 00:31'] })}
  <a class="btn compact block" href="direct-upload-workspace-control.html">${ic('external')}<span class="bl">Open full Direct Upload</span></a>
 </div>
</section>
<section class="dp-center">
 <div class="statebar"><span>Preview state</span><div class="btn-group" data-grp>
  <button type="button" class="btn" data-set=".dp|data-act|compare">Compare</button><button type="button" class="btn active" data-set=".dp|data-act|review">Compared (SYNC)</button><button type="button" class="btn" data-set=".dp|data-act|commit">Commit</button><button type="button" class="btn" data-set=".dp|data-act|pull">Pull &amp; deploy</button><button type="button" class="btn" data-set=".dp|data-act|push">Push</button><button type="button" class="btn" data-set=".dp|data-act|connect">Connect + push</button><button type="button" class="btn" data-set=".dp|data-act|nopush">Nothing to push</button><button type="button" class="btn" data-set=".dp|data-act|idle">Nothing to commit</button><button type="button" class="btn" data-set=".dp|data-act|synced">Synced</button><button type="button" class="btn" data-set=".dp|data-act|deploying">Deploying…</button></div></div>
 <div class="dp-tb">
  <div class="dp-tb-top">
   <div class="dp-tb-left">
    <button type="button" class="btn ghost brsum" title="Show / hide branch selectors" data-flip=".dp|data-branches|1|0">${ic('git-branch', 'accent')}<span class="mono"><span class="chipb">master</span> → <span class="chipb">production</span></span><span class="badge success" title="2 commits behind origin/master" style="height:18px">↓2</span><span class="badge warning" style="height:18px" title="3 commits ahead">↑3</span>${ic('chevron-down', 'chev muted')}</button>
    <a class="btn ghost" style="height:32px;color:var(--warning);font-size:12px" href="rollback-preview-window.html" title="Preview and roll back the last Git commit (then push)">${ic('undo')}<span class="bl">Rollback</span></a>
   </div>
   <div class="dp-tb-right">
    <div class="dp-util">
     <span class="skipwrap"><label class="check" title="Skip the review modal. Same as Deploy → Commit + Push in the review window."><input type="checkbox" data-flip=".dp|data-skip|0|1">${ic('zap', 'warn ic-sm')}Skip review</label></span>
     <button type="button" class="btn icon ghost skipwrap" title="Default upload workers for Commit + Deploy (1–8). Click to change." data-open="dlgWorkers" style="width:28px">${ic('users')}</button>
     <button type="button" class="btn compact" style="color:var(--error);min-width:48px;height:28px" title="Auto-refresh every 10s. 10s = higher CPU · 30s · 60s · Off = lowest. Click to cycle 10 → 30 → 60 → Off." data-cycle>${ic('clock', 'ic-sm')}<span class="bl" id="arT">10s</span></button>
     <button type="button" class="btn icon compact" style="background:var(--info-surface);border-color:var(--info);color:var(--info);width:30px" title="Sync now (blue) — immediately rescan local changes and fetch remote so Commit / Push / Pull &amp; Deploy update without waiting for the timer." data-working="" data-done="Status refreshed" data-done-msg="Local changes rescanned, remote fetched">${ic('refresh')}</button>
    </div>
    ${acts.map(actBtn).join('')}
    <button type="button" class="btn primary deploybtn" title="Upload selected files to FTP/SFTP first, then sync branches." data-confirm="Deploy 8 selected file(s) to Production and sync master into production?" data-confirm-title="FTP DEPLOY + SYNC">${ic('refresh')}<span class="bl">FTP DEPLOY + SYNC</span></button>
    <div class="pushbadge">${ic('cloud', 'ic-sm')}Push pending: 3 commit(s)</div>
   </div>
  </div>
  <div class="brdetails">
   ${bf('From', ['master', 'main', 'beta', 'development', 'feature/coupons'], `<button type="button" class="btn compact ghost" style="width:28px;min-width:28px;padding:0;font-size:14px;font-weight:700" title="Create New Branch" data-open="dlgBranch">+</button>`)}
   <span class="muted" style="font-size:14px;font-weight:600">→</span>
   ${bf('To', ['production', 'staging', 'master'])}
  </div>
 </div>
 <div class="dp-scroll">
  <div class="card filescard">
   <div class="ft"><h3>Changed Files</h3><span class="badge">8</span><label class="check"><input type="checkbox" checked>Select All</label><span class="grow"></span><button type="button" class="btn icon compact ghost" title="Refresh Git — scan and stage new untracked files (respects .gitignore)" data-working="" data-done="Git refreshed" data-done-msg="Staged 1 new file(s) for Git.">${ic('refresh')}</button></div>
   <div class="st st-compare">Ready to compare changes...</div>
   <div class="st st-review">Review 8 file(s) (2 new local), then click SYNC.</div>
   <div class="st st-commit">You have 8 pending file(s). Review first, then deploy -&gt; commit -&gt; push.</div>
   <div class="st st-pull">Remote is ahead by 2 commit(s). Pull and deploy only those changes.</div>
   <div class="st st-push">You have commits pending push.</div>
   <div class="st st-nopush">Branch is up to date with remote.</div>
   <div class="st st-idle">No commit available yet.</div>
   <div class="st st-synced">Branches are synchronized.</div>
   <div class="st st-busy">Deploying 8 file(s) with 3 live worker(s)...</div>
   <div class="fl fileslist" data-selectable>${dpFiles.map(([[p, s], r], i) => frow(p, s, { r, sel: i === 0, rs: rsHtml })).join('')}</div>
   <div class="noFiles">${ic('git-branch')}<b class="sec">No changes to deploy.</b><span>Select source and target branches, then press the action button.</span></div>
  </div>
  <div class="card dense pcard"><div class="pt pc-idle">Waiting...</div><div class="pt pc-run ellipsis">resources/js/checkout.js</div>
   <div class="progress lg busy"><i data-progress="30" data-speed="1.3" data-loop style="width:30%"></i></div></div>
 </div>
</section>
<section class="dp-remote">${remote()}</section>
<aside class="dp-rail r">
 <button type="button" class="btn icon rb-rm" title="FTP / Remote Host — show or hide the remote file tree" data-flip=".dp|data-remote|1|0">${ic('folder-open', 'ic-lg')}</button>
 <a class="btn icon" title="Edit the sync manifest — select paths to share with other devs" href="sync-manifest-editor-window.html">${ic('clipboard', 'ic-lg')}</a>
</aside>
<div class="dp-mon">${monitor()}</div>
<footer class="dp-bottom">
 <div class="grip" title="Drag to resize Logs / Terminal"></div>
 <div class="bd-h">
  <span class="lbl2">Console</span>
  <button type="button" class="dtab" data-set=".dp|data-btab|logs" title="Deployment logs" id="tabLogs">Logs</button>
  <button type="button" class="dtab" data-set=".dp|data-btab|term" title="Local terminal session" id="tabTerm">${ic('terminal')}Local<span class="muted" style="margin-left:4px">${ic('x')}</span></button>
  <button type="button" class="btn compact ghost" style="height:24px;min-width:0;padding:0 8px" title="New Local terminal" data-set=".dp|data-btab|term">+</button>
  <button type="button" class="btn compact ghost" style="height:24px;min-width:0;padding:0 6px" title="New terminal session…" data-flip="#tpick|class|tpick|tpick open">${ic('chevron-up', 'ic-sm')}</button>
  <span class="sp"></span>
  <button type="button" class="btn compact ghost" style="height:24px;font-size:11px" title="Clear deployment logs" data-confirm="Clear all deployment log output?" data-confirm-title="Clear logs">Clear</button>
  <button type="button" class="btn compact ghost" style="height:24px;width:28px;min-width:0;padding:0" title="Collapse bottom Console panel" data-flip=".dp|data-bottom|open|min">${ic('minus')}</button>
 </div>
 <div class="tpick" id="tpick">
  <div class="input-icon" style="margin-bottom:8px">${ic('search')}<input class="input sm" placeholder="Search Local or server…"></div>
  ${pickItems.map(([b, t, s, fav], i) => `<div class="it ${i === 0 ? 'on' : ''}">${b ? '<span class="badge accent">Project</span>' : ''}<div><b>${t}</b><small>${s}</small></div><span class="star ${fav ? 'on' : ''}" title="${fav ? 'Remove from favorites' : 'Add to favorites (shows at top of list)'}">${ic('star')}</span></div>`).join('')}
 </div>
 <div class="bd-b">
  <div class="bp-logs" data-ctx="ctxLog">${stream(dpLogInit, dpLogLive, 1300)}</div>
  <div class="bp-term">${stream(termInit, termLive, 1700)}</div>
 </div>
</footer>
</div>
<style>.dp[data-btab="logs"] #tabLogs,.dp[data-btab="term"] #tabTerm{color:var(--accent);border-bottom-color:var(--accent)}.chipb{padding:1px 7px;border-radius:999px;background:var(--surface-raised);border:1px solid var(--border-subtle)}</style>
<script>(function(){var a=document.querySelector('[data-cycle]'),t=document.getElementById('arT'),v=['10s','30s','60s','Off'],c=['var(--error)','var(--warning)','var(--success)','var(--text-muted)'],i=0;if(a)a.addEventListener('click',function(){i=(i+1)%4;t.textContent=v[i];a.style.color=c[i];});})();</script>
${ctxMenus}
<div class="ctx" id="ctxLog"><div class="menu"><div class="mi">${ic('copy')}Copy<span class="k">Ctrl+C</span></div><div class="mi">${ic('list')}Select All<span class="k">Ctrl+A</span></div><div class="msep"></div><div class="mi" data-confirm="Clear all deployment log output?" data-confirm-title="Clear logs">${ic('trash')}Clear</div></div></div>
${SCRIPT}`,
    dialogs: `<div class="scrim" id="dlgWorkers"><div class="modal sm"><div class="modal-h">${ic('users')}<h3>Default upload workers</h3></div>
<div class="modal-b"><div class="stack sm"><div class="sec" style="white-space:pre-line">How many simultaneous workers for Commit + Deploy uploads?

Each worker uses one FTP/SFTP connection and transfers whole files.
Range: 1–8. This is saved globally — deploy will not ask again.</div>
<input class="input" type="number" min="1" max="8" value="3" style="max-width:120px"></div></div>
<div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="success" data-title="Default upload workers" data-msg="Default upload workers set to 3 (Commit + Deploy).">OK</button></div></div></div>
<div class="scrim" id="dlgBranch"><div class="modal sm"><div class="modal-h">${ic('git-branch')}<h3>Create New Branch</h3></div>
<div class="modal-b"><div class="stack sm">${L.field('Branch name', L.inp('e.g. feature/coupons', '', 'text', 'mono'))}${L.field('Based on', L.sel(['master', 'main', 'development']))}${L.chk('Switch to the new branch after creating', true)}</div></div>
<div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="success" data-title="Branch created" data-msg="feature/coupons created from master">Create</button></div></div></div>`,
  });

  // =================================================================================================
  //  DiffViewer control
  // =================================================================================================
  L.page({
    slug: 'diff-viewer', title: 'DiffViewer', nav: 'deploy', group: 'Controls',
    css: BASE_CSS + `.dvg{display:grid;grid-template-columns:repeat(auto-fit,minmax(380px,1fr));gap:16px}.dvg .dv{height:420px}.dvg .dv.sm{height:240px}`,
    body: `<div class="page-head"><div><h1 class="page-title">DiffViewer</h1><div class="page-sub">Read-only AvalonEdit diff with line numbers. Used by Compare, Commit, Rollback and the read-only diff window.</div></div>
<div class="r"><a class="btn" href="diff-window.html">${ic('diff')}<span class="bl">Compare window</span></a><a class="btn" href="read-only-diff-window.html">${ic('eye')}<span class="bl">Read-only window</span></a></div></div>
<div class="dvg">
${dv({ title: 'Diff preview', status: 'Modified · +14 −3 · src/Services/PaymentService.php', body: DIFF_PHP })}
${dv({ title: 'Diff preview', status: 'Modified · +4 −1 · resources/js/checkout.js', body: DIFF_JS })}
${dv({ title: 'Diff preview', status: '', empty: 'Select a file to see the diff', cls: 'sm' })}
${dv({ title: 'Selected file diff', status: 'Deleted · public/img/legacy-banner.png', empty: 'No diff text available for this file.', cls: 'sm' })}
</div>`,
  });

  // =================================================================================================
  //  GitPage
  // =================================================================================================
  L.page({
    slug: 'git', title: 'GitHub Integration', nav: 'git',
    css: BASE_CSS + `.gp-tags{display:flex;flex-direction:column;gap:6px;max-height:340px;overflow:auto;margin-bottom:12px}.gp-tag{display:flex;align-items:center;gap:10px;padding:8px 12px;border:1px solid var(--border-subtle);border-radius:8px;background:var(--surface-raised)}.gp-tag .ic{color:var(--warning)}
.gp-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:20px}@media(max-width:1000px){.gp-grid{grid-template-columns:minmax(0,1fr)}}
.gp-remote{display:flex;flex-wrap:wrap;gap:16px;align-items:center;justify-content:space-between}.gp-remote .u{font-size:14px;font-weight:600;color:var(--info);font-family:var(--font-mono);overflow-wrap:anywhere}`,
    body: `<div class="page-inner"><div class="page-head"><div class="row" style="gap:16px"><span class="avatar xl" style="background:var(--surface-raised);border:1px solid var(--border-strong);color:var(--text-primary)">${ic('git-branch', 'ic-lg')}</span><div><h1 class="page-title">GitHub Integration</h1><div class="page-sub">Repository sync, pull, push and release tags in one panel.</div></div></div>
<div class="r"><button type="button" class="btn icon" title="Open in new window">${ic('external')}</button><a class="btn toolbar" href="clone-repo-window.html" style="color:var(--text-primary);border-color:var(--border-subtle)">${ic('link', 'inf')}<span class="bl">Clone / Connect</span></a></div></div>
<div class="card" style="margin-bottom:20px"><div class="gp-remote"><div style="min-width:0"><div class="muted" style="margin-bottom:5px">Remote Repository (origin):</div><div class="u">https://github.com/poromix/myproject.git</div>
<div class="sec" style="margin-top:10px">Current Branch: <span class="chip active mono">${ic('git-branch')}master</span></div></div>
<div class="row"><button type="button" class="btn ghost" data-working="⏳ Pulling..." data-done="Success" data-done-msg="Successfully pulled changes from remote!">${ic('arrow-down')}<span class="bl">Pull</span></button><button type="button" class="btn primary" data-working="⏳ Pushing..." data-done="Success" data-done-msg="Successfully pushed changes to remote!">${ic('arrow-up')}<span class="bl">Push</span></button></div></div></div>
${L.worker({ icon: 'cloud', name: 'git push origin master', desc: 'Enumerating objects: 14, done. Writing objects: 100% (9/9), 3.2 KiB', pct: 64, speed: 1.4, stats: ['<b>3 commit(s)</b>', 'origin/master'] })}
<div class="gp-grid" style="margin-top:20px">
${L.card('Tags', `<div class="gp-tags" data-selectable>${['v2.4.0', 'v2.3.2', 'v2.3.1', 'v2.3.0', 'v2.2.0', 'v2.1.0', 'v2.0.0'].map((t) => `<div class="gp-tag">${ic('tag')}<span class="mono">${t}</span></div>`).join('')}</div>
<div class="empty" style="padding:10px;display:none"><div>No tags found</div></div>
<button type="button" class="btn toolbar" style="border-color:var(--border-subtle)" data-working="⏳ Pushing Tags..." data-done="Success" data-done-msg="Successfully pushed all tags to remote!">${ic('arrow-up')}<span class="bl">Push All Tags</span></button>`, { icon: 'tag' })}
${L.card('Create New Tag', `<div class="stack">${L.field('Tag Name (e.g. v1.0.0):', L.inp('v2.5.0', '', 'text', 'mono'))}${L.field('Description (Message):', '<textarea class="input" rows="4" placeholder="Release notes…"></textarea>')}
<div class="banner warn" style="display:none">${ic('alert')}<span>Please enter a Tag Name.</span></div>
<button type="button" class="btn success" data-working="⏳ Creating..." data-done="Success" data-done-msg="Tag 'v2.5.0' created and pushed successfully!">${ic('plus')}<span class="bl">Create &amp; Push Tag</span></button></div>`, { icon: 'plus' })}
</div></div>`
  });

  // =================================================================================================
  //  WINDOWS
  // =================================================================================================
  // ---- DiffWindow
  L.window({
    slug: 'diff-window', title: 'Compare Changes', icon: 'search', w: 1200, h: 780, css: BASE_CSS,
    body: `<div class="fill"><div class="wh">${ic('search', 'ic-xl')}<h2>Review Changes</h2></div>
<div class="card dense" style="flex:1;min-height:0;display:flex;flex-direction:column;gap:12px">
 <div class="row"><label class="check"><input type="checkbox" checked>Select All</label><b style="font-size:14px">8 Files</b></div>
 <div class="split"><div class="fl" data-selectable>${CHANGES.map(([p, s], i) => frow(p, s, { sel: i === 0, dbl: 'read-only-diff-window.html', extra: '' })).join('')}</div>
 ${dv({ title: 'Diff preview', status: 'Modified · src/Services/PaymentService.php', body: DIFF_PHP, empty: 'Pick a file to preview differences' })}</div></div></div>`,
    footer: `<span class="l">Double-click a file to open the diff in its own window.</span><button class="btn ghost">Cancel</button><button class="btn primary" data-confirm="Deploy 8 selected file(s)?" data-confirm-title="Deploy selected">${ic('rocket')}<span class="bl">DEPLOY SELECTED</span></button>`,
  });

  // ---- ReadOnlyDiffWindow
  L.window({
    slug: 'read-only-diff-window', title: 'Diff Preview', icon: 'diff', w: 900, h: 560, css: BASE_CSS,
    body: `<div class="fill" style="gap:10px"><div><div style="font-size:16px;font-weight:700" class="mono">src/Services/PaymentService.php</div><div class="muted" style="font-size:12px">Modified · +14 −3 · 2 hunks</div></div>
${dv({ title: 'Selected file diff', status: '', body: DIFF_PHP, empty: 'No diff text available for this file.' }).replace('class="dv ', 'style="flex:1" class="dv ')}</div>`,
    footer: `<button class="btn primary" data-toast="success" data-title="Copied" data-msg="Diff copied to clipboard">${ic('copy')}<span class="bl">Copy Diff</span></button><button class="btn ghost">Close</button>`,
  });

  // ---- CommitWindow
  L.window({
    slug: 'commit-window', title: 'Review &amp; Commit', icon: 'git-commit', w: 1150, h: 760, css: BASE_CSS + `.cm-msg textarea{min-height:64px}.frow .btn.icon{border:0;background:transparent}`,
    body: `<div class="fill"><div class="wh">${ic('edit', 'ic-xl')}<h2>Uncommitted Changes</h2></div>
<div class="card dense" style="flex:1;min-height:0;display:flex;flex-direction:column;gap:12px">
 <div class="row"><span class="sec" style="font-size:14px">Pending Changes:</span><b style="font-size:14px">8 Files</b></div>
 <div class="split"><div class="fl" data-selectable data-ctx="ctxCommit">${CHANGES.map(([p, s], i) => frow(p, s, { sel: i === 0, chk: false, status: 'first', extra: `<button type="button" class="btn icon compact" title="Show in Explorer">${ic('folder-open')}</button><button type="button" class="btn icon compact" title="Delete File" data-confirm="Are you sure you want to delete '${p}' permanently?" data-confirm-title="Confirm Delete" style="color:var(--error)">${ic('trash')}</button>` })).join('')}</div>
 ${dv({ title: 'Diff preview', status: 'Modified · src/Services/PaymentService.php', body: DIFF_PHP, empty: 'Select a file to review its diff' })}</div></div>
<div class="field cm-msg"><label>Commit Message:</label><textarea class="input" rows="3">feat(payment): apply coupon discount before gateway charge

Adds config/payment.php and the coupons migration.</textarea></div></div>`,
    footer: `<span class="l">Right-click a file: Add to .gitignore · Sync (No Deploy)</span><button class="btn ghost">Cancel</button><button class="btn primary" data-working="Deploying…" data-done="Deploy → Commit + Push" data-done-msg="8 file(s) uploaded, committed and pushed.">${ic('zap')}<span class="bl">DEPLOY → COMMIT + PUSH</span></button>`,
    dialogs: `<div class="ctx" id="ctxCommit"><div class="menu"><div class="mi" data-confirm="Add 'config/payment.php' to .gitignore?" data-confirm-title="Git ignore">${ic('eye')}Add to .gitignore</div><div class="mi" data-confirm="Mark 'src/Services/PaymentService.php' as synced without deploying it to FTP?" data-confirm-title="Sync (No Deploy)">${ic('swap')}Sync (No Deploy)</div></div></div>`,
  });

  // ---- InitGitWindow
  L.window({
    slug: 'init-git-window', title: 'Initialize Git Repository', icon: 'git-branch', w: 600, h: 450, css: BASE_CSS + `.brgrid{display:flex;flex-wrap:wrap;gap:10px 28px}`,
    body: `<div class="stack lg"><div class="wh">${ic('git-branch', 'ic-xl')}<div><h2>Initialize Git Repository</h2><small>Set up version control for this project.</small></div></div>
<div><div class="sec" style="font-size:14px;font-weight:600;margin-bottom:10px">Select Branches to Create:</div><div class="card"><div class="brgrid">${L.chk('<span class="mono">main</span>', true)}${L.chk('<span class="mono">master</span>')}${L.chk('<span class="mono">beta</span>')}${L.chk('<span class="mono">development</span>')}</div></div></div>
<div><div class="sec" style="font-size:14px;font-weight:600;margin-bottom:10px">Remote Repository URL (Optional):</div>${L.inp('', '', 'text', 'mono')}<div class="hint" style="margin-top:5px;font-size:12px">e.g. https://github.com/username/repo.git</div></div></div>`,
    footer: `<button class="btn ghost">Cancel</button><button class="btn success" data-working="Initializing…" data-done="Git repository initialized" data-done-msg="Created branches: main">${ic('rocket')}<span class="bl">Initialize &amp; Create</span></button>`,
  });

  // ---- RollbackPreviewWindow
  const RB_FILES = [['src/Services/PaymentService.php', 'M', 'was changed', 'edit'], ['config/payment.php', 'A', 'was added', 'file-plus'], ['public/img/legacy-banner.png', 'D', 'was deleted', 'trash'], ['resources/js/checkout.js', 'M', 'was changed', 'edit']];
  L.window({
    slug: 'rollback-preview-window', title: 'Rollback Preview', icon: 'undo', w: 1100, h: 720, css: BASE_CSS + `.cmeta{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:12px;align-items:center}.cmeta .hash{font-family:var(--font-mono);padding:3px 9px;border-radius:6px;background:var(--accent-soft);color:var(--accent);font-weight:700}
.rbst{margin-left:auto;font-size:11px;color:var(--text-muted);white-space:nowrap}.frow .fn.ellipsis{flex:0 1 auto}.rbrow .fn{flex:1 1 0}.frow .rbi{width:16px;height:16px}`,
    body: `<div class="fill" style="gap:12px"><div class="wh">${ic('undo', 'ic-xl')}<div><h2>Rollback commit</h2><small>Browse older commits, preview file diffs, rollback one file or the whole commit.</small></div></div>
<div class="card dense"><div class="cmeta"><div class="row"><button class="btn compact" title="Go to a newer commit" disabled>${ic('arrow-left')}<span class="bl">Newer</span></button><button class="btn compact" title="Go further back in history">Older<span class="bl"></span>${ic('arrow-right')}</button></div>
<div style="min-width:0"><div class="row"><span class="hash">a3f9c21</span><b class="ellipsis">feat(payment): apply coupon discount before gateway charge</b></div><div class="muted" style="font-size:12px;margin-top:3px">Sharif · 2026-11-02 00:39</div></div><span class="badge">1 / 48</span></div></div>
<div class="split" style="grid-template-columns:minmax(260px,4fr) minmax(0,8fr)"><div class="card dense" style="display:flex;flex-direction:column;min-height:0"><div class="row between" style="margin-bottom:8px"><b>Files in commit</b><span class="muted mono" style="font-size:12px">4 file(s)</span></div>
<div class="fl" data-selectable>${RB_FILES.map(([p, s, t, i], k) => `<div class="list-item frow rbrow ${k === 0 ? 'sel' : ''}">${ic(i, 'rbi ' + (s === 'A' ? 'ok' : s === 'D' ? 'err' : 'warn'))}<span class="fn ellipsis" title="${p}">${pathHtml(p)}</span><span class="rbst">${t}</span></div>`).join('')}</div></div>
${dv({ title: 'Diff preview', status: 'Modified · src/Services/PaymentService.php', body: DIFF_PHP, empty: 'Select a file to preview the code diff' })}</div>
<label class="check"><input type="checkbox" checked>Also redeploy affected files to FTP after rollback</label></div>`,
    footer: `<button class="btn warn" style="color:var(--warning);border-color:rgba(230,184,77,.5);margin-right:auto" title="Create a new commit that undoes only the selected file" data-confirm="Create a new commit that undoes only src/Services/PaymentService.php?" data-confirm-title="Rollback this file">${ic('undo')}<span class="bl">Rollback this file</span></button><button class="btn ghost">Cancel</button><button class="btn danger" title="git revert the selected commit, then push" data-confirm="git revert a3f9c21, push, and redeploy 4 affected file(s) to FTP?" data-confirm-title="Rollback whole commit">${ic('undo')}<span class="bl">Rollback whole commit</span></button>`,
    dialogs: `<div class="scrim" id="rbRun"><div class="modal"><div class="modal-h">${ic('undo')}<h3>Rolling back</h3></div><div class="modal-b stack">${L.worker({ icon: 'undo', name: 'git revert a3f9c21', desc: 'Rolling back commit a3f9c21', pct: 20, speed: 3 })}${L.worker({ icon: 'upload', name: 'Redeploy to FTP', desc: 'Redeploying 4 rolled-back file(s)…', pct: 0, state: 'queued' })}</div><div class="modal-f"><button class="btn" data-close>Hide</button></div></div></div>`,
  });

  // ---- SyncPreviewWindow
  const SYNC_ITEMS = [['Folder', '/public_html/app/Services', true], ['File', '/public_html/config/payment.php', true], ['File', '/public_html/resources/js/checkout.js', true], ['Folder', '/public_html/resources/views/cart', true], ['File', '/public_html/public/css/app.css', false], ['File', '/public_html/composer.json', false]];
  L.window({
    slug: 'sync-preview-window', title: 'Sync preview', icon: 'swap', w: 720, h: 720, css: BASE_CSS + `.sp-list .list-item{padding:7px 12px}`,
    body: `<div class="fill" style="gap:12px"><div><div class="sec" style="font-size:14px;font-weight:600">6 path(s) in manifest — 4 checked for download</div><div class="mono muted" style="font-size:12px;margin-top:3px">/public_html/.gitdeploy.sync.json</div></div>
<div class="card flush" style="flex:1;min-height:0;overflow:auto"><div class="list sp-list" data-selectable>${SYNC_ITEMS.map(([k, r, c]) => `<div class="list-item"><label class="check"><input type="checkbox" ${c ? 'checked' : ''}></label><span class="badge ${k === 'Folder' ? 'warning' : 'info'}" style="width:54px;justify-content:center">${k}</span><span class="mono ellipsis grow" style="font-size:12px" title="${r}">${r}</span></div>`).join('')}</div></div>
<div class="row"><button class="btn compact">Select all in remote tree</button><button class="btn compact">Clear all checkboxes</button></div>
<div class="banner warn" style="display:none">${ic('alert')}<span>No paths selected for download.</span></div>
${L.worker({ icon: 'download', name: 'Downloading checked paths', desc: '/public_html/app/Services · 3 of 4 paths', pct: 55, speed: 1.2, stats: ['<b>1.1 MB/s</b>'] })}</div>`,
    footer: `<button class="btn">Cancel</button><button class="btn primary" data-working="Downloading…" data-done="Sync finished" data-done-msg="4 path(s) downloaded to the local project.">${ic('download')}<span class="bl">Download</span></button>`,
  });

  // ---- SyncManifestEditorWindow
  const mn = (name, o = {}) => {
    const { dir = false, open = false, state = 'off', kids = '' } = o;
    const k = fx(name);
    return `<div class="tn ${dir ? 'dir' : ''} ${open ? 'open' : ''} ${tmap(name)}"><span class="exp">${dir ? ic('chevron-right') : ''}</span><label class="check"><input type="checkbox" ${state === 'on' ? 'checked' : ''} ${state === 'mix' ? 'data-ind' : ''}></label>${ic(dir ? (open ? 'folder-open' : 'folder') : k === 'img' ? 'image' : k === 'other' ? 'file-text' : 'file-code', 'fi')}<span class="nm">${name}</span></div>${dir ? `<div class="tchildren">${kids}</div>` : ''}`;
  };
  L.window({
    slug: 'sync-manifest-editor-window', title: 'Edit sync manifest', icon: 'clipboard', w: 760, h: 620, css: BASE_CSS,
    body: `<div class="fill" style="gap:10px"><div><div class="sec" style="font-size:15px;font-weight:600">Edit sync manifest — Production</div><div class="mono muted" style="font-size:12px;margin-top:3px">/public_html/.gitdeploy.sync.json</div></div>
<div class="row"><button class="btn compact">Select all in remote tree</button><button class="btn compact">Clear all checkboxes</button><span class="grow"></span><span class="badge accent">4 checked</span></div>
<div class="card flush" style="flex:1;min-height:0;overflow:auto;padding:8px"><div class="tree">${mn('public_html', { dir: true, open: true, state: 'mix', kids:
      mn('app', { dir: true, open: true, state: 'on', kids: mn('Services', { dir: true, open: true, state: 'on', kids: mn('PaymentService.php', { state: 'on' }) + mn('InvoiceService.php', { state: 'on' }) }) + mn('Http', { dir: true, state: 'on', kids: '' }) }) +
      mn('config', { dir: true, open: true, state: 'mix', kids: mn('payment.php', { state: 'on' }) + mn('app.php') }) +
      mn('public', { dir: true, open: true, kids: mn('css', { dir: true, kids: mn('app.css') }) + mn('index.php') }) + mn('vendor', { dir: true }) + mn('composer.json') + mn('.env')
  })}</div></div>
<div class="muted" style="font-size:12px">Check files and folders to include in the shared manifest, then save.</div></div>
<script>document.querySelectorAll('[data-ind]').forEach(function(i){i.indeterminate=true;});</script>`,
    footer: `<button class="btn">Cancel</button><button class="btn primary" data-working="Saving…" data-done="Sync manifest saved to FTP." data-done-msg="/public_html/.gitdeploy.sync.json">${ic('upload')}<span class="bl">Save to FTP</span></button>`,
  });

  // ---- FileConflictDialog
  L.window({
    slug: 'file-conflict-dialog', title: 'File Exists', icon: 'alert', w: 620, h: 340, css: BASE_CSS + `.fc{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:8px}.fc .btn{min-width:0;padding:0 6px;font-size:12px}.fc .col{display:flex;flex-direction:column;gap:6px}.fc .col .eyebrow{text-align:center}`,
    body: `<div class="stack"><div class="row" style="font-size:18px;font-weight:700;color:var(--warning)">${ic('alert', 'ic-lg')}Conflict detected</div>
<div class="sec">Another file already exists at the destination. Decide how to proceed.</div>
<div style="padding:10px 12px;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px"><div class="muted" style="font-size:12px;margin-bottom:3px">Conflicting file:</div><div class="mono" style="font-size:12px;overflow-wrap:anywhere">/public_html/app/Services/PaymentService.php</div></div>
<div class="fc"><div class="col"><span class="eyebrow">Overwrite</span><button class="btn danger">Overwrite file</button></div><div class="col"><span class="eyebrow">Skip</span><button class="btn">Skip this file</button></div><div class="col"><span class="eyebrow">Apply to all</span><button class="btn danger">Overwrite all</button></div><div class="col"><span class="eyebrow">Apply to all</span><button class="btn">Skip all</button></div><div class="col"><span class="eyebrow">Abort</span><button class="btn ghost">Cancel</button></div></div></div>`,
    footer: `<span class="l">Closing this window cancels the whole transfer.</span>`,
  });

  // ---- HistoryDetailsWindow
  L.window({
    slug: 'history-details-window', title: 'Deployment Details', icon: 'clipboard', w: 700, h: 500, css: BASE_CSS + `.hd{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:16px}.hd small{display:block;color:var(--text-muted);font-size:13px;margin-bottom:5px}.hd b{font-size:16px}`,
    body: `<div class="fill" style="gap:16px"><div class="wh">${ic('clipboard', 'ic-xl')}<h2>Deployment Details</h2></div>
<div class="card"><div class="hd"><div><small>Status</small><b class="ok row" style="gap:8px">${ic('check-circle')}Success</b></div><div><small>Branch</small><b class="mono">master</b></div><div><small>Date</small><b>2026/11/02 00:39</b></div></div></div>
<div style="flex:1;min-height:0;display:flex;flex-direction:column"><b style="margin-bottom:8px">Changed Files (8)</b><div class="card" style="flex:1;min-height:0;overflow:auto;padding:12px"><div class="stack sm">${CHANGES.map(([p]) => `<div class="row">${fileIc(p)}<span class="fn ellipsis" title="${p}">${pathHtml(p)}</span></div>`).join('')}</div></div></div></div>`,
    footer: `<button class="btn danger" data-confirm="Rollback to this version (a3f9c21)? A new revert commit will be created." data-confirm-title="Rollback">${ic('undo')}<span class="bl">Rollback to this version</span></button><button class="btn">Close</button>`,
  });

  // ---- CloneRepoWindow
  L.window({
    slug: 'clone-repo-window', title: 'Clone Repository', icon: 'link', w: 640, h: 520, css: BASE_CSS,
    body: `<div class="stack"><div class="wh">${ic('link', 'ic-xl')}<div><h2>Clone or Connect Repository</h2><small>Build the remote URL automatically and pick a destination.</small></div></div>
<div class="field"><label>Repository (owner/name or full URL)</label>${L.inp('', 'poromix/myproject', 'text', 'mono')}<div class="hint">Example: rmscms/gitdeploy or https://github.com/rmscms/gitdeploy.git</div></div>
<div class="form-grid" style="grid-template-columns:minmax(0,1fr) minmax(0,160px)">${L.field('Host', L.inp('', 'github.com', 'text', 'mono'))}${L.field('Protocol', L.sel([['HTTPS', true], 'SSH']))}</div>
<div class="field"><label>Remote URL Preview</label><input class="input mono" readonly value="https://github.com/poromix/myproject.git"></div>
<div class="field"><label>Local Target Folder</label><div class="row"><input class="input mono" value="C:\\Users\\poromix\\Documents\\GitDeployProjects\\myproject"><button class="btn compact">Use Default</button><button class="btn compact">Browse</button></div><div class="hint">We will create this folder if needed.</div></div>
<div id="cloneRun" hidden>${L.worker({ icon: 'download', name: 'git clone', desc: "Cloning into 'myproject'… Receiving objects", pct: 8, speed: 3, stats: ['<b>1.8 MiB/s</b>'] })}</div></div>`,
    footer: `<button class="btn ghost">Cancel</button><button class="btn primary" data-working="Cloning…" data-done="Success" data-done-msg="Repository cloned successfully!" onclick="document.getElementById('cloneRun').hidden=false">${ic('rocket')}<span class="bl">Clone Now</span></button>`,
  });
};
