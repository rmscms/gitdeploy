// Run from anywhere: node public/gitdeployPro/_build/build.cjs
const fs = require('fs');
const path = require('path');
const L = require('./lib.cjs');
const dir = path.join(__dirname, 'pages');
for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.cjs')).sort()) {
  try { require(path.join(dir, f))(L); } catch (e) { console.error('FAILED in pages/' + f + ': ' + e.message); process.exitCode = 1; }
}
console.log('built', L.emit(), 'screens');
