'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { isNewerVersion, isSimplySoundReleaseAssetUrl, isSimplySoundReleaseUrl } = require('./version-utils.cjs');

test('compares release versions by numeric segments', () => {
  assert.equal(isNewerVersion('1.9.9', 'v1.10.0'), true);
  assert.equal(isNewerVersion('1.2.3', '1.2.4'), true);
  assert.equal(isNewerVersion('2.0.0', '1.99.99'), false);
  assert.equal(isNewerVersion('1.2.3', 'v1.2.3'), false);
});

test('accepts only HTTPS executable assets from SimplySound releases', () => {
  assert.equal(isSimplySoundReleaseAssetUrl('https://github.com/js664/SimplySound/releases/download/v1.2.0/SimplySound-1.2.0-Setup.exe'), true);
  assert.equal(isSimplySoundReleaseAssetUrl('https://github.com/attacker/repo/releases/download/v1.2.0/payload.exe'), false);
  assert.equal(isSimplySoundReleaseAssetUrl('http://github.com/js664/SimplySound/releases/download/v1.2.0/app.exe'), false);
});

test('rejects release tags that are not semantic versions', () => {
  assert.throws(() => isNewerVersion('1.0.0', 'main'), /Invalid release version/);
});

test('accepts only SimplySound release links on GitHub HTTPS', () => {
  assert.equal(isSimplySoundReleaseUrl('https://github.com/js664/SimplySound/releases/tag/v1.2.0'), true);
  assert.equal(isSimplySoundReleaseUrl('https://github.com/js664/SimplySound/releases/latest'), true);
  assert.equal(isSimplySoundReleaseUrl('https://example.com/js664/SimplySound/releases/tag/v1.2.0'), false);
  assert.equal(isSimplySoundReleaseUrl('http://github.com/js664/SimplySound/releases/tag/v1.2.0'), false);
  assert.equal(isSimplySoundReleaseUrl('https://github.com/other/repo/releases/tag/v1.2.0'), false);
});
