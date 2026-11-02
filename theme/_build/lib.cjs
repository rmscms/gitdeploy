// Shared helpers + page registry for the GitDeployPro theme generator.
// Page files in _build/pages/*.cjs export (L) => { L.page({...}); L.window({...}); }
const fs = require('fs');
const path = require('path');
const root = path.join(__dirname, '..');
const icons = require('./icons.cjs');
const sprite = icons.sprite();

const L = { pages: [], root };
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
L.esc = esc;
L.ic = (n, c = '') => {
  if (!icons.I[n]) throw new Error('unknown icon: ' + n);
  return `<svg class="ic ${c}"><use href="#i-${n}"/></svg>`;
};
const ic = L.ic;
// button: L.btn('rocket', 'Deploy', 'primary', 'data-open="x"')   (icon or label may be '')
L.btn = (icon, label, cls = '', attrs = '') => `<button type="button" class="btn ${cls}" ${attrs}>${icon ? ic(icon) : ''}${label ? `<span class="bl">${label}</span>` : ''}</button>`;
L.iconBtn = (icon, title = '', cls = '', attrs = '') => `<button type="button" class="btn icon ${cls}" title="${esc(title)}" ${attrs}>${ic(icon)}</button>`;
L.field = (label, input, cls = '', hint = '') => `<div class="field ${cls}"><label>${label}</label>${input}${hint ? `<div class="hint">${hint}</div>` : ''}</div>`;
L.inp = (ph = '', v = '', type = 'text', cls = '') => `<input class="input ${cls}" type="${type}" placeholder="${esc(ph)}" value="${esc(v)}">`;
L.sel = (opts, cls = '') => `<select class="select ${cls}">${opts.map((o) => (Array.isArray(o) ? `<option ${o[1] ? 'selected' : ''}>${o[0]}</option>` : `<option>${o}</option>`)).join('')}</select>`;
L.chk = (label, on = false, type = 'checkbox', name = '') => `<label class="check"><input type="${type}" ${name ? `name="${name}"` : ''} ${on ? 'checked' : ''}>${label}</label>`;
L.sw = (label, on = false) => `<label class="switch"><input type="checkbox" ${on ? 'checked' : ''}>${label}</label>`;
L.badge = (t, cls = '') => `<span class="badge ${cls}">${t}</span>`;
L.card = (title, inner, { icon = '', right = '', cls = '' } = {}) => `<div class="card ${cls}">${title || right ? `<div class="card-head">${icon ? ic(icon) : ''}<h3>${title}</h3>${right ? `<div class="r">${right}</div>` : ''}</div>` : ''}${inner}</div>`;
L.stat = (icon, value, label, cls = '') => `<div class="card dense"><div class="stat ${cls}"><div class="si">${ic(icon, 'ic-lg')}</div><div><b>${value}</b><small>${label}</small></div></div></div>`;
L.progress = (pct, cls = '', attrs = '') => `<div class="progress ${cls}"><i style="width:${pct}%" ${attrs}></i></div>`;
L.avatar = (name, cls = '', color = '') => `<span class="avatar ${cls}" style="${color ? `background:${color}` : ''}">${esc(name.charAt(0).toUpperCase())}</span>`;
// live worker card
L.worker = ({ icon = 'zap', name, desc = '', pct = 0, speed = 1, state = 'running', stats = [], loop = false, controls = true }) => {
  const st = { running: ['accent', 'Running'], paused: ['warning', 'Paused'], done: ['success', 'Done'], failed: ['error', 'Failed'], queued: ['', 'Queued'] }[state];
  return `<div class="worker ${state}" data-worker><div class="wi">${ic(icon, 'ic-lg')}</div><div><div class="row"><span class="wn ellipsis">${name}</span><span class="badge ${st[0]}" data-state>${st[1]}</span></div><div class="wd ellipsis">${desc}</div></div>
<div class="row">${state === 'running' || state === 'paused' ? (controls ? `<button class="btn icon compact ghost" data-worker-toggle title="Pause / resume">${ic(state === 'paused' ? 'play' : 'pause')}</button><button class="btn icon compact ghost" data-confirm="Cancel this job?" title="Cancel">${ic('x')}</button>` : '') : ''}</div>
<div class="wp"><div class="progress ${state === 'running' ? 'busy' : state === 'done' ? 'success' : state === 'failed' ? 'error' : ''}"><i data-progress="${pct}" data-speed="${speed}" ${loop ? 'data-loop' : ''} style="width:${pct}%"></i></div>
<div class="wstat" style="margin-top:6px"><span><b data-pct>${Math.floor(pct)}%</b></span>${stats.map((s) => `<span>${s}</span>`).join('')}</div></div></div>`;
};
L.stream = (lines, { cls = 'terminal', interval = 900, style = '' } = {}) => `<div class="${cls}" data-stream data-interval="${interval}" data-lines='${JSON.stringify(lines).replace(/'/g, '&#39;')}' style="${style}"><span class="cursor"></span></div>`;
// file tree node helpers
L.node = (name, { dir = false, open = false, type = '', git = '', meta = '', sel = false, children = '' } = {}) => {
  const ext = type || (dir ? '' : (name.split('.').pop() || ''));
  const t = { php: 'php', js: 'js', ts: 'js', css: 'css', scss: 'css', html: 'html', json: 'json', md: 'md', png: 'img', jpg: 'img', svg: 'img', cs: 'cs', xaml: 'xaml' }[ext] ? `t-${{ php: 'php', js: 'js', ts: 'js', css: 'css', scss: 'css', html: 'html', json: 'json', md: 'md', png: 'img', jpg: 'img', svg: 'img', cs: 'cs', xaml: 'xaml' }[ext]}` : '';
  const gm = { M: ['git-m', 'warning', 'M'], A: ['git-a', 'success', 'A'], D: ['git-d', 'error', 'D'], U: ['git-a', 'success', 'U'] }[git];
  return `<div class="tn ${dir ? 'dir' : ''} ${open ? 'open' : ''} ${t} ${gm ? gm[0] : ''} ${sel ? 'sel' : ''}"><span class="exp">${dir ? ic('chevron-right') : ''}</span>${ic(dir ? (open ? 'folder-open' : 'folder') : (t === 't-img' ? 'image' : t ? 'file-code' : 'file-text'), 'fi')}<span class="nm">${name}</span>${gm ? `<span class="gb ${gm[1]}">${gm[2]}</span>` : ''}${meta ? `<span class="meta">${meta}</span>` : ''}</div>${dir ? `<div class="tchildren">${children}</div>` : ''}`;
};

// ---------------- app shell ----------------
const NAV = [
  ['deploy', 'rocket', 'Deploy', 'deploy.html'], ['dashboard', 'dashboard-ic', 'Dashboard', 'dashboard.html'], ['direct-upload', 'upload', 'Direct Upload', 'direct-upload.html'],
  ['database', 'database', 'Database', 'database.html'], ['git', 'git-branch', 'GitHub', 'git.html'], ['backup', 'calendar', 'Backup plans', 'backup.html'],
  ['history', 'history', 'History', 'history.html'], ['telegram', 'chat', 'Telegram', 'telegram.html'], ['settings', 'settings', 'Settings', 'settings.html'],
];
NAV[1][1] = 'activity';
L.confirmDialog = `<div class="scrim" id="dlgConfirm"><div class="modal sm"><div class="modal-h">${ic('alert')}<h3 id="dlgConfirmTitle">Confirm</h3></div><div class="modal-b" style="text-align:center"><div class="hero warn">${ic('alert', 'ic-xl')}</div><div id="dlgConfirmMsg">Are you sure?</div></div><div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary">Confirm</button></div></div></div>`;
const headerBtn = (icon, tip, href = '#', extra = '') => `<a class="tb-icon ${extra}" href="${href}" title="${tip}">${ic(icon)}</a>`;

L.shell = ({ nav = '', body, flush = false, project = 'MyProject', dialogs = '' }) => `<div class="app"><div class="shell">
<header class="titlebar">
  <button class="tb-icon nav-btn" data-popup="navMenu" title="Menu">${ic('menu', 'ic-lg')}</button>
  <button class="proj-btn" data-popup="projMenu" title="Switch project"><span class="avatar">${project.charAt(0)}</span><span class="nm ellipsis">${project}</span>${ic('chevron-down')}</button>
  <span class="tb-sep"></span>
  ${headerBtn('rocket', 'Deploy', 'deploy.html', nav === 'deploy' ? 'on' : '')}${headerBtn('folder-open', 'Open in Explorer')}${headerBtn('terminal', 'Local terminal', 'terminal-window.html')}${headerBtn('settings', 'Settings', 'settings.html', nav === 'settings' ? 'on' : '')}${headerBtn('database', 'Database backup', 'database.html', nav === 'database' ? 'on' : '')}
  <div class="grow"></div>
  <a class="update-pill tb-opt" href="update-status-window.html" title="Check updates">${ic('refresh', 'ic-sm')}v2.4.0 · up to date</a>
  <span class="tb-sep tb-opt"></span>
  <div class="wc"><button title="Minimize">${ic('minus')}</button><button title="Maximize">${ic('square')}</button><button class="close" title="Close">${ic('x')}</button></div>
</header>
<div class="popup nav" id="navMenu">
  ${NAV.map(([s, i, l, h]) => `<a class="pi ${s === nav ? 'active' : ''}" href="${h}">${ic(i)}<span>${l}</span></a>`).join('')}
  <a class="pi" data-sub="themeSub" href="#">${ic('palette')}<span>Theme</span><span class="k">▸</span></a>
  <div id="themeSub" hidden><a class="pi" style="padding-left:34px" href="#">Midnight Blue ✓</a><a class="pi" style="padding-left:34px" href="#">Dark</a><a class="pi" style="padding-left:34px" href="#">Light</a></div>
  <div class="pdiv"></div>
  <a class="pi" href="update-status-window.html">${ic('refresh')}<span>Check for updates</span></a><a class="pi ${nav === 'about' ? 'active' : ''}" href="about.html">${ic('info')}<span>About</span></a><a class="pi" data-confirm="Exit GitDeploy Pro?" href="#">${ic('power')}<span>Exit</span></a>
  <div class="pfoot">Version 2.4.0</div>
</div>
<div class="popup proj" id="projMenu">
  <a class="pi" href="project-picker-window.html">${ic('folder-open')}<span>Open project…</span><span class="k">Ctrl+O</span></a><a class="pi" href="project-setup-wizard.html">${ic('plus')}<span>New project…</span></a>
  <div class="pdiv"></div><div class="phead">Recent projects</div>
  ${[['MyProject', 'C:\\laragon\\www\\myproject', '#4c8dff'], ['SubTell', 'C:\\laragon\\www\\subtell', '#7b61ff'], ['CartEx', 'C:\\laragon\\www\\cartex', '#3ddc84'], ['Bourse', 'C:\\laragon\\www\\bourse', '#e6b84d']].map(([n, p, c]) => `<a class="recent" href="dashboard.html"><span class="avatar lg" style="background:${c}">${n.charAt(0)}</span><span><b>${n}</b><small>${p}</small></span></a>`).join('')}
  <div class="pdiv"></div><a class="pi" href="project-picker-window.html">${ic('search')}<span>More / Search…</span></a>
</div>
<main class="page ${flush ? 'flush' : ''}" style="position:relative">${body}</main>
${L.confirmDialog}${dialogs}
</div></div>`;

// ---------------- registry ----------------
const htmlDoc = (slug, title, inner, extraHead = '') => `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)} · GitDeploy Pro</title>
<link rel="stylesheet" href="assets/css/core.css"><link rel="stylesheet" href="assets/css/pages/${slug}.css">${extraHead}
</head><body>${sprite}
${inner}
<script src="assets/js/app.js"></script></body></html>`;

// L.page: a page rendered inside the main app shell (Pages/*.xaml, Controls shown as pages)
L.page = ({ slug, title, nav = slug, group = 'Pages', css = '', body, flush = false, dialogs = '', project }) => {
  L.pages.push({ slug, title, group, css, html: htmlDoc(slug, title, L.shell({ nav, body, flush, dialogs, project })) });
};
// L.window: a standalone modal window (Windows/*.xaml)
L.window = ({ slug, title, sub = '', icon = 'layers', w = 760, h = 560, group = 'Windows', css = '', body, footer = '', flush = false, dialogs = '' }) => {
  const inner = `<div class="win-stage"><div class="win" style="--w:${w}px;--h:${h}px"><div class="wt">${ic(icon)}<span>${title}</span>${sub ? `<span class="sub">${sub}</span>` : ''}<div class="wc"><button title="Minimize">${ic('minus')}</button><button class="close" title="Close">${ic('x')}</button></div></div>
<div class="wbody ${flush ? 'flush' : ''}">${body}</div><div class="wfoot">${footer || `<button class="btn">Close</button>`}</div>${L.confirmDialog}${dialogs}</div></div>`;
  L.pages.push({ slug, title, group, css, html: htmlDoc(slug, title, inner) });
};
L.raw = ({ slug, title, group = 'Pages', css = '', html }) => L.pages.push({ slug, title, group, css, html: htmlDoc(slug, title, html) });

L.emit = () => {
  fs.mkdirSync(path.join(root, 'assets/css/pages'), { recursive: true });
  fs.mkdirSync(path.join(root, 'assets/icons'), { recursive: true });
  fs.writeFileSync(path.join(root, 'assets/icons/sprite.svg'), sprite);
  const seen = new Set();
  for (const p of L.pages) {
    if (seen.has(p.slug)) throw new Error('duplicate slug: ' + p.slug);
    seen.add(p.slug);
    fs.writeFileSync(path.join(root, `assets/css/pages/${p.slug}.css`), `/* page: ${p.slug} */\n${p.css}\n`);
    fs.writeFileSync(path.join(root, `${p.slug}.html`), p.html);
  }
  const groups = {};
  L.pages.forEach((p) => (groups[p.group] = groups[p.group] || []).push(p));
  fs.writeFileSync(path.join(root, 'index.html'), htmlDoc('index', 'Index', `<div style="padding:40px;max-width:1100px;margin:auto"><div class="row" style="gap:14px"><span class="avatar xl">G</span><div><h1 class="page-title">GitDeploy Pro · Midnight Blue theme</h1><div class="page-sub">HTML spec for the WPF app — ${L.pages.length} screens. See CONVERSION.md</div></div></div>${Object.entries(groups).map(([g, ps]) => `<div class="section-title" style="margin-top:28px">${g} <span class="badge">${ps.length}</span></div><div class="grid auto-fit-sm">${ps.map((p) => `<a class="btn" style="justify-content:flex-start" href="${p.slug}.html">${esc(p.title)}</a>`).join('')}</div>`).join('')}</div>`));
  fs.writeFileSync(path.join(root, 'assets/css/pages/index.css'), '');
  return L.pages.length;
};
module.exports = L;
