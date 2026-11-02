// Terminal / Saved commands / Telegram chat / ModernMessageBox screens
module.exports = (L) => {
  const ic = L.ic;

  // ---------------------------------------------------------------- shared CSS
  const XCSS = `
.xc{--xfs:14px;--xfg:#d4d4d4;display:flex;flex-direction:column;height:100%;min-height:0;background:var(--surface-base);position:relative}
.xc-bar{display:flex;align-items:center;gap:10px;height:32px;flex:none;padding:0 8px 0 10px;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);font-size:11px;min-width:0;overflow:hidden}
.xc-bar .st{display:flex;align-items:center;gap:6px;color:var(--text-muted);min-width:0;white-space:nowrap}
.xc-bar .st .stt{overflow:hidden;text-overflow:ellipsis}
.xc-bar .st.ok .stt{color:var(--text-secondary)} .xc-bar .st.err .stt{color:var(--error)} .xc-bar .st.warn .stt{color:var(--warning)}
.xc-bar .badge{height:18px;font-size:10px;padding:0 6px}
.xc-run{display:none;align-items:center;gap:6px;height:20px;padding:0 8px;border-radius:999px;background:var(--accent-soft);border:1px solid rgba(76,141,255,.35);color:var(--accent);white-space:nowrap;min-width:0}
.xc-run code{max-width:220px;overflow:hidden;text-overflow:ellipsis;color:var(--text-primary);font-size:11px} .xc.busy .xc-run{display:inline-flex} .xc-run .ic{width:12px;height:12px}
.xc-tools{margin-left:auto;display:flex;align-items:center;gap:2px;flex:none}
.xt-btn{width:28px;height:26px;border:0;background:none;border-radius:3px;color:var(--text-secondary);display:grid;place-items:center;margin-right:2px;cursor:pointer}
.xt-btn:hover{background:var(--surface-hover)} .xt-btn.on{background:var(--surface-raised);color:var(--text-primary)} .xt-btn .ic{width:15px;height:15px}
.xc .select.xs{height:24px;font-size:11px;padding:0 16px 0 6px;border-radius:5px;background-position:calc(100% - 10px) 10px,calc(100% - 6px) 10px;background-size:4px 4px;width:auto}
.xc-target{display:none;flex:none;align-items:center;gap:8px;padding:4px 10px;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle)} .xc-target.show{display:flex} .xc-target .select{width:min(260px,100%);height:24px;font-size:11px;padding:0 24px 0 6px;background-position:calc(100% - 13px) 10px,calc(100% - 9px) 10px;background-size:4px 4px}
.xc-drawer{display:none;flex:none;border-bottom:1px solid var(--border-subtle)} .xc-drawer.open{display:block}
.xc-screen{background:#0c0c0c;min-height:0;flex:1;display:grid;grid-template-rows:minmax(0,1fr) auto;position:relative}
.xt-wrap{position:relative;min-height:0}
.terminal.xt{position:absolute;inset:0;background:#0c0c0c;border:0;border-radius:0;font-size:var(--xfs);line-height:1.35;color:var(--xfg);padding:8px 12px;white-space:pre-wrap}
.xt .w{color:#e6d07a} .xt .cursor{background:var(--xfg);width:.6em}
.xt-over{display:none;position:absolute;inset:0;background:rgba(0,0,0,.4);place-items:center;color:#e0e0e0;font-size:13px;pointer-events:none;z-index:3} .xc.typing-off .xt-over{display:grid}
.xprompt{display:flex;align-items:center;gap:8px;padding:6px 12px 8px;font-family:var(--font-mono);font-size:var(--xfs);background:#0c0c0c;border-top:1px solid #1a1a1a;color:var(--xfg);white-space:nowrap;min-width:0}
.xprompt .psh{flex:none;max-width:55%;overflow:hidden;text-overflow:ellipsis}
.xprompt .psh .u{color:#16c60c} .xprompt .psh .p{color:#3b78ff} .xprompt .psh .m{color:#9aa0a6}
.xin{position:relative;flex:1;min-width:0;height:1.5em}
.xin input{position:absolute;inset:0;width:100%;background:none;border:0;outline:0;padding:0;font:inherit;color:var(--xfg);caret-color:var(--xfg)}
.xin .ghost{position:absolute;top:0;line-height:1.5em;color:#e6d07a;opacity:.7;pointer-events:none;white-space:pre}
.xc.typing-off .xin input{opacity:.4}
.xt .u{color:#16c60c} .xt .p{color:#3b78ff} .xt .e{color:#ff6b6b} .xt .m{color:#7d8590} .xt .i{color:#3a96dd} .xt .c{color:#b48bff}
.suggest-picker{display:none;position:absolute;left:12px;bottom:6px;z-index:4;min-width:min(520px,calc(100% - 24px));max-width:calc(100% - 24px);background:#141414;border:1px solid #3a3a3a;border-radius:6px;box-shadow:0 10px 30px rgba(0,0,0,.7);overflow:hidden;font-family:var(--font-mono);font-size:12px}
.suggest-picker.show{display:block}
.picker-row{display:flex;align-items:center;gap:12px;padding:5px 10px;color:#d4d4d4;cursor:pointer;min-width:0} .picker-row:hover{background:#1e1e1e}
.picker-row.active{background:#2a2618} .picker-row.active .picker-cmd{color:#e6d07a}
.picker-cmd{flex:none;max-width:60%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap} .picker-desc{flex:1;min-width:0;color:#a0a0a0;font-family:var(--font-ui);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.scope-badge{flex:none;font-family:var(--font-ui);font-size:10px;padding:0 6px;border-radius:4px;border:1px solid rgba(76,141,255,.4);color:#4c8dff;background:rgba(76,141,255,.12)}
.scope-badge.current{border-color:rgba(92,255,176,.45);color:#5cffb0;background:rgba(92,255,176,.12)} .scope-badge.other{border-color:rgba(255,255,255,.12);color:#9aa0a6;background:rgba(255,255,255,.04)}
.suggest-bar{display:flex;align-items:center;gap:10px;padding:4px 10px;background:rgba(20,20,20,.92);border-top:1px solid #333;color:#9aa0a6;font-size:11px;font-family:var(--font-ui)}
.suggest-bar .dict-warn{display:none;color:#ffb74d;margin-left:auto} .suggest-bar .dict-warn.show{display:inline}
.dict-warn-solo{display:none;position:absolute;right:14px;bottom:8px;z-index:4;padding:2px 8px;border-radius:4px;background:#2a1d08;color:#ffb74d;border:1px solid rgba(255,183,77,.4);font-size:11px;font-family:var(--font-ui)} .dict-warn-solo.show{display:block}
.xtabs{display:flex;align-items:flex-end;gap:2px;padding:6px 8px 0;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);overflow-x:auto}
.xtabs .tab{height:32px;font-size:12px;padding:0 6px 0 12px;border:1px solid transparent;border-bottom:0;border-radius:8px 8px 0 0;gap:7px;color:var(--text-muted)}
.xtabs .tab.active{background:#0c0c0c;border-color:var(--border-subtle);color:var(--text-primary);box-shadow:inset 0 2px 0 var(--accent)}
.xtabs .tab .x{width:18px;height:18px;border-radius:4px;display:grid;place-items:center;color:var(--text-muted)} .xtabs .tab .x:hover{background:var(--surface-pressed);color:var(--text-primary)} .xtabs .tab .x .ic{width:11px;height:11px}
.xtabs .tab .badge{height:16px;font-size:9px;padding:0 5px}
.xtabs .add{width:30px;height:30px;margin-bottom:1px;border:0;background:none;color:var(--text-muted);border-radius:6px;display:grid;place-items:center;flex:none;cursor:pointer} .xtabs .add:hover{background:var(--surface-hover);color:var(--text-primary)}
.xtabs .sp{flex:1} .xtabs .chip{margin-bottom:3px;height:24px;font-size:11px;flex:none}
.xset{display:grid;grid-template-rows:auto minmax(0,1fr);height:100%;min-height:0}
.xpanels{min-height:0;position:relative} .xpanels>.tabpanel{height:100%} .xpanels>.tabpanel.active{display:block;animation:none}
`;

  const SCPCSS = `
.scp{background:var(--surface-card);padding:6px 8px;font-size:11px;min-width:0}
.scp-h{display:flex;align-items:center;gap:8px;margin-bottom:6px;min-width:0} .scp-h b{font-size:11px;font-weight:600;white-space:nowrap} .scp-h .hint{font-size:10.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis} .scp-h .btn{height:22px;font-size:11px;padding:0 8px;min-width:0;border-radius:4px}
.scp-h .btn-group{margin-left:auto;flex:none} .scp-ib{width:24px;height:22px;border:0;background:none;border-radius:4px;color:var(--text-muted);display:grid;place-items:center;cursor:pointer;flex:none} .scp-ib:hover{background:var(--surface-hover);color:var(--text-primary)} .scp-ib .ic{width:13px;height:13px}
.scp-chips{background:var(--surface-raised);border:1px solid var(--border-subtle);border-radius:6px;padding:4px;margin-bottom:6px;min-height:28px;max-height:190px;overflow:auto}
.scp-row{display:grid;grid-template-columns:auto minmax(70px,120px) minmax(0,1fr) auto;align-items:center;gap:6px;min-height:24px;padding:1px 0;border-radius:4px} .scp-row:hover{background:var(--surface-hover)}
.scp-row .t{font-weight:600;font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap} .scp-row .c{font:10.5px var(--font-mono);color:var(--text-muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.scp-ab{display:inline-flex;gap:2px;align-items:center}
.sib{width:26px;height:22px;border:1px solid transparent;background:none;border-radius:4px;color:var(--text-secondary);display:inline-grid;place-items:center;cursor:pointer;flex:none} .sib:hover{background:var(--surface-hover);border-color:var(--border-strong);color:var(--text-primary)} .sib .ic{width:13px;height:13px}
.sib.run{color:var(--success)} .sib.run:hover{background:var(--success-surface);border-color:rgba(61,220,132,.4)} .sib.del{color:var(--error)} .sib.del:hover{background:var(--error-surface);border-color:rgba(255,92,106,.4)} .sib.fav{color:var(--warning)} .sib.fav .ic{fill:currentColor}
.scp-empty{display:none;color:var(--text-muted);font-size:10.5px;padding:4px 6px} .scp.nofav .scp-empty{display:block}
.scp-more{display:flex;align-items:center;gap:6px;margin:6px 2px 2px} .scp-more>span{color:var(--text-muted);font-size:10.5px;margin:0 2px 0 4px} .scp-more .select{flex:1;min-width:0;height:24px;font-size:11px;padding:0 24px 0 6px;background-position:calc(100% - 13px) 10px,calc(100% - 9px) 10px;background-size:4px 4px}
.scp-add{display:grid;grid-template-columns:minmax(70px,110px) minmax(0,1fr) auto;gap:4px;align-items:center} .scp-add .input{height:24px;font-size:11px;padding:0 6px} .scp-add .btn.compact{height:24px;font-size:11px;padding:0 10px;min-width:0;border-radius:4px}
`;

  const MMBCSS = `
.mmb{--k:var(--info);--ks:var(--info-surface);position:relative;width:100%;max-width:520px;min-width:0;background:var(--surface-raised);border:1px solid var(--border-subtle);border-radius:16px;overflow:hidden;box-shadow:0 10px 40px rgba(0,0,0,.5);text-align:left}
.mmb::before{content:"";position:absolute;left:0;top:0;bottom:0;width:4px;background:var(--k)}
.mmb.success{--k:var(--success);--ks:var(--success-surface)} .mmb.warning{--k:var(--warning);--ks:var(--warning-surface)} .mmb.error{--k:var(--error);--ks:var(--error-surface)}
.mmb-in{padding:20px 20px 18px 24px}
.mmb-h{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:14px;margin-bottom:16px;align-items:start}
.mmb-ib{width:44px;height:44px;border-radius:12px;background:var(--ks);color:var(--k);display:grid;place-items:center} .mmb-ib .ic{width:22px;height:22px}
.mmb-k{font-size:11px;font-weight:600;color:var(--k);margin:1px 0 4px} .mmb-t{font-size:17px;font-weight:600;color:var(--text-primary)}
.mmb-x{width:28px;height:28px;border:0;background:none;color:var(--text-muted);border-radius:6px;display:grid;place-items:center;cursor:pointer} .mmb-x:hover{background:var(--surface-hover);color:var(--text-primary)} .mmb-x .ic{width:14px;height:14px}
.mmb-m{font-size:13.5px;line-height:21px;color:var(--text-secondary);margin-bottom:8px;overflow-wrap:anywhere} .mmb-m code{background:var(--surface-input);padding:1px 5px;border-radius:4px;font-size:12px;color:var(--text-primary)}
.mmb-sep{height:1px;background:var(--border-subtle);opacity:.85;margin:14px 0}
.mmb-f{display:flex;justify-content:flex-end;gap:8px} .mmb-f .btn{min-width:80px;border-radius:8px}
.mmb.error .btn.ok{background:var(--error);border-color:var(--error)}
.scrim>.mmb{width:min(440px,100%)}
`;

  // ---------------------------------------------------------------- ModernMessageBox markup
  const KIND = { info: ['info', 'Info'], success: ['check-circle', 'Success'], warning: ['alert', 'Warning'], error: ['x-circle', 'Error'], question: ['info', 'Confirm'] };
  // buttons: 'ok' | 'okcancel' | 'yesno' | 'yesnocancel'
  const mmb = (kind, title, msg, buttons = 'ok', { yes = 'Yes', closeAttr = 'data-close' } = {}) => {
    const cls = kind === 'question' || kind === 'info' ? '' : kind;
    const [icon, label] = KIND[kind];
    const B = {
      ok: `<button class="btn primary ok" ${closeAttr}>OK</button>`,
      okcancel: `<button class="btn" ${closeAttr}>Cancel</button><button class="btn primary ok" ${closeAttr}>OK</button>`,
      yesno: `<button class="btn" ${closeAttr}>No</button><button class="btn primary ok" ${closeAttr}>${yes}</button>`,
      yesnocancel: `<button class="btn" ${closeAttr}>No</button><button class="btn ghost" ${closeAttr}>Cancel</button><button class="btn primary ok" ${closeAttr}>${yes}</button>`,
    }[buttons];
    return `<div class="mmb ${cls}" role="dialog"><div class="mmb-in"><div class="mmb-h"><span class="mmb-ib">${ic(icon)}</span><div><div class="mmb-k">${label}</div><div class="mmb-t">${title}</div></div>${buttons === 'ok' ? `<button class="mmb-x" ${closeAttr} title="Close">${ic('x')}</button>` : '<span></span>'}</div><div class="mmb-m">${msg}</div><div class="mmb-sep"></div><div class="mmb-f">${B}</div></div></div>`;
  };
  const mmbScrim = (id, ...a) => `<div class="scrim" id="${id}">${mmb(...a)}</div>`;

  // ---------------------------------------------------------------- SavedCommandsPanel markup
  const FAVS = [
    ['Pull latest', 'git pull origin main'],
    ['Build assets', 'npm run build'],
    ['Clear cache', 'php artisan optimize:clear'],
    ['Restart queue', 'php artisan queue:restart'],
    ['Tail laravel log', 'tail -n 100 storage/logs/laravel.log'],
    ['Composer (prod)', 'composer install --no-dev --optimize-autoloader'],
  ];
  const MORE = [
    ['Migrate (force)', 'php artisan migrate --force'],
    ['Git stash + pull', 'git stash && git pull --rebase'],
    ['Disk usage', 'df -h /var/www'],
    ['Reload php-fpm', 'sudo systemctl reload php8.3-fpm'],
  ];
  const scp = ({ favs = FAVS, more = MORE, mode = 'Dock', close = true } = {}) => `<div class="scp ${favs.length ? '' : 'nofav'}">
<div class="scp-h"><b>Saved commands</b><span class="hint muted">Favorites only · pin with ★</span>
<div class="btn-group" data-single><button type="button" class="btn ${mode === 'Dock' ? 'active' : ''}" title="Open as a strip above the terminal">Dock</button><button type="button" class="btn ${mode === 'Float' ? 'active' : ''}" title="Open as a helper window (terminal stays usable)">Float</button></div>
${close ? `<button class="scp-ib" title="Close">${ic('x')}</button>` : ''}</div>
<div class="scp-chips">
${favs.map(([t, c]) => `<div class="scp-row"><button class="sib fav" title="Remove from favorites" data-toast="info" data-title="Removed from favorites" data-msg="${L.esc(t)}">${ic('star')}</button><span class="t" title="${L.esc(t)}">${t}</span><span class="c" title="${L.esc(c)}">${L.esc(c)}</span><span class="scp-ab"><button class="sib run" title="Run in terminal (Enter)" data-run data-cmd="${L.esc(c)}">${ic('play')}</button><button class="sib" title="Insert into terminal (no Enter)" data-insert data-cmd="${L.esc(c)}">${ic('arrow-down')}</button><button class="sib" title="Edit command" data-edit data-t="${L.esc(t)}" data-c="${L.esc(c)}">${ic('edit')}</button><button class="sib del" title="Delete command" data-confirm="Delete command &ldquo;${L.esc(t)}&rdquo;?">${ic('trash')}</button></span></div>`).join('')}
<div class="scp-empty">No favorites yet. Pick one below and press ★.</div>
${more.length ? `<div class="scp-more"><span>All</span><select class="select" title="Other saved commands (not favorited)">${more.map(([t, c]) => `<option value="${L.esc(c)}" data-t="${L.esc(t)}">${t}</option>`).join('')}</select>
<button class="sib fav" title="Add to favorites list" data-toast="success" data-title="Added to favorites">${ic('star')}</button><button class="sib run" title="Run in terminal (Enter)" data-run data-from="select">${ic('play')}</button><button class="sib" title="Insert into terminal (no Enter)" data-insert data-from="select">${ic('arrow-down')}</button><button class="sib" title="Edit command" data-edit data-from="select">${ic('edit')}</button><button class="sib del" title="Delete selected" data-confirm="Delete the selected saved command?">${ic('trash')}</button></div>` : ''}
</div>
<div class="scp-add"><input class="input scp-title" placeholder="Title" title="Preset title"><input class="input mono scp-cmd" placeholder="Command" title="Command to save / send"><span class="scp-ab"><button class="btn secondary compact" title="Save command preset" data-scp-save>Save</button><button class="sib run" title="Run box text in terminal" data-run data-from="box">${ic('play')}</button><button class="sib" title="Insert box text (no Enter)" data-insert data-from="box">${ic('arrow-down')}</button></span></div>
</div>`;

  // ---------------------------------------------------------------- terminal control markup
  const SUG_LOCAL = [
    ['git status', 'Show working tree status', 'global'], ['git pull origin main', 'Fetch and merge origin/main', 'current'], ['git push origin HEAD', 'Push current branch', 'current'],
    ['git log --oneline -10', 'Last 10 commits', 'global'], ['git stash', 'Stash local changes', 'global'], ['git checkout -b ', 'Create a new branch', 'global'], ['git diff --stat', 'Changed files summary', 'global'],
    ['npm run build', 'vite build (package.json)', 'current'], ['npm run dev', 'vite dev server', 'current'], ['npm install', 'Install node dependencies', 'global'],
    ['composer install', 'Install PHP dependencies', 'global'], ['php artisan migrate', 'Run database migrations', 'current'], ['php artisan optimize:clear', 'Clear all caches', 'current'], ['php artisan queue:restart', 'Restart queue workers', 'other'],
    ['tail -n 100 storage/logs/laravel.log', 'Last 100 log lines', 'other'],
  ];
  const SUG_SSH = SUG_LOCAL.concat([['sudo systemctl restart nginx', 'Restart nginx', 'other'], ['df -h /var/www', 'Disk usage', 'global'], ['htop', 'Interactive process viewer', 'global']]);
  const PS_LOCAL = '<span class="m">PS</span> <span class="p">C:\\laragon\\www\\myproject</span><span class="m">&gt;</span>';
  const PS_SSH = '<span class="u">deploy@prod-web-01</span><span class="m">:</span><span class="p">~/app</span><span class="m">$</span>';
  const PS_STG = '<span class="u">deploy@staging-api</span><span class="m">:</span><span class="p">~</span><span class="m">$</span>';
  const ln = (h) => `<div>${h}</div>`;
  const streamed = (pre, lines, interval) => L.stream(lines, { cls: 'terminal xt', interval }).replace('<span class="cursor"></span>', pre + '<span class="cursor"></span>');
  const sel = (title, opts, extra = '') => `<select class="select xs" title="${title}" ${extra}>${opts}</select>`;
  const FONTS = [10, 12, 14, 16, 18, 20].map((n) => `<option ${n === 14 ? 'selected' : ''}>${n}</option>`).join('');
  const COLORS = [['Gray', '#D4D4D4'], ['White', '#FFFFFF'], ['Green', '#00FF00'], ['Cyan', '#00FFFF'], ['Yellow', '#FFFF00']].map(([n, v]) => `<option value="${v}">${n}</option>`).join('');

  // o: {status, dot, stCls, badge, busy:{cmd,sec}, ps, shell, pre, lines, interval, sugg, init, drawer, target, typingOff, id}
  const xc = (o) => `<div ${o.id ? `id="${o.id}"` : ''} class="xc ${o.busy ? 'busy' : ''} ${o.typingOff ? 'typing-off' : ''}" data-sugg='${JSON.stringify(o.sugg || []).replace(/'/g, '&#39;')}' data-psh='${o.ps}' data-shell="${o.shell || 'ps'}" data-sec="${o.busy ? o.busy.sec : 0}">
<div class="xc-bar"><div class="st ${o.stCls || ''}"><span class="dot ${o.dot || ''}"></span><span class="stt">${o.status}</span>${o.badge || ''}</div>
<span class="xc-run" title="Command is running">${ic('spinner', 'spin')}<code class="rc">${o.busy ? o.busy.cmd : ''}</code><span class="rt mono">${o.busy ? '00:' + String(o.busy.sec).padStart(2, '0') : ''}</span></span>
<div class="xc-tools">
<button class="xt-btn ${o.drawer ? 'on' : ''}" data-xt="presets" title="Saved commands (Dock strip or Float helper window)">${ic('bookmark')}</button>
<a class="xt-btn" href="terminal-window.html" title="Float: open another terminal window">${ic('external')}</a>
<button class="xt-btn" data-xt="clear" title="Clear terminal screen">${ic('trash')}</button>
<button class="xt-btn ${o.typingOff ? '' : 'on'}" data-xt="type" title="Focus input (toggle typing)">${ic('edit')}</button>
${sel('Terminal font size', FONTS, 'data-xt-font')}${sel('Terminal color theme', COLORS, 'data-xt-color')}
</div></div>
<div class="xc-target ${o.target ? 'show' : ''}"><select class="select" title="Terminal connection target">${o.target || '<option>Local · project root</option>'}</select></div>
<div class="xc-drawer ${o.drawer ? 'open' : ''}">${scp({ mode: 'Dock' })}</div>
<div class="xc-screen"><div class="xt-wrap">${streamed(o.pre || '', o.lines || [['m', '']], o.interval || 1000)}
<div class="suggest-picker"><div class="picker-list"></div><div class="suggest-bar"><span>Tab accept · ↑↓ cycle · → accept</span><span class="scope-badge sb-scope">global</span><span class="sb-desc"></span><span class="dict-warn">Unknown command</span></div></div>
<div class="dict-warn-solo">Unknown command</div>
<div class="xt-over">Typing disabled</div></div>
<div class="xprompt"><span class="psh">${o.ps}</span><div class="xin"><input spellcheck="false" autocomplete="off" value="${L.esc(o.init || '')}" ${o.typingOff ? 'disabled' : ''} title="Terminal input"><span class="ghost"></span></div></div></div>
</div>`;

  const TABX = `<span class="x" title="Close session">${ic('x')}</span>`;
  const tab = (id, name, kind, active, dot = 'ok') => `<button type="button" class="tab ${active ? 'active' : ''}" data-tab="${id}" data-ctx="ctxTab" title="${name}"><span class="dot ${dot}"></span><span>${name}</span>${kind === 'ssh' ? '<span class="badge violet">SSH</span>' : '<span class="badge success">LOCAL</span>'}${TABX}</button>`;
  const xset = (tabs, panels, n = 4) => `<div class="xset" data-tabscope><div class="xtabs">${tabs}<button class="add" data-open="dlgNewSession" title="New terminal session…">${ic('plus')}</button><a class="add" href="project-local-terminal-window.html" title="New Local terminal">${ic('terminal')}</a><span class="sp"></span><span class="chip" title="Number of open terminals">${ic('terminal')}<b data-count>${n}</b> open</span></div><div class="xpanels">${panels}</div></div>`;

  const CTX_TAB = `<div class="ctx" id="ctxTab"><div class="menu"><div class="mi">${ic('edit')}Rename session</div><div class="mi">${ic('copy')}Duplicate</div><a class="mi" href="terminal-window.html" style="color:inherit">${ic('external')}Float in new window</a><div class="msep"></div><div class="mi">${ic('refresh')}Reconnect</div><div class="mi danger" data-confirm="Close this terminal session?">${ic('x')}Close session</div></div></div>`;
  const sub = (t) => `<span class="muted mono" style="font-size:11px">${t}</span>`;
  const DLG_NEW = `<div class="scrim" id="dlgNewSession"><div class="modal sm"><div class="modal-h">${ic('terminal')}<h3>New terminal session</h3></div><div class="modal-b stack sm">
<label class="lbl">Terminal connection target</label>
${L.chk('<span><b>Local</b> · project root<br>' + sub('C:\\laragon\\www\\myproject') + '</span>', true, 'radio', 'tgt')}
${L.chk('<span><b>prod-web-01</b> ' + ic('star', 'ic-sm warn') + '<br>' + sub('deploy@203.0.113.10:22') + '</span>', false, 'radio', 'tgt')}
${L.chk('<span><b>staging-api</b><br>' + sub('deploy@198.51.100.24:22') + '</span>', false, 'radio', 'tgt')}
${L.chk('<span><b>Project SSH</b> · myproject<br>' + sub('from project FTP/SFTP target') + '</span>', false, 'radio', 'tgt')}
</div><div class="modal-f"><button class="btn" data-close>Cancel</button><button class="btn primary" data-close data-toast="info" data-title="Connecting to prod-web-01..." data-msg="deploy@203.0.113.10:22">${ic('zap')}Connect</button></div></div></div>`;

  // runtime behaviour for every .xc (suggestions, run, clear, tools, tabs)
  const TSCRIPT = `<script>(function(){
var KNOWN='cd ls dir cat echo git npm npx node php composer artisan tail grep clear cls pwd ssh rm cp mv mkdir touch sudo systemctl df du ps top htop curl wget vim nano go docker yarn pnpm'.split(' ');
function $$(s,r){return Array.prototype.slice.call((r||document).querySelectorAll(s));}
function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;');}
function pad(n){return ('0'+n).slice(-2);}
$$('.xc').forEach(function(xc){
 var inp=xc.querySelector('.xin input'),ghost=xc.querySelector('.ghost'),pick=xc.querySelector('.suggest-picker'),list=xc.querySelector('.picker-list'),term=xc.querySelector('.xt'),solo=xc.querySelector('.dict-warn-solo'),bw=xc.querySelector('.suggest-bar .dict-warn');
 var sugg=[];try{sugg=JSON.parse(xc.dataset.sugg||'[]');}catch(e){}
 var idx=0,m=[],hist=[],hi=-1,tm=null;
 function out(h){var d=document.createElement('div');d.innerHTML=h;var c=term.querySelector('.cursor');if(c&&c.parentNode===term)term.insertBefore(d,c);else term.appendChild(d);term.scrollTop=term.scrollHeight;}
 function render(){
  var v=inp.value,lv=v.toLowerCase();
  m=v?sugg.filter(function(s){return s[0].toLowerCase().indexOf(lv)===0&&s[0]!==v;}).concat(sugg.filter(function(s){return s[0].toLowerCase().indexOf(lv)>0;})):[];
  if(idx>=m.length)idx=0;
  var unk=false;if(v.trim()){var w=v.trim().split(' ')[0].toLowerCase();unk=KNOWN.indexOf(w)<0&&!sugg.some(function(s){return s[0].toLowerCase().indexOf(w)===0;});}
  list.innerHTML=m.slice(0,6).map(function(s,i){return '<div class="picker-row'+(i===idx?' active':'')+'" data-i="'+i+'"><span class="picker-cmd">'+esc(s[0])+'</span><span class="picker-desc">'+esc(s[1])+'</span><span class="scope-badge '+s[2]+'">'+s[2]+'</span></div>';}).join('');
  pick.classList.toggle('show',m.length>0&&!inp.disabled);
  if(m[idx]){var b=xc.querySelector('.sb-scope');b.textContent=m[idx][2];b.className='scope-badge sb-scope '+m[idx][2];xc.querySelector('.sb-desc').textContent=m[idx][1];}
  ghost.textContent=(m[idx]&&m[idx][0].toLowerCase().indexOf(lv)===0)?m[idx][0].slice(v.length):'';
  ghost.style.left=v.length+'ch';
  bw.classList.toggle('show',unk&&m.length>0);solo.classList.toggle('show',unk&&!m.length);
 }
 function accept(){if(m[idx]){inp.value=m[idx][0];idx=0;render();}}
 function clear(){Array.prototype.slice.call(term.children).forEach(function(c){if(!c.classList.contains('cursor'))term.removeChild(c);});}
 function reply(c){
  var sh=xc.dataset.shell,w=c.split(' ')[0];
  if(c==='git status')return '<span class="m">On branch</span> feature/deploy-queue<br><span class="m">Changes not staged for commit:</span><br><span class="w">  modified:   app/Http/Controllers/DeployController.php</span><br><span class="u">  new file:   app/Jobs/UploadChunk.php</span>';
  if(c.indexOf('git pull')===0)return 'From github.com:acme/myproject<br> * branch            main       -> FETCH_HEAD<br>Updating 3d93978..2c3e767<br>Fast-forward<br> app/Services/FtpUploader.php | 24 <span class="u">++++++++++++</span><span class="e">---</span><br> 1 file changed, 18 insertions(+), 6 deletions(-)';
  if(w==='ls'||w==='dir')return 'app  bootstrap  config  <span class="p">database</span>  public  resources  routes  storage  vendor  artisan  composer.json  package.json';
  if(KNOWN.indexOf(w)<0)return sh==='ps'?'<span class="e">'+esc(w)+' : The term \\''+esc(w)+'\\' is not recognized as the name of a cmdlet, function, script file, or operable program.</span>':'<span class="e">bash: '+esc(w)+': command not found</span>';
  return '<span class="u">&#10003;</span> <span class="m">'+esc(c)+' finished (exit code 0)</span>';
 }
 function run(c){c=(c||'').trim();if(!c)return;hist.unshift(c);hi=-1;out(xc.dataset.psh+' '+esc(c));inp.value='';idx=0;render();
  if(c==='clear'||c==='cls'){clear();return;}
  xc.classList.add('busy');xc.dataset.sec=0;xc.querySelector('.rc').textContent=c;xc.querySelector('.rt').textContent='00:00';
  clearTimeout(tm);tm=setTimeout(function(){out(reply(c));xc.classList.remove('busy');},1400);}
 xc._run=run;xc._clear=clear;xc._insert=function(c){inp.value=c;inp.focus();render();};
 inp.addEventListener('input',function(){idx=0;render();});
 inp.addEventListener('keydown',function(e){
  if(e.key==='Enter'){e.preventDefault();run(inp.value);}
  else if(e.key==='Tab'){if(m.length){e.preventDefault();accept();}}
  else if(e.key==='ArrowRight'&&inp.selectionStart===inp.value.length&&m.length){e.preventDefault();accept();}
  else if(e.key==='ArrowDown'&&m.length){e.preventDefault();idx=(idx+1)%Math.min(m.length,6);render();}
  else if(e.key==='ArrowUp'){e.preventDefault();if(m.length){idx=(idx+Math.min(m.length,6)-1)%Math.min(m.length,6);render();}else if(hist.length){hi=Math.min(hi+1,hist.length-1);inp.value=hist[hi];render();}}
  else if(e.key==='Escape'){pick.classList.remove('show');}
 });
 list.addEventListener('mousedown',function(e){var r=e.target.closest('.picker-row');if(r){e.preventDefault();idx=+r.dataset.i;accept();inp.focus();}});
 xc.querySelector('.xt-wrap').addEventListener('click',function(){inp.focus();});
 render();
});
document.addEventListener('click',function(e){
 var t=e.target.closest('[data-xt]');
 if(t){var xc=t.closest('.xc'),a=t.dataset.xt;
  if(a==='presets'){xc.querySelector('.xc-drawer').classList.toggle('open');t.classList.toggle('on');}
  if(a==='clear'&&xc._clear)xc._clear();
  if(a==='type'){var on=t.classList.toggle('on');xc.classList.toggle('typing-off',!on);var i=xc.querySelector('.xin input');i.disabled=!on;if(on)i.focus();}
 }
 var r=e.target.closest('[data-run],[data-insert]');
 if(r){var scp=r.closest('.scp'),c=r.dataset.cmd;
  if(c===undefined&&scp){if(r.dataset.from==='select')c=scp.querySelector('.scp-more select').value;else c=scp.querySelector('.scp-cmd').value;}
  var host=r.closest('.xc')||document.querySelector(r.dataset.target||'#demoXc');
  if(!c){var mm=document.getElementById('mbPresetReq');if(mm)mm.classList.add('open');return;}
  if(host&&r.hasAttribute('data-run')&&host._run)host._run(c);
  else if(host&&host._insert)host._insert(c);
  if(r.hasAttribute('data-run')&&window.toast)toast({type:'info',title:'Run in terminal',msg:c,duration:1800});
 }
 var ed=e.target.closest('[data-edit]');
 if(ed){var s=ed.closest('.scp'),t1=ed.dataset.t,c1=ed.dataset.c;if(ed.dataset.from==='select'){var o=s.querySelector('.scp-more select');t1=o.options[o.selectedIndex].dataset.t;c1=o.value;}
  s.querySelector('.scp-title').value=t1;s.querySelector('.scp-cmd').value=c1;s.querySelector('.scp-cmd').focus();}
 var sv=e.target.closest('[data-scp-save]');
 if(sv){var s2=sv.closest('.scp');if(!s2.querySelector('.scp-title').value.trim()||!s2.querySelector('.scp-cmd').value.trim()){var mm2=document.getElementById('mbPresetReq');if(mm2)mm2.classList.add('open');}else if(window.toast)toast({type:'success',title:'Preset saved',msg:s2.querySelector('.scp-title').value});}
 var x=e.target.closest('.xtabs .tab .x');
 if(x){e.stopImmediatePropagation();var tb=x.closest('.tab'),p=document.getElementById(tb.dataset.tab),bar=tb.parentNode,was=tb.classList.contains('active');tb.remove();if(p)p.remove();
  var cnt=document.querySelector('[data-count]');if(cnt)cnt.textContent=document.querySelectorAll('.xtabs .tab').length;
  if(was){var f=bar.querySelector('.tab');if(f)f.click();}}
},true);
document.addEventListener('change',function(e){
 var f=e.target.closest('[data-xt-font]');if(f)f.closest('.xc').style.setProperty('--xfs',f.value+'px');
 var c=e.target.closest('[data-xt-color]');if(c)c.closest('.xc').style.setProperty('--xfg',c.value);
});
setInterval(function(){$$('.xc.busy').forEach(function(xc){var s=(+xc.dataset.sec||0)+1;xc.dataset.sec=s;xc.querySelector('.rt').textContent=pad(Math.floor(s/60))+':'+pad(s%60);});},1000);
})();</script>`;

  // ---------------------------------------------------------------- terminal sessions (shared by control page + window)
  const P_LOCAL = () => xc({
    status: 'Local shell · C:\\laragon\\www\\myproject', dot: 'ok', stCls: 'ok', badge: '<span class="badge success" title="Local PowerShell session">LOCAL</span>', ps: PS_LOCAL, shell: 'ps',
    busy: { cmd: 'npm run build', sec: 14 }, sugg: SUG_LOCAL, init: '',
    pre: ln('<span class="m">Windows PowerShell</span>') + ln('<span class="m">Copyright (C) Microsoft Corporation. All rights reserved.</span>') + ln('&nbsp;') +
      ln(PS_LOCAL + ' git status') + ln('On branch <span class="u">feature/deploy-queue</span>') + ln('Your branch is ahead of <span class="e">\'origin/feature/deploy-queue\'</span> by 2 commits.') + ln('&nbsp;') +
      ln('<span class="m">Changes not staged for commit:</span>') + ln('<span class="w">  modified:   app/Http/Controllers/DeployController.php</span>') + ln('<span class="w">  modified:   resources/js/deploy.js</span>') + ln('<span class="u">  new file:   app/Jobs/UploadChunk.php</span>') + ln('&nbsp;') +
      ln(PS_LOCAL + ' npm run build') + ln('<span class="m">&gt; myproject@2.4.0 build</span>') + ln('<span class="m">&gt; vite build</span>') + ln('<span class="i">vite v5.4.2</span> building for production...'),
    lines: [['m', 'transforming (118/184) resources/js/deploy.js'], ['m', 'transforming (184/184) resources/css/app.css'], ['ok', '✓ 184 modules transformed.'], ['i', 'rendering chunks...'], ['m', 'public/build/assets/app-3f9a1c.css   48.12 kB │ gzip:   9.31 kB'], ['m', 'public/build/assets/app-b71e04.js   312.77 kB │ gzip: 101.40 kB'], ['ok', '✓ built in 6.42s']], interval: 1100,
  });
  const P_PROD = (drawer = false, id = '') => xc({
    id, status: 'Connected to prod-web-01', dot: 'live', stCls: 'ok', badge: '<span class="badge violet" title="SSH session">' + ic('lock', 'ic-sm') + 'SSH</span><span class="mono muted">deploy@203.0.113.10:22</span>', ps: PS_SSH, shell: 'bash',
    busy: { cmd: 'tail -f storage/logs/laravel.log', sec: 47 }, sugg: SUG_SSH, init: 'git p', drawer,
    pre: ln('<span class="m">Linux prod-web-01 5.15.0-122-generic x86_64 · Ubuntu 22.04.5 LTS</span>') + ln('<span class="m">Last login: Mon Nov  2 00:36:08 2026 from 85.9.114.20</span>') + ln('&nbsp;') +
      ln(PS_SSH + ' git pull origin main') + ln('From github.com:acme/myproject') + ln(' * branch            main       -&gt; FETCH_HEAD') + ln('Updating 638d950..2c3e767') + ln('Fast-forward') + ln(' app/Services/FtpUploader.php | 24 <span class="u">++++++++++++</span><span class="e">---</span>') + ln(' 3 files changed, 41 insertions(+), 9 deletions(-)') +
      ln(PS_SSH + ' php artisan queue:restart') + ln('<span class="u">INFO</span>  Broadcasting queue restart signal.') + ln(PS_SSH + ' tail -f storage/logs/laravel.log'),
    lines: [['i', '[2026-11-02 00:41:12] production.INFO: Deploy #482 started {"files":12,"user":"sharif"}'], ['m', '[2026-11-02 00:41:13] production.DEBUG: SFTP put app/Services/FtpUploader.php (4.2 KB)'], ['m', '[2026-11-02 00:41:14] production.DEBUG: SFTP put resources/js/deploy.js (18.7 KB)'], ['w', '[2026-11-02 00:41:15] production.WARNING: Slow query 1.84s select * from deployments where status = ?'], ['ok', '[2026-11-02 00:41:17] production.INFO: Deploy #482 finished in 5.1s'], ['e', '[2026-11-02 00:41:44] production.ERROR: Connection refused [tcp://127.0.0.1:6379] (Predis\\Connection\\ConnectionException)']], interval: 1300,
  });
  const P_STG = () => xc({
    status: 'Connecting to staging-api...', dot: 'warn pulse', stCls: 'warn', badge: '<span class="badge violet">' + ic('lock', 'ic-sm') + 'SSH</span><span class="mono muted">deploy@198.51.100.24:22</span>', ps: PS_STG, shell: 'bash', typingOff: true,
    pre: ln('<span class="i">Connecting to staging-api (198.51.100.24:22)...</span>') + ln('<span class="m">Host key fingerprint SHA256:Qh1x7mZ0kq4W2dY9aT3vN8cLr5uE6bJfK1pXoVwDsAg (ed25519)</span>') + ln('<span class="m">Authenticating with key ~/.ssh/id_ed25519 ...</span>'),
    lines: [['m', 'Opening channel (session)...'], ['m', 'Requesting pty xterm-256color 120x32'], ['ok', 'Shell ready.']], interval: 2200,
  });
  const P_OLD = () => xc({
    status: 'Disconnected', dot: '', badge: '<span class="badge violet">SSH</span><span class="mono muted">root@db-old.internal:22</span>', ps: '<span class="u">root@db-old</span><span class="m">#</span>', shell: 'bash',
    target: '<option>db-old (root@db-old.internal:22)</option><option>Local · project root</option><option selected>prod-web-01 (deploy@203.0.113.10:22)</option><option>staging-api (deploy@198.51.100.24:22)</option>',
    pre: ln('<span class="u">root@db-old</span><span class="m">#</span> mysqldump --single-transaction shop &gt; /backup/shop.sql') + ln('<span class="m">-- Dumping data for table `orders`</span>') + ln('<span class="e">client_loop: send disconnect: Broken pipe</span>') + ln('<span class="e">Connection to db-old.internal closed by remote host.</span>') + ln('&nbsp;') + ln('<span class="m">[Session closed 00:38:51] Pick a target above to connect again.</span>'),
    lines: [['m', '']], interval: 3600000,
  });

  const sessionTabs = (act) => tab('s1', 'Local', 'local', act === 's1') + tab('s2', 'prod-web-01', 'ssh', act === 's2') + tab('s3', 'staging-api', 'ssh', act === 's3', 'warn pulse') + tab('s4', 'db-old', 'ssh', act === 's4', '');
  const sessionPanels = (act, drawer) => `<div class="tabpanel ${act === 's1' ? 'active' : ''}" id="s1">${P_LOCAL()}</div><div class="tabpanel ${act === 's2' ? 'active' : ''}" id="s2">${P_PROD(drawer)}</div><div class="tabpanel ${act === 's3' ? 'active' : ''}" id="s3">${P_STG()}</div><div class="tabpanel ${act === 's4' ? 'active' : ''}" id="s4">${P_OLD()}</div>`;

  // ================================================================ 1. TerminalControl
  L.page({
    slug: 'terminal-control', title: 'TerminalControl', nav: 'deploy', group: 'Controls', flush: true, css: XCSS + SCPCSS + MMBCSS + `.tcwrap{height:100%;min-height:0}`,
    body: `<div class="tcwrap">${xset(sessionTabs('s1'), sessionPanels('s1', false))}</div>${CTX_TAB}${TSCRIPT}`,
    dialogs: DLG_NEW + mmbScrim('mbPresetReq', 'warning', 'Command Presets', 'Please enter both a title and a command.'),
  });

  // ================================================================ 2. TerminalWindow
  const tw = `<div class="tw">
<div class="tw-ssh"><span class="sec">SSH Connection:</span><select class="select" style="width:250px;height:28px"><option selected>prod-web-01 (deploy@203.0.113.10:22)</option><option>staging-api (deploy@198.51.100.24:22)</option><option>db-old (root@db-old.internal:22)</option></select>
<button class="btn primary" style="height:28px;padding:0 10px" data-working="Connecting..." data-done="Connected to prod-web-01">${ic('zap')}<span class="bl">Quick Connect</span></button><a class="btn ghost" style="height:28px;padding:0 10px" href="connection-manager-window.html">${ic('settings')}<span class="bl">Manage Profiles</span></a></div>
<div class="tw-pre"><div class="row"><select class="select grow" style="height:34px" title="Saved command presets"><option>Pull latest — git pull origin main</option><option>Build assets — npm run build</option><option>Clear cache — php artisan optimize:clear</option><option>Restart queue — php artisan queue:restart</option></select>
<button class="btn icon tw-ib acc" title="Insert the selected preset into the terminal" data-insert data-cmd="git pull origin main" data-target="#twXcInner">${ic('clipboard')}</button><button class="btn icon tw-ib dng" title="Delete the selected preset" data-confirm="Delete the selected preset?">${ic('trash')}</button>
<label class="check" title="Send the command to every open terminal"><input type="checkbox">Send to all</label></div>
<div class="tw-save"><input class="input" placeholder="Title" title="Preset title"><input class="input mono" placeholder="Command" title="Command to save as a preset"><button class="btn icon tw-ib acc" style="margin:0" title="Save this command as a preset" data-toast="success" data-title="Preset saved" data-msg="Title + command stored">${ic('save')}</button></div></div>
<div class="tw-term">${xset(tab('s1', 'prod-web-01', 'ssh', true) + tab('s2', 'Local', 'local', false), `<div class="tabpanel active" id="s1">${P_PROD(false, 'twXcInner')}</div><div class="tabpanel" id="s2">${P_LOCAL()}</div>`, 2)}</div></div>`;
  L.window({
    slug: 'terminal-window', title: 'Terminal', icon: 'terminal', w: 980, h: 700, group: 'Windows', flush: true,
    css: XCSS + SCPCSS + MMBCSS + `.tw{display:grid;grid-template-rows:auto auto minmax(0,1fr);height:100%;min-height:0}
.tw-ssh{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px;background:var(--surface-card);border-bottom:1px solid var(--border-subtle)}
.tw-pre{padding:10px;background:var(--surface-shell);border-bottom:1px solid var(--border-subtle);display:grid;gap:10px}
.tw-save{display:grid;grid-template-columns:170px minmax(0,1fr) auto;gap:8px} .tw-ib{width:36px;height:34px;min-width:36px;background:transparent;border-color:var(--border-subtle);color:var(--text-muted)} .tw-ib.acc{border-color:var(--accent);color:var(--accent)} .tw-ib.dng{border-color:var(--error);color:var(--error)}
.tw-term{min-height:0}`,
    body: tw + CTX_TAB + TSCRIPT,
    footer: `<span class="l">2 terminals open · broadcast off</span><button class="btn">Close</button>`,
    dialogs: DLG_NEW + mmbScrim('mbPresetReq', 'warning', 'Command Presets', 'Please enter both a title and a command.'),
  });

  // ================================================================ 3. ProjectLocalTerminalWindow
  L.window({
    slug: 'project-local-terminal-window', title: 'Local Terminal — myproject', icon: 'terminal', w: 980, h: 620, group: 'Windows', flush: true,
    css: XCSS + SCPCSS + MMBCSS + `.pl{display:grid;grid-template-rows:auto minmax(0,1fr);height:100%;min-height:0}
.pl-h{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:center;gap:12px;padding:8px 12px;background:var(--surface-card);border-bottom:1px solid var(--border-subtle)}
.pl-h .b{display:flex;align-items:center;gap:10px;font-weight:600;font-size:12.5px} .pl-h .b i{width:10px;height:10px;border-radius:50%;background:#13a10e;box-shadow:0 0 0 0 rgba(19,161,14,.6);animation:ping 1.6s infinite}
.pl-h .path{font:12px Consolas,var(--font-mono);color:#16c60c;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}`,
    body: `<div class="pl"><div class="pl-h"><div class="b"><i></i>Local · project root</div><div class="path" title="C:\\laragon\\www\\myproject">C:\\laragon\\www\\myproject</div>
<button class="btn ghost compact" style="height:28px" title="Restart shell in project root" data-working="Restarting..." data-done="Shell restarted" data-done-msg="Local shell started in C:\\laragon\\www\\myproject">${ic('refresh')}<span class="bl">Restart</span></button></div>
${xc({
      status: 'Local shell · C:\\laragon\\www\\myproject', dot: 'ok', stCls: 'ok', badge: '<span class="badge success">LOCAL</span>', ps: PS_LOCAL, shell: 'ps', sugg: SUG_LOCAL, init: 'php artisan m', target: '<option selected>Local · project root</option><option>prod-web-01 (deploy@203.0.113.10:22)</option><option>staging-api (deploy@198.51.100.24:22)</option>',
      pre: ln('<span class="m">Starting local shell...</span>') + ln('<span class="m">PowerShell 7.4.5 · cwd C:\\laragon\\www\\myproject</span>') + ln('&nbsp;') + ln(PS_LOCAL + ' php -v') + ln('PHP 8.3.12 (cli) (built: Sep 24 2026) (ZTS Visual C++ 2019 x64)') + ln(PS_LOCAL + ' git log --oneline -5') + ln('<span class="w">2c3e767</span> deploy update 2026-11-02 00:39') + ln('<span class="w">638d950</span> deploy update 2026-11-02 00:21') + ln('<span class="w">2babf2f</span> deploy update 2026-11-02 00:15') + ln('<span class="w">ebca1af</span> deploy update 2026-11-02 00:10') + ln('<span class="w">3d93978</span> deploy update 2026-10-16 17:09'),
      lines: [['m', '']], interval: 3600000,
    })}</div>${TSCRIPT}`,
    footer: `<span class="l">Error states:</span><button class="btn compact" data-open="mbNoProj">No project</button><button class="btn compact" data-open="mbStartFail">Start failure</button><button class="btn">Close</button>`,
    dialogs: mmbScrim('mbNoProj', 'warning', 'Local Terminal', 'No project is open. Open a project first.') +
      mmbScrim('mbStartFail', 'error', 'Local Terminal', 'Could not start local shell: The system cannot find the file specified (pwsh.exe).') + mmbScrim('mbPresetReq', 'warning', 'Command Presets', 'Please enter both a title and a command.') + DLG_NEW,
  });

  // ================================================================ 4. SavedCommandsPanel
  L.page({
    slug: 'saved-commands-panel', title: 'SavedCommandsPanel', nav: 'deploy', group: 'Controls', css: XCSS + SCPCSS + MMBCSS + `.sc-demo{border:1px solid var(--border-subtle);border-radius:12px;overflow:hidden}.sc-demo .xc{height:260px}.sc-demo .xc-bar{display:none}`,
    body: `<div class="page-inner"><div class="page-head"><div><h1 class="page-title">Saved commands</h1><div class="page-sub">Docked strip above the terminal (or floated as a helper window). Favorites are pinned with the star, everything else lives in the “All” select. The play button runs with Enter, the arrow inserts without Enter.</div></div>
<div class="r"><a class="btn" href="saved-commands-window.html">${ic('external')}Float window</a><a class="btn" href="terminal-control.html">${ic('terminal')}Terminal control</a></div></div>
<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(min(100%,520px),1fr))">
<div class="stack sm"><div class="eyebrow">Favorites + “All” select (Dock mode)</div><div class="card flush">${scp({ mode: 'Dock' })}</div></div>
<div class="stack sm"><div class="eyebrow">Empty favorites (hint visible, Float mode)</div><div class="card flush">${scp({ favs: [], more: MORE.slice(0, 2), mode: 'Float' })}</div></div>
<div class="stack sm"><div class="eyebrow">Only favorites (no “All” row, no close button)</div><div class="card flush">${scp({ favs: FAVS.slice(0, 3), more: [], close: false })}</div></div>
<div class="stack sm"><div class="eyebrow">Fresh install (nothing saved)</div><div class="card flush">${scp({ favs: [], more: [] })}</div></div></div>
<div class="section-title" style="margin-top:20px">${ic('terminal')}Attached terminal <span class="badge accent">live</span></div>
<div class="sc-demo">${xc({ id: 'demoXc', status: 'demo', dot: 'ok', ps: PS_LOCAL, shell: 'ps', sugg: SUG_LOCAL, pre: ln(PS_LOCAL + ' <span class="m"># press play in any row above, or type here</span>'), lines: [['m', '']], interval: 3600000 })}</div></div>${TSCRIPT}`,
    dialogs: mmbScrim('mbPresetReq', 'warning', 'Command Presets', 'Please enter both a title and a command.'),
  });

  // ================================================================ 5. SavedCommandsWindow
  L.window({
    slug: 'saved-commands-window', title: 'Saved commands', icon: 'bookmark', w: 680, h: 380, group: 'Windows', flush: true, css: XCSS + SCPCSS + MMBCSS + `.win .scp{height:100%;padding:10px 12px;font-size:12px} .win .scp-chips{max-height:none}`,
    body: scp({ mode: 'Float', close: false }) + TSCRIPT,
    footer: `<span class="l">${ic('external', 'ic-sm')} Helper window · the terminal stays usable</span><button class="btn">Close</button>`,
    dialogs: mmbScrim('mbPresetReq', 'warning', 'Command Presets', 'Please enter both a title and a command.'),
  });

  // ================================================================ 6. TelegramTerminalCommandsPanel + 7. dialog
  const TT = [
    ['Restart service', 'sudo systemctl restart myproject', 'yes'],
    ['Go build + restart', 'cd /srv/excore && go build -o bin/excore ./cmd/excore && sudo systemctl res…', 'yes'],
    ['Clear cache', 'php artisan optimize:clear', 'no'],
    ['Queue status', 'php artisan queue:monitor default,emails --max=100', 'no'],
    ['Disk usage', 'df -h /var/www', 'no'],
  ];
  const ttForm = (idp) => `<div class="stack">
<div class="muted" style="font-size:11px">Stored for project: <span class="mono">C:\\laragon\\www\\myproject</span></div>
${L.field('Button label (description)', `<input class="input" id="${idp}L" value="Restart service">`)}
${L.field('Command (single line, trusted)', `<textarea class="input mono" id="${idp}C" rows="3" style="min-height:64px;max-height:120px">sudo systemctl restart myproject && sudo systemctl status myproject --no-pager -n 5</textarea>`)}
${L.chk('Send Telegram notice before running (recommended for restart)', true)}
${L.field('Notice text (optional)', `<input class="input" value="Restarting myproject on the server…">`)}</div>`;
  const TTSAVE = `<script>document.addEventListener('click',function(e){var b=e.target.closest('[data-tt-save]');if(!b)return;e.stopImmediatePropagation();var s=b.closest('.win,.shell'),l=s.querySelector('[id$=L]'),c=s.querySelector('[id$=C]');
var id=!c.value.trim()?'mbCmd':!l.value.trim()?'mbLbl':null;if(id){document.getElementById(id).classList.add('open');}else{var sc=b.closest('.scrim');if(sc)sc.classList.remove('open');if(window.toast)toast({type:'success',title:'Preset saved',msg:l.value});}},true);</script>`;
  const TTMB = mmbScrim('mbCmd', 'warning', 'Telegram terminal preset', 'Command is required.') + mmbScrim('mbLbl', 'warning', 'Telegram terminal preset', 'Button label is required.');
  const kbBtn = (t, a = '') => `<span class="kbb ${a}">${t}</span>`;
  const TGCSS = `.kbb{display:inline-flex;align-items:center;justify-content:center;min-height:30px;padding:4px 10px;border-radius:8px;background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.06);font-size:12px;text-align:center;flex:1 1 calc(50% - 4px);color:#e8eefb} .kbb.wide{flex-basis:100%}
.tgp{display:flex;flex-direction:column;gap:8px;background:#0a1220;border:1px solid var(--border-subtle);border-radius:12px;padding:12px;min-width:0} .tgp .bubble{max-width:92%;font-size:12.5px} .tgp .bubble.in{background:#17212f} .tgp pre{margin:6px 0 0;padding:6px 8px;background:#0c0c0c;border-radius:6px;font:11.5px var(--font-mono);color:#d4d4d4;white-space:pre-wrap}
.tgkb{display:flex;flex-wrap:wrap;gap:4px;max-width:92%}`;
  L.page({
    slug: 'telegram-terminal-commands-panel', title: 'TelegramTerminalCommandsPanel', nav: 'settings', group: 'Controls', css: MMBCSS + TGCSS,
    body: `<div class="page-inner"><div class="page-head"><div><h1 class="page-title">Telegram terminal presets</h1><div class="page-sub">Settings · Telegram — trusted one-line commands for the bot’s SSH terminal.</div></div><div class="r"><a class="btn" href="settings.html">${ic('settings')}Settings</a><a class="btn" href="telegram.html">${ic('chat')}Telegram</a></div></div>
<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(min(100%,520px),1fr));align-items:start">
<div class="stack">
${L.card('Telegram terminal presets', `<div class="muted" style="font-size:11px;margin-bottom:8px">Current project: myproject<br><span class="mono">C:\\laragon\\www\\myproject</span></div>
<div class="muted" style="font-size:10px;margin-bottom:12px">Trusted one-line commands shown as inline buttons while Telegram SSH terminal is open. Free-text lines still use the safety guard.</div>
<div class="row wrap" style="margin-bottom:10px"><button class="btn primary" style="height:30px;min-width:100px" data-open="dlgPreset">${ic('plus')}<span class="bl">Add preset</span></button><button class="btn ghost" style="height:30px;min-width:120px" data-open="dlgPreset">${ic('wand')}<span class="bl">Go build template</span></button></div>
<div class="grid-wrap" style="border:1px solid var(--border-subtle);border-radius:8px;min-height:120px;max-height:260px;background:var(--surface-card)"><table class="dg" data-select><thead><tr><th style="width:120px">Label</th><th style="width:260px">Command</th><th style="width:60px">Notify</th></tr></thead><tbody>${TT.map((r, i) => `<tr class="${i === 0 ? 'sel' : ''}"><td>${r[0]}</td><td class="mono">${L.esc(r[1])}</td><td>${r[2]}</td></tr>`).join('')}</tbody></table></div>
<div class="row" style="margin-top:10px"><button class="btn ghost compact" style="height:28px;min-width:72px" data-open="dlgPreset">Edit</button><button class="btn ghost compact" style="height:28px;min-width:72px" data-confirm="Delete the selected Telegram terminal preset?">Delete</button></div>`, { icon: 'terminal' })}
${L.card('Empty state — no project open', `<div class="muted" style="font-size:11px;margin-bottom:8px">Current project: (none) — open a project to manage presets.</div><div class="row wrap" style="margin-bottom:10px"><button class="btn primary disabled" style="height:30px;min-width:100px">Add preset</button><button class="btn ghost disabled" style="height:30px;min-width:120px">Go build template</button></div><div class="grid-wrap" style="border:1px solid var(--border-subtle);border-radius:8px;min-height:80px"><table class="dg"><thead><tr><th style="width:120px">Label</th><th style="width:260px">Command</th><th style="width:60px">Notify</th></tr></thead><tbody></tbody></table></div><div class="row" style="margin-top:10px"><button class="btn ghost compact disabled">Edit</button><button class="btn ghost compact disabled">Delete</button></div>`, { icon: 'folder-open' })}
</div>
${L.card('How it looks in Telegram', `<div class="tgp">
<div class="bubble in">Connected: <b>prod-web-01</b><br>Send a single-line command. To return to the agent: Close terminal</div>
<div class="bubble in">Quick commands for this project:</div>
<div class="tgkb">${TT.slice(0, 4).map((r) => kbBtn(r[0])).join('')}${kbBtn('Exit', 'wide')}</div>
<div class="bubble out">df -h /var/www<small>00:42</small></div>
<div class="bubble in">Filesystem Size Used Avail Use%<pre>/dev/vda1  80G  41G  35G  55% /</pre></div>
<div class="bubble in">▶ Restart service — running on server…</div>
<div class="bubble in"><span class="warn">${ic('alert', 'ic-sm')}</span> This command cannot run (chaining with &amp;&amp;, |, or ; is not allowed).</div>
<div class="bubble in"><span class="pulse">${ic('spinner', 'ic-sm spin')}</span> Command is still running. Output so far:<pre>00:43:12 INFO building excore…</pre></div>
</div>`, { icon: 'chat' })}
</div></div>`,
    dialogs: `<div class="scrim" id="dlgPreset"><div class="modal"><div class="modal-h">${ic('terminal')}<h3>Add Telegram terminal preset</h3><a class="r btn compact ghost" href="telegram-terminal-command-dialog.html" title="Open as window">${ic('external')}</a></div><div class="modal-b">${ttForm('pn')}</div><div class="modal-f"><button class="btn primary" data-tt-save style="width:90px">Save</button><button class="btn ghost" data-close style="width:90px">Cancel</button></div></div></div>` + TTMB + TTSAVE,
  });

  L.window({
    slug: 'telegram-terminal-command-dialog', title: 'Telegram terminal preset', sub: 'Add Telegram terminal preset', icon: 'chat', w: 560, h: 560, group: 'Windows', css: MMBCSS,
    body: ttForm('td'), footer: `<span class="l"><kbd>Enter</kbd> save · <kbd>Esc</kbd> cancel</span><button class="btn primary" data-tt-save style="width:90px;height:32px">Save</button><button class="btn ghost" style="width:90px;height:32px">Cancel</button>`,
    dialogs: TTMB + TTSAVE,
  });

  // ================================================================ 8. TelegramChatPage
  const threads = [
    { id: 'c1', n: 'MyProject', col: '#4c8dff', ws: 'WS · 2', pv: '3 pending file(s) — tap Deploy when ready', t: '00:41', u: 2 },
    { id: 'c2', n: 'SubTell', col: '#7b61ff', pv: 'Photo', t: '00:12', u: 0 },
    { id: 'c3', n: 'CartEx', col: '#2fa866', pv: 'Deploy done: 7 file(s), 7 uploaded, committed and pushed.', t: 'Sun', u: 0 },
    { id: 'c4', n: 'Bourse', col: '#d9822b', pv: 'Only txt/md, zip, pdf, or images (as document/photo) are accepted.', t: 'Sat', u: 5 },
    { id: 'c5', n: 'api-gateway', col: '#c2457a', pv: 'No messages yet', t: '', u: 0 },
    { id: 'c6', n: 'Unassigned', col: '#6b7a93', pv: 'Add a bot token in Settings.', t: 'Fri', u: 0 },
  ];
  const thread = (th, s) => `<div class="thread ${s ? 'sel' : ''}" data-chat="${th.id}" data-ctx="ctxThread" title="${th.n}"><span class="tav" style="background:${th.col}">${th.n[0].toUpperCase()}</span><div class="grow"><div class="tn1 ellipsis">${th.n}</div><div class="row" style="gap:6px;margin-top:2px">${th.ws ? `<span class="wsb">${th.ws}</span>` : ''}<span class="muted ellipsis pv" style="font-size:11px">${th.pv}</span></div></div><div class="tmeta"><span class="muted tt" style="font-size:10px">${th.t}</span>${th.u ? `<span class="unread">${th.u}</span>` : ''}</div></div>`;
  const bub = (dir, html, t, attrs = '') => `<div class="bubble ${dir}" ${attrs}>${html}<small>${t}</small></div>`;
  const photo = (n = 'screenshot.png') => `<div class="tgphoto" title="Open photo"><i></i><i></i><i></i><span>${n}</span></div>`;
  const doc = (n) => `<div class="row" style="gap:6px">${ic('file-text')}<b>${n}</b></div>`;
  const composer = (disabled = false) => `<div class="tgc"><button class="btn icon" title="Attach photo" data-attach ${disabled ? 'disabled' : ''}>${ic('paperclip')}</button><textarea class="input tgbox" rows="1" placeholder="Message" ${disabled ? 'disabled' : ''}></textarea><button class="btn primary" style="min-width:88px" data-send ${disabled ? 'disabled' : ''}><span class="bl">Send</span></button></div>`;
  const pending = (show) => `<div class="tgpend" ${show ? '' : 'hidden'}><span class="sec ellipsis">${ic('image')} Photo attached</span><button class="btn ghost compact" data-clear-photo>Remove photo</button></div>`;
  const panel = (th, { status, stCls = '', msgs = '', pend = false, banner = '', worker = '', empty = '', dis = false, active = false }) => `<div class="tgp-pane ${active ? 'active' : ''}" id="pane-${th.id}">
<div class="tgh"><div class="grow"><div class="tgt">${th.n}</div><div class="tgs ${stCls}">${status}</div></div><div class="row"><button class="btn secondary" title="Clear chat history in the app only" data-confirm="Clear this project's chat history in GitDeploy? Telegram messages stay." data-confirm-title="Clear chat">${ic('trash')}<span class="bl">Clear chat</span></button><button class="btn secondary" title="Delete Telegram messages + clear app chat" data-confirm="Delete tracked Telegram messages for this project and clear the app chat?" data-confirm-title="Clear Telegram">${ic('send')}<span class="bl">Clear Telegram</span></button></div></div>
${banner}<div class="tgm">${empty || msgs}</div>${worker}${pending(pend)}${composer(dis)}</div>`;

  const m1 = bub('in', 'Add a loading spinner to the deploy button', '00:31') + bub('in', photo() + 'see this alignment bug on mobile', '00:32') +
    bub('out', 'Got it. Queued 2 tasks for the agent (Cursor · Auto).', '00:32') + bub('in', '<span style="display:block;text-align:right">لطفاً دکمه دیپلوی را در حالت بارگذاری غیرفعال کن</span>', '00:35', 'dir="rtl"') +
    bub('in', doc('notes.md'), '00:37') + bub('out', 'Agent finished: <span class="mono">resources/js/deploy.js</span>, <span class="mono">DeployController.php</span> updated.', '00:39') +
    bub('out', '3 pending file(s) — tap Deploy when ready (you can keep sending tasks).', '00:40') + bub('in', 'Deploy', '00:41') + bub('out', 'Deploy started in GitDeploy… checking connection and uploading.', '00:41');
  const dep = L.worker({ icon: 'upload', name: 'Deploy from Telegram', desc: 'myproject → prod-web-01 · 3 files · sftp', pct: 34, speed: 1.4, stats: ['<b>1.2</b> MB/s', 'elapsed <b data-elapsed="12">00:00:12</b>'] });
  const m2 = bub('in', photo('checkout.jpg') + 'checkout page', '00:10') + bub('in', doc('invoice-template.pdf'), '00:12');
  const m3 = bub('in', 'deploy', 'Sun 22:04') + bub('out', 'Deploy started in GitDeploy… checking connection and uploading.', 'Sun 22:04') + bub('out', 'Deploy done: 7 file(s), 7 uploaded, committed and pushed.', 'Sun 22:06');
  const m4 = bub('in', 'archive.rar', 'Sat 18:20') + bub('out', 'Only txt/md, zip, pdf, or images (as document/photo) are accepted.', 'Sat 18:20') + bub('in', 'Deploy', 'Sat 18:25') + bub('out', 'A deploy is already running. Wait for it to finish.', 'Sat 18:25') + bub('out', 'Deploy failed: Upload failed. Commit was skipped.', 'Sat 18:31') + bub('in', 'status', 'Sat 18:40');
  const m6 = bub('out', 'Add a bot token in Settings.', 'Fri 09:12') + bub('out', 'Send a message to the bot from Telegram first.', 'Fri 09:13');

  L.page({
    slug: 'telegram', title: 'Telegram', nav: 'telegram', flush: true, group: 'Pages',
    css: `
.tg{display:grid;grid-template-columns:clamp(220px,24vw,300px) minmax(0,1fr);gap:8px;padding:8px;height:100%;min-height:0}
.tgcol{background:var(--surface-shell);border:1px solid var(--border-subtle);border-radius:10px;min-height:0;min-width:0;display:grid;overflow:hidden}
.tgside{grid-template-rows:auto auto minmax(0,1fr) auto} .tgmain{grid-template-rows:minmax(0,1fr)}
.tgsh{padding:14px 14px 8px} .tgsh h2{font-size:16px;font-weight:600} .tgsh p{margin:4px 0 0;font-size:11px;color:var(--text-muted)}
.tgsearch{padding:0 12px 10px}
.tglist{overflow:auto;padding:0 6px 8px;min-height:0}
.thread{align-items:flex-start;padding:8px;cursor:pointer;margin-bottom:2px} .thread .grow{align-self:center}
.tav{width:38px;height:38px;border-radius:50%;display:grid;place-items:center;color:#fff;font-weight:600;font-size:14px;flex:none}
.tn1{font-weight:600;font-size:13px} .wsb{background:var(--info-surface);color:var(--info);font-size:9px;font-weight:600;border-radius:6px;padding:1px 5px;flex:none}
.tmeta{display:flex;flex-direction:column;align-items:flex-end;gap:6px;flex:none} .thread .unread{font-size:10px;font-weight:600;min-width:18px}
.tgbridge{padding:8px;border-top:1px solid var(--border-subtle)}
.tgpanes{min-height:0;display:grid}
.tgp-pane{display:none;flex-direction:column;min-height:0} .tgp-pane.active{display:flex}
.tgh{display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:12px 14px;border-bottom:1px solid var(--border-subtle);flex:none}
.tgt{font-size:15px;font-weight:600} .tgs{font-size:11px;color:var(--text-muted);margin-top:3px} .tgs.err{color:var(--error)} .tgs.ok{color:var(--text-secondary)}
.tgh .btn{padding:6px 10px;height:32px}
.tgm{flex:1;overflow:auto;padding:12px 16px;display:flex;flex-direction:column;gap:8px;min-height:0;background:radial-gradient(600px 300px at 80% 0,rgba(76,141,255,.05),transparent 70%)}
.tgm .bubble{max-width:min(460px,82%);font-size:13px;overflow-wrap:anywhere;padding:9px 12px;flex:none} .tgm .bubble small{opacity:.75;font-size:10px;margin-top:6px}
.tgm .bubble.in small{text-align:right}
.tgphoto{position:relative;width:min(260px,100%);height:150px;border-radius:8px;margin-bottom:8px;background:linear-gradient(135deg,#0d1a30,#1b2d52 60%,#26407a);border:1px solid rgba(255,255,255,.08);overflow:hidden;cursor:pointer}
.tgphoto i{position:absolute;left:12px;right:12px;height:12px;border-radius:3px;background:rgba(255,255,255,.14)} .tgphoto i:nth-child(1){top:16px;width:40%} .tgphoto i:nth-child(2){top:40px;height:46px;right:30%;background:rgba(76,141,255,.45)} .tgphoto i:nth-child(3){top:96px;width:60%;background:rgba(255,92,106,.4)}
.tgphoto span{position:absolute;left:8px;bottom:6px;font:10px var(--font-mono);color:rgba(255,255,255,.7)}
.tgpend{display:flex;justify-content:space-between;align-items:center;gap:10px;padding:8px 14px;background:var(--surface-raised);border-top:1px solid var(--border-subtle);flex:none} .tgpend[hidden]{display:none}
.tgc{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:8px;align-items:end;padding:10px;border-top:1px solid var(--border-subtle);background:var(--chat-composer);flex:none}
.tgc .btn.icon{width:36px;height:36px} .tgc .tgbox{min-height:36px;max-height:90px;padding:8px 12px;resize:none;overflow:auto;height:36px} .tgc .btn.primary{height:36px}
.tgwork{padding:8px 14px 10px;flex:none}
.tgban{margin:10px 14px 0;flex:none}
.typing{display:inline-flex;gap:3px;padding:4px 2px} .typing i{width:5px;height:5px;border-radius:50%;background:var(--text-muted);animation:pulse 1s infinite} .typing i:nth-child(2){animation-delay:.2s} .typing i:nth-child(3){animation-delay:.4s}
@media (max-width:1099.98px){.tgh .bl{display:none}}
`,
    body: `<div class="tg">
<aside class="tgcol tgside"><div class="tgsh"><h2>Telegram</h2><p>One bot, a separate chat for each project.</p></div>
<div class="tgsearch"><div class="input-icon">${ic('search')}<input class="input" placeholder="Search projects…" data-filter="#tgThreads"></div></div>
<div class="tglist" id="tgThreads" data-selectable>${threads.map((th, i) => thread(th, i === 0)).join('')}</div>
<div class="tgbridge">${L.worker({ icon: 'chat', name: 'Telegram bridge', desc: 'getUpdates · long polling', pct: 20, speed: 3, loop: true, controls: false, stats: ['offset <b>48213</b>', '<b>1</b> bot'] })}</div></aside>
<section class="tgcol tgmain"><div class="tgpanes">
${panel(threads[0], { active: true, status: 'WS · 2 extra · Connected', stCls: 'ok', msgs: m1, worker: `<div class="tgwork">${dep}</div>` })}
${panel(threads[1], { status: 'Connected', stCls: 'ok', msgs: m2, pend: true })}
${panel(threads[2], { status: 'Connected', stCls: 'ok', msgs: m3 })}
${panel(threads[3], { status: 'Error: 409 Conflict: terminated by other getUpdates request', stCls: 'err', msgs: m4, banner: `<div class="banner error tgban">${ic('x-circle')}<div class="grow"><b>Bot is not connected</b><div class="muted" style="font-size:12px">Telegram bridge stalled — reconnecting…</div></div><button class="btn compact" data-working="Restarting..." data-done="Restarting Telegram bridge…">${ic('refresh')}<span class="bl">Retry</span></button></div>` })}
${panel(threads[4], { status: 'Starting…', empty: `<div class="empty" style="margin:auto">${ic('chat')}<b class="sec">No messages yet</b><span>Open a project to start this chat.</span></div>`, dis: true })}
${panel(threads[5], { status: 'Stopped — No bot token', msgs: m6, banner: `<div class="banner warn tgban">${ic('alert')}<div class="grow">Add a bot token in Settings.</div><a class="btn compact" href="settings.html">${ic('settings')}<span class="bl">Settings</span></a></div>` })}
</div></section></div>
<div class="ctx" id="ctxThread"><div class="menu"><a class="mi" href="manage-workspace-window.html" style="color:inherit">${ic('folder-plus')}Manage workspace</a><div class="msep"></div><div class="mi" data-confirm="Clear this project's chat history in GitDeploy? Telegram messages stay." data-confirm-title="Clear this chat">${ic('trash')}Clear this chat</div></div></div>
<script>(function(){
var tg=document.querySelector('.tg');function $$(s,r){return Array.prototype.slice.call((r||document).querySelectorAll(s));}
function pad(n){return ('0'+n).slice(-2);}function now(){var d=new Date();return pad(d.getHours())+':'+pad(d.getMinutes());}
function show(id){$$('.tgp-pane',tg).forEach(function(p){p.classList.toggle('active',p.id==='pane-'+id);});var p=document.getElementById('pane-'+id),m=p.querySelector('.tgm');m.scrollTop=m.scrollHeight;}
document.getElementById('tgThreads').addEventListener('click',function(e){var t=e.target.closest('.thread');if(!t)return;show(t.dataset.chat);var u=t.querySelector('.unread');if(u)u.remove();});
tg.addEventListener('click',function(e){var p=e.target.closest('.tgp-pane');if(!p)return;
 if(e.target.closest('[data-attach]'))p.querySelector('.tgpend').hidden=false;
 if(e.target.closest('[data-clear-photo]'))p.querySelector('.tgpend').hidden=true;
 if(e.target.closest('[data-send]'))send(p);});
tg.addEventListener('keydown',function(e){if(e.target.classList.contains('tgbox')&&e.key==='Enter'&&!e.shiftKey){e.preventDefault();send(e.target.closest('.tgp-pane'));}});
tg.addEventListener('input',function(e){if(e.target.classList.contains('tgbox')){e.target.style.height='36px';e.target.style.height=Math.min(90,e.target.scrollHeight)+'px';}});
function add(p,dir,html){var m=p.querySelector('.tgm'),d=document.createElement('div');d.className='bubble '+dir;d.innerHTML=html+'<small>'+now()+'</small>';m.appendChild(d);m.scrollTop=m.scrollHeight;return d;}
function send(p){var b=p.querySelector('.tgbox'),v=b.value.trim(),ph=!p.querySelector('.tgpend').hidden;if(!v&&!ph){if(window.toast)toast({type:'warning',title:'Type a message or attach a photo.'});return;}
 add(p,'out',(ph?'<div class="tgphoto"><i></i><i></i><i></i><span>photo.jpg</span></div>':'')+(v?v.replace(/</g,'&lt;'):''));b.value='';b.style.height='36px';p.querySelector('.tgpend').hidden=true;
 var t=add(p,'in','<span class="typing"><i></i><i></i><i></i></span>');setTimeout(function(){t.innerHTML='Received. Queued for the agent.<small>'+now()+'</small>';p.querySelector('.tgm').scrollTop=1e6;},1600);}
function bump(id,txt){var t=document.querySelector('.thread[data-chat="'+id+'"]');if(!t||t.classList.contains('sel'))return;var u=t.querySelector('.unread');if(!u){u=document.createElement('span');u.className='unread';u.textContent='0';t.querySelector('.tmeta').appendChild(u);}u.textContent=(+u.textContent||0)+1;t.querySelector('.pv').textContent=txt;t.querySelector('.tt').textContent=now();var l=t.parentNode;l.insertBefore(t,l.firstChild);}
var feed=[['c2','Photo'],['c4','status'],['c3','deploy'],['c2','Is the new build live?']],fi=0;
setInterval(function(){var f=feed[fi++%feed.length];bump(f[0],f[1]);var p=document.getElementById('pane-'+f[0]);if(p)add(p,'in',f[1]==='Photo'?'<div class="tgphoto"><i></i><i></i><i></i><span>IMG_2041.jpg</span></div>':f[1]);},6500);
show('c1');
setTimeout(function(){var p=document.getElementById('pane-c1'),t=add(p,'in','<span class="typing"><i></i><i></i><i></i></span>');setTimeout(function(){t.innerHTML='status<small>'+now()+'</small>';add(p,'out','Agent: warm / alive · Queue: busy · Model: Auto');},2200);},4000);
})();</script>`,
  });

  // ================================================================ 9. ModernMessageBox
  const live = (id, label, icon) => `<button class="btn" data-open="${id}">${ic(icon)}<span class="bl">${label}</span></button>`;
  const V = [
    ['mbInfo', 'info', 'Info', 'Please select an SSH profile first.', 'ok', 'Information · OK (dismiss X visible, scrim click closes)'],
    ['mbSuccess', 'success', 'Deploy Success', 'Deploy done: 12 file(s), 12 uploaded, committed and pushed.', 'ok', 'Information + title containing “Success” → green kind'],
    ['mbWarn', 'warning', 'Command Presets', 'Please enter both a title and a command.', 'ok', 'Warning · OK'],
    ['mbWarn2', 'warning', 'Overwrite remote file?', 'The remote file <code>/var/www/myproject/.env</code> is newer than your local copy. Overwrite it?', 'okcancel', 'Warning · OK / Cancel'],
    ['mbErr', 'error', 'Connection Error', 'Failed to connect: Connection timed out (203.0.113.10:22)', 'ok', 'Error · OK button turns red'],
    ['mbQ', 'question', 'Delete preset', 'Delete the selected preset “Restart queue”? This cannot be undone.', 'yesno', 'Question → “Confirm” · Yes / No'],
    ['mbQ3', 'question', 'Unsaved changes', 'Save changes to <code>.env.production</code> before closing?', 'yesnocancel', 'Question · Yes / No / Cancel'],
  ];
  L.page({
    slug: 'modern-message-box', title: 'ModernMessageBox', nav: '', group: 'Controls', css: MMBCSS + `.mmbgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(100%,400px),1fr));gap:20px}.mmbcell{display:flex;flex-direction:column;gap:8px;align-items:flex-start;min-width:0;width:100%}`,
    body: `<div class="page-inner"><div class="page-head"><div><h1 class="page-title">ModernMessageBox</h1><div class="page-sub">Replacement for the system MessageBox — scrim overlay, kind accent bar, icon badge, title, message and right-aligned buttons.</div></div>
<div class="r">${live('mbInfo', 'Info', 'info')}${live('mbSuccess', 'Success', 'check-circle')}${live('mbWarn2', 'Warning', 'alert')}${live('mbErr', 'Error', 'x-circle')}${live('mbQ', 'Question', 'info')}</div></div>
<div class="mmbgrid">${V.map((v) => `<div class="mmbcell"><span class="eyebrow">${v[5]}</span>${mmb(v[1], v[2], v[3], v[4], { closeAttr: `data-toast="info" data-title="${v[2]}" data-msg="Dialog result sent"` })}</div>`).join('')}</div>
<div class="section-title" style="margin-top:28px">${ic('layers')}Anatomy</div>
<div class="grid auto-fit"><div class="card dense"><div class="eyebrow">Card</div><div class="sec">440 wide (min 380, max 520), radius 16, Surface.Raised, 1px Border.Subtle, 40px blur shadow, fade-in.</div></div>
<div class="card dense"><div class="eyebrow">Accent bar</div><div class="sec">4px left bar and icon in the kind colour; icon badge 44×44, radius 12, Status.*Surface.</div></div>
<div class="card dense"><div class="eyebrow">Buttons</div><div class="sec">Default button = accent (red for Error). OK-only boxes show a dismiss X; Enter = default, Esc = cancel.</div></div></div></div>`,
    dialogs: V.map((v) => mmbScrim(v[0], v[1], v[2], v[3], v[4])).join(''),
  });
};
