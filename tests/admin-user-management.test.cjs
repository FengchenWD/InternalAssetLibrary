'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const management = require('../src/InternalAssetLibrary.Server/wwwroot/admin-user-management.js');

const expectedPermissions = [
  'assets.browse',
  'assets.preview',
  'assets.download',
  'assets.upload',
  'assets.tags',
  'markers.maintain',
  'assets.edit-own',
  'assets.delete-own',
  'assets.category.bgm',
  'assets.category.sound-effect',
  'assets.category.image',
  'assets.category.video'
];
const expectedProfileFields = ['displayName', 'bio', 'birthday', 'gender', 'contact'];

test('exposes the exact twelve server permission keys', () => {
  assert.deepEqual(management.permissions.map(item => item.key), expectedPermissions);
  assert.equal(new Set(management.permissions.map(item => item.key)).size, 12);
});

test('builds a canonical role and permission request', () => {
  const payload = management.createPermissionsPayload(true, [
    'assets.category.video',
    'assets.browse',
    'assets.category.video'
  ]);

  assert.deepEqual(payload, {
    isAdmin: true,
    permissions: ['assets.browse', 'assets.category.video']
  });
  assert.throws(
    () => management.createPermissionsPayload(false, ['users.impersonate']),
    /users\.impersonate/);
});

test('limits public-profile clearing to the five moderation fields', () => {
  assert.deepEqual(management.profileFields.map(item => item.key), expectedProfileFields);
  assert.deepEqual(
    management.createProfileClearPayload(['contact', 'displayName', 'contact']),
    { fields: ['displayName', 'contact'] });
  assert.throws(() => management.createProfileClearPayload(['email']), /email/);
  assert.throws(() => management.createProfileClearPayload('bio'), /\u6570\u7ec4/);
});

test('binding labels never return an email address or another profile value', () => {
  const labels = management.bindingLabels({
    hasEmail: true,
    hasAvatar: false,
    email: 'private@example.invalid',
    contact: 'private contact'
  });

  assert.deepEqual(labels, { email: '已绑定', avatar: '未设置' });
  assert.doesNotMatch(JSON.stringify(labels), /private@example|private contact/);
});

test('admin page wires every existing account-management endpoint', () => {
  const root = path.resolve(__dirname, '..');
  const html = fs.readFileSync(path.join(root, 'src/InternalAssetLibrary.Server/wwwroot/index.html'), 'utf8');
  const app = fs.readFileSync(path.join(root, 'src/InternalAssetLibrary.Server/wwwroot/app.js'), 'utf8');

  const ids = [...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length, 'HTML ids must remain unique.');
  for (const id of [
    'adminUserDialog',
    'adminUsernameForm',
    'adminPermissionsForm',
    'adminPermissionGrid',
    'clearAdminEmail',
    'clearAdminAvatar',
    'adminProfileClearForm',
    'adminProfileFieldGrid'
  ]) assert.ok(ids.includes(id), `Missing admin control #${id}.`);

  assert.ok(
    html.indexOf('src="/admin-user-management.js"') < html.indexOf('src="/app.js"'),
    'The management contract must load before the application script.');
  for (const suffix of ['username', 'permissions', 'email', 'avatar', 'public-profile/clear']) {
    assert.ok(
      app.includes(`/api/admin/users/\${user.id}/${suffix}`),
      `Missing admin endpoint wiring for ${suffix}.`);
  }
  const usernameHandler = app.slice(
    app.indexOf("$('#adminUsernameForm').addEventListener"),
    app.indexOf("$('#adminPermissionsForm').addEventListener"));
  assert.match(usernameHandler, /if \(!confirm\(/);
  assert.match(usernameHandler, /用户名是主要登录名/);
  assert.match(usernameHandler, /修改后立即生效/);
  assert.ok(
    usernameHandler.indexOf('confirm(') < usernameHandler.indexOf('/username'),
    'Username confirmation must run before the update API.');
  assert.doesNotMatch(app.slice(app.indexOf('async function loadAdmin'), app.indexOf('async function hashSmallFile')), /user\.email\b/);
});
