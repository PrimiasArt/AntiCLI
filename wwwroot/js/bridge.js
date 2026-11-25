// AntiCLI Native WebView2 IPC Bridge

const pending = new Map();
const listeners = new Map();
let seq = 1;

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
    const set = listeners.get(msg.event);
    if (set) {
      for (const fn of set) {
        try { fn(msg.payload); } catch (e) { console.error(e); }
      }
    }
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', (ev) => dispatch(ev.data));
}

export function invoke(action, payload = {}) {
  if (!window.chrome || !window.chrome.webview) {
    return Promise.reject(new Error('WebView2 bridge unavailable'));
  }
  const id = `r${seq++}`;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, action, payload });
  });
}

export function on(eventName, handler) {
  if (!listeners.has(eventName)) listeners.set(eventName, new Set());
  listeners.get(eventName).add(handler);
  return () => listeners.get(eventName)?.delete(handler);
}
