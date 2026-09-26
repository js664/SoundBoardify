const { app, BrowserWindow, dialog, globalShortcut, ipcMain, Menu, shell } = require('electron');
const { execFile, spawn } = require('node:child_process');
const { promisify } = require('node:util');
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { isNewerVersion, isSimplySoundReleaseAssetUrl, isSimplySoundReleaseUrl, selectNewestRelease } = require('./version-utils.cjs');
const { createMarketplaceBridge } = require('./marketplace-bridge.cjs');
const { buildFirewallCommand } = require('./firewall-command.cjs');
const { isSoundboardWindowTarget } = require('./soundboard-link.cjs');

app.setName('SimplySound');
app.setAppUserModelId('com.simplysound.desktop');

// Electron's application name determines its roaming settings directory. Copy the old
// profile once so port, privacy, and desktop preferences survive the product rename.
const newUserData = path.join(app.getPath('appData'), 'SimplySound');
const previousUserData = path.join(app.getPath('appData'), 'Soundboardify');
if (!fs.existsSync(newUserData) && fs.existsSync(previousUserData)) {
  fs.cpSync(previousUserData, newUserData, { recursive: true, errorOnExist: true });
}
app.setPath('userData', newUserData);
const setupStatePath = path.join(newUserData, 'setup-state.json');

let backend;
let marketplaceBridge;
let mainWindow;
let portFile;
let activePort;
let appOrigin;
let portPoll;
let stableBackendPath;
let hotkeyPoll;
let hotkeyRefreshActive = false;
let hotkeySignature = '';
const registeredHotkeys = new Map();
let unavailableHotkeys = [];
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
  const runtime = path.join(localAppData, 'SimplySoundRuntime', 'backend');
  const relativeRuntime = path.relative(localAppData, runtime);
  if (relativeRuntime.startsWith('..') || path.isAbsolute(relativeRuntime)) throw new Error('The audio service install path is invalid.');
  const removeObsoleteRuntime = async () => {
    const obsolete = path.join(localAppData, 'SimplySound', 'backend');
    const relativeObsolete = path.relative(localAppData, obsolete);
    if (path.resolve(obsolete) === path.resolve(runtime) || relativeObsolete.startsWith('..') || path.isAbsolute(relativeObsolete)) return;
    try {
      const oldManifest = JSON.parse(await fs.promises.readFile(path.join(obsolete, '.SimplySound-backend.json'), 'utf8'));
      if (typeof oldManifest.version === 'string' && /^[a-f0-9]{64}$/i.test(oldManifest.fingerprint || '') && fs.existsSync(path.join(obsolete, 'VRSoundboard.exe'))) {
        await fs.promises.rm(obsolete, { recursive: true, force: true });
      }
    } catch {}
  };
  const stamp = path.join(runtime, '.SimplySound-backend.json');
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
  await fs.promises.writeFile(path.join(staged, '.SimplySound-backend.json'), JSON.stringify(manifest));
  await fs.promises.rm(runtime, { recursive: true, force: true });
  await fs.promises.rename(staged, runtime);
  await removeObsoleteRuntime();
  return path.join(runtime, 'VRSoundboard.exe');
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
  marketplaceBridge = await createMarketplaceBridge();
  backend = spawn(executable, ['--electron-backend', `--electron-port-file=${portFile}`, `--electron-marketplace-bridge-port=${marketplaceBridge.port}`, `--electron-marketplace-bridge-token=${marketplaceBridge.token}`], { windowsHide: true, stdio: 'ignore' });
  return waitForServer();
}
async function refreshGlobalHotkeys() {
  if (!appOrigin || hotkeyRefreshActive) return;
  hotkeyRefreshActive = true;
  try {
    const response = await fetch(`${appOrigin}/api/hotkeys`, { signal: AbortSignal.timeout(1800) });
    if (!response.ok) return;
    const sounds = await response.json();
    const entries = sounds.filter(sound => typeof sound.hotkey === 'string' && sound.hotkey).map(sound => ({ id: sound.id, name: sound.name, hotkey: sound.hotkey }));
    const signature = JSON.stringify(entries);
    if (signature === hotkeySignature) {
      const stillUnavailable = [];
      for (const sound of unavailableHotkeys) {
        try {
          if (globalShortcut.register(sound.hotkey, () => {
            void fetch(`${appOrigin}/api/hotkeys/${sound.id}/trigger`, { method: 'POST', signal: AbortSignal.timeout(2500) }).catch(error => console.warn(`Could not play hotkey sound ${sound.id}:`, error));
          })) registeredHotkeys.set(sound.hotkey, sound.id);
          else stillUnavailable.push(sound);
        } catch (error) { stillUnavailable.push({ ...sound, reason: error.message }); }
      }
      unavailableHotkeys = stillUnavailable;
      return;
    }
    globalShortcut.unregisterAll();
    registeredHotkeys.clear();
    unavailableHotkeys = [];
    hotkeySignature = signature;
    for (const sound of entries) {
      try {
        const registered = globalShortcut.register(sound.hotkey, () => {
          void fetch(`${appOrigin}/api/hotkeys/${sound.id}/trigger`, { method: 'POST', signal: AbortSignal.timeout(2500) }).catch(error => console.warn(`Could not play hotkey sound ${sound.id}:`, error));
        });
        if (registered) registeredHotkeys.set(sound.hotkey, sound.id);
        else unavailableHotkeys.push(sound);
      } catch (error) {
        unavailableHotkeys.push({ ...sound, reason: error.message });
      }
    }
  } catch (error) { console.warn('Could not refresh sound hotkeys:', error); }
  finally { hotkeyRefreshActive = false; }
}
async function createWindow(port) {
  activePort = port;
  appOrigin = `http://127.0.0.1:${port}`;
  Menu.setApplicationMenu(null);
  mainWindow = new BrowserWindow({
    width: 1080,
    height: 780,
    minWidth: 900,
    minHeight: 660,
    backgroundColor: '#202124',
    title: 'SimplySound',
    show: false,
    autoHideMenuBar: true,
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#202124', symbolColor: '#c9cbd1', height: 36 },
    icon: app.isPackaged ? path.join(process.resourcesPath, 'app-icon.ico') : path.join(__dirname, 'SimplySound.ico'),
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: false },
  });
  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('closed', () => { mainWindow = null; });
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (isSoundboardWindowTarget(appOrigin, url)) {
      void shell.openExternal(url).catch(error => {
        console.error('Could not open the SimplySound soundboard:', error);
        dialog.showErrorBox('Could not open soundboard', 'SimplySound could not open the soundboard in your browser. Check that a default browser is installed, then try again.');
      });
    }
    return { action: 'deny' };
  });
  mainWindow.webContents.on('will-navigate', (event, url) => {
    try { if (new URL(url).origin !== appOrigin) event.preventDefault(); }
    catch { event.preventDefault(); }
  });
  await mainWindow.loadURL(`http://127.0.0.1:${port}/?desktop=1`);
}

ipcMain.handle('SimplySound:configure-firewall', async (event, options) => {
  if (!mainWindow || event.sender !== mainWindow.webContents || !stableBackendPath) throw new Error('Firewall setup is only available from the SimplySound desktop window.');
  if (!Number.isInteger(activePort) || activePort < 1024 || activePort > 65535) throw new Error('The Web UI port is not ready yet.');
  const lanAccess = options?.lanAccess === true;
  const tailscaleAccess = options?.tailscaleAccess === true;
  if (!lanAccess && !tailscaleAccess) throw new Error('Enable Wi-Fi/LAN or Tailscale access before creating a firewall rule.');
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  const script = path.join(process.resourcesPath, 'firewall-setup.ps1');
  const literal = value => `'${String(value).replaceAll("'", "''")}'`;
  const elevatedScript = `& ${literal(script)} -Program ${literal(stableBackendPath)} -Port ${activePort} -LanAccess $${lanAccess ? 'true' : 'false'} -TailscaleAccess $${tailscaleAccess ? 'true' : 'false'}`;
  const encoded = Buffer.from(elevatedScript, 'utf16le').toString('base64');
  const elevatedArgs = `-NoProfile -ExecutionPolicy Bypass -EncodedCommand ${encoded}`;
  const launcher = `$process = Start-Process -FilePath ${literal(powershell)} -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList ${literal(elevatedArgs)}; exit $process.ExitCode`;
  await execFileAsync(powershell, ['-NoProfile', '-Command', launcher], { windowsHide: true, timeout: 120000 });
  return true;
});

ipcMain.handle('SimplySound:firewall-command', (event, options) => {
  if (!mainWindow || event.sender !== mainWindow.webContents || !stableBackendPath) throw new Error('The manual firewall command is only available from the SimplySound desktop window.');
  return buildFirewallCommand({
    program: stableBackendPath,
    port: activePort,
    lanAccess: options?.lanAccess === true,
    tailscaleAccess: options?.tailscaleAccess === true,
  });
});

ipcMain.handle('SimplySound:setup-status', async event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Setup status is only available from the SimplySound desktop window.');
  try {
    const state = JSON.parse(await fs.promises.readFile(setupStatePath, 'utf8'));
    return state.completed === true;
  } catch { return false; }
});

ipcMain.handle('SimplySound:setup-complete', async event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Setup completion is only available from the SimplySound desktop window.');
  await fs.promises.mkdir(path.dirname(setupStatePath), { recursive: true });
  await fs.promises.writeFile(setupStatePath, JSON.stringify({ completed: true, completedAt: new Date().toISOString() }));
  return true;
});

ipcMain.handle('SimplySound:app-version', event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Version information is only available in SimplySound.');
  return app.getVersion();
});

ipcMain.handle('SimplySound:hotkey-status', event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Hotkey status is only available in SimplySound.');
  return { active: registeredHotkeys.size, unavailable: unavailableHotkeys };
});

ipcMain.handle('SimplySound:check-updates', async event => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Update checks are only available in SimplySound.');
  const currentVersion = app.getVersion();
  const response = await fetch('https://api.github.com/repos/js664/SimplySound/releases?per_page=100', {
    headers: { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2026-03-10', 'User-Agent': 'SimplySound' },
    signal: AbortSignal.timeout(8000),
  });
  if (response.status === 404) return { currentVersion, latestVersion: null, updateAvailable: false, state: 'no-release' };
  if (!response.ok) throw new Error(response.status === 403 ? 'GitHub update checks are temporarily rate-limited.' : `GitHub update check failed (${response.status}).`);
  const release = selectNewestRelease(await response.json());
  if (!release) return { currentVersion, latestVersion: null, updateAvailable: false, state: 'no-release' };
  const latestVersion = release.tag_name.replace(/^v/i, '');
  const updateAvailable = isNewerVersion(currentVersion, release.tag_name);
  return {
    currentVersion,
    latestVersion,
    updateAvailable,
    releaseUrl: release.html_url,
    releaseName: typeof release.name === 'string' ? release.name.slice(0, 160) : `SimplySound ${latestVersion}`,
    releaseNotes: typeof release.body === 'string' ? release.body.slice(0, 6000) : '',
    releasePublishedAt: typeof release.published_at === 'string' ? release.published_at : null,
    assets: Array.isArray(release.assets) ? release.assets.filter(asset => typeof asset.name === 'string' && /^SimplySound-.*\.(exe)$/i.test(asset.name) && isSimplySoundReleaseAssetUrl(asset.browser_download_url)).map(asset => ({ name: asset.name, size: asset.size })) : [],
    state: updateAvailable ? 'available' : 'current',
  };
});

ipcMain.handle('SimplySound:open-release', async (event, url) => {
  if (!mainWindow || event.sender !== mainWindow.webContents) throw new Error('Release links are only available in SimplySound.');
  if (!isSimplySoundReleaseUrl(url)) throw new Error('That is not a SimplySound GitHub release link.');
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

app.on('before-quit', () => {
  if (portPoll) clearInterval(portPoll);
  if (hotkeyPoll) clearInterval(hotkeyPoll);
  // A secondary launch can call app.quit() before Electron reaches ready.
  // globalShortcut is only available after ready, so guard early shutdowns.
  if (app.isReady()) globalShortcut.unregisterAll();
  if (backend && !backend.killed) backend.kill();
  marketplaceBridge?.server.close();
});
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });

const hasLock = app.requestSingleInstanceLock();
app.on('second-instance', () => {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
});
if (!hasLock) app.quit();
else app.whenReady().then(async () => {
  try { const port = await startBackend(); await createWindow(port); watchBackendPort(); void refreshGlobalHotkeys(); hotkeyPoll = setInterval(() => { void refreshGlobalHotkeys(); }, 3000); }
  catch (error) { dialog.showErrorBox('SimplySound could not start', error.message); app.quit(); }
});
