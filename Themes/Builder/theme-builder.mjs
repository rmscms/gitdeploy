#!/usr/bin/env node
// GitDeployPro Theme Builder — generates a complete theme pack (gitdeploypro-theme/v1) from a tiny spec.
//
//   node Themes/Builder/theme-builder.mjs specs/ocean-teal.json            -> Themes/Builder/out/ocean-teal.json
//   node Themes/Builder/theme-builder.mjs --all                            -> every spec in specs/ -> out/
//   node Themes/Builder/theme-builder.mjs specs/x.json --install           -> also copies the pack to Themes/Packs/ (ships with the app)
//   node Themes/Builder/theme-builder.mjs --tokens                         -> prints every palette token the app knows
//   node Themes/Builder/theme-builder.mjs --check Themes/Packs/Foo.json    -> validates + contrast report for an existing pack
//
// Spec (all fields but id/displayName optional):
// {
//   "id": "ocean-teal", "displayName": "Ocean Teal", "version": "1", "author": "me",
//   "mode": "dark" | "light",        // surface ramp direction (default dark)
//   "basedOn": "dark" | "default" | "<other pack id>",   // supplies every token you do not set (default: dark)
//   "hue": 190, "tint": 0.5,         // surface tint: hue 0-360, saturation 0-1 (0 = neutral grays)
//   "accent": "#2DD4BF", "accent2": "#7B61FF",
//   "glow": true,                    // accent glow on the shell + ambient radial lights (Shell.* tokens)
//   "palette": { "Status.Success": "#..." },            // explicit overrides win over derived values
//   "deploy":  { "terminal": { "hostBackground": "#..." } },   // deep-merged into deploy.*
//   "icons":   { "rocket": "M..." },                    // icon overrides (24x24 stroke path data)
//   "metrics": { "Radius.Card": "16", "Spacing.PageMargin": "24" }
// }
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const appRoot = path.join(here, '..', '..');
const packsDir = path.join(appRoot, 'Themes', 'Packs');
const outDir = path.join(here, 'out');
const catalogPath = path.join(appRoot, 'Services', 'Theme', 'ThemeTokenCatalog.cs');

// ---------- color helpers ----------
const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const hex2 = (n) => Math.round(clamp(n, 0, 255)).toString(16).padStart(2, '0').toUpperCase();
function parseHex(h) {
  const m = /^#([0-9a-f]{6})([0-9a-f]{2})?$/i.exec(h) || /^#([0-9a-f]{2})([0-9a-f]{6})$/i.exec(h);
  if (!m) throw new Error('bad color ' + h);
  const six = h.length === 9 ? h.slice(3) : h.slice(1);
  return [0, 2, 4].map((i) => parseInt(six.slice(i, i + 2), 16));
}
const toHex = ([r, g, b], a) => '#' + (a == null || a >= 1 ? '' : hex2(a * 255)) + hex2(r) + hex2(g) + hex2(b);
function hsl2rgb(h, s, l) {
  h = ((h % 360) + 360) % 360 / 360;
  if (s === 0) return [l * 255, l * 255, l * 255];
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
  const f = (t) => { t = (t + 1) % 1; return t < 1 / 6 ? p + (q - p) * 6 * t : t < 1 / 2 ? q : t < 2 / 3 ? p + (q - p) * (2 / 3 - t) * 6 : p; };
  return [f(h + 1 / 3) * 255, f(h) * 255, f(h - 1 / 3) * 255];
}
function rgb2hsl([r, g, b]) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min, s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s, l];
}
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
const lighten = (rgb, d) => { const [h, s, l] = rgb2hsl(rgb); return hsl2rgb(h, s, clamp(l + d)); };
const lum = (rgb) => { const f = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(rgb[0]) + 0.7152 * f(rgb[1]) + 0.0722 * f(rgb[2]); };
const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };

// ---------- catalog (kept in sync with C#) ----------
function readRequiredKeys() {
  const src = fs.readFileSync(catalogPath, 'utf8');
  const block = /RequiredPaletteKeys\s*=\s*\{([\s\S]*?)\};/.exec(src);
  if (!block) throw new Error('cannot read RequiredPaletteKeys from ThemeTokenCatalog.cs');
  return [...block[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}
function loadPack(id) {
  const file = fs.readdirSync(packsDir).find((f) => f.endsWith('.json') && JSON.parse(fs.readFileSync(path.join(packsDir, f), 'utf8')).id === id);
  if (!file) throw new Error(`basedOn pack "${id}" not found in Themes/Packs (built-ins: dark, default)`);
  return JSON.parse(fs.readFileSync(path.join(packsDir, file), 'utf8'));
}
function deepMerge(base, over) {
  if (Array.isArray(over) || typeof over !== 'object' || over === null) return over === undefined ? base : over;
  const out = { ...(base && typeof base === 'object' ? base : {}) };
  for (const [k, v] of Object.entries(over)) out[k] = deepMerge(out[k], v);
  return out;
}

// ---------- palette derivation ----------
function derivePalette(spec, base) {
  const mode = spec.mode === 'light' ? 'light' : 'dark';
  const hue = spec.hue ?? 220, sat = spec.tint ?? 0.5;
  const accent = parseHex(spec.accent || base.palette['Accent.Primary']);
  const accent2 = parseHex(spec.accent2 || base.palette['Accent.Secondary']);
  const surf = (l, s = sat) => hsl2rgb(hue, s, l);
  const dark = mode === 'dark';
  const L = dark
    ? { base: 0.028, shell: 0.058, raised: 0.088, card: 0.108, input: 0.062, subtle: 0.15, strong: 0.24 }
    : { base: 0.965, shell: 0.94, raised: 0.915, card: 0.985, input: 0.995, subtle: 0.86, strong: 0.76 };
  const S = dark ? sat : sat * 0.6;
  const p = {};
  p['Surface.Base'] = toHex(surf(L.base, S));
  p['Surface.Shell'] = toHex(surf(L.shell, S));
  p['Surface.Raised'] = toHex(surf(L.raised, S));
  p['Surface.Card'] = toHex(surf(L.card, S));
  p['Surface.Input'] = toHex(surf(L.input, S));
  p['Surface.Overlay'] = toHex(surf(dark ? 0.02 : 0.2, S), dark ? 0.8 : 0.55);
  p['Border.Subtle'] = toHex(surf(L.subtle, S));
  p['Border.Strong'] = toHex(surf(L.strong, S));
  p['Text.Primary'] = toHex(hsl2rgb(hue, dark ? 0.55 : 0.25, dark ? 0.95 : 0.1));
  p['Text.Secondary'] = toHex(hsl2rgb(hue, dark ? 0.35 : 0.2, dark ? 0.82 : 0.24));
  p['Text.Muted'] = toHex(hsl2rgb(hue, dark ? 0.16 : 0.12, dark ? 0.62 : 0.42));
  p['Text.Inverse'] = p['Surface.Base'];
  p['Accent.Primary'] = toHex(accent);
  p['Accent.PrimaryHover'] = toHex(lighten(accent, dark ? 0.08 : -0.08));
  p['Accent.Secondary'] = toHex(accent2);
  p['Accent.SecondaryHover'] = toHex(lighten(accent2, dark ? 0.08 : -0.08));
  const washBase = surf(L.base, S);
  for (const k of ['Success', 'Warning', 'Error', 'Info']) {
    let c = parseHex(base.palette['Status.' + k]);
    if (!dark) { const [h, sa, l] = rgb2hsl(c); c = hsl2rgb(h, sa, Math.min(l, 0.36)); } // keep status colours readable on light surfaces
    p['Status.' + k] = toHex(c);
    p['Status.' + k + 'Surface'] = toHex(mix(washBase, c, dark ? 0.1 : 0.14));
  }
  const white = hsl2rgb(hue, 1, dark ? 0.75 : 0.2);
  p['State.Hover'] = toHex(white, dark ? 0.08 : 0.07);
  p['State.Pressed'] = toHex(white, dark ? 0.14 : 0.12);
  p['State.DisabledOverlay'] = toHex(surf(L.shell, S), 0.67);
  p['Shadow.Color'] = '#99000000';
  p['Chat.BubbleIn'] = toHex(mix(surf(L.card, S), surf(L.strong, S), 0.25));
  p['Chat.BubbleOut'] = toHex(accent);
  p['Chat.BubbleInText'] = p['Text.Primary'];
  p['Chat.BubbleOutText'] = '#FFFFFF';
  p['Chat.Composer'] = p['Surface.Input'];
  p['Chat.Unread'] = toHex(accent);
  p['Chat.ThreadHover'] = p['State.Hover'];
  p['Accent.Soft'] = toHex(accent, 0.14);
  p['Accent.Glow'] = toHex(accent, 0.4);
  const glow = spec.glow !== false && dark;
  p['Shell.Glow'] = glow ? toHex(accent, 0.2) : '#00000000';
  p['Shell.AmbientA'] = glow ? toHex(accent, 0.1) : '#00000000';
  p['Shell.AmbientB'] = glow ? toHex(accent2, 0.07) : '#00000000';
  return p;
}

// ---------- build / validate ----------
function build(specPath) {
  const spec = JSON.parse(fs.readFileSync(specPath, 'utf8'));
  if (!spec.id || !spec.displayName) throw new Error('spec needs id and displayName');
  const basedOn = spec.basedOn || (spec.mode === 'light' ? 'default' : 'dark');
  const base = loadPack(basedOn);
  const required = readRequiredKeys();
  const palette = { ...base.palette, ...derivePalette(spec, base), ...(spec.palette || {}) };
  const pack = {
    schema: 'gitdeploypro-theme/v1',
    id: spec.id,
    displayName: spec.displayName,
    basedOn,
    version: String(spec.version || '1'),
    author: spec.author || 'GitDeployPro',
    palette: Object.fromEntries(required.map((k) => [k, palette[k]]).filter(([, v]) => v)),
    deploy: deepMerge(base.deploy, spec.deploy || {}),
  };
  if (spec.icons && Object.keys(spec.icons).length) pack.icons = spec.icons;
  if (spec.metrics && Object.keys(spec.metrics).length) pack.metrics = spec.metrics;
  return { pack, report: check(pack, required) };
}

function check(pack, required = readRequiredKeys()) {
  const issues = [];
  for (const k of required) if (!pack.palette[k]) issues.push(`E missing palette.${k}`);
  for (const [k, v] of Object.entries(pack.palette)) {
    if (!/^#([0-9a-f]{6}|[0-9a-f]{8})$/i.test(v)) issues.push(`E palette.${k} is not a hex color: ${v}`);
    if (!required.includes(k)) issues.push(`W unknown palette key ${k}`);
  }
  const rgb = (k) => parseHex(pack.palette[k]);
  const pairs = [['Text.Primary', 'Surface.Base', 7], ['Text.Primary', 'Surface.Card', 7], ['Text.Secondary', 'Surface.Card', 4.5], ['Text.Muted', 'Surface.Card', 3], ['Accent.Primary', 'Surface.Card', 3], ['Status.Error', 'Surface.Card', 3], ['Status.Success', 'Surface.Card', 3]];
  for (const [fg, bg, min] of pairs) {
    try { const c = contrast(rgb(fg), rgb(bg)); if (c < min) issues.push(`W low contrast ${fg} on ${bg}: ${c.toFixed(2)} (< ${min})`); } catch { /* alpha colours skipped */ }
  }
  return issues;
}

// ---------- CLI ----------
const args = process.argv.slice(2);
if (args.includes('--tokens')) {
  console.log(readRequiredKeys().join('\n'));
} else if (args[0] === '--check') {
  const pack = JSON.parse(fs.readFileSync(args[1], 'utf8'));
  const issues = check({ ...pack, palette: { ...loadPack(pack.basedOn || 'dark').palette, ...pack.palette } });
  console.log(issues.length ? issues.join('\n') : 'OK — no issues');
} else {
  const specs = args.includes('--all')
    ? fs.readdirSync(path.join(here, 'specs')).filter((f) => f.endsWith('.json')).map((f) => path.join(here, 'specs', f))
    : args.filter((a) => !a.startsWith('--')).map((a) => path.resolve(a));
  if (!specs.length) { console.log('usage: node theme-builder.mjs <spec.json> [--install] | --all | --tokens | --check <pack.json>'); process.exit(1); }
  fs.mkdirSync(outDir, { recursive: true });
  for (const s of specs) {
    const { pack, report } = build(s);
    const file = path.join(outDir, pack.id + '.json');
    fs.writeFileSync(file, JSON.stringify(pack, null, 2) + '\n');
    if (args.includes('--install')) fs.copyFileSync(file, path.join(packsDir, pack.id + '.json'));
    console.log(`${pack.displayName} (${pack.id}) -> ${path.relative(appRoot, file)}${args.includes('--install') ? ' [installed]' : ''}`);
    report.forEach((r) => console.log('  ' + r));
  }
}
