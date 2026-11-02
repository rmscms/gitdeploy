// 30-upload-transfers: Direct Upload page + editor / transfer monitor / remote dialogs
module.exports = (L) => {
  const ic = L.ic;
  const esc = L.esc;

  // ---------------------------------------------------------------- helpers
  // tiny PHP highlighter -> html using core tok-* classes
  const hl = (src) => {
    const re = /(<\?php|\/\/.*|\/\*[\s\S]*?\*\/)|('(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*")|(\$[A-Za-z_]\w*)|\b(namespace|use|class|function|public|private|protected|return|new|if|else|foreach|as|static|extends|implements|try|catch|throw|null|true|false|const|continue)\b|\b(\d+)\b|([A-Za-z_]\w*)(?=\()/g;
    let out = '', last = 0, m;
    while ((m = re.exec(src))) {
      out += esc(src.slice(last, m.index));
      const t = esc(m[0]);
      if (m[1]) out += m[1][0] === '<' ? `<span class="tok-k">${t}</span>` : `<span class="tok-c">${t}</span>`;
      else if (m[2]) out += `<span class="tok-s">${t}</span>`;
      else if (m[3]) out += `<span class="tok-v">${t}</span>`;
      else if (m[4]) out += `<span class="tok-k">${t}</span>`;
      else if (m[5]) out += `<span class="tok-n">${t}</span>`;
      else out += `<span class="tok-f">${t}</span>`;
      last = m.index + m[0].length;
    }
    return out + esc(src.slice(last));
  };
  const PHP = `<?php

namespace App\\Http\\Controllers;

use App\\Services\\FtpService;
use Illuminate\\Http\\Request;

class DeployController extends Controller
{
    public function __construct(private FtpService $ftp) {}

    // Upload the selected files to the active FTP profile
    public function upload(Request $request)
    {
        $files = $request->input('files', []);
        $overwrite = $request->boolean('overwrite', true);

        foreach ($files as $path) {
            $remote = '/public_html/' . ltrim($path, '/');
            if (!$overwrite && $this->ftp->exists($remote)) {
                continue; // already uploaded in previous session
            }
            $this->ftp->put(base_path($path), $remote, 0644);
        }

        return response()->json(['uploaded' => count($files)]);
    }
}
`;
  const codeBlock = (src, editable = false) => {
    const n = src.replace(/\n$/, '').split('\n').length;
    return `<div class="code"><div class="gut">${Array.from({ length: n }, (_, i) => i + 1).join('<br>')}</div><pre ${editable ? 'contenteditable="true" spellcheck="false"' : ''}>${hl(src)}</pre></div>`;
  };

  const modal = (id, title, icon, inner, foot, cls = '') => `<div class="scrim" id="${id}"><div class="modal ${cls}"><div class="modal-h">${ic(icon)}<h3>${title}</h3></div><div class="modal-b">${inner}</div><div class="modal-f">${foot}</div></div></div>`;
  const okCancel = (ok = 'OK', okAttrs = '', cancel = 'Cancel') => `<button class="btn" data-close style="min-width:90px">${cancel}</button><button class="btn primary" data-close style="min-width:90px" ${okAttrs}>${ok}</button>`;

  // ---- dialog bodies (shared between Windows/*.xaml pages and in-page modals) ----
  const inputBody = (prompt, value, extra = '') => `<div class="stack"><div style="font-weight:600;font-size:13px;white-space:pre-line">${prompt}</div><input class="input" type="text" value="${esc(value)}" spellcheck="false">${extra}</div>`;
  const newFileBody = () => `<div class="stack"><div class="field"><label>File name</label><input class="input" id="nfName" type="text" placeholder="" spellcheck="false"></div>
<div class="field"><label>Extension (e.g. php, txt, json)</label><input class="input" id="nfExt" type="text" value="txt" spellcheck="false"></div>
<div class="hint">Will create <code class="sec" id="nfPreview">.txt</code> in <code class="sec">app/Http/Controllers</code></div></div>
<script>(function(){var n=document.getElementById('nfName'),e=document.getElementById('nfExt'),p=document.getElementById('nfPreview');if(!n)return;function u(){p.textContent=(n.value||'')+(e.value?'.'+e.value.replace(/^\\./,''):'')}n.addEventListener('input',u);e.addEventListener('input',u);u();})();</script>`;
  const propsRows = (rows) => `<div class="card dense"><dl class="kv" style="grid-template-columns:110px minmax(0,1fr);gap:6px 10px">${rows.map(([k, v, mono]) => `<dt>${k}</dt><dd class="${mono ? 'mono' : ''}" style="${mono ? 'font-size:12px' : ''}">${v}</dd>`).join('')}</dl></div>`;
  const propsBody = (kind) => {
    const folder = kind === 'folder';
    return `<div class="row" style="gap:12px;margin-bottom:14px"><span class="avatar xl" style="background:var(--surface-card);color:${folder ? 'var(--warning)' : '#8892bf'};border:1px solid var(--border-subtle)">${ic(folder ? 'folder' : 'file-code', 'ic-lg')}</span><div><div style="font-size:15px;font-weight:600">${folder ? 'Controllers' : 'DeployController.php'}</div><div class="muted" style="font-size:12px">${folder ? 'Folder' : 'File'}</div></div></div>
${propsRows(folder
    ? [['Location', 'app/Http', 1], ['Path', 'C:\\laragon\\www\\subtell\\app\\Http\\Controllers', 1], ['Size', '212.4 KB (217,498 bytes)'], ['Contains', '14 files, 2 folders'], ['Created', '2025-03-18 10:41:07'], ['Modified', '2026-11-02 00:39:12'], ['Attributes', 'Directory']]
    : [['Location', 'app/Http/Controllers', 1], ['Path', 'C:\\laragon\\www\\subtell\\app\\Http\\Controllers\\DeployController.php', 1], ['Size', '14.2 KB (14,538 bytes)'], ['Created', '2025-03-18 10:41:07'], ['Modified', '2026-11-02 00:36:51'], ['Extension', '.php'], ['Attributes', 'Archive']])}`;
  };
  const mappingBody = () => `<div style="margin-bottom:12px"><div style="font-size:15px;font-weight:600">Production · cPanel (SFTP)</div><div class="mono muted" style="font-size:12px;margin-top:4px">FTP folder: /public_html/assets</div></div>
<div class="card dense" style="padding:8px"><div class="pm-row pm-h"><span>Local</span><span>Remote</span><span></span></div>
<div id="pmList" style="max-height:240px;overflow:auto">
${[['/', '/public_html'], ['/public', '/public_html/assets'], ['/storage/app/public', '/public_html/uploads'], ['/resources/lang', '/public_html/lang']].map(([a, b]) => `<div class="pm-row"><span class="mono ellipsis" title="${a}">${a}</span><span class="mono sec ellipsis" title="${b}">${b}</span><span class="row" style="justify-content:flex-end;gap:4px"><button class="btn icon compact ghost pm-edit" title="Edit mapping" data-l="${a}" data-r="${b}">${ic('edit')}</button><button class="btn icon compact ghost" style="color:var(--error)" title="Delete mapping" data-confirm="Delete mapping ${a} -> ${b}?">${ic('trash')}</button></span></div>`).join('')}
</div>
<div class="muted" id="pmEmpty" hidden style="text-align:center;padding:24px 16px;font-size:12px">No mappings yet. Save below to add the first one for this folder.</div></div>
<div class="card raised dense" style="margin-top:14px"><div id="pmTitle" style="font-size:12px;font-weight:600;margin-bottom:8px">New mapping</div>
<div class="grid cols-2" style="gap:12px"><div class="field"><label>Local (project folder)</label><input class="input" id="pmLocal" placeholder="/" value="/"></div><div class="field"><label>Remote (FTP folder)</label><input class="input mono" id="pmRemote" placeholder="/public_html" value="/public_html/assets"></div></div>
<div class="row end" style="margin-top:12px"><button class="btn primary" style="min-width:130px" data-toast="success" data-title="Mapping saved" data-msg="/public -> /public_html/assets">Save mapping</button><button class="btn ghost" id="pmCancel" style="min-width:90px">Cancel</button></div></div>
<script>(function(){var t=document.getElementById('pmTitle');if(!t)return;document.addEventListener('click',function(e){var b=e.target.closest('.pm-edit');if(b){t.textContent='Edit mapping';document.getElementById('pmLocal').value=b.dataset.l;document.getElementById('pmRemote').value=b.dataset.r;}if(e.target.closest('#pmCancel')){t.textContent='New mapping';document.getElementById('pmLocal').value='/';document.getElementById('pmRemote').value='/public_html/assets';}});})();</script>`;

  // ---- editor (LocalFileEditorControl) ----
  const editorBody = (closeAttr, statusInit = 'Monaco editor ready.') => `<div class="ed"><div class="ed-head">
<button class="btn icon compact ghost" style="color:var(--error)" title="Close editor" ${closeAttr}>${ic('x')}</button>
<span class="grow mono ellipsis sec" style="font-size:12px">C:\\laragon\\www\\subtell\\app\\Http\\Controllers\\DeployController.php</span>
<button class="btn icon compact ghost ed-revert" disabled title="Revert unsaved editor changes">${ic('undo')}</button>
<a class="btn icon compact ghost" href="editor-host-window.html" title="Float — open this file in a separate window">${ic('external')}</a>
<button class="btn icon compact ghost ed-save" disabled title="Save and upload the current file">${ic('upload')}</button></div>
<div class="ed-status muted" id="edStatus">${statusInit}</div>${codeBlock(PHP, true)}</div>
<script>(function(){var r=document.querySelector('.ed');if(!r)return;var pre=r.querySelector('pre'),orig=pre.innerHTML,rv=r.querySelector('.ed-revert'),sv=r.querySelector('.ed-save'),st=document.getElementById('edStatus');
function t(){var d=new Date();return ('0'+d.getHours()).slice(-2)+':'+('0'+d.getMinutes()).slice(-2)+':'+('0'+d.getSeconds()).slice(-2)}
pre.addEventListener('input',function(){rv.disabled=false;sv.disabled=false;st.textContent='Modified - unsaved changes';st.className='ed-status warn'});
rv.addEventListener('click',function(){pre.innerHTML=orig;rv.disabled=true;sv.disabled=true;st.textContent='Reverted.';st.className='ed-status muted'});
sv.addEventListener('click',function(){orig=pre.innerHTML;rv.disabled=true;sv.disabled=true;st.textContent='Saved '+t()+' - uploading to /public_html/app/Http/Controllers/DeployController.php';st.className='ed-status ok';if(window.toast)toast({type:'success',title:'Uploaded',msg:'DeployController.php (14.2 KB)'});});})();</script>`;

  // ---- transfer monitor ----
  const tmLog = [['ok', '[W2] ✓ app/Http/Controllers/AuthController.php (4.2 KB)'], ['i', '[W1] → public/js/app.js (212.8 KB)'], ['ok', '[W4] ✓ resources/views/deploy.blade.php (3.1 KB)'], ['w', '[W3] retry 1/3 routes/web.php (timeout)'], ['i', '[W5] connecting…'], ['ok', '[W3] ✓ routes/web.php (1.8 KB)'], ['e', '[W6] ✗ storage/app/cache.lock: 550 Permission denied'], ['i', '[W2] → public/css/app.css (96.4 KB)']];
  const tmInit = `<div><span class="m">00:21:52</span> <span class="u">[W1] ✓ composer.json (2.1 KB)</span></div><div><span class="m">00:21:53</span> <span class="u">[W2] ✓ artisan (1.7 KB)</span></div><div><span class="m">00:21:53</span> <span class="i">[W3] → app/Http/Controllers/DeployController.php (14.2 KB)</span></div>`;
  const slotsData = [['W1', 'Busy', 'public/js/app.js', 40, '1.9 MB/s'], ['W2', 'Busy', 'public/css/app.css', 70, '1.2 MB/s'], ['W3', 'Connecting', 'routes/web.php', 5, '—'], ['W4', 'Done', 'resources/views/deploy.blade.php', 100, '—'], ['W5', 'Waiting', '', 0, '—'], ['W6', 'Error', 'storage/app/cache.lock', 100, '—']];
  const slotRows = (big) => slotsData.map(([id, s, f, p, sp]) => {
    const live = s === 'Busy';
    const inner = `<span class="slot-id">${id}</span><span class="slot-st ${s.toLowerCase()}" ${live ? 'data-state' : ''}>${s}</span><span class="ellipsis sec slot-f" title="${f}">${f || '—'}</span>${big ? `<span class="mono muted slot-sp">${sp}</span><div class="progress slot-pg ${s === 'Error' ? 'error' : s === 'Done' ? 'success' : live ? 'busy' : ''}"><i ${live ? `data-progress="${p}" data-speed="${p > 50 ? 1.1 : 0.6}"` : ''} style="width:${p}%"></i></div>` : ''}`;
    return live ? `<div class="slot ${big ? 'big' : ''} running" data-worker>${inner}</div>` : `<div class="slot ${big ? 'big' : ''}">${inner}</div>`;
  }).join('');
  const tmHead = (title, headline, pctHtml, big, prog = 31) => `<div class="tm-top"><b style="font-size:${big ? 15 : 13}px">${title}</b></div>
<div class="tm-head" style="font-size:${big ? 12 : 11}px">${headline}</div>
<div data-worker class="running"><div class="progress" style="height:${big ? 8 : 6}px;margin-top:8px;background:var(--border-subtle)"><i data-progress="${prog}" data-speed="0.5" style="width:${prog}%"></i></div>
<div class="tm-pct" style="font-size:${big ? 12 : 11}px">${pctHtml}</div></div>`;
  const pctRun = '<span class="mono">38.2 MB / 124.6 MB</span> (<b data-pct>31%</b>)';
  const tmPanel = (id) => `<div class="tm" id="${id}">
<div class="row">${`<b style="font-size:13px">Upload · 6 workers</b>`}<span class="grow"></span><a class="btn icon compact ghost" href="transfer-monitor-window.html" title="Open transfer report window">${ic('external')}</a><button class="btn compact ghost" style="height:22px;min-width:48px;padding:0 8px" data-hide="${id}">Hide</button></div>
<div class="tm-head">4/6 workers · 128/412 files · uploading</div>
<div data-worker class="running"><div class="progress" style="height:6px;margin-top:8px;background:var(--border-subtle)"><i data-progress="31" data-speed="0.5" style="width:31%"></i></div><div class="tm-pct">${pctRun}</div></div>
<div class="tm-sec">Workers</div>
<div class="tm-slots">${slotRows(false)}</div>
<div class="terminal tm-log" data-stream data-interval="1500" data-lines='${JSON.stringify(tmLog)}'>${tmInit}<span class="cursor"></span></div>
<div class="muted" style="font-size:10px;margin-top:8px">Hide only closes this card. Transfer keeps running.</div></div>`;

  const queueCards = () => {
    const w = (o) => L.worker(o);
    const retry = (h) => h.replace('<div class="row"></div>', `<div class="row"><button class="btn compact" data-toast="info" data-title="Retry queued" data-msg="storage/app/cache.lock">${ic('refresh')}<span class="bl">Retry</span></button></div>`);
    const dl = (h) => h.replace('<div class="row"></div>', `<div class="row"><span class="badge info">W7</span></div>`);
    return `<div class="stack sm">
${w({ icon: 'upload', name: 'public/js/app.js', desc: '→ /public_html/js/app.js · W1', pct: 42, speed: 0.9, stats: ['<b>1.9 MB/s</b>', 'ETA 00:07', '89.4 / 212.8 KB'] })}
${w({ icon: 'download', name: 'storage/logs/laravel-2026-11-01.log', desc: '← /public_html/storage/logs · W7 (download)', pct: 18, speed: 1.4, stats: ['<b>3.4 MB/s</b>', 'ETA 00:19', '9.2 / 51.6 MB'] })}
${w({ icon: 'upload', name: 'vendor/laravel/framework/src/…/Builder.php', desc: 'Queued · waiting for a free worker slot', state: 'queued', pct: 0, stats: ['position 3 of 281', '128.0 KB'] })}
${w({ icon: 'upload', name: 'database/dump-2026-11-02.sql.gz', desc: 'Paused by user · resumes from 61%', state: 'paused', pct: 61, stats: ['<b>0 B/s</b>', '18.4 / 30.2 MB'] })}
${retry(w({ icon: 'upload', name: 'storage/app/cache.lock', desc: '550 Permission denied · attempt 3/3', state: 'failed', pct: 100, stats: ['failed after 3 retries'] }))}
${w({ icon: 'upload', name: 'resources/views/deploy.blade.php', desc: '✓ uploaded to /public_html/resources/views', state: 'done', pct: 100, stats: ['3.1 KB', '0.2 s'] })}
</div>`;
  };

  // ---------------------------------------------------------------- shared css
  const CSS_TM = `
.tm{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:8px;padding:10px 12px;box-shadow:0 0 16px rgba(0,0,0,.45);width:min(400px,100%);}
.tm-head{margin-top:6px;font-size:11px;color:var(--info)}
.tm-pct{margin-top:4px;font-size:11px;font-weight:600;color:var(--accent)}
.tm-sec{margin:10px 0 4px;font-size:12px;font-weight:600}
.tm-log{margin-top:8px;max-height:140px;font-size:10px;line-height:1.5;padding:8px 10px;background:var(--surface-input)}
.slot{display:grid;grid-template-columns:32px 74px minmax(0,1fr);gap:0 4px;align-items:center;font-size:11px;margin-bottom:4px}
.slot-id{font-weight:600;color:var(--accent)}
.slot-st{color:var(--info)} .slot-st.error{color:var(--error)} .slot-st.done{color:var(--success)} .slot-st.waiting{color:var(--text-muted)}
.slot-st.badge{height:18px;width:max-content;font-size:10px}
.slot.big{grid-template-columns:40px 88px minmax(0,1fr) 78px;font-size:12px;padding:5px 0;border-bottom:1px solid var(--border-subtle);margin:0;row-gap:4px}
.slot.big:last-child{border-bottom:0}
.slot.big .slot-pg{grid-column:2 / -1;height:4px}
.slot.big .slot-sp{text-align:right;font-size:11px}
`;
  const CSS_ED = `
.ed{display:grid;grid-template-rows:auto auto minmax(0,1fr);height:100%;min-height:0;padding:8px;background:var(--surface-card)}
.ed-head{display:flex;align-items:center;gap:6px;margin-bottom:6px}
.ed-status{font-size:11px;margin-bottom:8px}
.ed .code{min-height:0;background:var(--surface-input);font-size:13px;align-content:start}
.ed .code pre{outline:none;min-height:100%}
.ed .btn[disabled]{opacity:.35}
`;

  // ================================================================ DIRECT UPLOAD PAGE
  const R = (name, o = {}) => {
    const { dir = false, open = false, chk = false, part = false, g = '', size = '', map = false, gt = '', up = '', kids = '', sel = false } = o;
    const ext = name.split('.').pop();
    const tmap = { php: 't-php', js: 't-js', css: 't-css', html: 't-html', json: 't-json', md: 't-md', png: 't-img', jpg: 't-img', svg: 't-img', sql: 't-json' };
    const t = dir ? '' : tmap[ext] || '';
    const icon = dir ? (open ? 'folder-open' : 'folder') : t === 't-img' ? 'image' : t ? 'file-code' : 'file-text';
    const gtx = { M: ['m', 'M'], U: ['u', '?'], I: ['i', 'ignored'], C: ['c', '!'] }[gt];
    const upx = { run: ['run', '…'], ok: ['ok', '✓'], skip: ['skip', '↺'] }[up];
    return `<div class="tn du-r ${dir ? 'dir' : ''} ${open ? 'open' : ''} ${t} ${g ? 'g-' + g : ''} ${map ? 'mapped' : ''} ${sel ? 'sel' : ''}" data-ctx="${dir ? 'ctxFolder' : 'ctxFile'}"><span class="exp">${dir ? ic('chevron-right') : ''}</span><label class="check"><input type="checkbox" ${chk ? 'checked' : ''} ${part ? 'data-part' : ''}></label>${ic(icon, 'fi')}<span class="nm">${name}</span>${size ? `<span class="sz">${size}</span>` : ''}${map ? `<span class="mp" data-open="dlgMapping" title="Path mapping: public → /public_html (click to edit)">mapped</span>` : ''}${gtx ? `<span class="gt ${gtx[0]}">${gtx[1]}</span>` : ''}${upx ? `<span class="ub ${upx[0]}">${upx[1]}</span>` : ''}</div>${dir ? `<div class="tchildren">${kids}</div>` : ''}`;
  };
  const ctrl = R('AuthController.php', { g: 'clean', size: '4.2 KB', up: 'skip', chk: 1 }) +
    R('DeployController.php', { g: 'mod', size: '14.2 KB', gt: 'M', up: 'run', chk: 1, sel: 1 }) +
    R('UploadController.php', { g: 'mod', size: '9.8 KB', gt: 'M', up: 'ok', chk: 1 }) +
    R('Controller.php', { g: 'clean', size: '0.4 KB' });
  const http = R('Controllers', { dir: 1, open: 1, g: 'mod', part: 1, kids: ctrl }) + R('Middleware', { dir: 1, g: 'clean' });
  const svc = R('FtpService.php', { g: 'con', size: '11.6 KB', gt: 'C' }) + R('GitService.php', { g: 'clean', size: '7.3 KB' });
  const appK = R('Http', { dir: 1, open: 1, g: 'mod', part: 1, kids: http }) + R('Models', { dir: 1, g: 'clean' }) + R('Services', { dir: 1, open: 1, g: 'con', kids: svc });
  const pubK = R('build', { dir: 1, g: 'unt', gt: 'U' }) +
    R('css', { dir: 1, open: 1, g: 'mod', part: 1, kids: R('app.css', { g: 'mod', size: '96.4 KB', gt: 'M', chk: 1, up: 'ok' }) }) +
    R('js', { dir: 1, open: 1, g: 'mod', part: 1, kids: R('app.js', { g: 'mod', size: '212.8 KB', gt: 'M', chk: 1, up: 'run' }) }) +
    R('images', { dir: 1, g: 'clean' }) + R('favicon.ico', { g: 'clean', size: '15.1 KB' }) + R('index.php', { g: 'clean', size: '1.7 KB' });
  const views = R('deploy.blade.php', { g: 'unt', size: '3.1 KB', gt: 'U', chk: 1 }) + R('welcome.blade.php', { g: 'clean', size: '5.9 KB' });
  const tree =
    R('app', { dir: 1, open: 1, g: 'mod', part: 1, kids: appK }) +
    R('config', { dir: 1, g: 'clean' }) +
    R('database', { dir: 1, g: 'clean' }) +
    R('public', { dir: 1, open: 1, g: 'mod', map: 1, part: 1, kids: pubK }) +
    R('resources', { dir: 1, open: 1, g: 'unt', kids: R('views', { dir: 1, open: 1, g: 'unt', kids: views }) }) +
    R('routes', { dir: 1, open: 1, g: 'mod', kids: R('web.php', { g: 'mod', size: '1.8 KB', gt: 'M', chk: 1 }) }) +
    R('storage', { dir: 1, g: 'ign', gt: 'I' }) + R('vendor', { dir: 1, g: 'ign', gt: 'I' }) +
    R('.env', { g: 'ign', size: '1.1 KB', gt: 'I' }) + R('.gitignore', { g: 'clean', size: '0.2 KB' }) +
    R('artisan', { g: 'clean', size: '1.7 KB' }) + R('composer.json', { g: 'clean', size: '2.1 KB' }) +
    R('package.json', { g: 'mod', size: '1.0 KB', gt: 'M' }) + R('README.md', { g: 'clean', size: '3.9 KB' });

  const results = ['app/Http/Controllers/DeployController.php', 'app/Http/Controllers/UploadController.php', 'app/Services/FtpService.php', 'config/filesystems.php', 'public/js/app.js', 'public/css/app.css', 'resources/views/deploy.blade.php', 'routes/web.php', 'composer.json'].map((p) => {
    const i = p.lastIndexOf('/') + 1;
    return `<div class="tn du-res"><span class="mono" style="font-size:12px"><span class="sec">${p.slice(0, i)}</span><span class="sec">${p.slice(i)}</span></span></div>`;
  }).join('');

  const ctxFolder = `<div class="ctx" id="ctxFolder"><div class="menu">
<a class="mi" data-open="dlgNewFolder">${ic('folder-plus')}New Folder</a><a class="mi" data-open="dlgNewFile">${ic('file-plus')}New File</a>
<a class="mi" data-toast="info" data-title="Explorer" data-msg="Opened C:\\laragon\\www\\subtell\\app">${ic('folder-open')}Open in Explorer</a><a class="mi" data-working="">${ic('refresh')}Refresh<span class="k">F5</span></a><a class="mi">${ic('clipboard')}Paste<span class="k">Ctrl+V</span></a>
<div class="msep"></div>
<a class="mi sub">${ic('git-branch')}Git<span class="k">▸</span><div class="menu subm"><a class="mi" data-open="dlgCommit">${ic('git-commit')}Commit this folder…</a><a class="mi" data-open="dlgCommit">${ic('package')}Commit all changes…</a><a class="mi" data-toast="info" data-title="Pushing…" data-msg="origin/master">${ic('cloud')}Push</a><a class="mi" data-toast="info" data-title="Pulling…" data-msg="origin/master">${ic('download')}Pull</a><div class="msep"></div><a class="mi">${ic('x-circle')}Add to .gitignore</a><a class="mi danger" data-confirm="Discard local changes under app/?">${ic('undo')}Discard local changes</a></div></a>
<div class="msep"></div><a class="mi" data-open="dlgProps">${ic('info')}Properties</a>
<div class="msep"></div><a class="mi danger" data-confirm="Delete 'app' and everything inside it from disk?">${ic('trash')}Delete<span class="k">Del</span></a></div></div>`;
  const ctxFile = `<div class="ctx" id="ctxFile"><div class="menu">
<a class="mi" data-open="dlgNewFolder">${ic('folder-plus')}New Folder</a><a class="mi" data-open="dlgNewFile">${ic('file-plus')}New File</a>
<a class="mi" data-open="dlgEditor">${ic('edit')}Edit</a><a class="mi" data-toast="success" data-title="Uploading" data-msg="DeployController.php → /public_html/app/Http/Controllers">${ic('rocket')}Upload file</a><a class="mi" data-toast="info" data-title="Explorer" data-msg="Selected file in Explorer">${ic('folder-open')}Open in Explorer</a><a class="mi">${ic('clipboard')}Paste<span class="k">Ctrl+V</span></a>
<div class="msep"></div>
<a class="mi sub">${ic('git-branch')}Git<span class="k">▸</span><div class="menu subm"><a class="mi" data-open="dlgCommit">${ic('git-commit')}Commit this file…</a><a class="mi" data-open="dlgCommit">${ic('package')}Commit all changes…</a><a class="mi" data-toast="info" data-title="Pushing…" data-msg="origin/master">${ic('cloud')}Push</a><a class="mi" data-toast="info" data-title="Pulling…" data-msg="origin/master">${ic('download')}Pull</a><div class="msep"></div><a class="mi">${ic('x-circle')}Add to .gitignore</a><a class="mi danger" data-confirm="Discard local changes to DeployController.php?">${ic('undo')}Discard local changes</a></div></a>
<div class="msep"></div><a class="mi" data-open="dlgProps">${ic('info')}Properties</a>
<div class="msep"></div><a class="mi danger" data-confirm="Delete DeployController.php from disk?">${ic('trash')}Delete<span class="k">Del</span></a></div></div>`;
  const ctxMulti = `<div class="ctx" id="ctxMulti"><div class="menu"><a class="mi" data-toast="success" data-title="Uploading 5 files" data-msg="Queued on 6 workers">${ic('rocket')}Upload 5 files</a><div class="msep"></div><a class="mi danger" data-confirm="Delete 7 items from disk?">${ic('trash')}Delete (7 items)</a></div></div>`;

  const duLogInit = `<div class="u">=== Upload Started at 2026-11-02 00:21:48 ===</div><div class="u">Total files to upload: 412</div><div class="u">Folders to ensure: 23</div><div class="u">Workers requested: 6</div><br><div class="u">[00:21:49] Ensure folder: app</div><div class="m">  → Remote Path: /public_html/app</div><div class="u">  ✓ Folder ready</div><br><div class="u">[00:21:50] Skipped (Session): AuthController.php</div><div class="m">  → Remote Path: /public_html/app/Http/Controllers/AuthController.php</div><div class="w">  ↺ Already uploaded in previous session</div><br><div class="u">[00:21:51] Queued: DeployController.php</div><div class="m">  → Remote Path: /public_html/app/Http/Controllers/DeployController.php</div><div class="u">  ✓ UploadController.php</div><br>`;
  const duLogLines = [['ok', '  ✓ app.css'], ['m', '  → Remote Path: /public_html/css/app.css'], ['ok', '[queue] Queued: web.php'], ['ok', '  ✓ deploy.blade.php'], ['w', '  ↺ Already uploaded in previous session'], ['e', '  ✗ cache.lock: 550 Permission denied']];

  const duCss = CSS_TM + CSS_ED + `
.du-head{display:flex;align-items:center;gap:14px;margin-bottom:20px;flex-wrap:wrap}
.du-head .big{width:52px;height:52px;border-radius:14px;display:grid;place-items:center;background:var(--accent-soft);color:var(--accent);flex:none}
.du-conn{color:var(--info);font-size:13px;margin-top:6px;overflow-wrap:anywhere}
.du-conn code{color:var(--text-secondary)}
.du-box{background:var(--surface-card);border:1px solid var(--border-subtle);border-radius:10px;padding:10px;margin-bottom:10px}
.du-opts{display:flex;flex-wrap:wrap;align-items:center;gap:4px 14px;min-height:28px}
.du-opts .check{font-weight:600}.du-opts .check.dim{font-weight:400;color:var(--text-muted)}
.du-sess{color:var(--warning);font-size:11px}
.du-stat{background:var(--surface-input);border:1px solid var(--border-subtle);border-radius:8px;padding:8px 10px;margin-top:6px}
.du-stat .l1{display:flex;align-items:center;gap:8px;min-width:0}
.du-stat .l1 b{font-size:12px;flex:1;min-width:0}
.du-stat .dot{width:8px;height:8px}
.du-detail{margin:4px 0 0 16px;font-size:11px;color:var(--text-secondary);font-family:var(--font-mono)}
.du-stat .progress{height:8px;margin-top:8px;background:var(--surface-input)}
.du-act{display:grid;grid-template-columns:1fr 1.6fr;gap:8px;margin-top:10px}
.du-act .btn{height:38px;font-size:12px;font-weight:700;letter-spacing:.04em;text-transform:uppercase}
.du-onrun{display:none}.is-run .du-onrun{display:inline-flex}.is-run .du-offrun{display:none}
.du-tool{display:flex;flex-wrap:wrap;align-items:center;gap:6px 8px;padding:14px;margin-bottom:10px}
.du-tool .btn{height:30px;min-width:30px;padding:0 10px;font-size:11px;font-weight:600}
.btn.t-info{border-color:var(--info);background:var(--info-surface);color:var(--info)}
.btn.t-ok{border-color:var(--success);background:var(--success-surface);color:var(--success)}
.btn.t-off{border-color:var(--border-strong);background:var(--surface-input);color:var(--text-muted)}
.du-stats{margin-left:6px;font-size:11px;color:var(--text-muted)}.du-stats b{color:var(--text-primary);margin:0 12px 0 5px}.du-stats b.ok{color:var(--success);margin-right:0}
.du-tree-card{padding:0;display:grid;grid-template-rows:auto auto minmax(0,1fr);height:clamp(300px,52vh,720px);overflow:hidden}
.du-legend{padding:8px 10px 4px;font-size:11px;color:var(--text-muted)}
.du-legend i{font-style:normal;font-weight:600}
.du-sbar{margin:0 10px 6px;padding:4px 6px;border-radius:6px;background:var(--surface-input);border:1px solid var(--border-subtle);display:flex;gap:6px;align-items:center}
.du-sbar .input{height:26px;font-size:12px;border:0;background:transparent;box-shadow:none}
.du-scroll{overflow:auto;padding:10px;min-height:0;position:relative}
.du-r{height:26px;padding-right:8px;border-radius:3px}
.du-r .check{min-height:0}
.g-clean .nm{color:var(--success)}.g-mod .nm{color:var(--error)}.g-unt .nm{color:var(--info)}.g-ign .nm{color:var(--text-muted)}.g-con .nm{color:var(--warning)}
.tn.du-r.mapped{background:var(--success-surface)}
.du-r .sz{font-size:10px;color:var(--text-muted);margin-left:8px}
.du-r .mp{font-size:9px;font-weight:600;color:var(--success);background:var(--success-surface);border:1px solid rgba(61,220,132,.25);border-radius:3px;padding:0 4px;margin-left:6px;cursor:pointer}
.du-r .gt{font-size:9px;font-weight:600;margin-left:6px}.gt.m{color:var(--error)}.gt.u{color:var(--info)}.gt.i{color:var(--text-muted)}.gt.c{color:var(--warning)}
.du-r .ub{font-size:11px;font-weight:700;margin-left:6px}.ub.ok{color:var(--success)}.ub.run{color:var(--info);animation:pulse 1s infinite}.ub.skip{color:var(--warning)}
.du-res{height:28px}
.du-empty{display:none;position:absolute;inset:0;place-items:center;color:var(--text-muted);font-size:12px}
.du-log{margin-top:15px;padding:0;min-height:180px;overflow:hidden;display:grid;grid-template-rows:auto minmax(0,1fr)}
.du-log .h{padding:8px 10px;background:var(--surface-shell);font-size:13px;font-weight:600;color:var(--text-secondary);display:flex;gap:8px;align-items:center}
.du-log .terminal{border:0;border-radius:0;background:transparent;color:var(--log-success);height:200px;font-size:11px;line-height:1.5;padding:10px}
.du-float{position:sticky;bottom:0;height:0;z-index:40}
.du-float .tm{position:absolute;right:0;bottom:6px}
.subm{position:absolute;left:100%;top:-6px;display:none}
.mi.sub{position:relative}.mi.sub:hover>.subm{display:block}
.pm-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 72px;gap:8px;align-items:center;min-height:28px;padding:1px 4px;font-size:12px}
.pm-row.pm-h{font-size:11px;font-weight:600;color:var(--text-muted)}
@media(max-height:760px){.du-float .tm-log{display:none}}
@media(max-width:1100px){.du-tree-card{height:clamp(280px,46vh,520px)}}
`;

  const duScript = `<script>(function(){
var card=document.getElementById('duCard'),run=document.getElementById('duRun'),bar=document.getElementById('duBar'),st=document.getElementById('duStatus'),dt=document.getElementById('duDetail'),dot=document.getElementById('duDot'),stop=document.getElementById('duStop'),start=document.getElementById('duStart'),mon=document.getElementById('duMon'),showm=document.getElementById('duShowMon');
var files=['app/Http/Controllers/DeployController.php','public/js/app.js','public/css/app.css','routes/web.php','resources/views/deploy.blade.php','app/Services/GitService.php'],fi=0,tot=412;
function setRun(){run.classList.remove('done');run.classList.add('running');card.classList.add('is-run');bar.dataset.progress=22;bar.style.width='22%';dot.className='dot live blue';stop.disabled=false;start.disabled=true;if(mon)mon.hidden=false;}
function setIdle(msg,cls){run.classList.remove('running');card.classList.remove('is-run');dot.className='dot '+(cls||'');st.textContent=msg;dt.textContent='';stop.disabled=true;start.disabled=false;}
start.addEventListener('click',setRun);
stop.addEventListener('click',function(){bar.dataset.progress=bar.dataset.progress;setIdle('Upload Stopped by User','warn');dt.textContent=Math.floor(parseFloat(bar.dataset.progress)/100*tot)+' of '+tot+' files uploaded before stop';if(window.toast)toast({type:'warning',title:'Upload stopped',msg:'Remaining files stay unchecked-as-pending'});});
document.getElementById('duStopC').addEventListener('click',function(){stop.click();});
document.addEventListener('click',function(e){var h=e.target.closest('[data-hide]');if(h){document.getElementById(h.dataset.hide).parentNode.hidden=true;if(showm)showm.hidden=false;}if(e.target.closest('#duShowMon')){mon.hidden=false;showm.hidden=true;}});
setInterval(function(){var p=parseFloat(bar.dataset.progress)||0;if(run.classList.contains('running')){st.textContent='4/6 workers · '+Math.floor(p/100*tot)+'/'+tot+' files · uploading';dt.textContent=files[Math.floor(p/6)%files.length]+' · '+(1.2+Math.random()*1.4).toFixed(1)+' MB/s';}
else if(run.classList.contains('done')&&!run.dataset.fin){run.dataset.fin=1;setIdle('Upload Completed!','ok');dt.textContent='412 files · 124.6 MB · 6 workers';if(window.toast)toast({type:'success',title:'Upload Completed!',msg:'412 files uploaded to /public_html'});}
if(run.classList.contains('running'))run.dataset.fin='';},700);
// tree: checkbox tri-state + counters
var tree=document.getElementById('duTree');
function files_(){return [].slice.call(tree.querySelectorAll('.tn:not(.dir)'));}
function count(){var f=files_(),n=0;f.forEach(function(r){if(r.querySelector('input').checked)n++;});document.getElementById('duTotal').textContent=f.length;document.getElementById('duSel').textContent=n;var all=n===f.length;var l=document.getElementById('duSelLbl'),b=document.getElementById('duSelBtn');l.textContent=n>0?'Clear':'Select';b.title=n>0?'Deselect All':'Select All';}
tree.querySelectorAll('input[data-part]').forEach(function(i){i.indeterminate=true;});
tree.addEventListener('click',function(e){if(e.target.closest('.check'))e.stopPropagation();});
tree.addEventListener('change',function(e){var inp=e.target,row=inp.closest('.tn'),nx=row.nextElementSibling;inp.indeterminate=false;
if(nx&&nx.classList.contains('tchildren'))nx.querySelectorAll('input').forEach(function(c){c.checked=inp.checked;c.indeterminate=false;});
var p=row.parentElement;while(p&&p!==tree){if(p.classList.contains('tchildren')){var pr=p.previousElementSibling,all=p.querySelectorAll('input'),n=0;all.forEach(function(c){if(c.checked)n++;});var pc=pr.querySelector('input');pc.checked=n===all.length;pc.indeterminate=n>0&&n<all.length;}p=p.parentElement;}count();});
document.getElementById('duSelBtn').addEventListener('click',function(){var any=parseInt(document.getElementById('duSel').textContent,10)>0;tree.querySelectorAll('input').forEach(function(c){c.checked=!any;c.indeterminate=false;});count();});
document.getElementById('duAuto').addEventListener('click',function(){var on=this.classList.toggle('t-ok');this.classList.toggle('t-off',!on);this.textContent=on?'⏱ On':'⏱ Off';});
count();
// search (type-to-search)
var bx=document.getElementById('duSbar'),inp=document.getElementById('duSearch'),res=document.getElementById('duResults'),emp=document.getElementById('duEmpty');
function sync(){var q=inp.value.trim();res.hidden=!q;tree.hidden=!!q;var vis=0;[].slice.call(res.children).forEach(function(c){var ok=!q||c.textContent.toLowerCase().indexOf(q.toLowerCase())>-1;c.hidden=!ok;if(ok)vis++;});emp.style.display=q&&!vis?'grid':'none';}
inp.addEventListener('input',sync);
document.addEventListener('keydown',function(e){if(document.querySelector('.scrim.open'))return;if((e.ctrlKey&&e.key==='f')||(e.key==='/'&&document.activeElement.tagName!=='INPUT')){e.preventDefault();bx.hidden=false;inp.focus();}if(e.key==='Escape'&&document.activeElement===inp){inp.value='';sync();bx.hidden=true;}});
document.getElementById('duSClear').addEventListener('click',function(){inp.value='';sync();bx.hidden=true;});
})();</script>`;

  const duBody = `<div class="page-inner" id="duCard" style="position:relative">
<div class="du-head"><span class="big">${ic('upload', 'ic-xl')}</span><div class="grow"><h1 class="page-title">Direct Upload</h1><div class="page-sub">Select files and folders to upload directly via FTP</div>
<div class="du-conn">Active Connection: <b>Production · cPanel</b> · SFTP · <code>ftp.mysite.com:22</code> · Local 'public' → Remote '/public_html'</div></div>
<a class="btn" href="direct-upload.html" title="Pop-out">${ic('square')}<span class="bl">Pop-out</span></a></div>

<div class="du-box"><div class="du-opts">
<label class="check" title="Keeps a log of uploaded files to resume if connection fails. Overwrite still re-uploads those files."><input type="checkbox" checked>Crash Recovery</label>
<label class="check dim" title="Always re-upload selected files. Crash Recovery will not skip them."><input type="checkbox" checked>Overwrite</label>
<span class="du-sess">128 files in session cache</span>
<button class="btn compact ghost" style="color:var(--error);height:26px;font-size:11px" title="Delete the saved session file manually" data-confirm="Delete the saved upload session?">${ic('trash', 'ic-sm')}<span class="bl">Clear</span></button></div>
<div class="du-stat running" id="duRun" data-worker><div class="l1"><span class="dot live blue" id="duDot"></span><b id="duStatus">4/6 workers · 128/412 files · uploading</b>
<button class="btn compact danger du-onrun" id="duStopC" style="height:24px;min-width:52px;font-size:11px;padding:0 8px" title="Stop upload">Stop</button>
<button class="btn compact ghost" id="duShowMon" hidden style="height:24px;font-size:11px">${ic('activity', 'ic-sm')}<span class="bl">Monitor</span></button></div>
<div class="du-detail" id="duDetail">app/Http/Controllers/DeployController.php · 1.9 MB/s</div>
<div class="progress"><i id="duBar" data-progress="22" data-speed="0.45" style="width:22%"></i></div></div>
<div class="du-act"><button class="btn danger" id="duStop" title="Stop upload">${ic('stop')}<span class="bl">Stop</span></button><button class="btn primary" id="duStart" disabled>${ic('rocket')}<span class="bl">Start Upload</span></button></div></div>

<div class="card du-tool"><button class="btn t-info" title="Re-scan the project folder from disk" data-working="Refreshing…" data-done="Refreshed from disk." data-done-msg="412 files · 23 folders">${ic('refresh')}<span class="bl">Refresh</span></button>
<button class="btn t-ok" id="duSelBtn" title="Select All">${ic('check-circle')}<span class="bl" id="duSelLbl">Select</span></button>
<button class="btn t-ok" id="duAuto" style="min-width:48px" title="Auto refresh ON — rescans the local disk every 10s so new files appear.&#10;Click to turn OFF (saves CPU).">⏱ On</button>
<span class="du-stats">Total Files:<b id="duTotal">0</b>Selected:<b class="ok" id="duSel">0</b></span><span class="grow"></span><span class="muted" style="font-size:11px">Type or press <kbd class="badge">Ctrl+F</kbd> to search</span></div>

<div class="card du-tree-card" title="Legend: green = clean · red = changed · blue = untracked · grey = ignored · green background = mapped">
<div class="du-legend">Legend: <i class="ok">green name</i> = clean · <i class="err">red</i> = changed · <i class="inf">blue</i> = untracked · <i>grey</i> = ignored · <i class="ok">green background</i> = path mapping</div>
<div class="du-sbar" id="duSbar" hidden>${ic('search', 'ic-sm')}<input class="input" id="duSearch" placeholder="Search files and folders…" data-filter="#duResults"><button class="btn icon compact ghost" id="duSClear" style="color:var(--text-muted)">${ic('x')}</button></div>
<div class="du-scroll"><div class="tree" id="duTree" data-ctx="ctxMulti" title="Right-click for actions. Drag files from Explorer to add them.">${tree}</div><div class="tree" id="duResults" hidden>${results}</div><div class="du-empty" id="duEmpty">No matching files or folders</div></div></div>

<div class="card du-log"><div class="h">${ic('clipboard', 'ic-sm')}Upload Log</div>
<div class="terminal" data-stream data-interval="1400" data-lines='${JSON.stringify(duLogLines)}'>${duLogInit}<span class="cursor"></span></div></div>
<div class="du-float" ><div id="duMon">${tmPanel('duMonPanel')}</div></div>
</div>${duScript}`;

  const duDialogs = ctxFolder + ctxFile + ctxMulti +
    modal('dlgNewFolder', 'New Folder', 'folder-plus', inputBody('Enter folder name:', 'new-folder'), okCancel('OK', 'data-toast="success" data-title="Created folder" data-msg="new-folder"'), 'sm') +
    modal('dlgNewFile', 'New File', 'file-plus', newFileBody(), okCancel('Create', 'data-toast="success" data-title="Created file" data-msg="Created file .txt"')) +
    modal('dlgProps', 'Properties', 'info', propsBody('folder'), `<button class="btn ghost" style="min-width:110px" data-toast="info" data-title="Copied" data-msg="Path copied to clipboard">Copy path</button><button class="btn primary" data-close style="min-width:90px">Close</button>`) +
    modal('dlgCommit', 'Commit', 'git-commit', inputBody('Commit message for file:\napp/Http/Controllers/DeployController.php', 'update DeployController.php'), okCancel('OK', 'data-toast="success" data-title="Committed" data-msg="Committed app/Http/Controllers/DeployController.php."'), 'sm') +
    modal('dlgMapping', 'Path Mapping', 'link', mappingBody(), `<button class="btn ghost" data-close style="min-width:90px">Close</button>`, 'lg') +
    `<div class="scrim" id="dlgEditor"><div class="modal full" style="grid-template-rows:minmax(0,1fr)">${editorBody('data-close')}</div></div>`;

  L.page({ slug: 'direct-upload', title: 'Direct Upload', nav: 'direct-upload', group: 'Pages', css: duCss, body: duBody, dialogs: duDialogs });

  // ================================================================ CONTROLS
  // DirectUploadWorkspaceControl (same as page, compact dock variant is not separate in XAML)
  L.page({ slug: 'direct-upload-workspace-control', title: 'DirectUploadWorkspaceControl', nav: 'direct-upload', group: 'Controls', css: duCss, body: duBody.replace(/id="duCard"/, 'id="duCard"'), dialogs: duDialogs });

  // LocalFileEditorControl
  L.page({ slug: 'local-file-editor-control', title: 'LocalFileEditorControl', nav: 'direct-upload', group: 'Controls', flush: true, css: CSS_ED + `.ed{border-radius:0}`, body: `<div style="height:100%;padding:14px;background:var(--surface-base)"><div class="card" style="height:100%;padding:0;overflow:hidden">${editorBody('onclick="location.href=\'direct-upload.html\'"')}</div></div>` });

  // TransferMonitorPanel - three states
  const tmFinished = `<div class="tm"><div class="row"><b style="font-size:13px">Upload · 6 workers</b><span class="grow"></span><button class="btn compact ghost" style="height:22px;min-width:48px;padding:0 8px">Hide</button></div><div class="tm-head" style="color:var(--success)">Completed · 412/412 files · 124.6 MB in 00:58</div><div class="progress success" style="height:6px;margin-top:8px"><i style="width:100%"></i></div><div class="tm-pct">124.6 MB / 124.6 MB (100%)</div><div class="tm-sec">Workers</div><div class="tm-slots">${['W1', 'W2', 'W3', 'W4', 'W5', 'W6'].map((w) => `<div class="slot"><span class="slot-id">${w}</span><span class="slot-st done">Done</span><span class="ellipsis sec slot-f">—</span></div>`).join('')}</div><div class="terminal tm-log"><div><span class="m">00:22:46</span> <span class="u">Upload finished: 412 ok, 0 failed</span></div></div><div class="muted" style="font-size:10px;margin-top:8px">Hide only closes this card. Transfer keeps running.</div></div>`;
  const tmFailed = `<div class="tm"><div class="row"><b style="font-size:13px">Upload · 3 workers</b><span class="grow"></span><button class="btn compact ghost" style="height:22px;min-width:48px;padding:0 8px">Hide</button></div><div class="tm-head" style="color:var(--error)">Finished with errors · 38/41 files · 3 failed</div><div class="progress error" style="height:6px;margin-top:8px"><i style="width:93%"></i></div><div class="tm-pct" style="color:var(--error)">9.4 MB / 10.1 MB (93%)</div><div class="tm-sec">Workers</div><div class="tm-slots">${[['W1', 'Error', 'storage/app/cache.lock'], ['W2', 'Done', '—'], ['W3', 'Error', 'storage/framework/sessions']].map(([a, b, c]) => `<div class="slot"><span class="slot-id">${a}</span><span class="slot-st ${b.toLowerCase()}">${b}</span><span class="ellipsis sec slot-f">${c}</span></div>`).join('')}</div><div class="terminal tm-log"><div><span class="m">00:30:12</span> <span class="e">✗ storage/app/cache.lock: 550 Permission denied</span></div><div><span class="m">00:30:12</span> <span class="e">✗ storage/framework/sessions: 553 Could not create file</span></div></div><div class="muted" style="font-size:10px;margin-top:8px">Hide only closes this card. Transfer keeps running.</div></div>`;
  L.page({ slug: 'transfer-monitor-panel', title: 'TransferMonitorPanel', nav: 'direct-upload', group: 'Controls', css: CSS_TM + `.tmg{display:grid;grid-template-columns:repeat(auto-fit,minmax(340px,440px));gap:20px;align-items:start}.tmg .tm{width:100%}`,
    body: `<div class="page-inner"><div class="page-head"><div><h1 class="page-title">Transfer monitor</h1><div class="page-sub">Floating card docked bottom-right of Direct Upload (340–440 px). Running · finished · failed.</div></div><div class="r"><a class="btn" href="transfer-monitor-window.html">${ic('external')}<span class="bl">Open as window</span></a></div></div>
<div class="tmg"><div><div class="eyebrow" style="margin-bottom:8px">Running</div>${tmPanel('tmRun')}</div><div><div class="eyebrow" style="margin-bottom:8px">Finished</div>${tmFinished}</div><div><div class="eyebrow" style="margin-bottom:8px">Failed</div>${tmFailed}</div></div></div>
<script>document.addEventListener('click',function(e){var h=e.target.closest('[data-hide]');if(h){document.getElementById(h.dataset.hide).style.opacity=.25;}});</script>` });

  // ================================================================ WINDOWS
  // TransferMonitorWindow
  L.window({ slug: 'transfer-monitor-window', title: 'Transfer report', icon: 'activity', w: 540, h: 780, group: 'Windows', css: CSS_TM + `.tmw{display:flex;flex-direction:column;gap:0;height:100%;min-height:0}.tmw .box{margin-top:10px;padding:10px;border:1px solid var(--border-subtle);border-radius:8px;background:var(--surface-card)}.tmw .logbox{background:var(--surface-input);flex:1;min-height:200px;display:flex;flex-direction:column}.tmw .logbox .terminal{flex:1;border:0;background:transparent;padding:6px 0 0;font-size:11px;min-height:120px}.tmw .tabs{margin-bottom:8px}.tmw .tabpanel.active{flex:1;min-height:0;display:flex;flex-direction:column}`,
    body: `<div class="tmw" data-tabscope>
${tmHead('Upload · 6 workers', '4/6 workers · 128/412 files · uploading', pctRun, true)}
<div class="box"><div style="font-size:12px;font-weight:600;margin-bottom:8px">Workers (live slots)</div>${slotRows(true)}</div>
<div class="box logbox"><div class="tabs"><button class="tab active" data-tab="tmLive" style="height:30px">Live log</button><button class="tab" data-tab="tmQueue" style="height:30px">Queue ${L.badge('6', 'accent')}</button></div>
<div class="tabpanel active" id="tmLive"><div class="terminal" data-stream data-interval="1300" data-lines='${JSON.stringify(tmLog)}'>${tmInit}<span class="cursor"></span></div></div>
<div class="tabpanel" id="tmQueue" style="overflow:auto">${queueCards()}</div></div>
</div>`, footer: '<span class="l" style="margin-right:0">Close does not cancel the transfer</span>' });

  // RemoteBrowserWindow
  const rbDirs = ['app', 'bootstrap', 'config', 'database', 'lang', 'public', 'resources', 'routes', 'storage', 'tests', 'vendor'];
  L.window({ slug: 'remote-browser-window', title: 'Remote Browser', icon: 'folder-open', w: 420, h: 560, group: 'Windows', css: `.rb{display:flex;flex-direction:column;gap:10px;height:100%;min-height:0}.rb .lst{flex:1;min-height:0;overflow:auto;background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:10px}.rb .list-item{padding:9px 14px;font-size:14px}.rb .list-item .ic{width:18px;height:18px}.rb .list-item .fo{color:var(--warning)}`,
    body: `<div class="rb"><div class="row" style="gap:10px;margin-bottom:5px"><span class="accent">${ic('folder-open', 'ic-xl')}</span><h2 style="font-size:20px;font-weight:700">Select Remote Folder</h2></div>
<input class="input mono" readonly value="/public_html/app" style="height:35px">
<div class="lst"><div class="list" data-selectable><div class="list-item">${ic('arrow-up')}<span>..</span></div>${rbDirs.map((d, i) => `<div class="list-item ${i === 0 ? 'sel' : ''}">${ic('folder', 'fo')}<span>${d}</span></div>`).join('')}</div></div></div>`,
    footer: `<button class="btn ghost" style="height:40px;min-width:100px;font-size:14px">Cancel</button><button class="btn primary" style="height:40px;min-width:100px;font-size:14px;font-weight:700" data-toast="success" data-title="Remote folder selected" data-msg="/public_html/app/app">Select</button>` });

  // RemoteFolderPickerWindow
  const FT = (name, path, o = {}) => `<div class="tn ${o.dir === false ? '' : 'dir'} ${o.open ? 'open' : ''} ${o.sel ? 'sel' : ''}" data-path="${path}"><span class="exp">${o.leaf ? '' : ic('chevron-right')}</span>${ic(o.open ? 'folder-open' : 'folder', 'fi')}<span class="nm">${name}</span></div>${o.kids !== undefined ? `<div class="tchildren">${o.kids}</div>` : ''}`;
  const loadingNode = `<div class="tn" data-nosel><span class="exp"></span>${ic('spinner', 'fi spin')}<span class="nm muted">Loading…</span></div>`;
  L.window({ slug: 'remote-folder-picker-window', title: 'Move to...', icon: 'folder-open', w: 520, h: 500, group: 'Windows', css: `.fp{display:grid;grid-template-rows:auto minmax(0,1fr) auto;gap:12px;height:100%;min-height:0}.fp .box{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:8px;padding:6px;overflow:auto;min-height:0}.fp .tn{height:26px;font-size:12px}`,
    body: `<div class="fp"><div>Choose a destination folder for 'index.php':</div>
<div class="box tree" id="fpTree">${FT('/', '/', { open: 1, kids:
      FT('public_html', '/public_html', { open: 1, kids:
        FT('app', '/public_html/app', { open: 1, kids: FT('Http', '/public_html/app/Http', { sel: 1, open: 1, kids: FT('Controllers', '/public_html/app/Http/Controllers', { open: 1, kids: loadingNode }) + FT('Middleware', '/public_html/app/Http/Middleware', { kids: '' })}) + FT('Models', '/public_html/app/Models', { kids: '' }) }) +
        FT('bootstrap', '/public_html/bootstrap', { kids: '' }) + FT('config', '/public_html/config', { kids: '' }) + FT('database', '/public_html/database', { kids: '' }) + FT('public', '/public_html/public', { kids: '' }) + FT('storage', '/public_html/storage', { kids: '' }) + FT('vendor', '/public_html/vendor', { kids: '' })}) + FT('backups', '/backups', { kids: '' }) + FT('logs', '/logs', { kids: '' })})}</div>
<div class="muted" id="fpSel" style="overflow-wrap:anywhere">Selected: /public_html/app/Http</div></div>
<script>document.addEventListener('click',function(e){var n=e.target.closest('#fpTree .tn[data-path]');if(n)document.getElementById('fpSel').textContent='Selected: '+n.dataset.path;});</script>`,
    footer: `<button class="btn primary" style="min-width:110px" data-toast="success" data-title="Moved" data-msg="index.php → /public_html/app/Http">Move Here</button><button class="btn ghost" style="min-width:90px">Cancel</button>` });

  // RemotePermissionsWindow
  const permRow = (lbl, k, vals) => `<div class="pm-l">${lbl}</div>${vals.map((v, i) => `<label class="pm-c"><span class="check"><input type="checkbox" data-g="${k}" data-b="${[4, 2, 1][i]}" ${v ? 'checked' : ''}></span></label>`).join('')}`;
  L.window({ slug: 'remote-permissions-window', title: 'Permissions', icon: 'lock', w: 440, h: 470, group: 'Windows', css: `.pg{display:grid;grid-template-columns:90px repeat(3,1fr);row-gap:0;align-items:center}.pg .h{text-align:center;font-weight:600;font-size:12px;color:var(--text-muted);margin-bottom:8px}.pg .pm-l{font-size:11px;font-weight:600;color:var(--text-secondary);height:36px;display:flex;align-items:center}.pg .pm-c{display:grid;place-items:center;height:36px}.pg .check{min-height:0}`,
    body: `<div class="stack"><div style="font-size:14px;font-weight:600;overflow-wrap:anywhere">Folder 'storage' Permissions</div>
<div class="card"><div class="pg"><span></span><span class="h">R</span><span class="h">W</span><span class="h">X</span>${permRow('OWNER', 'o', [1, 1, 1])}${permRow('GROUP', 'g', [1, 0, 1])}${permRow('OTHERS', 't', [1, 0, 1])}</div></div>
<div class="row"><span class="sec" style="font-size:12px;margin-right:2px">Octal:</span><input class="input mono" id="pOct" maxlength="4" value="755" style="width:88px;height:32px"><span class="mono muted" id="pSym" style="font-size:12px;margin-left:6px">rwxr-xr-x</span></div>
<div class="hint">Applies to this folder only — not files inside.</div>
<div class="banner error" id="pBan" hidden>${ic('alert')}<span id="pBanT" style="font-size:12px;color:var(--error)"></span></div></div>
<script>(function(){var cb=[].slice.call(document.querySelectorAll('.pg input')),oc=document.getElementById('pOct'),sy=document.getElementById('pSym'),ban=document.getElementById('pBan');
function sym(m){var s='';[6,3,0].forEach(function(sh){var d=(m>>sh)&7;s+=(d&4?'r':'-')+(d&2?'w':'-')+(d&1?'x':'-');});return s;}
function fromBoxes(){var m=0;cb.forEach(function(c){if(c.checked){var sh={o:6,g:3,t:0}[c.dataset.g];m|=(+c.dataset.b)<<sh;}});oc.value=m.toString(8);sy.textContent=sym(m);ban.hidden=true;oc.classList.remove('invalid');}
cb.forEach(function(c){c.addEventListener('change',fromBoxes);});
oc.addEventListener('input',function(){var v=oc.value;if(!/^[0-7]{3,4}$/.test(v)){ban.hidden=false;document.getElementById('pBanT').textContent='Enter a 3-digit octal mode such as 644 or 755.';oc.classList.add('invalid');return;}ban.hidden=true;oc.classList.remove('invalid');var m=parseInt(v.slice(-3),8);cb.forEach(function(c){var sh={o:6,g:3,t:0}[c.dataset.g];c.checked=!!(((m>>sh)&7)&(+c.dataset.b));});sy.textContent=sym(m);});})();</script>`,
    footer: `<button class="btn primary" style="min-width:90px" data-working="Applying…" data-done="Permissions updated" data-done-msg="chmod 755 /public_html/storage">Apply</button><button class="btn ghost" style="min-width:90px">Cancel</button>` });

  // PathMappingModal
  L.window({ slug: 'path-mapping-modal', title: 'Path Mapping', icon: 'link', w: 660, h: 580, group: 'Windows', css: `.pm-row{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 72px;gap:8px;align-items:center;min-height:28px;padding:1px 4px;font-size:12px}.pm-row.pm-h{font-size:11px;font-weight:600;color:var(--text-muted)}.pm-row:not(.pm-h):hover{background:var(--surface-hover);border-radius:6px}`,
    body: mappingBody(), footer: `<button class="btn ghost" style="min-width:90px">Close</button>` });

  // ManageWorkspaceWindow
  L.window({ slug: 'manage-workspace-window', title: 'Manage workspace', icon: 'folder', w: 540, h: 560, group: 'Windows', css: `.mw-list .list-item{padding:7px 10px;font-family:var(--font-mono);font-size:12px}`,
    body: `<div class="stack" style="height:100%"><div class="muted" style="font-size:12px">Primary project root is fixed. Add extra folders the Cursor agent may read and edit (--add-dir).</div>
<div class="field"><label style="color:var(--text-primary)">Primary root</label><input class="input mono" readonly value="C:\\laragon\\www\\subtell" style="font-size:12px"></div>
<div class="card" style="background:var(--surface-shell);padding:6px;flex:1;min-height:140px"><div style="font-size:12px;font-weight:600;margin:4px 4px 8px">Extra roots</div><div class="list mw-list" data-selectable>
<div class="list-item sel">${ic('folder', 'warn')}<span class="grow ellipsis">C:\\laragon\\www\\shared-ui</span></div><div class="list-item">${ic('folder', 'warn')}<span class="grow ellipsis">D:\\projects\\api-gateway</span></div><div class="list-item">${ic('folder', 'warn')}<span class="grow ellipsis">C:\\Users\\poromix\\Documents\\docs</span></div></div></div>
<div class="row"><button class="btn secondary" data-toast="info" data-title="Folder picker" data-msg="Choose folder…" style="padding:0 12px">Add folder</button><button class="btn ghost" data-confirm="Remove the selected extra root?">Remove</button></div></div>`,
    footer: `<button class="btn primary" style="min-width:100px" data-toast="success" data-title="Workspace saved" data-msg="3 extra roots">Save</button><button class="btn ghost" style="min-width:100px">Cancel</button>` });

  // ProcessMonitorWindow
  const procs = [[1, 'root', 'localhost:51233', 'gitdeploy', 'Query', 0, 'executing', 'SHOW FULL PROCESSLIST'], [14, 'app_user', '10.0.4.17:44120', 'subtell', 'Sleep', 124, '', 'NULL'], [27, 'app_user', '10.0.4.17:44122', 'subtell', 'Query', 12, 'Sending data', "SELECT * FROM orders WHERE status = 'pending' ORDER BY created_at DESC"], [31, 'backup', '127.0.0.1:50011', 'subtell', 'Query', 38, 'Copying to tmp table', 'SELECT o.*, u.email FROM orders o JOIN users u ON u.id = o.user_id'], [5, 'replica', '10.0.4.21:38102', 'NULL', 'Binlog Dump', 86412, 'Master has sent all binlog to slave', 'NULL'], [2, 'event_scheduler', 'localhost', 'NULL', 'Daemon', 86412, 'Waiting on empty queue', 'NULL']];
  L.window({ slug: 'process-monitor-window', title: 'Database Process Monitor', icon: 'cpu', w: 1040, h: 560, group: 'Windows', css: `.pmon{display:grid;grid-template-rows:auto minmax(0,1fr) auto;gap:16px;height:100%;min-height:0}.pmon .conn{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.pmon .conn .select{flex:1;min-width:200px}.pmon .gw{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:10px;min-height:0}.pmon td.info{max-width:420px;overflow:hidden;text-overflow:ellipsis;font-family:var(--font-mono);font-size:12px}.pmon .slow{color:var(--warning);font-weight:700}`,
    body: `<div class="pmon"><div class="card dense"><div class="muted" style="font-size:13px;margin-bottom:8px">Select a database connection</div><div class="conn">${L.sel([['Production MySQL · db.mysite.com:3306', 1], 'Staging MariaDB · 10.0.4.30:3306', 'Local · 127.0.0.1:3306 (PostgreSQL)'])}<button class="btn icon" style="height:32px" title="Reload saved connections">${ic('refresh')}</button><button class="btn primary" style="height:32px;padding:0 18px" data-working="Connecting…" data-done="Connected" data-done-msg="Connected to Production MySQL.">${ic('wifi')}<span class="bl">Connect</span></button><button class="btn toolbar" id="pmRefresh" style="height:32px;padding:0 18px">Refresh list</button>
<span class="muted" style="margin-left:6px">Auto-refresh:</span>${L.sel([['Off', 1], '5 seconds', '10 seconds', '30 seconds', '60 seconds'], 'sm')}</div></div>
<div class="gw grid-wrap"><table class="dg" data-select><thead><tr><th>ID</th><th>User</th><th>Host</th><th>Database</th><th>Command</th><th>Time (s)</th><th>State</th><th>Info</th></tr></thead><tbody>${procs.map((p) => `<tr><td class="mono">${p[0]}</td><td>${p[1]}</td><td class="mono">${p[2]}</td><td>${p[3]}</td><td>${p[4]}</td><td class="num ${p[5] >= 10 && p[5] < 1000 ? 'slow' : ''}" data-t="${p[5]}">${p[5]}</td><td>${p[6]}</td><td class="info" title="${esc(p[7])}">${esc(p[7])}</td></tr>`).join('')}</tbody></table></div>
<div class="row"><div class="progress indeterminate" id="pmBusy" hidden style="width:180px;height:6px"><i></i></div><span class="sec" id="pmStat">Updated 00:41:07 · 6 sessions.</span></div></div>
<script>(function(){var b=document.getElementById('pmRefresh'),bar=document.getElementById('pmBusy'),s=document.getElementById('pmStat');
function t(){var d=new Date();return ('0'+d.getHours()).slice(-2)+':'+('0'+d.getMinutes()).slice(-2)+':'+('0'+d.getSeconds()).slice(-2)}
b.addEventListener('click',function(){bar.hidden=false;s.textContent='Loading process list...';setTimeout(function(){bar.hidden=true;s.textContent='Updated '+t()+' · 6 sessions.';},1200);});
setInterval(function(){document.querySelectorAll('td[data-t]').forEach(function(c){var v=+c.dataset.t;if(v>0){v++;c.dataset.t=v;c.textContent=v;}});},1000);})();</script>`,
    footer: '<span class="l" style="margin-right:0">Read-only view of SHOW FULL PROCESSLIST</span>' });

  // CodeViewerWindow
  L.window({ slug: 'code-viewer-window', title: 'Code Viewer', icon: 'code', w: 1200, h: 720, group: 'Windows', flush: true, css: `.cv{display:grid;grid-template-rows:46px minmax(0,1fr);height:100%;min-height:0}.cv-h{display:flex;align-items:center;gap:8px;padding:0 10px 0 16px;background:var(--surface-card);border-bottom:1px solid var(--border-subtle);flex-wrap:wrap;overflow:hidden}.cv-h .btn{height:30px}.cv-h b{font-size:15px;display:block;line-height:1.2}.cv-h small{display:block;color:var(--text-muted);font-size:12px;font-family:var(--font-mono);line-height:1.2}.cv .code{border:0;border-radius:0;font-size:13px;align-content:start;background:var(--surface-input);min-height:0}.cv .code pre{outline:none}.cv .code.edit{box-shadow:inset 0 0 0 1px var(--accent)}`,
    body: `<div class="cv"><div class="cv-h"><div class="grow" style="min-width:0"><b class="ellipsis">DeployController.php</b><small class="ellipsis">C:\\laragon\\www\\subtell\\app\\Http\\Controllers\\DeployController.php</small></div>
<button class="btn secondary" id="cvEdit" style="width:120px">Enable Edit</button><button class="btn primary" style="width:90px" data-working="Saving…" data-done="Saved" data-done-msg="DeployController.php">Save</button><button class="btn ghost" style="width:80px" data-toast="info" data-title="Copied" data-msg="File content copied to clipboard">Copy</button><button class="btn danger" style="width:80px" onclick="location.href='direct-upload.html'">Close</button></div>
<div style="overflow:auto;min-height:0" id="cvBox">${codeBlock(PHP + '\n// …\n', false)}</div></div>
<script>(function(){var b=document.getElementById('cvEdit'),c=document.querySelector('#cvBox .code'),p=c.querySelector('pre');b.addEventListener('click',function(){var on=p.getAttribute('contenteditable')!=='true';p.setAttribute('contenteditable',on);p.setAttribute('spellcheck','false');c.classList.toggle('edit',on);b.textContent=on?'Disable Edit':'Enable Edit';if(on)p.focus();});})();</script>`,
    footer: '<span class="l" style="margin-right:0">PHP · UTF-8 · LF · Read-only</span>' });

  // EditorHostWindow
  L.window({ slug: 'editor-host-window', title: 'Editor', sub: 'DeployController.php', icon: 'edit', w: 960, h: 720, group: 'Windows', flush: true, css: CSS_ED + `.ed{padding:8px 8px 8px}`, body: `<div style="height:100%;padding:0;background:var(--surface-base)">${editorBody('onclick="location.href=\'direct-upload.html\'"', 'Simple editor ready. Loading Monaco…')}</div>`, footer: '<span class="l" style="margin-right:0">Hosted LocalFileEditorControl · floated from Direct Upload</span>' });

  // LocalItemPropertiesWindow
  L.window({ slug: 'local-item-properties-window', title: 'Properties', icon: 'info', w: 540, h: 500, group: 'Windows', css: '', body: propsBody('file'),
    footer: `<button class="btn ghost" style="min-width:110px" data-toast="info" data-title="Copied" data-msg="Path copied to clipboard">Copy path</button><button class="btn primary" style="min-width:90px">Close</button>` });

  // NewFileDialog
  L.window({ slug: 'new-file-dialog', title: 'New File', icon: 'file-plus', w: 460, h: 360, group: 'Windows', css: '', body: newFileBody(), footer: `<button class="btn primary" style="min-width:90px" data-toast="success" data-title="Created file" data-msg="Created file .txt">Create</button><button class="btn ghost" style="min-width:90px">Cancel</button>` });

  // InputDialog
  L.window({ slug: 'input-dialog', title: 'NEW FOLDER', icon: 'folder-plus', w: 450, h: 330, group: 'Windows', css: '', body: inputBody('Enter folder name:', 'new-folder'), footer: `<button class="btn primary" style="min-width:90px" data-toast="success" data-title="Created folder" data-msg="new-folder">OK</button><button class="btn ghost" style="min-width:90px">Cancel</button>` });
};
