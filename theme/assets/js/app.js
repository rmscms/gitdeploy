// GitDeployPro theme prototype behaviour. Plain JS, no dependencies, no alert().
(function () {
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  // ---- tabs: <button class="tab" data-tab="panelId"> inside any container; panels = .tabpanel#panelId ----
  document.addEventListener('click', function (e) {
    var t = e.target.closest('.tab[data-tab]');
    if (!t) return;
    var scope = t.closest('[data-tabscope]') || document;
    var bar = t.parentNode;
    $$('.tab', bar).forEach(function (x) { x.classList.toggle('active', x === t); });
    var ids = $$('.tab[data-tab]', bar).map(function (x) { return x.dataset.tab; });
    $$('.tabpanel', scope).forEach(function (p) { if (ids.indexOf(p.id) > -1) p.classList.toggle('active', p.id === t.dataset.tab); });
  });

  // ---- popups (nav menu, project switcher) ----
  document.addEventListener('click', function (e) {
    var tg = e.target.closest('[data-popup]');
    $$('.popup.open').forEach(function (p) { if (!tg || p.id !== tg.dataset.popup) p.classList.remove('open'); });
    $$('.proj-btn.open').forEach(function (b) { if (b !== tg) b.classList.remove('open'); });
    if (tg) { var p = document.getElementById(tg.dataset.popup); if (p) { p.classList.toggle('open'); tg.classList.toggle('open', p.classList.contains('open')); } e.stopPropagation(); }
    var sub = e.target.closest('[data-sub]');
    if (sub) { var s = document.getElementById(sub.dataset.sub); if (s) s.hidden = !s.hidden; }
  });

  // ---- modals ----
  function openM(id) { var m = document.getElementById(id); if (m) { m.classList.add('open'); var f = $('input:not([type=checkbox]):not([type=radio]),select,textarea', m); if (f) setTimeout(function () { f.focus(); }, 0); } }
  function closeM(m) { if (m) m.classList.remove('open'); }
  document.addEventListener('click', function (e) {
    var o = e.target.closest('[data-open]'); if (o) { e.preventDefault(); openM(o.dataset.open); return; }
    var c = e.target.closest('[data-close]'); if (c) { closeM(c.closest('.scrim')); return; }
    if (e.target.classList && e.target.classList.contains('scrim')) closeM(e.target);
    var cf = e.target.closest('[data-confirm]');           // shared confirm dialog (never alert)
    if (cf) { e.preventDefault(); var m = document.getElementById('dlgConfirmMsg'); if (m) m.textContent = cf.dataset.confirm; var t = document.getElementById('dlgConfirmTitle'); if (t && cf.dataset.confirmTitle) t.textContent = cf.dataset.confirmTitle; openM('dlgConfirm'); }
  });
  document.addEventListener('keydown', function (e) {
    var m = $('.scrim.open'); if (!m) return;
    if (e.key === 'Escape') closeM(m);
    if (e.key === 'Enter' && e.target.tagName !== 'TEXTAREA') { var p = $('.modal-f .btn.primary, .modal-f .btn.danger.solid', m); if (p) p.click(); }
  });

  // ---- toasts ----
  var icons = { success: 'check-circle', error: 'x-circle', warning: 'alert', info: 'info' };
  window.toast = function (o) {
    var host = $('.shell') || document.body, box = $('.toasts', host);
    if (!box) { box = document.createElement('div'); box.className = 'toasts'; host.appendChild(box); }
    var type = o.type || 'info', dur = o.duration == null ? 4000 : o.duration, el = document.createElement('div');
    el.className = 'toast ' + type;
    el.innerHTML = '<svg class="ic"><use href="#i-' + icons[type] + '"/></svg><div><div class="t"></div><div class="m"></div></div><button class="x" type="button"><svg class="ic"><use href="#i-x"/></svg></button>' + (dur ? '<span class="bar" style="animation-duration:' + dur + 'ms"></span>' : '');
    el.querySelector('.t').textContent = o.title || type; el.querySelector('.m').textContent = o.msg || '';
    var close = function () { if (el.classList.contains('out')) return; el.classList.add('out'); setTimeout(function () { el.remove(); }, 180); };
    el.querySelector('.x').onclick = close; box.appendChild(el); if (dur) setTimeout(close, dur);
  };
  document.addEventListener('click', function (e) {
    var b = e.target.closest('[data-toast]');
    if (b) toast({ type: b.dataset.toast, title: b.dataset.title, msg: b.dataset.msg, duration: b.dataset.duration ? +b.dataset.duration : undefined });
    var ok = e.target.closest('#dlgConfirm .btn.primary, #dlgConfirm .btn.danger.solid');
    if (ok) { toast({ type: 'success', title: 'Done', msg: 'Action confirmed' }); closeM($('#dlgConfirm')); }
    // button that simulates a long-running action
    var w = e.target.closest('[data-working]');
    if (w && !w.classList.contains('working')) {
      w.classList.add('working'); var lbl = w.querySelector('.bl'), old = lbl && lbl.textContent;
      if (lbl && w.dataset.working) lbl.textContent = w.dataset.working;
      setTimeout(function () { w.classList.remove('working'); if (lbl) lbl.textContent = old; if (w.dataset.done) toast({ type: 'success', title: w.dataset.done, msg: w.dataset.doneMsg || '' }); }, 1600);
    }
  });

  // ---- tree expand / select, grid row select, list select ----
  document.addEventListener('click', function (e) {
    var n = e.target.closest('.tn');
    if (n) {
      var tree = n.closest('.tree'); if (tree && !e.target.closest('.exp') && !n.hasAttribute('data-nosel')) { $$('.tn.sel', tree).forEach(function (x) { x.classList.remove('sel'); }); n.classList.add('sel'); }
      if (n.classList.contains('dir') && (e.target.closest('.exp') || e.detail === 2 || true)) n.classList.toggle('open');
    }
    var tr = e.target.closest('table.dg[data-select] tbody tr');
    if (tr) { $$('tr.sel', tr.parentNode).forEach(function (r) { r.classList.remove('sel'); }); tr.classList.add('sel'); }
    var li = e.target.closest('[data-selectable] > .list-item, [data-selectable] > .thread');
    if (li) { $$('.sel', li.parentNode).forEach(function (r) { r.classList.remove('sel'); }); li.classList.add('sel'); }
    var ch = e.target.closest('.chip[data-chip]'); if (ch) ch.classList.toggle('active');
    var bg = e.target.closest('.btn-group[data-single] .btn'); if (bg) { $$('.btn', bg.parentNode).forEach(function (x) { x.classList.toggle('active', x === bg); }); }
  });

  // ---- context menu: right-click on [data-ctx="menuId"] ----
  document.addEventListener('contextmenu', function (e) {
    var t = e.target.closest('[data-ctx]'); if (!t) return;
    var m = document.getElementById(t.dataset.ctx); if (!m) return;
    e.preventDefault(); var host = $('.shell') || document.body, r = host.getBoundingClientRect();
    m.style.left = Math.min(e.clientX - r.left, r.width - 230) + 'px'; m.style.top = Math.min(e.clientY - r.top, r.height - 260) + 'px'; m.classList.add('open');
  });
  document.addEventListener('click', function () { $$('.ctx.open').forEach(function (m) { m.classList.remove('open'); }); });

  // ---- live workers: progress bars advance, finished workers flip to "done" ----
  // <div class="worker running" data-worker> ... <div class="progress"><i data-progress="35" data-speed="1.2"></i></div> <span data-pct></span> <span data-state class="badge"></span>
  var bars = $$('[data-progress]');
  if (bars.length) setInterval(function () {
    bars.forEach(function (b) {
      var w = b.closest('[data-worker]'); if (w && !w.classList.contains('running')) return;
      var v = parseFloat(b.dataset.progress) || 0, sp = parseFloat(b.dataset.speed) || 1; v += sp * (0.4 + Math.random() * 1.2);
      if (v >= 100) { if (b.hasAttribute('data-loop')) v = 0; else { v = 100; if (w) { w.classList.remove('running'); w.classList.add('done'); var st = $('[data-state]', w); if (st) { st.className = 'badge success'; st.textContent = 'Done'; } } } }
      b.dataset.progress = v; b.style.width = v.toFixed(1) + '%';
      var p = w && $('[data-pct]', w); if (p) p.textContent = Math.floor(v) + '%';
    });
  }, 600);
  bars.forEach(function (b) { b.style.width = (parseFloat(b.dataset.progress) || 0) + '%'; });
  // pause / resume buttons inside a worker
  document.addEventListener('click', function (e) {
    var pb = e.target.closest('[data-worker-toggle]'); if (!pb) return;
    var w = pb.closest('[data-worker]'); var run = w.classList.toggle('running'); w.classList.toggle('paused', !run);
    var st = $('[data-state]', w); if (st) { st.className = 'badge ' + (run ? 'accent' : 'warning'); st.textContent = run ? 'Running' : 'Paused'; }
    var u = pb.querySelector('use'); if (u) u.setAttribute('href', run ? '#i-pause' : '#i-play');
  });

  // ---- streaming logs: <div data-stream data-lines='[["i","msg"],...]' data-interval="900"> ----
  $$('[data-stream]').forEach(function (box) {
    var lines; try { lines = JSON.parse(box.dataset.lines || '[]'); } catch (x) { lines = []; } if (!lines.length) return;
    var i = 0, cls = { i: 'i', ok: 'u', w: 'w', e: 'e', m: 'm', c: 'c', p: 'p' };
    var term = box.classList.contains('terminal');
    setInterval(function () {
      var l = lines[i++ % lines.length], d = document.createElement('div');
      var ts = new Date().toTimeString().slice(0, 8);
      if (term) { d.innerHTML = '<span class="m">' + ts + '</span> <span class="' + (cls[l[0]] || 'm') + '"></span>'; d.lastChild.textContent = l[1]; }
      else { d.className = 'ln'; d.innerHTML = '<span class="ts">' + ts + '</span><span class="lv ' + l[0] + '">' + ({ i: 'INFO', ok: 'OK', w: 'WARN', e: 'ERROR' }[l[0]] || '') + '</span><span></span>'; d.lastChild.textContent = l[1]; }
      var cur = box.querySelector('.cursor'); if (cur && cur.parentNode === box) box.insertBefore(d, cur); else box.appendChild(d);
      while (box.children.length > 400) box.removeChild(box.firstChild);
      box.scrollTop = box.scrollHeight;
    }, parseInt(box.dataset.interval, 10) || 900);
  });

  // ---- counters that tick (uptime / elapsed) ----
  $$('[data-elapsed]').forEach(function (el) { var s = parseInt(el.dataset.elapsed, 10) || 0; setInterval(function () { s++; el.textContent = ('0' + Math.floor(s / 3600)).slice(-2) + ':' + ('0' + Math.floor(s / 60) % 60).slice(-2) + ':' + ('0' + s % 60).slice(-2); }, 1000); });

  // ---- simple filter box: <input data-filter="#listId"> hides non-matching children ----
  document.addEventListener('input', function (e) {
    var f = e.target.closest('[data-filter]'); if (!f) return; var q = f.value.toLowerCase(), box = $(f.dataset.filter);
    if (box) $$(f.dataset.itemSel || ':scope > *', box).forEach(function (c) { c.hidden = q && c.textContent.toLowerCase().indexOf(q) < 0; });
  });
})();
