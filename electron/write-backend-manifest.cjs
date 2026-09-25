const fs = require('node:fs/promises');
const { createHash } = require('node:crypto');
const { createReadStream } = require('node:fs');
const path = require('node:path');

const stage = path.resolve(__dirname, '..', 'obj', 'electron-backend');
const manifestPath = path.join(stage, 'backend-manifest.json');
const packageJson = require('./package.json');

async function* filesUnder(directory, prefix = '') {
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) yield* filesUnder(absolute, relative);
    else if (entry.isFile() && relative !== 'backend-manifest.json' && isPackagedFile(relative)) yield [relative, absolute];
  }
}

function isPackagedFile(relative) {
  return relative.startsWith('Web/') || /\.(dll|exe)$/i.test(relative) || /\.(deps|runtimeconfig)\.json$/i.test(relative);
}

function hashFile(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    const stream = createReadStream(file);
    stream.on('error', reject);
    stream.on('data', chunk => hash.update(chunk));
    stream.on('end', () => resolve(hash.digest('hex')));
  });
}

async function main() {
  const files = [];
  for await (const [relative, absolute] of filesUnder(stage)) {
    files.push([relative, await hashFile(absolute)]);
  }
  files.sort(([left], [right]) => left.localeCompare(right));
  const fingerprint = createHash('sha256').update(JSON.stringify(files)).digest('hex');
  await fs.writeFile(manifestPath, JSON.stringify({ version: packageJson.version, fingerprint }, null, 2) + '\n');
}

main().catch(error => { console.error('Could not create the backend runtime manifest:', error); process.exitCode = 1; });
