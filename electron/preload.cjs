const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('SimplySoundDesktop', Object.freeze({
  configureFirewall: options => ipcRenderer.invoke('SimplySound:configure-firewall', {
    tailscaleAccess: options?.tailscaleAccess === true,
  }),
  getVersion: () => ipcRenderer.invoke('SimplySound:app-version'),
  getHotkeyStatus: () => ipcRenderer.invoke('SimplySound:hotkey-status'),
  checkForUpdates: () => ipcRenderer.invoke('SimplySound:check-updates'),
  openRelease: url => ipcRenderer.invoke('SimplySound:open-release', url),
}));
