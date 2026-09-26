const test = require('node:test');
const assert = require('node:assert/strict');
const { createSoundboardUrl, openSoundboardInBrowser } = require('./soundboard-link.cjs');

test('builds the local soundboard root URL for the active port', () => {
  assert.equal(createSoundboardUrl(6769), 'http://127.0.0.1:6769/');
  assert.equal(createSoundboardUrl(6669), 'http://127.0.0.1:6669/');
});

test('rejects ports that are not valid for the local soundboard URL', () => {
  for (const port of [0, 80, 65536, 6769.5, '6769', NaN]) {
    assert.throws(() => createSoundboardUrl(port), /Web UI port is not ready/);
  }
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
