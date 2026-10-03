'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(
  __dirname,
  '..',
  'src',
  'InternalAssetLibrary.Server',
  'wwwroot',
  'app.js'), 'utf8');

test('web SignalR starts a 15-second ping only after its handshake', () => {
  const handshake = source.slice(
    source.indexOf('function completeRealtimeHandshake'),
    source.indexOf('async function connectRealtime'));

  assert.match(handshake, /setInterval\(\(\) => \{/);
  assert.match(handshake, /JSON\.stringify\(\{ type: 6 \}\)/);
  assert.match(handshake, /\}, 15000\)/);
  assert.match(source, /socket\.assetLibraryHandshakePending = true/);
  const frameHandler = source.slice(
    source.indexOf('function handleRealtimeFrame'),
    source.indexOf('async function connectRealtime'));
  assert.ok(
    frameHandler.indexOf('completeRealtimeHandshake(socket)') >
      frameHandler.indexOf('if (socket.assetLibraryHandshakePending)'),
    'The ping must start after the SignalR handshake response.');
});

test('web SignalR clears ping state on close and logout', () => {
  const disconnect = source.slice(
    source.indexOf('function disconnectRealtime'),
    source.indexOf('function scheduleRealtimeReconnect'));
  const closeHandler = source.slice(
    source.indexOf("socket.addEventListener('close'"),
    source.indexOf("socket.addEventListener('error'"));

  assert.match(disconnect, /clearInterval\(state\.realtimePingTimer\)/);
  assert.match(disconnect, /state\.realtimeConnectedOnce = false/);
  assert.match(closeHandler, /if \(state\.realtimeSocket !== socket\) return/);
  assert.match(closeHandler, /clearInterval\(state\.realtimePingTimer\)/);
});

test('web reconnect and current-user notifications compensate missed state', () => {
  const reconnect = source.slice(
    source.indexOf('async function refreshAfterRealtimeReconnect'),
    source.indexOf('function completeRealtimeHandshake'));
  const notificationRefresh = source.slice(
    source.indexOf('function scheduleRealtimeRefresh'),
    source.indexOf('async function refreshAfterRealtimeReconnect'));

  assert.match(reconnect, /state\.me = await api\('\/api\/me'\)/);
  assert.match(reconnect, /setIdentity\(\)/);
  assert.match(reconnect, /await showView\(state\.view\)/);
  assert.match(source, /const isReconnect = state\.realtimeConnectedOnce/);
  assert.match(source, /if \(isReconnect\) refreshAfterRealtimeReconnect\(\)/);
  assert.match(notificationRefresh, /notification\.entityId\.toLowerCase\(\) === state\.me\?\.id\?\.toLowerCase\(\)/);
  assert.match(notificationRefresh, /state\.realtimeRefreshMe = true/);
  assert.match(notificationRefresh, /state\.me = await api\('\/api\/me'\)/);
});
