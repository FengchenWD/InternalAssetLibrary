'use strict';

(function initialize(factory) {
  const management = factory();
  if (typeof module === 'object' && module.exports) module.exports = management;
  if (typeof window !== 'undefined') window.AdminUserManagement = management;
})(function createAdminUserManagement() {
  const permissions = Object.freeze([
    { key: 'assets.browse', label: '浏览素材库' },
    { key: 'assets.preview', label: '预览素材' },
    { key: 'assets.download', label: '下载素材' },
    { key: 'assets.upload', label: '上传素材' },
    { key: 'assets.tags', label: '维护素材标签' },
    { key: 'markers.maintain', label: '维护标记' },
    { key: 'assets.edit-own', label: '编辑自己的素材' },
    { key: 'assets.delete-own', label: '删除自己的素材' },
    { key: 'assets.category.bgm', label: '访问 BGM' },
    { key: 'assets.category.sound-effect', label: '访问音效' },
    { key: 'assets.category.image', label: '访问图片' },
    { key: 'assets.category.video', label: '访问视频' }
  ].map(Object.freeze));

  const profileFields = Object.freeze([
    { key: 'displayName', label: '显示名称' },
    { key: 'bio', label: '个人简介' },
    { key: 'birthday', label: '团队可见的生日' },
    { key: 'gender', label: '团队可见的性别' },
    { key: 'contact', label: '团队可见的联系方式' }
  ].map(Object.freeze));

  function normalizeSelection(definitions, values, kind) {
    if (!Array.isArray(values)) throw new TypeError(`${kind}必须是数组。`);
    const requested = new Set(values.map(value => String(value).trim()));
    const known = new Set(definitions.map(item => item.key));
    const unknown = [...requested].find(value => !known.has(value));
    if (unknown !== undefined) throw new RangeError(`未知${kind}：${unknown}`);
    return definitions.filter(item => requested.has(item.key)).map(item => item.key);
  }

  function createPermissionsPayload(isAdmin, selectedPermissions) {
    return {
      isAdmin: Boolean(isAdmin),
      permissions: normalizeSelection(permissions, selectedPermissions, '权限')
    };
  }

  function createProfileClearPayload(selectedFields) {
    return { fields: normalizeSelection(profileFields, selectedFields, '资料字段') };
  }

  function profileLabels(fields) {
    const normalized = normalizeSelection(profileFields, fields, '资料字段');
    return normalized.map(key => profileFields.find(item => item.key === key).label);
  }

  function bindingLabels(user) {
    return {
      email: user?.hasEmail ? '已绑定' : '未绑定',
      avatar: user?.hasAvatar ? '已设置' : '未设置'
    };
  }

  return Object.freeze({
    permissions,
    profileFields,
    createPermissionsPayload,
    createProfileClearPayload,
    profileLabels,
    bindingLabels
  });
});
