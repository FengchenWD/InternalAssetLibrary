(function (root) {
  'use strict';

  const maximumBytes = 10 * 1024 * 1024;
  const pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

  function fail(message) {
    throw new Error(message);
  }

  function startsWith(bytes, expected, offset = 0) {
    return expected.every((value, index) => bytes[offset + index] === value);
  }

  function ascii(bytes, offset, length) {
    return String.fromCharCode(...bytes.subarray(offset, offset + length));
  }

  function uint32BigEndian(bytes, offset) {
    return ((bytes[offset] * 0x1000000) +
      (bytes[offset + 1] << 16) +
      (bytes[offset + 2] << 8) +
      bytes[offset + 3]) >>> 0;
  }

  function uint32LittleEndian(bytes, offset) {
    return (bytes[offset] +
      (bytes[offset + 1] << 8) +
      (bytes[offset + 2] << 16) +
      (bytes[offset + 3] * 0x1000000)) >>> 0;
  }

  function inspectPng(bytes) {
    let offset = pngSignature.length;
    let sawHeader = false;
    let sawImageData = false;
    let sawEnd = false;

    while (offset <= bytes.length - 12) {
      const length = uint32BigEndian(bytes, offset);
      const dataEnd = offset + 8 + length;
      const chunkEnd = dataEnd + 4;
      if (chunkEnd > bytes.length) fail('PNG 图片结构无效。');

      const type = ascii(bytes, offset + 4, 4);
      if (!sawHeader && (type !== 'IHDR' || length !== 13)) fail('PNG 图片结构无效。');
      if (type === 'acTL' || type === 'fcTL' || type === 'fdAT') fail('不支持 APNG 动画头像。');

      sawHeader ||= type === 'IHDR';
      sawImageData ||= type === 'IDAT';
      if (type === 'IEND') {
        if (length !== 0 || chunkEnd !== bytes.length) fail('PNG 图片结构无效。');
        sawEnd = true;
        offset = chunkEnd;
        break;
      }

      offset = chunkEnd;
    }

    if (!sawHeader || !sawImageData || !sawEnd || offset !== bytes.length) fail('PNG 图片结构无效。');
    return { format: 'png', contentType: 'image/png' };
  }

  function inspectWebp(bytes) {
    if (bytes.length < 20 || uint32LittleEndian(bytes, 4) + 8 !== bytes.length) {
      fail('WebP 图片结构无效。');
    }

    let offset = 12;
    let imageChunkCount = 0;
    while (offset <= bytes.length - 8) {
      const type = ascii(bytes, offset, 4);
      const length = uint32LittleEndian(bytes, offset + 4);
      const dataStart = offset + 8;
      const dataEnd = dataStart + length;
      const chunkEnd = dataEnd + (length & 1);
      if (chunkEnd > bytes.length) fail('WebP 图片结构无效。');

      if (type === 'ANIM' || type === 'ANMF' ||
          (type === 'VP8X' && length >= 1 && (bytes[dataStart] & 0x02) !== 0)) {
        fail('不支持动画 WebP 头像。');
      }
      if (type === 'VP8 ' || type === 'VP8L') imageChunkCount += 1;
      offset = chunkEnd;
    }

    if (offset !== bytes.length || imageChunkCount !== 1) fail('WebP 图片结构无效。');
    return { format: 'webp', contentType: 'image/webp' };
  }

  function inspectAvatarBytes(input) {
    const bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
    if (startsWith(bytes, [0x47, 0x49, 0x46, 0x38, 0x37, 0x61]) ||
        startsWith(bytes, [0x47, 0x49, 0x46, 0x38, 0x39, 0x61])) {
      fail('不支持 GIF 动画头像。');
    }
    if (startsWith(bytes, pngSignature)) return inspectPng(bytes);
    if (startsWith(bytes, [0x52, 0x49, 0x46, 0x46]) &&
        startsWith(bytes, [0x57, 0x45, 0x42, 0x50], 8)) return inspectWebp(bytes);
    if (bytes.length >= 4 && startsWith(bytes, [0xff, 0xd8, 0xff]) &&
        bytes[bytes.length - 2] === 0xff && bytes[bytes.length - 1] === 0xd9) {
      return { format: 'jpeg', contentType: 'image/jpeg' };
    }
    fail('头像只支持静态 JPG、PNG 或 WebP 图片。');
  }

  async function validateAvatarFile(file) {
    if (!file || typeof file.arrayBuffer !== 'function' || file.size <= 0) fail('请选择头像图片。');
    if (file.size > maximumBytes) fail('头像不能超过 10 MB。');

    const actual = inspectAvatarBytes(await file.arrayBuffer());
    const claimedContentType = String(file.type || '').trim().toLowerCase();
    if (claimedContentType !== actual.contentType) fail('文件 MIME 类型与实际图片格式不一致。');
    return actual;
  }

  const api = Object.freeze({ maximumBytes, inspectAvatarBytes, validateAvatarFile });
  root.AvatarValidation = api;
  if (typeof module === 'object' && module.exports) module.exports = api;
})(typeof globalThis === 'object' ? globalThis : this);
