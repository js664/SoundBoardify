function buildSoundboardUrl(origin) {
  let target;
  try { target = new URL(origin); }
  catch { throw new Error('The local soundboard address is invalid.'); }

  const port = Number(target.port);
  if (target.protocol !== 'http:' || target.hostname !== '127.0.0.1' || !Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error('The soundboard link must point to SimplySound on this PC.');
  }
  return `${target.origin}/`;
}

module.exports = { buildSoundboardUrl };
