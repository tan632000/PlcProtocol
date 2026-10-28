// SCADA page wiring: DOM and browser APIs only; the rules live in scada-core.js. Display only: nothing here writes.
// API data is always written with textContent, never as HTML.
import * as core from './scada-core.js';

const REDIRECT_URI = new URL('/scada/', location.origin).toString();
const QUALITY = { good: ['Tốt', 'ok'], bad: ['Lỗi', 'bad'], uncertain: ['Không chắc', 'warn'] };

const $ = (id) => document.getElementById(id);
const page = {
  config: null,
  session: null,
  api: null,
  live: null,
  liveState: 'connecting',
  table: core.createLiveTable(),
  trend: core.createLatch(),
  trendTag: null,
  lastFailure: null, // status of the last API answer that returned no data
  fatal: false,
};

const NETWORK_RETRY_MS = 5_000;

// Event handlers are async; a surprise (e.g. a malformed response) must show up on the page, not vanish.
const guard = (promise) => promise.catch(() => showBanner('Lỗi không mong đợi khi xử lý dữ liệu. Tải lại trang để thử lại.'));

main().catch(() => showFatal('Không khởi động được trang SCADA. Tải lại trang để thử lại.'));

async function main() {
  const response = await fetch('/scada/config', { cache: 'no-store' });
  if (!response.ok) {
    showFatal('Không lấy được cấu hình đăng nhập của trang SCADA.');
    return;
  }

  page.config = await response.json();
  page.session = core.createSession({ config: page.config, redirectUri: REDIRECT_URI, fetch: (...a) => fetch(...a), now: Date.now });
  page.api = core.createApi({ fetch: (...a) => fetch(...a), session: page.session, storage: sessionStorage, now: Date.now });

  const href = location.href;
  const result = await core.completeLogin({ href, storage: sessionStorage, session: page.session });
  if (result.kind !== 'none') history.replaceState(null, '', core.cleanUrl(href));

  if (result.kind === 'none') {
    // Tokens are kept in memory only, so a fresh page always logs in (Keycloak's session makes it quick).
    await login();
    return;
  }

  if (result.kind === 'error') {
    showLoginProblem(result.reason === 'login_error'
      ? 'Keycloak báo đăng nhập không thành công.'
      : 'Phiên đăng nhập không hợp lệ hoặc đã dùng rồi.');
    return;
  }

  $('logout').hidden = false;
  $('logout').addEventListener('click', logout);
  $('edge').addEventListener('change', () => guard(selectEdge($('edge').value)));
  $('trendWindow').addEventListener('change', () => guard(loadTrend()));
  $('trendClose').addEventListener('click', closeTrend);

  startLive();
  setInterval(() => guard(keepTokenFresh()), 10_000);
  setInterval(renderStatus, 5_000);
  await loadEdges();
}

async function login() {
  setStatus('Đang chuyển tới trang đăng nhập…', 'idle');
  location.assign(await core.beginLogin({ config: page.config, crypto: window.crypto, storage: sessionStorage, redirectUri: REDIRECT_URI }));
}

function logout() {
  page.live?.stop();
  const { action, fields } = page.session.logoutForm(REDIRECT_URI);
  page.session.clear();

  // A POST keeps the id token out of the URL (form-action allows the identity provider and its redirect back here).
  const form = document.createElement('form');
  form.method = 'post';
  form.action = action;
  for (const [name, value] of Object.entries(fields)) {
    const input = document.createElement('input');
    input.type = 'hidden';
    input.name = name;
    input.value = value;
    form.appendChild(input);
  }

  document.body.appendChild(form);
  form.submit();
}

async function keepTokenFresh() {
  if (page.fatal) return; // the page has stopped; a new login would only end in the same message
  try {
    await page.session.accessToken();
  } catch (error) {
    if (error instanceof core.LoginRequired) await login();
  }
}

/** Returns the data, or null after handling the answer (login, stop, message). */
async function apiGet(path) {
  if (page.fatal) return null; // e.g. a retry timer firing after the page stopped
  const result = await page.api.get(path);
  page.lastFailure = result.status === 'ok' ? null : result.status;
  switch (result.status) {
    case 'ok':
      hideBanner();
      return result.data;
    case 'login':
      await login();
      return null;
    case 'rejected':
      showLoginProblem('Cloud không chấp nhận token vừa nhận. Kiểm tra cấu hình Keycloak (file hosts, địa chỉ issuer) rồi đăng nhập lại.');
      return null;
    case 'forbidden':
      showFatal('Tài khoản không có quyền xem (cần role Viewer).');
      return null;
    case 'network':
      showBanner('Mất kết nối tới Cloud. Trang sẽ thử lại.');
      return null;
    default:
      showBanner(`Cloud trả lỗi ${result.code}.`);
      return null;
  }
}

// ---- Edges and live values ----

async function loadEdges() {
  const edges = await apiGet('/api/v1/edges');
  if (edges === null) {
    if (page.lastFailure === 'network') setTimeout(() => guard(loadEdges()), NETWORK_RETRY_MS);
    return;
  }

  if (edges.length === 0) {
    setStatus('Chưa cấu hình Edge nào trên Cloud.', 'idle');
    return;
  }

  const select = $('edge');
  select.replaceChildren(...edges.map((edge) => {
    const option = document.createElement('option');
    option.value = edge.edgeId;
    option.textContent = edge.displayName === edge.edgeId ? edge.edgeId : `${edge.displayName} (${edge.edgeId})`;
    return option;
  }));
  select.disabled = false;
  await selectEdge(edges[0].edgeId);
}

async function selectEdge(edgeId) {
  closeTrend();
  const generation = page.table.select(edgeId);
  render();
  guard(page.live.switchEdge());
  await loadLatest(edgeId, generation);
}

async function loadLatest(edgeId, generation) {
  const latest = await apiGet(`/api/v1/edges/${encodeURIComponent(edgeId)}/tags/latest`);
  if (latest !== null) {
    if (page.table.applyLatest(generation, latest, Date.now())) render();
  } else if (page.lastFailure === 'network') {
    // Retried only while this selection is current; a newer selection runs its own load.
    setTimeout(() => {
      if (page.table.generation === generation) guard(loadLatest(edgeId, generation));
    }, NETWORK_RETRY_MS);
  }
}

function startLive() {
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl('/hubs/live', { accessTokenFactory: () => page.session.accessToken() })
    .withAutomaticReconnect()
    .configureLogging(window.signalR.LogLevel.None)
    .build();
  page.live = core.createLiveConnection({
    connection,
    table: page.table,
    schedule: (fn, ms) => setTimeout(fn, ms),
    now: Date.now,
    onChange: render,
    onState: (state) => {
      page.liveState = state;
      renderStatus();
    },
  });
  page.live.start();
}

function render() {
  const rows = page.table.rows();
  const body = $('tags');
  body.replaceChildren(...rows.map((row) => {
    const tr = document.createElement('tr');
    tr.appendChild(cell(row.tagId, 'mono'));
    tr.appendChild(cell(core.formatValue(row.value), 'value'));
    const [text, cls] = QUALITY[row.quality] ?? [String(row.quality), 'idle'];
    tr.appendChild(badgeCell(text, cls));
    tr.appendChild(cell(new Date(row.time).toLocaleTimeString('vi-VN', { hour12: false }), 'mono'));
    const action = document.createElement('td');
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = 'Trend';
    button.setAttribute('aria-label', `Trend ${row.tagId}`);
    button.addEventListener('click', () => openTrend(row.tagId));
    action.appendChild(button);
    tr.appendChild(action);
    return tr;
  }));
  $('empty').hidden = rows.length > 0 || !page.table.loaded;
  renderStatus();
}

function renderStatus() {
  if (page.fatal || page.table.edgeId === null) return;
  if (!page.table.loaded) {
    setStatus('Đang tải…', 'idle');
  } else if (page.liveState === 'disconnected' || page.liveState === 'reconnecting') {
    setStatus('Mất kết nối realtime, đang kết nối lại…', 'warn');
  } else if (page.liveState === 'join-failed') {
    setStatus('Không theo dõi được Edge này (Cloud không nhận Edge).', 'bad');
  } else if (page.table.isStale(Date.now())) {
    setStatus('Không nhận dữ liệu mới', 'warn');
  } else {
    setStatus('Đang cập nhật', 'ok');
  }
}

// ---- trend ----

function openTrend(tagId) {
  page.trendTag = tagId;
  $('trendTitle').textContent = tagId;
  $('trend').hidden = false;
  guard(loadTrend());
}

function closeTrend() {
  page.trendTag = null;
  page.trend.next(); // drop any answer still on its way
  $('trend').hidden = true;
}

async function loadTrend() {
  if (page.trendTag === null) return;
  const request = page.trend.next();
  const minutes = Number($('trendWindow').value);
  const toMs = Date.now();
  const fromMs = toMs - minutes * 60_000;
  $('trendNotice').textContent = 'Đang tải…';

  const history = await apiGet(core.historyUrl(page.table.edgeId, page.trendTag, minutes, toMs));
  if (!page.trend.isCurrent(request)) return;
  if (history === null) {
    $('trendNotice').textContent = 'Không tải được dữ liệu trend. Chọn lại khoảng để thử lại.';
    return;
  }

  const segments = core.trendSegments(history.items);
  const geometry = core.chartGeometry(segments, { fromMs, toMs, width: 800, height: 240 });
  const svg = $('chart');
  const ns = 'http://www.w3.org/2000/svg';
  svg.replaceChildren(...geometry.polylines.map((points) => {
    const line = document.createElementNS(ns, 'polyline');
    line.setAttribute('points', points);
    return line;
  }));
  $('chartMax').textContent = segments.length ? core.formatValue(geometry.max) : '';
  $('chartMin').textContent = segments.length ? core.formatValue(geometry.min) : '';
  $('trendNotice').textContent = core.truncationNotice(history) ?? (segments.length ? '' : 'Không có mẫu hợp lệ trong khoảng này.');
}

// ---- DOM helpers ----

function cell(text, cls) {
  const td = document.createElement('td');
  td.textContent = text;
  if (cls) td.className = cls;
  return td;
}

function badgeCell(text, cls) {
  const td = document.createElement('td');
  const span = document.createElement('span');
  span.className = `badge ${cls}`;
  span.textContent = text;
  td.appendChild(span);
  return td;
}

function setStatus(text, cls) {
  const status = $('status');
  status.textContent = text;
  status.className = `badge ${cls}`;
}

function showBanner(text) {
  $('banner').textContent = text;
  $('banner').hidden = false;
}

function hideBanner() {
  $('banner').hidden = true;
}

function showFatal(text) {
  page.fatal = true;
  page.live?.stop();
  $('content').hidden = true;
  showBanner(text);
  setStatus('Dừng', 'bad');
}

function showLoginProblem(text) {
  showFatal(text);
  const again = $('loginAgain');
  again.hidden = false;
  again.onclick = () => login();
}
