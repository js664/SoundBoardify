const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('SimplySoundDesktop', Object.freeze({
  configureFirewall: options => ipcRenderer.invoke('SimplySound:configure-firewall', {
    lanAccess: options?.lanAccess === true,
    tailscaleAccess: options?.tailscaleAccess === true,
  }),
  getFirewallCommand: options => ipcRenderer.invoke('SimplySound:firewall-command', {
    lanAccess: options?.lanAccess === true,
    tailscaleAccess: options?.tailscaleAccess === true,
  }),
  isSetupComplete: () => ipcRenderer.invoke('SimplySound:setup-status'),
  completeSetup: () => ipcRenderer.invoke('SimplySound:setup-complete'),
  getVersion: () => ipcRenderer.invoke('SimplySound:app-version'),
  getHotkeyStatus: () => ipcRenderer.invoke('SimplySound:hotkey-status'),
  checkForUpdates: () => ipcRenderer.invoke('SimplySound:check-updates'),
  openRelease: url => ipcRenderer.invoke('SimplySound:open-release', url),
  openSoundboard: () => ipcRenderer.invoke('SimplySound:open-soundboard'),
  onSoundboardLaunchError: callback => {
    if (typeof callback !== 'function') return () => {};
    const listener = (_event, message) => callback(String(message || 'Could not open the soundboard in your default browser.'));
    ipcRenderer.on('SimplySound:soundboard-launch-error', listener);
    return () => ipcRenderer.removeListener('SimplySound:soundboard-launch-error', listener);
  },
}));
