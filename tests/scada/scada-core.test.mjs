// Tests for the SCADA page logic (src/Cloud/Scada/scada-core.js). Run: node --test tests/scada/scada-core.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import * as core from '../../src/Cloud/Scada/scada-core.js';

const CONFIG = { authority: 'https://keycloak:8443/realms/iiot', clientId: 'iiot-scada' };
const REDIRECT = 'http://localhost:8080/scada/';
const TOKEN_URL = 'https://keycloak:8443/realms/iiot/protocol/openid-connect/token';
const ACCESS = 'access-token-value';
const REFRESH = 'refresh-token-value';
const ID_TOKEN = 'id-token-value';
const SECRETS = [ACCESS, REFRESH, ID_TOKEN, 'access-2', 'refresh-2'];

/** sessionStorage stand-in that remembers every write. */
function recordingStorage() {
  const map = new Map();
  const writes = [];
  return {
    writes,
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { writes.push([k, String(v)]); map.set(k, String(v)); },
    removeItem: (k) => map.delete(k),
    keys: () => [...map.keys()],
  };
}

/** fetch stand-in: answers with the queued responses and records every call. */
function fakeFetch(...responses) {
  const calls = [];
  const fn = async (url, init = {}) => {
    calls.push({ url: String(url), init });
    const next = responses.shift();
    if (next instanceof Error) throw next;
    const { status = 200, body = {} } = next ?? {};
    return { status, ok: status >= 200 && status < 300, json: async () => body };
  };
  fn.calls = calls;
  return fn;
}

const tokenBody = (access = ACCESS, refresh = REFRESH, expiresIn = 300) =>
  ({ status: 200, body: { access_token: access, refresh_token: refresh, id_token: ID_TOKEN, expires_in: expiresIn } });

async function loginFlow(storage) {
  const url = new URL(await core.beginLogin({ config: CONFIG, crypto: webcrypto, storage, redirectUri: REDIRECT }));
  return url.searchParams.get('state');
}

async function signedInSession({ now = () => 0, responses = [] } = {}) {
  const fetch = fakeFetch(tokenBody(), ...responses);
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now });
  await session.exchangeCode('code-1', 'verifier-1');
  return { session, fetch };
}

// ---- AC-03: login ----

test('beginLogin builds an openid S256 authorize URL and stores only state and verifier', async () => {
  const storage = recordingStorage();
  const url = new URL(await core.beginLogin({ config: CONFIG, crypto: webcrypto, storage, redirectUri: REDIRECT }));

  assert.equal(url.origin + url.pathname, 'https://keycloak:8443/realms/iiot/protocol/openid-connect/auth');
  const p = url.searchParams;
  assert.equal(p.get('response_type'), 'code');
  assert.equal(p.get('client_id'), 'iiot-scada');
  assert.equal(p.get('redirect_uri'), REDIRECT);
  assert.equal(p.get('scope'), 'openid');
  assert.equal(p.get('code_challenge_method'), 'S256');
  assert.deepEqual(storage.keys().sort(), [core.STORAGE_KEYS.state, core.STORAGE_KEYS.verifier].sort());

  // state is at least 128 bits of randomness (32 bytes, base64url) and is the one sent.
  const state = storage.getItem(core.STORAGE_KEYS.state);
  assert.equal(p.get('state'), state);
  assert.match(state, /^[A-Za-z0-9_-]{43}$/);

  // challenge = base64url(SHA-256(verifier)); the verifier itself never leaves the browser.
  const verifier = storage.getItem(core.STORAGE_KEYS.verifier);
  assert.match(verifier, /^[A-Za-z0-9_-]{43,128}$/);
  const digest = new Uint8Array(await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
  assert.equal(p.get('code_challenge'), Buffer.from(digest).toString('base64url'));
  assert.equal(url.toString().includes(verifier), false);
});

test('beginLogin uses a new state each time', async () => {
  const a = await loginFlow(recordingStorage());
  const b = await loginFlow(recordingStorage());
  assert.notEqual(a, b);
});

test('a page without callback parameters is not a callback', () => {
  assert.deepEqual(core.readCallback('http://localhost:8080/scada/', recordingStorage()), { kind: 'none' });
});

for (const [name, query] of [
  ['missing state', '?code=abc'],
  ['wrong state', '?code=abc&state=forged'],
  ['error from the identity provider', '?error=access_denied&error_description=evil%20text&state=STATE'],
]) {
  test(`callback with ${name} is rejected and never requests a token`, async () => {
    const storage = recordingStorage();
    const state = await loginFlow(storage);
    const fetch = fakeFetch(tokenBody());
    const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now: () => 0 });

    const result = await core.completeLogin({ href: REDIRECT + query.replace('STATE', state), storage, session });

    assert.equal(result.kind, 'error');
    assert.equal(fetch.calls.length, 0);
    assert.equal(session.signedIn, false);
    // The identity provider's text is never shown as is.
    assert.equal(JSON.stringify(result).includes('evil'), false);
  });
}

test('callback with the right state exchanges the code once and clears state and verifier', async () => {
  const storage = recordingStorage();
  const state = await loginFlow(storage);
  const verifier = storage.getItem(core.STORAGE_KEYS.verifier);
  const fetch = fakeFetch(tokenBody());
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now: () => 5_000 });

  const result = await core.completeLogin({ href: `${REDIRECT}?state=${state}&code=the-code&session_state=x`, storage, session, now: () => 5_000 });

  assert.equal(result.kind, 'signed-in');
  assert.equal(session.signedIn, true);
  assert.equal(fetch.calls.length, 1);
  assert.equal(fetch.calls[0].url, TOKEN_URL);
  assert.equal(fetch.calls[0].init.method, 'POST');
  assert.deepEqual(Object.fromEntries(new URLSearchParams(fetch.calls[0].init.body)), {
    grant_type: 'authorization_code', client_id: 'iiot-scada', code: 'the-code', code_verifier: verifier, redirect_uri: REDIRECT,
  });
  assert.equal(storage.getItem(core.STORAGE_KEYS.state), null);
  assert.equal(storage.getItem(core.STORAGE_KEYS.verifier), null);
  assert.equal(storage.getItem(core.STORAGE_KEYS.loginAt), '5000');
});

test('a replayed callback fails because the state was already used', async () => {
  const storage = recordingStorage();
  const state = await loginFlow(storage);
  const href = `${REDIRECT}?state=${state}&code=c`;
  const fetch = fakeFetch(tokenBody(), tokenBody());
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now: () => 0 });

  assert.equal((await core.completeLogin({ href, storage, session })).kind, 'signed-in');
  assert.equal((await core.completeLogin({ href, storage, session })).kind, 'error');
  assert.equal(fetch.calls.length, 1);
});

test('cleanUrl removes every login parameter and keeps the rest', () => {
  assert.equal(core.cleanUrl(`${REDIRECT}?state=s&code=c&session_state=x&iss=y&keep=1#top`), '/scada/?keep=1#top');
  assert.equal(core.cleanUrl(`${REDIRECT}?error=e&error_description=d`), '/scada/');
});

test('tokens are never written to storage and never put into a URL the module builds', async () => {
  const storage = recordingStorage();
  const state = await loginFlow(storage);
  let clock = 0;
  const fetch = fakeFetch(tokenBody(), tokenBody('access-2', 'refresh-2'), { status: 200, body: [] });
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now: () => clock });
  await core.completeLogin({ href: `${REDIRECT}?state=${state}&code=c`, storage, session, now: () => clock });
  clock = 290_000; // inside the refresh window
  const api = core.createApi({ fetch, session, storage, now: () => clock });
  await api.get('/api/v1/edges');

  for (const [, value] of storage.writes) {
    for (const secret of SECRETS) assert.equal(value.includes(secret), false, 'a token reached storage');
  }

  const built = [
    await core.beginLogin({ config: CONFIG, crypto: webcrypto, storage, redirectUri: REDIRECT }),
    core.historyUrl('edge-a', 'Line1.Speed', 15, Date.UTC(2026, 9, 28)),
    session.logoutForm(REDIRECT).action,
    ...fetch.calls.map((c) => c.url),
  ];
  for (const url of built) {
    for (const secret of SECRETS) assert.equal(url.includes(secret), false, `a token reached a URL: ${url}`);
  }

  // The API call carries the token in the header only.
  const apiCall = fetch.calls[2];
  assert.equal(apiCall.init.headers.Authorization, 'Bearer access-2');
});

test('logout is a form POST with the id token as a field, not in the URL', async () => {
  const { session } = await signedInSession();
  const form = session.logoutForm(REDIRECT);
  assert.equal(form.action, 'https://keycloak:8443/realms/iiot/protocol/openid-connect/logout');
  assert.deepEqual(form.fields, { client_id: 'iiot-scada', post_logout_redirect_uri: REDIRECT, id_token_hint: ID_TOKEN });
});

// ---- token refresh ----

test('accessToken refreshes only when less than 30 s are left, once for concurrent callers', async () => {
  let clock = 0;
  const { session, fetch } = await signedInSession({ now: () => clock, responses: [tokenBody('access-2', 'refresh-2')] });

  clock = 269_999; // 30.001 s left
  assert.equal(await session.accessToken(), ACCESS);
  assert.equal(fetch.calls.length, 1);

  clock = 270_001; // 29.999 s left
  const [a, b] = await Promise.all([session.accessToken(), session.accessToken()]);
  assert.equal(a, 'access-2');
  assert.equal(b, 'access-2');
  assert.equal(fetch.calls.length, 2);
  assert.deepEqual(Object.fromEntries(new URLSearchParams(fetch.calls[1].init.body)), {
    grant_type: 'refresh_token', client_id: 'iiot-scada', refresh_token: REFRESH,
  });
});

test('a failed refresh signs out and asks for a new login', async () => {
  let clock = 0;
  const { session } = await signedInSession({ now: () => clock, responses: [{ status: 400, body: { error: 'invalid_grant' } }] });
  clock = 299_000;

  await assert.rejects(session.accessToken(), core.LoginRequired);
  assert.equal(session.signedIn, false);
});

test('without a session accessToken asks for a login', async () => {
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch: fakeFetch(), now: () => 0 });
  await assert.rejects(session.accessToken(), core.LoginRequired);
});

// ---- AC-06: API answers ----

test('401 long after the login starts a new login', async () => {
  const storage = recordingStorage();
  core.markLoginCompleted(storage, 0);
  const { session, fetch } = await signedInSession({ now: () => 61_000, responses: [{ status: 401 }] });
  const api = core.createApi({ fetch, session, storage, now: () => 61_000 });
  assert.equal((await api.get('/api/v1/edges')).status, 'login');
});

test('401 within 60 s of a completed login stops instead of looping', async () => {
  const storage = recordingStorage();
  core.markLoginCompleted(storage, 10_000);
  const { session, fetch } = await signedInSession({ now: () => 69_999, responses: [{ status: 401 }] });
  const api = core.createApi({ fetch, session, storage, now: () => 69_999 });
  assert.equal((await api.get('/api/v1/edges')).status, 'rejected');
});

test('two 401s in a row stop; a success in between resets the count', async () => {
  const storage = recordingStorage();
  const { session, fetch } = await signedInSession({ responses: [{ status: 401 }, { status: 200, body: [] }, { status: 401 }, { status: 401 }] });
  const api = core.createApi({ fetch, session, storage, now: () => 1_000_000 });

  assert.equal((await api.get('/a')).status, 'login');
  assert.equal((await api.get('/a')).status, 'ok');
  assert.equal((await api.get('/a')).status, 'login');
  assert.equal((await api.get('/a')).status, 'rejected');
});

test('403 is a no-permission answer, other failures are errors', async () => {
  const { session, fetch } = await signedInSession({ responses: [{ status: 403 }, { status: 500 }, new TypeError('offline'), { status: 200, body: { x: 1 } }] });
  const api = core.createApi({ fetch, session, storage: recordingStorage(), now: () => 0 });

  assert.equal((await api.get('/a')).status, 'forbidden');
  assert.deepEqual(await api.get('/a'), { status: 'error', code: 500 });
  assert.equal((await api.get('/a')).status, 'network');
  assert.deepEqual(await api.get('/a'), { status: 'ok', data: { x: 1 } });
});

test('an API call without a session asks for a login without calling the API', async () => {
  const fetch = fakeFetch();
  const session = core.createSession({ config: CONFIG, redirectUri: REDIRECT, fetch, now: () => 0 });
  const api = core.createApi({ fetch, session, storage: recordingStorage(), now: () => 0 });
  assert.equal((await api.get('/a')).status, 'login');
  assert.equal(fetch.calls.length, 0);
});

// ---- AC-04: live table ----

const sample = (tagId, timestampUtc, value = 1, edgeId = 'edge-a') => ({ edgeId, tagId, value, quality: 'good', timestampUtc });

test('a newer push wins whatever the timestamp format; an older one is ignored', () => {
  const table = core.createLiveTable();
  const gen = table.select('edge-a');
  table.applyLatest(gen, [{ tagId: 't', value: 1, quality: 'good', timestampUtc: '2026-10-28T08:00:00Z' }], 0);

  // As strings "…00.500Z" < "…00Z"; as times it is 500 ms newer.
  assert.equal(table.applyPush(sample('t', '2026-10-28T08:00:00.500Z', 2), 0), true);
  assert.equal(table.rows()[0].value, 2);

  assert.equal(table.applyPush(sample('t', '2026-10-28T08:00:00.400Z', 3), 0), false);
  assert.equal(table.applyPush(sample('t', '2026-10-28T08:00:00.500Z', 4), 0), false); // same time: keep the first
  assert.equal(table.rows()[0].value, 2);
});

test('the initial latest load merges and never overwrites a newer push that arrived first', () => {
  const table = core.createLiveTable();
  const gen = table.select('edge-a');
  table.applyPush(sample('t', '2026-10-28T08:00:05.000Z', 'pushed'), 0);

  table.applyLatest(gen, [
    { tagId: 't', value: 'stale', quality: 'good', timestampUtc: '2026-10-28T08:00:01Z' },
    { tagId: 'u', value: 7, quality: 'bad', timestampUtc: '2026-10-28T08:00:01.5Z' },
  ], 0);

  assert.deepEqual(table.rows().map((r) => [r.tagId, r.value]), [['t', 'pushed'], ['u', 7]]);
});

test('pushes for another Edge are ignored; an unknown tag is added in tag order', () => {
  const table = core.createLiveTable();
  const gen = table.select('edge-a');
  table.applyLatest(gen, [{ tagId: 'b', value: 1, quality: 'good', timestampUtc: '2026-10-28T08:00:00Z' }], 0);

  assert.equal(table.applyPush(sample('b', '2026-10-28T09:00:00Z', 9, 'edge-b'), 0), false);
  assert.equal(table.applyPush(sample('a', '2026-10-28T08:00:01Z', 5), 0), true);

  assert.deepEqual(table.rows().map((r) => [r.tagId, r.value]), [['a', 5], ['b', 1]]);
});

test('a latest response from an earlier selection is dropped', () => {
  const table = core.createLiveTable();
  const genA = table.select('edge-a');
  const genB = table.select('edge-b');

  assert.equal(table.applyLatest(genA, [{ tagId: 'x', value: 1, quality: 'good', timestampUtc: '2026-10-28T08:00:00Z' }], 0), false);
  assert.deepEqual(table.rows(), []);
  assert.equal(table.applyLatest(genB, [], 0), true);
});

test('selecting another Edge clears the rows', () => {
  const table = core.createLiveTable();
  table.applyLatest(table.select('edge-a'), [{ tagId: 'x', value: 1, quality: 'good', timestampUtc: '2026-10-28T08:00:00Z' }], 0);
  table.select('edge-b');
  assert.deepEqual(table.rows(), []);
  assert.equal(table.edgeId, 'edge-b');
});

test('the table is stale only after more than 30 s without data for the selected Edge', () => {
  const table = core.createLiveTable();
  const gen = table.select('edge-a');
  assert.equal(table.isStale(1_000_000), false); // nothing loaded yet: loading, not stale
  assert.equal(table.loaded, false);
  table.applyLatest(gen, [], 1_000);
  assert.equal(table.loaded, true);
  assert.equal(table.isStale(31_000), false);
  assert.equal(table.isStale(31_001), true);
  table.applyPush(sample('t', '2026-10-28T08:00:00Z'), 31_001);
  assert.equal(table.isStale(61_001), false);
  table.applyPush(sample('t', '2026-10-28T09:00:00Z', 1, 'edge-b'), 70_000); // another Edge does not count
  assert.equal(table.isStale(61_002), true);
});

// ---- AC-04: connection lifecycle ----

function fakeConnection({ failStarts = 0 } = {}) {
  const handlers = {};
  const invocations = [];
  let starts = 0;
  const connection = {
    state: 'Disconnected',
    invocations,
    on: (name, fn) => { handlers[name] = fn; },
    onreconnecting: (fn) => { handlers.reconnecting = fn; },
    onreconnected: (fn) => { handlers.reconnected = fn; },
    onclose: (fn) => { handlers.close = fn; },
    start: async () => {
      starts++;
      if (starts <= failStarts) throw new Error('unreachable');
      connection.state = 'Connected';
    },
    invoke: async (method, arg) => { invocations.push([method, arg]); },
    get starts() { return starts; },
    emit: (name, ...args) => handlers[name]?.(...args),
  };
  return connection;
}

const flush = () => new Promise((r) => setImmediate(r));

function recordingSchedule() {
  const delays = [];
  const schedule = (fn, ms) => { delays.push(ms); queueMicrotask(fn); };
  schedule.delays = delays;
  return schedule;
}

test('start joins the selected Edge and pushes reach the table', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  let changes = 0;
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0, onChange: () => changes++ });

  await live.start();
  connection.emit('telemetry', sample('t', '2026-10-28T08:00:00Z'));

  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-a']]);
  assert.equal(changes, 1);
  assert.equal(table.rows().length, 1);
});

test('after an automatic reconnect the current selection is joined again', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0 });
  await live.start();

  table.select('edge-b'); // changed while the connection was down
  await live.switchEdge().catch(() => {});
  connection.invocations.length = 0;
  connection.emit('reconnected');
  await flush();

  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-b']]);
});

test('a closed connection (e.g. token expiry) restarts with backoff forever and re-joins', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  const schedule = recordingSchedule();
  const live = core.createLiveConnection({ connection, table, schedule, now: () => 0 });
  await live.start();

  // The next seven starts fail, then one succeeds.
  let failures = 7;
  connection.start = async () => {
    if (failures-- > 0) throw new Error('down');
    connection.state = 'Connected';
  };
  connection.state = 'Disconnected';
  connection.invocations.length = 0;
  connection.emit('close', new Error('token expired'));
  for (let i = 0; i < 50; i++) await flush();

  assert.deepEqual(schedule.delays, [1000, 2000, 5000, 10000, 30000, 30000, 30000]);
  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-a']]);
});

test('switching Edge while the hub is down does not report it connected; the next start joins', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  const states = [];
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0, onState: (s) => states.push(s) });
  await live.start();
  connection.state = 'Reconnecting';
  states.length = 0;
  connection.invocations.length = 0;

  table.select('edge-b');
  await live.switchEdge();
  assert.deepEqual(states, []);
  assert.deepEqual(connection.invocations, []);

  connection.state = 'Connected';
  connection.emit('reconnected');
  await flush();
  assert.deepEqual(states, ['connected']);
  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-b']]);
});

test('switching Edge leaves the old group and joins the new one', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0 });
  await live.start();

  table.select('edge-b');
  await live.switchEdge();

  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-a'], ['LeaveEdge', 'edge-a'], ['JoinEdge', 'edge-b']]);
});

test('a join that completes after the selection moved on is left again', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  let release;
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0 });
  connection.invoke = async (method, arg) => {
    connection.invocations.push([method, arg]);
    if (method === 'JoinEdge' && arg === 'edge-a') await new Promise((r) => { release = r; });
  };
  const starting = live.start();
  await flush();
  table.select('edge-b');
  release();
  await starting;

  assert.deepEqual(connection.invocations, [['JoinEdge', 'edge-a'], ['LeaveEdge', 'edge-a']]);
});

test('a stopped connection is closed and does not restart', async () => {
  const table = core.createLiveTable();
  table.select('edge-a');
  const connection = fakeConnection();
  let stops = 0;
  connection.stop = async () => { stops++; };
  const schedule = recordingSchedule();
  const live = core.createLiveConnection({ connection, table, schedule, now: () => 0 });
  await live.start();
  live.stop();
  connection.emit('close');
  await flush();
  assert.equal(stops, 1);
  assert.equal(connection.starts, 1);
  assert.deepEqual(schedule.delays, []);
});

test('a failed join is reported and a later successful join clears it', async () => {
  const table = core.createLiveTable();
  table.select('edge-x');
  const connection = fakeConnection();
  connection.invoke = async (method, arg) => {
    if (method === 'JoinEdge' && arg === 'edge-x') throw new Error('edge_not_found');
  };
  const states = [];
  const live = core.createLiveConnection({ connection, table, schedule: recordingSchedule(), now: () => 0, onState: (s) => states.push(s) });
  await live.start();
  table.select('edge-a');
  await live.switchEdge();
  assert.deepEqual(states, ['join-failed', 'connected']);
});

// ---- AC-05: trend ----

test('the history URL is one 15 min or 1 h window in ISO UTC with limit 10000 and encoded ids', () => {
  const now = Date.UTC(2026, 9, 28, 8, 30, 0, 250);
  assert.equal(
    core.historyUrl('edge a', 'Line1/Speed', 15, now),
    '/api/v1/edges/edge%20a/tags/Line1%2FSpeed/history?from=2026-10-28T08%3A15%3A00.250Z&to=2026-10-28T08%3A30%3A00.250Z&limit=10000');
  assert.match(core.historyUrl('e', 't', 60, now), /from=2026-10-28T07%3A30%3A00\.250Z&to=2026-10-28T08%3A30%3A00\.250Z&limit=10000$/);
  assert.throws(() => core.historyUrl('e', 't', 480, now), RangeError);
  assert.throws(() => core.historyUrl('e', 't', 1440, now), RangeError);
});

test('a truncated page shows the notice, a complete one does not', () => {
  assert.equal(core.truncationNotice({ items: [], truncated: true }), 'Chỉ hiển thị 10 000 mẫu đầu tiên của khoảng này');
  assert.equal(core.truncationNotice({ items: [], truncated: false }), null);
});

test('numbers form a line; null and bad samples break it into segments', () => {
  const t = (s) => `2026-10-28T08:00:0${s}Z`;
  const segments = core.trendSegments([
    { value: 1.5, quality: 'good', timestampUtc: t(0) },
    { value: 2, quality: 'good', timestampUtc: t(1) },
    { value: null, quality: 'bad', timestampUtc: t(2) },
    { value: 4, quality: 'good', timestampUtc: t(3) },
    { value: 99, quality: 'bad', timestampUtc: t(4) }, // bad with a value is still a gap
    { value: 5, quality: 'uncertain', timestampUtc: t(5) },
  ]);
  const ms = (s) => Date.parse(t(s));
  assert.deepEqual(segments, [[{ t: ms(0), y: 1.5 }, { t: ms(1), y: 2 }], [{ t: ms(3), y: 4 }], [{ t: ms(5), y: 5 }]]);
});

test('a Bool tag is a 0/1 step line that holds each value until the next sample', () => {
  const t = (s) => `2026-10-28T08:00:0${s}Z`;
  const ms = (s) => Date.parse(t(s));
  const segments = core.trendSegments([
    { value: false, quality: 'good', timestampUtc: t(0) },
    { value: true, quality: 'good', timestampUtc: t(2) },
    { value: true, quality: 'good', timestampUtc: t(3) },
    { value: false, quality: 'good', timestampUtc: t(5) },
  ]);
  assert.deepEqual(segments, [[
    { t: ms(0), y: 0 }, { t: ms(2), y: 0 }, { t: ms(2), y: 1 }, { t: ms(3), y: 1 }, { t: ms(5), y: 1 }, { t: ms(5), y: 0 },
  ]]);
});

test('chart geometry scales points into the box with time on x and value on y', () => {
  const geometry = core.chartGeometry([[{ t: 0, y: 0 }, { t: 50, y: 10 }]], { fromMs: 0, toMs: 100, width: 200, height: 100 });
  assert.deepEqual(geometry.polylines, ['0,100 100,0']);
  assert.equal(geometry.min, 0);
  assert.equal(geometry.max, 10);

  // A flat line is drawn in the middle, not divided by zero.
  const flat = core.chartGeometry([[{ t: 0, y: 5 }, { t: 100, y: 5 }]], { fromMs: 0, toMs: 100, width: 200, height: 100 });
  assert.deepEqual(flat.polylines, ['0,50 200,50']);
});

test('a trend latch drops answers to an earlier request', () => {
  const latch = core.createLatch();
  const first = latch.next();
  const second = latch.next();
  assert.equal(latch.isCurrent(first), false);
  assert.equal(latch.isCurrent(second), true);
});

// ---- display helpers ----

test('values are formatted for display', () => {
  assert.equal(core.formatValue(true), 'TRUE');
  assert.equal(core.formatValue(false), 'FALSE');
  assert.equal(core.formatValue(null), '—');
  assert.equal(core.formatValue(12.5), '12.5');
});
