'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const validation = require('../src/InternalAssetLibrary.Server/wwwroot/avatar-validation.js');

function fakeFile(bytes, type) {
  return {
    size: bytes.length,
    type,
    async arrayBuffer() {
      return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    }
  };
}

function pngChunk(type, payload = []) {
  const chunk = Buffer.alloc(12 + payload.length);
  chunk.writeUInt32BE(payload.length, 0);
  chunk.write(type, 4, 4, 'ascii');
  Buffer.from(payload).copy(chunk, 8);
  return chunk;
}

function png(...extraChunks) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  return Buffer.concat([
    signature,
    pngChunk('IHDR', new Array(13).fill(0)),
    ...extraChunks,
    pngChunk('IDAT', [0]),
    pngChunk('IEND')
  ]);
}

function webpChunk(type, payload) {
  const chunk = Buffer.alloc(8 + payload.length + (payload.length & 1));
  chunk.write(type, 0, 4, 'ascii');
  chunk.writeUInt32LE(payload.length, 4);
  Buffer.from(payload).copy(chunk, 8);
  return chunk;
}

function webp(...chunks) {
  const body = Buffer.concat([Buffer.from('WEBP'), ...chunks]);
  const header = Buffer.alloc(8);
  header.write('RIFF', 0, 4, 'ascii');
  header.writeUInt32LE(body.length, 4);
  return Buffer.concat([header, body]);
}

test('accepts matching static JPEG, PNG, and WebP sources', async () => {
  const jpeg = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0xff, 0xd9]);
  const staticWebp = webp(webpChunk('VP8L', [0x2f, 0, 0, 0, 0]));

  assert.equal((await validation.validateAvatarFile(fakeFile(jpeg, 'image/jpeg'))).format, 'jpeg');
  assert.equal((await validation.validateAvatarFile(fakeFile(png(), 'image/png'))).format, 'png');
  assert.equal((await validation.validateAvatarFile(fakeFile(staticWebp, 'image/webp'))).format, 'webp');
});

test('rejects GIF and bytes disguised with an allowed MIME', async () => {
  const gif = Buffer.from('GIF89a');
  await assert.rejects(validation.validateAvatarFile(fakeFile(gif, 'image/png')), /GIF/);
  await assert.rejects(
    validation.validateAvatarFile(fakeFile(Buffer.from('not an image'), 'image/jpeg')),
    /只支持静态/);
});

test('rejects APNG and animated WebP', async () => {
  const apng = png(pngChunk('acTL', new Array(8).fill(0)));
  const extendedHeader = new Array(10).fill(0);
  extendedHeader[0] = 0x02;
  const animatedWebp = webp(
    webpChunk('VP8X', extendedHeader),
    webpChunk('ANIM', new Array(6).fill(0)),
    webpChunk('VP8L', [0x2f, 0, 0, 0, 0]));

  await assert.rejects(validation.validateAvatarFile(fakeFile(apng, 'image/png')), /APNG/);
  await assert.rejects(validation.validateAvatarFile(fakeFile(animatedWebp, 'image/webp')), /动画 WebP/);
});

test('rejects a MIME declaration that disagrees with the actual static format', async () => {
  const jpeg = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0xff, 0xd9]);
  await assert.rejects(validation.validateAvatarFile(fakeFile(jpeg, 'image/png')), /MIME/);
  await assert.rejects(validation.validateAvatarFile(fakeFile(jpeg, '')), /MIME/);
});
