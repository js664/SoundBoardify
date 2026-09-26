import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import './desktop.css';

const ART = '/assets/logo.png';
async function request(path, options = {}) {
  const response = await fetch(path, options);
  if (!response.ok) throw new Error((await response.text()) || `Request failed (${response.status})`);
  return response.status === 204 ? null : response.json().catch(() => null);
}
function Icon({ name, size = 18 }) {
  const paths = {
    home: <><path d="m3 10 9-7 9 7v10a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/><path d="M9 21v-6h6v6"/></>,
    volume: <><path d="M11 5 6 9H3v6h3l5 4z"/><path d="M15 9a5 5 0 0 1 0 6M18 6a9 9 0 0 1 0 12"/></>,
    copy: <><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></>,
    check: <path d="m5 12 4 4L19 6"/>,
    refresh: <><path d="M20 7v5h-5M4 17v-5h5"/><path d="M5.6 9A7 7 0 0 1 18 6l2 2M4 16l2 2a7 7 0 0 0 12.4-3"/></>,
    lock: <><rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></>,
    arrow: <><path d="M7 17 17 7M7 7h10v10"/></>,
  };
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}
function Switch({ checked, onChange, label }) { return <button type="button" role="switch" aria-checked={checked} aria-label={label} className={`desktop-switch ${checked ? 'on' : ''}`} onClick={() => onChange(!checked)}><i/></button>; }
function DevicePicker({ id, label, detail, value, currentName, devices, onChange }) {
  const [open, setOpen] = useState(false);
  const selected = devices.find(device => device.id === value);
  const ordered = useMemo(() => [...devices].sort((a, b) => Number(b.state === 'Active') - Number(a.state === 'Active') || a.name.localeCompare(b.name)), [devices]);
  useEffect(() => {
    if (!open) return;
    const close = event => { if (!event.target.closest(`[data-picker="${id}"]`)) setOpen(false); };
    const escape = event => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', close); document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('pointerdown', close); document.removeEventListener('keydown', escape); };
  }, [id, open]);
  const choose = next => { onChange(next); setOpen(false); };
  return <div className={`desktop-device ${open ? 'is-open' : ''}`} data-picker={id}>
    <div className="desktop-device-copy"><strong>{label}</strong><small>{detail}</small></div>
    <button type="button" className="device-picker-trigger" aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      <span className={`device-led ${selected?.state === 'Active' || !value ? 'ready' : ''}`}/><span className="device-picker-value"><strong>{selected?.name || (value ? 'Selected device unavailable' : currentName || 'Windows default')}</strong><small>{selected ? `${selected.sampleRate ? `${Math.round(selected.sampleRate / 1000)} kHz · ` : ''}${selected.state}` : 'Follow Windows default'}</small></span><span className="device-chevron">⌄</span>
    </button>
    {open && <div className="device-picker-menu" role="listbox">
      <button type="button" role="option" aria-selected={!value} onClick={() => choose(null)}><span><strong>Windows default</strong><small>{currentName || 'Follow your system playback device'}</small></span>{!value && <Icon name="check" size={15}/>}</button>
      {ordered.map(device => <button type="button" role="option" aria-selected={value === device.id} key={device.id} onClick={() => choose(device.id)}><span><strong>{device.name}</strong><small>{device.sampleRate ? `${Math.round(device.sampleRate / 1000)} kHz · ` : ''}{device.state}</small></span>{value === device.id && <Icon name="check" size={15}/>}</button>)}
    </div>}
  </div>;
}

export default function DesktopApp() {
  const [settings, setSettings] = useState(null);
  const [devices, setDevices] = useState([]);
  const [status, setStatus] = useState(null);
  const [portDraft, setPortDraft] = useState('6769');
  const [editingPort, setEditingPort] = useState(false);
  const [pairDraft, setPairDraft] = useState('');
  const [qrVersion, setQrVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState('');
  const [appVersion, setAppVersion] = useState(null);
  const [updateState, setUpdateState] = useState('checking');
  const [latestVersion, setLatestVersion] = useState(null);
  const [releaseUrl, setReleaseUrl] = useState(null);
  const [releaseName, setReleaseName] = useState('');
  const [releaseNotes, setReleaseNotes] = useState('');
  const [releasePublishedAt, setReleasePublishedAt] = useState(null);
  const [releaseAssets, setReleaseAssets] = useState([]);
  const [hotkeyStatus, setHotkeyStatus] = useState({ active: 0, unavailable: [] });
  const [checkingUpdates, setCheckingUpdates] = useState(false);
  const [activeSection, setActiveSection] = useState('overview');
  const masterTimer = useRef(null);

  async function refresh() {
    const [nextSettings, endpoints, state] = await Promise.all([request('/api/settings'), request('/api/audio/devices'), request('/api/status')]);
    setSettings(nextSettings); setPortDraft(String(nextSettings.port)); setDevices(endpoints || []); setStatus(state);
  }
  const checkForUpdates = useCallback(async () => {
    const bridge = window.SimplySoundDesktop;
    if (!bridge?.getVersion || !bridge?.checkForUpdates) { setUpdateState('unavailable'); return; }
    setCheckingUpdates(true); setUpdateState('checking');
    try {
      const currentVersion = await bridge.getVersion();
      setAppVersion(currentVersion);
      const result = await bridge.checkForUpdates();
      setAppVersion(result.currentVersion || currentVersion);
      setLatestVersion(result.latestVersion || null);
      setReleaseUrl(result.releaseUrl || null);
      setReleaseName(result.releaseName || '');
      setReleaseNotes(result.releaseNotes || '');
      setReleasePublishedAt(result.releasePublishedAt || null);
      setReleaseAssets(result.assets || []);
      setUpdateState(result.state || (result.updateAvailable ? 'available' : 'current'));
    } catch {
      setUpdateState('error');
    } finally { setCheckingUpdates(false); }
  }, []);
  useEffect(() => {
    refresh().catch(error => setToast(error.message));
    checkForUpdates();
    const timer = setInterval(() => request('/api/status').then(setStatus).catch(() => {}), 3500);
    return () => clearInterval(timer);
  }, [checkForUpdates]);
  useEffect(() => {
    const refreshHotkeys = () => window.SimplySoundDesktop?.getHotkeyStatus?.().then(setHotkeyStatus).catch(() => {});
    refreshHotkeys();
    const timer = setInterval(refreshHotkeys, 2500);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => () => clearTimeout(masterTimer.current), []);
  useEffect(() => { if (!toast) return; const timer = setTimeout(() => setToast(''), 3500); return () => clearTimeout(timer); }, [toast]);

  async function patchSettings(patch) {
    try {
      const saved = await request('/api/settings', { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) });
      setSettings(current => ({ ...current, ...saved })); setStatus(current => current ? { ...current, wifiUrl: undefined, tailscaleUrl: undefined } : current);
      await refresh();
      return saved;
    } catch (error) { setToast(error.message); throw error; }
  }
  async function selectDevice(id) {
    try { await request('/api/audio/device', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: id || null }) }); await refresh(); setToast('Playback output updated'); }
    catch (error) { setToast(error.message); }
  }
  async function selectMonitorDevice(id) {
    try { await request('/api/audio/monitor-device', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: id || null }) }); await refresh(); }
    catch (error) { setToast(error.message); }
  }
  function changeMasterVolume(value) {
    setSettings(current => ({ ...current, masterVolume: value }));
    clearTimeout(masterTimer.current);
    masterTimer.current = setTimeout(async () => {
      try { const saved = await request('/api/settings', { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ masterVolume: value }) }); setSettings(current => ({ ...current, masterVolume: saved.masterVolume })); }
      catch (error) { setToast(error.message); refresh().catch(() => {}); }
    }, 100);
  }
  async function applyPort() {
    const port = Number(portDraft);
    if (!Number.isInteger(port) || port < 1024 || port > 65535) { setToast('Choose a port between 1024 and 65535'); return; }
    setBusy(true);
    try { await patchSettings({ port }); setEditingPort(false); setToast(`Web UI restarting on ${port}`); }
    catch {} finally { setBusy(false); }
  }
  async function copy(value, label) {
    if (!value) return;
    try { await navigator.clipboard.writeText(value); setToast(`${label} link copied`); }
    catch { setToast(value); }
  }
  function createToken() {
    const bytes = crypto.getRandomValues(new Uint8Array(24));
    return btoa(String.fromCharCode(...bytes)).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
  }
  async function pairingAction(action) {
    setBusy(true);
    try {
      const patch = action === 'disable' ? { clearPairingToken: true } : { pairingToken: action === 'rotate' ? createToken() : pairDraft.trim() || createToken() };
      await patchSettings(patch); setPairDraft(''); setQrVersion(version => version + 1); setToast(action === 'disable' ? 'Pairing protection disabled' : 'Pairing link updated — scan the new QR code');
    } catch {} finally { setBusy(false); }
  }
  async function configureFirewall() {
    if (!window.SimplySoundDesktop?.configureFirewall) {
      setToast('Open this in the SimplySound desktop app to update Windows Firewall.');
      return;
    }
    setBusy(true);
    try {
      await window.SimplySoundDesktop.configureFirewall({ tailscaleAccess: settings.tailscaleAccess });
      setToast('Windows Firewall now allows SimplySound on this network.');
    } catch (error) {
      setToast(error.message || 'Windows Firewall permission was not granted.');
    } finally { setBusy(false); }
  }
  async function openRelease() {
    try { await window.SimplySoundDesktop?.openRelease(releaseUrl); }
    catch { setToast('Could not open the GitHub release page.'); }
  }

  const appClass = 'desktop-app options-only';
  const wifiUrl = status?.wifiUrl || status?.url;
  const tailscaleUrl = status?.tailscaleUrl;
  const port = status?.activePort || settings?.port || 6769;
  const outputReady = status?.audio?.startsWith('Connected');
  const updateMessage = {
    checking: 'Checking GitHub…',
    available: `v${latestVersion} is available`,
    current: 'You’re up to date',
    'no-release': 'No GitHub release yet',
    error: 'Could not reach GitHub',
    unavailable: 'Desktop version unavailable',
  }[updateState] || 'Check for updates';
  const navigation = [
    { id: 'overview', label: 'Overview', icon: 'home' },
    { id: 'audio', label: 'Audio', icon: 'volume' },
    { id: 'network', label: 'Phone access', icon: 'lock' },
    { id: 'updates', label: 'Updates', icon: 'refresh' },
  ];
  const sectionCopy = {
    overview: ['Your soundboard at a glance', 'Open the phone controller or check that audio is ready.'],
    audio: ['Audio setup', 'Choose where sound effects play and tune local monitoring.'],
    network: ['Phone access', 'Choose which private networks can reach your controller.'],
    updates: ['Updates & about', 'Review new versions before choosing when to install them.'],
  }[activeSection];
  return <main className={appClass}>
    <aside className="studio-sidebar" aria-label="Main navigation">
      <div className="desktop-brand"><span className="desktop-mark"><img src={ART} alt=""/></span><span><strong>SimplySound</strong><small>DESKTOP</small></span></div>
      <div className="sidebar-caption">WORKSPACE</div>
      <nav className="studio-navigation" aria-label="Settings sections">
        {navigation.map(item => <button key={item.id} type="button" className={`studio-nav-item ${activeSection === item.id ? 'active' : ''}`} aria-current={activeSection === item.id ? 'page' : undefined} onClick={() => setActiveSection(item.id)}><Icon name={item.icon} size={18}/><span>{item.label}</span>{item.id === 'updates' && updateState === 'available' && <i className="nav-update-dot"/>}</button>)}
      </nav>
      <div className="sidebar-spacer"/>
      <div className="sidebar-health"><i className={outputReady ? 'connected' : ''}/><span><strong>{outputReady ? 'Audio ready' : 'Starting audio'}</strong><small>{outputReady ? (status?.endpoint || 'Sound output connected') : 'Checking your device'}</small></span></div>
      <div className="sidebar-version">EARLY BETA <span>{appVersion ? `v${appVersion}` : ''}</span></div>
    </aside>
    <div className="studio-main">
      <header className="desktop-topbar">
        <div className="breadcrumb"><span>SimplySound</span><b>/</b><strong>{navigation.find(item => item.id === activeSection)?.label}</strong></div>
        <div className="desktop-live"><i className={outputReady ? 'connected' : ''}/>{outputReady ? 'System ready' : 'Starting up'}</div>
      </header>
      <section className="control-layout">
      <div className="control-heading"><div><h1>{sectionCopy[0]}</h1><p>{sectionCopy[1]}</p></div></div>
      {!settings ? <div className="desktop-loading">Connecting to SimplySound…</div> : <>
        {activeSection === 'overview' && <>
        <section className="connect-panel">
          <div className="connect-main">
            <div className="connect-title"><span className="connect-glyph"><Icon name="arrow" size={17}/></span><div><h2>Open your soundboard</h2><p>Scan to open the mobile controller.</p></div></div>
            <div className="connection-addresses">
              <div className="address-row"><span className="address-label">Wi-Fi</span><div className="address-actions"><a className="address-link" href={wifiUrl || undefined} target="_blank" rel="noreferrer" title="Open Wi-Fi soundboard">{wifiUrl || (settings?.lanAccess ? 'Waiting for network…' : 'Access is off')}<Icon name="arrow" size={14}/></a><button className="url-copy" onClick={() => copy(wifiUrl, 'Wi-Fi')} disabled={!wifiUrl} aria-label="Copy Wi-Fi soundboard link"><Icon name="copy" size={14}/></button></div></div>
              {tailscaleUrl && <div className="address-row"><span className="address-label">Tailscale</span><div className="address-actions"><a className="address-link" href={tailscaleUrl} target="_blank" rel="noreferrer" title="Open Tailscale soundboard">{tailscaleUrl}<Icon name="arrow" size={14}/></a><button className="url-copy" onClick={() => copy(tailscaleUrl, 'Tailscale')} aria-label="Copy Tailscale soundboard link"><Icon name="copy" size={14}/></button></div></div>}
              <div className="address-row port-row"><span className="address-label">Web UI port</span>{editingPort ? <div className="port-edit"><input autoFocus type="number" min="1024" max="65535" value={portDraft} onChange={event => setPortDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') applyPort(); if (event.key === 'Escape') setEditingPort(false); }}/><button disabled={busy} onClick={applyPort}>{busy ? 'Saving…' : 'Apply'}</button></div> : <button className="port-value" onClick={() => { setPortDraft(String(settings.port)); setEditingPort(true); }} aria-label={`Change web UI port, currently ${port}`}><strong>{port}</strong><span>Edit</span></button>}</div>
            </div>
          </div>
          <div className="qr-frame">{wifiUrl && !wifiUrl.includes('127.0.0.1') ? <img src={`/api/phone/qr?v=${qrVersion}`} alt="Wi-Fi/LAN QR code for the soundboard"/> : <div className="qr-unavailable"><span className="qr-unavailable-mark"><Icon name="arrow" size={19}/></span><span>{settings?.lanAccess ? 'Connect this PC to Wi-Fi to show a phone QR.' : 'Turn on Wi-Fi / LAN access to show the QR.'}</span></div>}<span>WI-FI / LAN QR</span></div>
        </section>
        <div className="overview-summary" aria-label="System summary">
          <div><span className="summary-label">Sound effects output</span><strong>{status?.endpoint || 'Windows default'}</strong><small>Default playback route</small></div>
          <div><span className="summary-label">Phone controller</span><strong>{settings.lanAccess || settings.tailscaleAccess ? 'Ready to connect' : 'Access is off'}</strong><small>{[settings.lanAccess && 'Wi-Fi', settings.tailscaleAccess && 'Tailscale'].filter(Boolean).join(' · ') || 'Enable access in Phone access'}</small></div>
          <button type="button" onClick={() => setActiveSection('audio')}><span className="summary-label">Quick audio check</span><strong>Open audio setup <Icon name="arrow" size={15}/></strong><small>Devices and local monitoring</small></button>
        </div>
        </>}
        {activeSection === 'audio' && <div className="settings-columns single-column">
          <section className="settings-section audio-section">
            <div className="section-heading"><span className="section-icon"><Icon name="volume"/></span><div><h2>Audio</h2><p>Choose where your sounds play.</p></div></div>
            <DevicePicker id="playback" label="Soundboard output" detail="Defaults to the Windows playback device." value={settings.endpointId || null} currentName={status?.endpoint} devices={devices} onChange={selectDevice}/>
            <div className="setting-divider"/>
            <div className="setting-inline"><div><strong>Hear sounds on this PC</strong><small>Play a local copy while sending audio to your output.</small></div><Switch label="Hear sounds on this PC" checked={settings.monitorLocally} onChange={monitorLocally => patchSettings({ monitorLocally }).catch(() => {})}/></div>
            {settings.monitorLocally && <DevicePicker id="monitor" label="Local playback" detail="Optional listening device; follows Windows by default." value={settings.monitorEndpointId || null} currentName={status?.monitorEndpoint} devices={devices} onChange={selectMonitorDevice}/>}
            <label className="master-level"><span><strong>Overall volume</strong><output>{Math.round(settings.masterVolume * 100)}%</output></span><input type="range" min="0" max="1" step=".01" value={settings.masterVolume} onChange={event => changeMasterVolume(Number(event.target.value))}/></label>
            <button className="test-output" onClick={() => request('/api/audio/test', { method: 'POST' }).then(() => setToast('Test sound played')).catch(error => setToast(error.message))}>Play a test sound <Icon name="arrow" size={14}/></button>
            <div className="shortcut-summary">
              <div><strong>Global sound hotkeys</strong><small>Assign Ctrl + Alt shortcuts in a sound’s edit menu. They work while SimplySound is running, even in the background.</small></div>
              <span className={hotkeyStatus.unavailable.length ? 'has-conflicts' : ''}>{hotkeyStatus.active} active</span>
              {hotkeyStatus.unavailable.length > 0 && <p>Could not register {hotkeyStatus.unavailable.map(item => item.hotkey).join(', ')}. Another app may already use these shortcuts.</p>}
            </div>
          </section>
        </div>}
        {activeSection === 'network' && <div className="settings-columns single-column">
          <section className="settings-section connection-section">
            <div className="section-heading"><span className="section-icon"><Icon name="lock"/></span><div><h2>Phone access</h2><p>Control this board from your private network.</p></div></div>
            <div className="setting-inline access-toggle"><div><strong>Allow Wi-Fi / LAN access</strong><small>Let devices on your local network control the board.</small></div><Switch label="Allow Wi-Fi and LAN access" checked={settings.lanAccess} onChange={lanAccess => patchSettings({ lanAccess }).catch(() => {})}/></div>
            <div className="setting-inline access-toggle"><div><strong>Allow Tailscale access</strong><small>Optional. Turn on when both devices use your Tailscale network.</small></div><Switch label="Allow Tailscale access" checked={settings.tailscaleAccess} onChange={tailscaleAccess => patchSettings({ tailscaleAccess }).catch(() => {})}/></div>
            <div className="pairing-setting">
              <div className="pairing-heading"><strong>Pairing protection</strong><span className={`pairing-state ${settings.pairingEnabled ? 'protected' : ''}`}><i/>{settings.pairingEnabled ? 'Protected' : 'Open access'}</span></div>
              <p>{settings.pairingEnabled ? 'Only people with the current QR link or pairing token can use phone controls.' : 'Turn this on to require a private token for phone control.'}</p>
              {settings.pairingEnabled ? <div className="pairing-actions"><button onClick={() => pairingAction('rotate')} disabled={busy}><Icon name="refresh" size={14}/> Rotate token</button><button className="quiet-danger" onClick={() => pairingAction('disable')} disabled={busy}>Turn off</button></div> : <div className="pairing-actions"><input aria-label="Optional pairing token" placeholder="Or enter your own token" value={pairDraft} onChange={event => setPairDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') pairingAction('enable'); }}/><button className="pairing-enable" onClick={() => pairingAction('enable')} disabled={busy}>Enable pairing</button></div>}
            </div>
            <details className="remote-guide">
              <summary>How remote access works</summary>
              <ol>
                <li><strong>Wi-Fi / LAN:</strong> connect your phone and PC to the same trusted Wi-Fi, then scan the LAN QR code.</li>
                <li><strong>Tailscale:</strong> install and sign in to Tailscale on both devices, enable access here, then open the Tailscale link.</li>
                <li><strong>Pairing:</strong> enable pairing to require the private link token. Without it, anyone who can reach the allowed network can control this board.</li>
              </ol>
              <p>No router port forwarding is needed. Pairing is an access gate, not encryption; use trusted Wi-Fi or Tailscale and never expose the port to the public internet.</p>
            </details>
            <button type="button" className="firewall-action" onClick={configureFirewall} disabled={busy}><span>Allow app in Windows Firewall</span><Icon name="arrow" size={14}/></button>
            <p className="firewall-note">Windows may ask for administrator approval. The rule is limited to this app and your local network{settings.tailscaleAccess ? ' and Tailscale' : ''}.</p>
          </section>
        </div>
        }
        {activeSection === 'updates' && <>
        <footer className="version-footer" aria-label="Application version and updates">
          <div className="version-information"><span>Version <strong>{appVersion ? `v${appVersion}` : '—'}</strong></span><span className={`version-message ${updateState}`} role="status" aria-live="polite">{updateMessage}</span></div>
          <div className="version-actions">
            {updateState === 'available' && releaseUrl && <button type="button" className="version-release" onClick={openRelease}>Review update</button>}
            <button type="button" className="version-check" onClick={checkForUpdates} disabled={checkingUpdates}>{checkingUpdates ? 'Checking…' : 'Check for updates'}</button>
          </div>
        </footer>
        {updateState === 'available' && releaseUrl && <section className="update-details" aria-label="Available update">
          <div><strong>{releaseName || `SimplySound ${latestVersion}`}</strong>{releasePublishedAt && <small>Published {new Date(releasePublishedAt).toLocaleDateString()}</small>}</div>
          <p>Updates are never installed automatically. Review the notes on the official GitHub release page, then choose the installer or portable download yourself.</p>
          {releaseAssets.length > 0 && <small className="update-assets">Available downloads: {releaseAssets.map(asset => /-Setup\.exe$/i.test(asset.name) ? 'Installer' : /-Windows\.exe$/i.test(asset.name) ? 'Portable' : asset.name).join(' · ')}</small>}
          {releaseNotes && <details><summary>Release notes</summary><pre>{releaseNotes}</pre></details>}
        </section>}
        <section className="about-panel"><span className="about-logo"><img src={ART} alt=""/></span><div><strong>SimplySound</strong><p>Open-source soundboard with a private phone controller.</p><small>Updates are checked automatically. Nothing installs until you choose it.</small></div><span className="beta-pill">EARLY BETA</span></section>
        </>}
      </>}
    </section>
      {toast && <div role="status" className="desktop-toast">{toast}</div>}
    </div>
  </main>;
}
