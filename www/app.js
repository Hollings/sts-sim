// StS2 Sim — single-file frontend.
// Loads the current run's deck from the embedded server, lets you mark card
// removals/additions for an A/B test, kicks off sim batches, and live-streams
// progress over WebSocket while drawing charts.

// ─── Tiny utilities ──────────────────────────────────────────────────────

const $ = id => document.getElementById(id);
const fmt = n => Number.isFinite(n) ? n.toFixed(2) : '—';

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

const BASE_COLOR = '#6da4c4';      // baseline series (muted blue)
const VARIANT_COLOR = '#d4a142';   // variant series (gold)

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

// ─── Charts ──────────────────────────────────────────────────────────────

let bestChart, avgChart, histChart;
let abMode = false;
// Per-phase series. Single-run mode only fills `base`.
const series = {
  base:    { best: [], avg: [], ciUpper: [], ciLower: [] },
  variant: { best: [], avg: [], ciUpper: [], ciLower: [] },
};
let runningMean = null;        // for the horizontal reference line

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

  bestChart = new Chart($('chart-best'), {
    type: 'scatter',
    data: { datasets: [
      { label: 'baseline', data: [], pointBackgroundColor: [], pointBorderColor: '#0f2733', pointBorderWidth: 1, radius: 5, hoverRadius: 7 },
      { label: 'variant', data: [], pointBackgroundColor: VARIANT_COLOR, pointBorderColor: '#0f2733', pointBorderWidth: 1, radius: 5, hoverRadius: 7, pointStyle: 'triangle' },
    ]},
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'seed index' }, ticks: { precision: 0 } },
        y: { title: { display: true, text: 'best damage' }, beginAtZero: false, grace: '5%' },
      },
      plugins: {
        legend: { display: false },
        tooltip: { callbacks: { label: c => `${c.dataset.label} seed ${c.parsed.x}: ${c.parsed.y} dmg` } },
      },
    },
    plugins: [meanLinePlugin],
  });

  avgChart = new Chart($('chart-avg'), {
    // scatter with line: keeps x-axis numeric (line type defaults to category, which
    // squashes everything to the left as new points come in).
    type: 'scatter',
    data: { datasets: [
      { label: 'CI upper', data: [], borderColor: 'rgba(212,161,66,0.4)', borderWidth: 1, fill: '+1', backgroundColor: 'rgba(212,161,66,0.12)', pointRadius: 0, showLine: true },
      { label: 'CI lower', data: [], borderColor: 'rgba(212,161,66,0.4)', borderWidth: 1, fill: false, pointRadius: 0, showLine: true },
      { label: 'baseline', data: [], borderColor: BASE_COLOR, borderWidth: 2, fill: false, pointRadius: 0, showLine: true, tension: 0.15 },
      { label: 'variant', data: [], borderColor: VARIANT_COLOR, borderWidth: 2, fill: false, pointRadius: 0, showLine: true, tension: 0.15 },
    ]},
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'seed index' }, ticks: { precision: 0 } },
        y: { title: { display: true, text: 'avg-of-best damage' }, beginAtZero: false, grace: '5%' },
      },
      plugins: { legend: { display: false } },
    },
  });

  histChart = new Chart($('chart-hist'), {
    type: 'bar',
    data: { labels: [], datasets: [
      { label: 'baseline', data: [], backgroundColor: [] },
      { label: 'variant', data: [], backgroundColor: VARIANT_COLOR + 'cc' },
    ]},
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'damage bucket (per-seed best)' }, grid: { display: false } },
        y: { title: { display: true, text: 'seeds' }, beginAtZero: true, ticks: { precision: 0 } },
      },
      plugins: { legend: { display: false } },
    },
  });
}

function updateCharts() {
  const baseYs = series.base.best.map(p => p.y);
  const varYs = series.variant.best.map(p => p.y);
  const allYs = baseYs.concat(varYs);
  const [min, max] = allYs.length ? minMax(allYs) : [0, 0];

  // Per-seed scatter. Single mode: gradient-colored dots. A/B mode: solid
  // per-phase colors so the two clouds are distinguishable.
  bestChart.data.datasets[0].data = series.base.best;
  bestChart.data.datasets[0].pointBackgroundColor = abMode
    ? BASE_COLOR
    : series.base.best.map(p => damageColor(p.y, min, max));
  bestChart.data.datasets[1].data = series.variant.best;
  bestChart.update('none');

  avgChart.data.datasets[0].data = abMode ? [] : series.base.ciUpper;
  avgChart.data.datasets[1].data = abMode ? [] : series.base.ciLower;
  avgChart.data.datasets[2].data = series.base.avg;
  avgChart.data.datasets[3].data = series.variant.avg;
  avgChart.update('none');

  // Histogram with ~12 shared buckets. A/B mode shows the two distributions
  // side by side; single mode keeps the damage-gradient coloring.
  if (allYs.length > 1 && max > min) {
    const buckets = 12;
    const w = (max - min) / buckets;
    const labels = [];
    const colors = [];
    for (let i = 0; i < buckets; i++) {
      labels.push(`${Math.round(min + i * w)}`);
      colors.push(damageColor(min + (i + 0.5) * w, min, max));
    }
    const countUp = ys => {
      const counts = new Array(buckets).fill(0);
      for (const y of ys) counts[Math.min(buckets - 1, Math.floor((y - min) / w))]++;
      return counts;
    };
    histChart.data.labels = labels;
    histChart.data.datasets[0].data = countUp(baseYs);
    histChart.data.datasets[0].backgroundColor = abMode ? BASE_COLOR + 'cc' : colors;
    histChart.data.datasets[1].data = abMode ? countUp(varYs) : [];
    histChart.update('none');
  }
}

function resetCharts() {
  for (const phase of Object.values(series)) {
    phase.best = []; phase.avg = []; phase.ciUpper = []; phase.ciLower = [];
  }
  runningMean = null;
  bestChart.data.datasets.forEach(ds => { ds.data = []; });
  bestChart.data.datasets[0].pointBackgroundColor = [];
  avgChart.data.datasets.forEach(ds => { ds.data = []; });
  histChart.data.labels = [];
  histChart.data.datasets.forEach(ds => { ds.data = []; });
  bestChart.update('none'); avgChart.update('none'); histChart.update('none');
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
let cardCatalog = [];         // /api/cards entries for the picker
// Pending A/B changes. Keys are `${id}|${upgrade}`.
const removals = new Map();   // key -> count to remove
let additions = [];           // [{ id, upgrade, count, name }]

const changeKey = (id, upgrade) => `${id}|${upgrade}`;

function hasChanges() { return removals.size > 0 || additions.length > 0; }

function clearChanges() {
  removals.clear();
  additions = [];
  renderDeckEditor();
}

function changesPayload() {
  return {
    removals: [...removals.entries()].map(([key, count]) => {
      const [id, upgrade] = key.split('|');
      return { id, upgrade: +upgrade, count };
    }),
    additions: additions.map(a => ({ id: a.id, upgrade: a.upgrade, count: a.count })),
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
    $('run-btn').textContent = 'Run A/B Sim';
  } else {
    bar.classList.add('hidden');
    $('run-btn').textContent = 'Run Sim';
  }
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

$('changes-clear').onclick = clearChanges;

$('add-card-btn').onclick = () => {
  const input = $('add-card-input');
  const name = input.value.trim();
  if (!name) return;
  const entry = cardCatalog.find(c => c.name.toLowerCase() === name.toLowerCase())
             || cardCatalog.find(c => c.name.toLowerCase().startsWith(name.toLowerCase()));
  if (!entry) {
    setVerdict('error', `Unknown card "${name}" — pick one from the list.`);
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

async function loadCardCatalog() {
  try {
    const r = await fetch('/api/cards');
    if (!r.ok) return;
    const d = await r.json();
    cardCatalog = d.cards;
    $('card-options').innerHTML = cardCatalog
      .map(c => `<option value="${escapeHtml(c.name)}">${escapeHtml(`${c.cost}⚡ ${c.cardType} · ${c.rarity}`)}</option>`)
      .join('');
  } catch { /* picker just stays empty */ }
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

let phaseDoneInfo = null; // baseline phaseDone payload, for status text

// Event handlers, one per `e.type` — dispatched from handleEvent below.
// Wire-shape contract: these `type` strings + field names match Server/SimJob.cs Broadcast calls.
const eventHandlers = {
  started(e) {
    abMode = !!e.abTest;
    phaseDoneInfo = null;
    resetCharts();
    $('ab-panel').className = 'ab-panel hidden';
    $('best-combat').innerHTML = '<div class="empty">Searching for the best combat…</div>';
    $('best-headline').textContent = '';
    const cfg = `${e.seeds} seeds × K=${e.k}${e.patience ? ` (patience ${e.patience})` : ''}, ${e.turns} turns, ε=${e.epsilon}`;
    setVerdict('running', abMode
      ? `A/B: ${e.changeSummary || 'deck edit'} — ${cfg}`
      : `Running: ${cfg}`);
    $('status').textContent = abMode ? 'Phase 1/2: baseline deck…' : 'Started…';
    $('prog').style.width = '0%';
    setRunning(true);
  },

  // Don't re-render synchronously — coalesce into next rAF.
  newBest(e) { bestPending = e; },

  seed(e) {
    const phase = series[e.phase] || series.base;
    phase.best.push({ x: e.index, y: e.bestForSeed });
    phase.avg.push({ x: e.index, y: e.runningAvg });
    phase.ciUpper.push({ x: e.index, y: e.runningAvg + e.ci95 });
    phase.ciLower.push({ x: e.index, y: e.runningAvg - e.ci95 });
    if (!abMode) runningMean = e.runningAvg;

    // Progress: in A/B mode each phase is half the bar.
    const phaseFrac = (e.index + 1) / e.total;
    const pct = abMode
      ? (e.phase === 'variant' ? 50 + phaseFrac * 50 : phaseFrac * 50)
      : phaseFrac * 100;
    $('prog').style.width = pct + '%';
    // Cheap text updates can stay per-event.
    const runsPerSec = (e.totalRuns / Math.max(1, e.elapsedMs / 1000)).toFixed(0);
    const phaseTxt = abMode ? (e.phase === 'variant' ? 'Phase 2/2 (variant)' : 'Phase 1/2 (baseline)') + ' · ' : '';
    $('status').textContent = `${phaseTxt}Seed ${e.index + 1} / ${e.total} · ${e.totalRuns} runs · ${runsPerSec} runs/s`;
    // Mark charts dirty; the rAF tick will redraw at most once per frame.
    chartsDirty = true;
    // Stat tiles get updated less often (once per ~10 seeds is fine).
    if (e.index % 10 === 0 || e.index + 1 === e.total) {
      const tiles = [
        statTile(`Running avg-of-best${abMode ? ` (${e.phase})` : ''}`, `${fmt(e.runningAvg)} ± ${fmt(e.ci95)}`),
        statTile('Last seed best', e.bestForSeed),
        statTile('Total runs', e.totalRuns),
      ];
      if (abMode && phaseDoneInfo)
        tiles.unshift(statTile('Baseline avg-of-best', `${fmt(phaseDoneInfo.avgOfBest)} ± ${fmt(phaseDoneInfo.ci95)}`));
      renderStats(tiles);
    }
  },

  phaseDone(e) {
    if (e.phase === 'base') {
      phaseDoneInfo = e;
      $('status').textContent = `Baseline done: ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)} · starting variant…`;
    }
  },

  done(e) {
    updateCharts();
    setVerdict('done', `Done: ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)} avg-of-best (${fmt(e.avgPerTurn)}/turn)`);
    $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs · best seed: ${e.bestOfBest}, worst seed: ${e.worstSeedBest}`;
    renderStats([
      statTile('Avg-of-best', `${fmt(e.avgOfBest)} ± ${fmt(e.ci95)}`),
      statTile('Per turn', fmt(e.avgPerTurn)),
      statTile('Best / worst seed', `${e.bestOfBest} / ${e.worstSeedBest}`),
      statTile('Convergence (median K)', e.medianConvergenceK),
    ]);
    setRunning(false);
  },

  abDone(e) {
    updateCharts();
    const sign = e.lift >= 0 ? '+' : '';
    setVerdict('done', `A/B done: lift ${sign}${fmt(e.lift)} ± ${fmt(e.liftCi95)} dmg`);
    $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs total (paired over ${e.pairedSeeds} seeds)`;

    const panel = $('ab-panel');
    panel.className = `ab-panel ${e.verdictClass}`;
    $('ab-verdict-line').textContent = e.verdict;
    $('ab-detail').innerHTML = [
      ['Change', e.changeSummary || '—'],
      ['Baseline', `${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}`],
      ['Variant', `${fmt(e.variantAvg)} ± ${fmt(e.variantCi95)}`],
      ['Lift', `${sign}${fmt(e.lift)} ± ${fmt(e.liftCi95)} (${sign}${fmt(e.liftPerTurn)}/turn)`],
      ['z-score (paired)', fmt(e.z)],
    ].map(([label, value]) =>
      `<div><span class="label">${escapeHtml(label)}</span><span class="value">${escapeHtml(String(value))}</span></div>`
    ).join('');

    renderStats([
      statTile('Baseline avg-of-best', `${fmt(e.baseAvg)} ± ${fmt(e.baseCi95)}`),
      statTile('Variant avg-of-best', `${fmt(e.variantAvg)} ± ${fmt(e.variantCi95)}`),
      statTile('Lift', `${sign}${fmt(e.lift)} ± ${fmt(e.liftCi95)}`),
      statTile('z (paired)', fmt(e.z)),
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
  const phaseTxt = abMode ? `[${e.phase === 'variant' ? 'variant' : 'baseline'}] ` : '';
  $('best-headline').textContent = `${phaseTxt}${e.totalDamage} dmg over ${e.turns.length} turns (${e.avgPerTurn.toFixed(1)}/turn) — seed 0x${seedHex}`;
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
connectWs();
