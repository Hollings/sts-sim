// StS2 Sim — single-file frontend.
// Loads the current run's deck from the embedded server, lets you kick off
// a sim batch, and live-streams progress over WebSocket while drawing charts.

const $ = id => document.getElementById(id);
const fmt = n => Number.isFinite(n) ? n.toFixed(2) : '—';

let bestChart, avgChart, histChart;
let bestSeries = [];           // [{x, y}] — per-seed best damage
let avgSeries = [];            // [{x, y}] — running avg
let ciUpper = [];
let ciLower = [];
let runningMean = null;        // for the horizontal reference line

// Color a damage value on a red→yellow→green gradient relative to the run's range.
function damageColor(y, min, max) {
  if (max === min) return '#d4a142';
  const t = (y - min) / (max - min); // 0 = worst, 1 = best
  // bad (red 8b1913) → mid (yellow d4a142) → good (green 5fb04f)
  const lerp = (a, b, t) => Math.round(a + (b - a) * t);
  if (t < 0.5) {
    const u = t * 2;
    return `rgb(${lerp(0x8b, 0xd4, u)},${lerp(0x19, 0xa1, u)},${lerp(0x13, 0x42, u)})`;
  }
  const u = (t - 0.5) * 2;
  return `rgb(${lerp(0xd4, 0x5f, u)},${lerp(0xa1, 0xb0, u)},${lerp(0x42, 0x4f, u)})`;
}

// ─── Init charts ─────────────────────────────────────────────────────────

function initCharts() {
  const fontColor = '#f2f0c4';
  const gridColor = 'rgba(33,69,90,0.6)';
  Chart.defaults.color = fontColor;
  Chart.defaults.borderColor = gridColor;

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
  // Per-seed scatter: color each dot by its damage relative to the run's range.
  if (bestSeries.length > 0) {
    const ys = bestSeries.map(p => p.y);
    const min = Math.min(...ys);
    const max = Math.max(...ys);
    bestChart.data.datasets[0].data = bestSeries;
    bestChart.data.datasets[0].pointBackgroundColor = bestSeries.map(p => damageColor(p.y, min, max));
  }
  bestChart.update('none');

  avgChart.data.datasets[0].data = ciUpper;
  avgChart.data.datasets[1].data = ciLower;
  avgChart.data.datasets[2].data = avgSeries;
  avgChart.update('none');

  // Histogram with ~12 buckets, colored by damage gradient.
  if (bestSeries.length > 1) {
    const ys = bestSeries.map(p => p.y);
    const min = Math.min(...ys);
    const max = Math.max(...ys);
    if (max > min) {
      const buckets = 12;
      const w = (max - min) / buckets;
      const counts = new Array(buckets).fill(0);
      const labels = [];
      const colors = [];
      for (let i = 0; i < buckets; i++) {
        labels.push(`${Math.round(min + i * w)}`);
        colors.push(damageColor(min + (i + 0.5) * w, min, max));
      }
      ys.forEach(y => {
        let i = Math.floor((y - min) / w);
        if (i >= buckets) i = buckets - 1;
        counts[i]++;
      });
      histChart.data.labels = labels;
      histChart.data.datasets[0].data = counts;
      histChart.data.datasets[0].backgroundColor = colors;
      histChart.update('none');
    }
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
      <div class="row"><span>Character</span><span>${d.characterPretty}</span></div>
      <div class="row"><span>HP</span><span>${d.currentHp} / ${d.maxHp}</span></div>
      <div class="row"><span>Gold</span><span>${d.gold}</span></div>
      <div class="row"><span>Save</span><span>${d.modified}</span></div>`;
    $('deck-info').innerHTML = `<div class="row"><span>Total cards</span><span><b>${d.deckSize}</b></span></div>`;
    $('deck-cards').innerHTML = d.cardsGrouped
      .map(c => `<li><span>${c.name}</span><span class="count">×${c.count}</span></li>`).join('');
    $('relics').innerHTML = d.relics.length
      ? d.relics.map(r => `<li><span>${r.name}</span></li>`).join('')
      : '<li class="empty">none</li>';
  } catch (e) {
    $('run-info').innerHTML = `<div class="empty">Error: ${e.message}</div>`;
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
      $('verdict').className = 'verdict idle';
      $('verdict').textContent = 'Reconnected. Click "Run Sim" to start.';
    }
  };
  ws.onmessage = ev => {
    const e = JSON.parse(ev.data);
    handleEvent(e);
  };
  ws.onclose = () => {
    wsRetries++;
    if (wsRetries > 3) {
      $('verdict').className = 'verdict error';
      $('verdict').textContent = `Lost server connection. Restart StS2Sim.exe and reload this page.`;
    }
    setTimeout(connectWs, Math.min(wsRetries * 1000, 5000));
  };
}

// Coalesce chart redraws into a single rAF tick — Chart.js updates are
// expensive once you have 200+ datapoints; doing them per-event makes
// big runs visibly stutter. We just buffer and let the browser repaint.
let chartsDirty = false;
let bestPending = null;
function scheduleRedraw() {
  if (chartsDirty || bestPending) return;
  // (intentionally fall through; the rAF below schedules)
}
function tick() {
  if (chartsDirty) { updateCharts(); chartsDirty = false; }
  if (bestPending) { renderBestCombat(bestPending); bestPending = null; }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

function handleEvent(e) {
  switch (e.type) {
    case 'started':
      resetCharts();
      $('best-combat').innerHTML = '<div class="empty">Searching for the best combat…</div>';
      $('best-headline').textContent = '';
      $('verdict').className = 'verdict running';
      $('verdict').textContent = `Running: ${e.seeds} seeds × ${e.k} samples each, ${e.turns} turns, ε=${e.epsilon}`;
      $('status').textContent = 'Started…';
      $('prog').style.width = '0%';
      $('run-btn').disabled = true;
      $('stop-btn').disabled = false;
      break;
    case 'newBest':
      // Don't re-render synchronously — coalesce into next rAF.
      bestPending = e;
      break;
    case 'seed':
      bestSeries.push({ x: e.index, y: e.bestForSeed });
      avgSeries.push({ x: e.index, y: e.runningAvg });
      ciUpper.push({ x: e.index, y: e.runningAvg + e.ci95 });
      ciLower.push({ x: e.index, y: e.runningAvg - e.ci95 });
      runningMean = e.runningAvg;
      const pct = ((e.index + 1) / e.total) * 100;
      $('prog').style.width = pct + '%';
      // Cheap text updates can stay per-event.
      $('status').textContent = `Seed ${e.index + 1} / ${e.total} · ${e.totalRuns} runs · ${(e.totalRuns / Math.max(1, e.elapsedMs / 1000)).toFixed(0)} runs/s`;
      // Mark charts dirty; the rAF tick will redraw at most once per frame.
      chartsDirty = true;
      // Stat tiles get updated less often (once per ~10 seeds is fine).
      if (e.index % 10 === 0 || e.index + 1 === e.total) {
        $('stats').innerHTML = `
          <div class="stat"><span class="label">Running avg-of-best</span><span class="value">${fmt(e.runningAvg)} ± ${fmt(e.ci95)}</span></div>
          <div class="stat"><span class="label">Last seed best</span><span class="value">${e.bestForSeed}</span></div>
          <div class="stat"><span class="label">Total runs</span><span class="value">${e.totalRuns}</span></div>`;
      }
      break;
    case 'done':
      updateCharts();
      $('verdict').className = 'verdict done';
      $('verdict').textContent = `Done: ${fmt(e.avgOfBest)} ± ${fmt(e.ci95)} avg-of-best (${fmt(e.avgPerTurn)}/turn)`;
      $('status').textContent = `Completed in ${e.elapsedSec.toFixed(1)}s · ${e.totalRuns} runs · best seed: ${e.bestOfBest}, worst seed: ${e.worstSeedBest}`;
      $('stats').innerHTML = `
        <div class="stat"><span class="label">Avg-of-best</span><span class="value">${fmt(e.avgOfBest)} ± ${fmt(e.ci95)}</span></div>
        <div class="stat"><span class="label">Per turn</span><span class="value">${fmt(e.avgPerTurn)}</span></div>
        <div class="stat"><span class="label">Best / worst seed</span><span class="value">${e.bestOfBest} / ${e.worstSeedBest}</span></div>
        <div class="stat"><span class="label">Convergence (median K)</span><span class="value">${e.medianConvergenceK}</span></div>`;
      $('run-btn').disabled = false;
      $('stop-btn').disabled = true;
      break;
    case 'cancelled':
      $('verdict').className = 'verdict idle';
      $('verdict').textContent = 'Cancelled.';
      $('run-btn').disabled = false;
      $('stop-btn').disabled = true;
      break;
    case 'error':
      $('verdict').className = 'verdict error';
      $('verdict').textContent = 'Error: ' + e.message;
      $('run-btn').disabled = false;
      $('stop-btn').disabled = true;
      break;
  }
}

// ─── Best combat renderer ───────────────────────────────────────────────

function renderBestCombat(e) {
  $('best-headline').textContent = `${e.totalDamage} dmg over ${e.turns.length} turns (${e.avgPerTurn.toFixed(1)}/turn) — seed 0x${e.seed.toString(16).toUpperCase()}`;
  const cells = e.turns.map(t => {
    const rows = (t.events || []).map(ev => {
      if (ev.kind === 'draw') {
        return `<div class="ev draw"><span class="icon">↓</span><span class="ev-label">${escapeHtml(ev.label)}</span></div>`;
      }
      const cls = ev.auto ? 'ev play auto' : 'ev play manual';
      const tag = ev.auto ? '<span class="auto-tag">auto</span>' : '';
      return `<div class="${cls}"><span class="icon">▶</span><span class="ev-label">${escapeHtml(ev.label)}</span>${tag}</div>`;
    }).join('');
    return `
      <div class="turn-card">
        <div class="head">
          <span class="turn-num">Turn ${t.turn}</span>
          <span class="turn-dmg">${t.damage} dmg</span>
        </div>
        <div class="event-list">${rows || '<div class="empty">no events</div>'}</div>
      </div>`;
  }).join('');
  $('best-combat').innerHTML = cells;
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
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
    const r = await fetch('/api/sim/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!r.ok) {
      const text = await r.text().catch(() => '');
      $('verdict').className = 'verdict error';
      $('verdict').textContent = `Server returned ${r.status}: ${text.slice(0, 200)}`;
    }
  } catch (e) {
    $('verdict').className = 'verdict error';
    $('verdict').textContent = `Can't reach server (is StS2Sim.exe running?): ${e.message}`;
  }
};
$('stop-btn').onclick = () => fetch('/api/sim/stop', { method: 'POST' }).catch(() => {});
$('refresh-btn').onclick = () => loadDeck();

// ─── Boot ────────────────────────────────────────────────────────────────

initCharts();
loadDeck();
connectWs();
