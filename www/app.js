// StS2 Sim — single-file frontend.
// Loads the current run's deck from the embedded server, lets you kick off
// a sim batch, and live-streams progress over WebSocket while drawing charts.

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
let bestSeries = [];           // [{x, y}] — per-seed best damage
let avgSeries = [];            // [{x, y}] — running avg
let ciUpper = [];
let ciLower = [];
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
    data: { datasets: [{ data: [], pointBackgroundColor: [], pointBorderColor: '#0f2733', pointBorderWidth: 1, radius: 5, hoverRadius: 7 }] },
    options: {
      animation: false,
      maintainAspectRatio: false,
      scales: {
        x: { title: { display: true, text: 'seed index' }, ticks: { precision: 0 } },
        y: { title: { display: true, text: 'best damage / 5 turns' }, beginAtZero: false, grace: '5%' },
      },
      plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => `seed ${c.parsed.x}: ${c.parsed.y} dmg` } } },
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
      { label: 'Running avg', data: [], borderColor: '#d4a142', borderWidth: 2, fill: false, pointRadius: 0, showLine: true, tension: 0.15 },
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
    data: { labels: [], datasets: [{ data: [], backgroundColor: [] }] },
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
  const ys = bestSeries.map(p => p.y);
  const [min, max] = ys.length ? minMax(ys) : [0, 0];

  // Per-seed scatter: color each dot by its damage relative to the run's range.
  if (bestSeries.length > 0) {
    bestChart.data.datasets[0].data = bestSeries;
    bestChart.data.datasets[0].pointBackgroundColor = bestSeries.map(p => damageColor(p.y, min, max));
  }
  bestChart.update('none');

  avgChart.data.datasets[0].data = ciUpper;
  avgChart.data.datasets[1].data = ciLower;
  avgChart.data.datasets[2].data = avgSeries;
  avgChart.update('none');

  // Histogram with ~12 buckets, colored by damage gradient.
  if (ys.length > 1 && max > min) {
    const buckets = 12;
    const w = (max - min) / buckets;
    const counts = new Array(buckets).fill(0);
    const labels = [];
    const colors = [];
    for (let i = 0; i < buckets; i++) {
      labels.push(`${Math.round(min + i * w)}`);
      colors.push(damageColor(min + (i + 0.5) * w, min, max));
    }
    for (const y of ys) {
      const i = Math.min(buckets - 1, Math.floor((y - min) / w));
      counts[i]++;
    }
    histChart.data.labels = labels;
    histChart.data.datasets[0].data = counts;
    histChart.data.datasets[0].backgroundColor = colors;
    histChart.update('none');
  }
}

function resetCharts() {
  bestSeries = []; avgSeries = []; ciUpper = []; ciLower = [];
  runningMean = null;
  bestChart.data.datasets[0].data = [];
  bestChart.data.datasets[0].pointBackgroundColor = [];
  avgChart.data.datasets.forEach(ds => ds.data = []);
  histChart.data.labels = [];
  histChart.data.datasets[0].data = [];
  histChart.data.datasets[0].backgroundColor = [];
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
    $('run-info').innerHTML = `
      <div class="row"><span>Character</span><span>${escapeHtml(d.characterPretty)}</span></div>
      <div class="row"><span>HP</span><span>${d.currentHp} / ${d.maxHp}</span></div>
      <div class="row"><span>Gold</span><span>${d.gold}</span></div>
      <div class="row"><span>Save</span><span>${escapeHtml(d.modified)}</span></div>`;
    $('deck-info').innerHTML = `<div class="row"><span>Total cards</span><span><b>${d.deckSize}</b></span></div>`;
    $('deck-cards').innerHTML = d.cardsGrouped
      .map(c => `<li><span>${escapeHtml(c.name)}</span><span class="count">×${c.count}</span></li>`).join('');
    $('relics').innerHTML = d.relics.length
      ? d.relics.map(r => `<li><span>${escapeHtml(r.name)}</span></li>`).join('')
      : '<li class="empty">none</li>';
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

// Event handlers, one per `e.type` — dispatched from handleEvent below.
// Wire-shape contract: these `type` strings + field names match Server/SimJob.cs Broadcast calls.
const eventHandlers = {
  started(e) {
    resetCharts();
    $('best-combat').innerHTML = '<div class="empty">Searching for the best combat…</div>';
    $('best-headline').textContent = '';
    setVerdict('running', `Running: ${e.seeds} seeds × ${e.k} samples each, ${e.turns} turns, ε=${e.epsilon}`);
    $('status').textContent = 'Started…';
    $('prog').style.width = '0%';
    setRunning(true);
  },

  // Don't re-render synchronously — coalesce into next rAF.
  newBest(e) { bestPending = e; },

  seed(e) {
    bestSeries.push({ x: e.index, y: e.bestForSeed });
    avgSeries.push({ x: e.index, y: e.runningAvg });
    ciUpper.push({ x: e.index, y: e.runningAvg + e.ci95 });
    ciLower.push({ x: e.index, y: e.runningAvg - e.ci95 });
    runningMean = e.runningAvg;

    const pct = ((e.index + 1) / e.total) * 100;
    $('prog').style.width = pct + '%';
    // Cheap text updates can stay per-event.
    const runsPerSec = (e.totalRuns / Math.max(1, e.elapsedMs / 1000)).toFixed(0);
    $('status').textContent = `Seed ${e.index + 1} / ${e.total} · ${e.totalRuns} runs · ${runsPerSec} runs/s`;
    // Mark charts dirty; the rAF tick will redraw at most once per frame.
    chartsDirty = true;
    // Stat tiles get updated less often (once per ~10 seeds is fine).
    if (e.index % 10 === 0 || e.index + 1 === e.total) {
      renderStats([
        statTile('Running avg-of-best', `${fmt(e.runningAvg)} ± ${fmt(e.ci95)}`),
        statTile('Last seed best', e.bestForSeed),
        statTile('Total runs', e.totalRuns),
      ]);
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
  $('best-headline').textContent = `${e.totalDamage} dmg over ${e.turns.length} turns (${e.avgPerTurn.toFixed(1)}/turn) — seed 0x${seedHex}`;
  $('best-combat').innerHTML = e.turns.map(renderTurnCard).join('');
}

// ─── Buttons ─────────────────────────────────────────────────────────────

$('run-btn').onclick = async () => {
  const body = {
    seeds: +$('cfg-seeds').value,
    k: +$('cfg-k').value,
    turns: +$('cfg-turns').value,
    epsilon: +$('cfg-eps').value,
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
loadDeck();
connectWs();
