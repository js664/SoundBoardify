'use strict';

function versionParts(value) {
  const match = /^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/i.exec(String(value));
  if (!match) throw new Error(`Invalid release version: ${value}`);
  return match.slice(1, 4).map(Number);
}

function isNewerVersion(currentVersion, latestVersion) {
  const current = versionParts(currentVersion);
  const latest = versionParts(latestVersion);
  for (let index = 0; index < current.length; index += 1) {
    if (latest[index] !== current[index]) return latest[index] > current[index];
  }
  return false;
}

function isSoundboardifyReleaseUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'github.com' && /^\/js664\/SoundBoardify\/releases\/(?:tag|latest)\/?[^/]*$/i.test(url.pathname);
  } catch {
    return false;
  }
}

function isSoundboardifyReleaseAssetUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'github.com' && /^\/js664\/SoundBoardify\/releases\/download\/v?\d+\.\d+\.\d+\/Soundboardify-[^/]+\.exe$/i.test(url.pathname);
  } catch {
    return false;
  }
}

module.exports = { isNewerVersion, isSoundboardifyReleaseAssetUrl, isSoundboardifyReleaseUrl };
