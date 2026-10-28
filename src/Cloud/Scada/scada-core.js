// SCADA page logic without the DOM: login (Authorization Code + PKCE), token handling, API answers, the live table,
// the hub connection lifecycle and trend data. Everything from outside (crypto, storage, fetch, clock, hub connection,
// timers) is passed in, so tests can drive it. Tokens live in memory only: never in storage, a URL or the console.

export const STORAGE_KEYS = Object.freeze({
  state: 'scada.state',
  verifier: 'scada.verifier',
  loginAt: 'scada.loginAt', // when the last login completed (ms); not a secret
});
export const TREND_WINDOWS_MIN = Object.freeze([15, 60]);
export const HISTORY_LIMIT = 10_000;
export const STALE_AFTER_MS = 30_000;
export const REFRESH_BEFORE_MS = 30_000;
export const LOGIN_GUARD_MS = 60_000;
export const RESTART_DELAYS_MS = Object.freeze([1_000, 2_000, 5_000, 10_000, 30_000]);

const CALLBACK_PARAMS = ['code', 'state', 'session_state', 'iss', 'error', 'error_description', 'error_uri'];

/** Thrown when there is no usable session and the user must log in again. */
export class LoginRequired extends Error {
  constructor() {
    super('login_required');
    this.name = 'LoginRequired';
  }
}

// ---- login ----

function base64Url(bytes) {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function randomToken(crypto, byteCount) {
  const bytes = new Uint8Array(byteCount);
  crypto.getRandomValues(bytes);
  return base64Url(bytes);
}

/** Keycloak's OpenID Connect endpoints below the realm URL (the Cloud's Auth:Authority). */
export function endpoints(authority) {
  const base = authority.replace(/\/+$/, '') + '/protocol/openid-connect';
  return { authorize: `${base}/auth`, token: `${base}/token`, logout: `${base}/logout` };
}

/** Stores a fresh state and PKCE verifier and returns the URL to send the browser to. */
export async function beginLogin({ config, crypto, storage, redirectUri }) {
  const state = randomToken(crypto, 32); // 256 bits
  const verifier = randomToken(crypto, 48); // 64 characters, within RFC 7636's 43..128
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  storage.setItem(STORAGE_KEYS.state, state);
  storage.setItem(STORAGE_KEYS.verifier, verifier);

  const url = new URL(endpoints(config.authority).authorize);
  url.search = new URLSearchParams({
    response_type: 'code',
    client_id: config.clientId,
    redirect_uri: redirectUri,
    scope: 'openid',
    state,
    code_challenge: base64Url(new Uint8Array(digest)),
    code_challenge_method: 'S256',
  }).toString();
  return url.toString();
}

/**
 * Reads the login callback from the page URL. State and verifier are removed from storage whatever the outcome, so a
 * callback can be used once. The identity provider's error text is never returned: it comes from the URL.
 */
export function readCallback(href, storage) {
  const params = new URL(href).searchParams;
  if (!params.has('code') && !params.has('state') && !params.has('error')) {
    return { kind: 'none' };
  }

  const expected = storage.getItem(STORAGE_KEYS.state);
  const verifier = storage.getItem(STORAGE_KEYS.verifier);
  storage.removeItem(STORAGE_KEYS.state);
  storage.removeItem(STORAGE_KEYS.verifier);

  if (params.has('error')) return { kind: 'error', reason: 'login_error' };
  const state = params.get('state');
  if (!expected || !verifier || !state || state !== expected) return { kind: 'error', reason: 'state_mismatch' };
  const code = params.get('code');
  if (!code) return { kind: 'error', reason: 'missing_code' };
  return { kind: 'code', code, verifier };
}

/** Exchanges a valid callback's code for tokens; nothing is requested for an invalid one. */
export async function completeLogin({ href, storage, session, now = Date.now }) {
  const callback = readCallback(href, storage);
  if (callback.kind !== 'code') return callback;
  try {
    await session.exchangeCode(callback.code, callback.verifier);
  } catch {
    return { kind: 'error', reason: 'token_request_failed' };
  }

  markLoginCompleted(storage, now());
  return { kind: 'signed-in' };
}

/** The page URL without login parameters (path, remaining query and hash), for history.replaceState. */
export function cleanUrl(href) {
  const url = new URL(href);
  for (const name of CALLBACK_PARAMS) url.searchParams.delete(name);
  return url.pathname + url.search + url.hash;
}

export function markLoginCompleted(storage, nowMs) {
  storage.setItem(STORAGE_KEYS.loginAt, String(nowMs));
}

// ---- tokens ----

/** Holds the tokens in memory and refreshes the access token when it has less than 30 s left. */
export function createSession({ config, redirectUri, fetch, now }) {
  const urls = endpoints(config.authority);
  let tokens = null; // { access, refresh, id, expiresAt }
  let refreshing = null;

  async function requestTokens(fields) {
    const response = await fetch(urls.token, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(fields).toString(),
    });
    if (!response.ok) throw new Error('token_request_failed');
    const body = await response.json();
    if (typeof body.access_token !== 'string' || typeof body.expires_in !== 'number') {
      throw new Error('token_response_invalid');
    }

    tokens = {
      access: body.access_token,
      refresh: typeof body.refresh_token === 'string' ? body.refresh_token : null,
      id: typeof body.id_token === 'string' ? body.id_token : tokens?.id ?? null,
      expiresAt: now() + body.expires_in * 1000,
    };
  }

  async function refresh() {
    try {
      if (!tokens?.refresh) throw new Error('no_refresh_token');
      await requestTokens({ grant_type: 'refresh_token', client_id: config.clientId, refresh_token: tokens.refresh });
    } catch {
      tokens = null;
      throw new LoginRequired();
    }
  }

  return {
    get signedIn() {
      return tokens !== null;
    },

    exchangeCode(code, verifier) {
      return requestTokens({
        grant_type: 'authorization_code',
        client_id: config.clientId,
        code,
        code_verifier: verifier,
        redirect_uri: redirectUri,
      });
    },

    /** The current access token, refreshed first when it expires within 30 s; one refresh serves every caller. */
    async accessToken() {
      if (!tokens) throw new LoginRequired();
      if (tokens.expiresAt - now() >= REFRESH_BEFORE_MS) return tokens.access;
      refreshing ??= refresh().finally(() => {
        refreshing = null;
      });
      await refreshing;
      return tokens.access;
    },

    /** Logout as a form POST, so the id token travels in the body and never in a URL. */
    logoutForm(postLogoutRedirectUri) {
      const fields = { client_id: config.clientId, post_logout_redirect_uri: postLogoutRedirectUri };
      if (tokens?.id) fields.id_token_hint = tokens.id;
      return { action: urls.logout, fields };
    },

    clear() {
      tokens = null;
    },
  };
}

// ---- API ----

/**
 * GET with the access token. Returns { status: 'ok', data } | 'login' (start a login) | 'rejected' (401 right after a
 * login or twice in a row: logging in again would loop) | 'forbidden' | 'error' (with code) | 'network'.
 */
export function createApi({ fetch, session, storage, now }) {
  let unauthorizedInARow = 0;

  return {
    async get(path) {
      let token;
      try {
        token = await session.accessToken();
      } catch (error) {
        if (error instanceof LoginRequired) return { status: 'login' };
        throw error;
      }

      let response;
      try {
        response = await fetch(path, { headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' }, cache: 'no-store' });
      } catch {
        return { status: 'network' };
      }

      if (response.status === 401) {
        unauthorizedInARow++;
        const loginAt = Number(storage.getItem(STORAGE_KEYS.loginAt));
        const justLoggedIn = loginAt > 0 && now() - loginAt < LOGIN_GUARD_MS;
        return { status: justLoggedIn || unauthorizedInARow >= 2 ? 'rejected' : 'login' };
      }

      unauthorizedInARow = 0;
      if (response.status === 403) return { status: 'forbidden' };
      if (!response.ok) return { status: 'error', code: response.status };
      return { status: 'ok', data: await response.json() };
    },
  };
}

// ---- live table ----

const byTagId = (a, b) => (a.tagId < b.tagId ? -1 : a.tagId > b.tagId ? 1 : 0);

/**
 * Rows of the selected Edge. Each selection gets a generation; a latest response for an older one is dropped. Rows
 * merge by tag and the newer sample wins, compared as times (the API and the hub format timestamps differently).
 */
export function createLiveTable() {
  let edgeId = null;
  let generation = 0;
  let lastDataMs = null;
  const rows = new Map();

  function merge(item) {
    const time = Date.parse(item.timestampUtc);
    if (typeof item.tagId !== 'string' || !Number.isFinite(time)) return false;
    const current = rows.get(item.tagId);
    if (current && current.time >= time) return false;
    rows.set(item.tagId, { tagId: item.tagId, value: item.value ?? null, quality: item.quality, timestampUtc: item.timestampUtc, time });
    return true;
  }

  return {
    get edgeId() {
      return edgeId;
    },

    get generation() {
      return generation;
    },

    select(id) {
      edgeId = id;
      generation++;
      rows.clear();
      lastDataMs = null;
      return generation;
    },

    /** Merges a latest response; false when it belongs to an earlier selection. */
    applyLatest(forGeneration, items, nowMs) {
      if (forGeneration !== generation) return false;
      for (const item of items) merge(item);
      lastDataMs = nowMs;
      return true;
    },

    /** Merges a hub push; false when it is for another Edge or not newer than the row. */
    applyPush(sample, nowMs) {
      if (edgeId === null || sample?.edgeId !== edgeId) return false;
      lastDataMs = nowMs;
      return merge(sample);
    },

    rows() {
      return [...rows.values()].sort(byTagId);
    },

    /** False until the first latest response or push for the selected Edge arrived. */
    get loaded() {
      return lastDataMs !== null;
    },

    /** No data for the selected Edge for more than 30 s (false while the first load is pending). */
    isStale(nowMs) {
      return lastDataMs !== null && nowMs - lastDataMs > STALE_AFTER_MS;
    },
  };
}

// ---- hub connection ----

/**
 * Drives a SignalR connection: joins the selected Edge after every start and reconnect, and restarts a closed
 * connection (including the server closing it when the token expires) with backoff 1, 2, 5, 10, 30 s, forever.
 */
export function createLiveConnection({ connection, table, schedule, now, onChange = () => {}, onState = () => {} }) {
  let joined = null;
  let stopped = false;
  let attempt = 0;

  const delay = (ms) => new Promise((resolve) => schedule(resolve, ms));

  async function join() {
    const target = table.edgeId;
    if (target === null || connection.state !== 'Connected') return; // joined on the next (re)connect
    await connection.invoke('JoinEdge', target);
    if (table.edgeId === target) {
      joined = target;
    } else {
      await connection.invoke('LeaveEdge', target).catch(() => {}); // the selection moved on meanwhile
    }
  }

  async function joinReporting() {
    try {
      await join();
      // Clears an earlier join failure, but only when the hub is really up (not while reconnecting or backing off).
      if (connection.state === 'Connected') onState('connected');
    } catch {
      onState('join-failed');
    }
  }

  async function run() {
    while (!stopped) {
      try {
        await connection.start();
        attempt = 0;
        joined = null;
        await joinReporting();
        return;
      } catch {
        onState('disconnected');
        await delay(RESTART_DELAYS_MS[Math.min(attempt++, RESTART_DELAYS_MS.length - 1)]);
      }
    }
  }

  connection.on('telemetry', (sample) => {
    if (table.applyPush(sample, now())) onChange();
  });
  connection.onreconnecting(() => onState('reconnecting'));
  connection.onreconnected(() => {
    joined = null; // groups belong to the old connection
    return joinReporting();
  });
  connection.onclose(() => {
    joined = null;
    if (!stopped) {
      onState('disconnected');
      run();
    }
  });

  return {
    start: run,

    /** Call after table.select(): leaves the previous Edge's group and joins the new one. */
    async switchEdge() {
      if (joined !== null && joined !== table.edgeId && connection.state === 'Connected') {
        const previous = joined;
        joined = null;
        await connection.invoke('LeaveEdge', previous).catch(() => {});
      }

      await joinReporting();
    },

    /** Ends the connection for good: no restart, no more pushes. */
    stop() {
      stopped = true;
      Promise.resolve(connection.stop?.()).catch(() => {});
    },
  };
}

// ---- trend ----

/** One history request: the last 15 or 60 minutes, ISO-8601 UTC with Z, at most 10 000 samples. */
export function historyUrl(edgeId, tagId, windowMinutes, nowMs) {
  if (!TREND_WINDOWS_MIN.includes(windowMinutes)) throw new RangeError('window must be 15 or 60 minutes');
  const query = new URLSearchParams({
    from: new Date(nowMs - windowMinutes * 60_000).toISOString(),
    to: new Date(nowMs).toISOString(),
    limit: String(HISTORY_LIMIT),
  });
  return `/api/v1/edges/${encodeURIComponent(edgeId)}/tags/${encodeURIComponent(tagId)}/history?${query}`;
}

export function truncationNotice(page) {
  return page.truncated ? 'Chỉ hiển thị 10 000 mẫu đầu tiên của khoảng này' : null;
}

/**
 * History items → line segments of { t, y }. Numbers are drawn as given, Bool as 0/1 held until the next sample (a
 * step), and a bad sample or a null or non-finite value ends the segment (a gap).
 */
export function trendSegments(items) {
  const segments = [];
  let current = null;
  for (const item of items) {
    const t = Date.parse(item.timestampUtc);
    const v = item.quality === 'bad' ? null : item.value;
    const isBool = typeof v === 'boolean';
    const y = isBool ? (v ? 1 : 0) : typeof v === 'number' && Number.isFinite(v) ? v : null;
    if (y === null || !Number.isFinite(t)) {
      current = null;
      continue;
    }

    if (current === null) {
      current = [];
      segments.push(current);
    } else if (isBool && current.at(-1).y !== y) {
      current.push({ t, y: current.at(-1).y });
    }

    current.push({ t, y });
  }

  return segments;
}

/** Segments → SVG polyline point lists in a width × height box (time on x over [fromMs, toMs], value on y). */
export function chartGeometry(segments, { fromMs, toMs, width, height }) {
  const ys = segments.flat().map((p) => p.y);
  const min = ys.length ? Math.min(...ys) : 0;
  const max = ys.length ? Math.max(...ys) : 1;
  const flat = min === max;
  const span = toMs - fromMs || 1;
  const x = (t) => round(((t - fromMs) / span) * width);
  const y = (v) => (flat ? round(height / 2) : round(height - ((v - min) / (max - min)) * height));
  return {
    polylines: segments.map((segment) => segment.map((p) => `${x(p.t)},${y(p.y)}`).join(' ')),
    min,
    max,
  };
}

const round = (n) => Math.round(n * 10) / 10;

/** Numbered requests; only the newest one's answer is used. */
export function createLatch() {
  let current = 0;
  return {
    next: () => ++current,
    isCurrent: (n) => n === current,
  };
}

// ---- display ----

export function formatValue(value) {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  return String(value);
}
