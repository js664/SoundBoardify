const fs = require('node:fs/promises');
const path = require('node:path');

const staging = path.resolve(__dirname, '..', 'obj', 'electron-backend');
fs.rm(staging, { recursive: true, force: true })
  .then(() => fs.mkdir(staging, { recursive: true }))
  .catch(error => { console.error(`Could not clean backend staging directory ${staging}:`, error); process.exitCode = 1; });
