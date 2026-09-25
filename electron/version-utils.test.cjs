'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { isNewerVersion, isSoundboardifyReleaseAssetUrl, isSoundboardifyReleaseUrl } = require('./version-utils.cjs');

test('compares release versions by numeric segments', () => {
  assert.equal(isNewerVersion('1.9.9', 'v1.10.0'), true);
  assert.equal(isNewerVersion('1.2.3', '1.2.4'), true);
  assert.equal(isNewerVersion('2.0.0', '1.99.99'), false);
  assert.equal(isNewerVersion('1.2.3', 'v1.2.3'), false);
});

test('accepts only HTTPS executable assets from Soundboardify releases', () => {
  assert.equal(isSoundboardifyReleaseAssetUrl('https://github.com/js664/SoundBoardify/releases/download/v1.2.0/Soundboardify-1.2.0-Setup.exe'), true);
  assert.equal(isSoundboardifyReleaseAssetUrl('https://github.com/attacker/repo/releases/download/v1.2.0/payload.exe'), false);
  assert.equal(isSoundboardifyReleaseAssetUrl('http://github.com/js664/SoundBoardify/releases/download/v1.2.0/app.exe'), false);
});

test('rejects release tags that are not semantic versions', () => {
  assert.throws(() => isNewerVersion('1.0.0', 'main'), /Invalid release version/);
});

test('accepts only Soundboardify release links on GitHub HTTPS', () => {
  assert.equal(isSoundboardifyReleaseUrl('https://github.com/js664/SoundBoardify/releases/tag/v1.2.0'), true);
  assert.equal(isSoundboardifyReleaseUrl('https://github.com/js664/SoundBoardify/releases/latest'), true);
  assert.equal(isSoundboardifyReleaseUrl('https://example.com/js664/SoundBoardify/releases/tag/v1.2.0'), false);
  assert.equal(isSoundboardifyReleaseUrl('http://github.com/js664/SoundBoardify/releases/tag/v1.2.0'), false);
  assert.equal(isSoundboardifyReleaseUrl('https://github.com/other/repo/releases/tag/v1.2.0'), false);
});
