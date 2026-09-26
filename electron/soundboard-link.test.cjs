const test = require('node:test');
const assert = require('node:assert/strict');
const { buildSoundboardUrl } = require('./soundboard-link.cjs');

test('opens the local soundboard root on the active service port', () => {
  assert.equal(buildSoundboardUrl('http://127.0.0.1:6769/?desktop=1'), 'http://127.0.0.1:6769/');
});

test('rejects non-local, insecure, and invalid soundboard addresses', () => {
  for (const origin of ['https://127.0.0.1:6769', 'http://localhost:6769', 'http://192.168.1.10:6769', 'http://127.0.0.1:80', 'not a url']) {
    assert.throws(() => buildSoundboardUrl(origin));
  }
});
