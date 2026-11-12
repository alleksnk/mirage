package main

const mirageHtmlUI = `<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Mirage — Свободный доступ</title>
<style>
  :root {
    --bg: #0B0E14;
    --surface: #111622;
    --card: #151B28;
    --tile: #111622;
    --border: #222B3D;
    --text: #F1F5F9;
    --muted: #8B98B0;
    --subtle: #5C6982;
    --blue: #3B82F6;
    --blue-soft: #60A5FA;
    --green: #22C55E;
    --red: #EF4444;
    --amber: #F59E0B;
    --cyan: #38BDF8;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: var(--bg);
    color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, "SF Pro Text", "Segoe UI", sans-serif;
    font-size: 13px;
    min-height: 100vh;
    display: flex;
    flex-direction: column;
  }
  header {
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    padding: 12px 18px;
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .brand { display: flex; align-items: center; gap: 12px; }
  .logo-box {
    width: 34px; height: 34px; border-radius: 10px;
    background: #111A2B; border: 1px solid #263B5E;
    display: flex; align-items: center; justify-content: center;
  }
  .brand-title { font-size: 15px; font-weight: 700; }
  .brand-sub { font-size: 11px; color: var(--muted); }
  .header-right { display: flex; align-items: center; gap: 8px; }
  .pill {
    background: var(--card); border: 1px solid var(--border);
    border-radius: 999px; padding: 5px 11px;
    display: flex; align-items: center; gap: 7px; font-size: 11.5px; font-weight: 600;
  }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--red); flex-shrink: 0; }
  .tabs {
    display: flex; background: var(--surface);
    border-bottom: 1px solid var(--border); padding: 0 14px;
  }
  .tab {
    background: none; border: none; color: var(--muted);
    font-size: 12.5px; font-weight: 600; padding: 10px 14px;
    cursor: pointer; border-bottom: 2px solid transparent;
  }
  .tab:hover { color: #fff; }
  .tab.active { color: var(--text); border-bottom-color: var(--blue); }
  main { padding: 16px 18px; flex: 1; max-width: 580px; width: 100%; margin: 0 auto; }
  .panel { display: none; }
  .panel.active { display: block; }
  .card {
    background: var(--card); border: 1px solid var(--border);
    border-radius: 14px; padding: 18px; margin-bottom: 12px;
  }
  .hero { text-align: center; }
  .hero-title { font-size: 19px; font-weight: 700; }
  .hero-sub { font-size: 12px; color: var(--muted); margin-top: 5px; margin-bottom: 16px; }
  .btn-master {
    width: 230px; height: 44px; border-radius: 22px; border: none;
    background: var(--blue); color: #fff; font-size: 13.5px; font-weight: 700;
    cursor: pointer; transition: opacity .15s;
  }
  .btn-master:hover { opacity: .9; }
  .btn-master.stop { background: var(--red); }
  .btn-master.probing { background: var(--amber); color: #0B0E14; }
  .card-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
  .card-title-row { display: flex; align-items: center; gap: 10px; font-weight: 600; }
  .count-badge {
    background: var(--surface); border: 1px solid var(--border);
    border-radius: 999px; padding: 2px 8px; font-size: 11px; color: var(--muted);
  }
  .btn {
    background: #1B2436; border: 1px solid #2A364F; color: var(--blue-soft);
    border-radius: 8px; padding: 6px 12px; font-size: 11.5px; font-weight: 600; cursor: pointer;
  }
  .btn:hover { background: #243047; color: #fff; }
  .btn-primary { background: var(--blue); border-color: var(--blue); color: #fff; }
  .btn-green { background: var(--green); border-color: var(--green); color: #0B0E14; }
  .btn-quit { background: transparent; border-color: var(--border); color: var(--muted); padding: 5px 10px; }
  .grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .res-tile {
    background: var(--tile); border: 1px solid var(--border); border-radius: 9px;
    padding: 10px 11px; display: flex; align-items: center; justify-content: space-between;
  }
  .res-tile.clickable { cursor: pointer; }
  .res-tile.clickable:hover { border-color: #364766; }
  .res-left { display: flex; align-items: center; gap: 8px; font-size: 12px; font-weight: 600; }
  .res-status { font-size: 11px; color: var(--muted); }
  .row { display: flex; gap: 8px; margin-bottom: 10px; }
  input[type=text], input[type=number], textarea {
    background: var(--bg); border: 1px solid var(--border); color: var(--text);
    border-radius: 8px; padding: 8px 10px; font-size: 12px; width: 100%;
  }
  textarea { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; height: 280px; resize: vertical; }
  pre.logs {
    background: var(--bg); border: 1px solid var(--border); border-radius: 10px;
    padding: 12px; height: 320px; overflow: auto;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; color: var(--muted);
    white-space: pre-wrap;
  }
  .check-row { display: flex; align-items: center; gap: 8px; margin: 10px 0; font-size: 12.5px; }
  footer {
    background: #0E121A; border-top: 1px solid var(--border);
    padding: 7px 18px; display: flex; justify-content: space-between;
    font-size: 11px; color: var(--muted);
  }
</style>
</head>
<body>
<header>
  <div class="brand">
    <div class="logo-box">
      <svg width="20" height="20" viewBox="0 0 24 24">
        <path fill="#60A5FA" d="M11.2,3 L3.5,12 L11.2,21 L11.2,16.6 L7.4,12 L11.2,7.4 Z"/>
        <path fill="#38BDF8" d="M12.8,3 L20.5,12 L12.8,21 L12.8,16.6 L16.6,12 L12.8,7.4 Z"/>
        <path fill="#F1F5F9" d="M12,9.2 L14.8,12 L12,14.8 L9.2,12 Z"/>
      </svg>
    </div>
    <div>
      <div class="brand-title">Mirage</div>
      <div class="brand-sub">Свободный доступ без VPN (macOS)</div>
    </div>
  </div>
  <div class="header-right">
    <div class="pill"><span id="headerDot" class="dot"></span><span id="headerBadge">Отключено</span></div>
    <button class="btn btn-quit" onclick="quitApp()">Выйти</button>
  </div>
</header>

<nav class="tabs">
  <button class="tab active" onclick="showTab(0,this)">Обзор</button>
  <button class="tab" onclick="showTab(1,this)">Сайты</button>
  <button class="tab" onclick="showTab(2,this)">Telegram</button>
  <button class="tab" onclick="showTab(3,this)">Журнал</button>
  <button class="tab" onclick="showTab(4,this)">Настройки</button>
</nav>

<main>
  <section id="tab0" class="panel active">
    <div class="card hero">
      <div id="mainStatus" class="hero-title">Обход DPI выключен</div>
      <div id="subStatus" class="hero-sub">Нажмите «Включить», чтобы обойти блокировки</div>
      <button id="btnToggle" class="btn-master" onclick="toggleEngine()">ВКЛЮЧИТЬ ОБХОД</button>
    </div>

    <div class="card">
      <div class="card-head">
        <div class="card-title-row">
          <span>Доступность ресурсов</span>
          <span id="resSummary" class="count-badge">0 / 8</span>
        </div>
        <button id="btnCheck" class="btn" onclick="runCheck()">Проверить</button>
      </div>
      <div id="resGrid" class="grid-2"></div>
    </div>
  </section>

  <section id="tab1" class="panel">
    <div class="card">
      <div class="row">
        <input id="quickDomain" type="text" placeholder="example.com">
        <button class="btn btn-primary" style="white-space:nowrap" onclick="quickAddSite()">+ Добавить</button>
      </div>
      <textarea id="sitesEditor"></textarea>
      <div class="row" style="justify-content:flex-end;margin-top:10px;margin-bottom:0">
        <button class="btn" onclick="resetSites()">Сбросить</button>
        <button class="btn btn-green" onclick="saveSites()">Сохранить список</button>
      </div>
    </div>
  </section>

  <section id="tab2" class="panel">
    <div class="card">
      <div style="font-size:15px;font-weight:700;margin-bottom:6px">Подключение Telegram в 1 клик</div>
      <div style="color:var(--muted);font-size:12px;margin-bottom:14px">
        Прямой доступ к IP-серверам Telegram часто блокируется. Используйте FakeTLS MTProxy (порт 443) или локальный SOCKS5.
      </div>
      <div style="margin-bottom:10px">
        <button class="btn btn-primary" style="padding:10px 16px" onclick="openTg('primary')">Подключить основной FakeTLS-прокси</button>
      </div>
      <div class="row" style="flex-wrap:wrap">
        <button class="btn" onclick="openTg('backup1')">Резервный #1 (Mail.ru)</button>
        <button class="btn" onclick="openTg('backup2')">Резервный #2 (Mishutkin)</button>
        <button class="btn" onclick="openTg('socks')">Локальный SOCKS5 (:1080)</button>
      </div>
      <div class="row" style="margin-top:8px;margin-bottom:0">
        <input id="tgLink" type="text" readonly>
        <button class="btn" onclick="copyTg()">Копировать</button>
      </div>
    </div>
  </section>

  <section id="tab3" class="panel">
    <div class="card">
      <div class="card-head">
        <span style="font-weight:600">Журнал событий</span>
        <button class="btn" onclick="clearLogs()">Очистить</button>
      </div>
      <pre id="logsBox" class="logs"></pre>
    </div>
  </section>

  <section id="tab4" class="panel">
    <div class="card">
      <div style="font-weight:700;margin-bottom:12px">Параметры работы</div>
      <div style="font-size:11.5px;color:var(--muted);margin-bottom:5px">Чистый DNS (обход подмены порта 53):</div>
      <div class="row">
        <input id="dnsHost" type="text" placeholder="77.88.8.8" style="flex:2">
        <input id="dnsPort" type="number" placeholder="1253" style="flex:1">
      </div>
      <label class="check-row"><input id="chkAutoProbe" type="checkbox"> Автоподбор стратегии tpws при запуске</label>
      <label class="check-row"><input id="chkAutoPac" type="checkbox"> Автоматически настраивать системный PAC-прокси macOS</label>
      <button class="btn btn-primary" style="margin-top:8px" onclick="saveSettings()">Сохранить настройки</button>
    </div>
  </section>
</main>

<footer>
  <span id="footerStatus">Готово к работе</span>
  <span>Яндекс DNS 77.88.8.8:1253</span>
</footer>

<script>
const defaultResources = [
  {id:'youtube', name:'YouTube'},
  {id:'instagram', name:'Instagram'},
  {id:'discord', name:'Discord'},
  {id:'telegram', name:'Telegram'},
  {id:'rutracker', name:'RuTracker'},
  {id:'rutor', name:'Rutor.info'},
  {id:'x', name:'X (Twitter)'},
  {id:'medium', name:'Medium'}
];
let tgLinks = {};
let wasRunning = false;

function initGrid(items) {
  const grid = document.getElementById('resGrid');
  grid.innerHTML = items.map(r => {
    const clickable = r.id === 'telegram' ? ' clickable" onclick="showTabByIndex(2)' : '"';
    const dotColor = r.ok === true ? 'var(--green)' : (r.ok === false ? (r.id === 'telegram' ? 'var(--cyan)' : 'var(--red)') : 'var(--subtle)');
    const txtColor = r.ok === true ? 'var(--green)' : (r.ok === false ? (r.id === 'telegram' ? 'var(--cyan)' : 'var(--red)') : 'var(--muted)');
    const status = r.statusText || (r.id === 'telegram' ? 'Настроить' : '—');
    return '<div class="res-tile' + clickable + '><span class="res-left"><span class="dot" style="background:' + dotColor + '"></span>' + r.name + '</span><span class="res-status" style="color:' + txtColor + '">' + status + '</span></div>';
  }).join('');
}

function showTab(idx, btn) {
  document.querySelectorAll('.tab').forEach((t, i) => t.classList.toggle('active', i === idx));
  document.querySelectorAll('.panel').forEach((p, i) => p.classList.toggle('active', i === idx));
}
function showTabByIndex(idx) {
  const tabs = document.querySelectorAll('.tab');
  showTab(idx, tabs[idx]);
}

async function api(path, opts) {
  const r = await fetch(path, opts);
  return r.json();
}

async function refreshState() {
  const st = await api('/api/state');
  const btn = document.getElementById('btnToggle');
  const dot = document.getElementById('headerDot');
  const badge = document.getElementById('headerBadge');
  document.getElementById('mainStatus').textContent = st.statusText;
  document.getElementById('subStatus').textContent = st.subStatus;
  document.getElementById('footerStatus').textContent = st.running ? ('PAC 127.0.0.1:' + st.proxyPort + ' • ' + st.strategyLabel) : (st.probing ? 'Подбор стратегии...' : 'Остановлено');

  btn.classList.remove('stop', 'probing');
  if (st.probing) {
    btn.classList.add('probing');
    btn.textContent = 'ОСТАНОВИТЬ';
    dot.style.background = 'var(--amber)';
    badge.textContent = 'Подбор...';
  } else if (st.running) {
    btn.classList.add('stop');
    btn.textContent = 'ОСТАНОВИТЬ';
    dot.style.background = 'var(--green)';
    badge.textContent = 'Активно';
  } else {
    btn.textContent = 'ВКЛЮЧИТЬ ОБХОД';
    dot.style.background = 'var(--red)';
    badge.textContent = 'Отключено';
  }
  if (st.running && !wasRunning) runCheck();
  wasRunning = st.running;

  const lg = await api('/api/logs');
  document.getElementById('logsBox').textContent = lg.logs;
}

async function toggleEngine() {
  await api('/api/toggle', {method:'POST'});
  setTimeout(refreshState, 200);
}

async function runCheck() {
  const btn = document.getElementById('btnCheck');
  const sum = document.getElementById('resSummary');
  btn.disabled = true;
  btn.textContent = 'Проверка...';
  sum.textContent = 'Проверка...';
  initGrid(defaultResources.map(r => ({...r, statusText:'...', ok:null})));
  try {
    const res = await api('/api/check', {method:'POST'});
    initGrid(res.items);
    const okCount = res.items.filter(x => x.ok).length;
    sum.textContent = okCount + ' / ' + res.items.length + ' онлайн';
  } finally {
    btn.disabled = false;
    btn.textContent = 'Проверить';
  }
}

async function loadData() {
  initGrid(defaultResources);
  const data = await api('/api/config');
  document.getElementById('sitesEditor').value = data.sites;
  document.getElementById('dnsHost').value = data.settings.dnsHost;
  document.getElementById('dnsPort').value = data.settings.dnsPort;
  document.getElementById('chkAutoProbe').checked = data.settings.autoProbe;
  document.getElementById('chkAutoPac').checked = data.settings.autoSetPac;
  tgLinks = data.tgLinks;
  document.getElementById('tgLink').value = tgLinks.primary || '';
  refreshState();
}

async function quickAddSite() {
  const el = document.getElementById('quickDomain');
  const d = el.value.trim();
  if (!d) return;
  const r = await api('/api/sites/add', {method:'POST', body:JSON.stringify({domain:d})});
  document.getElementById('sitesEditor').value = r.sites;
  el.value = '';
}
async function saveSites() {
  await api('/api/sites', {method:'POST', body:JSON.stringify({sites:document.getElementById('sitesEditor').value})});
  alert('Список сайтов сохранён');
}
async function resetSites() {
  const r = await api('/api/sites/reset', {method:'POST'});
  document.getElementById('sitesEditor').value = r.sites;
}
async function saveSettings() {
  await api('/api/settings', {
    method:'POST',
    body:JSON.stringify({
      dnsHost: document.getElementById('dnsHost').value,
      dnsPort: parseInt(document.getElementById('dnsPort').value || '1253', 10),
      autoProbe: document.getElementById('chkAutoProbe').checked,
      autoSetPac: document.getElementById('chkAutoPac').checked
    })
  });
  alert('Настройки сохранены');
}
async function openTg(key) {
  document.getElementById('tgLink').value = tgLinks[key] || '';
  await api('/api/tg/open', {method:'POST', body:JSON.stringify({key})});
}
function copyTg() {
  navigator.clipboard.writeText(document.getElementById('tgLink').value);
}
async function clearLogs() {
  await api('/api/logs/clear', {method:'POST'});
  refreshState();
}
async function quitApp() {
  await api('/api/quit', {method:'POST'});
  document.body.innerHTML = '<div style="margin:auto;color:#8B98B0">Mirage остановлен и выключен. Эту вкладку можно закрыть.</div>';
}

loadData();
setInterval(refreshState, 1200);
</script>
</body>
</html>
`

