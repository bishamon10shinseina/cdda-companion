'use strict';
// CDDA Companion front end. Talks only to the local tool (same origin) with the token from the page.

const TOKEN = document.querySelector('meta[name="companion-token"]').content;
const OM = 180;
const $ = id => document.getElementById(id);

async function api(path, body) {
  const opt = { headers: { 'X-Companion-Token': TOKEN } };
  if (body !== undefined) {
    opt.method = 'POST';
    opt.headers['Content-Type'] = 'application/json';
    opt.body = JSON.stringify(body);
  }
  let r;
  try { r = await fetch(path, opt); }
  catch (e) { offline('The tool isn\'t running any more (it was quit, or stopped after sitting with no page open). Start it again, then reload this page.'); throw e; }
  if (r.status === 403) { offline('This page belongs to an earlier run of the tool. Reload to connect to the current one.'); throw new Error('stale page'); }
  const j = await r.json().catch(() => ({ error: 'Bad response from the tool (' + r.status + ')' }));
  if (!r.ok && !j.error) j.error = 'HTTP ' + r.status;
  return j;
}

const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fdiv = (a, b) => Math.floor(a / b);
function inGame(x, y) {
  const ox = fdiv(x, OM), oy = fdiv(y, OM);
  return `${ox}'${x - ox * OM}, ${oy}'${y - oy * OM}`;
}
function fmtBytes(n) {
  if (!n) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i ? n.toFixed(n < 10 ? 1 : 0) : n) + ' ' + u[i];
}
function ago(iso) {
  if (!iso) return '';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 45) return 'just now';
  if (s < 3600) return Math.round(s / 60) + ' min ago';
  if (s < 86400) return Math.round(s / 3600) + ' h ago';
  return new Date(iso).toLocaleString();
}
function localTime(iso) { return iso ? new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : ''; }

let toastTimer, isOffline = false, quitRequested = false;
function offline(msg) {
  isOffline = true;
  $('bannerText').textContent = msg;
  $('banner').classList.remove('hidden');
}
$('bannerReload').onclick = () => location.reload();
$('quitBtn').onclick = async () => {
  try {
    quitRequested = true;
    const r = await api('/api/shutdown', {});
    offline(r.waiting ? `Stopping after "${r.job}" finishes. You can close this tab.` : 'The tool has stopped. You can close this tab.');
  } catch { }
};
function toast(msg, kind) {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast ' + (kind || '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), kind === 'err' ? 9000 : 5000);
}

function confirmModal(title, bodyHtml, okText) {
  return new Promise(resolve => {
    $('mTitle').textContent = title;
    $('mBody').innerHTML = bodyHtml;
    $('mOk').textContent = okText;
    $('modal').classList.remove('hidden');
    const done = v => { $('modal').classList.add('hidden'); $('mOk').onclick = $('mCancel').onclick = null; document.removeEventListener('keydown', onKey); resolve(v); };
    const onKey = e => { if (e.key === 'Escape') done(false); };
    document.addEventListener('keydown', onKey);
    $('mOk').onclick = () => done(true);
    $('mCancel').onclick = () => done(false);
    $('mOk').focus();
  });
}

// ------------------------------------------------------------------ state
const S = {
  status: null,
  palette: ['#000000', '#ff0000', '#006e00', '#61381c', '#0a0adc', '#8b3a62', '#0096b4', '#969696', '#636363', '#ff9696', '#00ff00', '#ffff00', '#6464ff', '#fe00fe', '#00f0ff', '#ffffff'],
  overmaps: new Set(),
  z: 0, full: false,
  cx: 0, cy: 0, ts: 16,          // view center (tile units) and tile size in CSS px
  cache: new Map(),              // "ox,oy,z,mode" -> data | 'loading' | 'missing'
  lastSave: null,
  tiles: [], hitTiles: new Map(), // "x,y,z" -> result tile
  sort: 'dist',
  from: 'base', custom: null,
  selected: null,
  centered: false,
  lastJobFinished: null,
};

// ------------------------------------------------------------------ status polling
async function pollStatus() {
  let st;
  try { st = await api('/api/status'); }
  catch { setTimeout(pollStatus, 5000); return; }
  if (isOffline && !quitRequested) { isOffline = false; $('banner').classList.add('hidden'); }
  if (st && !st.error) applyStatus(st);
  setTimeout(pollStatus, st && st.job && st.job.running ? 1200 : 4000);
}

function applyStatus(st) {
  if (st.setup) { showSetup(); return; }
  if (!$('setup').classList.contains('hidden')) { location.reload(); return; }
  const prev = S.status;
  S.status = st;
  if (st.palette && st.palette.join() !== S.palette.join()) { S.palette = st.palette; applyPalette(); clearMapCache(); }
  S.overmaps = new Set((st.overmaps || []).map(([x, y]) => x + ',' + y));

  const ch = st.character;
  $('who').innerHTML = `<b>${esc(st.world)}</b>` + (ch ? `<span>${esc(ch.name)}</span><span>${esc(ch.date)}</span>` : (st.characterError ? '<span class="bad">character unreadable</span>' : ''));

  // snapshot chip
  const sc = $('snapChip'), sn = st.snapshot;
  const chip = (v, cls) => { sc.innerHTML = `<span class="k">Snapshot:</span> <span class="v">${esc(v)}</span>`; sc.className = 'chip ' + cls; };
  if (!st.compressed) chip('not needed', 'fresh');
  else if (!sn.exists) chip('none yet', 'none');
  else if (sn.stale) chip(sn.auto ? 'older than save, refreshing soon' : 'older than save', 'stale');
  else chip('up to date (' + ago(sn.taken) + ')', 'fresh');

  // job chip
  const j = st.job;
  if (j.running) {
    $('jobChip').classList.remove('hidden');
    $('jobText').textContent = (j.text || j.name) + (j.total ? ` ${pct(j.done, j.total)}` : '');
    $('jobBar').style.width = j.total ? (100 * j.done / j.total).toFixed(1) + '%' : '30%';
  } else {
    $('jobChip').classList.add('hidden');
    if (j.finished && j.finished !== S.lastJobFinished) {
      if (S.lastJobFinished !== null || (prev && prev.job.running)) {
        if (j.error) toast(j.error, 'err'); else if (j.result) toast(j.result, 'ok');
        if (['backup', 'restore', 'delete'].includes(j.name)) loadSlots();
        if (j.name === 'restore') clearMapCache();
      }
      S.lastJobFinished = j.finished;
    }
  }
  if (S.lastJobFinished === null) S.lastJobFinished = j.finished || '';

  // the save changed: the seen-map and terrain may have changed too
  if (S.lastSave && st.lastSave && st.lastSave !== S.lastSave) clearMapCache();
  S.lastSave = st.lastSave;

  if (!st.game.ready) {
    $('mapMsg').textContent = st.game.error ? 'Game data failed to load: ' + st.game.error : 'Loading game data…';
    $('mapMsg').classList.remove('hidden');
  } else {
    $('mapMsg').classList.add('hidden');
    if (!S.centered) {
      S.centered = true;
      if (ch) centerOn(ch.x, ch.y, ch.z); else if (st.base) centerOn(st.base.x, st.base.y, 0);
    }
  }
  renderStatusPanel();
  updateFromSeg();
  requestDraw();
  if (!$('panel-saves').classList.contains('hidden')) renderDisk();
}
// the page's colours are the game's 16, so a colour theme picked in the game's colour manager shows here too
function applyPalette() {
  const root = document.documentElement.style;
  S.palette.forEach((c, i) => root.setProperty('--c' + i, c));
}
const pct = (a, b) => b ? Math.floor(100 * a / b) + '%' : '';

function renderStatusPanel() {
  const st = S.status; if (!st) return;
  const sn = st.snapshot, g = st.game;
  const rows = [
    ['Version', esc(st.version)],
    ['World folder', `<span class="mono">${esc(st.worldDir)}</span>`],
    ['Settings file', `<span class="mono">${esc(st.configPath)}</span>`],
    ['Last game save', st.lastSave ? localTime(st.lastSave) : '—'],
    ['Snapshot', !st.compressed ? 'not needed (uncompressed world)' : sn.exists ? `${localTime(sn.taken)}, ${sn.files.toLocaleString()} map chunks, ${fmtBytes(sn.bytes)}${sn.errors ? `, ${sn.errors} unreadable` : ''}` : 'none yet'],
    ['Auto refresh', sn.auto ? 'on (after each game save)' : 'off'],
    ['Colours', !st.paletteSource || st.paletteSource === 'built-in' || st.paletteSource === 'game' ? 'the game\'s defaults' : `your colour theme <span class="muted">${esc(st.paletteSource)}</span>`],
    ['Game data', g.ready ? `${g.terrain.toLocaleString()} map tile types, ${g.items.toLocaleString()} items from ${g.files.toLocaleString()} files (${g.seconds}s)` : (g.error ? 'failed: ' + esc(g.error) : 'loading…')],
  ];
  if (st.character) rows.splice(3, 0, ['Character', `${esc(st.character.name)} at ${esc(st.character.inGame)}, z ${st.character.z}`]);
  if (st.base) rows.splice(4, 0, ['Base', esc(st.base.inGame)]);
  $('statusKv').innerHTML = rows.map(([k, v]) => `<div class="k">${k}</div><div class="v">${v}</div>`).join('');
  const mw = $('modWarn');
  if (g.ready && g.missingMods && g.missingMods.length) {
    mw.innerHTML = `These mods from the world's mod list weren't found in the game folder, so their map tiles show as <code>?</code> and their items' counts are guessed: <code>${g.missingMods.map(esc).join(', ')}</code>`;
    mw.classList.remove('hidden');
  } else mw.classList.add('hidden');
  $('refreshSnap').disabled = !st.compressed || st.job.running;
}

// ------------------------------------------------------------------ map data
function clearMapCache() { S.cache.clear(); requestDraw(); }

function overmapData(ox, oy) {
  if (!S.overmaps.has(ox + ',' + oy) || !S.status || !S.status.game.ready) return null;
  const tsName = activeTileset() || '';
  const key = `${ox},${oy},${S.z},${S.full ? 'full' : 'fog'},${tsName}`;
  const c = S.cache.get(key);
  if (c === undefined) {
    S.cache.set(key, 'loading');
    api(`/api/overmap?x=${ox}&y=${oy}&z=${S.z}&mode=${S.full ? 'full' : 'fog'}&ts=${encodeURIComponent(tsName)}`).then(d => {
      if (d.error || !d.exists) { S.cache.set(key, 'missing'); return; }
      d.thumb = makeThumb(d);
      d.noteAt = new Map(d.notes.map(n => [n[0] + n[1] * OM, n]));
      S.cache.set(key, d);
      if (S.selected && fdiv(S.selected[0], OM) === ox && fdiv(S.selected[1], OM) === oy && !$('tileCard').classList.contains('hidden')) showTile(S.selected[0], S.selected[1]);
      requestDraw();
    }).catch(() => S.cache.delete(key));
    return null;
  }
  return typeof c === 'object' ? c : null;
}

// one pixel per tile, for zoomed-out views
function makeThumb(d) {
  const cv = document.createElement('canvas'); cv.width = OM; cv.height = OM;
  const ctx = cv.getContext('2d');
  const img = ctx.createImageData(OM, OM);
  const rgb = S.palette.map(h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)]);
  const sty = d.styles.map(s => {
    const unseen = s[3] === 'unexplored';
    const c = rgb[s[2] >= 0 ? s[2] : s[1]] || rgb[7];
    return unseen ? [14, 16, 15] : c;
  });
  for (let i = 0; i < OM * OM; i++) {
    const c = sty[d.grid[i]];
    img.data[i * 4] = c[0]; img.data[i * 4 + 1] = c[1]; img.data[i * 4 + 2] = c[2]; img.data[i * 4 + 3] = 255;
  }
  for (const n of d.notes) {
    const c = rgb[n[3]] || rgb[11], i = n[0] + n[1] * OM;
    img.data[i * 4] = c[0]; img.data[i * 4 + 1] = c[1]; img.data[i * 4 + 2] = c[2];
  }
  ctx.putImageData(img, 0, 0);
  return cv;
}

// ------------------------------------------------------------------ drawing
const canvas = $('map');
const ctx = canvas.getContext('2d');
let drawPending = false;
function requestDraw() { if (!drawPending) { drawPending = true; requestAnimationFrame(draw); } }

function resize() {
  const r = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(r.width * dpr));
  canvas.height = Math.max(1, Math.round(r.height * dpr));
  requestDraw();
}
new ResizeObserver(resize).observe(canvas);

function view() {
  const r = canvas.getBoundingClientRect();
  return { w: r.width, h: r.height, x0: S.cx - r.width / 2 / S.ts, y0: S.cy - r.height / 2 / S.ts };
}
function screenToTile(sx, sy) { const v = view(); return [Math.floor(v.x0 + sx / S.ts), Math.floor(v.y0 + sy / S.ts)]; }

function draw() {
  drawPending = false;
  const dpr = window.devicePixelRatio || 1;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const v = view(), ts = S.ts;
  ctx.fillStyle = '#000'; ctx.fillRect(0, 0, v.w, v.h);
  if (!S.status) return;
  const gx0 = Math.floor(v.x0), gy0 = Math.floor(v.y0);
  const gx1 = Math.ceil(v.x0 + v.w / ts), gy1 = Math.ceil(v.y0 + v.h / ts);
  const px = gx => (gx - v.x0) * ts, py = gy => (gy - v.y0) * ts;
  const text = ts >= 9;
  const font = `bold ${Math.round(ts * 0.86)}px ${getComputedStyle(document.body).getPropertyValue('--mono')}`;
  const tsMeta = activeTileset() ? TS.meta.get(activeTileset()) : null;
  if (activeTileset() && !TS.meta.has(activeTileset())) loadTilesetMeta(activeTileset());
  ctx.textAlign = 'center'; ctx.textBaseline = 'middle';

  for (let oy = fdiv(gy0, OM); oy <= fdiv(gy1, OM); oy++) {
    for (let ox = fdiv(gx0, OM); ox <= fdiv(gx1, OM); ox++) {
      const d = overmapData(ox, oy);
      const bx = ox * OM, by = oy * OM;
      if (!d) {
        if (S.overmaps.has(ox + ',' + oy)) { ctx.fillStyle = '#070807'; ctx.fillRect(px(bx), py(by), OM * ts, OM * ts); }
        continue;
      }
      if (!text && !(tsMeta && ts >= 7)) {
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(d.thumb, px(bx), py(by), OM * ts, OM * ts);
        continue;
      }
      if (tsMeta && d.tgrid) { drawTiles(d, tsMeta, bx, by, gx0, gy0, gx1, gy1, px, py, ts, font); continue; }
      const lx0 = Math.max(0, gx0 - bx), ly0 = Math.max(0, gy0 - by);
      const lx1 = Math.min(OM - 1, gx1 - bx), ly1 = Math.min(OM - 1, gy1 - by);
      const buckets = Array.from({ length: 16 }, () => []);
      for (let y = ly0; y <= ly1; y++) {
        for (let x = lx0; x <= lx1; x++) {
          const i = x + y * OM;
          const n = d.noteAt.get(i);
          let sym, fg, bg;
          if (n) { sym = n[2]; fg = n[3]; bg = -1; }
          else { const s = d.styles[d.grid[i]]; sym = s[0]; fg = s[1]; bg = s[2]; }
          const X = px(bx + x), Y = py(by + y);
          if (bg >= 0) { ctx.fillStyle = S.palette[bg]; ctx.fillRect(X, Y, ts, ts); }
          if (n && n[5]) { ctx.strokeStyle = S.palette[1]; ctx.lineWidth = 1; ctx.strokeRect(X + .5, Y + .5, ts - 1, ts - 1); }
          (buckets[fg] || buckets[7]).push(sym, X + ts / 2, Y + ts / 2 + 0.5);
        }
      }
      ctx.font = font;
      for (let c = 0; c < 16; c++) {
        const b = buckets[c]; if (!b.length) continue;
        ctx.fillStyle = S.palette[c];
        for (let k = 0; k < b.length; k += 3) ctx.fillText(b[k], b[k + 1], b[k + 2]);
      }
    }
  }

  // city names (ground level)
  if (S.z === 0 && ts >= 4) {
    ctx.font = `600 ${ts >= 12 ? 13 : 11}px ${getComputedStyle(document.body).getPropertyValue('--sans')}`;
    for (const [key, d] of S.cache) {
      if (typeof d !== 'object' || d.z !== 0 || !key.endsWith((S.full ? 'full' : 'fog') + ',' + (activeTileset() || ''))) continue;
      for (const [x, y, name] of d.cities) {
        const X = px(d.ox * OM + x) + ts / 2, Y = py(d.oy * OM + y) - Math.max(ts, 10);
        if (X < -100 || Y < -20 || X > v.w + 100 || Y > v.h + 20) continue;
        const w = ctx.measureText(name).width + 12;
        ctx.fillStyle = 'rgba(0,0,0,.72)'; ctx.fillRect(X - w / 2, Y - 9, w, 18);
        ctx.fillStyle = '#f2e3b8'; ctx.fillText(name, X, Y + 0.5);
      }
    }
  }

  // base and character
  const st = S.status;
  if (st.base) {
    const X = px(st.base.x), Y = py(st.base.y);
    ctx.strokeStyle = '#7fcf72'; ctx.lineWidth = 2;
    ctx.setLineDash(S.z === 0 ? [] : [3, 3]);
    ctx.strokeRect(X - 2, Y - 2, ts + 4, ts + 4);
    ctx.setLineDash([]);
  }
  const ch = st.character;
  if (ch) {
    const X = px(ch.x), Y = py(ch.y);
    if (ch.z === S.z) {
      ctx.fillStyle = S.palette[4]; ctx.fillRect(X, Y, ts, ts);
      if (text) { ctx.font = font; ctx.fillStyle = '#fff'; ctx.fillText('@', X + ts / 2, Y + ts / 2 + 0.5); }
      else { ctx.fillStyle = '#fff'; ctx.fillRect(X + ts / 2 - 2, Y + ts / 2 - 2, 4, 4); }
    } else {
      ctx.strokeStyle = '#8fb8ff'; ctx.lineWidth = 1.5; ctx.setLineDash([2, 2]);
      ctx.strokeRect(X - 1, Y - 1, ts + 2, ts + 2); ctx.setLineDash([]);
    }
  }
  drawOverlay();
}


// ------------------------------------------------------------------ tilesets
const TS = { choice: '', game: null, list: [], meta: new Map(), sheets: new Map(), last: '' };
try { TS.choice = localStorage.getItem('tileset') ?? ''; TS.last = localStorage.getItem('tilesetLast') || ''; } catch { }
function activeTileset() {
  if (TS.choice === 'ascii') return null;
  const name = TS.choice || TS.game;
  return name && TS.list.some(t => t.name === name) ? name : null;
}
async function loadTilesets() {
  const r = await api('/api/tilesets');
  if (r.error) return;
  TS.game = r.game || null; TS.list = r.tilesets;
  const sel = $('tsSel');
  const gameLabel = TS.game ? `Game: ${TS.list.find(t => t.name === TS.game).view}` : (r.gameMissing ? `Game: ASCII (${r.gameMissing} not found)` : 'Game: ASCII');
  sel.innerHTML = `<option value="">${esc(gameLabel)}</option><option value="ascii">ASCII</option>` +
    TS.list.map(t => `<option value="${esc(t.name)}">${esc(t.view)}</option>`).join('');
  if (TS.choice && TS.choice !== 'ascii' && !TS.list.some(t => t.name === TS.choice)) TS.choice = '';
  sel.value = TS.choice;
  requestDraw();
}
function setTileset(choice) {
  TS.choice = choice;
  if (activeTileset()) TS.last = choice;
  try { localStorage.setItem('tileset', choice); if (TS.last) localStorage.setItem('tilesetLast', TS.last); } catch { }
  $('tsSel').value = choice;
  requestDraw();
}
$('tsSel').onchange = () => { setTileset($('tsSel').value); $('map').focus(); };

function loadTilesetMeta(name) {
  if (TS.meta.has(name)) return;
  TS.meta.set(name, null);
  api(`/api/tileset?name=${encodeURIComponent(name)}`).then(m => {
    if (m.error) { toast(m.error, 'err'); TS.meta.delete(name); setTileset('ascii'); return; }
    TS.meta.set(name, m);
    requestDraw();
  });
}
// sprite sheets come through the API (token header), then become ImageBitmaps
function sheetImage(m, sh) {
  const key = m.name + '\u0001' + sh.file;
  const img = TS.sheets.get(key);
  if (img !== undefined) return img;
  TS.sheets.set(key, null);
  fetch(`/api/gfx?name=${encodeURIComponent(m.name)}&file=${encodeURIComponent(sh.file)}`, { headers: { 'X-Companion-Token': TOKEN } })
    .then(r => r.ok ? r.blob() : Promise.reject(r.status)).then(b => createImageBitmap(b))
    .then(bm => { TS.sheets.set(key, bm); requestDraw(); })
    .catch(() => TS.sheets.delete(key));
  return null;
}
function sheetOf(m, idx) {
  let lo = 0, hi = m.sheets.length - 1;
  while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (m.sheets[mid].first <= idx) lo = mid; else hi = mid - 1; }
  const sh = m.sheets[lo];
  return sh && idx >= sh.first && idx < sh.first + sh.count ? sh : null;
}
function drawSprite(m, idx, rot, X, Y, ts) {
  if (idx < 0) return true;
  const sh = sheetOf(m, idx); if (!sh) return true;
  const img = sheetImage(m, sh); if (!img) return false;
  const local = idx - sh.first, sx = (local % sh.cols) * sh.w, sy = Math.floor(local / sh.cols) * sh.h;
  const k = ts / m.tileW;
  const dw = sh.w * k * sh.scale, dh = sh.h * k * sh.scale, dx = X + sh.ox * k, dy = Y + sh.oy * k;
  if (!rot) { ctx.drawImage(img, sx, sy, sh.w, sh.h, dx, dy, dw, dh); return true; }
  ctx.save();
  ctx.translate(dx + dw / 2, dy + dh / 2);
  if (rot === -1) ctx.scale(-1, 1);
  else ctx.rotate(rot === 1 ? -Math.PI / 2 : rot === 2 ? Math.PI : Math.PI / 2);   // SDL: 1 = 90° counter-clockwise
  ctx.drawImage(img, sx, sy, sh.w, sh.h, -dw / 2, -dh / 2, dw, dh);
  ctx.restore();
  return true;
}
function drawTiles(d, m, bx, by, gx0, gy0, gx1, gy1, px, py, ts, font) {
  ctx.imageSmoothingEnabled = false;
  const lx0 = Math.max(0, gx0 - bx), ly0 = Math.max(0, gy0 - by);
  const lx1 = Math.min(OM - 1, gx1 - bx), ly1 = Math.min(OM - 1, gy1 - by);
  const glyphs = [];
  for (let y = ly0; y <= ly1; y++) {
    for (let x = lx0; x <= lx1; x++) {
      const i = x + y * OM, X = px(bx + x), Y = py(by + y), t = d.tgrid[i];
      if (t) {
        const e = d.tiles[t - 1];
        drawSprite(m, e[2], e[3], X, Y, ts);
        drawSprite(m, e[0], e[1], X, Y, ts);
      } else glyphs.push(i, X, Y, false);
      if (d.noteAt.has(i)) glyphs.push(i, X, Y, true);
    }
  }
  if (!glyphs.length) return;
  ctx.font = font;
  for (let k = 0; k < glyphs.length; k += 4) {
    const i = glyphs[k], X = glyphs[k + 1], Y = glyphs[k + 2], note = glyphs[k + 3];
    let sym, fg, bg;
    if (note) { const n = d.noteAt.get(i); sym = n[2]; fg = n[3]; bg = 0; }
    else { const s = d.styles[d.grid[i]]; sym = s[0]; fg = s[1]; bg = s[2] >= 0 ? s[2] : 0; }
    if (!note) { ctx.fillStyle = S.palette[bg]; ctx.fillRect(X, Y, ts, ts); }
    if (ts < 7) continue;
    if (note) {
      // map notes sit on top of the sprites: the letter with a dark outline, like the game's fallback glyphs
      const n = d.noteAt.get(i);
      if (n[5]) { ctx.strokeStyle = S.palette[1]; ctx.lineWidth = 2; ctx.strokeRect(X + 1, Y + 1, ts - 2, ts - 2); }
      ctx.lineWidth = Math.max(2, ts / 6); ctx.strokeStyle = 'rgba(0,0,0,.9)'; ctx.lineJoin = 'round';
      ctx.strokeText(sym, X + ts / 2, Y + ts / 2 + 0.5);
    }
    ctx.fillStyle = S.palette[fg] || S.palette[7]; ctx.fillText(sym, X + ts / 2, Y + ts / 2 + 0.5);
  }
}

// ------------------------------------------------------------------ highlight overlay (pins, selected result)
const ov = $('overlay');
const octx = ov.getContext('2d');
new ResizeObserver(() => {
  const r = ov.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  ov.width = Math.max(1, Math.round(r.width * dpr)); ov.height = Math.max(1, Math.round(r.height * dpr));
  drawOverlay();
}).observe(ov);

function selectedHit() {
  if (!S.selected) return null;
  const z = S.selected[2] ?? S.z;
  return S.hitTiles.get(`${S.selected[0]},${S.selected[1]},${z}`) || null;
}

function drawOverlay() {
  const dpr = window.devicePixelRatio || 1;
  octx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const v = view(), ts = S.ts;
  octx.clearRect(0, 0, v.w, v.h);
  const px = gx => (gx - v.x0) * ts, py = gy => (gy - v.y0) * ts;
  const gx0 = Math.floor(v.x0), gy0 = Math.floor(v.y0), gx1 = Math.ceil(v.x0 + v.w / ts), gy1 = Math.ceil(v.y0 + v.h / ts);
  const sel = selectedHit();
  const sans = getComputedStyle(document.body).getPropertyValue('--sans');

  // pins: yellow with a dark halo so they read on any terrain or tileset; quieter while one is selected
  octx.globalAlpha = sel ? 0.55 : 1;
  for (const t of S.hitTiles.values()) {
    if (t.z !== S.z || t.x < gx0 - 1 || t.x > gx1 || t.y < gy0 - 1 || t.y > gy1 || t === sel) continue;
    const X = px(t.x), Y = py(t.y);
    if (ts < 6) {
      octx.fillStyle = '#000'; octx.fillRect(X + ts / 2 - 3, Y + ts / 2 - 3, 6, 6);
      octx.fillStyle = '#ffd34d'; octx.fillRect(X + ts / 2 - 2, Y + ts / 2 - 2, 4, 4);
      continue;
    }
    octx.lineWidth = 4; octx.strokeStyle = 'rgba(0,0,0,.85)'; octx.strokeRect(X - 1, Y - 1, ts + 2, ts + 2);
    octx.lineWidth = 2; octx.strokeStyle = '#ffd34d'; octx.strokeRect(X - 1, Y - 1, ts + 2, ts + 2);
    if (ts >= 14 && t.qty > 1 && !sel) {
      octx.font = `bold 10px ${sans}`; octx.textAlign = 'center'; octx.textBaseline = 'middle';
      const label = fmtQty(t.qty), w = octx.measureText(label).width + 6;
      octx.fillStyle = '#ffd34d'; octx.fillRect(X + ts - w / 2, Y - 9, w, 12);
      octx.fillStyle = '#1a1305'; octx.fillText(label, X + ts, Y - 3);
    }
  }
  octx.globalAlpha = 1;

  if (!S.selected) return;
  const [sx, sy] = S.selected, sz = S.selected[2] ?? S.z;
  const X = px(sx), Y = py(sy), cx = X + ts / 2, cy = Y + ts / 2;
  // "on screen" leaves out the strips under the zoom buttons and the hover line, where it would be hidden
  const onScreen = cx >= 8 && cy >= 8 && cx <= v.w - 66 && cy <= v.h - 42;
  const COL = '#3de7ff';
  if (sz === S.z && onScreen) {
    if (sel) {
      // crosshair through the whole map and corner brackets
      octx.strokeStyle = 'rgba(61,231,255,.28)'; octx.lineWidth = 1;
      octx.beginPath(); octx.moveTo(0, cy + .5); octx.lineTo(v.w, cy + .5); octx.moveTo(cx + .5, 0); octx.lineTo(cx + .5, v.h); octx.stroke();
    }
    const g = Math.max(4, ts * 0.35), m = 3, a = X - m, b = Y - m, s2 = ts + m * 2;
    const brackets = () => {
      octx.beginPath();
      octx.moveTo(a, b + g); octx.lineTo(a, b); octx.lineTo(a + g, b);
      octx.moveTo(a + s2 - g, b); octx.lineTo(a + s2, b); octx.lineTo(a + s2, b + g);
      octx.moveTo(a + s2, b + s2 - g); octx.lineTo(a + s2, b + s2); octx.lineTo(a + s2 - g, b + s2);
      octx.moveTo(a + g, b + s2); octx.lineTo(a, b + s2); octx.lineTo(a, b + s2 - g);
      octx.stroke();
    };
    octx.lineCap = 'square';
    octx.lineWidth = sel ? 6 : 5; octx.strokeStyle = 'rgba(0,0,0,.85)'; brackets();
    octx.lineWidth = sel ? 3 : 2; octx.strokeStyle = sel ? COL : '#e8eef0'; brackets();
    octx.lineCap = 'butt';
    if (sel && ts >= 10) {
      octx.font = `bold 11px ${sans}`; octx.textAlign = 'left'; octx.textBaseline = 'middle';
      const label = sel.ids.length === 1 ? `${sel.ids[0].name || sel.ids[0].id} ×${fmtQty(sel.qty)}` : `${fmtQty(sel.qty)} of ${sel.ids.length} kinds`;
      const w = octx.measureText(label).width + 10, lx = Math.min(v.w - w - 4, a + s2 + 6), ly = Math.max(10, b - 2);
      octx.fillStyle = 'rgba(0,0,0,.85)'; octx.fillRect(lx, ly - 9, w, 18);
      octx.fillStyle = COL; octx.fillText(label, lx + 5, ly + .5);
    }
    return;
  }
  if (!sel) return;
  // on another level but in view: dashed brackets and a hint; off screen: an arrow at the map edge
  const sans2 = sans;
  const tag = (label, lx, ly) => {
    octx.font = `bold 11px ${sans2}`; octx.textAlign = 'center'; octx.textBaseline = 'middle';
    const w = octx.measureText(label).width + 12;
    lx = Math.max(w / 2 + 6, Math.min(v.w - w / 2 - 72, lx)); ly = Math.max(14, Math.min(v.h - 50, ly));
    octx.fillStyle = 'rgba(0,0,0,.88)'; octx.fillRect(lx - w / 2, ly - 9, w, 18);
    octx.fillStyle = COL; octx.fillText(label, lx, ly + .5);
  };
  const zHint = sz > S.z ? `up ${sz - S.z} (<)` : `down ${S.z - sz} (>)`;
  if (sz !== S.z && onScreen) {
    octx.setLineDash([4, 3]);
    octx.lineWidth = 4; octx.strokeStyle = 'rgba(0,0,0,.85)'; octx.strokeRect(X - 3, Y - 3, ts + 6, ts + 6);
    octx.lineWidth = 2; octx.strokeStyle = COL; octx.strokeRect(X - 3, Y - 3, ts + 6, ts + 6);
    octx.setLineDash([]);
    tag(`selected result is on z ${sz}: ${zHint}`, cx, Y - 16);
    return;
  }
  // keep clear of the map buttons (top), the zoom buttons (right) and the hover line (bottom)
  const L = 30, T = 104, R = v.w - 80, B = v.h - 64, mx = (L + R) / 2, my = (T + B) / 2;
  const ang = Math.atan2(cy - my, cx - mx), dx = Math.cos(ang), dy = Math.sin(ang);
  const kx = dx > 0 ? (R - mx) / dx : dx < 0 ? (L - mx) / dx : Infinity;
  const ky = dy > 0 ? (B - my) / dy : dy < 0 ? (T - my) / dy : Infinity;
  const k = Math.min(kx, ky), ax = mx + dx * k, ay = my + dy * k;
  octx.save(); octx.translate(ax, ay); octx.rotate(ang);
  octx.beginPath(); octx.moveTo(16, 0); octx.lineTo(-9, -12); octx.lineTo(-3, 0); octx.lineTo(-9, 12); octx.closePath();
  octx.lineWidth = 4; octx.lineJoin = 'round'; octx.strokeStyle = 'rgba(0,0,0,.85)'; octx.stroke(); octx.fillStyle = COL; octx.fill();
  octx.restore();
  const dist = Math.max(Math.abs(Math.floor(S.cx) - sx), Math.abs(Math.floor(S.cy) - sy));
  tag(sz !== S.z ? `selected result: ${dist} tiles, ${zHint}` : `selected result: ${dist} tiles`, ax - dx * 34, ay - dy * 26);
}

// ------------------------------------------------------------------ map interaction
function setZ(z) {
  z = Math.max(-10, Math.min(10, z));
  if (z === S.z) return;
  S.z = z;
  $('zLabel').textContent = 'z ' + z;
  if (S.selected) showTile(S.selected[0], S.selected[1]);
  requestDraw();
}
function centerOn(x, y, z) {
  S.cx = x + 0.5; S.cy = y + 0.5;
  if (z !== undefined) setZ(z);
  requestDraw();
}
function zoom(f, sx, sy) {
  const r = canvas.getBoundingClientRect();
  if (sx === undefined) { sx = r.width / 2; sy = r.height / 2; }
  const before = view();
  const wx = before.x0 + sx / S.ts, wy = before.y0 + sy / S.ts;
  S.ts = Math.max(1.5, Math.min(48, S.ts * f));
  S.cx = wx - (sx - r.width / 2) / S.ts; S.cy = wy - (sy - r.height / 2) / S.ts;
  requestDraw();
}

let drag = null;
canvas.addEventListener('pointerdown', e => {
  canvas.focus();
  drag = { x: e.clientX, y: e.clientY, cx: S.cx, cy: S.cy, moved: false };
  canvas.setPointerCapture(e.pointerId);
});
canvas.addEventListener('pointermove', e => {
  const r = canvas.getBoundingClientRect();
  if (drag) {
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 3) { drag.moved = true; canvas.classList.add('dragging'); }
    if (drag.moved) { S.cx = drag.cx - dx / S.ts; S.cy = drag.cy - dy / S.ts; requestDraw(); }
  }
  const [x, y] = screenToTile(e.clientX - r.left, e.clientY - r.top);
  showHover(x, y);
});
canvas.addEventListener('pointerup', e => {
  canvas.classList.remove('dragging');
  if (drag && !drag.moved) {
    const r = canvas.getBoundingClientRect();
    const [x, y] = screenToTile(e.clientX - r.left, e.clientY - r.top);
    S.selected = [x, y, S.z];
    showTile(x, y);
    requestDraw();
  }
  drag = null;
});
canvas.addEventListener('wheel', e => {
  e.preventDefault();
  const r = canvas.getBoundingClientRect();
  zoom(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX - r.left, e.clientY - r.top);
}, { passive: false });

function tileInfo(x, y) {
  const ox = fdiv(x, OM), oy = fdiv(y, OM);
  const d = overmapData(ox, oy);
  if (!d) return null;
  const i = (x - ox * OM) + (y - oy * OM) * OM;
  const s = d.styles[d.grid[i]];
  return { name: s[3], id: s[4], note: d.noteAt.get(i) };
}
function showHover(x, y) {
  const t = tileInfo(x, y);
  const SEP = '<span class="sep">|</span>';
  let h = `<b>${inGame(x, y)}</b>  z ${S.z}`;
  if (t) {
    h += SEP + esc(t.name) + (t.id ? ` <span class="id">${esc(t.id)}</span>` : '');
    if (t.note) h += SEP + `<span class="note">${esc(t.note[4])}</span>`;
  }
  const ht = S.hitTiles.get(`${x},${y},${S.z}`);
  if (ht) h += SEP + `<span class="hits">${ht.ids.map(d => esc(d.name || d.id) + ' ×' + fmtQty(d.qty) + (S.partsMode && !d.direct ? ' → ' + esc(d.yield) : '')).join(', ')}</span>`;
  $('hover').innerHTML = h;
}
function showTile(x, y) {
  const t = tileInfo(x, y);
  $('tcHead').textContent = `${inGame(x, y)}  z ${S.z}`;
  $('tcTer').innerHTML = t ? `${esc(t.name)}${t.id ? ` <span class="id">${esc(t.id)}</span>` : ''}` : '<span class="id">not generated / no data</span>';
  const note = $('tcNote');
  if (t && t.note) { note.textContent = (t.note[5] ? '⚠ ' : '') + t.note[4]; note.classList.remove('hidden'); } else note.classList.add('hidden');
  const ht = S.hitTiles.get(`${x},${y},${S.z}`);
  const hs = ht ? ht.hits : [];
  $('tcHits').innerHTML = hs.length ? hs.slice(0, 40).map(h => `<div class="row"><b title="${esc(h.id)}">${esc(h.name || h.id)}</b> ×${h.qty} <span class="kind">${h.loaded ? '<span class="loaded">loaded</span>' : esc(h.kind)}</span>${S.partsMode && h.yield && !h.direct ? ` <span class="yield">→ ${esc(h.yield)}</span>` : ''}<br><span class="muted">${h.detail ? esc(h.detail) + ', ' : ''}square ${esc(h.square)}</span></div>`).join('') + ((ht.count > 40) ? `<div class="row">…and ${ht.count - 40} more</div>` : '') : '';
  $('tileCard').classList.remove('hidden');
}
$('tileClose').onclick = () => { $('tileCard').classList.add('hidden'); S.selected = null; requestDraw(); };
$('tcFrom').onclick = () => {
  if (!S.selected) return;
  S.custom = [S.selected[0], S.selected[1]];
  S.from = 'custom';
  updateFromSeg();
  toast(`Distances now measured from ${inGame(S.custom[0], S.custom[1])}.`, 'ok');
  rerun();
};

$('zUp').onclick = () => setZ(S.z + 1);
$('zDown').onclick = () => setZ(S.z - 1);
$('zoomIn').onclick = () => zoom(1.4);
$('zoomOut').onclick = () => zoom(1 / 1.4);
function setMode(full) {
  S.full = full;
  $('modeFull').classList.toggle('on', full);
  $('modeFog').classList.toggle('on', !full);
  if (S.selected) showTile(S.selected[0], S.selected[1]);
  requestDraw();
}
$('modeFog').onclick = () => setMode(false);
$('modeFull').onclick = () => setMode(true);
$('goMe').onclick = () => { const c = S.status && S.status.character; if (c) centerOn(c.x, c.y, c.z); };
$('goBase').onclick = () => { const b = S.status && S.status.base; if (b) centerOn(b.x, b.y, 0); else toast('No base set. Put "base" in the settings file, like "0\'87, 0\'41".'); };

document.addEventListener('keydown', e => {
  if (e.target.matches('input, select, textarea') || !$('modal').classList.contains('hidden')) return;
  const step = 8 / Math.max(1, S.ts / 8);
  switch (e.key) {
    case '<': case 'PageUp': setZ(S.z + 1); break;
    case '>': case 'PageDown': setZ(S.z - 1); break;
    case '+': case '=': zoom(1.4); break;
    case '-': case '_': zoom(1 / 1.4); break;
    case 'f': case 'F': setMode(!S.full); break;
    case 'Home': $('goMe').click(); break;
    case 't': case 'T': setTileset(activeTileset() ? 'ascii' : (TS.last || '')); break;
    case '/': e.preventDefault(); showTab('find'); (S.mode === 'cat' ? $('catSel') : $('q')).focus(); break;
    case 'ArrowLeft': S.cx -= step; requestDraw(); break;
    case 'ArrowRight': S.cx += step; requestDraw(); break;
    case 'ArrowUp': S.cy -= step; requestDraw(); break;
    case 'ArrowDown': S.cy += step; requestDraw(); break;
    case 'Escape': $('tileClose').click(); break;
    default: return;
  }
  e.preventDefault();
});

// ------------------------------------------------------------------ tabs
function showTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('on', t.dataset.tab === name));
  document.querySelectorAll('.panel').forEach(p => p.classList.toggle('hidden', p.id !== 'panel-' + name));
  if (name === 'saves') loadSlots();
  if (name === 'status') renderSettings($('settingsBox'), false);
  if (name === 'codex' && !CX.loaded) { CX.loaded = true; codexSearch(); setTimeout(() => $('cxQ').focus(), 0); }
}
document.querySelectorAll('.tab').forEach(t => t.onclick = () => showTab(t.dataset.tab));
$('snapChip').onclick = () => showTab('status');

// ------------------------------------------------------------------ search
let lastQuery = '';
function updateFromSeg() {
  if (S.from === 'base' && S.status && !S.status.base && S.status.character) S.from = 'me';   // no base set yet
  document.querySelectorAll('#fromSeg button').forEach(b => b.classList.toggle('on', b.dataset.from === S.from));
  const c = $('fromCustom');
  c.disabled = !S.custom;
  c.textContent = S.custom ? inGame(S.custom[0], S.custom[1]) : 'Picked';
  c.title = 'Pick a tile on the map, then "Measure distances from here"';
  const st = S.status;
  document.querySelector('#fromSeg [data-from=base]').disabled = !(st && st.base);
  document.querySelector('#fromSeg [data-from=me]').disabled = !(st && st.character);
}
document.querySelectorAll('#fromSeg button').forEach(b => b.onclick = () => {
  if (b.disabled) return;
  S.from = b.dataset.from; updateFromSeg();
  rerun();
});

function zParam() {
  const v = $('zSel').value;
  if (v === 'cur') return String(S.z);
  if (v === 'above') return '1,2,3,4,5,6,7,8,9,10';
  if (v === 'below') return '-1,-2,-3,-4,-5,-6,-7,-8,-9,-10';
  return v;
}
function fromParam() {
  if (S.from === 'custom' && S.custom) return S.custom.join(',');
  return S.from;
}

$('searchForm').addEventListener('submit', e => { e.preventDefault(); if (S.mode === 'cat') { S.onlySet = null; runCatSearch(); } else runSearch($('q').value); });
$('idsOnly').onchange = () => { rerun(); };
$('partsOn').onchange = () => { rerun(); };
$('zSel').onchange = () => { rerun(); };
document.querySelectorAll('#sortSeg button').forEach(b => b.onclick = () => {
  S.sort = b.dataset.sort;
  document.querySelectorAll('#sortSeg button').forEach(x => x.classList.toggle('on', x === b));
  rerun();
});

const fmtQty = n => n >= 10000 ? (n / 1000).toFixed(n >= 100000 ? 0 : 1) + 'k' : n.toLocaleString();

let searchSeq = 0;
async function runSearch(q) {
  q = q.trim(); if (!q) return;
  lastQuery = q;
  const seq = ++searchSeq;
  const idsOnly = $('idsOnly').checked, parts = $('partsOn').checked;
  $('searchSummary').textContent = 'Searching…';
  $('results').innerHTML = '';
  $('sourcesBox').classList.add('hidden');
  const r = await api(`/api/search?q=${encodeURIComponent(q)}&z=${encodeURIComponent(zParam())}&from=${encodeURIComponent(fromParam())}&ids=${idsOnly ? 1 : 0}&sort=${S.sort}&src=${parts ? 1 : 0}`);
  if (seq !== searchSeq) return;
  if (r.error) { $('searchSummary').textContent = r.error; setTiles([]); return; }
  S.partsMode = !!r.parts;
  const stats = `${r.parsed.toLocaleString()} of ${r.files.toLocaleString()} chunks parsed, ${r.seconds}s`;
  if (r.parts) renderSources(r);
  if (idsOnly) {
    setTiles([]);
    $('searchSummary').textContent = `${r.ids.length} matching id${r.ids.length === 1 ? '' : 's'}\n${stats}`;
    const gives = r.parts ? '<th class="num">Gives</th>' : '';
    $('results').innerHTML = r.ids.length ? `<table class="ids"><tr><th>Id</th><th>Kind</th><th class="num">Places</th><th class="num">Qty</th>${gives}<th class="num">Nearest</th></tr>` +
      r.ids.map(i => `<tr class="click" data-id="${esc(i.id)}"><td>${esc(i.id)}</td><td>${esc(i.kind)}</td><td class="num">${i.places}</td><td class="num">${i.qty.toLocaleString()}</td>${r.parts ? `<td class="num">${esc(i.yield)}</td>` : ''}<td class="num">${i.nearest ?? ''}</td></tr>`).join('') + '</table>' : '';
    $('results').querySelectorAll('tr.click').forEach(tr => tr.onclick = () => { $('idsOnly').checked = false; $('partsOn').checked = false; $('q').value = tr.dataset.id; runSearch(tr.dataset.id); });
    return;
  }
  setTiles(r.tiles);
  const shown = r.tiles.length < r.tileCount ? ` (first ${r.tiles.length.toLocaleString()} shown)` : '';
  if (r.parts) {
    const what = r.targetCount === 1 ? r.targets[0] : `${r.targetCount} matching items`;
    $('searchSummary').textContent = r.total
      ? `${r.yield} ${what} obtainable in ${r.tileCount.toLocaleString()} place${r.tileCount === 1 ? '' : 's'}${shown}\n${stats}`
      : `Nothing in the explored map has or gives "${r.query}"\n${stats}`;
  } else {
    $('searchSummary').textContent = r.total
      ? `${r.qtyTotal.toLocaleString()} total in ${r.tileCount.toLocaleString()} place${r.tileCount === 1 ? '' : 's'}${shown}\n${stats}`
      : `Nothing found for "${r.query}" in the explored map\n${stats}`;
  }
  renderTiles(0);
}

function renderSources(r) {
  const box = $('sourcesBox');
  if (!r.sourceCount) { box.classList.add('hidden'); return; }
  $('sourcesSum').textContent = `Comes from ${r.sourceCount} thing${r.sourceCount === 1 ? '' : 's'} (per one taken apart)`;
  $('sourcesList').innerHTML = r.sources.map(s =>
    `<div>${esc(s.id)} <span class="k">${s.kind === 'item' ? 'disassemble' : 'deconstruct ' + esc(s.kind)}</span>${s.via ? ` <span class="via">→ ${esc(s.via)} →</span>` : ''}</div><div class="n">${esc(s.yield)}</div>`).join('') +
    (r.sourceCount > r.sources.length ? `<div class="k">…and ${r.sourceCount - r.sources.length} more</div><div></div>` : '');
  box.classList.remove('hidden');
}

function setTiles(tiles) {
  S.tiles = tiles;
  S.hitTiles = new Map();
  for (const t of tiles) S.hitTiles.set(`${t.x},${t.y},${t.z}`, t);
  requestDraw();
}

function renderTiles(start) {
  const box = $('results');
  const old = box.querySelector('.more'); if (old) old.remove();
  const end = Math.min(S.tiles.length, start + 300);
  const frag = document.createDocumentFragment();
  for (let i = start; i < end; i++) {
    const t = S.tiles[i];
    const el = document.createElement('div');
    el.className = 'hit';
    const line = d => `<span title="${esc(d.id)}">${esc(d.name || d.id)}</span> <span class="qty">×${fmtQty(d.qty)}</span>` +
      (d.loaded ? ` <span class="loaded">${d.loaded === d.qty ? 'loaded' : fmtQty(d.loaded) + ' loaded'}</span>` : '') +
      (S.partsMode && !d.direct ? ` <span class="yield">→ ${esc(d.yield)}</span>${d.via ? ` <span class="via">(via ${esc(d.via)})</span>` : ''}` : '');
    const ids = t.ids.slice(0, 3).map(line).join('<br>') +
      (t.ids.length > 3 ? `<br><span class="more-ids">+${t.ids.length - 3} more ids</span>` : '');
    const kinds = t.sets ? t.sets.map(x => `${x.name} ${fmtQty(x.qty)}`).join(', ') : [...new Set(t.ids.map(d => d.kind))].join(', ');
    const where = t.count === 1 && t.hits[0].detail ? `<span>${esc(t.hits[0].detail)}</span>` : (t.count > 1 ? `<span>${t.count} stacks</span>` : '');
    el.innerHTML = `<div class="dist">${t.dist !== undefined ? t.dist + ' ' + esc(t.dir) : ''}<small>z ${t.z}</small></div>` +
      `<div class="ids">${ids}</div>` +
      `<div class="where"><span class="kind">${esc(kinds)}</span><span>${esc(t.inGame)}</span>${where}</div>`;
    el.onclick = () => {
      box.querySelectorAll('.hit.sel').forEach(x => x.classList.remove('sel'));
      el.classList.add('sel');
      centerOn(t.x, t.y, t.z);
      if (S.ts < 12) { S.ts = 16; }
      S.selected = [t.x, t.y, t.z]; showTile(t.x, t.y); requestDraw();
    };
    frag.appendChild(el);
  }
  box.appendChild(frag);
  if (end < S.tiles.length) {
    const more = document.createElement('button');
    more.className = 'more'; more.textContent = `Show more (${(S.tiles.length - end).toLocaleString()} left)`;
    more.onclick = () => renderTiles(end);
    box.appendChild(more);
  }
}


// ------------------------------------------------------------------ category search
S.mode = 'id'; S.cat = 'ammo'; S.onlySet = null; S.catSearched = false; S.catInfo = null;
try { const m = localStorage.getItem('findMode'); if (m === 'cat') S.mode = 'cat'; S.cat = localStorage.getItem('findCat') || 'ammo'; } catch { }

function rerun() {
  if (!$('catEditor').classList.contains('hidden')) return;
  if (S.mode === 'cat') { if (S.catSearched) runCatSearch(); }
  else if (lastQuery) runSearch(lastQuery);
}

function setFindMode(mode) {
  S.mode = mode;
  try { localStorage.setItem('findMode', mode); } catch { }
  document.querySelectorAll('#modeSeg button').forEach(b => b.classList.toggle('on', b.dataset.mode === mode));
  const cat = mode === 'cat';
  $('q').classList.toggle('hidden', cat);
  $('catSel').classList.toggle('hidden', !cat);
  $('catLine').classList.toggle('hidden', !cat);
  $('partsLbl').classList.toggle('hidden', cat);
  $('idsText').textContent = cat ? 'summary' : 'ids only';
  $('idsLbl').title = cat ? 'One row per ammo type (or per item) with totals for the whole map' : '';
  closeCatEditor(false);
  setTiles([]); $('results').innerHTML = ''; $('sourcesBox').classList.add('hidden');
  $('searchSummary').textContent = cat
    ? 'Pick a category and search. Ammo counts rounds, including what\'s loaded in magazines and guns.'
    : 'Items (also in bags and vehicles), furniture, terrain, vehicles, traps, fields, spawns. * is a wildcard.';
  if (cat) loadCategories();
}
document.querySelectorAll('#modeSeg button').forEach(b => b.onclick = () => setFindMode(b.dataset.mode));

async function loadCategories() {
  const r = await api('/api/categories');
  if (r.error) { $('catInfo').textContent = r.error; setTimeout(() => { if (S.mode === 'cat') loadCategories(); }, 1500); return; }
  const sel = $('catSel');
  if (!r.categories.some(c => c.id === S.cat)) S.cat = r.categories.find(c => c.id === 'ammo')?.id || r.categories[0]?.id;
  sel.innerHTML = r.categories.map(c => `<option value="${esc(c.id)}">${esc(c.name)} (${c.count})</option>`).join('');
  sel.value = S.cat;
  await loadCatInfo();
}
$('catSel').onchange = async () => {
  S.cat = $('catSel').value; S.onlySet = null;
  try { localStorage.setItem('findCat', S.cat); } catch { }
  closeCatEditor(false);
  await loadCatInfo();
  if (S.catSearched) runCatSearch();
};

function presetName(c) {
  if (!c.excludedItems.length && !c.excludedSets.length) return 'everything';
  if (c.split && !c.excludedItems.length && c.firearmSets.length) {
    const fa = new Set(c.firearmSets);
    const allSets = c.groups.flatMap(g => g.sets.map(x => x.id));
    const ex = new Set(c.excludedSets);
    if (allSets.every(id => fa.has(id) ? !ex.has(id) : ex.has(id))) return 'firearms only';
  }
  return 'your filter';
}
async function loadCatInfo() {
  const c = await api(`/api/category?cat=${encodeURIComponent(S.cat)}`);
  if (c.error) { $('catInfo').textContent = c.error; return; }
  S.catInfo = c;
  showCatInfo();
}
function showCatInfo() {
  const c = S.catInfo; if (!c) return;
  $('catInfo').innerHTML = `${c.included === c.count ? 'All' : `<b>${c.included}</b> of`} ${c.count} kinds, ${esc(presetName(c))}` +
    (S.onlySet ? `, only <b>${esc(S.onlySetName || S.onlySet)}</b> <button type="button" class="linkbtn" id="clearOnly">show all</button>` : '');
  if ($('clearOnly')) $('clearOnly').onclick = () => { S.onlySet = null; showCatInfo(); runCatSearch(); };
}

let catSeq = 0;
async function runCatSearch() {
  if (!S.cat) return;
  S.catSearched = true;
  const seq = ++catSeq, summary = $('idsOnly').checked;
  $('searchSummary').textContent = 'Searching…';
  $('results').innerHTML = ''; $('sourcesBox').classList.add('hidden');
  S.partsMode = false;
  const r = await api(`/api/search?cat=${encodeURIComponent(S.cat)}${S.onlySet ? '&set=' + encodeURIComponent(S.onlySet) : ''}&z=${encodeURIComponent(zParam())}&from=${encodeURIComponent(fromParam())}&ids=${summary ? 1 : 0}&sort=${S.sort}`);
  if (seq !== catSeq) return;
  if (r.error) { $('searchSummary').textContent = r.error; setTiles([]); return; }
  const stats = `${r.parsed.toLocaleString()} of ${r.files.toLocaleString()} chunks parsed, ${r.seconds}s`;
  const what = r.setName ? r.setName : r.catName.toLowerCase();
  const loaded = r.loadedTotal ? ` (${r.loadedTotal.toLocaleString()} loaded)` : '';
  if (summary) {
    setTiles([]);
    $('searchSummary').textContent = r.total ? `${r.qtyTotal.toLocaleString()} ${what}${loaded}\n${stats}` : `No ${what} in the explored map\n${stats}`;
    const isAmmo = r.sets.some(x => x.id !== r.cat);
    $('results').innerHTML = r.sets.length ? `<table class="ids sum"><tr><th>${isAmmo ? 'Type' : 'Item'}</th><th class="num">Places</th><th class="num">Total</th><th class="num">Loaded</th><th class="num">Nearest</th></tr>` +
      r.sets.map(x => `<tr class="click" data-set="${esc(x.id)}" data-name="${esc(x.name)}" title="${esc(x.items.map(i => `${i[1]} ×${i[2]}`).join('\n'))}"><td class="nm">${esc(x.name)}<div class="sub">${esc(x.items.slice(0, 3).map(i => i[1]).join(', '))}${x.items.length > 3 ? ` +${x.items.length - 3}` : ''}</div></td><td class="num">${x.places}</td><td class="num">${x.qty.toLocaleString()}</td><td class="num">${x.loaded ? x.loaded.toLocaleString() : ''}</td><td class="num">${x.nearest ?? ''}</td></tr>`).join('') + '</table>' : '';
    $('results').querySelectorAll('tr.click').forEach(tr => tr.onclick = () => {
      $('idsOnly').checked = false;
      if (tr.dataset.set !== r.cat) { S.onlySet = tr.dataset.set; S.onlySetName = tr.dataset.name; }
      showCatInfo(); runCatSearch();
    });
    return;
  }
  setTiles(r.tiles);
  const shown = r.tiles.length < r.tileCount ? ` (first ${r.tiles.length.toLocaleString()} shown)` : '';
  $('searchSummary').textContent = r.total
    ? `${r.qtyTotal.toLocaleString()} ${what}${loaded} in ${r.tileCount.toLocaleString()} place${r.tileCount === 1 ? '' : 's'}${shown}\n${stats}`
    : `No ${what} in the explored map\n${stats}`;
  renderTiles(0);
}

// ------------------------------------------------------------------ category filter editor
let ED = null;   // { c, sets:Set, items:Set }
$('catEdit').onclick = () => { if ($('catEditor').classList.contains('hidden')) openCatEditor(); else closeCatEditor(false); };

function closeCatEditor(rerunAfter) {
  ED = null;
  $('catEditor').classList.add('hidden'); $('catEditor').innerHTML = '';
  $('results').classList.remove('hidden');
  $('catEdit').textContent = 'Edit filter';
  if (rerunAfter && S.catSearched) runCatSearch();
}

function openCatEditor() {
  const c = S.catInfo; if (!c) return;
  ED = { c, sets: new Set(c.excludedSets), items: new Set(c.excludedItems) };
  $('results').classList.add('hidden');
  $('catEdit').textContent = 'Close filter';
  const flat = !c.split;
  let h = `<div class="ce-head"><b>${esc(c.name)}</b>: tick what to search for.</div><div class="ce-presets">` +
    (c.split && c.firearmSets.length ? `<button type="button" data-p="fa">Firearms only</button>` : '') +
    `<button type="button" data-p="all">Everything</button><button type="button" data-p="none">Nothing</button>` +
    (c.saved ? `<button type="button" data-p="reset" title="Forget your changes for this category">Default</button>` : '') + `</div>` +
    `<input type="search" id="ceFind" placeholder="Filter the list, e.g. 9x19 or pebble" spellcheck="false">` +
    `<div class="ce-list">`;
  c.groups.forEach((g, gi) => {
    const items = g.sets.reduce((n, x) => n + x.items.length, 0);
    h += `<div class="ce-g" data-gi="${gi}"><label class="ce-gh"><input type="checkbox" data-g="${gi}"> <span>${esc(g.name)}</span> <span class="n" data-gn="${gi}"></span></label>`;
    g.sets.forEach((x, si) => {
      const key = `${gi}.${si}`;
      const itemsHtml = x.items.map(([id, nm]) => `<label class="ce-i" data-text="${esc((nm + ' ' + id).toLowerCase())}"><input type="checkbox" data-i="${esc(id)}" data-k="${key}"> ${esc(nm)} <code>${esc(id)}</code></label>`).join('');
      if (flat) { h += `<div class="ce-items flat" data-k="${key}">${itemsHtml}</div>`; return; }
      h += `<div class="ce-s" data-k="${key}" data-text="${esc(x.name.toLowerCase())}"><label><input type="checkbox" data-s="${esc(x.id)}" data-k="${key}"> ${esc(x.name)}</label> <span class="n" data-sn="${key}"></span><button type="button" class="exp" data-k="${key}" aria-label="Show items">▸</button></div>` +
           `<div class="ce-items hidden" data-k="${key}">${itemsHtml}</div>`;
    });
    h += `</div>`;
  });
  h += `</div><div class="ce-foot"><span id="ceCount"></span><button type="button" id="ceCancel">Cancel</button><button type="button" class="primary" id="ceDone">Save and search</button></div>`;
  const box = $('catEditor');
  box.innerHTML = h; box.classList.remove('hidden');
  paintEditor();

  box.querySelectorAll('[data-p]').forEach(b => b.onclick = async () => {
    const all = c.groups.flatMap(g => g.sets.map(x => x.id));
    if (b.dataset.p === 'reset') {
      const r = await api('/api/category/filter', { cat: c.id, reset: true });
      if (!r.error) { S.catInfo = r; showCatInfo(); openCatEditor(); }
      return;
    }
    ED.items.clear();
    ED.sets = new Set(b.dataset.p === 'all' ? [] : b.dataset.p === 'none' ? all : all.filter(id => !c.firearmSets.includes(id)));
    paintEditor();
  });
  box.querySelectorAll('input[data-g]').forEach(cb => cb.onchange = () => {
    const g = c.groups[+cb.dataset.g];
    for (const x of g.sets) {
      if (cb.checked) { ED.sets.delete(x.id); for (const [id] of x.items) ED.items.delete(id); }
      else ED.sets.add(x.id);
    }
    paintEditor();
  });
  box.querySelectorAll('input[data-s]').forEach(cb => cb.onchange = () => {
    const [gi, si] = cb.dataset.k.split('.').map(Number), x = c.groups[gi].sets[si];
    if (cb.checked) { ED.sets.delete(x.id); for (const [id] of x.items) ED.items.delete(id); } else ED.sets.add(x.id);
    paintEditor();
  });
  box.querySelectorAll('input[data-i]').forEach(cb => cb.onchange = () => {
    const [gi, si] = cb.dataset.k.split('.').map(Number), x = c.groups[gi].sets[si];
    if (cb.checked) { ED.items.delete(cb.dataset.i); if (ED.sets.has(x.id)) { ED.sets.delete(x.id); for (const [id] of x.items) if (id !== cb.dataset.i) ED.items.add(id); } }
    else ED.items.add(cb.dataset.i);
    paintEditor();
  });
  box.querySelectorAll('button.exp').forEach(b => b.onclick = () => {
    const it = box.querySelector(`.ce-items[data-k="${b.dataset.k}"]`);
    it.classList.toggle('hidden'); b.textContent = it.classList.contains('hidden') ? '▸' : '▾';
  });
  $('ceFind').addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
  $('ceFind').oninput = () => filterEditor($('ceFind').value.trim().toLowerCase());
  $('ceCancel').onclick = () => closeCatEditor(false);
  $('ceDone').onclick = async () => {
    const r = await api('/api/category/filter', { cat: c.id, sets: [...ED.sets], items: [...ED.items] });
    if (r.error) { toast(r.error, 'err'); return; }
    S.catInfo = r; S.onlySet = null; showCatInfo();
    closeCatEditor(false);
    runCatSearch();
  };
}

function paintEditor() {
  if (!ED) return;
  const c = ED.c, box = $('catEditor');
  let total = 0, on = 0;
  c.groups.forEach((g, gi) => {
    let gOn = 0, gAll = 0, setsOn = 0;
    g.sets.forEach((x, si) => {
      const key = `${gi}.${si}`, setOff = ED.sets.has(x.id);
      let n = 0;
      for (const [id] of x.items) if (!setOff && !ED.items.has(id)) n++;
      gOn += n; gAll += x.items.length; if (!setOff) setsOn++;
      const scb = box.querySelector(`input[data-s][data-k="${key}"]`);
      if (scb) { scb.checked = !setOff; scb.indeterminate = !setOff && n < x.items.length; }
      const sn = box.querySelector(`[data-sn="${key}"]`);
      if (sn) sn.textContent = n === x.items.length ? `${n}` : `${n}/${x.items.length}`;
      box.querySelectorAll(`input[data-i][data-k="${key}"]`).forEach(cb => { cb.checked = !setOff && !ED.items.has(cb.dataset.i); });
    });
    const gcb = box.querySelector(`input[data-g="${gi}"]`);
    gcb.checked = gOn === gAll && gAll > 0; gcb.indeterminate = gOn > 0 && gOn < gAll;
    box.querySelector(`[data-gn="${gi}"]`).textContent = `${gOn}/${gAll}`;
    total += gAll; on += gOn;
  });
  $('ceCount').textContent = `${on} of ${total} kinds`;
  $('ceDone').disabled = on === 0;
}

function filterEditor(q) {
  const box = $('catEditor');
  box.querySelectorAll('.ce-g').forEach(g => {
    let any = false;
    g.querySelectorAll('.ce-items').forEach(list => {
      const key = list.dataset.k, row = box.querySelector(`.ce-s[data-k="${key}"]`);
      const setHit = !q || (row && row.dataset.text.includes(q));
      let itemHit = false;
      list.querySelectorAll('.ce-i').forEach(i => { const hit = !q || setHit || i.dataset.text.includes(q); i.classList.toggle('hidden', !hit); if (q && i.dataset.text.includes(q)) itemHit = true; });
      const show = setHit || itemHit;
      if (row) { row.classList.toggle('hidden', !show); if (!list.classList.contains('flat')) { list.classList.toggle('hidden', !(q && itemHit && !setHit)); const b = row.querySelector('.exp'); b.textContent = list.classList.contains('hidden') ? '▸' : '▾'; } }
      if (show) any = true;
    });
    g.classList.toggle('hidden', !any);
  });
}


// ------------------------------------------------------------------ setup and settings
let setupShown = false;
function showSetup() {
  if (setupShown) return;
  setupShown = true;
  $('setup').classList.remove('hidden');
  renderSettings($('setupForm'), true);
}

/** The same form serves the first-run screen (setup = true) and the Settings tab. */
async function renderSettings(box, setup, gameDir) {
  const scan = await api('/api/setup/scan' + (gameDir !== undefined ? '?gameDir=' + encodeURIComponent(gameDir) : ''));
  if (scan.error) { box.innerHTML = `<div class="problem">${esc(scan.error)}</div>`; return; }
  const cur = scan.current;
  const sameGame = scan.gameDir === cur.gameDir;
  let world = sameGame && scan.worlds.some(w => w.name === cur.world) ? cur.world : (scan.worlds.find(w => w.character) || scan.worlds[0] || {}).name;
  const others = scan.detected.filter(d => d !== scan.gameDir);
  let h = `<label class="f" for="sfGame">Game folder <span class="note">(the one with data/ and gfx/ inside)</span></label>
    <div class="row"><input type="text" id="sfGame" spellcheck="false" value="${esc(scan.gameDir)}" placeholder="${esc(scan.exeDir)}"><button type="button" id="sfCheck">Check</button></div>`;
  if (others.length) h += `<div class="found">Also found: ${others.map(d => `<button type="button" data-dir="${esc(d)}" title="${esc(d)}">${esc(d)}</button>`).join('')}</div>`;
  if (scan.problem) h += `<div class="problem">${esc(scan.problem)}</div>`;
  if (scan.worlds.length) {
    h += `<label class="f">World <span class="note">in ${esc(scan.saveRoot)}</span></label><div class="worlds">` +
      scan.worlds.map(w => `<div class="world${w.name === world ? ' on' : ''}" data-world="${esc(w.name)}" tabindex="0"><span class="nm">${esc(w.name)}</span>` +
        `<span class="meta">${w.character ? esc(w.character) : 'no character yet'}, played ${ago(w.lastPlayed)}${w.compressed ? '' : ', uncompressed'}</span></div>`).join('') + `</div>`;
  }
  h += `<label class="f" for="sfBase">Base <span class="note">(optional)</span></label>
    <div class="row"><input type="text" id="sfBase" spellcheck="false" value="${esc(sameGame ? (scan.bases[world] || '') : '')}" placeholder="e.g. 0'87, 0'41, as the game's map shows it"></div>
    <div class="note">Search distances are measured from here. You can also click a tile on the map later and pick <b>Set as base</b>.</div>`;
  if (!setup) {
    h += `<details><summary>More settings</summary><div class="adv">
      <label class="f" for="sfData">Data folder <span class="note">(snapshot and save slots; same drive as the game makes loading fast)</span></label>
      <div class="row"><input type="text" id="sfData" spellcheck="false" value="${esc(cur.dataDir)}" placeholder="${esc(cur.dataRoot)}"></div>
      <label class="f" for="sfSlots">Save slots</label><div class="row"><input type="number" id="sfSlots" min="1" max="20" value="${cur.slots}"></div>
      <label class="f" for="sfIdle">Quit by itself after this many minutes with no page open <span class="note">(0 = never)</span></label>
      <div class="row"><input type="number" id="sfIdle" min="0" max="1440" value="${cur.idleExitMinutes}"></div>
      <label class="check"><input type="checkbox" id="sfAuto"${cur.autoRefreshSnapshot ? ' checked' : ''}> Refresh the search snapshot after each game save</label>
      <div class="note">Settings file: <span class="mono">${esc(scan.configPath)}</span></div>
    </div></details>`;
  }
  h += `<div class="go"><button type="button" class="primary" id="sfSave"${scan.worlds.length ? '' : ' disabled'}>${setup ? 'Start' : 'Save'}</button><span class="msg" id="sfMsg"></span></div>`;
  box.innerHTML = h;

  const q = sel => box.querySelector(sel);
  q('#sfCheck').onclick = () => renderSettings(box, setup, q('#sfGame').value);
  q('#sfGame').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); renderSettings(box, setup, q('#sfGame').value); } });
  box.querySelectorAll('.found button').forEach(b => b.onclick = () => renderSettings(box, setup, b.dataset.dir));
  box.querySelectorAll('.world').forEach(el => {
    const pick = () => {
      world = el.dataset.world; box.querySelectorAll('.world').forEach(x => x.classList.toggle('on', x === el));
      q('#sfBase').value = sameGame ? (scan.bases[world] || '') : '';   // each world has its own base
    };
    el.onclick = pick;
    el.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); } };
  });
  q('#sfSave').onclick = async () => {
    const body = { gameDir: q('#sfGame').value, world: world || '', base: q('#sfBase').value };
    if (!setup) {
      body.dataDir = q('#sfData').value;
      body.slots = +q('#sfSlots').value || 5;
      body.idleExitMinutes = Math.max(0, +q('#sfIdle').value || 0);
      body.autoRefreshSnapshot = q('#sfAuto').checked;
    }
    q('#sfSave').disabled = true; q('#sfMsg').className = 'msg'; q('#sfMsg').textContent = 'Saving…';
    const r = await api('/api/settings', body);
    if (r.error) { q('#sfMsg').className = 'msg err'; q('#sfMsg').textContent = r.error; q('#sfSave').disabled = false; return; }
    q('#sfMsg').textContent = 'Saved. Loading…';
    setTimeout(() => location.reload(), 300);
  };
}

$('tcBase').onclick = async () => {
  if (!S.selected) return;
  const label = inGame(S.selected[0], S.selected[1]);
  const r = await api('/api/settings/base', { base: label });
  if (r.error) { toast(r.error, 'err'); return; }
  toast(`Base set to ${label}.`, 'ok');
  if (!$('panel-status').classList.contains('hidden')) renderSettings($('settingsBox'), false);
  S.from = 'base';
  const st = await api('/api/status'); if (!st.error) applyStatus(st);
  rerun();
};

// ------------------------------------------------------------------ codex
const KIND_LABEL = { item: 'item', furniture: 'furniture', terrain: 'terrain', vehicle: 'vehicle', vehicle_part: 'vehicle part' };
const CX = { kind: '', seq: 0, stack: [], timer: 0 };

// colours are set through the DOM afterwards: the page's CSP doesn't allow inline style attributes
function glyph(e) { return `<span class="glyph" data-fg="${e.fg}" data-bg="${e.bg}">${esc(e.sym)}</span>`; }
function paintGlyphs(root) {
  root.querySelectorAll('.glyph[data-fg]').forEach(g => {
    const fg = +g.dataset.fg, bg = +g.dataset.bg;
    g.style.color = S.palette[fg] || S.palette[7];
    if (bg >= 0 && S.palette[bg]) g.style.background = S.palette[bg];
    delete g.dataset.fg;
  });
}

async function codexSearch() {
  const q = $('cxQ').value, seq = ++CX.seq;
  const r = await api(`/api/codex/search?q=${encodeURIComponent(q)}&kind=${CX.kind}`);
  if (seq !== CX.seq) return;
  if (r.error) { $('cxSummary').textContent = r.error; $('cxList').innerHTML = ''; return; }
  const total = Object.values(r.counts).reduce((a, b) => a + b, 0);
  if (!q.trim()) $('cxSummary').textContent = `${total.toLocaleString()} things: ${Object.entries(r.counts).map(([k, n]) => `${n.toLocaleString()} ${KIND_LABEL[k]}${k === 'furniture' || k === 'terrain' ? '' : 's'}`).join(', ')}.`;
  else $('cxSummary').textContent = r.results.length ? (r.results.length >= 200 ? 'First 200 matches. Type more to narrow it down.' : `${r.results.length} match${r.results.length === 1 ? '' : 'es'}.`) : `Nothing called "${q.trim()}".`;
  $('cxList').innerHTML = r.results.map(e =>
    `<div class="cxrow" data-kind="${esc(e.kind)}" data-id="${esc(e.id)}">${glyph(e)}<div class="nm">${esc(e.name)}<div class="sub"><span class="id">${esc(e.id)}</span><span class="kindlbl">${KIND_LABEL[e.kind]}</span>${e.mod ? `<span>${esc(e.mod)}</span>` : ''}</div></div></div>`).join('');
  paintGlyphs($('cxList'));
  $('cxList').querySelectorAll('.cxrow').forEach(el => el.onclick = () => { CX.stack = []; openCodex(el.dataset.kind, el.dataset.id); });
}
$('cxQ').addEventListener('input', () => { clearTimeout(CX.timer); CX.timer = setTimeout(codexSearch, 140); closeCodexCard(); });
$('cxForm').addEventListener('submit', e => { e.preventDefault(); clearTimeout(CX.timer); closeCodexCard(); codexSearch(); });
document.querySelectorAll('#cxKinds button').forEach(b => b.onclick = () => {
  CX.kind = b.dataset.kind;
  document.querySelectorAll('#cxKinds button').forEach(x => x.classList.toggle('on', x === b));
  closeCodexCard(); codexSearch();
});

function closeCodexCard() {
  $('cxCard').classList.add('hidden'); $('cxList').classList.remove('hidden');
  CX.stack = [];
}

function cxLink(kind, id, name, known) {
  return known ? `<a href="#" class="cxlink" data-kind="${esc(kind)}" data-id="${esc(id)}">${esc(name)}</a>` : `<span>${esc(name)}</span>`;
}

async function openCodex(kind, id, back) {
  const r = await api(`/api/codex/entry?kind=${encodeURIComponent(kind)}&id=${encodeURIComponent(id)}`);
  if (r.error) { toast(r.error, 'err'); return; }
  if (!back) CX.stack.push([kind, id]);
  const card = $('cxCard');
  const facts = r.facts.length ? `<div class="kv">${r.facts.map(([l, v]) => `<div class="k">${esc(l)}</div><div class="v">${esc(v)}</div>`).join('')}</div>` : '';
  let h = `<div class="cxnav"><button id="cxBack">${CX.stack.length > 1 ? 'Back' : 'Back to results'}</button></div>`;
  h += `<div class="cxhead">${glyph(r)}<div><div class="cxname">${esc(r.name)}</div><div class="sub"><span class="id">${esc(r.id)}</span><span class="kindlbl">${KIND_LABEL[r.kind]}</span><span>${esc(r.mod)}</span></div></div></div>`;
  h += `<div class="cxfind"><button class="primary" id="cxFind">Find in my world</button>` +
       (r.kind === 'item' && r.sourceCount ? `<button id="cxFindParts" title="Also everything that gives it when taken apart">Find, including parts</button>` : '') + `</div>`;
  if (r.description) h += `<p class="cxdesc">${esc(r.description)}</p>`;
  h += facts;
  if (r.item) h += `<div class="cxsec"><h4>Installed from</h4>${cxLink(r.item.kind, r.item.id, r.item.name, r.item.known)}</div>`;
  if (r.parts.length) h += `<div class="cxsec"><h4>Parts (${r.parts.length} kinds)</h4><div class="cxgrid">${r.parts.map(p => `<div>${cxLink(p.kind, p.id, p.name, p.known)}</div><div class="n">${esc(p.count)}</div>`).join('')}</div></div>`;
  if (r.givesParts.length) h += `<div class="cxsec"><h4>${r.kind === 'item' ? 'Disassembles into' : 'Deconstructs into'}</h4><div class="cxgrid">${r.givesParts.map(p => `<div>${cxLink('item', p.id, p.name, p.known)}${p.more && r.kind !== 'item' ? ' <span class="via">(takes apart further)</span>' : ''}</div><div class="n">${esc(p.yield)}</div>`).join('')}</div></div>`;
  if (r.comesFrom.length) h += `<details class="cxsec" ${r.sourceCount <= 12 ? 'open' : ''}><summary><h4>Comes from ${r.sourceCount} thing${r.sourceCount === 1 ? '' : 's'}</h4></summary><div class="cxgrid">${r.comesFrom.map(s => `<div>${cxLink(s.kind, s.id, s.name, s.known)} <span class="k">${s.kind === 'item' ? 'disassemble' : 'deconstruct'}</span>${s.via ? ` <span class="via">via ${esc(s.via)}</span>` : ''}</div><div class="n">${esc(s.yield)}</div>`).join('')}${r.sourceCount > r.comesFrom.length ? `<div class="k">…and ${r.sourceCount - r.comesFrom.length} more</div><div></div>` : ''}</div></details>`;
  if (r.flags.length) h += `<details class="cxsec"><summary><h4>Flags (${r.flags.length})</h4></summary><div class="cxflags">${r.flags.map(f => `<code>${esc(f)}</code>`).join(' ')}</div></details>`;
  card.innerHTML = h;
  paintGlyphs(card);
  card.classList.remove('hidden'); $('cxList').classList.add('hidden');
  $('panel-codex').scrollTop = 0;
  $('cxBack').onclick = () => {
    CX.stack.pop();
    const prev = CX.stack[CX.stack.length - 1];
    if (prev) openCodex(prev[0], prev[1], true); else closeCodexCard();
  };
  card.querySelectorAll('.cxlink').forEach(a => a.onclick = ev => { ev.preventDefault(); openCodex(a.dataset.kind, a.dataset.id); });
  $('cxFind').onclick = () => findInWorld(r.id, false);
  if ($('cxFindParts')) $('cxFindParts').onclick = () => findInWorld(r.id, true);
}

function findInWorld(id, parts) {
  if (S.mode === 'cat') setFindMode('id');
  $('q').value = id;
  $('idsOnly').checked = false;
  $('partsOn').checked = parts;
  showTab('find');
  runSearch(id);
}

// ------------------------------------------------------------------ saves
async function loadSlots() {
  const r = await api('/api/slots');
  if (r.error) { $('slots').textContent = r.error; return; }
  const busy = S.status && S.status.job.running;
  $('slots').innerHTML = '';
  for (const s of r.slots) {
    const undo = s.id === 'undo';
    const el = document.createElement('div');
    el.className = 'slot' + (s.exists ? '' : ' empty') + (undo ? ' undo' : '');
    const title = undo ? 'Undo' : 'Slot ' + s.id;
    el.innerHTML = `<div class="title"><b>${title}</b><span class="meta">${s.exists ? fmtBytes(s.bytes) : ''}</span></div>` +
      (s.exists
        ? `<div class="label">${esc(s.gameDate || 'unknown date')}</div>${undo && s.label ? `<div class="meta">${esc(s.label)}</div>` : ''}<div class="meta">saved ${esc(localTime(s.created))}${s.character ? ', ' + esc(s.character) : ''}</div>`
        : `<div class="meta">${undo ? 'Your save from before the last load goes here.' : 'empty'}</div>`) +
      `<div class="actions"></div>`;
    const act = el.querySelector('.actions');
    const btn = (text, cls, fn) => { const b = document.createElement('button'); b.textContent = text; if (cls) b.className = cls; b.disabled = busy; b.onclick = fn; act.appendChild(b); return b; };
    if (!undo) btn(s.exists ? 'Overwrite' : 'Save here', '', () => saveSlot(s));
    if (s.exists) btn('Load', '', () => restoreSlot(s));
    if (s.exists) btn(undo ? 'Clear' : 'Delete', 'danger', () => deleteSlot(s));
    $('slots').appendChild(el);
  }
  renderDisk();
}

async function saveSlot(s) {
  const r = await api('/api/slots/backup', { slot: s.id });
  if (!r.started) toast(r.error || 'Could not start', 'err'); else { toast('Saving to slot ' + s.id + '…'); pollSoon(); }
}

async function restoreSlot(s) {
  const what = s.id === 'undo' ? 'the Undo slot' : `slot ${s.id}`;
  const ok = await confirmModal(`Load ${what}?`,
    `<p>This replaces your current save with ${what}${s.gameDate ? ': <b class="nb">' + esc(s.gameDate) + '.</b>' : '.'}</p>` +
    `<p>Make sure CDDA is <b>closed or at the main menu</b>.</p><p class="small">Your current save is kept in the Undo slot, so you can switch back.</p>`, 'Load');
  if (!ok) return;
  const r = await api('/api/slots/restore', { slot: s.id });
  if (!r.started) toast(r.error || 'Could not start', 'err'); else { toast('Loading ' + what + '…'); pollSoon(); }
}

async function deleteSlot(s) {
  const what = s.id === 'undo' ? 'the Undo slot' : `slot ${s.id}`;
  const ok = await confirmModal(`Delete ${what}?`, `<p>${what[0].toUpperCase() + what.slice(1)}${s.gameDate ? ' <span class="nb">(' + esc(s.gameDate) + ')</span>' : ''} will be gone for good.</p>`, 'Delete');
  if (!ok) return;
  const r = await api('/api/slots/delete', { slot: s.id });
  if (!r.started) toast(r.error || 'Could not start', 'err'); else pollSoon();
}

function renderDisk() {
  const st = S.status; if (!st) return;
  const parts = [];
  if (st.compressed) parts.push(`snapshot ${fmtBytes(st.snapshot.bytes)}`);
  parts.push(`backups ${fmtBytes(st.disk.backups)}`);
  if (st.disk.free !== undefined) parts.push(`free on drive ${fmtBytes(st.disk.free)}`);
  $('diskLine').textContent = 'Disk: ' + parts.join(', ');
}

$('refreshSnap').onclick = async () => {
  const r = await api('/api/snapshot/refresh', {});
  if (!r.started) toast(r.error || 'Could not start', 'err'); else pollSoon();
};

let soon;
function pollSoon() { clearTimeout(soon); soon = setTimeout(async () => { const st = await api('/api/status'); if (!st.error) applyStatus(st); }, 400); }

// ------------------------------------------------------------------ go
$('hover').textContent = 'Point at a tile to see what it is. Click it for details.';
pollStatus();

if (S.mode === 'cat') setFindMode('cat');
loadTilesets();
