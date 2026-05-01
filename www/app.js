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

// ─── Init charts ─────────────────────────────────────────────────────────

function initCharts() {
  const fontColor = '#f2f0c4';
  const gridColor = 'rgba(33,69,90,0.6)';
  Chart.defaults.color = fontColor;
  Chart.defaults.borderColor = gridColor;

  bestChart = new Chart($('chart-best'), {
    type: 'scatter',
    data: { datasets: [{ data: [], backgroundColor: '#d4a142', radius: 2 }] },
    options: {
      animation: false,
      scales: {
        x: { title: { display: true, text: 'seed index' } },
        y: { title: { display: true, text: 'best damage' }, beginAtZero: true },
      },
      plugins: { legend: { display: false } },
    },
  });

  avgChart = new Chart($('chart-avg'), {
    type: 'line',
    data: { datasets: [
      { label: 'CI upper', data: [], borderColor: 'rgba(212,161,66,0.3)', borderWidth: 1, fill: '+1', backgroundColor: 'rgba(212,161,66,0.1)', pointRadius: 0 },
      { label: 'CI lower', data: [], borderColor: 'rgba(212,161,66,0.3)', borderWidth: 1, fill: false, pointRadius: 0 },
      { label: 'Running avg-of-best', data: [], borderColor: '#d4a142', borderWidth: 2, fill: false, pointRadius: 0, tension: 0.1 },
    ]},
    options: {
      animation: false,
      scales: {
        x: { title: { display: true, text: 'seed index' } },
        y: { title: { display: true, text: 'damage / N turns' }, beginAtZero: false },
      },
      plugins: { legend: { display: false } },
    },
  });

  histChart = new Chart($('chart-hist'), {
    type: 'bar',
    data: { labels: [], datasets: [{ data: [], backgroundColor: '#8b1913' }] },
    options: {
      animation: false,
      scales: {
        x: { title: { display: true, text: 'damage bucket' } },
        y: { title: { display: true, text: 'seed count' }, beginAtZero: true },
      },
      plugins: { legend: { display: false } },
    },
  });
}

function updateCharts() {
  bestChart.data.datasets[0].data = bestSeries;
  bestChart.update('none');

  avgChart.data.datasets[0].data = ciUpper;
  avgChart.data.datasets[1].data = ciLower;
  avgChart.data.datasets[2].data = avgSeries;
  avgChart.update('none');

  // Build histogram with ~12 buckets.
  if (bestSeries.length > 1) {
    const ys = bestSeries.map(p => p.y);
    const min = Math.min(...ys);
    const max = Math.max(...ys);
    if (max > min) {
      const buckets = 12;
      const w = (max - min) / buckets;
      const counts = new Array(buckets).fill(0);
      const labels = [];
      for (let i = 0; i < buckets; i++) labels.push(`${(min + i * w).toFixed(0)}-${(min + (i + 1) * w).toFixed(0)}`);
      ys.forEach(y => {
        let i = Math.floor((y - min) / w);
        if (i >= buckets) i = buckets - 1;
        counts[i]++;
      });
      histChart.data.labels = labels;
      histChart.data.datasets[0].data = counts;
      histChart.update('none');
    }
  }
}

function resetCharts() {
  bestSeries = []; avgSeries = []; ciUpper = []; ciLower = [];
  bestChart.data.datasets[0].data = [];
  avgChart.data.datasets.forEach(ds => ds.data = []);
  histChart.data.labels = [];
  histChart.data.datasets[0].data = [];
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
function connectWs() {
  ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = ev => {
    const e = JSON.parse(ev.data);
    handleEvent(e);
  };
  ws.onclose = () => setTimeout(connectWs, 1000);
}

function handleEvent(e) {
  switch (e.type) {
    case 'started':
      resetCharts();
      $('verdict').className = 'verdict running';
      $('verdict').textContent = `Running: ${e.seeds} seeds × ${e.k} samples each, ${e.turns} turns, ε=${e.epsilon}`;
      $('status').textContent = 'Started…';
      $('prog').style.width = '0%';
      $('run-btn').disabled = true;
      $('stop-btn').disabled = false;
      break;
    case 'seed':
      bestSeries.push({ x: e.index, y: e.bestForSeed });
      avgSeries.push({ x: e.index, y: e.runningAvg });
      ciUpper.push({ x: e.index, y: e.runningAvg + e.ci95 });
      ciLower.push({ x: e.index, y: e.runningAvg - e.ci95 });
      const pct = ((e.index + 1) / e.total) * 100;
      $('prog').style.width = pct + '%';
      $('status').textContent = `Seed ${e.index + 1} / ${e.total} · ${e.totalRuns} runs · ${(e.totalRuns / (e.elapsedMs / 1000)).toFixed(0)} runs/s`;
      $('stats').innerHTML = `
        <div class="stat"><span class="label">Running avg-of-best</span><span class="value">${fmt(e.runningAvg)} ± ${fmt(e.ci95)}</span></div>
        <div class="stat"><span class="label">Last seed best</span><span class="value">${e.bestForSeed}</span></div>
        <div class="stat"><span class="label">Total runs</span><span class="value">${e.totalRuns}</span></div>`;
      // Throttle chart updates to every 5th seed for big runs.
      if (e.index % 3 === 0 || e.index + 1 === e.total) updateCharts();
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

// ─── Buttons ─────────────────────────────────────────────────────────────

$('run-btn').onclick = async () => {
  const body = {
    seeds: +$('cfg-seeds').value,
    k: +$('cfg-k').value,
    turns: +$('cfg-turns').value,
    epsilon: +$('cfg-eps').value,
  };
  await fetch('/api/sim/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
};
$('stop-btn').onclick = () => fetch('/api/sim/stop', { method: 'POST' });
$('refresh-btn').onclick = () => loadDeck();

// ─── Boot ────────────────────────────────────────────────────────────────

initCharts();
loadDeck();
connectWs();
