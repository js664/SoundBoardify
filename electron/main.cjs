const { app, BrowserWindow, dialog, ipcMain, Menu, shell } = require('electron');
const { execFile, spawn } = require('node:child_process');
const { promisify } = require('node:util');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { isNewerVersion, isSoundboardifyReleaseUrl } = require('./version-utils.cjs');

app.setName('Soundboardify');
app.setAppUserModelId('com.soundboardify.desktop');

let backend;
let mainWindow;
let portFile;
let activePort;
let appOrigin;
let portPoll;
let stableBackendPath;
const execFileAsync = promisify(execFile);

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function backendExecutable() {
  if (app.isPackaged) return path.join(process.resourcesPath, 'backend', 'VRSoundboard.exe');
  return path.resolve(__dirname, '..', 'obj', 'electron-backend', 'VRSoundboard.exe');
}
async function preparePackagedBackend() {
  const source = path.join(process.resourcesPath, 'backend');
  const manifestPath = path.join(source, 'backend-manifest.json');
  const manifest = JSON.parse(await fs.promises.readFile(manifestPath, 'utf8'));
  if (typeof manifest.version !== 'string' || !/^[a-f0-9]{64}$/i.test(manifest.fingerprint || '')) {
    throw new Error('The bundled audio service manifest is invalid. Rebuild the desktop package.');
  }

  const localAppData = path.resolve(process.env.LOCALAPPDATA || app.getPath('userData'));
  const runtime = path.join(localAppData, 'SoundboardifyRuntime', 'backend');
  const relativeRuntime = path.relative(localAppData, runtime);
  if (relativeRuntime.startsWith('..') || path.isAbsolute(relativeRuntime)) throw new Error('The audio service install path is invalid.');
  const removeObsoleteRuntime = async () => {
    const obsolete = path.join(localAppData, 'Soundboardify', 'backend');
    const relativeObsolete = path.relative(localAppData, obsolete);
    if (path.resolve(obsolete) === path.resolve(runtime) || relativeObsolete.startsWith('..') || path.isAbsolute(relativeObsolete)) return;
    try {
      const oldManifest = JSON.parse(await fs.promises.readFile(path.join(obsolete, '.soundboardify-backend.json'), 'utf8'));
      if (typeof oldManifest.version === 'string' && /^[a-f0-9]{64}$/i.test(oldManifest.fingerprint || '') && fs.existsSync(path.join(obsolete, 'VRSoundboard.exe'))) {
        await fs.promises.rm(obsolete, { recursive: true, force: true });
      }
    } catch {}
  };
  const stamp = path.join(runtime, '.soundboardify-backend.json');
  try {
    const installed = JSON.parse(await fs.promises.readFile(stamp, 'utf8'));
    if (installed.fingerprint === manifest.fingerprint && fs.existsSync(path.join(runtime, 'VRSoundboard.exe'))) {
      await removeObsoleteRuntime();
      return path.join(runtime, 'VRSoundboard.exe');
    }
  } catch {}

  const staged = `${runtime}.new-${process.pid}`;
  await fs.promises.mkdir(path.dirname(runtime), { recursive: true });
  await fs.promises.rm(staged, { recursive: true, force: true });
  await fs.promises.cp(source, staged, { recursive: true });
  await fs.promises.writeFile(path.join(staged, '.soundboardify-backend.json'), JSON.stringify(manifest));
  await fs.promises.rm(runtime, { recursive: true, force: true });
  await fs.promises.rename(staged, runtime);
  await removeObsoleteRuntime();
  return path.join(runtime, 'VRSoundboard.exe');
}
function isPrivateHost(host) {
  if (host === 'localhost') return true;
  if (net.isIP(host) !== 4) return false;
  const [a, b] = host.split('.').map(Number);
  return a === 10 || a === 192 && b === 168 || a === 172 && b >= 16 && b <= 31 || a === 169 && b === 254 || a === 100 && b >= 64 && b <= 127;
}
async function waitForServer(timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let port;
  while (Date.now() < deadline) {
    if (backend?.exitCode !== null && backend?.exitCode !== undefined) throw new Error(`Audio service exited with code ${backend.exitCode}.`);
    try { port = Number((await fs.promises.readFile(portFile, 'utf8')).trim()); } catch {}
    if (port >= 1024 && port <= 65535) {
      try {
        const response = await fetch(`http://127.0.0.1:${port}/api/status`, { signal: AbortSignal.timeout(900) });
        if (response.ok) return port;
      } catch {}
    }
    await delay(180);
  }
  throw new Error('The audio service did not start. Check that the soundboard port is available, then try again.');
}
async function startBackend() {
  const executable = app.isPackaged ? await preparePackagedBackend() : backendExecutable();
  if (!fs.existsSync(executable)) throw new Error('The soundboard audio service is missing. Rebuild the desktop package.');
  stableBackendPath = executable;
  portFile = path.join(app.getPath('userData'), 'backend-port.txt');
  await fs.promises.mkdir(path.dirname(portFile), { recursive: true });
  await fs.promises.rm(portFile, { force: true });
  backend = spawn(executable, ['--electron-backend', `--electron-port-file=${portFile}`], { windowsHide: true, stdio: 'ignore' });
  return waitForServer();
}
async function createWindow(port) {
  activePort = port;
  appOrigin = `http://127.0.0.1:${port}`;
  Menu.setApplicationMenu(null);
  mainWindow = new BrowserWindow({
    width: 1220,
    height: 850,
    minWidth: 850,
    minHeight: 620,
    backgroundColor: '#10110f',
    title: 'Soundboardify',
    show: false,
    autoHideMenuBar: true,
    titleBarStyle: 'default',
    icon: app.isPackaged ? path.join(process.resourcesPath, 'app-icon.ico') : path.join(__dirname, 'soundboardify.ico'),
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: false },
  });
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('closed', () => { mainWindow = null; });
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    try {
      const target = new URL(url);
      if (['http:', 'https:'].includes(target.protocol) && isPrivateHost(target.hostname)) void shell.openExternal(url);
    } catch {}
    return { action: 'deny' };
  });
  mainWindow.webContents.on('will-navigate', (event, url) => {
    try { if (new URL(url).origin !== appOrigin) event.preventDefault(); }
    catch { event.preventDefault(); }
  });
  await mainWindow.loadURL(`http://127.0.0.1:${port}/?desktop=1`);
}

ipcMain.handle('soundboardify:configure-firewall', async (event, options) => {
  if (!mainWindow || event.sender !== mainWindow.webContents || !stableBackendPath) throw new Error('Firewall setup is only available from the Soundboardify desktop window.');
  if (!Number.isInteger(activePort) || activePort < 1024 || activePort > 65535) throw new Error('The Web UI port is not ready yet.');
  const tailscaleAccess = options?.tailscaleAccess === true;
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const script = path.join(process.resourcesPath, 'firewall-setup.ps1');
  const literal = value => `'${String(value).replaceAll("'", "''")}'`;
  const elevatedScript = `& ${literal(script)} -Program ${literal(stableBackendPath)} -Port ${activePort} -TailscaleAccess $${tailscaleAccess ? 'true' : 'false'}`;
  const encoded = Buffer.from(elevatedScript, 'utf16le').toString('base64');
  const elevatedArgs = `-NoProfile -ExecutionPolicy Bypass -EncodedCommand ${encoded}`;
  const launcher = `$process = Start-Process -FilePath ${literal(powershell)} -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList ${literal(elevatedArgs)}; exit $process.ExitCode`;
  await execFileAsync(powershell, ['-NoProfile', '-Command', launcher], { windowsHide: true, timeout: 120000 });
  return true;
});

ipcMain.handle('soundboardify:app-version', event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Version information is only available in Soundboardify.');
  return app.getVersion();
});

ipcMain.handle('soundboardify:check-updates', async event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Update checks are only available in Soundboardify.');
  const currentVersion = app.getVersion();
  const response = await fetch('https://api.github.com/repos/js664/SoundBoardify/releases/latest', {
    headers: { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Soundboardify' },
    signal: AbortSignal.timeout(8000),
  });
  if (response.status === 404) return { currentVersion, latestVersion: null, updateAvailable: false, state: 'no-release' };
  if (!response.ok) throw new Error(response.status === 403 ? 'GitHub update checks are temporarily rate-limited.' : `GitHub update check failed (${response.status}).`);
  const release = await response.json();
  if (typeof release.tag_name !== 'string' || !isSoundboardifyReleaseUrl(release.html_url)) throw new Error('GitHub returned invalid release information.');
  const latestVersion = release.tag_name.replace(/^v/i, '');
  const updateAvailable = isNewerVersion(currentVersion, release.tag_name);
  return { currentVersion, latestVersion, updateAvailable, releaseUrl: release.html_url, state: updateAvailable ? 'available' : 'current' };
});

ipcMain.handle('soundboardify:open-release', async (event, url) => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Release links are only available in Soundboardify.');
  if (!isSoundboardifyReleaseUrl(url)) throw new Error('That is not a Soundboardify GitHub release link.');
  await shell.openExternal(url);
  return true;
});

function watchBackendPort() {
  portPoll = setInterval(async () => {
    let nextPort;
    try { nextPort = Number((await fs.promises.readFile(portFile, 'utf8')).trim()); } catch { return; }
    if (!Number.isInteger(nextPort) || nextPort < 1024 || nextPort > 65535 || nextPort === activePort) return;
    activePort = nextPort;
    appOrigin = `http://127.0.0.1:${nextPort}`;
    if (!mainWindow || mainWindow.isDestroyed()) return;
    try { await mainWindow.loadURL(`${appOrigin}/?desktop=1`); }
    catch (error) { console.error('Could not reconnect the desktop window after a port change:', error); }
  }, 350);
}

app.on('before-quit', () => { if (portPoll) clearInterval(portPoll); if (backend && !backend.killed) backend.kill(); });
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });

const hasLock = app.requestSingleInstanceLock();
if (!hasLock) app.quit();
else app.whenReady().then(async () => {
  try { const port = await startBackend(); await createWindow(port); watchBackendPort(); }
  catch (error) { dialog.showErrorBox('Soundboardify could not start', error.message); app.quit(); }
});
