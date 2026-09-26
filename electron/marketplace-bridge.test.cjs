const test = require('node:test');
const assert = require('node:assert/strict');
const { allowedAudioUrl } = require('./marketplace-bridge.cjs');

test('marketplace bridge accepts only HTTPS MP3 media URLs on MyInstants', () => {
  assert.equal(allowedAudioUrl('https://www.myinstants.com/media/sounds/example.mp3'), true);
  assert.equal(allowedAudioUrl('http://www.myinstants.com/media/sounds/example.mp3'), false);
  assert.equal(allowedAudioUrl('https://www.myinstants.com.attacker.invalid/media/sounds/example.mp3'), false);
  assert.equal(allowedAudioUrl('https://www.myinstants.com/media/sounds/../private.mp3'), false);
  assert.equal(allowedAudioUrl('https://example.com/media/sounds/example.mp3'), false);
});
