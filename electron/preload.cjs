const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soundboardifyDesktop', Object.freeze({
  configureFirewall: options => ipcRenderer.invoke('soundboardify:configure-firewall', {
    tailscaleAccess: options?.tailscaleAccess === true,
  }),
  getVersion: () => ipcRenderer.invoke('soundboardify:app-version'),
  getHotkeyStatus: () => ipcRenderer.invoke('soundboardify:hotkey-status'),
  checkForUpdates: () => ipcRenderer.invoke('soundboardify:check-updates'),
  openRelease: url => ipcRenderer.invoke('soundboardify:open-release', url),
}));
