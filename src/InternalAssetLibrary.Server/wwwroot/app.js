'use strict';

const state = {
  token: sessionStorage.getItem('assetLibraryToken'),
  me: null,
  view: 'overview',
  assetState: 'active',
  avatarUrl: null,
  adminUsers: [],
  selectedAdminUserId: null,
  adminTab: 'users',
  adminAssetState: 'active',
  adminLutState: 'active',
  debounce: null,
  realtimeSocket: null,
  realtimeConnecting: false,
  realtimeReconnectTimer: null,
  realtimeRefreshTimer: null,
  realtimePingTimer: null,
  realtimeRefreshMe: false,
  realtimeConnectedOnce: false,
  realtimeRetry: 0
};

const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];
const viewNames = {
  overview: ['WORKSPACE', '概览'],
  assets: ['SHARED LIBRARY', '共享素材'],
  users: ['TEAM DIRECTORY', '团队成员'],
  profile: ['MY ACCOUNT', '个人资料'],
  admin: ['ADMINISTRATION', '管理后台']
};
const categoryLabels = { bgm: 'BGM', 'sound-effect': '音效', image: '图片', video: '视频' };
const adminManagement = window.AdminUserManagement;

function isAdminRoute(pathname = window.location.pathname) {
  const normalized = String(pathname || '/').replace(/\/+$/, '') || '/';
  return normalized === '/admin' || normalized.startsWith('/admin/');
}

const adminTabs = ['users', 'assets', 'luts', 'tags', 'audit', 'settings'];

function normalizeRoutePath(pathname = window.location.pathname) {
  return String(pathname || '/').replace(/\/+$/, '') || '/';
}

function parseAdminRoute(pathname = window.location.pathname) {
  const path = normalizeRoutePath(pathname);
  if (path === '/admin' || path === '/admin/overview') return { view: 'overview', adminTab: null };
  if (path === '/admin/shared') return { view: 'assets', adminTab: null };
  if (path === '/admin/member' || path === '/admin/menber') return { view: 'users', adminTab: null };
  if (path === '/admin/profile') return { view: 'profile', adminTab: null };

  const tabAliases = {
    '/admin/management': 'users',
    '/admin/users': 'users',
    '/admin/assets': 'assets',
    '/admin/luts': 'luts',
    '/admin/tags': 'tags',
    '/admin/audit': 'audit',
    '/admin/settings': 'settings',
    '/admin/management/users': 'users',
    '/admin/management/assets': 'assets',
    '/admin/management/luts': 'luts',
    '/admin/management/tags': 'tags',
    '/admin/management/audit': 'audit',
    '/admin/management/settings': 'settings'
  };
  if (tabAliases[path]) return { view: 'admin', adminTab: tabAliases[path] };
  const managementPrefix = '/admin/management/';
  if (path.startsWith(managementPrefix)) {
    const tab = path.slice(managementPrefix.length);
    return { view: 'admin', adminTab: adminTabs.includes(tab) ? tab : 'users' };
  }
  return { view: 'overview', adminTab: null };
}

function pathForView(name, adminTab = state.adminTab) {
  if (name === 'overview') return '/admin';
  if (name === 'assets') return '/admin/shared';
  if (name === 'users') return '/admin/member';
  if (name === 'profile') return '/admin/profile';
  if (name === 'admin') return `/admin/management/${adminTabs.includes(adminTab) ? adminTab : 'users'}`;
  return '/admin';
}

function updateRoute(path, replace = false) {
  if (normalizeRoutePath(location.pathname) === normalizeRoutePath(path)) return;
  const method = replace ? 'replaceState' : 'pushState';
  history[method]({ view: state.view, adminTab: state.adminTab }, '', path);
}

class ApiError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (state.token) headers.set('Authorization', `Bearer ${state.token}`);
  let body = options.body;
  if (body && !(body instanceof Blob) && !(body instanceof ArrayBuffer) && typeof body !== 'string') {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(body);
  }
  try {
    const response = await fetch(path, { ...options, headers, body });
    $('#connectionBadge').classList.remove('offline');
    const contentType = response.headers.get('content-type') || '';
    const payload = response.status === 204 ? null : contentType.includes('json') ? await response.json() : await response.text();
    if (!response.ok) {
      const error = payload?.error;
      if (response.status === 401 && path !== '/api/auth/login') signOut(false);
      throw new ApiError(
        response.status,
        payload?.code || error?.code || 'request_failed',
        payload?.detail || error?.message || payload?.title || `请求失败 (${response.status})`);
    }
    return payload;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    $('#connectionBadge').classList.add('offline');
    throw new ApiError(0, 'network_error', '无法连接服务器。');
  }
}

function notify(message, kind = 'success') {
  const toast = document.createElement('div');
  toast.className = `toast ${kind === 'error' ? 'error' : ''}`;
  toast.textContent = message;
  $('#toastRegion').append(toast);
  setTimeout(() => toast.remove(), 3600);
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes, index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  return `${value >= 10 || index === 0 ? value.toFixed(index ? 1 : 0) : value.toFixed(2)} ${units[index]}`;
}

function formatDate(value) {
  if (!value) return '—';
  return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium' }).format(new Date(value));
}

function initialFor(user) {
  const text = (user.displayName || user.username || 'U').trim();
  return [...text][0]?.toUpperCase() || 'U';
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[character]);
}

function setIdentity() {
  const label = state.me.displayName || state.me.username;
  $('#sideIdentity').innerHTML = `<strong>${escapeHtml(label)}</strong><small>@${escapeHtml(state.me.username)}</small>`;
  $('#adminNav').classList.toggle('hidden', !state.me.isAdmin);
  const canBrowse = hasPermission('assets.browse');
  $('#mainNav [data-view="assets"]').classList.toggle('hidden', !canBrowse);
  $('#openAssetDialog').classList.toggle('hidden', !hasPermission('assets.upload'));
}

function hasPermission(permission) {
  return Boolean(state.me?.isAdmin || state.me?.permissions?.includes(permission));
}

async function loadMe(showPasswordRecommendation = false) {
  state.me = await api('/api/me');
  const route = parseAdminRoute();
  state.view = route.view;
  state.adminTab = route.adminTab || 'users';
  if (isAdminRoute() && !state.me.isAdmin) {
    // Keep the protected entry URL stable.  Replacing it with '/' made a
    // browser refresh request the reserved web-library root and return 404.
    signOut(false);
    throw new Error('当前账号没有管理后台权限。');
  }
  setIdentity();
  $('#loginView').classList.add('hidden');
  $('#appView').classList.remove('hidden');
  await showView(state.view);
  connectRealtime();
  if (showPasswordRecommendation) $('#passwordRecommendationDialog').showModal();
}

function signOut(callServer = true) {
  if (callServer && state.token) api('/api/auth/logout', { method: 'POST' }).catch(() => {});
  sessionStorage.removeItem('assetLibraryToken');
  state.token = null;
  state.me = null;
  disconnectRealtime();
  if (state.avatarUrl) URL.revokeObjectURL(state.avatarUrl);
  $('#appView').classList.add('hidden');
  $('#loginView').classList.remove('hidden');
  $('#loginPassword').value = '';
}

async function showView(name, { updateHistory = true } = {}) {
  if (name === 'admin' && !state.me?.isAdmin) name = 'overview';
  state.view = name;
  if (updateHistory) updateRoute(pathForView(name));
  $$('.view').forEach(view => view.classList.toggle('active', view.id === `${name}View`));
  $$('#mainNav button').forEach(button => button.classList.toggle('active', button.dataset.view === name));
  $('#viewEyebrow').textContent = viewNames[name][0];
  $('#viewTitle').textContent = viewNames[name][1];
  try {
    if (name === 'overview') await loadOverview();
    if (name === 'assets') await loadAssets();
    if (name === 'users') await loadUsers();
    if (name === 'profile') await loadProfile();
    if (name === 'admin') showAdminTab(state.adminTab, { updateHistory: false });
  } catch (error) { notify(error.message, 'error'); }
}

window.addEventListener('popstate', () => {
  if (!state.me) return;
  const route = parseAdminRoute();
  state.adminTab = route.adminTab || 'users';
  showView(route.view, { updateHistory: false });
});

function disconnectRealtime() {
  if (state.realtimeReconnectTimer) clearTimeout(state.realtimeReconnectTimer);
  if (state.realtimeRefreshTimer) clearTimeout(state.realtimeRefreshTimer);
  if (state.realtimePingTimer) clearInterval(state.realtimePingTimer);
  state.realtimeReconnectTimer = null;
  state.realtimeRefreshTimer = null;
  state.realtimePingTimer = null;
  state.realtimeRefreshMe = false;
  state.realtimeConnectedOnce = false;
  state.realtimeConnecting = false;
  state.realtimeRetry = 0;
  const socket = state.realtimeSocket;
  state.realtimeSocket = null;
  if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, 'signed out');
}

function scheduleRealtimeReconnect() {
  if (!state.token || !state.me || state.realtimeReconnectTimer) return;
  const delay = Math.min(15000, 1000 * (2 ** Math.min(state.realtimeRetry++, 4)));
  state.realtimeReconnectTimer = setTimeout(() => {
    state.realtimeReconnectTimer = null;
    connectRealtime();
  }, delay);
}

function scheduleRealtimeRefresh(notification) {
  const supported = new Set(['assets', 'tags', 'markers', 'users', 'profiles', 'derivatives', 'luts']);
  if (!notification || !supported.has(notification.scope)) return;
  if ((notification.scope === 'users' || notification.scope === 'profiles') &&
      (!notification.entityId || notification.entityId.toLowerCase() === state.me?.id?.toLowerCase())) {
    state.realtimeRefreshMe = true;
  }
  if (state.realtimeRefreshTimer) clearTimeout(state.realtimeRefreshTimer);
  state.realtimeRefreshTimer = setTimeout(async () => {
    state.realtimeRefreshTimer = null;
    if (!state.me || !state.token) return;
    const refreshMe = state.realtimeRefreshMe;
    state.realtimeRefreshMe = false;
    try {
      if (refreshMe) {
        state.me = await api('/api/me');
        setIdentity();
      }
      await showView(state.view);
    } catch (error) {
      if (state.me && state.token) notify(error.message, 'error');
    }
  }, 350);
}

async function refreshAfterRealtimeReconnect() {
  if (!state.me || !state.token) return;
  try {
    state.me = await api('/api/me');
    setIdentity();
    await showView(state.view);
  } catch (error) {
    if (state.me && state.token) notify(error.message, 'error');
  }
}

function completeRealtimeHandshake(socket) {
  if (state.realtimeSocket !== socket) return;
  state.realtimeRetry = 0;
  if (state.realtimePingTimer) clearInterval(state.realtimePingTimer);
  state.realtimePingTimer = setInterval(() => {
    if (state.realtimeSocket !== socket || socket.readyState !== WebSocket.OPEN) return;
    socket.send(`${JSON.stringify({ type: 6 })}${String.fromCharCode(0x1e)}`);
  }, 15000);

  const isReconnect = state.realtimeConnectedOnce;
  state.realtimeConnectedOnce = true;
  if (isReconnect) refreshAfterRealtimeReconnect();
}

function handleRealtimeFrame(payload, socket) {
  for (const frame of String(payload).split(String.fromCharCode(0x1e))) {
    if (!frame) continue;
    let message;
    try { message = JSON.parse(frame); } catch { continue; }
    if (socket.assetLibraryHandshakePending) {
      socket.assetLibraryHandshakePending = false;
      if (message.error) {
        socket.close(1002, 'SignalR handshake failed');
        return;
      }
      completeRealtimeHandshake(socket);
      continue;
    }
    if (message.type === 1 && message.target === 'libraryChanged') {
      scheduleRealtimeRefresh(message.arguments?.[0]);
    } else if (message.type === 7 && state.realtimeSocket === socket) {
      socket.close(1011, 'server closed hub connection');
    }
  }
}

async function connectRealtime() {
  if (!state.token || !state.me || state.realtimeConnecting ||
      state.realtimeSocket?.readyState === WebSocket.OPEN ||
      state.realtimeSocket?.readyState === WebSocket.CONNECTING) return;
  state.realtimeConnecting = true;
  try {
    const negotiation = await api('/hubs/library/negotiate?negotiateVersion=1', { method: 'POST' });
    if (!negotiation.connectionToken ||
        !negotiation.availableTransports?.some(item => item.transport === 'WebSockets')) {
      throw new ApiError(0, 'signalr_unavailable', '服务器未开放实时更新连接。');
    }

    const url = new URL('/hubs/library', window.location.href);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    url.searchParams.set('id', negotiation.connectionToken);
    url.searchParams.set('access_token', state.token);
    const socket = new WebSocket(url);
    socket.assetLibraryHandshakePending = true;
    state.realtimeSocket = socket;
    socket.addEventListener('open', () => {
      socket.send(`${JSON.stringify({ protocol: 'json', version: 1 })}${String.fromCharCode(0x1e)}`);
    });
    socket.addEventListener('message', event => handleRealtimeFrame(event.data, socket));
    socket.addEventListener('close', () => {
      if (state.realtimeSocket !== socket) return;
      if (state.realtimePingTimer) clearInterval(state.realtimePingTimer);
      state.realtimePingTimer = null;
      state.realtimeSocket = null;
      scheduleRealtimeReconnect();
    });
    socket.addEventListener('error', () => socket.close());
  } catch (error) {
    if (error.status !== 401) scheduleRealtimeReconnect();
  } finally {
    state.realtimeConnecting = false;
  }
}

async function loadOverview() {
  const users = await api('/api/users?pageSize=6');
  const [library, assets] = hasPermission('assets.browse')
    ? await Promise.all([api('/api/library/status'), api('/api/assets?sort=uploaded&order=desc&pageSize=6')])
    : [{ activeAssets: 0, originalBytes: 0, quotaBytes: 0, recycledAssets: 0 }, { items: [] }];
  $('#activeAssetMetric').textContent = hasPermission('assets.browse') ? library.activeAssets : '—';
  $('#storageMetric').textContent = hasPermission('assets.browse') ? formatBytes(library.originalBytes) : '—';
  $('#quotaMetric').textContent = hasPermission('assets.browse')
    ? `共 ${formatBytes(library.quotaBytes)} · 已用 ${Math.round((library.usageRatio || 0) * 100)}% · ${library.capacityLevel || 'normal'}`
    : '未开放素材浏览权限';
  $('#memberMetric').textContent = users.total;
  $('#trashMetric').textContent = library.recycledAssets;
  renderRecentAssets(assets.items);
  renderRecentUsers(users.items);
}

function renderRecentAssets(items) {
  const root = $('#recentAssets');
  root.replaceChildren(...items.map(asset => {
    const item = document.createElement('div');
    item.className = 'compact-item';
    item.innerHTML = `<div class="compact-main"><span class="type-tile">${escapeHtml(categoryLabels[asset.category] || asset.category)}</span><div><strong>${escapeHtml(asset.name)}</strong><small>${escapeHtml(asset.uploadedBy.displayName || asset.uploadedBy.username)} · ${formatBytes(asset.sizeBytes)}</small></div></div><small class="muted">${formatDate(asset.uploadedAt)}</small>`;
    return item;
  }));
  if (!items.length) root.innerHTML = '<div class="empty">暂无素材</div>';
}

function renderRecentUsers(items) {
  const root = $('#recentUsers');
  root.replaceChildren(...items.map(user => {
    const item = document.createElement('div');
    item.className = 'compact-item';
    item.innerHTML = `<div class="compact-main"><span class="avatar">${escapeHtml(initialFor(user))}</span><div><strong>${escapeHtml(user.displayName || user.username)}</strong><small>@${escapeHtml(user.username)}</small></div></div><span class="badge ${user.isEnabled ? 'active' : 'disabled'}">${user.isEnabled ? '使用中' : '已停用'}</span>`;
    return item;
  }));
  if (!items.length) root.innerHTML = '<div class="empty">暂无成员</div>';
}

async function loadAssets() {
  const search = encodeURIComponent($('#assetSearch').value.trim());
  const category = encodeURIComponent($('#assetCategory').value);
  const [sort, order] = $('#assetSort').value.split('-');
  const result = await api(`/api/assets?state=${state.assetState}&search=${search}&category=${category}&sort=${sort}&order=${order}&pageSize=100`);
  renderAssetRows(result.items);
}

function renderAssetRows(items) {
  const body = $('#assetRows');
  body.innerHTML = items.map(asset => {
    const tags = asset.tags.length
      ? `<div class="tag-list">${asset.tags.map(tag => `<span class="badge">${escapeHtml(tag)}</span>`).join('')}</div>`
      : '<span class="muted">—</span>';
    const ownsAsset = state.me.isAdmin || asset.uploadedBy.id === state.me.id;
    const canDelete = state.me.isAdmin || (ownsAsset && hasPermission('assets.delete-own'));
    const actions = !ownsAsset || !canDelete ? '' : state.assetState === 'recycled'
      ? `<button data-asset-action="restore" data-id="${asset.id}">恢复</button><button class="danger" data-asset-action="permanent" data-id="${asset.id}">永久删除</button>`
      : `<button class="danger" data-asset-action="recycle" data-id="${asset.id}">移入回收站</button>`;
    return `<tr><td><div class="cell-title"><strong>${escapeHtml(asset.name)}</strong><small>${escapeHtml(asset.originalFileName)}</small></div></td><td><span class="badge">${escapeHtml(categoryLabels[asset.category] || asset.category)}</span></td><td>${tags}</td><td>${escapeHtml(asset.uploadedBy.displayName || asset.uploadedBy.username)}</td><td>${formatBytes(asset.sizeBytes)}</td><td>${formatDate(asset.uploadedAt)}</td><td><div class="row-actions">${actions}</div></td></tr>`;
  }).join('');
  $('#assetEmpty').classList.toggle('hidden', items.length > 0);
}

async function loadUsers() {
  const search = encodeURIComponent($('#userSearch').value.trim());
  const result = await api(`/api/users?search=${search}&pageSize=100`);
  const root = $('#userGrid');
  root.innerHTML = result.items.map(user => {
    const counts = user.assetCounts || {};
    return `<button class="user-card" data-user-id="${user.id}"><div class="user-card-top"><span class="avatar">${escapeHtml(initialFor(user))}</span><div><strong>${escapeHtml(user.displayName || user.username)}</strong><small>@${escapeHtml(user.username)} · ${user.isEnabled ? '使用中' : '已停用'}</small></div></div><p>${escapeHtml(user.bio || '暂无个人简介')}</p><div class="count-row"><span>BGM ${counts.bgm || 0}</span><span>音效 ${counts['sound-effect'] || 0}</span><span>图片 ${counts.image || 0}</span><span>视频 ${counts.video || 0}</span></div></button>`;
  }).join('');
  $('#userEmpty').classList.toggle('hidden', result.items.length > 0);
}

async function openUserProfile(userId) {
  const user = await api(`/api/users/${userId}`);
  const assets = hasPermission('assets.browse')
    ? await api(`/api/assets?uploaderId=${userId}&sort=uploaded&order=desc&pageSize=12`)
    : { items: [] };
  $('#userProfileTitle').textContent = user.displayName || user.username;
  const counts = user.assetCounts || {};
  $('#userProfileContent').innerHTML = `<div class="profile-detail"><span class="avatar large">${escapeHtml(initialFor(user))}</span><dl><dt>用户名</dt><dd>@${escapeHtml(user.username)}</dd><dt>状态</dt><dd>${user.isEnabled ? '使用中' : '已停用'}</dd><dt>简介</dt><dd>${escapeHtml(user.bio || '—')}</dd><dt>生日</dt><dd>${escapeHtml(user.birthday || '—')}</dd><dt>性别</dt><dd>${escapeHtml(user.customGender || user.gender || '—')}</dd><dt>联系方式</dt><dd>${escapeHtml(user.contact || '—')}</dd><dt>素材</dt><dd>BGM ${counts.bgm || 0} · 音效 ${counts['sound-effect'] || 0} · 图片 ${counts.image || 0} · 视频 ${counts.video || 0}</dd><dt>最近上传</dt><dd>${assets.items.slice(0, 4).map(asset => escapeHtml(asset.name)).join('、') || '—'}</dd></dl></div>`;
  $('#userProfileDialog').showModal();
}

async function loadProfile() {
  state.me = await api('/api/me');
  setIdentity();
  $('#profileHeading').textContent = state.me.displayName || state.me.username;
  $('#profileUsername').textContent = `@${state.me.username}`;
  $('#avatarInitial').textContent = initialFor(state.me);
  $('#profileDisplayName').value = state.me.displayName || '';
  $('#profileBirthday').value = state.me.birthday || '';
  $('#profileBio').value = state.me.bio || '';
  $('#profileGender').value = state.me.gender || '';
  $('#profileCustomGender').value = state.me.customGender || '';
  $('#profileContact').value = state.me.contact || '';
  $('#birthdayVisibility').value = state.me.birthdayVisibility || 'private';
  $('#genderVisibility').value = state.me.genderVisibility || 'private';
  $('#contactVisibility').value = state.me.contactVisibility || 'private';
  $('#profileEmail').value = state.me.email || '';
  $('#removeAvatarButton').classList.toggle('hidden', !state.me.hasAvatar);
  await refreshCurrentAvatar();
}

async function refreshCurrentAvatar() {
  if (state.avatarUrl) { URL.revokeObjectURL(state.avatarUrl); state.avatarUrl = null; }
  $('#avatarImage').classList.add('hidden');
  $('#avatarInitial').classList.remove('hidden');
  if (!state.me?.hasAvatar) return;
  try {
    const response = await fetch(`/api/users/${state.me.id}/avatar`, { headers: { Authorization: `Bearer ${state.token}` } });
    if (!response.ok) return;
    state.avatarUrl = URL.createObjectURL(await response.blob());
    $('#avatarImage').src = state.avatarUrl;
    $('#avatarImage').classList.remove('hidden');
    $('#avatarInitial').classList.add('hidden');
  } catch { }
}

async function loadAdmin() {
  const users = await api('/api/admin/users');
  state.adminUsers = users;
  $('#adminUserRows').innerHTML = users.map(user => {
    const bindings = adminManagement.bindingLabels(user);
    return `<tr><td><div class="cell-title"><strong>${escapeHtml(user.displayName || user.username)}</strong><small>@${escapeHtml(user.username)}</small></div></td><td><div class="badge-stack"><span class="badge">${user.isAdmin ? '管理员' : '成员'}</span><span class="badge ${user.isEnabled ? 'active' : 'disabled'}">${user.isEnabled ? '使用中' : '已停用'}</span></div></td><td><span class="badge">${user.isAdmin ? '全部' : `${user.permissions.length} / ${adminManagement.permissions.length}`}</span></td><td><span class="table-note">邮箱 ${bindings.email} · 头像 ${bindings.avatar}</span><small class="table-note">${user.mustChangePassword ? '等待首次登录安全提示' : '已完成首次登录'}</small></td><td>${formatDate(user.createdAt)}</td><td><div class="row-actions"><button data-admin-action="manage" data-id="${user.id}">管理</button><button data-admin-action="toggle" data-id="${user.id}" data-enabled="${user.isEnabled}">${user.isEnabled ? '停用' : '恢复'}</button><button class="danger" data-admin-action="reset" data-id="${user.id}">重置密码</button><button class="danger" data-admin-action="delete" data-id="${user.id}" data-username="${escapeHtml(user.username)}" ${user.isEnabled ? 'disabled title="必须先停用账号"' : ''}>永久删除</button></div></td></tr>`;
  }).join('');
}

function showAdminTab(name, { updateHistory = true } = {}) {
  state.adminTab = adminTabs.includes(name) ? name : 'users';
  if (updateHistory && state.view === 'admin') updateRoute(pathForView('admin'));
  $$('#adminTabs [data-admin-tab]').forEach(button => button.classList.toggle('active', button.dataset.adminTab === state.adminTab));
  adminTabs.forEach(tab => $(`#admin${tab[0].toUpperCase()}${tab.slice(1)}Panel`).classList.toggle('hidden', tab !== state.adminTab));
  const tasks = {
    users: loadAdmin,
    assets: loadAdminAssets,
    luts: loadAdminLuts,
    tags: loadAdminTags,
    audit: loadAdminAudit,
    settings: loadAdminSettings
  };
  tasks[state.adminTab]().catch(error => notify(error.message, 'error'));
}

async function loadAdminAssets() {
  const stateName = state.adminAssetState;
  const result = await api(`/api/assets?state=${stateName}&sort=uploaded&order=desc&pageSize=200`);
  $('#adminAssetRows').innerHTML = result.items.map(asset => {
    const owner = asset.uploadedBy?.displayName || asset.uploadedBy?.username || '—';
    const actions = stateName === 'recycled'
      ? `<button data-admin-asset-action="restore" data-id="${asset.id}">恢复</button><button class="danger" data-admin-asset-action="permanent" data-id="${asset.id}">永久删除</button>`
      : `<button class="danger" data-admin-asset-action="recycle" data-id="${asset.id}">移入回收站</button>`;
    return `<tr><td><div class="cell-title"><strong>${escapeHtml(asset.name)}</strong><small>${escapeHtml(asset.originalFileName)}</small></div></td><td>${escapeHtml(categoryLabels[asset.category] || asset.category)}</td><td>${escapeHtml(owner)}</td><td>${formatBytes(asset.sizeBytes)}</td><td>${formatDate(stateName === 'recycled' ? asset.recycledAt : asset.uploadedAt)}</td><td><div class="row-actions">${actions}</div></td></tr>`;
  }).join('');
  $('#adminAssetEmpty').classList.toggle('hidden', result.items.length > 0);
}

async function loadAdminLuts() {
  const result = await api(`/api/luts?state=${state.adminLutState}&pageSize=200`);
  $('#adminLutRows').innerHTML = result.items.map(lut => {
    const actions = state.adminLutState === 'recycled'
      ? `<button data-admin-lut-action="restore" data-id="${lut.id}">恢复</button><button class="danger" data-admin-lut-action="permanent" data-id="${lut.id}">永久删除</button>`
      : `<button class="danger" data-admin-lut-action="recycle" data-id="${lut.id}">移入回收站</button>`;
    return `<tr><td>${escapeHtml(lut.name)}</td><td>${escapeHtml(lut.originalFileName)}</td><td>${escapeHtml(lut.uploadedBy?.displayName || lut.uploadedBy?.username || '—')}</td><td>${formatBytes(lut.sizeBytes)}</td><td>${formatDate(lut.updatedAt)}</td><td><div class="row-actions">${actions}</div></td></tr>`;
  }).join('');
  $('#adminLutEmpty').classList.toggle('hidden', result.items.length > 0);
}

async function loadAdminTags() {
  const tags = await api('/api/tags');
  $('#adminTagRows').innerHTML = tags.map(tag => `<tr><td>${escapeHtml(tag.name)}</td><td>${Number(tag.usageCount) || 0}</td><td><div class="row-actions"><button data-admin-tag-action="rename" data-id="${tag.id}" data-name="${escapeHtml(tag.name)}">重命名</button><button class="danger" data-admin-tag-action="delete" data-id="${tag.id}" data-name="${escapeHtml(tag.name)}">删除</button></div></td></tr>`).join('');
  $('#adminTagEmpty').classList.toggle('hidden', tags.length > 0);
}

async function loadAdminAudit() {
  const records = await api('/api/admin/audit?limit=500');
  $('#adminAuditRows').innerHTML = records.map(record => `<tr><td>${new Date(record.occurredAt).toLocaleString('zh-CN')}</td><td>${escapeHtml(record.action)}</td><td>${escapeHtml(record.targetType)}</td><td>${escapeHtml(record.targetId || '—')}</td><td>${escapeHtml(record.detail || '—')}</td></tr>`).join('');
}

const serverSettingsFields = {
  originalQuotaBytes: ['settingQuotaGiB', 1024 ** 3], audioMaxBytes: ['settingAudioGiB', 1024 ** 3],
  imageMaxBytes: ['settingImageGiB', 1024 ** 3], videoMaxBytes: ['settingVideoGiB', 1024 ** 3],
  thumbnailMaxBytes: ['settingThumbnailMiB', 1024 ** 2], proxyMaxBytes: ['settingProxyGiB', 1024 ** 3],
  lutMaxBytes: ['settingLutMiB', 1024 ** 2]
};

function fillAdminSettings(settings) {
  for (const [key, [id, divisor]] of Object.entries(serverSettingsFields)) $(`#${id}`).value = (settings[key] / divisor).toFixed(divisor === 1024 ** 3 ? 1 : 0);
  const fields = {
    recycleRetentionDays: 'settingRecycleDays', auditRetentionDays: 'settingAuditDays', downloadLogRetentionDays: 'settingDownloadDays',
    backupIntervalHours: 'settingBackupInterval', backupRetentionDays: 'settingBackupRetention', backupLocalPath: 'settingBackupPath',
    backupObjectPrefix: 'settingBackupPrefix', derivativePollIntervalSeconds: 'settingDerivativePoll',
    derivativeProcessTimeoutSeconds: 'settingDerivativeTimeout', thumbnailMaxEdge: 'settingThumbnailEdge',
    sessionLifetimeDays: 'settingSessionDays', loginFailureLimit: 'settingLoginLimit',
    loginFailureWindowMinutes: 'settingLoginWindow', loginBlockMinutes: 'settingLoginBlock'
  };
  for (const [key, id] of Object.entries(fields)) $(`#${id}`).value = settings[key];
  $('#settingWarningRatio').value = Math.round(settings.capacityWarningRatio * 100);
  $('#settingCriticalRatio').value = Math.round(settings.capacityCriticalRatio * 100);
  $('#settingBackupsEnabled').checked = settings.backupsEnabled;
  $('#settingDerivativesEnabled').checked = settings.derivativesEnabled;
  $('#settingMultipartMiB').value = settings.multipartPartSizeBytes / (1024 ** 2);
  $('#settingUploadSessionHours').value = settings.uploadSessionLifetimeHours;
  $('#settingSignedUrlMinutes').value = settings.signedUrlLifetimeMinutes;
}

async function loadAdminSettings() {
  fillAdminSettings(await api('/api/admin/settings'));
}

function collectAdminSettings() {
  const result = {};
  for (const [key, [id, multiplier]] of Object.entries(serverSettingsFields)) result[key] = Math.round(Number($(`#${id}`).value) * multiplier);
  const fields = {
    recycleRetentionDays: 'settingRecycleDays', auditRetentionDays: 'settingAuditDays', downloadLogRetentionDays: 'settingDownloadDays',
    backupIntervalHours: 'settingBackupInterval', backupRetentionDays: 'settingBackupRetention', backupLocalPath: 'settingBackupPath',
    backupObjectPrefix: 'settingBackupPrefix', derivativePollIntervalSeconds: 'settingDerivativePoll',
    derivativeProcessTimeoutSeconds: 'settingDerivativeTimeout', thumbnailMaxEdge: 'settingThumbnailEdge',
    sessionLifetimeDays: 'settingSessionDays', loginFailureLimit: 'settingLoginLimit',
    loginFailureWindowMinutes: 'settingLoginWindow', loginBlockMinutes: 'settingLoginBlock'
  };
  for (const [key, id] of Object.entries(fields)) result[key] = ['backupLocalPath','backupObjectPrefix'].includes(key) ? $(`#${id}`).value.trim() : Number($(`#${id}`).value);
  result.capacityWarningRatio = Number($('#settingWarningRatio').value) / 100;
  result.capacityCriticalRatio = Number($('#settingCriticalRatio').value) / 100;
  result.backupsEnabled = $('#settingBackupsEnabled').checked;
  result.derivativesEnabled = $('#settingDerivativesEnabled').checked;
  result.multipartPartSizeBytes = Number($('#settingMultipartMiB').value) * 1024 ** 2;
  result.uploadSessionLifetimeHours = Number($('#settingUploadSessionHours').value);
  result.signedUrlLifetimeMinutes = Number($('#settingSignedUrlMinutes').value);
  return result;
}

function selectedAdminUser() {
  return state.adminUsers.find(user => user.id === state.selectedAdminUserId) || null;
}

function renderAdminOption(item, name, checked) {
  return `<label class="permission-option"><input type="checkbox" name="${name}" value="${escapeHtml(item.key)}" ${checked ? 'checked' : ''}><span><strong>${escapeHtml(item.label)}</strong><small>${escapeHtml(item.key)}</small></span></label>`;
}

function populateAdminUserDialog(user) {
  state.selectedAdminUserId = user.id;
  $('#adminUserDialogTitle').textContent = user.displayName || user.username;
  $('#adminUserDialogMeta').textContent = `@${user.username} · ${user.isEnabled ? '使用中' : '已停用'}`;
  $('#adminUsername').value = user.username;
  $('#adminIsAdmin').checked = user.isAdmin;
  const enabledPermissions = new Set(user.permissions);
  $('#adminPermissionGrid').innerHTML = adminManagement.permissions
    .map(item => renderAdminOption(item, 'adminPermission', enabledPermissions.has(item.key)))
    .join('');
  $('#adminProfileFieldGrid').innerHTML = adminManagement.profileFields
    .map(item => renderAdminOption(item, 'adminProfileField', false))
    .join('');
  const bindings = adminManagement.bindingLabels(user);
  $('#adminEmailStatus').textContent = bindings.email;
  $('#adminAvatarStatus').textContent = bindings.avatar;
  $('#clearAdminEmail').disabled = !user.hasEmail;
  $('#clearAdminAvatar').disabled = !user.hasAvatar;
}

function openAdminUserDialog(userId) {
  const user = state.adminUsers.find(item => item.id === userId);
  if (!user) return;
  populateAdminUserDialog(user);
  if (!$('#adminUserDialog').open) $('#adminUserDialog').showModal();
}

async function refreshAdminUser(userId) {
  if (userId === state.me.id) {
    state.me = await api('/api/me');
    setIdentity();
    if (!state.me.isAdmin) {
      $('#adminUserDialog').close();
      await showView('overview');
      return;
    }
  }
  await loadAdmin();
  if ($('#adminUserDialog').open) {
    const user = state.adminUsers.find(item => item.id === userId);
    if (user) populateAdminUserDialog(user);
  }
}

async function hashSmallFile(file) {
  if (file.size > 64 * 1024 * 1024) {
    $('#assetHashStatus').textContent = '文件较大，请使用本地工具计算并填写 SHA-256。';
    return;
  }
  $('#assetHashStatus').textContent = '正在计算 SHA-256…';
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
  $('#assetContentHash').value = [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('').toUpperCase();
  $('#assetHashStatus').textContent = `${file.name} · ${formatBytes(file.size)}`;
}

function inferCategory(fileName) {
  const extension = fileName.split('.').pop()?.toLowerCase();
  if (['jpg','jpeg','png','gif','webp','bmp','tiff','tif','svg','ai','eps','psd'].includes(extension)) return 'image';
  if (['mp4','mkv','mov','avi','wmv','flv','webm'].includes(extension)) return 'video';
  return '';
}

async function prepareAvatar(file) {
  await AvatarValidation.validateAvatarFile(file);
  const url = URL.createObjectURL(file);
  try {
    const image = new Image();
    image.src = url;
    await image.decode();
    const side = Math.min(image.naturalWidth, image.naturalHeight);
    const sx = (image.naturalWidth - side) / 2;
    const sy = (image.naturalHeight - side) / 2;
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 512;
    canvas.getContext('2d').drawImage(image, sx, sy, side, side, 0, 0, 512, 512);
    const blob = await new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('无法处理头像。')), 'image/webp', .9));
    if (blob.type !== 'image/webp') throw new Error('当前浏览器无法生成 WebP 头像。');
    return blob;
  } finally { URL.revokeObjectURL(url); }
}

$('#loginForm').addEventListener('submit', async event => {
  event.preventDefault();
  $('#loginError').textContent = '';
  try {
    const result = await api('/api/auth/login', { method: 'POST', body: { identifier: $('#loginIdentifier').value, password: $('#loginPassword').value } });
    state.token = result.token;
    sessionStorage.setItem('assetLibraryToken', state.token);
    state.me = result.user;
    await loadMe(Boolean(result.user.mustChangePassword));
  } catch (error) { $('#loginError').textContent = error.message; }
});
$('#logoutButton').addEventListener('click', () => signOut());
$('#mainNav').addEventListener('click', event => { const button = event.target.closest('[data-view]'); if (button) showView(button.dataset.view); });
document.addEventListener('click', event => { const button = event.target.closest('[data-go]'); if (button) showView(button.dataset.go); });
$('#refreshButton').addEventListener('click', () => showView(state.view));

$('#adminTabs').addEventListener('click', event => {
  const button = event.target.closest('[data-admin-tab]');
  if (button) showAdminTab(button.dataset.adminTab);
});
$('#adminAssetStateTabs').addEventListener('click', event => {
  const button = event.target.closest('[data-state]'); if (!button) return;
  state.adminAssetState = button.dataset.state;
  $$('#adminAssetStateTabs button').forEach(item => item.classList.toggle('active', item === button));
  loadAdminAssets().catch(error => notify(error.message, 'error'));
});
$('#adminLutStateTabs').addEventListener('click', event => {
  const button = event.target.closest('[data-state]'); if (!button) return;
  state.adminLutState = button.dataset.state;
  $$('#adminLutStateTabs button').forEach(item => item.classList.toggle('active', item === button));
  loadAdminLuts().catch(error => notify(error.message, 'error'));
});
$('#adminAssetRows').addEventListener('click', async event => {
  const button = event.target.closest('[data-admin-asset-action]'); if (!button) return;
  const id = button.dataset.id;
  try {
    if (button.dataset.adminAssetAction === 'recycle') {
      if (!confirm('将该素材移入回收站？')) return;
      await api(`/api/assets/${id}`, { method: 'DELETE' });
    } else if (button.dataset.adminAssetAction === 'restore') {
      await api(`/api/assets/${id}/restore`, { method: 'POST' });
    } else {
      if (!confirm('永久删除该素材及其对象，无法恢复。确定继续？')) return;
      await api(`/api/assets/${id}/permanent`, { method: 'DELETE' });
    }
    notify('素材状态已更新'); await loadAdminAssets();
  } catch (error) { notify(error.message, 'error'); }
});
$('#adminLutRows').addEventListener('click', async event => {
  const button = event.target.closest('[data-admin-lut-action]'); if (!button) return;
  const id = button.dataset.id;
  try {
    if (button.dataset.adminLutAction === 'recycle') {
      if (!confirm('将该 LUT 移入回收站？')) return;
      await api(`/api/luts/${id}`, { method: 'DELETE' });
    } else if (button.dataset.adminLutAction === 'restore') {
      await api(`/api/luts/${id}/restore`, { method: 'POST' });
    } else {
      if (!confirm('永久删除该 LUT，无法恢复。确定继续？')) return;
      await api(`/api/luts/${id}/permanent`, { method: 'DELETE' });
    }
    notify('LUT 状态已更新'); await loadAdminLuts();
  } catch (error) { notify(error.message, 'error'); }
});
$('#adminTagCreateForm').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    await api('/api/tags', { method: 'POST', body: { name: $('#adminTagName').value.trim() } });
    $('#adminTagName').value = ''; notify('标签已创建'); await loadAdminTags();
  } catch (error) { notify(error.message, 'error'); }
});
$('#adminTagRows').addEventListener('click', async event => {
  const button = event.target.closest('[data-admin-tag-action]'); if (!button) return;
  try {
    if (button.dataset.adminTagAction === 'rename') {
      const name = prompt(`将标签“${button.dataset.name}”重命名为：`, button.dataset.name)?.trim();
      if (!name || name === button.dataset.name) return;
      await api(`/api/tags/${button.dataset.id}`, { method: 'PUT', body: { name } });
    } else {
      if (!confirm(`删除标签“${button.dataset.name}”？素材上的该标签也会被移除。`)) return;
      await api(`/api/tags/${button.dataset.id}`, { method: 'DELETE' });
    }
    notify('标签已更新'); await loadAdminTags();
  } catch (error) { notify(error.message, 'error'); }
});
$('#reloadAdminSettings').addEventListener('click', () => loadAdminSettings().catch(error => notify(error.message, 'error')));
$('#adminSettingsForm').addEventListener('submit', async event => {
  event.preventDefault();
  const payload = collectAdminSettings();
  if (!confirm('保存并立即应用这些服务端业务设置？备份、保留周期、配额与登录策略都会更新。')) return;
  try {
    fillAdminSettings(await api('/api/admin/settings', { method: 'PUT', body: payload }));
    notify('服务端设置已保存并生效');
  } catch (error) { notify(error.message, 'error'); }
});

$('#assetStateTabs').addEventListener('click', event => {
  const button = event.target.closest('[data-state]'); if (!button) return;
  state.assetState = button.dataset.state;
  $$('#assetStateTabs button').forEach(item => item.classList.toggle('active', item === button));
  loadAssets().catch(error => notify(error.message, 'error'));
});
['assetSearch','assetCategory','assetSort'].forEach(id => $(`#${id}`).addEventListener(id === 'assetSearch' ? 'input' : 'change', () => {
  clearTimeout(state.debounce); state.debounce = setTimeout(() => loadAssets().catch(error => notify(error.message, 'error')), 220);
}));
$('#userSearch').addEventListener('input', () => { clearTimeout(state.debounce); state.debounce = setTimeout(() => loadUsers().catch(error => notify(error.message, 'error')), 220); });
$('#userGrid').addEventListener('click', event => { const card = event.target.closest('[data-user-id]'); if (card) openUserProfile(card.dataset.userId).catch(error => notify(error.message, 'error')); });

$('#assetRows').addEventListener('click', async event => {
  const button = event.target.closest('[data-asset-action]'); if (!button) return;
  try {
    if (button.dataset.assetAction === 'recycle') {
      if (!confirm('将此素材移入回收站？')) return;
      await api(`/api/assets/${button.dataset.id}`, { method: 'DELETE' });
    } else if (button.dataset.assetAction === 'restore') {
      await api(`/api/assets/${button.dataset.id}/restore`, { method: 'POST' });
    } else {
      if (!confirm('永久删除后无法恢复，确定继续？')) return;
      await api(`/api/assets/${button.dataset.id}/permanent`, { method: 'DELETE' });
    }
    notify('素材状态已更新'); await loadAssets();
  } catch (error) { notify(error.message, 'error'); }
});

$('#openAssetDialog').addEventListener('click', () => { $('#assetForm').reset(); $('#assetHashStatus').textContent = ''; $('#assetDialog').showModal(); });
$('#assetFile').addEventListener('change', async () => {
  const file = $('#assetFile').files[0]; if (!file) return;
  $('#assetName').value = file.name.replace(/\.[^.]+$/, '');
  $('#assetCategoryInput').value = inferCategory(file.name);
  await hashSmallFile(file);
});
const dropZone = $('#assetDropZone');
['dragenter','dragover'].forEach(name => dropZone.addEventListener(name, event => { event.preventDefault(); dropZone.classList.add('dragging'); }));
['dragleave','drop'].forEach(name => dropZone.addEventListener(name, event => { event.preventDefault(); dropZone.classList.remove('dragging'); }));
dropZone.addEventListener('drop', async event => {
  const file = event.dataTransfer.files[0]; if (!file) return;
  const transfer = new DataTransfer(); transfer.items.add(file); $('#assetFile').files = transfer.files;
  $('#assetName').value = file.name.replace(/\.[^.]+$/, ''); $('#assetCategoryInput').value = inferCategory(file.name); await hashSmallFile(file);
});
$('#assetForm').addEventListener('submit', async event => {
  event.preventDefault();
  const file = $('#assetFile').files[0]; if (!file) return;
  try {
    await api('/api/assets', { method: 'POST', body: {
      name: $('#assetName').value, category: $('#assetCategoryInput').value, originalFileName: file.name,
      sizeBytes: file.size, contentHash: $('#assetContentHash').value, notes: $('#assetNotes').value,
      tags: $('#assetTags').value.split(/[,，]/).map(value => value.trim()).filter(Boolean)
    }});
    $('#assetDialog').close(); notify('素材元数据已登记'); await loadAssets();
  } catch (error) { notify(error.message, 'error'); }
});

$('#profileForm').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    state.me = await api('/api/me/profile', { method: 'PUT', body: {
      displayName: $('#profileDisplayName').value, bio: $('#profileBio').value, birthday: $('#profileBirthday').value || null,
      gender: $('#profileGender').value || null, customGender: $('#profileCustomGender').value, contact: $('#profileContact').value,
      birthdayVisibility: $('#birthdayVisibility').value, genderVisibility: $('#genderVisibility').value, contactVisibility: $('#contactVisibility').value
    }});
    setIdentity(); notify('个人资料已保存'); await loadProfile();
  } catch (error) { notify(error.message, 'error'); }
});
$('#emailForm').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    state.me = await api('/api/me/email', { method: 'PUT', body: { email: $('#profileEmail').value, currentPassword: $('#emailPassword').value } });
    $('#emailPassword').value = ''; notify('登录邮箱已绑定');
  } catch (error) { notify(error.message, 'error'); }
});
$('#avatarButton').addEventListener('click', () => $('#avatarInput').click());
$('#avatarInput').addEventListener('change', async () => {
  try {
    const blob = await prepareAvatar($('#avatarInput').files[0]);
    state.me = await api('/api/me/avatar', { method: 'POST', headers: { 'Content-Type': 'image/webp' }, body: blob });
    notify('头像已更新'); await loadProfile();
  } catch (error) { notify(error.message, 'error'); }
  finally { $('#avatarInput').value = ''; }
});
$('#removeAvatarButton').addEventListener('click', async () => {
  if (!confirm('确定移除当前头像吗？移除后将显示默认头像，以后仍可重新上传。')) return;
  try { state.me = await api('/api/me/avatar', { method: 'DELETE' }); notify('头像已移除'); await loadProfile(); }
  catch (error) { notify(error.message, 'error'); }
});

$('#openUserDialog').addEventListener('click', () => { $('#userForm').reset(); $('#userDialog').showModal(); });
$('#userForm').addEventListener('submit', async event => {
  event.preventDefault();
  try {
    const role = $('#newIsAdmin').checked ? '管理员' : '团队成员';
    if (!confirm(`将创建一个新${role}账号，确定继续？`)) return;
    await api('/api/admin/users', { method: 'POST', body: {
      username: $('#newUsername').value, temporaryPassword: $('#newTemporaryPassword').value,
      displayName: $('#newDisplayName').value, isAdmin: $('#newIsAdmin').checked, permissions: null
    }});
    $('#userDialog').close(); notify('账号已创建'); await loadAdmin();
  } catch (error) { notify(error.message, 'error'); }
});
$('#adminUserRows').addEventListener('click', async event => {
  const button = event.target.closest('[data-admin-action]'); if (!button) return;
  try {
    if (button.dataset.adminAction === 'manage') {
      openAdminUserDialog(button.dataset.id);
      return;
    } else if (button.dataset.adminAction === 'toggle') {
      const enabled = button.dataset.enabled === 'true';
      const message = enabled
        ? '停用账号会立即撤销其所有会话，确定继续？'
        : '恢复后该账号可立即登录，确定继续？';
      if (!confirm(message)) return;
      await api(`/api/admin/users/${button.dataset.id}/status`, { method: 'PUT', body: { isEnabled: !enabled } });
    } else if (button.dataset.adminAction === 'reset') {
      const temporaryPassword = prompt('输入新的初始密码（8 至 128 位，且至少包含大写字母、小写字母、数字、特殊符号中的两种）');
      if (!temporaryPassword) return;
      if (!confirm('重置后会立即撤销该账号的所有会话，并在下次登录时提示用户建议修改初始密码。确定继续？')) return;
      await api(`/api/admin/users/${button.dataset.id}/reset-password`, { method: 'POST', body: { temporaryPassword } });
    } else if (button.dataset.adminAction === 'delete') {
      if (!confirm(`将永久删除账号 @${button.dataset.username}。账号会从用户列表消失，历史素材与标记只保留署名，且无法恢复。确定继续？`)) return;
      await api(`/api/admin/users/${button.dataset.id}`, { method: 'DELETE' });
    }
    notify('账号已更新'); await loadAdmin();
  } catch (error) { notify(error.message, 'error'); }
});

$('#adminUsernameForm').addEventListener('submit', async event => {
  event.preventDefault();
  const user = selectedAdminUser();
  if (!user) return;
  const username = $('#adminUsername').value.trim();
  if (username === user.username) { notify('用户名未变更'); return; }
  if (!confirm(`用户名是主要登录名，将从 @${user.username} 修正为 @${username}，修改后立即生效。确定继续？`)) return;
  try {
    await api(`/api/admin/users/${user.id}/username`, { method: 'PUT', body: { username } });
    notify('登录用户名已更新');
    await refreshAdminUser(user.id);
  } catch (error) { notify(error.message, 'error'); }
});

$('#adminPermissionsForm').addEventListener('submit', async event => {
  event.preventDefault();
  const user = selectedAdminUser();
  if (!user) return;
  const selected = $$('#adminPermissionGrid input:checked').map(input => input.value);
  const body = adminManagement.createPermissionsPayload($('#adminIsAdmin').checked, selected);
  const currentPermissions = [...user.permissions].sort().join('|');
  const nextPermissions = [...body.permissions].sort().join('|');
  if (body.isAdmin === user.isAdmin && currentPermissions === nextPermissions) {
    notify('角色和权限未变更');
    return;
  }
  const roleText = body.isAdmin ? '授予管理员角色' : '保存成员权限';
  if (!confirm(`${roleText}会立即改变该账号可执行的操作，确定继续？`)) return;
  try {
    await api(`/api/admin/users/${user.id}/permissions`, { method: 'PUT', body });
    notify('角色与权限已更新');
    await refreshAdminUser(user.id);
  } catch (error) { notify(error.message, 'error'); }
});

$('#clearAdminEmail').addEventListener('click', async () => {
  const user = selectedAdminUser();
  if (!user?.hasEmail || !confirm('只会移除该账号的附属登录邮箱，不会显示原邮箱地址。确定清除？')) return;
  try {
    await api(`/api/admin/users/${user.id}/email`, { method: 'DELETE' });
    notify('附属登录邮箱已清除');
    await refreshAdminUser(user.id);
  } catch (error) { notify(error.message, 'error'); }
});

$('#clearAdminAvatar').addEventListener('click', async () => {
  const user = selectedAdminUser();
  if (!user?.hasAvatar || !confirm('清除后该用户需要重新上传头像。确定继续？')) return;
  try {
    await api(`/api/admin/users/${user.id}/avatar`, { method: 'DELETE' });
    notify('头像已清除');
    await refreshAdminUser(user.id);
  } catch (error) { notify(error.message, 'error'); }
});

$('#adminProfileClearForm').addEventListener('submit', async event => {
  event.preventDefault();
  const user = selectedAdminUser();
  if (!user) return;
  const selected = $$('#adminProfileFieldGrid input:checked').map(input => input.value);
  if (!selected.length) { notify('请先选择要清理的公开资料字段', 'error'); return; }
  const body = adminManagement.createProfileClearPayload(selected);
  const labels = adminManagement.profileLabels(body.fields).join('、');
  if (!confirm(`将清理 ${labels}。私人资料不会被修改，确定继续？`)) return;
  try {
    const result = await api(`/api/admin/users/${user.id}/public-profile/clear`, { method: 'POST', body });
    const clearedLabels = adminManagement.profileLabels(result.clearedFields);
    notify(clearedLabels.length ? `已清理：${clearedLabels.join('、')}` : '所选字段没有可清理的公开内容');
    await refreshAdminUser(user.id);
  } catch (error) { notify(error.message, 'error'); }
});

$('#adminUserDialog').addEventListener('close', () => { state.selectedAdminUserId = null; });

$$('.close-dialog').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));

(async function initialize() {
  if (!state.token) return;
  try { await loadMe(); }
  catch (error) {
    signOut(false);
    if (error?.message) $('#loginError').textContent = error.message;
  }
})();
