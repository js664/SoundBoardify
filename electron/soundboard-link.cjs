function isSoundboardWindowTarget(origin, url) {
  try {
    const expected = new URL(origin);
    const target = new URL(url);
    return target.origin === expected.origin && target.pathname === '/' && !target.search && !target.hash && !target.username && !target.password;
  } catch { return false; }
}

module.exports = { isSoundboardWindowTarget };
