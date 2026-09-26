const test = require('node:test');
const assert = require('node:assert/strict');
const { isSoundboardWindowTarget } = require('./soundboard-link.cjs');

test('opens only the active local soundboard root from the desktop window', () => {
  const origin = 'http://127.0.0.1:6769';
  assert.equal(isSoundboardWindowTarget(origin, `${origin}/`), true);
  assert.equal(isSoundboardWindowTarget(origin, `${origin}/?desktop=1`), false);
  assert.equal(isSoundboardWindowTarget(origin, 'http://192.168.1.2:6769/'), false);
  assert.equal(isSoundboardWindowTarget(origin, `${origin}/api/status`), false);
  assert.equal(isSoundboardWindowTarget(origin, 'not a url'), false);
});

test('rejects non-local, insecure, and invalid soundboard window targets', () => {
  const origin = 'http://127.0.0.1:6769';
  for (const target of ['https://127.0.0.1:6769/', 'http://localhost:6769/', 'http://192.168.1.10:6769/', 'http://127.0.0.1:80/', `${origin}/api/status`, 'not a url']) {
    assert.equal(isSoundboardWindowTarget(origin, target), false);
  }
});
