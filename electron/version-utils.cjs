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

function selectNewestRelease(releases) {
  if (!Array.isArray(releases)) throw new TypeError('GitHub release list must be an array.');
  let newest = null;
  for (const release of releases) {
    if (!release || release.draft === true || typeof release.tag_name !== 'string' || !isSimplySoundReleaseUrl(release.html_url)) continue;
    try { versionParts(release.tag_name); }
    catch { continue; }
    if (!newest || isNewerVersion(newest.tag_name, release.tag_name)) newest = release;
  }
  return newest;
}

function isSimplySoundReleaseUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'github.com' && /^\/js664\/SimplySound\/releases\/(?:tag|latest)\/?[^/]*$/i.test(url.pathname);
  } catch {
    return false;
  }
}

function isSimplySoundReleaseAssetUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'github.com' && /^\/js664\/SimplySound\/releases\/download\/v?\d+\.\d+\.\d+\/SimplySound-[^/]+\.exe$/i.test(url.pathname);
  } catch {
    return false;
  }
}

module.exports = { isNewerVersion, isSimplySoundReleaseAssetUrl, isSimplySoundReleaseUrl, selectNewestRelease };
