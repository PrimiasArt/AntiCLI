import {
  invoke,
  on,
  isServerMode,
  checkServerAuth,
  loginServer,
  logoutServer,
  changeServerPassword,
  revokeAllServerSessions,
  connectWebSocket
} from './bridge.js';
import {
  escapeHtml,
  renderLineNumberedCode,
  renderMarkdown,
  computeLineDiff,
  renderUnifiedDiffHtml,
  renderSplitDiffHtml
} from './highlight.js';

// ============================================================================
// State
// ============================================================================
const state = {
  workspace: '',
  workspaceName: '',
  settings: null,
  cli: null,
  git: { available: false, isRepo: false, branch: '', files: [] },
  expandedDirs: new Set(),
  selectedFile: null,
  fileEditMode: false,
  fileOriginalContent: '',
  currentDiff: null, // { path, oldText, newText }
  diffSplitMode: false,
  sessionDiffs: new Map(), // path -> { path, oldText, newText, status }

  // ACP / Session
  connState: 'connecting',
  sessionId: null,
  sessionTitle: 'Phiên mới',
  configOptions: [],
  availableCommands: [],
  sessions: [],
  busy: false,
  activeTurn: null, // { el, thoughtEl, thoughtBodyEl, thoughtText, textEl, rawText, tools: Map, startedAt }
  pendingPermKey: null,
  pendingPermOptions: [],
  streamTurnRaw: '',

  // Composer
  attachments: [], // { kind: 'file'|'image', name, path?, content?, mimeType?, data? }
  history: [],
  historyIdx: -1,
  acItems: [],
  acSelected: 0,
  acMode: null, // '/' | '@' | null
  acStartPos: 0,

  // Terminal
  termStarted: false,
  termHistory: [],
  termHistIdx: -1,

  // Palette
  paletteItems: [],
  paletteIdx: 0,

  // Server / Multi-Room Mode
  serverMode: isServerMode(),
  currentRoomId: localStorage.getItem('anticli_room') || 'default',
  rooms: [],
  serverInfo: null,
  folderBrowsePath: '',
  folderBrowseParent: '',

  // Auto-scroll
  autoScroll: true
};

const LOCAL_SLASH_COMMANDS = [
  { name: 'help', description: 'Hiển thị danh sách lệnh CLI và phím tắt', local: true },
  { name: 'new', description: 'Tạo phiên làm việc mới sạch (Ctrl+N)', local: true },
  { name: 'clear', description: 'Xóa màn hình timeline hiện tại (Ctrl+L)', local: true },
  { name: 'usage', description: 'Kiểm tra hạn mức / quota Antigravity qua agy CLI', local: true },
  { name: 'credits', description: 'Xem số dư AI credits qua agy CLI', local: true },
  { name: 'model', description: 'Đổi nhanh model (vd: /model gemini-4-neon)', local: true },
  { name: 'effort', description: 'Đổi mức thinking (low, medium, high, xhigh)', local: true },
  { name: 'mode', description: 'Đổi chế độ (default, accept-edits, plan)', local: true },
  { name: 'diff', description: 'Mở Diff Inspector xem thay đổi mới nhất', local: true },
  { name: 'terminal', description: 'Bật/tắt PowerShell Terminal tích hợp (Ctrl+J)', local: true },
  { name: 'settings', description: 'Mở cài đặt & nguồn xác thực (Ctrl+,)', local: true },
  { name: 'reconnect', description: 'Khởi động lại kết nối Antigravity ACP/CLI', local: true },
  { name: 'login', description: 'Mở cửa sổ đăng nhập Google cho agy CLI', local: true }
];

const $ = (id) => document.getElementById(id);

// ============================================================================
// Bootstrap
// ============================================================================
async function boot() {
  bindUIEvents();
  bindBridgeEvents();
  setupResizers();

  // Start with sidebar collapsed on narrow mobile viewports
  if (window.innerWidth <= 720) {
    $('sidebar').classList.add('hidden');
    $('resizer-left').classList.add('hidden');
    $('btn-toggle-sidebar').classList.remove('active');
  }

  if (state.serverMode) {
    $('room-picker-wrap').classList.remove('hidden');
    $('settings-server-section').classList.remove('hidden');
    try {
      const authStatus = await checkServerAuth();
      if (authStatus.machineName) {
        $('auth-host-label').textContent = `Máy chủ: ${authStatus.machineName} — nhập mật khẩu để mở khóa phiên lập trình từ xa.`;
      }
      if (!authStatus.authenticated) {
        showAuthOverlay();
        return;
      }
      await startServerWorkbench();
    } catch (err) {
      showAuthOverlay(`Không thể kiểm tra máy chủ: ${err.message}`);
    }
    return;
  }

  await initializeWorkbenchData();
  startMetricsPolling();
}

let metricsTimer = null;
function startMetricsPolling() {
  if (metricsTimer) return;
  metricsTimer = setInterval(async () => {
    try {
      const m = await invoke('app:metrics');
      updateMetricsUi(m);
    } catch {}
  }, 8000);
}

function showAuthOverlay(errorMsg = '') {
  $('auth-overlay').classList.remove('hidden');
  const errEl = $('auth-error');
  if (errorMsg) {
    errEl.textContent = errorMsg;
    errEl.classList.remove('hidden');
  } else {
    errEl.classList.add('hidden');
  }
  setTimeout(() => $('auth-password-input').focus(), 40);
}

async function startServerWorkbench() {
  $('auth-overlay').classList.add('hidden');
  try {
    setConnStatus('connecting', 'Đang nối WebSocket…');
    await connectWebSocket(state.currentRoomId || 'default');
    await initializeWorkbenchData();
    startMetricsPolling();
  } catch (err) {
    toast(`Lỗi kết nối Server: ${err.message}`, 'err');
  }
}

async function initializeWorkbenchData() {
  try {
    const init = await invoke('app:init');
    await applyInitPayload(init);
  } catch (err) {
    toast(`Lỗi khởi tạo: ${err.message}`, 'err');
  }
}

async function applyInitPayload(init) {
  applySettingsToUi(init.settings);
  state.cli = init.cli;
  state.sessionDiffs.clear();
  updateWorkspaceUi(init.workspace, init.workspaceName, init.git, init.tree);
  updateMetricsUi(init.metrics);

  if (init.serverInfo) state.serverInfo = init.serverInfo;
  if (Array.isArray(init.rooms)) {
    state.rooms = init.rooms;
    renderRoomsUi();
  }

  if (init.roomSnapshot) {
    hydrateRoomSnapshot(init.roomSnapshot);
    // Connect ACP lazily if this room hasn't started ACP yet
    if (init.roomSnapshot.connState !== 'connected') {
      await connectAntigravity();
    } else {
      await refreshSessions();
    }
  } else {
    await connectAntigravity();
  }
}

function hydrateRoomSnapshot(snap) {
  clearTimeline();
  if (!snap) return;

  if (snap.room?.id) {
    state.currentRoomId = snap.room.id;
    try { localStorage.setItem('anticli_room', snap.room.id); } catch {}
    renderRoomsUi();
  }
  if (snap.sessionId) {
    state.sessionId = snap.sessionId;
    $('session-id-pill').textContent = snap.sessionId.slice(0, 8);
    $('session-id-pill').classList.remove('hidden');
  }
  state.sessionTitle = snap.sessionTitle || 'Phiên mới';
  $('session-title').textContent = state.sessionTitle;

  if (snap.tier?.name) showTierBadge(snap.tier.name);
  if (Array.isArray(snap.configOptions)) applyConfigOptions(snap.configOptions);
  if (Array.isArray(snap.availableCommands)) state.availableCommands = snap.availableCommands;
  updateContextMeter(snap.ctxUsed || 0, snap.ctxSize || 1048576);
  setBusy(!!snap.busy);

  if (snap.connState) setConnStatus(snap.connState, snap.connLabel || snap.connState);

  // Replay buffered room history so late-joining devices see live shared session immediately
  if (Array.isArray(snap.replay) && snap.replay.length > 0) {
    for (const ev of snap.replay) {
      replaySingleEvent(ev.event, ev.payload);
    }
    scrollToBottom(true);
  }
}

function replaySingleEvent(eventName, payload) {
  if (eventName === 'room:user_message') {
    state.activeTurn = null;
    appendUserBubble(payload?.text || '', []);
  } else if (eventName === 'acp:update') {
    handleAcpSessionUpdate(payload);
  } else if (eventName === 'acp:permission_request') {
    handlePermissionRequest(payload);
  } else if (eventName === 'room:permission_resolved') {
    markPermissionCardResolved(payload?.requestKey, payload?.optionId, payload?.cancelled);
  } else if (eventName === 'room:turn_done') {
    finalizeActiveTurn(payload?.usage || null, payload?.error || null);
  } else if (eventName === 'stream:event') {
    handleStreamJsonEvent(payload);
  }
}

async function connectAntigravity() {
  setConnStatus('connecting', 'Đang kết nối…');
  $('auth-banner').classList.add('hidden');
  try {
    const res = await invoke('acp:connect');
    if (res && res.ok) {
      if (state.settings?.transportMode === 'stream-json') {
        setConnStatus('connected', 'Stream-JSON CLI');
        $('transport-pill').textContent = 'STREAM-JSON';
        await loadCliModelsFallback();
      } else {
        applySessionPayload(res.session, res.tier);
        const ver = res.agentInfo?.version ? `v${res.agentInfo.version}` : '';
        const authTag = state.settings?.authMode === 'gemini-api-key' ? 'API Key' : 'ACP';
        setConnStatus('connected', `${authTag} ${ver}`.trim());
        $('transport-pill').textContent = 'ACP';
        await refreshSessions();
      }
    } else if (res && res.authRequired) {
      setConnStatus('auth_required', 'Cần đăng nhập');
      $('auth-banner-msg').textContent = res.error || 'Hãy đăng nhập Google hoặc dùng Gemini API Key trong Settings.';
      $('auth-banner').classList.remove('hidden');
    } else {
      setConnStatus('error', 'Lỗi kết nối');
      appendSystemMessage(`Không thể kết nối Antigravity: ${res?.error || 'Lỗi không xác định'}`, true);
    }
  } catch (err) {
    setConnStatus('error', 'Lỗi kết nối');
    appendSystemMessage(`Lỗi kết nối Antigravity: ${err.message}`, true);
  }
}

async function loadCliModelsFallback() {
  try {
    const res = await invoke('cli:models');
    if (res?.ok && Array.isArray(res.models)) {
      const sel = $('select-model');
      sel.innerHTML = res.models.map((m) => `<option value="${escapeHtml(m.id)}">${escapeHtml(m.name)}</option>`).join('');
      if (state.settings?.defaultModel) sel.value = state.settings.defaultModel;
    }
  } catch {}
}

// ============================================================================
// Bridge Events
// ============================================================================
function bindBridgeEvents() {
  on('acp:status', (p) => {
    if (!p) return;
    if (p.state === 'connected') {
      const ver = p.agentInfo?.version ? `v${p.agentInfo.version}` : '';
      const prefix = p.authMode === 'gemini-api-key' ? 'API Key' : 'ACP';
      setConnStatus('connected', `${prefix} ${ver}`.trim());
      if (p.tier?.name) showTierBadge(p.tier.name);
      else if (p.authMode === 'gemini-api-key') showTierBadge('Gemini API Key');
      $('auth-banner').classList.add('hidden');
    } else if (p.state === 'connecting') {
      setConnStatus('connecting', 'Đang kết nối…');
    } else if (p.state === 'auth_required') {
      setConnStatus('auth_required', 'Cần đăng nhập');
      $('auth-banner').classList.remove('hidden');
    } else if (p.state === 'error' || p.state === 'disconnected') {
      setConnStatus('error', p.state === 'disconnected' ? 'Ngắt kết nối' : 'Lỗi');
    }
  });

  on('acp:update', (p) => handleAcpSessionUpdate(p));
  on('acp:permission_request', (p) => handlePermissionRequest(p));
  on('acp:wire', (p) => appendWireEntry(p.dir, p.json));
  on('acp:log', (p) => appendWireEntry('err', `[${p.direction}] ${p.text}`));

  on('stream:event', (ev) => handleStreamJsonEvent(ev));
  on('stream:status', (p) => {
    if (p?.state === 'connected') setConnStatus('connected', 'Stream-JSON');
    else setConnStatus('error', 'Stream dừng');
  });

  on('workspace:changed', async (p) => {
    await refreshFileTree();
    await refreshGitStatus();
    if (state.selectedFile && !state.fileEditMode && p?.path && p.path.toLowerCase().endsWith(state.selectedFile.relativePath.replace(/\//g, '\\').toLowerCase())) {
      await openFileInInspector(state.selectedFile.relativePath, false);
    }
  });

  on('terminal:output', (p) => appendTerminalOutput(p.text, p.stream === 'stderr'));
  on('terminal:status', (p) => {
    $('terminal-cwd').textContent = p?.cwd || state.workspace;
  });

  // Server Multi-Room Live Sync & Auth Events
  on('room:user_message', (p) => {
    state.activeTurn = null;
    appendUserBubble(p?.text || '', []);
  });
  on('room:busy', (p) => setBusy(!!p?.busy));
  on('room:permission_resolved', (p) => markPermissionCardResolved(p?.requestKey, p?.optionId, p?.cancelled));
  on('room:turn_done', (p) => {
    finalizeActiveTurn(p?.usage || null, p?.error || null);
    setBusy(false);
    refreshGitStatus();
  });
  on('room:cleared', (p) => {
    clearTimeline();
    state.sessionTitle = 'Phiên mới';
    $('session-title').textContent = state.sessionTitle;
    $('plan-bar').classList.add('hidden');
    updateContextMeter(0, 1048576);
    if (p?.session) applySessionPayload(p.session, p.tier);
  });
  on('room:workspace_changed', (p) => {
    if (!p) return;
    applySettingsToUi(p.settings);
    state.sessionDiffs.clear();
    clearTimeline();
    updateWorkspaceUi(p.workspace, p.workspaceName, p.git, p.tree);
    if (p.connection?.ok && p.connection.session) applySessionPayload(p.connection.session, p.connection.tier);
  });
  on('room:config_updated', (p) => {
    if (Array.isArray(p?.configOptions)) applyConfigOptions(p.configOptions);
  });
  on('server:rooms', (p) => {
    if (Array.isArray(p?.rooms)) state.rooms = p.rooms;
    if (p?.serverInfo) state.serverInfo = p.serverInfo;
    renderRoomsUi();
  });
  on('bridge:disconnected', () => {
    setConnStatus('connecting', 'Đang kết nối lại…');
  });
  on('bridge:connected', () => {
    if (state.serverMode && state.connState === 'connecting') {
      initializeWorkbenchData();
    }
  });
  on('bridge:unauthorized', (p) => {
    showAuthOverlay(p?.reason || 'Phiên đăng nhập đã hết hạn hoặc bị thu hồi.');
  });
}

// ============================================================================
// ACP Session & Stream Handling
// ============================================================================
function applySessionPayload(session, tier) {
  if (!session) return;
  if (session.sessionId) {
    state.sessionId = session.sessionId;
    const pill = $('session-id-pill');
    pill.textContent = session.sessionId.slice(0, 8);
    pill.title = `Session ID: ${session.sessionId}`;
    pill.classList.remove('hidden');
  }
  if (Array.isArray(session.configOptions)) {
    applyConfigOptions(session.configOptions);
  }
  if (tier?.name) {
    showTierBadge(tier.name);
  } else if (session._meta?.antigravity?.tier?.name) {
    showTierBadge(session._meta.antigravity.tier.name);
  }
}

function applyConfigOptions(options) {
  state.configOptions = options || [];
  for (const opt of state.configOptions) {
    if (opt.id === 'model' || opt.category === 'model') {
      fillSelect($('select-model'), opt.options || [], opt.currentValue);
    } else if (opt.id === 'thought_level' || opt.category === 'thought_level') {
      fillSelect($('select-thought'), opt.options || [], opt.currentValue);
    } else if (opt.id === 'mode' || opt.category === 'mode') {
      fillSelect($('select-mode'), opt.options || [], opt.currentValue);
      styleModeSelect(opt.currentValue);
    }
  }
}

function fillSelect(sel, items, currentValue) {
  sel.innerHTML = items.map((o) => {
    const val = o.value ?? o.id ?? '';
    const label = o.name ?? val;
    return `<option value="${escapeHtml(val)}">${escapeHtml(label)}</option>`;
  }).join('');
  if (currentValue !== undefined && currentValue !== null) {
    sel.value = currentValue;
  }
}

function styleModeSelect(mode) {
  const sel = $('select-mode');
  sel.style.color = mode === 'accept-edits' ? 'var(--green)'
    : mode === 'plan' ? 'var(--purple)'
    : 'var(--text-0)';
}

function handleAcpSessionUpdate(payload) {
  const upd = payload?.update;
  if (!upd || !upd.sessionUpdate) return;
  const kind = upd.sessionUpdate;

  if (kind === 'available_commands_update') {
    state.availableCommands = Array.isArray(upd.availableCommands) ? upd.availableCommands : [];
    return;
  }

  if (kind === 'config_option_update') {
    if (Array.isArray(upd.configOptions)) applyConfigOptions(upd.configOptions);
    return;
  }

  if (kind === 'session_info_update') {
    if (upd.title) {
      state.sessionTitle = upd.title;
      $('session-title').textContent = upd.title;
    }
    return;
  }

  if (kind === 'usage_update') {
    updateContextMeter(upd.used || 0, upd.size || 1048576);
    return;
  }

  if (kind === 'plan') {
    renderPlanBar(upd.entries || []);
    return;
  }

  if (kind === 'user_message_chunk') {
    // Replayed user turn during session/load
    const txt = extractChunkText(upd.content);
    if (txt) {
      hideWelcome();
      appendUserBubble(txt, []);
      state.activeTurn = null;
    }
    return;
  }

  if (kind === 'agent_thought_chunk') {
    const turn = ensureActiveTurn();
    const chunk = extractChunkText(upd.content);
    if (!chunk) return;
    ensureThoughtCard(turn);
    turn.thoughtText += chunk;
    turn.thoughtBodyEl.textContent = turn.thoughtText;
    scrollToBottomIfPinned();
    return;
  }

  if (kind === 'agent_message_chunk') {
    const turn = ensureActiveTurn();
    const chunk = extractChunkText(upd.content);
    if (!chunk) return;
    ensureAgentTextEl(turn);
    turn.rawText += chunk;
    scheduleMarkdownRender(turn);
    scrollToBottomIfPinned();
    return;
  }

  if (kind === 'tool_call' || kind === 'tool_call_update') {
    const turn = ensureActiveTurn();
    upsertToolCard(turn, upd);
    scrollToBottomIfPinned();
  }
}

function extractChunkText(content) {
  if (!content) return '';
  if (typeof content === 'string') return content;
  if (typeof content.text === 'string') return content.text;
  if (Array.isArray(content)) return content.map(extractChunkText).join('');
  return '';
}

function handleStreamJsonEvent(ev) {
  if (!ev || !ev.event) return;
  if (ev.event === 'init') {
    if (ev.conversation_id) {
      state.sessionId = ev.conversation_id;
      $('session-id-pill').textContent = ev.conversation_id.slice(0, 8);
      $('session-id-pill').classList.remove('hidden');
    }
    return;
  }
  if (ev.event === 'step_update') {
    const su = ev.step_update || {};
    const turn = ensureActiveTurn();
    if (su.step_type === 'agent_response' && su.text_delta) {
      ensureAgentTextEl(turn);
      turn.rawText += su.text_delta;
      scheduleMarkdownRender(turn);
      scrollToBottomIfPinned();
    } else if (su.step_type === 'tool') {
      const tid = `step_${su.step_index}`;
      upsertToolCard(turn, {
        toolCallId: tid,
        name: su.tool_name || su.tool_info?.name || 'tool',
        title: su.tool_info?.parameters?.CommandLine || su.tool_info?.parameters?.Path || su.tool_name || 'Tool',
        kind: su.tool_name === 'run_command' ? 'execute' : 'tool',
        status: su.state === 'DONE' ? (su.tool_info?.error ? 'failed' : 'completed') : 'in_progress',
        rawInput: su.tool_info?.parameters,
        rawOutput: su.tool_info?.output ?? su.tool_info?.error
      });
      scrollToBottomIfPinned();
    }
    return;
  }
  if (ev.event === 'result') {
    const res = ev.result || {};
    if (state.activeTurn) {
      if (res.response && !state.activeTurn.rawText) {
        ensureAgentTextEl(state.activeTurn);
        state.activeTurn.rawText = res.response;
      }
      finalizeActiveTurn(res.usage ? {
        inputTokens: res.usage.input_tokens,
        outputTokens: res.usage.output_tokens,
        thoughtTokens: res.usage.thinking_tokens,
        totalTokens: res.usage.total_tokens
      } : null, res.error || null);
    }
    setBusy(false);
    refreshGitStatus();
  }
}

function handlePermissionRequest(payload) {
  const { requestKey, params } = payload || {};
  if (!requestKey || !params) return;

  const turn = ensureActiveTurn();
  const tc = params.toolCall || {};
  const options = Array.isArray(params.options) ? params.options : [
    { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
    { optionId: 'deny', name: 'Deny', kind: 'reject_once' }
  ];

  state.pendingPermKey = requestKey;
  state.pendingPermOptions = options;

  const card = document.createElement('div');
  card.className = 'perm-card';
  card.dataset.requestKey = requestKey;

  const cmdPreview = tc.args?.CommandLine || tc.rawInput?.CommandLine || tc.title || tc.name || 'Thao tác cần xác nhận';
  const extraDetail = tc.args && Object.keys(tc.args).length > 0
    ? JSON.stringify(tc.args, null, 2)
    : '';

  const btnHtml = options.map((opt, idx) => {
    const isAllow = (opt.kind || '').includes('allow') || opt.optionId === 'allow';
    const cls = isAllow ? 'btn-primary-sm' : 'btn-danger-sm';
    const keyHint = isAllow && idx === 0 ? ' [Y]' : (!isAllow ? ' [N]' : '');
    return `<button class="${cls} perm-opt-btn" data-opt-id="${escapeHtml(opt.optionId)}">${escapeHtml(opt.name)}${keyHint}</button>`;
  }).join('');

  card.innerHTML = `
    <div class="perm-title">
      <span>⚠ Yêu cầu cấp quyền: ${escapeHtml(tc.name || tc.kind || 'Tool')}</span>
      <span class="mono" style="font-size:11px;opacity:.85">Đang chờ bạn duyệt</span>
    </div>
    <div class="perm-detail">${escapeHtml(cmdPreview)}${extraDetail && extraDetail !== cmdPreview ? '\n\n' + escapeHtml(extraDetail) : ''}</div>
    <div class="perm-actions">${btnHtml}</div>
  `;

  card.querySelectorAll('.perm-opt-btn').forEach((btn) => {
    btn.addEventListener('click', () => resolvePermission(requestKey, btn.dataset.optId, false, card, btn.textContent));
  });

  turn.el.appendChild(card);
  scrollToBottom(true);
}

async function resolvePermission(requestKey, optionId, cancelled, cardEl, chosenLabel) {
  if (state.pendingPermKey === requestKey) {
    state.pendingPermKey = null;
    state.pendingPermOptions = [];
  }
  try {
    await invoke('acp:respondPermission', { requestKey, optionId: optionId || '', cancelled: !!cancelled });
    markPermissionCardResolved(requestKey, chosenLabel || optionId, cancelled, cardEl);
  } catch (err) {
    toast(`Lỗi phản hồi quyền: ${err.message}`, 'err');
  }
}

function markPermissionCardResolved(requestKey, label, cancelled, explicitCard = null) {
  if (state.pendingPermKey === requestKey) {
    state.pendingPermKey = null;
    state.pendingPermOptions = [];
  }
  const card = explicitCard || document.querySelector(`.perm-card[data-request-key="${requestKey}"]`);
  if (card && !card.classList.contains('resolved')) {
    card.classList.add('resolved');
    const act = card.querySelector('.perm-actions');
    if (act) act.innerHTML = `<span class="muted-label mono">Đã chọn: ${escapeHtml(cancelled ? 'Cancelled' : (label || 'OK'))}</span>`;
  }
}

// ============================================================================
// Timeline Rendering Helpers
// ============================================================================
function hideWelcome() {
  $('welcome-view').classList.add('hidden');
}

function appendUserBubble(text, attachments = []) {
  hideWelcome();
  const el = document.createElement('div');
  el.className = 'msg-user';
  const now = new Date().toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  let attHtml = '';
  if (attachments.length > 0) {
    attHtml = '<div class="msg-attachments">' + attachments.map((a) => {
      if (a.kind === 'image') {
        return `<img class="att-thumb" src="data:${escapeHtml(a.mimeType)};base64,${a.data}" alt="image" />`;
      }
      return `<span class="att-badge">@${escapeHtml(a.path || a.name)}</span>`;
    }).join('') + '</div>';
  }

  el.innerHTML = `
    <div class="msg-user-header">
      <span class="msg-user-prompt-sign">❯</span>
      <span>USER</span>
      <span>·</span>
      <span>${now}</span>
    </div>
    <div class="msg-user-text">${escapeHtml(text)}</div>
    ${attHtml}
  `;
  $('stream-timeline').appendChild(el);
  scrollToBottom(true);
}

function appendSystemMessage(text, isError = false) {
  hideWelcome();
  const el = document.createElement('div');
  el.className = `msg-system${isError ? ' error' : ''}`;
  el.textContent = text;
  $('stream-timeline').appendChild(el);
  scrollToBottom(true);
}

function ensureActiveTurn() {
  if (state.activeTurn) return state.activeTurn;
  hideWelcome();
  const el = document.createElement('div');
  el.className = 'turn-block';
  $('stream-timeline').appendChild(el);
  state.activeTurn = {
    el,
    thoughtEl: null,
    thoughtBodyEl: null,
    thoughtText: '',
    textEl: null,
    rawText: '',
    renderTimer: null,
    tools: new Map(),
    startedAt: Date.now()
  };
  return state.activeTurn;
}

function ensureThoughtCard(turn) {
  if (turn.thoughtEl) return;
  const card = document.createElement('div');
  card.className = 'thought-card';
  const expanded = !!state.settings?.showThinkingByDefault;
  card.innerHTML = `
    <div class="thought-summary">
      <span class="thought-caret">${expanded ? '▾' : '▸'}</span>
      <span>💭 Suy luận (Thinking)</span>
    </div>
    <div class="thought-body ${expanded ? '' : 'hidden'}"></div>
  `;
  const summary = card.querySelector('.thought-summary');
  const body = card.querySelector('.thought-body');
  const caret = card.querySelector('.thought-caret');
  summary.addEventListener('click', () => {
    const isHidden = body.classList.toggle('hidden');
    caret.textContent = isHidden ? '▸' : '▾';
  });
  turn.el.appendChild(card);
  turn.thoughtEl = card;
  turn.thoughtBodyEl = body;
}

function ensureAgentTextEl(turn) {
  if (turn.textEl) return;
  const el = document.createElement('div');
  el.className = 'msg-agent streaming';
  turn.el.appendChild(el);
  turn.textEl = el;
}

function scheduleMarkdownRender(turn) {
  if (turn.renderTimer) return;
  turn.renderTimer = setTimeout(() => {
    turn.renderTimer = null;
    if (turn.textEl) {
      turn.textEl.innerHTML = renderMarkdown(turn.rawText);
      wireCodeBlockActions(turn.textEl);
    }
  }, 45);
}

function finalizeActiveTurn(usage, errorMsg) {
  const turn = state.activeTurn;
  state.activeTurn = null;
  if (!turn) return;

  if (turn.renderTimer) {
    clearTimeout(turn.renderTimer);
    turn.renderTimer = null;
  }
  if (turn.textEl) {
    turn.textEl.classList.remove('streaming');
    turn.textEl.innerHTML = renderMarkdown(turn.rawText);
    wireCodeBlockActions(turn.textEl);
  }
  if (errorMsg) {
    const errEl = document.createElement('div');
    errEl.className = 'msg-system error';
    errEl.textContent = errorMsg;
    turn.el.appendChild(errEl);
  }

  const elapsed = ((Date.now() - turn.startedAt) / 1000).toFixed(1);
  const parts = [`${elapsed}s`];
  if (usage) {
    if (usage.inputTokens) parts.push(`in ${fmtNum(usage.inputTokens)}`);
    if (usage.outputTokens) parts.push(`out ${fmtNum(usage.outputTokens)}`);
    if (usage.thoughtTokens) parts.push(`think ${fmtNum(usage.thoughtTokens)}`);
    if (usage.cachedReadTokens) parts.push(`cache ${fmtNum(usage.cachedReadTokens)}`);
  }
  const footer = document.createElement('div');
  footer.className = 'turn-footer';
  footer.textContent = parts.join('  ·  ');
  turn.el.appendChild(footer);
  scrollToBottomIfPinned();
}

function upsertToolCard(turn, upd) {
  const id = upd.toolCallId || `tc_${turn.tools.size + 1}`;
  let entry = turn.tools.get(id);
  if (!entry) {
    const card = document.createElement('div');
    card.className = 'tool-card';
    card.innerHTML = `
      <div class="tool-header">
        <span class="tool-status-ic in_progress">●</span>
        <span class="tool-kind"></span>
        <span class="tool-title"></span>
        <span class="tool-caret muted-label">▸</span>
      </div>
      <div class="tool-body hidden"></div>
    `;
    const header = card.querySelector('.tool-header');
    const body = card.querySelector('.tool-body');
    const caret = card.querySelector('.tool-caret');
    header.addEventListener('click', () => {
      const hidden = body.classList.toggle('hidden');
      caret.textContent = hidden ? '▸' : '▾';
    });
    turn.el.appendChild(card);
    entry = { card, body, caret, data: {} };
    turn.tools.set(id, entry);
  }

  const d = Object.assign(entry.data, upd);
  const status = d.status || 'in_progress';
  const ic = entry.card.querySelector('.tool-status-ic');
  ic.className = `tool-status-ic ${status}`;
  ic.textContent = status === 'completed' ? '✓' : status === 'failed' ? '✕' : status === 'pending' ? '?' : '●';
  entry.card.className = `tool-card ${status}`;

  entry.card.querySelector('.tool-kind').textContent = d.kind || d.name || 'tool';
  entry.card.querySelector('.tool-title').textContent = d.title || d.name || id;

  // Build details + check for diffs
  let bodyHtml = '';
  const inputObj = d.rawInput || d.args;
  if (inputObj && Object.keys(inputObj).length > 0) {
    const preview = typeof inputObj === 'string' ? inputObj : JSON.stringify(inputObj, null, 2);
    bodyHtml += `<div><div class="muted-label" style="margin-bottom:3px">Input</div><pre class="tool-pre">${escapeHtml(preview)}</pre></div>`;
  }

  // Handle ACP content array (diffs & text outputs)
  let foundDiff = false;
  if (Array.isArray(d.content)) {
    for (const item of d.content) {
      if (item?.type === 'diff') {
        foundDiff = true;
        const path = item.path || d.title || 'modified-file';
        const oldText = item.oldText ?? '';
        const newText = item.newText ?? '';
        recordSessionDiff(path, oldText, newText, oldText === '' ? 'added' : 'modified');
        const diff = computeLineDiff(oldText, newText);
        bodyHtml += `
          <div class="inline-diff-box">
            <div class="inline-diff-bar">
              <span>${escapeHtml(path)} <span class="diff-stats"><span class="add">+${diff.added}</span><span class="del">-${diff.removed}</span></span></span>
              <button class="chip-btn open-diff-btn" data-diff-path="${escapeHtml(path)}">Mở Diff Inspector</button>
            </div>
            <div>${renderUnifiedDiffHtml(diff, 24)}</div>
          </div>`;
      } else if (item?.type === 'content' && item.content?.text) {
        bodyHtml += `<div><div class="muted-label" style="margin-bottom:3px">Output</div><pre class="tool-pre">${escapeHtml(item.content.text)}</pre></div>`;
      } else if (item?.type === 'text' && item.text) {
        bodyHtml += `<div><div class="muted-label" style="margin-bottom:3px">Output</div><pre class="tool-pre">${escapeHtml(item.text)}</pre></div>`;
      }
    }
  } else if (d.rawOutput) {
    const outText = typeof d.rawOutput === 'string'
      ? d.rawOutput
      : (d.rawOutput.output ?? JSON.stringify(d.rawOutput, null, 2));
    bodyHtml += `<div><div class="muted-label" style="margin-bottom:3px">Output</div><pre class="tool-pre">${escapeHtml(outText)}</pre></div>`;
  }

  entry.body.innerHTML = bodyHtml;
  entry.body.querySelectorAll('.open-diff-btn').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const diffEntry = state.sessionDiffs.get(btn.dataset.diffPath);
      if (diffEntry) openDiffInInspector(diffEntry.path, diffEntry.oldText, diffEntry.newText);
    });
  });

  // Auto-expand diffs so user sees edits immediately
  if (foundDiff) {
    entry.body.classList.remove('hidden');
    entry.caret.textContent = '▾';
  }
}

function wireCodeBlockActions(container) {
  container.querySelectorAll('.md-copy-btn').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const code = decodeURIComponent(btn.dataset.code || '');
      try {
        await navigator.clipboard.writeText(code);
        btn.textContent = 'Copied!';
        setTimeout(() => (btn.textContent = 'Copy'), 1200);
      } catch {}
    });
  });
  container.querySelectorAll('.md-run-btn').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const code = decodeURIComponent(btn.dataset.code || '');
      await ensureTerminalOpen();
      appendTerminalOutput(`\nPS> ${code}\n`, false, true);
      await invoke('terminal:input', { command: code });
    });
  });
  container.querySelectorAll('.ext-link').forEach((a) => {
    a.addEventListener('click', (e) => {
      e.preventDefault();
      const url = a.dataset.url || a.href;
      if (state.serverMode) window.open(url, '_blank', 'noopener');
      else invoke('app:openExternal', { url });
    });
  });
}

function renderPlanBar(entries) {
  const bar = $('plan-bar');
  if (!entries || entries.length === 0) {
    bar.classList.add('hidden');
    return;
  }
  bar.classList.remove('hidden');
  $('plan-items').innerHTML = entries.map((e) => {
    const st = e.status || 'pending';
    const ic = st === 'completed' ? '✓' : st === 'in_progress' ? '▶' : '○';
    return `<div class="plan-item ${escapeHtml(st)}"><span>${ic}</span><span>${escapeHtml(e.content || '')}</span></div>`;
  }).join('');
}

// ============================================================================
// Sending Prompts & Slash Commands
// ============================================================================
async function submitComposer() {
  const input = $('prompt-input');
  const raw = input.value.trim();
  if (!raw && state.attachments.length === 0) return;

  closeAutocomplete();

  // Save prompt history
  if (raw && state.history[0] !== raw) {
    state.history.unshift(raw);
    if (state.history.length > 200) state.history.pop();
  }
  state.historyIdx = -1;

  // Handle local CLI slash commands
  if (raw.startsWith('/') && state.attachments.length === 0) {
    const handled = await tryHandleLocalSlash(raw);
    if (handled) {
      input.value = '';
      autoResizePrompt();
      return;
    }
  }

  if (state.busy) {
    toast('Agent đang xử lý — nhấn Esc để hủy turn hiện tại trước.', 'err');
    return;
  }

  const attachments = [...state.attachments];
  state.attachments = [];
  renderAttachments();
  input.value = '';
  autoResizePrompt();

  appendUserBubble(raw || '(Đính kèm tệp/hình ảnh)', attachments);
  setBusy(true);
  ensureActiveTurn();

  // Build ACP prompt blocks
  const blocks = [];
  if (raw) blocks.push({ type: 'text', text: raw });

  let plainCombined = raw;
  for (const att of attachments) {
    if (att.kind === 'file') {
      const uri = 'file:///' + (att.fullPath || att.path).replace(/\\/g, '/').replace(/^\/+/, '');
      blocks.push({
        type: 'resource',
        resource: {
          uri,
          mimeType: 'text/plain',
          text: att.content || ''
        }
      });
      plainCombined += `\n\n--- File: ${att.path} ---\n${att.content || ''}`;
    } else if (att.kind === 'image') {
      blocks.push({
        type: 'image',
        data: att.data,
        mimeType: att.mimeType
      });
    }
  }

  try {
    const res = await invoke('acp:prompt', { blocks, plainText: plainCombined, displayText: raw || '(Đính kèm tệp/hình ảnh)' });
    if (state.settings?.transportMode !== 'stream-json') {
      if (res?.ok) {
        const usage = res.result?.usage || null;
        finalizeActiveTurn(usage, null);
      } else {
        finalizeActiveTurn(null, res?.error || 'Không nhận được phản hồi từ Antigravity.');
      }
      setBusy(false);
      await refreshGitStatus();
      await refreshSessions();
    }
  } catch (err) {
    finalizeActiveTurn(null, `Lỗi gửi lệnh: ${err.message}`);
    setBusy(false);
  }
}

async function tryHandleLocalSlash(raw) {
  const spaceIdx = raw.indexOf(' ');
  const cmd = (spaceIdx > 0 ? raw.slice(1, spaceIdx) : raw.slice(1)).toLowerCase();
  const arg = spaceIdx > 0 ? raw.slice(spaceIdx + 1).trim() : '';

  switch (cmd) {
    case 'help':
      appendSystemMessage(
        `AntiCLI Studio — Các lệnh CLI tích hợp:\n` +
        `  /new, /clear        Tạo phiên mới / Xóa timeline\n` +
        `  /model <id>         Đổi model Antigravity ngay lập tức\n` +
        `  /effort <level>     Đổi thinking level (low|medium|high|xhigh)\n` +
        `  /mode <mode>        Đổi chế độ (default|accept-edits|plan)\n` +
        `  /usage, /credits    Kiểm tra quota & credits qua agy CLI\n` +
        `  /diff               Mở Diff Inspector\n` +
        `  /terminal           Bật/tắt PowerShell Terminal (Ctrl+J)\n` +
        `  /settings           Mở Settings & Auth (Ctrl+,)\n` +
        `  /reconnect          Kết nối lại Antigravity ACP Server\n` +
        `  /login              Mở cửa sổ đăng nhập Google cho agy\n\n` +
        `Ngoài ra mọi slash command của Antigravity (/plan, /compact, /goal, /grill-me, /boost, /swarm…) được chuyển thẳng tới Agent!`
      );
      return true;

    case 'clear':
      clearTimeline();
      return true;

    case 'new':
      await startNewSession();
      return true;

    case 'settings':
      openSettingsModal();
      return true;

    case 'terminal':
      await toggleTerminal();
      return true;

    case 'diff':
      openInspectorTab('diff');
      return true;

    case 'reconnect':
      await connectAntigravity();
      return true;

    case 'login':
      await invoke('cli:login');
      toast('Đã mở cửa sổ PowerShell để đăng nhập agy.');
      return true;

    case 'usage':
    case 'credits':
    case 'changelog': {
      appendUserBubble(raw);
      appendSystemMessage(`Đang chạy agy -p /${cmd}…`);
      const rep = await invoke('cli:slashReport', { command: `/${cmd}` });
      appendSystemMessage(rep?.output || rep?.error || 'Không có kết quả.', !rep?.ok);
      return true;
    }

    case 'model':
      if (!arg) { $('select-model').focus(); return true; }
      await changeConfigOption('model', arg);
      return true;

    case 'effort':
      if (!arg) { $('select-thought').focus(); return true; }
      await changeConfigOption('thought_level', arg);
      return true;

    case 'mode':
      if (!arg) { cycleMode(); return true; }
      await changeConfigOption('mode', arg);
      return true;

    default:
      return false; // Forward to Antigravity ACP agent!
  }
}

async function changeConfigOption(configId, value) {
  if (state.settings?.transportMode === 'stream-json') {
    toast('Đang chạy Stream-JSON: áp dụng ở phiên mới.', 'ok');
    return;
  }
  const res = await invoke('acp:setConfig', { configId, value });
  if (res?.ok) {
    if (Array.isArray(res.data?.configOptions)) applyConfigOptions(res.data.configOptions);
    toast(`Đã chuyển ${configId} → ${value}`, 'ok');
  } else {
    toast(`Không thể đổi ${configId}: ${res?.error || ''}`, 'err');
  }
}

function cycleMode() {
  const sel = $('select-mode');
  if (!sel.options.length) return;
  const next = (sel.selectedIndex + 1) % sel.options.length;
  sel.selectedIndex = next;
  styleModeSelect(sel.value);
  changeConfigOption('mode', sel.value);
}

async function startNewSession() {
  clearTimeline();
  state.sessionTitle = 'Phiên mới';
  $('session-title').textContent = state.sessionTitle;
  $('plan-bar').classList.add('hidden');
  updateContextMeter(0, 1048576);
  const res = await invoke('acp:newSession');
  if (res?.ok && res.session) {
    applySessionPayload(res.session, res.tier);
    toast('Đã tạo phiên mới', 'ok');
    await refreshSessions();
  }
}

function clearTimeline() {
  $('stream-timeline').innerHTML = '';
  $('welcome-view').classList.remove('hidden');
  state.activeTurn = null;
}

// ============================================================================
// Workspace, File Tree, Sessions, Changes & Diff Inspector
// ============================================================================
function updateWorkspaceUi(workspace, workspaceName, git, tree) {
  state.workspace = workspace;
  state.workspaceName = workspaceName;
  state.expandedDirs.clear();
  $('workspace-name').textContent = workspaceName || workspace;
  $('workspace-name').title = workspace;
  $('terminal-cwd').textContent = workspace;
  renderRecentWorkspaces();
  updateGitUi(git);
  renderRootTree(tree);
}

function renderRecentWorkspaces() {
  const list = $('recent-workspaces-list');
  const recents = state.settings?.recentWorkspaces || [];
  if (!recents.length) {
    list.innerHTML = '<div class="muted-label" style="padding:6px 9px">Chưa có dự án nào khác</div>';
    return;
  }
  list.innerHTML = recents.map((p) => {
    const name = p.split(/[\\/]/).filter(Boolean).pop() || p;
    const isCur = p.toLowerCase() === (state.workspace || '').toLowerCase();
    return `<button class="dropdown-item" data-ws="${escapeHtml(p)}" title="${escapeHtml(p)}">
      <span class="truncate">${isCur ? '● ' : ''}${escapeHtml(name)}</span>
      <span class="muted-label mono truncate" style="max-width:130px;font-size:10px">${escapeHtml(p)}</span>
    </button>`;
  }).join('');

  list.querySelectorAll('[data-ws]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      $('workspace-dropdown').classList.add('hidden');
      await openWorkspace(btn.dataset.ws);
    });
  });
}

async function openWorkspace(path) {
  toast('Đang chuyển workspace…');
  const res = await invoke('workspace:open', { path });
  if (res?.ok) {
    applySettingsToUi(res.settings);
    state.sessionDiffs.clear();
    clearTimeline();
    updateWorkspaceUi(res.workspace, res.workspaceName, res.git, res.tree);
    if (res.connection?.ok && res.connection.session) {
      applySessionPayload(res.connection.session, res.connection.tier);
    }
    await refreshSessions();
    toast(`Đã mở ${res.workspaceName}`, 'ok');
  } else {
    toast(res?.error || 'Không thể mở thư mục', 'err');
  }
}

async function refreshFileTree() {
  const filter = $('file-filter-input').value.trim();
  if (filter) {
    const res = await invoke('workspace:searchFiles', { query: filter, maxResults: 60 });
    renderFlatSearchTree(res?.files || []);
    return;
  }
  const tree = await invoke('workspace:listDir', { relativePath: '' });
  renderRootTree(tree);
}

function renderRootTree(treeRes) {
  const container = $('file-tree');
  container.innerHTML = '';
  const entries = treeRes?.entries || [];
  if (!entries.length) {
    container.innerHTML = '<div class="empty-state">Thư mục trống</div>';
    return;
  }
  appendTreeEntries(container, entries, 0);
}

function renderFlatSearchTree(files) {
  const container = $('file-tree');
  container.innerHTML = '';
  if (!files.length) {
    container.innerHTML = '<div class="empty-state">Không tìm thấy tệp khớp</div>';
    return;
  }
  for (const f of files) {
    const row = createTreeRow({ ...f, isDir: false }, 0, true);
    container.appendChild(row);
  }
}

function appendTreeEntries(parentEl, entries, depth) {
  for (const entry of entries) {
    const wrap = document.createElement('div');
    const row = createTreeRow(entry, depth, false);
    wrap.appendChild(row);

    if (entry.isDir) {
      const childBox = document.createElement('div');
      childBox.className = 'tree-children';
      wrap.appendChild(childBox);

      const isOpen = state.expandedDirs.has(entry.relativePath);
      if (isOpen) {
        row.querySelector('.tree-caret').textContent = '▾';
        loadDirChildren(entry.relativePath, childBox, depth + 1);
      }

      row.addEventListener('click', async () => {
        if (state.expandedDirs.has(entry.relativePath)) {
          state.expandedDirs.delete(entry.relativePath);
          row.querySelector('.tree-caret').textContent = '▸';
          childBox.innerHTML = '';
        } else {
          state.expandedDirs.add(entry.relativePath);
          row.querySelector('.tree-caret').textContent = '▾';
          await loadDirChildren(entry.relativePath, childBox, depth + 1);
        }
      });
    }

    parentEl.appendChild(wrap);
  }
}

async function loadDirChildren(relPath, container, depth) {
  const res = await invoke('workspace:listDir', { relativePath: relPath });
  container.innerHTML = '';
  appendTreeEntries(container, res?.entries || [], depth);
}

function createTreeRow(entry, depth, showFullRel) {
  const row = document.createElement('div');
  row.className = 'tree-row';
  row.style.paddingLeft = `${6 + depth * 14}px`;
  row.title = entry.relativePath;

  const badge = entry.isDir ? 'DIR' : (entry.ext ? entry.ext.replace('.', '').toUpperCase().slice(0, 4) : 'FILE');
  const displayName = showFullRel ? entry.relativePath : entry.name;

  row.innerHTML = `
    <span class="tree-caret">${entry.isDir ? '▸' : ''}</span>
    <span class="tree-badge">${escapeHtml(badge)}</span>
    <span class="tree-name">${escapeHtml(displayName)}</span>
    ${entry.isDir ? '' : '<button class="tree-mention" title="Gắn tệp này vào prompt">@</button>'}
  `;

  if (!entry.isDir) {
    row.addEventListener('click', () => openFileInInspector(entry.relativePath, true));
    row.querySelector('.tree-mention').addEventListener('click', async (e) => {
      e.stopPropagation();
      await attachFileByPath(entry.relativePath);
    });
  }
  return row;
}

async function openFileInInspector(relPath, revealInspector = true) {
  const res = await invoke('workspace:readFile', { path: relPath });
  if (!res?.ok) {
    toast(res?.error || 'Không đọc được tệp', 'err');
    return;
  }
  state.selectedFile = res;
  state.fileEditMode = false;
  state.fileOriginalContent = res.content || '';

  $('code-filepath').textContent = res.relativePath;
  $('code-filepath').title = res.fullPath;
  $('btn-code-mention').classList.remove('hidden');
  $('btn-code-edit').classList.toggle('hidden', !!res.binary);
  $('btn-code-save').classList.add('hidden');
  $('btn-code-edit').textContent = 'Sửa';
  $('code-editor-textarea').classList.add('hidden');
  $('code-view-container').classList.remove('hidden');

  if (res.binary) {
    $('code-view-container').innerHTML = `<div class="empty-state">Tệp nhị phân (${fmtBytes(res.size)}) — không hiển thị dạng văn bản.</div>`;
  } else {
    $('code-view-container').innerHTML = renderLineNumberedCode(res.content || '', res.ext || '');
  }

  if (revealInspector) openInspectorTab('code');
}

async function saveEditedFile() {
  if (!state.selectedFile || !state.fileEditMode) return;
  const newContent = $('code-editor-textarea').value;
  const res = await invoke('workspace:writeFile', { path: state.selectedFile.relativePath, content: newContent });
  if (res?.ok) {
    recordSessionDiff(state.selectedFile.relativePath, state.fileOriginalContent, newContent, 'modified');
    state.fileOriginalContent = newContent;
    state.selectedFile.content = newContent;
    state.fileEditMode = false;
    $('code-editor-textarea').classList.add('hidden');
    $('code-view-container').classList.remove('hidden');
    $('code-view-container').innerHTML = renderLineNumberedCode(newContent, state.selectedFile.ext || '');
    $('btn-code-save').classList.add('hidden');
    $('btn-code-edit').textContent = 'Sửa';
    toast(`Đã lưu ${state.selectedFile.relativePath}`, 'ok');
    refreshGitStatus();
  } else {
    toast(`Lỗi lưu tệp: ${res?.error}`, 'err');
  }
}

function recordSessionDiff(path, oldText, newText, status = 'modified') {
  state.sessionDiffs.set(path, { path, oldText, newText, status });
  renderChangesList();
}

function openDiffInInspector(path, oldText, newText) {
  state.currentDiff = { path, oldText, newText };
  renderCurrentDiff();
  openInspectorTab('diff');
}

function renderCurrentDiff() {
  if (!state.currentDiff) return;
  const { path, oldText, newText } = state.currentDiff;
  const diff = computeLineDiff(oldText, newText);
  $('diff-filepath').textContent = path;
  $('diff-stats').innerHTML = `<span class="add">+${diff.added}</span><span class="del">-${diff.removed}</span>`;
  $('btn-diff-mode').textContent = state.diffSplitMode ? 'Split' : 'Unified';
  $('diff-view-container').innerHTML = state.diffSplitMode
    ? renderSplitDiffHtml(diff)
    : renderUnifiedDiffHtml(diff);
}

async function refreshGitStatus() {
  try {
    const git = await invoke('git:status');
    updateGitUi(git);
  } catch {}
}

function updateGitUi(git) {
  state.git = git || { available: false, isRepo: false, branch: '', files: [] };
  const badge = $('git-branch-badge');
  if (state.git.isRepo && state.git.branch) {
    $('git-branch-name').textContent = state.git.branch;
    badge.classList.remove('hidden');
  } else {
    badge.classList.add('hidden');
  }
  renderChangesList();
}

function renderChangesList() {
  const merged = new Map();
  for (const f of (state.git.files || [])) {
    merged.set(f.path, { path: f.path, status: f.status, source: 'git' });
  }
  for (const [path, d] of state.sessionDiffs.entries()) {
    merged.set(path, { path, status: d.status || 'modified', source: 'acp', diff: d });
  }

  const countEl = $('changes-count');
  if (merged.size > 0) {
    countEl.textContent = String(merged.size);
    countEl.classList.remove('hidden');
  } else {
    countEl.classList.add('hidden');
  }

  const list = $('changes-list');
  if (merged.size === 0) {
    list.innerHTML = '<div class="empty-state">Chưa có thay đổi tệp nào.</div>';
    return;
  }

  list.innerHTML = '';
  for (const item of merged.values()) {
    const row = document.createElement('div');
    row.className = 'change-item';
    const letter = item.status === 'added' || item.status === 'untracked' ? 'A' : item.status === 'deleted' ? 'D' : 'M';
    row.innerHTML = `
      <span class="change-tag ${escapeHtml(item.status)}">${letter}</span>
      <span class="truncate mono" style="flex:1">${escapeHtml(item.path)}</span>
      <span class="muted-label" style="font-size:10px">${item.source.toUpperCase()}</span>
    `;
    row.addEventListener('click', async () => {
      if (item.diff) {
        openDiffInInspector(item.diff.path, item.diff.oldText, item.diff.newText);
      } else {
        const res = await invoke('git:diff', { path: item.path });
        if (res?.ok) openDiffInInspector(res.path, res.oldText, res.newText);
      }
    });
    list.appendChild(row);
  }
}

async function refreshSessions() {
  if (state.settings?.transportMode === 'stream-json') return;
  try {
    const res = await invoke('acp:listSessions', { all: true });
    state.sessions = res?.data?.sessions || [];
    renderSessionsList();
  } catch {}
}

function renderSessionsList() {
  const list = $('sessions-list');
  if (!state.sessions.length) {
    list.innerHTML = '<div class="empty-state">Chưa có lịch sử phiên nào.</div>';
    return;
  }
  list.innerHTML = '';
  for (const s of state.sessions) {
    const item = document.createElement('div');
    item.className = `session-item${s.sessionId === state.sessionId ? ' active' : ''}`;
    const when = s.updatedAt ? new Date(s.updatedAt).toLocaleString('vi-VN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) : '';
    const folder = (s.cwd || '').split(/[\\/]/).filter(Boolean).pop() || '';
    item.innerHTML = `
      <div class="session-item-title truncate">${escapeHtml(s.title || 'Untitled session')}</div>
      <div class="session-item-meta">
        <span class="truncate mono">${escapeHtml(decodeURIComponent(folder))}</span>
        <span>${escapeHtml(when)}</span>
      </div>
    `;
    item.addEventListener('click', async () => {
      if (state.busy) { toast('Hãy dừng turn hiện tại trước khi chuyển phiên.', 'err'); return; }
      clearTimeline();
      toast('Đang tải lại phiên…');
      const res = await invoke('acp:loadSession', { sessionId: s.sessionId, cwd: s.cwd });
      if (res?.ok) {
        state.sessionId = s.sessionId;
        state.sessionTitle = s.title || 'Phiên đã khôi phục';
        $('session-title').textContent = state.sessionTitle;
        $('session-id-pill').textContent = s.sessionId.slice(0, 8);
        $('session-id-pill').classList.remove('hidden');
        if (res.session?.configOptions) applyConfigOptions(res.session.configOptions);
        if (res.resumedWithoutReplay) {
          appendSystemMessage(`Đã khôi phục phiên "${state.sessionTitle}" (ID: ${s.sessionId}). Bạn có thể tiếp tục hội thoại ngay.`);
        }
        renderSessionsList();
      } else {
        toast(`Không thể mở phiên: ${res?.error || ''}`, 'err');
      }
    });
    list.appendChild(item);
  }
}

// ============================================================================
// Autocomplete (/ slash commands & @ files) + Attachments
// ============================================================================
async function updateAutocomplete() {
  const input = $('prompt-input');
  const val = input.value;
  const pos = input.selectionStart || 0;
  const before = val.slice(0, pos);

  // Slash command at start of input
  const slashMatch = before.match(/^\/([\w-]*)$/);
  if (slashMatch) {
    const q = slashMatch[1].toLowerCase();
    const merged = [
      ...LOCAL_SLASH_COMMANDS,
      ...state.availableCommands.filter((c) => !LOCAL_SLASH_COMMANDS.some((l) => l.name === c.name))
    ];
    const hits = merged.filter((c) => c.name.toLowerCase().includes(q)).slice(0, 14);
    if (hits.length) {
      state.acMode = '/';
      state.acStartPos = 0;
      state.acItems = hits.map((c) => ({ label: `/${c.name}`, desc: c.description || '', value: `/${c.name} ` }));
      state.acSelected = 0;
      renderAutocomplete();
      return;
    }
  }

  // @ file mention anywhere
  const atMatch = before.match(/(?:^|\s)@([^\s@]*)$/);
  if (atMatch) {
    const q = atMatch[1];
    const startPos = pos - q.length - 1;
    const res = await invoke('workspace:searchFiles', { query: q, maxResults: 12 });
    const files = res?.files || [];
    if (files.length) {
      state.acMode = '@';
      state.acStartPos = startPos;
      state.acItems = files.map((f) => ({ label: `@${f.relativePath}`, desc: fmtBytes(f.size), filePath: f.relativePath }));
      state.acSelected = 0;
      renderAutocomplete();
      return;
    }
  }

  closeAutocomplete();
}

function renderAutocomplete() {
  const pop = $('autocomplete-popup');
  pop.classList.remove('hidden');
  pop.innerHTML = state.acItems.map((it, idx) => `
    <div class="ac-item${idx === state.acSelected ? ' selected' : ''}" data-ac-idx="${idx}">
      <span class="ac-name">${escapeHtml(it.label)}</span>
      <span class="ac-desc">${escapeHtml(it.desc)}</span>
    </div>
  `).join('');

  pop.querySelectorAll('.ac-item').forEach((el) => {
    el.addEventListener('mousedown', (e) => {
      e.preventDefault();
      applyAutocomplete(Number(el.dataset.acIdx));
    });
  });
}

async function applyAutocomplete(idx) {
  const item = state.acItems[idx];
  if (!item) return;
  const input = $('prompt-input');
  const pos = input.selectionStart || 0;

  if (state.acMode === '/') {
    input.value = item.value;
    input.focus();
  } else if (state.acMode === '@') {
    const before = input.value.slice(0, state.acStartPos);
    const after = input.value.slice(pos);
    input.value = `${before}@${item.filePath} ${after}`.trimStart();
    await attachFileByPath(item.filePath);
    input.focus();
  }
  closeAutocomplete();
  autoResizePrompt();
}

function closeAutocomplete() {
  state.acMode = null;
  state.acItems = [];
  $('autocomplete-popup').classList.add('hidden');
}

async function attachFileByPath(relPath) {
  if (state.attachments.some((a) => a.kind === 'file' && a.path === relPath)) return;
  const res = await invoke('workspace:readFile', { path: relPath });
  if (!res?.ok || res.binary) {
    toast(res?.binary ? 'Không thể gắn tệp nhị phân làm ngữ cảnh văn bản.' : (res?.error || 'Lỗi đọc tệp'), 'err');
    return;
  }
  state.attachments.push({
    kind: 'file',
    name: res.name,
    path: res.relativePath,
    fullPath: res.fullPath,
    content: res.content
  });
  renderAttachments();
  toast(`Đã gắn @${res.relativePath}`, 'ok');
}

function renderAttachments() {
  const strip = $('attachments-strip');
  if (!state.attachments.length) {
    strip.classList.add('hidden');
    strip.innerHTML = '';
    return;
  }
  strip.classList.remove('hidden');
  strip.innerHTML = state.attachments.map((a, idx) => `
    <span class="att-chip">
      <span>${a.kind === 'image' ? '🖼 ' + escapeHtml(a.name) : '@' + escapeHtml(a.path)}</span>
      <button data-rm-att="${idx}" title="Bỏ đính kèm">✕</button>
    </span>
  `).join('');
  strip.querySelectorAll('[data-rm-att]').forEach((btn) => {
    btn.addEventListener('click', () => {
      state.attachments.splice(Number(btn.dataset.rmAtt), 1);
      renderAttachments();
    });
  });
}

// ============================================================================
// Integrated Terminal
// ============================================================================
async function ensureTerminalOpen() {
  const pane = $('terminal-pane');
  const resizer = $('resizer-terminal');
  if (pane.classList.contains('hidden')) {
    pane.classList.remove('hidden');
    resizer.classList.remove('hidden');
    $('btn-toggle-terminal').classList.add('active');
  }
  if (!state.termStarted) {
    state.termStarted = true;
    const r = await invoke('terminal:start');
    if (r?.cwd) $('terminal-cwd').textContent = r.cwd;
  }
}

async function toggleTerminal() {
  const pane = $('terminal-pane');
  if (pane.classList.contains('hidden')) {
    await ensureTerminalOpen();
    $('terminal-input').focus();
  } else {
    pane.classList.add('hidden');
    $('resizer-terminal').classList.add('hidden');
    $('btn-toggle-terminal').classList.remove('active');
  }
}

function appendTerminalOutput(text, isErr = false, isCmd = false) {
  const out = $('terminal-output');
  const span = document.createElement('span');
  if (isErr) span.className = 'term-err';
  else if (isCmd) span.className = 'term-cmd';
  span.textContent = text;
  out.appendChild(span);
  out.scrollTop = out.scrollHeight;
}

// ============================================================================
// Panels, Wire Log, Settings & Command Palette
// ============================================================================
function openInspectorTab(tabName) {
  $('inspector-pane').classList.remove('hidden');
  $('resizer-right').classList.remove('hidden');
  $('btn-toggle-inspector').classList.add('active');

  document.querySelectorAll('[data-inspector-tab]').forEach((b) => {
    b.classList.toggle('active', b.dataset.inspectorTab === tabName);
  });
  ['code', 'diff', 'wire'].forEach((t) => {
    $(`inspector-${t}`).classList.toggle('active', t === tabName);
  });
}

function toggleInspector() {
  const hidden = $('inspector-pane').classList.toggle('hidden');
  $('resizer-right').classList.toggle('hidden', hidden);
  $('btn-toggle-inspector').classList.toggle('active', !hidden);
}

function toggleSidebar() {
  const hidden = $('sidebar').classList.toggle('hidden');
  $('resizer-left').classList.toggle('hidden', hidden);
  $('btn-toggle-sidebar').classList.toggle('active', !hidden);
}

function appendWireEntry(dir, text) {
  const log = $('wire-log');
  if (log.childElementCount > 250) log.removeChild(log.firstChild);
  const el = document.createElement('div');
  el.className = 'wire-entry';
  const arrow = dir === 'out' ? '▲ OUT' : dir === 'in' ? '▼ IN' : '● LOG';
  el.innerHTML = `<span class="wire-dir ${escapeHtml(dir)}">${arrow}</span><span>${escapeHtml(text)}</span>`;
  log.appendChild(el);
  log.scrollTop = log.scrollHeight;
}

function applySettingsToUi(s) {
  if (!s) return;
  state.settings = s;
  document.documentElement.setAttribute('data-theme', s.theme || 'obsidian-cyan');
  document.documentElement.style.setProperty('--code-size', `${s.fontSize || 13}px`);
  $('transport-pill').textContent = (s.transportMode || 'acp').toUpperCase();
}

function openSettingsModal() {
  const s = state.settings || {};
  const authMode = s.authMode || 'antigravity';
  document.querySelectorAll('input[name="authMode"]').forEach((r) => { r.checked = r.value === authMode; });
  updateAuthBoxes(authMode);

  $('settings-cli-status').textContent = state.cli?.installed
    ? `✓ agy ${state.cli.version} (${s.resolvedAgyPath || state.cli.path})`
    : `⚠ Chưa tìm thấy agy.exe (${s.resolvedAgyPath || ''})`;

  $('input-gemini-key').value = '';
  $('apikey-saved-hint').textContent = s.hasGeminiApiKey
    ? `Đã lưu khóa mã hóa (DPAPI): ${s.geminiApiKeyMasked}`
    : 'Chưa có khóa API nào được lưu.';
  $('btn-clear-apikey').classList.toggle('hidden', !s.hasGeminiApiKey);

  $('input-gemini-base').value = s.geminiBaseUrl || '';
  $('select-transport').value = s.transportMode || 'acp';
  $('input-agy-path').value = s.agyPath || '';
  $('chk-sandbox').checked = !!s.enableSandbox;
  $('chk-auto-approve').checked = !!s.autoApprovePermissions;
  $('select-theme').value = s.theme || 'obsidian-cyan';
  $('input-fontsize').value = s.fontSize || 13;
  $('fontsize-val').textContent = String(s.fontSize || 13);
  $('chk-show-thinking').checked = !!s.showThinkingByDefault;

  if (state.serverMode) renderServerInfoInSettings();

  $('settings-modal').classList.remove('hidden');
}

function renderServerInfoInSettings() {
  const list = $('server-urls-list');
  const urls = [];
  if (state.serverInfo?.publicUrl) urls.push({ label: 'HTTPS Public', url: state.serverInfo.publicUrl });
  for (const u of (state.serverInfo?.lanUrls || [location.origin])) {
    urls.push({ label: u.includes('localhost') ? 'Local' : 'LAN / VPN', url: u });
  }
  list.innerHTML = urls.map((item) => `
    <div class="server-url-row">
      <span class="truncate">${escapeHtml(item.label)}: ${escapeHtml(item.url)}</span>
      <button class="chip-btn copy-url-btn" data-url="${escapeHtml(item.url)}">Copy</button>
    </div>
  `).join('');
  list.querySelectorAll('.copy-url-btn').forEach((btn) => {
    btn.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(btn.dataset.url || '');
        toast('Đã copy địa chỉ truy cập!', 'ok');
      } catch {}
    });
  });
}

function updateAuthBoxes(mode) {
  $('auth-antigravity-box').classList.toggle('hidden', mode !== 'antigravity');
  $('auth-apikey-box').classList.toggle('hidden', mode !== 'gemini-api-key');
}

async function saveSettingsFromModal(clearKey = false) {
  const authMode = document.querySelector('input[name="authMode"]:checked')?.value || 'antigravity';
  const payload = {
    authMode,
    geminiApiKey: $('input-gemini-key').value.trim(),
    clearGeminiApiKey: clearKey,
    geminiBaseUrl: $('input-gemini-base').value.trim(),
    transportMode: $('select-transport').value,
    agyPath: $('input-agy-path').value.trim(),
    enableSandbox: $('chk-sandbox').checked,
    autoApprovePermissions: $('chk-auto-approve').checked,
    theme: $('select-theme').value,
    fontSize: Number($('input-fontsize').value) || 13,
    showThinkingByDefault: $('chk-show-thinking').checked
  };

  const res = await invoke('settings:save', payload);
  if (res?.ok) {
    applySettingsToUi(res.settings);
    $('settings-modal').classList.add('hidden');
    if (res.reconnected) {
      if (res.reconnectResult?.ok && res.reconnectResult.session) {
        applySessionPayload(res.reconnectResult.session, res.reconnectResult.tier);
      }
      toast('Đã lưu cài đặt và kết nối lại Antigravity.', 'ok');
    } else {
      toast('Đã lưu cài đặt.', 'ok');
    }
  }
}

// Command Palette
function openCommandPalette(initialPrefix = '') {
  $('cmd-palette-modal').classList.remove('hidden');
  const inp = $('palette-input');
  inp.value = initialPrefix;
  inp.focus();
  refreshPaletteResults(initialPrefix);
}

async function refreshPaletteResults(query) {
  const q = query.trim().toLowerCase();
  const actions = [
    { title: 'Mở thư mục dự án (Workspace)…', sub: 'Ctrl+O', run: () => pickWorkspace() },
    { title: 'Phiên làm việc mới (New Session)', sub: 'Ctrl+N', run: () => startNewSession() },
    { title: 'Bật / tắt Terminal PowerShell', sub: 'Ctrl+J', run: () => toggleTerminal() },
    { title: 'Bật / tắt Inspector (Code / Diff / Wire)', sub: 'Ctrl+\\', run: () => toggleInspector() },
    { title: 'Mở Diff Inspector', sub: 'Xem thay đổi tệp', run: () => openInspectorTab('diff') },
    { title: 'Mở Wire Protocol Inspector', sub: 'Xem luồng JSON-RPC ACP', run: () => openInspectorTab('wire') },
    { title: 'Kiểm tra Quota Antigravity (/usage)', sub: 'agy -p /usage', run: () => tryHandleLocalSlash('/usage') },
    { title: 'Đổi chế độ thực thi (Cycle Mode)', sub: 'Shift+Tab', run: () => cycleMode() },
    { title: 'Mở Cài đặt & Xác thực (Settings)', sub: 'Ctrl+,', run: () => openSettingsModal() },
    { title: 'Kết nối lại Antigravity ACP Server', sub: '/reconnect', run: () => connectAntigravity() }
  ];

  const items = actions.filter((a) => !q || a.title.toLowerCase().includes(q) || a.sub.toLowerCase().includes(q));

  // Also search workspace files
  const fileRes = await invoke('workspace:searchFiles', { query: query.trim(), maxResults: 12 });
  for (const f of (fileRes?.files || [])) {
    items.push({
      title: `📄 ${f.relativePath}`,
      sub: fmtBytes(f.size),
      run: () => openFileInInspector(f.relativePath, true)
    });
  }

  state.paletteItems = items.slice(0, 18);
  state.paletteIdx = 0;
  renderPalette();
}

function renderPalette() {
  const box = $('palette-results');
  if (!state.paletteItems.length) {
    box.innerHTML = '<div class="empty-state">Không tìm thấy kết quả</div>';
    return;
  }
  box.innerHTML = state.paletteItems.map((it, idx) => `
    <div class="palette-item${idx === state.paletteIdx ? ' selected' : ''}" data-pidx="${idx}">
      <span class="palette-item-title truncate">${escapeHtml(it.title)}</span>
      <span class="palette-item-sub mono">${escapeHtml(it.sub)}</span>
    </div>
  `).join('');
  box.querySelectorAll('[data-pidx]').forEach((el) => {
    el.addEventListener('click', () => executePalette(Number(el.dataset.pidx)));
  });
}

function executePalette(idx) {
  const item = state.paletteItems[idx];
  $('cmd-palette-modal').classList.add('hidden');
  if (item) item.run();
}

// ============================================================================
// UI Bindings & Keyboard Shortcuts
// ============================================================================
function bindUIEvents() {
  // Topbar
  $('btn-workspace-menu').addEventListener('click', (e) => {
    e.stopPropagation();
    $('room-dropdown').classList.add('hidden');
    $('workspace-dropdown').classList.toggle('hidden');
  });
  $('btn-room-menu').addEventListener('click', (e) => {
    e.stopPropagation();
    $('workspace-dropdown').classList.add('hidden');
    $('room-dropdown').classList.toggle('hidden');
  });
  document.addEventListener('click', () => {
    $('workspace-dropdown').classList.add('hidden');
    $('room-dropdown').classList.add('hidden');
  });
  $('btn-create-isolated-room').addEventListener('click', () => {
    $('room-dropdown').classList.add('hidden');
    createRoom(false);
  });
  $('btn-create-shared-room').addEventListener('click', () => {
    $('room-dropdown').classList.add('hidden');
    createRoom(true);
  });
  $('btn-open-folder').addEventListener('click', () => {
    $('workspace-dropdown').classList.add('hidden');
    pickWorkspace();
  });
  $('btn-cmd-palette').addEventListener('click', () => openCommandPalette(''));
  $('btn-conn-status').addEventListener('click', () => connectAntigravity());
  $('btn-toggle-sidebar').addEventListener('click', toggleSidebar);
  $('btn-toggle-terminal').addEventListener('click', toggleTerminal);
  $('btn-toggle-inspector').addEventListener('click', toggleInspector);
  $('btn-settings').addEventListener('click', openSettingsModal);

  // Sidebar tabs
  document.querySelectorAll('[data-sidebar-tab]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const t = btn.dataset.sidebarTab;
      document.querySelectorAll('[data-sidebar-tab]').forEach((b) => b.classList.toggle('active', b === btn));
      ['files', 'sessions', 'changes'].forEach((p) => $(`sidebar-${p}`).classList.toggle('active', p === t));
      if (t === 'sessions') refreshSessions();
      if (t === 'changes') refreshGitStatus();
    });
  });

  // Sidebar controls
  let filterTimer = null;
  $('file-filter-input').addEventListener('input', () => {
    clearTimeout(filterTimer);
    filterTimer = setTimeout(refreshFileTree, 150);
  });
  $('btn-refresh-files').addEventListener('click', refreshFileTree);
  $('btn-new-session-side').addEventListener('click', startNewSession);
  $('btn-refresh-sessions').addEventListener('click', refreshSessions);
  $('btn-refresh-changes').addEventListener('click', refreshGitStatus);

  // Session bar & Plan & Auth banner
  $('btn-quick-usage').addEventListener('click', () => tryHandleLocalSlash('/usage'));
  $('btn-quick-clear').addEventListener('click', startNewSession);
  $('btn-close-plan').addEventListener('click', () => $('plan-bar').classList.add('hidden'));
  $('btn-auth-google').addEventListener('click', async () => {
    toast('Đang mở đăng nhập Google…');
    const r = await invoke('acp:authenticate', { methodId: 'oauth-personal' });
    if (r?.ok) await connectAntigravity();
    else toast(r?.error || 'Đăng nhập chưa hoàn tất', 'err');
  });
  $('btn-auth-terminal').addEventListener('click', () => invoke('cli:login'));
  $('btn-auth-settings').addEventListener('click', () => {
    openSettingsModal();
    document.querySelector('input[name="authMode"][value="gemini-api-key"]').checked = true;
    updateAuthBoxes('gemini-api-key');
  });

  // Starter cards
  document.querySelectorAll('.starter-card').forEach((card) => {
    card.addEventListener('click', () => {
      if (card.dataset.slash) {
        tryHandleLocalSlash(card.dataset.slash);
      } else if (card.dataset.prompt) {
        $('prompt-input').value = card.dataset.prompt;
        autoResizePrompt();
        $('prompt-input').focus();
        if (!card.dataset.prompt.endsWith(' ')) submitComposer();
      }
    });
  });

  // Scroll tracking
  const scrollBox = $('stream-scroll');
  scrollBox.addEventListener('scroll', () => {
    const dist = scrollBox.scrollHeight - scrollBox.scrollTop - scrollBox.clientHeight;
    state.autoScroll = dist < 48;
    $('btn-scroll-bottom').classList.toggle('hidden', state.autoScroll);
  });
  $('btn-scroll-bottom').addEventListener('click', () => scrollToBottom(true));

  // Composer input
  const promptInput = $('prompt-input');
  promptInput.addEventListener('input', () => {
    autoResizePrompt();
    updateAutocomplete();
  });

  promptInput.addEventListener('keydown', (e) => {
    if (state.acMode && state.acItems.length) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        state.acSelected = (state.acSelected + 1) % state.acItems.length;
        renderAutocomplete();
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        state.acSelected = (state.acSelected - 1 + state.acItems.length) % state.acItems.length;
        renderAutocomplete();
        return;
      }
      if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) {
        e.preventDefault();
        applyAutocomplete(state.acSelected);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        closeAutocomplete();
        return;
      }
    }

    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      submitComposer();
      return;
    }

    // CLI history with Up/Down when cursor at first/last line
    if (e.key === 'ArrowUp' && (promptInput.selectionStart === 0 || !promptInput.value)) {
      if (state.history.length && state.historyIdx < state.history.length - 1) {
        e.preventDefault();
        state.historyIdx++;
        promptInput.value = state.history[state.historyIdx];
        autoResizePrompt();
      }
    } else if (e.key === 'ArrowDown' && state.historyIdx >= 0) {
      e.preventDefault();
      state.historyIdx--;
      promptInput.value = state.historyIdx >= 0 ? state.history[state.historyIdx] : '';
      autoResizePrompt();
    }
  });

  // Paste image screenshot directly into composer
  promptInput.addEventListener('paste', (e) => {
    const items = e.clipboardData?.items || [];
    for (const it of items) {
      if (it.type && it.type.startsWith('image/')) {
        e.preventDefault();
        const file = it.getAsFile();
        if (!file) continue;
        const reader = new FileReader();
        reader.onload = () => {
          const dataUrl = String(reader.result || '');
          const base64 = dataUrl.split(',')[1] || '';
          state.attachments.push({
            kind: 'image',
            name: `screenshot-${state.attachments.length + 1}.png`,
            mimeType: it.type,
            data: base64
          });
          renderAttachments();
          toast('Đã dán ảnh vào prompt.', 'ok');
        };
        reader.readAsDataURL(file);
      }
    }
  });

  $('btn-attach-file').addEventListener('click', () => {
    promptInput.value += (promptInput.value && !promptInput.value.endsWith(' ') ? ' ' : '') + '@';
    promptInput.focus();
    updateAutocomplete();
  });
  $('btn-send').addEventListener('click', submitComposer);
  $('btn-stop').addEventListener('click', async () => {
    await invoke('acp:cancel');
    toast('Đã gửi lệnh dừng turn.', 'ok');
  });

  // Config pickers
  $('select-model').addEventListener('change', (e) => changeConfigOption('model', e.target.value));
  $('select-thought').addEventListener('change', (e) => changeConfigOption('thought_level', e.target.value));
  $('select-mode').addEventListener('change', (e) => {
    styleModeSelect(e.target.value);
    changeConfigOption('mode', e.target.value);
  });

  // Terminal controls
  $('btn-term-clear').addEventListener('click', () => ($('terminal-output').innerHTML = ''));
  $('btn-term-restart').addEventListener('click', async () => {
    $('terminal-output').innerHTML = '';
    await invoke('terminal:restart');
    toast('Đã khởi động lại PowerShell.', 'ok');
  });
  $('btn-term-close').addEventListener('click', toggleTerminal);

  const termInput = $('terminal-input');
  termInput.addEventListener('keydown', async (e) => {
    if (e.key === 'Enter') {
      const cmd = termInput.value.trim();
      if (!cmd) return;
      termInput.value = '';
      state.termHistory.unshift(cmd);
      state.termHistIdx = -1;
      appendTerminalOutput(`PS> ${cmd}\n`, false, true);
      await invoke('terminal:input', { command: cmd });
    } else if (e.key === 'ArrowUp' && state.termHistory.length) {
      e.preventDefault();
      state.termHistIdx = Math.min(state.termHistIdx + 1, state.termHistory.length - 1);
      termInput.value = state.termHistory[state.termHistIdx];
    } else if (e.key === 'ArrowDown' && state.termHistIdx >= 0) {
      e.preventDefault();
      state.termHistIdx--;
      termInput.value = state.termHistIdx >= 0 ? state.termHistory[state.termHistIdx] : '';
    }
  });

  // Inspector controls
  document.querySelectorAll('[data-inspector-tab]').forEach((btn) => {
    btn.addEventListener('click', () => openInspectorTab(btn.dataset.inspectorTab));
  });
  $('btn-close-inspector').addEventListener('click', toggleInspector);
  $('btn-code-mention').addEventListener('click', () => {
    if (state.selectedFile) attachFileByPath(state.selectedFile.relativePath);
  });
  $('btn-code-edit').addEventListener('click', () => {
    if (!state.selectedFile || state.selectedFile.binary) return;
    state.fileEditMode = !state.fileEditMode;
    if (state.fileEditMode) {
      $('code-editor-textarea').value = state.selectedFile.content || '';
      $('code-view-container').classList.add('hidden');
      $('code-editor-textarea').classList.remove('hidden');
      $('btn-code-save').classList.remove('hidden');
      $('btn-code-edit').textContent = 'Hủy sửa';
      $('code-editor-textarea').focus();
    } else {
      $('code-editor-textarea').classList.add('hidden');
      $('code-view-container').classList.remove('hidden');
      $('btn-code-save').classList.add('hidden');
      $('btn-code-edit').textContent = 'Sửa';
    }
  });
  $('btn-code-save').addEventListener('click', saveEditedFile);
  $('btn-diff-mode').addEventListener('click', () => {
    state.diffSplitMode = !state.diffSplitMode;
    renderCurrentDiff();
  });
  $('btn-wire-clear').addEventListener('click', () => ($('wire-log').innerHTML = ''));

  // Settings Modal
  document.querySelectorAll('input[name="authMode"]').forEach((r) => {
    r.addEventListener('change', () => updateAuthBoxes(r.value));
  });
  $('select-theme').addEventListener('change', (e) => {
    document.documentElement.setAttribute('data-theme', e.target.value);
  });
  $('input-fontsize').addEventListener('input', (e) => {
    $('fontsize-val').textContent = e.target.value;
    document.documentElement.style.setProperty('--code-size', `${e.target.value}px`);
  });
  $('btn-close-settings').addEventListener('click', () => $('settings-modal').classList.add('hidden'));
  $('btn-settings-cancel').addEventListener('click', () => {
    applySettingsToUi(state.settings);
    $('settings-modal').classList.add('hidden');
  });
  $('btn-settings-save').addEventListener('click', () => saveSettingsFromModal(false));
  $('btn-clear-apikey').addEventListener('click', () => saveSettingsFromModal(true));
  $('btn-settings-login').addEventListener('click', () => invoke('cli:login'));
  $('btn-settings-logout').addEventListener('click', async () => {
    const r = await invoke('acp:logout');
    toast(r?.ok ? 'Đã đăng xuất khỏi Antigravity.' : (r?.error || 'Lỗi đăng xuất'), r?.ok ? 'ok' : 'err');
  });

  // Server Mode Auth Overlay & Security Controls
  $('auth-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const pwd = $('auth-password-input').value;
    const remember = $('auth-remember').checked;
    const btn = $('btn-auth-submit');
    btn.disabled = true;
    btn.textContent = 'Đang xác thực…';
    $('auth-error').classList.add('hidden');
    try {
      const res = await loginServer(pwd, remember);
      if (res?.ok) {
        $('auth-password-input').value = '';
        await startServerWorkbench();
      } else {
        $('auth-error').textContent = res?.error || 'Đăng nhập thất bại.';
        $('auth-error').classList.remove('hidden');
      }
    } catch (err) {
      $('auth-error').textContent = `Lỗi kết nối: ${err.message}`;
      $('auth-error').classList.remove('hidden');
    } finally {
      btn.disabled = false;
      btn.textContent = 'Mở khóa & Kết nối';
    }
  });

  $('btn-change-pwd').addEventListener('click', async () => {
    const cur = $('input-cur-pwd').value;
    const next = $('input-new-pwd').value;
    const rev = $('chk-revoke-others').checked;
    const res = await changeServerPassword(cur, next, rev);
    if (res?.ok) {
      $('input-cur-pwd').value = '';
      $('input-new-pwd').value = '';
      toast('Đã đổi mật khẩu máy chủ thành công!', 'ok');
    } else {
      toast(res?.error || 'Không thể đổi mật khẩu', 'err');
    }
  });

  $('btn-server-logout').addEventListener('click', async () => {
    await logoutServer();
    $('settings-modal').classList.add('hidden');
    showAuthOverlay('Đã đăng xuất khỏi máy chủ.');
  });

  $('btn-server-revoke-all').addEventListener('click', async () => {
    await revokeAllServerSessions();
    $('settings-modal').classList.add('hidden');
    showAuthOverlay('Đã thu hồi tất cả phiên đăng nhập trên mọi thiết bị.');
  });

  // Remote Host Directory Browser Modal
  $('btn-close-folder').addEventListener('click', () => $('folder-modal').classList.add('hidden'));
  $('btn-folder-cancel').addEventListener('click', () => $('folder-modal').classList.add('hidden'));
  $('btn-folder-drives').addEventListener('click', () => browseRemoteHostFolder(''));
  $('btn-folder-up').addEventListener('click', () => browseRemoteHostFolder(state.folderBrowseParent || ''));
  $('btn-folder-go').addEventListener('click', () => browseRemoteHostFolder($('input-folder-path').value.trim()));
  $('input-folder-path').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') browseRemoteHostFolder($('input-folder-path').value.trim());
  });
  $('btn-folder-select').addEventListener('click', async () => {
    const target = state.folderBrowsePath || $('input-folder-path').value.trim();
    if (!target) { toast('Hãy chọn một thư mục cụ thể.', 'err'); return; }
    $('folder-modal').classList.add('hidden');
    await openWorkspace(target);
  });

  // Command Palette Modal
  $('cmd-palette-modal').addEventListener('click', (e) => {
    if (e.target === $('cmd-palette-modal')) $('cmd-palette-modal').classList.add('hidden');
  });
  $('palette-input').addEventListener('input', (e) => refreshPaletteResults(e.target.value));
  $('palette-input').addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      state.paletteIdx = (state.paletteIdx + 1) % Math.max(1, state.paletteItems.length);
      renderPalette();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      state.paletteIdx = (state.paletteIdx - 1 + Math.max(1, state.paletteItems.length)) % Math.max(1, state.paletteItems.length);
      renderPalette();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      executePalette(state.paletteIdx);
    } else if (e.key === 'Escape') {
      $('cmd-palette-modal').classList.add('hidden');
    }
  });

  // Global Keyboard Shortcuts
  document.addEventListener('keydown', async (e) => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === 'k') { e.preventDefault(); openCommandPalette(''); return; }
    if (mod && e.key.toLowerCase() === 'p') { e.preventDefault(); openCommandPalette(''); return; }
    if (mod && e.key.toLowerCase() === 'b') { e.preventDefault(); toggleSidebar(); return; }
    if (mod && e.key.toLowerCase() === 'j') { e.preventDefault(); toggleTerminal(); return; }
    if (mod && e.key === '\\') { e.preventDefault(); toggleInspector(); return; }
    if (mod && e.key.toLowerCase() === 'n') { e.preventDefault(); startNewSession(); return; }
    if (mod && e.key.toLowerCase() === 'o') { e.preventDefault(); pickWorkspace(); return; }
    if (mod && e.key === ',') { e.preventDefault(); openSettingsModal(); return; }
    if (mod && e.key.toLowerCase() === 'l') { e.preventDefault(); clearTimeline(); return; }
    if (mod && e.key.toLowerCase() === 's' && state.fileEditMode) { e.preventDefault(); saveEditedFile(); return; }

    if (e.shiftKey && e.key === 'Tab') {
      e.preventDefault();
      cycleMode();
      return;
    }

    if (e.key === 'Escape') {
      if (!$('cmd-palette-modal').classList.contains('hidden')) { $('cmd-palette-modal').classList.add('hidden'); return; }
      if (!$('settings-modal').classList.contains('hidden')) { $('settings-modal').classList.add('hidden'); return; }
      if (!$('folder-modal').classList.contains('hidden')) { $('folder-modal').classList.add('hidden'); return; }
      if (state.busy) { await invoke('acp:cancel'); toast('Đã hủy turn hiện tại.'); }
      return;
    }

    // Quick Y/N approval when a permission card is awaiting and user isn't typing in an input
    const tag = (document.activeElement?.tagName || '').toLowerCase();
    if (state.pendingPermKey && tag !== 'input' && tag !== 'textarea' && tag !== 'select') {
      if (e.key.toLowerCase() === 'y' || e.key.toLowerCase() === 'n') {
        const wantAllow = e.key.toLowerCase() === 'y';
        const opt = state.pendingPermOptions.find((o) =>
          wantAllow ? ((o.kind || '').includes('allow') || o.optionId === 'allow')
                    : ((o.kind || '').includes('reject') || o.optionId === 'deny')
        );
        if (opt) {
          const card = document.querySelector(`.perm-card[data-request-key="${state.pendingPermKey}"]`);
          resolvePermission(state.pendingPermKey, opt.optionId, false, card, opt.name);
        }
      }
    }
  });
}

async function pickWorkspace() {
  if (state.serverMode) {
    openRemoteFolderBrowser(state.workspace);
    return;
  }
  const res = await invoke('workspace:pick');
  if (res?.remoteBrowseRequired) {
    openRemoteFolderBrowser(res.currentPath || state.workspace);
    return;
  }
  if (res?.ok) {
    applySettingsToUi(res.settings);
    state.sessionDiffs.clear();
    clearTimeline();
    updateWorkspaceUi(res.workspace, res.workspaceName, res.git, res.tree);
    if (res.connection?.ok && res.connection.session) {
      applySessionPayload(res.connection.session, res.connection.tier);
    }
    await refreshSessions();
    toast(`Đã mở workspace: ${res.workspaceName}`, 'ok');
  }
}

// ============================================================================
// Server Mode: Multi-Room Manager & Remote Host Folder Browser
// ============================================================================
function renderRoomsUi() {
  if (!state.serverMode) return;
  const cur = state.rooms.find((r) => r.id === state.currentRoomId) || state.rooms[0];
  if (cur) {
    $('room-name').textContent = cur.name;
    $('room-clients-badge').textContent = String(cur.clientCount || 1);
    $('room-mode-dot').className = `room-mode-dot ${cur.isShared ? 'shared' : 'isolated'}`;
  }

  const list = $('rooms-dropdown-list');
  if (!state.rooms.length) {
    list.innerHTML = '<div class="muted-label" style="padding:6px 9px">Chưa có Room nào</div>';
    return;
  }

  list.innerHTML = state.rooms.map((r) => {
    const active = r.id === state.currentRoomId;
    const tag = r.isShared ? 'SHARED' : 'ISOLATED';
    const delBtn = r.id !== 'default'
      ? `<button class="icon-btn xs del-room-btn" data-del-room="${escapeHtml(r.id)}" title="Đóng Room & giải phóng RAM">✕</button>`
      : '';
    return `
      <div class="room-list-row${active ? ' active' : ''}" data-join-room="${escapeHtml(r.id)}">
        <span class="room-mode-dot ${r.isShared ? 'shared' : 'isolated'}"></span>
        <div class="room-list-main">
          <div class="room-list-title">
            <span class="truncate">${escapeHtml(r.name)}</span>
            <span class="room-type-tag">${tag}</span>
            <span class="room-clients-badge mono">${r.clientCount || 0}</span>
          </div>
          <div class="room-list-sub truncate mono">${escapeHtml(r.workspaceName || r.workspace)} · ${escapeHtml(r.sessionTitle || 'Phiên mới')}</div>
        </div>
        ${delBtn}
      </div>
    `;
  }).join('');

  list.querySelectorAll('[data-join-room]').forEach((row) => {
    row.addEventListener('click', () => {
      $('room-dropdown').classList.add('hidden');
      const id = row.dataset.joinRoom;
      if (id && id !== state.currentRoomId) switchRoom(id);
    });
  });

  list.querySelectorAll('[data-del-room]').forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      deleteRoom(btn.dataset.delRoom);
    });
  });
}

async function createRoom(isShared) {
  toast(isShared ? 'Đang tạo Shared Room mới…' : 'Đang tạo Room Độc Lập mới…');
  try {
    const res = await invoke('room:create', { isShared, workspace: state.workspace });
    if (res?.ok && res.init) {
      state.currentRoomId = res.room.id;
      try { localStorage.setItem('anticli_room', res.room.id); } catch {}
      await applyInitPayload(res.init);
      toast(`Đã chuyển sang ${res.room.name}`, 'ok');
    }
  } catch (err) {
    toast(`Lỗi tạo Room: ${err.message}`, 'err');
  }
}

async function switchRoom(roomId) {
  toast('Đang chuyển Room…');
  try {
    const res = await invoke('room:join', { roomId });
    if (res?.ok && res.init) {
      state.currentRoomId = roomId;
      try { localStorage.setItem('anticli_room', roomId); } catch {}
      await applyInitPayload(res.init);
      toast(`Đã tham gia ${res.room.name}`, 'ok');
    }
  } catch (err) {
    toast(`Lỗi chuyển Room: ${err.message}`, 'err');
  }
}

async function deleteRoom(roomId) {
  try {
    const res = await invoke('room:delete', { roomId });
    if (res?.ok) {
      if (Array.isArray(res.rooms)) state.rooms = res.rooms;
      if (res.switchedToDefault && res.init) {
        state.currentRoomId = 'default';
        try { localStorage.setItem('anticli_room', 'default'); } catch {}
        await applyInitPayload(res.init);
      } else {
        renderRoomsUi();
      }
      toast('Đã đóng Room và giải phóng tiến trình ACP.', 'ok');
    } else {
      toast(res?.error || 'Không thể xóa Room', 'err');
    }
  } catch (err) {
    toast(`Lỗi xóa Room: ${err.message}`, 'err');
  }
}

function openRemoteFolderBrowser(initialPath) {
  $('folder-modal').classList.remove('hidden');
  browseRemoteHostFolder(initialPath || '');
}

async function browseRemoteHostFolder(path) {
  const list = $('folder-list');
  list.innerHTML = '<div class="empty-state">Đang đọc thư mục máy chủ…</div>';
  try {
    const res = await invoke('workspace:browseHost', { path: path || '' });
    if (!res?.ok) {
      list.innerHTML = `<div class="empty-state">${escapeHtml(res?.error || 'Không thể đọc thư mục')}</div>`;
      return;
    }
    state.folderBrowsePath = res.currentPath || '';
    state.folderBrowseParent = res.parentPath || '';
    $('input-folder-path').value = state.folderBrowsePath;
    $('folder-selected-preview').textContent = state.folderBrowsePath || 'Chọn một ổ đĩa hoặc thư mục';

    const quickStrip = $('folder-quick-strip');
    if (Array.isArray(res.quick) && res.quick.length) {
      quickStrip.classList.remove('hidden');
      quickStrip.innerHTML = res.quick.map((q) =>
        `<button class="chip-btn" data-qpath="${escapeHtml(q.fullPath)}">★ ${escapeHtml(q.name)}</button>`
      ).join('');
      quickStrip.querySelectorAll('[data-qpath]').forEach((b) => {
        b.addEventListener('click', () => browseRemoteHostFolder(b.dataset.qpath));
      });
    } else {
      quickStrip.classList.add('hidden');
    }

    const entries = res.entries || [];
    if (!entries.length) {
      list.innerHTML = '<div class="empty-state">Thư mục này không có thư mục con (bấm "Mở thư mục này" để chọn).</div>';
      return;
    }
    list.innerHTML = entries.map((d) => `
      <div class="folder-row" data-fpath="${escapeHtml(d.fullPath)}">
        <span>${d.isDrive ? '💽' : '📁'}</span>
        <span class="truncate mono">${escapeHtml(d.name)}</span>
      </div>
    `).join('');
    list.querySelectorAll('[data-fpath]').forEach((row) => {
      row.addEventListener('click', () => browseRemoteHostFolder(row.dataset.fpath));
    });
  } catch (err) {
    list.innerHTML = `<div class="empty-state">Lỗi: ${escapeHtml(err.message)}</div>`;
  }
}

// ============================================================================
// Resizable Panes
// ============================================================================
function setupResizers() {
  bindHorizontalDrag($('resizer-left'), (dx, startW) => {
    $('sidebar').style.width = `${Math.max(190, Math.min(480, startW + dx))}px`;
  }, () => $('sidebar').getBoundingClientRect().width);

  bindHorizontalDrag($('resizer-right'), (dx, startW) => {
    $('inspector-pane').style.width = `${Math.max(280, Math.min(window.innerWidth * 0.6, startW - dx))}px`;
  }, () => $('inspector-pane').getBoundingClientRect().width);

  const termResizer = $('resizer-terminal');
  termResizer.addEventListener('mousedown', (e) => {
    e.preventDefault();
    const startY = e.clientY;
    const startH = $('terminal-pane').getBoundingClientRect().height;
    termResizer.classList.add('dragging');
    const onMove = (mv) => {
      const h = Math.max(110, Math.min(window.innerHeight * 0.6, startH - (mv.clientY - startY)));
      $('terminal-pane').style.height = `${h}px`;
    };
    const onUp = () => {
      termResizer.classList.remove('dragging');
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  });
}

function bindHorizontalDrag(handle, onResize, getStartW) {
  handle.addEventListener('mousedown', (e) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = getStartW();
    handle.classList.add('dragging');
    const onMove = (mv) => onResize(mv.clientX - startX, startW);
    const onUp = () => {
      handle.classList.remove('dragging');
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  });
}

// ============================================================================
// Small Helpers
// ============================================================================
function setConnStatus(st, label) {
  state.connState = st;
  $('conn-dot').className = `status-dot ${st}`;
  $('conn-label').textContent = label;
}

function showTierBadge(name) {
  const b = $('tier-badge');
  if (name) {
    b.textContent = name;
    b.classList.remove('hidden');
  } else {
    b.classList.add('hidden');
  }
}

function setBusy(busy) {
  state.busy = busy;
  $('btn-send').classList.toggle('hidden', busy);
  $('btn-stop').classList.toggle('hidden', !busy);
  $('prompt-gutter').textContent = busy ? '◌' : '>';
}

function updateContextMeter(used, size) {
  const pct = size > 0 ? Math.min(100, (used / size) * 100) : 0;
  $('ctx-label').textContent = `${fmtTokens(used)} / ${fmtTokens(size)}`;
  const fill = $('ctx-fill');
  fill.style.width = `${pct.toFixed(1)}%`;
  fill.style.background = pct > 80 ? 'var(--red)' : pct > 55 ? 'var(--amber)' : 'var(--accent)';
}

function updateMetricsUi(m) {
  if (m && typeof m.workingSetMb === 'number') {
    $('ram-badge').textContent = `${m.workingSetMb.toFixed(0)} MB`;
  }
}

function autoResizePrompt() {
  const el = $('prompt-input');
  el.style.height = 'auto';
  const nextH = Math.min(180, Math.max(24, el.scrollHeight));
  el.style.height = `${nextH}px`;
  el.style.overflowY = el.scrollHeight > 180 ? 'auto' : 'hidden';
}

function scrollToBottom(force = false) {
  if (!force && !state.autoScroll) return;
  const box = $('stream-scroll');
  box.scrollTop = box.scrollHeight;
}
function scrollToBottomIfPinned() { scrollToBottom(false); }

function fmtTokens(n) {
  if (n >= 1000000) return `${(n / 1000000).toFixed(n % 1000000 === 0 ? 0 : 1)}M`;
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
}

function fmtNum(n) { return Number(n || 0).toLocaleString('en-US'); }

function fmtBytes(b) {
  if (b < 1024) return `${b} B`;
  if (b < 1048576) return `${(b / 1024).toFixed(1)} KB`;
  return `${(b / 1048576).toFixed(1)} MB`;
}

function toast(msg, kind = '') {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.textContent = msg;
  $('toast-stack').appendChild(el);
  setTimeout(() => el.remove(), 3400);
}

boot();
