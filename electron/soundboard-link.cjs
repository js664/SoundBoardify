function createSoundboardUrl(port) {
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('The Web UI port is not ready yet.');
  return `http://127.0.0.1:${port}/`;
}

function isSoundboardWindowOpenRequest(requestedUrl, port) {
  try { return requestedUrl === createSoundboardUrl(port); }
  catch { return false; }
}

async function openSoundboardInBrowser({ sender, mainWindowWebContents, port, openExternal }) {
  if (!mainWindowWebContents || sender !== mainWindowWebContents) throw new Error('The soundboard can only be opened from the SimplySound desktop window.');
  if (typeof openExternal !== 'function') throw new Error('Windows could not open the soundboard.');
  const url = createSoundboardUrl(port);
  await openExternal(url);
  return { url };
}

module.exports = { createSoundboardUrl, isSoundboardWindowOpenRequest, openSoundboardInBrowser };
