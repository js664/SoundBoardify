const http = require('node:http');
const crypto = require('node:crypto');
const { net } = require('electron');

const MAX_AUDIO_BYTES = 25 * 1024 * 1024;

function allowedAudioUrl(value) {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.hostname === 'www.myinstants.com' && url.port === '' &&
      url.pathname.startsWith('/media/sounds/') && url.pathname.toLowerCase().endsWith('.mp3');
  } catch { return false; }
}

function createMarketplaceBridge() {
  const token = crypto.randomBytes(32).toString('hex');
  const server = http.createServer(async (request, response) => {
    if (request.method !== 'POST' || request.url !== '/download' || request.headers.authorization !== `Bearer ${token}`) {
      response.writeHead(404).end();
      return;
    }
    let body = '';
    for await (const chunk of request) {
      body += chunk;
      if (body.length > 2048) { response.writeHead(413).end(); return; }
    }
    let audioUrl;
    try { audioUrl = JSON.parse(body).audioUrl; } catch {}
    if (!allowedAudioUrl(audioUrl)) { response.writeHead(400).end('Invalid MyInstants audio URL.'); return; }
    try {
      const result = await net.fetch(audioUrl, { headers: { Referer: 'https://www.myinstants.com/' } });
      if (!result.ok) { response.writeHead(502).end(`MyInstants returned HTTP ${result.status}.`); return; }
      if (Number(result.headers.get('content-length')) > MAX_AUDIO_BYTES) { response.writeHead(413).end('Sound is too large.'); return; }
      const reader = result.body?.getReader();
      if (!reader) { response.writeHead(502).end('MyInstants returned an empty sound.'); return; }
      const chunks = [];
      let size = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        size += value.byteLength;
        if (size > MAX_AUDIO_BYTES) { await reader.cancel(); response.writeHead(413).end('Sound is too large.'); return; }
        chunks.push(Buffer.from(value));
      }
      const data = Buffer.concat(chunks, size);
      if (!data.length) { response.writeHead(502).end('MyInstants returned an empty sound.'); return; }
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': data.length, 'Cache-Control': 'no-store' }).end(data);
    } catch (error) {
      response.writeHead(502).end(error?.message || 'The browser could not download this sound.');
    }
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      server.removeListener('error', reject);
      resolve({ server, token, port: server.address().port });
    });
  });
}

module.exports = { allowedAudioUrl, createMarketplaceBridge };
