const test = require('node:test');
const assert = require('node:assert/strict');
const { createSoundboardUrl, isSoundboardWindowOpenRequest, openSoundboardInBrowser } = require('./soundboard-link.cjs');

test('builds the local soundboard root URL for the active port', () => {
  assert.equal(createSoundboardUrl(6769), 'http://127.0.0.1:6769/');
  assert.equal(createSoundboardUrl(6669), 'http://127.0.0.1:6669/');
});

test('rejects ports that are not valid for the local soundboard URL', () => {
  for (const port of [0, 80, 65536, 6769.5, '6769', NaN]) {
    assert.throws(() => createSoundboardUrl(port), /Web UI port is not ready/);
  }
});

test('recognizes only the exact local soundboard link for the active port', () => {
  assert.equal(isSoundboardWindowOpenRequest('http://127.0.0.1:6769/', 6769), true);
  for (const url of [
    'http://127.0.0.1:6769/?desktop=1',
    'http://localhost:6769/',
    'http://127.0.0.1:6669/',
    'https://127.0.0.1:6769/',
    'https://example.com/',
  ]) assert.equal(isSoundboardWindowOpenRequest(url, 6769), false, url);
});

test('opens the active local soundboard for the trusted desktop window', async () => {
  const mainWindowWebContents = {};
  let openedUrl;
  const result = await openSoundboardInBrowser({
    sender: mainWindowWebContents,
    mainWindowWebContents,
    port: 6769,
    openExternal: async url => { openedUrl = url; },
  });
  assert.equal(openedUrl, 'http://127.0.0.1:6769/');
  assert.deepEqual(result, { url: openedUrl });
});

test('opens the configured fallback port rather than assuming the primary port', async () => {
  const mainWindowWebContents = {};
  const result = await openSoundboardInBrowser({
    sender: mainWindowWebContents,
    mainWindowWebContents,
    port: 6669,
    openExternal: async url => { assert.equal(url, 'http://127.0.0.1:6669/'); },
  });
  assert.equal(result.url, 'http://127.0.0.1:6669/');
});

test('reports that the service is not ready when no active port is available', async () => {
  const mainWindowWebContents = {};
  await assert.rejects(openSoundboardInBrowser({
    sender: mainWindowWebContents,
    mainWindowWebContents,
    port: undefined,
    openExternal: async () => {},
  }), /Web UI port is not ready/);
});

test('rejects soundboard launch requests from other renderer windows', async () => {
  await assert.rejects(openSoundboardInBrowser({
    sender: {},
    mainWindowWebContents: {},
    port: 6769,
    openExternal: async () => {},
  }), /can only be opened from the SimplySound desktop window/);
});

test('surfaces a default-browser launch failure', async () => {
  const mainWindowWebContents = {};
  await assert.rejects(openSoundboardInBrowser({
    sender: mainWindowWebContents,
    mainWindowWebContents,
    port: 6769,
    openExternal: async () => { throw new Error('Browser unavailable'); },
  }), /Browser unavailable/);
});
