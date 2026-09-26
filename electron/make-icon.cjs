const fs = require('node:fs');
const path = require('node:path');
const { PNG } = require('pngjs');

async function makeIcon() {
  const sourcePath = path.resolve(__dirname, '..', 'assets', 'logo.png');
  const squarePath = path.join(__dirname, '.square-icon.png');
  const source = PNG.sync.read(await fs.promises.readFile(sourcePath));
  const size = Math.max(source.width, source.height);
  const square = new PNG({ width: size, height: size });
  const offsetX = Math.floor((size - source.width) / 2);
  const offsetY = Math.floor((size - source.height) / 2);
  for (let y = 0; y < source.height; y++) {
    const from = y * source.width * 4;
    const to = ((y + offsetY) * size + offsetX) * 4;
    source.data.copy(square.data, to, from, from + source.width * 4);
  }
  await fs.promises.writeFile(squarePath, PNG.sync.write(square));
  try {
    const { default: pngToIco } = await import('png-to-ico');
    const icon = await pngToIco(squarePath);
    await fs.promises.writeFile(path.join(__dirname, 'SimplySound.ico'), icon);
  } finally { await fs.promises.rm(squarePath, { force: true }); }
}
makeIcon()
  .catch(error => { console.error(error); process.exitCode = 1; });
