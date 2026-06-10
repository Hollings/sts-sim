// StS2 Sim — single-file frontend.
// Loads the current run's deck from the embedded server, lets you mark card
// removals/additions (A/B test) or pick reward candidates (compare mode),
// kicks off sim batches, and live-streams progress over WebSocket while
// drawing charts. Phases are dynamic: 1 (single), 2 (A/B), or 1+N (compare).

// ─── Tiny utilities ──────────────────────────────────────────────────────

const $ = id => document.getElementById(id);
const fmt = n => Number.isFinite(n) ? n.toFixed(2) : '—';
const sgn = n => (n >= 0 ? '+' : '');

const HTML_ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
const escapeHtml = s => String(s).replace(/[&<>"']/g, c => HTML_ESCAPES[c]);

const lerp = (a, b, t) => Math.round(a + (b - a) * t);
const minMax = arr => arr.reduce(
  ([lo, hi], v) => [v < lo ? v : lo, v > hi ? v : hi],
  [Infinity, -Infinity]
);

// Color a damage value on a red→yellow→green gradient relative to the run's range.
function damageColor(y, min, max) {
  if (max === min) return '#d4a142';
  const t = (y - min) / (max - min); // 0 = worst, 1 = best
  // bad (red 8b1913) → mid (yellow d4a142) → good (green 5fb04f)
  if (t < 0.5) {
    const u = t * 2;
    return `rgb(${lerp(0x8b, 0xd4, u)},${lerp(0x19, 0xa1, u)},${lerp(0x13, 0x42, u)})`;
  }
  const u = (t - 0.5) * 2;
  return `rgb(${lerp(0xd4, 0x5f, u)},${lerp(0xa1, 0xb0, u)},${lerp(0x42, 0x4f, u)})`;
}

// Baseline is always slot 0 (muted blue); candidates/variant take the rest.
const PHASE_COLORS = ['#6da4c4', '#d4a142', '#5fb04f', '#c75450', '#9d7bd8', '#e0884f', '#5fc4b8', '#c45f9e'];

// ─── DOM helpers (UI state mutations are repeated everywhere) ────────────

function setVerdict(cls, text) {
  const v = $('verdict');
  v.className = `verdict ${cls}`;
  v.textContent = text;
}

function setRunning(running) {
  $('run-btn').disabled = running;
  $('stop-btn').disabled = !running;
}

const statTile = (label, value) =>
  `<div class="stat"><span class="label">${label}</span><span class="value">${value}</span></div>`;

const renderStats = tiles => { $('stats').innerHTML = tiles.join(''); };

// ─── Charts (dynamic per-phase datasets) ─────────────────────────────────

let bestChart, avgChart, histChart;
let mode = 'single';            // 'single' | 'ab' | 'compare'
let metric = 'damage';          // 'damage' (dummy) | 'score' (encounter fight)
let phaseDefs = [];             // [{key, label, color}] — index 0 is baseline
let phaseIndexByKey = {};
const seriesByPhase = new Map(); // key -> {best, avg, ciUpper, ciLower}
let runningMean = null;          // single mode: horizontal reference line

function initCharts() {
  Chart.defaults.color = '#f2f0c4';
  Chart.defaults.borderColor = 'rgba(33,69,90,0.6)';

  // Plugin to draw a horizontal reference line at the running mean.
  const meanLinePlugin = {
    id: 'meanLine',
    afterDatasetsDraw(chart) {
      if (runningMean == null) return;
      const { ctx, chartArea: { left, right }, scales: { y } } = chart;
      const yPx = y.getPixelForValue(runningMean);
      if (yPx < y.top || yPx > y.bottom) return;
      ctx.save();
      ctx.strokeStyle = 'rgba(242,240,196,0.5)';
      ctx.setLineDash([4, 4]);
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(left, yPx);
      ctx.lineTo(right, yPx);
      ctx.stroke();
      ctx.fillStyle = 'rgba(242,240,196,0.7)';
      ctx.font = '10px system-ui';
      ctx.textAlign = 'right';
      ctx.fillText(`mean ${runningMean.toFixed(1)}`, right - 4, yPx - 4);
      ctx.restore();
    },
  };

  const legendOpts = { display: false, labels: { boxWidth: 10, boxHeight: 10, font: { size: 11 } } };

  bestChart = new Chart($('chart-best'), {
    type: 'scatter',
    data: { datasets: [] },
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'seed index' }, ticks: { precision: 0 } },
        y: { title: { display: true, text: 'best damage' }, beginAtZero: false, grace: '5%' },
      },
      plugins: {
        legend: { ...legendOpts },
        tooltip: { callbacks: { label: c => `${c.dataset.label} seed ${c.parsed.x}: ${c.parsed.y} dmg` } },
      },
    },
    plugins: [meanLinePlugin],
  });

  avgChart = new Chart($('chart-avg'), {
    // scatter with showLine: keeps x-axis numeric (line type defaults to category,
    // which squashes everything to the left as new points come in).
    type: 'scatter',
    data: { datasets: [] },
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'seed index' }, ticks: { precision: 0 } },
        y: { title: { display: true, text: 'avg-of-best damage' }, beginAtZero: false, grace: '5%' },
      },
      plugins: { legend: { ...legendOpts } },
    },
  });

  histChart = new Chart($('chart-hist'), {
    type: 'bar',
    data: { labels: [], datasets: [] },
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'damage bucket (per-seed best)' }, grid: { display: false } },
        y: { title: { display: true, text: 'seeds' }, beginAtZero: true, ticks: { precision: 0 } },
      },
      plugins: { legend: { ...legendOpts } },
    },
  });

  configurePhases('single', [{ key: 'base', label: 'current deck' }]);
}

// Rebuild every chart's datasets for a new phase layout. Called on `started`.
function configurePhases(newMode, defs) {
  mode = newMode;
  phaseDefs = defs.map((d, i) => ({ ...d, color: PHASE_COLORS[i % PHASE_COLORS.length] }));
  phaseIndexByKey = Object.fromEntries(phaseDefs.map((d, i) => [d.key, i]));
  seriesByPhase.clear();
  for (const d of phaseDefs)
    seriesByPhase.set(d.key, { best: [], avg: [], ciUpper: [], ciLower: [] });
  runningMean = null;

  const multi = phaseDefs.length > 1;

  bestChart.data.datasets = phaseDefs.map((d, i) => ({
    label: d.label, data: [],
    pointBackgroundColor: d.color, pointBorderColor: '#0f2733', pointBorderWidth: 1,
    radius: multi ? 4 : 5, hoverRadius: 7,
    pointStyle: i === 0 ? 'circle' : 'triangle',
  }));
  bestChart.options.plugins.legend.display = multi;

  // CI band datasets first (only drawn in single mode), then one line per phase.
  avgChart.data.datasets = [
    { label: 'CI upper', data: [], borderColor: 'rgba(212,161,66,0.4)', borderWidth: 1, fill: '+1', backgroundColor: 'rgba(212,161,66,0.12)', pointRadius: 0, showLine: true },
    { label: 'CI lower', data: [], borderColor: 'rgba(212,161,66,0.4)', borderWidth: 1, fill: false, pointRadius: 0, showLine: true },
    ...phaseDefs.map(d => ({
      label: d.label, data: [], borderColor: d.color, borderWidth: 2,
      fill: false, pointRadius: 0, showLine: true, tension: 0.15,
    })),
  ];
  avgChart.options.plugins.legend.display = multi;
  avgChart.options.plugins.legend.labels.filter = item => item.text !== 'CI upper' && item.text !== 'CI lower';

  histChart.data.labels = [];
  histChart.data.datasets = phaseDefs.map(d => ({ label: d.label, data: [], backgroundColor: d.color + 'cc' }));
  histChart.options.plugins.legend.display = multi;

  bestChart.update('none'); avgChart.update('none'); histChart.update('none');
}

function updateCharts() {
  const single = phaseDefs.length === 1;
  const allYs = phaseDefs.flatMap(d => seriesByPhase.get(d.key).best.map(p => p.y));
  const [min, max] = allYs.length ? minMax(allYs) : [0, 0];

  phaseDefs.forEach((d, i) => {
    const s = seriesByPhase.get(d.key);
    bestChart.data.datasets[i].data = s.best;
    // Single mode keeps the damage-gradient dots; multi-phase uses solid
    // per-phase colors so the clouds are distinguishable.
    bestChart.data.datasets[i].pointBackgroundColor = single
      ? s.best.map(p => damageColor(p.y, min, max))
      : d.color;
    avgChart.data.datasets[2 + i].data = s.avg;
  });
  const baseSeries = seriesByPhase.get(phaseDefs[0].key);
  avgChart.data.datasets[0].data = single ? baseSeries.ciUpper : [];
  avgChart.data.datasets[1].data = single ? baseSeries.ciLower : [];
  bestChart.update('none');
  avgChart.update('none');

  // Histogram: shared buckets across all phases so the bars line up.
  if (allYs.length > 1 && max > min) {
    const buckets = 12;
    const w = (max - min) / buckets;
    const labels = [];
    const gradientColors = [];
    for (let i = 0; i < buckets; i++) {
      labels.push(`${Math.round(min + i * w)}`);
      gradientColors.push(damageColor(min + (i + 0.5) * w, min, max));
    }
    const countUp = ys => {
      const counts = new Array(buckets).fill(0);
      for (const y of ys) counts[Math.min(buckets - 1, Math.floor((y - min) / w))]++;
      return counts;
    };
    histChart.data.labels = labels;
    phaseDefs.forEach((d, i) => {
      const ys = seriesByPhase.get(d.key).best.map(p => p.y);
      histChart.data.datasets[i].data = countUp(ys);
      histChart.data.datasets[i].backgroundColor = single ? gradientColors : d.color + 'cc';
    });
    histChart.update('none');
  }
}

// Coalesce chart redraws into a single rAF tick — Chart.js updates are
// expensive once you have 200+ datapoints; doing them per-event makes
// big runs visibly stutter. We just buffer and let the browser repaint.
let chartsDirty = false;
let bestPending = null;
function tick() {
  if (chartsDirty) { updateCharts(); chartsDirty = false; }
  if (bestPending) { renderBestCombat(bestPending); bestPending = null; }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

// ─── Deck editor state ───────────────────────────────────────────────────

let deckData = null;          // last /api/deck payload
let cardCatalog = [];         // /api/cards entries for the pickers
// Pending A/B changes. Keys are `${id}|${upgrade}`.
const removals = new Map();   // key -> count to remove
let additions = [];           // [{ id, upgrade, count, name }]
let candidates = [];          // compare mode: [{ id, upgrade, name }]

const changeKey = (id, upgrade) => `${id}|${upgrade}`;

function hasChanges() { return removals.size > 0 || additions.length > 0; }

function clearChanges() {
  removals.clear();
  additions = [];
  candidates = [];
  renderDeckEditor();
}

function changesPayload() {
  return {
    removals: [...removals.entries()].map(([key, count]) => {
      const [id, upgrade] = key.split('|');
      return { id, upgrade: +upgrade, count };
    }),
    additions: additions.map(a => ({ id: a.id, upgrade: a.upgrade, count: a.count })),
    candidates: candidates.map(c => ({ id: c.id, upgrade: c.upgrade })),
  };
}

function describeChanges() {
  const parts = [];
  for (const [key, count] of removals) {
    const [id, upgrade] = key.split('|');
    const g = deckData?.cardsGrouped.find(c => c.id === id && c.upgrade === +upgrade);
    parts.push(`−${g ? g.name : id}${count > 1 ? ` ×${count}` : ''}`);
  }
  for (const a of additions)
    parts.push(`+${a.name}${a.count > 1 ? ` ×${a.count}` : ''}`);
  return parts.join(' · ');
}

function lookupCatalogEntry(name) {
  const q = name.trim().toLowerCase();
  if (!q) return null;
  return cardCatalog.find(c => c.name.toLowerCase() === q)
      || cardCatalog.find(c => c.name.toLowerCase().startsWith(q));
}

function renderDeckEditor() {
  if (!deckData) return;

  $('deck-cards').innerHTML = deckData.cardsGrouped.map((c, i) => {
    const key = changeKey(c.id, c.upgrade);
    const removing = removals.get(key) || 0;
    const tag = removing > 0 ? `<span class="removed-tag">−${removing}</span>` : '';
    return `<li class="editable ${removing > 0 ? 'removing' : ''}" data-idx="${i}">
      <span>${escapeHtml(c.name)}${tag}</span><span class="count">×${c.count}</span></li>`;
  }).join('')
  + additions.map((a, i) =>
      `<li class="editable added" data-add="${i}" title="Click to un-add">
        <span>+ ${escapeHtml(a.name)}</span><span class="count">×${a.count}</span></li>`
    ).join('');

  const bar = $('changes-bar');
  if (hasChanges()) {
    bar.classList.remove('hidden');
    $('changes-text').textContent = describeChanges();
  } else {
    bar.classList.add('hidden');
  }

  $('cand-list').innerHTML = candidates.map((c, i) =>
    `<li class="editable cand" data-cand="${i}" title="Click to remove">
      <span>${escapeHtml(c.name)}</span><span class="cand-vs">vs</span></li>`).join('');

  $('run-btn').textContent = candidates.length > 0
    ? `Compare ${candidates.length} Card${candidates.length > 1 ? 's' : ''}`
    : hasChanges() ? 'Run A/B Sim' : 'Run Sim';
}

$('deck-cards').addEventListener('click', ev => {
  const li = ev.target.closest('li');
  if (!li || $('run-btn').disabled) return;

  if (li.dataset.add !== undefined) {
    // Click a pending addition: decrement, drop at 0.
    const i = +li.dataset.add;
    if (--additions[i].count <= 0) additions.splice(i, 1);
    renderDeckEditor();
    return;
  }
  if (li.dataset.idx !== undefined) {
    // Click a deck card: cycle removal count 0 → 1 → … → count → 0.
    const c = deckData.cardsGrouped[+li.dataset.idx];
    const key = changeKey(c.id, c.upgrade);
    const next = (removals.get(key) || 0) + 1;
    if (next > c.count) removals.delete(key);
    else removals.set(key, next);
    renderDeckEditor();
  }
});

$('cand-list').addEventListener('click', ev => {
  const li = ev.target.closest('li');
  if (!li || li.dataset.cand === undefined || $('run-btn').disabled) return;
  candidates.splice(+li.dataset.cand, 1);
  renderDeckEditor();
});

$('changes-clear').onclick = clearChanges;

$('add-card-btn').onclick = () => {
  const input = $('add-card-input');
  const entry = lookupCatalogEntry(input.value);
  if (!entry) {
    if (input.value.trim()) setVerdict('error', `Unknown card "${input.value.trim()}" — pick one from the list.`);
    return;
  }
  const upgrade = $('add-card-upgraded').checked ? 1 : 0;
  const display = entry.name + (upgrade ? '+' : '');
  const existing = additions.find(a => a.id === entry.id && a.upgrade === upgrade);
  if (existing) existing.count++;
  else additions.push({ id: entry.id, upgrade, count: 1, name: display });
  input.value = '';
  renderDeckEditor();
};
$('add-card-input').addEventListener('keydown', ev => {
  if (ev.key === 'Enter') $('add-card-btn').click();
});

$('cand-add-btn').onclick = () => {
  const input = $('cand-input');
  const entry = lookupCatalogEntry(input.value);
  if (!entry) {
    if (input.value.trim()) setVerdict('error', `Unknown card "${input.value.trim()}" — pick one from the list.`);
    return;
  }
  if (candidates.length >= 8) {
    setVerdict('error', 'At most 8 candidates per compare run.');
    return;
  }
  const upgrade = $('cand-upgraded').checked ? 1 : 0;
  candidates.push({ id: entry.id, upgrade, name: entry.name + (upgrade ? '+' : '') });
  input.value = '';
  renderDeckEditor();
};
$('cand-input').addEventListener('keydown', ev => {
  if (ev.key === 'Enter') $('cand-add-btn').click();
});

async function loadEncounters() {
  try {
    const r = await fetch('/api/encounters');
    if (!r.ok) return;
    const d = await r.json();
    const sel = $('cfg-encounter');
    const saved = localStorage.getItem('sts2sim.cfg-encounter') || '';
    // Group by act + room type for a readable dropdown.
    const groups = new Map();
    for (const e of d.encounters) {
      const key = `${e.act} — ${e.roomType === 'Monster' ? 'Normal' : e.roomType}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(e);
    }
    for (const [label, encounters] of groups) {
      const og = document.createElement('optgroup');
      og.label = label;
      for (const e of encounters) {
        const opt = document.createElement('option');
        opt.value = e.id;
        opt.textContent = e.name;
        og.appendChild(opt);
      }
      sel.appendChild(og);
    }
    if (saved && [...sel.options].some(o => o.value === saved)) sel.value = saved;
  } catch { /* dropdown just stays dummy-only */ }
}

$('cfg-encounter').addEventListener('change', () => {
  localStorage.setItem('sts2sim.cfg-encounter', $('cfg-encounter').value);
  const fight = $('cfg-encounter').value !== '';
  $('cfg-turns-label').textContent = fight ? 'Max turns (cap = loss)' : 'Turns / combat';
  // Sensible turn budget when toggling between modes (only adjust the value
  // when it still looks like the other mode's default).
  const turns = +$('cfg-turns').value;
  if (fight && turns <= 10) $('cfg-turns').value = 30;
  if (!fight && turns >= 20) $('cfg-turns').value = 5;
  localStorage.setItem('sts2sim.cfg-turns', $('cfg-turns').value);
});

async function loadCardCatalog() {
  try {
    const r = await fetch('/api/cards');
    if (!r.ok) return;
    const d = await r.json();
    cardCatalog = d.cards;
    $('card-options').innerHTML = cardCatalog
      .map(c => `<option value="${escapeHtml(c.name)}">${escapeHtml(`${c.cost}⚡ ${c.cardType} · ${c.rarity}`)}</option>`)
      .join('');
  } catch { /* pickers just stay empty */ }
}

// ─── Deck UI ─────────────────────────────────────────────────────────────

async function loadDeck() {
  $('run-info').innerHTML = '<div class="empty">Loading…</div>';
  try {
    const r = await fetch('/api/deck');
    if (!r.ok) {
      $('run-info').innerHTML = `<div class="empty">No save file found. Start a run in StS2 first.</div>`;
      return;
    }
    const d = await r.json();
    deckData = d;
    clearChanges(); // deck changed on disk — stale edits make no sense
    $('run-info').innerHTML = `
      <div class="row"><span>Character</span><span>${escapeHtml(d.characterPretty)}</span></div>
      <div class="row"><span>HP</span><span>${d.currentHp} / ${d.maxHp}</span></div>
      <div class="row"><span>Gold</span><span>${d.gold}</span></div>
      <div class="row"><span>Save</span><span>${escapeHtml(d.modified)}</span></div>`;
    $('deck-info').innerHTML = `<div class="row"><span>Total cards</span><span><b>${d.deckSize}</b></span></div>`;
    $('relics').innerHTML = d.relics.length
      ? d.relics.map(r => `<li><span>${escapeHtml(r.name)}</span></li>`).join('')
      : '<li class="empty">none</li>';
    renderDeckEditor();
    loadCardCatalog();
  } catch (e) {
    $('run-info').innerHTML = `<div class="empty">Error: ${escapeHtml(e.message)}</div>`;
  }
}

// ─── WebSocket events ────────────────────────────────────────────────────

let ws;
let wsRetries = 0;
function connectWs() {
  ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onopen = () => {
    wsRetries = 0;
    if ($('verdict').className.includes('error')) {
      setVerdict('idle', 'Reconnected. Click "Run Sim" to start.');
    }
  };
  ws.onmessage = ev => handleEvent(JSON.parse(ev.data));
  ws.onclose = () => {
    wsRetries++;
    if (wsRetries > 3) {
      setVerdict('error', 'Lost server connection. Restart StS2Sim.exe and reload this page.');
    }
    setTimeout(connectWs, Math.min(wsRetries * 1000, 5000));
  };
}

const phaseDoneByKey = new Map(); // phase key -> phaseDone payload

// Event handlers, one per `e.type` — dispatched from handleEvent below.
// Wire-shape contract: these `type` strings + field names match Server/SimJob.cs Broadcast calls.
const eventHandlers = {
  started(e) {
    phaseDoneByKey.clear();
    metric = e.metric || 'damage';
    const defs = [{ key: 'base', label: 'current deck' }];
    if (e.mode === 'ab') defs.push({ key: 'variant', label: e.changeSummary || 'variant' });
    if (e.mode === 'compare') e.candidates.forEach((label, i) => defs.push({ key: `cand${i}`, label }));
    configurePhases(e.mode || 'single', defs);

    // Metric-aware axis labels: damage ceiling vs fight outcome score.
    const yLabel = metric === 'score' ? 'outcome (+HP kept / −boss HP)' : 'best damage';
    bestChart.options.scales.y.title.text = yLabel;
    avgChart.options.scales.y.title.text = metric === 'score' ? 'avg outcome score' : 'avg-of-best damage';

    $('ab-panel').className = 'ab-panel hidden';
    $('best-combat').innerHTML = '<div class="empty">Searching for the best combat…</div>';
    $('best-headline').textContent = '';
    const vsTxt = metric === 'score' ? ` vs ${e.encounterName}` : '';
    const cfg = `${e.seeds} seeds × K=${e.k}${e.patience ? ` (patience ${e.patience})` : ''}, ${e.turns} turns, ε=${e.epsilon}`;
    const head = e.mode === 'compare'
      ? `Compare${vsTxt}: ${e.candidates.join(' vs ')}${e.changeSummary ? ` (on top of ${e.changeSummary})` : ''}`
      : e.mode === 'ab' ? `A/B${vsTxt}: ${e.changeSummary || 'deck edit'}`
      : `Running${vsTxt}`;
    setVerdict('running', `${head} — ${cfg}`);
    $('status').textContent = phaseDefs.length > 1 ? `Phase 1/${phaseDefs.length}: current deck…` : 'Started…';
    $('prog').style.width = '0%';
    setRunning(true);
  },

  // Don't re-render synchronously — coalesce into next rAF.
  newBest(e) { bestPending = e; },

  seed(e) {
    const s = seriesByPhase.get(e.phase);
    if (!s) return;
    s.best.push({ x: e.index, y: e.bestForSeed });
    s.avg.push({ x: e.index, y: e.runningAvg });
    s.ciUpper.push({ x: e.index, y: e.runningAvg + e.ci95 });
    s.ciLower.push({ x: e.index, y: e.runningAvg - e.ci95 });
    if (mode === 'single') runningMean = e.runningAvg;

    // Progress: each phase is an equal slice of the bar.
    const phaseIdx = phaseIndexByKey[e.phase] ?? 0;
    const phaseCount = phaseDefs.length;
    const frac = (e.index + 1) / e.total;
    $('prog').style.width = ((phaseIdx + frac) / phaseCount) * 100 + '%';
    // Cheap text updates can stay per-event.
    const runsPerSec = (e.totalRuns / Math.max(1, e.elapsedMs / 1000)).toFixed(0);
    const phaseTxt = phaseCount > 1
      ? `Phase ${phaseIdx + 1}/${phaseCount} (${phaseDefs[phaseIdx].label}) · ` : '';
    $('status').textContent = `${phaseTxt}Seed ${e.index + 1} / ${e.total} · ${e.totalRuns} runs · ${runsPerSec} runs/s`;
    // Mark charts dirty; the rAF tick will redraw at most once per frame.
    chartsDirty = true;
    // Stat tiles get updated less often (once per ~10 seeds is fine).
    if (e.index % 10 === 0 || e.index + 1 === e.total) {
      const tiles = [];
      const baseDone = phaseDoneByKey.get('base');
      if (baseDone && e.phase !== 'base')
        tiles.push(statTile('Current deck avg-of-best', `${fmt(baseDone.avgOfBest)} ± ${fmt(baseDone.ci95)}`));
      tiles.push(
        statTile(`Running avg-of-best${phaseCount > 1 ? ` (${phaseDefs[phaseIdx].label})` : ''}`, `${fmt(e.runningAvg)} ± ${fmt(e.ci95)}`),
        statTile('Last seed best', e.bestForSeed),
      );
      if (metric === 'score' && e.winnableSeeds != null)
        tiles.push(statTile('Winnable seeds', `${e.winnableSeeds} / ${e.index + 1} (${(100 * e.winnableSeeds / (e.index + 1)).toFixed(0)}%)`));
      tiles.push(statTile('Total runs', e.totalRuns));
      renderStats(tiles);
    }
  },

  phaseDone(e) {
    phaseDoneByKey.set(e.phase, e);
    const phaseIdx = phaseIndexByKey[e.phase] ?? 0;
    const next = phaseDefs[phaseIdx + 1];
    if (next)
      $('status').textContent = `${e.label}: ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)} · starting ${next.label}…`;
  },

  done(e) {
    updateCharts();
    setVerdict('done', metric === 'score' && e.winRate != null
      ? `Done: ${(e.winRate * 100).toFixed(0)}% of seeds winnable · avg outcome ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)}`
      : `Done: ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)} avg-of-best (${fmt(e.avgPerTurn)}/turn)`);
    $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs · best seed: ${e.bestOfBest}, worst seed: ${e.worstSeedBest}`;
    const tiles = [
      statTile('Avg-of-best', `${fmt(e.avgOfBest)} ± ${fmt(e.ci95)}`),
    ];
    if (metric === 'score' && e.winRate != null)
      tiles.push(statTile('Winnable seeds', `${e.winnableSeeds} (${(e.winRate * 100).toFixed(0)}%)`));
    else
      tiles.push(statTile('Per turn', fmt(e.avgPerTurn)));
    tiles.push(
      statTile('Best / worst seed', `${e.bestOfBest} / ${e.worstSeedBest}`),
      statTile('Convergence (median K)', e.medianConvergenceK),
    );
    renderStats(tiles);
    setRunning(false);
  },

  abDone(e) {
    updateCharts();
    setVerdict('done', `A/B done: lift ${sgn(e.lift)}${fmt(e.lift)} ± ${fmt(e.liftCi95)} dmg`);
    $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs total (paired over ${e.pairedSeeds} seeds)`;

    const panel = $('ab-panel');
    panel.className = `ab-panel ${e.verdictClass}`;
    $('ab-verdict-line').textContent = e.verdict;
    const winTxt = wr => wr != null ? ` · ${(wr * 100).toFixed(0)}% win` : '';
    $('ab-detail').innerHTML = [
      ['Change', e.changeSummary || '—'],
      ['Baseline', `${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}${winTxt(e.baseWinRate)}`],
      ['Variant', `${fmt(e.variantAvg)} ± ${fmt(e.variantCi95)}${winTxt(e.variantWinRate)}`],
      ['Lift', `${sgn(e.lift)}${fmt(e.lift)} ± ${fmt(e.liftCi95)}${metric === 'score' ? '' : ` (${sgn(e.lift)}${fmt(e.liftPerTurn)}/turn)`}`],
      ['z-score (paired)', fmt(e.z)],
    ].map(([label, value]) =>
      `<div><span class="label">${escapeHtml(label)}</span><span class="value">${escapeHtml(String(value))}</span></div>`
    ).join('');

    renderStats([
      statTile('Baseline avg-of-best', `${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}`),
      statTile('Variant avg-of-best', `${fmt(e.variantAvg)} ± ${fmt(e.variantCi95)}`),
      statTile('Lift', `${sgn(e.lift)}${fmt(e.lift)} ± ${fmt(e.liftCi95)}`),
      statTile('z (paired)', fmt(e.z)),
    ]);
    setRunning(false);
  },

  compareDone(e) {
    updateCharts();
    setVerdict('done', `Compare done: best is ${e.winnerLabel}`);
    $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs total (paired over ${e.pairedSeeds} seeds)`;

    const panel = $('ab-panel');
    panel.className = `ab-panel ${e.verdictClass}`;
    $('ab-verdict-line').textContent = e.verdict;

    // Ranking table: candidates by lift vs current deck, then the skip row.
    const fightMode = metric === 'score';
    const winCell = wr => fightMode ? `<td>${wr != null ? (wr * 100).toFixed(0) + '%' : '—'}</td>` : '';
    const colorOf = label => phaseDefs.find(d => d.label === label)?.color || '#888';
    const rows = e.results.map((r, i) => `
      <tr class="${i === 0 && r.beatsBase ? 'winner' : ''}">
        <td>${i + 1}</td>
        <td><span class="swatch" style="background:${colorOf(r.label)}"></span>${escapeHtml(r.label)}</td>
        <td>${fmt(r.avg)} ± ${fmt(r.ci95)}</td>
        ${winCell(r.winRate)}
        <td>${sgn(r.lift)}${fmt(r.lift)} ± ${fmt(r.liftCi95)}</td>
        <td>${fmt(r.z)}</td>
        <td>${r.beatsBase ? '<span class="chip good">beats deck</span>' : r.z < -1.96 ? '<span class="chip bad">worse</span>' : '<span class="chip">within noise</span>'}</td>
      </tr>`).join('');
    const skipRow = `
      <tr class="skip-row">
        <td>—</td>
        <td><span class="swatch" style="background:${colorOf('current deck')}"></span>skip (current deck)</td>
        <td>${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}</td>
        ${winCell(e.baseWinRate)}
        <td>0</td><td>—</td><td></td>
      </tr>`;
    const wvr = e.winnerVsRunnerUp;
    const wvrLine = wvr
      ? `<div class="wvr">${escapeHtml(wvr.winnerLabel)} vs ${escapeHtml(wvr.runnerUpLabel)}: ${sgn(wvr.lift)}${fmt(wvr.lift)} ± ${fmt(wvr.liftCi95)} (z=${fmt(wvr.z)}${wvr.z > 1.96 ? ', decisive' : ', too close to call'})</div>`
      : '';
    $('ab-detail').innerHTML = `
      <table class="rank-table">
        <thead><tr><th>#</th><th>Card</th><th>avg-of-best</th>${fightMode ? '<th>win %</th>' : ''}<th>lift vs current</th><th>z</th><th></th></tr></thead>
        <tbody>${rows}${skipRow}</tbody>
      </table>${wvrLine}`;

    renderStats([
      statTile('Winner', e.winnerLabel),
      statTile('Winner lift', `${sgn(e.results[0].lift)}${fmt(e.results[0].lift)} ± ${fmt(e.results[0].liftCi95)}`),
      statTile('Current deck', `${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}`),
      statTile('Total runs', e.totalRuns),
    ]);
    setRunning(false);
  },

  cancelled() {
    setVerdict('idle', 'Cancelled.');
    setRunning(false);
  },

  error(e) {
    setVerdict('error', 'Error: ' + e.message);
    setRunning(false);
  },
};

function handleEvent(e) {
  const handler = eventHandlers[e.type];
  if (handler) handler(e);
}

// ─── Best combat renderer ───────────────────────────────────────────────

function renderEvent(ev) {
  if (ev.kind === 'draw') {
    return `<div class="ev draw"><span class="icon">↓</span><span class="ev-label">${escapeHtml(ev.label)}</span></div>`;
  }
  if (ev.kind === 'enemy') {
    const dmg = ev.subject ? ` <span class="ev-subject">${escapeHtml(ev.subject)}</span>` : '';
    return `<div class="ev enemy"><span class="icon">⚔</span><span class="ev-label">${escapeHtml(ev.label)}</span>${dmg}</div>`;
  }
  const cls = ev.auto ? 'ev play auto' : 'ev play manual';
  const subj = ev.subject ? ` <span class="ev-subject">→ ${escapeHtml(ev.subject)}</span>` : '';
  const tag = ev.auto ? '<span class="auto-tag">auto</span>' : '';
  return `<div class="${cls}"><span class="icon">▶</span><span class="ev-label">${escapeHtml(ev.label)}</span>${subj}${tag}</div>`;
}

function renderTurnCard(t) {
  const rows = (t.events || []).map(renderEvent).join('') || '<div class="empty">no events</div>';
  return `
    <div class="turn-card">
      <div class="head">
        <span class="turn-num">Turn ${t.turn}</span>
        <span class="turn-dmg">${t.damage} dmg</span>
      </div>
      <div class="event-list">${rows}</div>
    </div>`;
}

function renderBestCombat(e) {
  const seedHex = e.seed.toString(16).toUpperCase();
  const phaseTxt = phaseDefs.length > 1 && e.label ? `[${e.label}] ` : '';
  const headline = e.win != null
    ? (e.win
        ? `${phaseTxt}WIN with ${e.playerHpRemaining}/${e.playerMaxHp} HP in ${e.turns.length} turns — seed 0x${seedHex}`
        : `${phaseTxt}best so far: LOSS, enemy at ${e.enemyHpRemaining} HP after ${e.turns.length} turns — seed 0x${seedHex}`)
    : `${phaseTxt}${e.totalDamage} dmg over ${e.turns.length} turns (${e.avgPerTurn.toFixed(1)}/turn) — seed 0x${seedHex}`;
  $('best-headline').textContent = headline;
  $('best-combat').innerHTML = e.turns.map(renderTurnCard).join('');
}

// ─── Config persistence ──────────────────────────────────────────────────

const CFG_IDS = ['cfg-seeds', 'cfg-k', 'cfg-patience', 'cfg-turns', 'cfg-eps'];
function restoreConfig() {
  for (const id of CFG_IDS) {
    const saved = localStorage.getItem(`sts2sim.${id}`);
    if (saved !== null) $(id).value = saved;
    $(id).addEventListener('change', () => localStorage.setItem(`sts2sim.${id}`, $(id).value));
  }
}

// ─── Buttons ─────────────────────────────────────────────────────────────

$('run-btn').onclick = async () => {
  const body = {
    seeds: +$('cfg-seeds').value,
    k: +$('cfg-k').value,
    patience: +$('cfg-patience').value,
    turns: +$('cfg-turns').value,
    epsilon: +$('cfg-eps').value,
    encounterId: $('cfg-encounter').value || null,
    ...changesPayload(),
  };
  try {
    const r = await fetch('/api/sim/start', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!r.ok) {
      const text = await r.text().catch(() => '');
      setVerdict('error', `Server returned ${r.status}: ${text.slice(0, 200)}`);
    }
  } catch (e) {
    setVerdict('error', `Can't reach server (is StS2Sim.exe running?): ${e.message}`);
  }
};
$('stop-btn').onclick = () => fetch('/api/sim/stop', { method: 'POST' }).catch(() => {});
$('refresh-btn').onclick = () => loadDeck();

// ─── Boot ────────────────────────────────────────────────────────────────

initCharts();
restoreConfig();
loadDeck();
loadEncounters();
connectWs();
