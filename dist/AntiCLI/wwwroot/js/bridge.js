// AntiCLI Dual-Transport Bridge: Native WebView2 IPC (Desktop) OR Authenticated WebSocket (Web Server)

const pending = new Map();
const listeners = new Map();
let seq = 1;

const HAS_WEBVIEW2 = Boolean(window.chrome && window.chrome.webview);
let ws = null;
let wsRoomId = 'default';
let wsReconnectTimer = null;
let wsShouldReconnect = false;
let wsBackoffMs = 800;

export function isServerMode() {
  return !HAS_WEBVIEW2;
}

function emitLocal(eventName, payload) {
  const set = listeners.get(eventName);
  if (!set) return;
  for (const fn of set) {
    try { fn(payload); } catch (e) { console.error(e); }
  }
}

function dispatch(raw) {
  let msg = raw;
  if (typeof raw === 'string') {
    try { msg = JSON.parse(raw); } catch { return; }
  }
  if (!msg || typeof msg !== 'object') return;

  if (msg.kind === 'response' && msg.id) {
    const entry = pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);
    if (msg.ok) entry.resolve(msg.data);
    else entry.reject(new Error(msg.error || 'Bridge error'));
    return;
  }

  if (msg.kind === 'event' && msg.event) {
    emitLocal(msg.event, msg.payload);
  }
}

if (HAS_WEBVIEW2) {
  window.chrome.webview.addEventListener('message', (ev) => dispatch(ev.data));
}

export async function checkServerAuth() {
  const res = await fetch('/api/auth/status', { credentials: 'same-origin' });
  return await res.json();
}

export async function loginServer(password, remember = true) {
  const res = await fetch('/api/auth/login', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password, remember })
  });
  return await res.json();
}

export async function logoutServer() {
  wsShouldReconnect = false;
  if (ws) { try { ws.close(); } catch {} ws = null; }
  const res = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
  return await res.json();
}

export async function changeServerPassword(currentPassword, newPassword, revokeOthers = true) {
  const res = await fetch('/api/auth/change-password', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ currentPassword, newPassword, revokeOthers })
  });
  return await res.json();
}

export async function revokeAllServerSessions() {
  wsShouldReconnect = false;
  const res = await fetch('/api/auth/revoke-all', { method: 'POST', credentials: 'same-origin' });
  if (ws) { try { ws.close(); } catch {} ws = null; }
  return await res.json();
}

export function connectWebSocket(roomId = 'default') {
  if (HAS_WEBVIEW2) return Promise.resolve();
  wsRoomId = roomId || 'default';
  wsShouldReconnect = true;
  if (wsReconnectTimer) { clearTimeout(wsReconnectTimer); wsReconnectTimer = null; }

  return new Promise((resolve, reject) => {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/ws?room=${encodeURIComponent(wsRoomId)}`;
    if (ws) {
      try { ws.onclose = null; ws.close(); } catch {}
    }

    const sock = new WebSocket(url);
    ws = sock;
    let settled = false;

    sock.onopen = () => {
      wsBackoffMs = 800;
      if (!settled) { settled = true; resolve(); }
      emitLocal('bridge:connected', { roomId: wsRoomId });
    };

    sock.onmessage = (ev) => dispatch(ev.data);

    sock.onerror = () => {
      if (!settled) {
        settled = true;
        reject(new Error('Không thể kết nối WebSocket tới AntiCLI Server.'));
      }
    };

    sock.onclose = async (ev) => {
      for (const [, p] of pending) p.reject(new Error('Mất kết nối WebSocket'));
      pending.clear();

      if (ev.code === 1008) {
        wsShouldReconnect = false;
        emitLocal('bridge:unauthorized', { reason: ev.reason || 'Phiên đã bị thu hồi.' });
        return;
      }

      emitLocal('bridge:disconnected', { code: ev.code });

      if (wsShouldReconnect) {
        try {
          const st = await checkServerAuth();
          if (!st.authenticated) {
            wsShouldReconnect = false;
            emitLocal('bridge:unauthorized', { reason: 'Phiên đăng nhập đã hết hạn.' });
            return;
          }
        } catch {}

        wsReconnectTimer = setTimeout(() => {
          wsBackoffMs = Math.min(8000, Math.round(wsBackoffMs * 1.5));
          connectWebSocket(wsRoomId).catch(() => {});
        }, wsBackoffMs);
      }
    };
  });
}

export function invoke(action, payload = {}) {
  const id = `r${seq++}`;
  if (HAS_WEBVIEW2) {
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, action, payload });
    });
  }

  if (!ws || ws.readyState !== WebSocket.OPEN) {
    return Promise.reject(new Error('Chưa kết nối tới máy chủ WebSocket.'));
  }

  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, action, payload }));
  });
}

export function on(eventName, handler) {
  if (!listeners.has(eventName)) listeners.set(eventName, new Set());
  listeners.get(eventName).add(handler);
  return () => listeners.get(eventName)?.delete(handler);
}
