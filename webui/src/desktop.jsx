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
    phone: <><rect x="6" y="2.5" width="12" height="19" rx="2.5"/><path d="M10 18.5h4"/></>,
    settings: <><path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3" fill="currentColor" stroke="none"/><circle cx="15" cy="17" r="3" fill="currentColor" stroke="none"/></>,
    chevron: <path d="m9 18 6-6-6-6"/>,
    arrow: <><path d="M7 17 17 7M7 7h10v10"/></>,
  };
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>;
}
function Switch({ checked, onChange, label, disabled = false }) { return <button type="button" role="switch" aria-checked={checked} aria-label={label} aria-disabled={disabled} disabled={disabled} className={`desktop-switch ${checked ? 'on' : ''}`} onClick={() => onChange(!checked)}><i/></button>; }
function DevicePicker({ id, label, detail, value, currentName, devices, onChange, onRefreshDevices }) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const [refreshing, setRefreshing] = useState(false);
  const [refreshError, setRefreshError] = useState('');
  const triggerRef = useRef(null);
  const listRef = useRef(null);
  const wasOpenRef = useRef(false);
  const selected = devices.find(device => device.id === value);
  const ordered = useMemo(() => [...devices].sort((a, b) => Number(b.state === 'Active') - Number(a.state === 'Active') || a.name.localeCompare(b.name)), [devices]);
  const options = useMemo(() => [null, ...ordered], [ordered]);
  const selectedIndex = Math.max(0, options.findIndex(device => device?.id === value));
  const canSelect = device => !device || device.state === 'Active';
  const findSelectable = (from, direction) => {
    for (let offset = 0; offset < options.length; offset++) {
      const index = (from + direction * offset + options.length) % options.length;
      if (canSelect(options[index])) return index;
    }
    return 0;
  };
  useEffect(() => {
    if (!open) return;
    const close = event => { if (!event.target.closest(`[data-picker="${id}"]`)) setOpen(false); };
    document.addEventListener('pointerdown', close);
    return () => document.removeEventListener('pointerdown', close);
  }, [id, open]);
  useEffect(() => {
    if (!open) { wasOpenRef.current = false; return; }
    if (!wasOpenRef.current) {
      wasOpenRef.current = true;
      setActiveIndex(canSelect(options[selectedIndex]) ? selectedIndex : 0);
      requestAnimationFrame(() => listRef.current?.focus());
      return;
    }
    setActiveIndex(current => canSelect(options[current]) ? current : canSelect(options[selectedIndex]) ? selectedIndex : 0);
  }, [open, selectedIndex, options]);
  const choose = next => { onChange(next); setOpen(false); requestAnimationFrame(() => triggerRef.current?.focus()); };
  const toggleOpen = () => {
    if (open) { setOpen(false); return; }
    setOpen(true); setRefreshError(''); setRefreshing(true);
    Promise.resolve().then(onRefreshDevices).catch(() => setRefreshError('Couldn’t refresh devices. Showing the last available list.')).finally(() => setRefreshing(false));
  };
  const onListKeyDown = event => {
    if (event.key === 'Escape') {
      event.preventDefault(); setOpen(false); requestAnimationFrame(() => triggerRef.current?.focus());
      return;
    }
    let next = activeIndex;
    if (event.key === 'ArrowDown') next = findSelectable((activeIndex + 1) % options.length, 1);
    else if (event.key === 'ArrowUp') next = findSelectable((activeIndex - 1 + options.length) % options.length, -1);
    else if (event.key === 'Home') next = findSelectable(0, 1);
    else if (event.key === 'End') next = findSelectable(options.length - 1, -1);
    else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault(); if (canSelect(options[activeIndex])) choose(options[activeIndex]?.id ?? null); return;
    } else return;
    event.preventDefault(); setActiveIndex(next);
  };
  return <div className={`desktop-device ${open ? 'is-open' : ''}`} data-picker={id}>
    <div className="desktop-device-copy"><strong>{label}</strong><small>{detail}</small></div>
    <button ref={triggerRef} type="button" className="device-picker-trigger" aria-haspopup="listbox" aria-expanded={open} onClick={toggleOpen}>
      <span className={`device-led ${selected?.state === 'Active' || !value ? 'ready' : ''}`}/><span className="device-picker-value"><strong>{selected?.name || (value ? 'Selected device unavailable' : currentName || 'Windows default')}</strong><small>{selected ? `${selected.sampleRate ? `${Math.round(selected.sampleRate / 1000)} kHz · ` : ''}${selected.state}` : 'Follow Windows default'}</small></span><span className="device-chevron"><Icon name="chevron" size={16}/></span>
    </button>
    {open && <div className="device-picker-popover" aria-busy={refreshing}>
      {refreshing && <div className="device-picker-status" role="status">Checking connected audio devices…</div>}
      {refreshError && <div className="device-picker-status error" role="status">{refreshError}</div>}
      <div ref={listRef} className="device-picker-menu" role="listbox" tabIndex={0} aria-label={`${label} devices`} aria-activedescendant={`${id}-option-${activeIndex}`} onKeyDown={onListKeyDown}>
        {options.map((device, index) => {
          const isSelected = device ? value === device.id : !value;
          const unavailable = !canSelect(device);
          return <div id={`${id}-option-${index}`} role="option" aria-selected={isSelected} aria-disabled={unavailable} data-active={activeIndex === index} key={device?.id ?? 'default'} onMouseMove={() => { if (!unavailable) setActiveIndex(index); }} onClick={() => { if (!unavailable) choose(device?.id ?? null); }}>
            <span><strong>{device?.name ?? 'Windows default'}</strong><small>{device ? `${device.sampleRate ? `${Math.round(device.sampleRate / 1000)} kHz · ` : ''}${device.state}` : currentName || 'Follow your system playback device'}</small></span>{isSelected && <Icon name="check" size={15}/>}
          </div>;
        })}
      </div>
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
  const [openingSoundboard, setOpeningSoundboard] = useState(false);
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
  const [connectionChoice, setConnectionChoice] = useState('wifi');
  const [setupOpen, setSetupOpen] = useState(false);
  const [setupStep, setSetupStep] = useState(0);
  const [setupFirewallState, setSetupFirewallState] = useState('idle');
  const [setupError, setSetupError] = useState('');
  const [showManualCommand, setShowManualCommand] = useState(false);
  const [manualCommand, setManualCommand] = useState('');
  const [manualCopied, setManualCopied] = useState(false);
  const masterTimer = useRef(null);
  const micGainTimer = useRef(null);
  const welcomeDialogRef = useRef(null);

  async function refresh() {
    const [nextSettings, endpoints, state] = await Promise.all([request('/api/settings'), request('/api/audio/devices'), request('/api/status')]);
    setSettings(nextSettings); setPortDraft(String(nextSettings.port)); setDevices(endpoints || []); setStatus(state);
  }
  const refreshDevices = useCallback(async () => {
    const endpoints = await request('/api/audio/devices');
    setDevices(endpoints || []);
  }, []);
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
    const bridge = window.SimplySoundDesktop;
    if (!bridge?.isSetupComplete) return;
    let active = true;
    bridge.isSetupComplete().then(complete => { if (active && !complete) setSetupOpen(true); }).catch(() => {});
    return () => { active = false; };
  }, []);
  useEffect(() => {
    if (!setupOpen) return;
    const dialog = welcomeDialogRef.current;
    if (!dialog) return;
    const focusable = () => [...dialog.querySelectorAll('button:not(:disabled), textarea:not(:disabled)')];
    const first = dialog.querySelector('.welcome-next') || focusable()[0];
    first?.focus({ preventScroll: true });
    const trapFocus = event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); return; }
      if (event.key !== 'Tab') return;
      const items = focusable();
      if (!items.length) { event.preventDefault(); return; }
      if (event.shiftKey && document.activeElement === items[0]) { event.preventDefault(); items.at(-1).focus(); }
      else if (!event.shiftKey && document.activeElement === items.at(-1)) { event.preventDefault(); items[0].focus(); }
    };
    document.addEventListener('keydown', trapFocus, true);
    return () => document.removeEventListener('keydown', trapFocus, true);
  }, [setupOpen, setupStep]);
  useEffect(() => {
    const refreshHotkeys = () => window.SimplySoundDesktop?.getHotkeyStatus?.().then(setHotkeyStatus).catch(() => {});
    refreshHotkeys();
    const timer = setInterval(refreshHotkeys, 2500);
    return () => clearInterval(timer);
  }, []);
  useEffect(() => () => { clearTimeout(masterTimer.current); clearTimeout(micGainTimer.current); }, []);
  useEffect(() => { if (!toast) return; const timer = setTimeout(() => setToast(''), 3500); return () => clearTimeout(timer); }, [toast]);
  useEffect(() => window.SimplySoundDesktop?.onSoundboardLaunchError?.(message => setToast(message)), []);

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
  function changeMicOutputGain(value) {
    setSettings(current => ({ ...current, micOutputGain: value }));
    clearTimeout(micGainTimer.current);
    micGainTimer.current = setTimeout(() => patchSettings({ micOutputGain: value }).catch(() => {}), 100);
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
  async function configureFirewall(access = settings) {
    if (!window.SimplySoundDesktop?.configureFirewall) {
      setToast('Open this in the SimplySound desktop app to update Windows Firewall.');
      return false;
    }
    if (!access?.lanAccess && !access?.tailscaleAccess) {
      const message = 'Enable Wi-Fi/LAN or Tailscale access before creating a firewall rule.';
      if (setupOpen) setSetupError(message); else setToast(message);
      return false;
    }
    setBusy(true);
    setSetupError('');
    try {
      await window.SimplySoundDesktop.configureFirewall({ lanAccess: access.lanAccess, tailscaleAccess: access.tailscaleAccess });
      setSetupFirewallState('allowed');
      if (setupOpen) setSetupError(''); else setToast('Windows Firewall now allows SimplySound on this network.');
      return true;
    } catch (error) {
      const message = error.message || 'Windows Firewall permission was not granted.';
      setSetupFirewallState('denied');
      if (setupOpen) setSetupError(`${message} You can copy the command below and run it yourself in an Administrator PowerShell window.`);
      else setToast(message);
      return false;
    } finally { setBusy(false); }
  }
  async function updateSetupAccess(patch) {
    setBusy(true); setSetupError('');
    try {
      await patchSettings(patch);
      setSetupFirewallState('idle'); setManualCommand('');
    } catch (error) { setSetupError(error.message || 'Could not save network access settings.'); }
    finally { setBusy(false); }
  }
  async function loadManualCommand() {
    setShowManualCommand(true); setSetupError(''); setManualCopied(false);
    try {
      const command = await window.SimplySoundDesktop?.getFirewallCommand?.({ lanAccess: settings.lanAccess, tailscaleAccess: settings.tailscaleAccess });
      if (!command) throw new Error('The PowerShell command is not available yet. Restart SimplySound and try again.');
      setManualCommand(command);
    } catch (error) { setSetupError(error.message || 'Could not prepare the firewall command.'); }
  }
  async function copyManualCommand() {
    if (!manualCommand) return;
    try { await navigator.clipboard.writeText(manualCommand); setManualCopied(true); setSetupError(''); }
    catch { setSetupError('Copy was blocked. Select the command text, then press Ctrl+C.'); }
  }
  async function completeSetup() {
    try { await window.SimplySoundDesktop?.completeSetup?.(); setSetupOpen(false); setSetupError(''); }
    catch (error) { setSetupError(error.message || 'Could not save setup progress.'); }
  }
  function openSetup() {
    setSetupStep(0); setSetupOpen(true); setSetupError(''); setShowManualCommand(false); setManualCommand('');
  }
  async function openRelease() {
    try { await window.SimplySoundDesktop?.openRelease(releaseUrl); }
    catch { setToast('Could not open the GitHub release page.'); }
  }

  async function openSoundboard() {
    const bridge = window.SimplySoundDesktop;
    if (openingSoundboard) return;
    if (!serverReady) { setToast('The soundboard is still starting. Try again in a moment.'); return; }
    if (typeof bridge?.openSoundboard !== 'function') { setToast('Open the soundboard from the installed SimplySound desktop app.'); return; }
    setOpeningSoundboard(true);
    try {
      const result = await bridge.openSoundboard();
      if (!result?.url) throw new Error('Windows did not confirm opening the soundboard. Try again.');
      setToast(`Soundboard opened at ${new URL(result.url).host}.`);
    }
    catch (error) { setToast(error.message || 'Could not open the soundboard in your browser.'); }
    finally { setOpeningSoundboard(false); }
  }

  const appClass = 'desktop-app options-only';
  const wifiUrl = status?.wifiUrl || status?.url;
  const tailscaleUrl = status?.tailscaleUrl;
  const port = status?.activePort || settings?.port || 6769;
  const serverReady = status?.server === 'running';
  const outputReady = status?.audio?.startsWith('Connected');
  const readinessState = !serverReady ? 'starting' : outputReady ? 'ready' : 'attention';
  const readinessLabel = !serverReady ? 'Starting' : outputReady ? 'Ready to play' : 'Audio needs attention';
  const selectedConnection = connectionChoice === 'tailscale' && tailscaleUrl ? 'tailscale' : 'wifi';
  const selectedPhoneUrl = selectedConnection === 'tailscale' ? tailscaleUrl : wifiUrl;
  const updateMessage = {
    checking: 'Checking GitHub…',
    available: `v${latestVersion} is available`,
    current: 'You’re up to date',
    'no-release': 'No GitHub release yet',
    error: 'Could not reach GitHub',
    unavailable: 'Desktop version unavailable',
  }[updateState] || 'Check for updates';
  const navigation = [
    { id: 'overview', label: 'Overview', icon: 'settings' },
    { id: 'audio', label: 'Audio', icon: 'volume' },
    { id: 'network', label: 'Phone access', icon: 'phone' },
    { id: 'updates', label: 'Updates', icon: 'refresh' },
  ];
  const sectionCopy = {
    overview: ['Overview', 'Your soundboard, at a glance.'],
    audio: ['Audio', 'Choose where your sounds play.'],
    network: ['Phone access', 'Choose how your phone connects to this PC.'],
    updates: ['Updates', 'Check your version and review new releases.'],
  }[activeSection];
  return <main className={appClass}>
    <div className="desktop-window-strip" aria-hidden="true"/>
    <aside className="studio-sidebar" aria-label="Main navigation">
      <div className="desktop-brand"><img src={ART} alt=""/><div><strong>SimplySound</strong></div></div>
      <nav className="studio-navigation" aria-label="Settings sections">
        {navigation.map(item => <button key={item.id} type="button" className={`studio-nav-item ${activeSection === item.id ? 'active' : ''}`} aria-current={activeSection === item.id ? 'page' : undefined} onClick={() => setActiveSection(item.id)}><span className={`nav-symbol nav-symbol-${item.id}`}><Icon name={item.icon} size={16}/></span><span>{item.label}</span>{item.id === 'updates' && updateState === 'available' && <i className="nav-update-dot"/>}</button>)}
      </nav>
      <div className="sidebar-spacer"/>
      <button className="sidebar-setup" onClick={openSetup}><Icon name="phone" size={16}/><span>Setup guide</span><Icon name="chevron" size={13}/></button>
      <div className="sidebar-health"><i className={readinessState === 'ready' ? 'connected' : readinessState}/><span><strong>{serverReady ? 'Soundboard online' : 'Starting soundboard'}</strong><small>{outputReady ? 'Ready to play' : serverReady ? 'Audio needs attention' : 'Waiting for audio'}</small></span></div>
      <div className="sidebar-version"><span>Early Beta</span><strong>{appVersion ? `v${appVersion}` : ''}</strong></div>
    </aside>
    <div className="studio-main">
      <section className="control-layout" key={activeSection}>
      <div className={`control-heading ${activeSection === 'overview' ? 'is-overview' : ''}`}>
        <div className="heading-copy">
          <div className="heading-title-row">
            <h1>{sectionCopy[0]}</h1>
            {activeSection === 'overview' && <span className="readiness-indicator" data-state={readinessState} role="status" aria-live="polite"><i/><span>{readinessLabel}</span></span>}
          </div>
          {sectionCopy[1] && <p>{sectionCopy[1]}</p>}
        </div>
        {activeSection === 'overview' && <button type="button" className="open-soundboard" aria-label="Open soundboard in your browser" aria-disabled={openingSoundboard} disabled={openingSoundboard} onClick={openSoundboard}><span>{openingSoundboard ? 'Opening…' : 'Open soundboard'}</span><Icon name="arrow" size={14}/></button>}
      </div>
      {!settings ? <div className="desktop-loading">Connecting to SimplySound…</div> : <>
        {activeSection === 'overview' && <>
        <section className="connect-panel">
          <div className="qr-frame">{wifiUrl && !wifiUrl.includes('127.0.0.1') ? <img src={`/api/phone/qr?v=${qrVersion}`} alt="Scan with your phone for Wi-Fi/LAN access"/> : <div className="qr-unavailable"><Icon name="phone" size={28}/><span>{settings.lanAccess ? 'Waiting for Wi-Fi' : 'Wi-Fi access is off'}</span></div>}<span>Scan on your Wi-Fi</span></div>
          <div className="connect-main">
            <div className="connect-title"><h2>Your phone. Your remote.</h2><p>Scan the code to play sounds from your phone.</p></div>
            <div className={`connection-segment ${selectedConnection === 'tailscale' ? 'is-tailscale' : ''}`} aria-label="Phone link network"><span className="segment-selection"/><button type="button" aria-pressed={selectedConnection === 'wifi'} onClick={() => setConnectionChoice('wifi')}>Wi-Fi</button><button type="button" aria-pressed={selectedConnection === 'tailscale'} disabled={!tailscaleUrl} onClick={() => setConnectionChoice('tailscale')}>Tailscale</button></div>
            <div className="phone-link-field"><a href={selectedPhoneUrl || undefined} target="_blank" rel="noreferrer" title="Open phone soundboard">{selectedPhoneUrl || (settings.lanAccess ? 'Waiting for a network…' : 'Enable Wi-Fi in Phone access')}</a><button onClick={() => copy(selectedPhoneUrl, selectedConnection === 'wifi' ? 'Wi-Fi' : 'Tailscale')} disabled={!selectedPhoneUrl} aria-label="Copy phone link" title="Copy link"><Icon name="copy" size={15}/></button></div>
          </div>
        </section>
        <h2 className="group-label">Playback</h2>
        <div className="overview-summary" aria-label="Playback settings">
          <button type="button" className="summary-row" onClick={() => setActiveSection('audio')}><span className="summary-label"><Icon name="volume" size={17}/><span>Sound output</span></span><strong>{status?.endpoint || 'Windows default'}<Icon name="chevron" size={14}/></strong></button>
          <button type="button" className="summary-row" onClick={() => setActiveSection('audio')}><span className="summary-label"><Icon name="volume" size={17}/><span>Local monitoring</span></span><strong>{settings.monitorLocally ? status?.monitorEndpoint || 'Windows default' : 'Off'}<Icon name="chevron" size={14}/></strong></button>
        </div>
        <h2 className="group-label">Connection</h2>
        <div className="overview-summary" aria-label="Connection settings">
          <div className="summary-row port-row"><span>Web UI port</span>{editingPort ? <div className="port-edit"><input autoFocus type="number" min="1024" max="65535" value={portDraft} onChange={event => setPortDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') applyPort(); if (event.key === 'Escape') setEditingPort(false); }}/><button disabled={busy} onClick={applyPort}>{busy ? 'Saving…' : 'Apply'}</button></div> : <button className="port-value" onClick={() => { setPortDraft(String(settings.port)); setEditingPort(true); }} aria-label={`Change web UI port, currently ${port}`}><strong>{port}</strong><span>Edit</span></button>}</div>
          <button type="button" className="summary-row" onClick={() => setActiveSection('network')}><span>Network & privacy</span><strong>{settings.pairingEnabled ? 'Pairing enabled' : 'Manage access'}<Icon name="chevron" size={14}/></strong></button>
        </div>
        </>}
        {activeSection === 'audio' && <div className="settings-columns single-column">
          <section className="settings-section audio-section">
            <div className="section-heading"><h2>Output & monitoring</h2></div>
            <DevicePicker id="playback" label="Soundboard output" detail="Defaults to the Windows playback device." value={settings.endpointId || null} currentName={status?.endpoint} devices={devices} onChange={selectDevice} onRefreshDevices={refreshDevices}/>
            <div className="setting-divider"/>
            <div className="setting-inline"><div><strong>Hear sounds on this PC</strong><small>Play a local copy while sending audio to your output.</small></div><Switch label="Hear sounds on this PC" checked={settings.monitorLocally} onChange={monitorLocally => patchSettings({ monitorLocally }).catch(() => {})}/></div>
            {settings.monitorLocally && <DevicePicker id="monitor" label="Local playback" detail="Optional listening device; follows Windows by default." value={settings.monitorEndpointId || null} currentName={status?.monitorEndpoint} devices={devices} onChange={selectMonitorDevice} onRefreshDevices={refreshDevices}/>}
            <label className="master-level"><span><strong>Overall volume</strong><output>{Math.round(settings.masterVolume * 100)}%</output></span><input type="range" min="0" max="1" step=".01" value={settings.masterVolume} onChange={event => changeMasterVolume(Number(event.target.value))}/></label>
            <div className="setting-inline virtual-mic-headroom"><div><strong>Reduce level for a virtual microphone</strong><small>Use this if sound clips on a virtual mic. Local playback stays unchanged.</small></div><Switch label="Reduce level for a virtual microphone" checked={settings.useVirtualMicHeadroom} onChange={useVirtualMicHeadroom => patchSettings({ useVirtualMicHeadroom }).catch(() => {})}/></div>
            {settings.useVirtualMicHeadroom && <label className="master-level mic-output-level"><span><strong>Virtual microphone level</strong><output>{(settings.micOutputGain * 100).toFixed(1)}%</output></span><input type="range" min="0" max=".25" step=".005" value={settings.micOutputGain} onChange={event => changeMicOutputGain(Number(event.target.value))}/></label>}
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
            <div className="section-heading"><h2>Network access</h2></div>
            <button type="button" className="setup-guide-link" onClick={openSetup}><span><strong>Run the welcome setup again</strong><small>Review network choices and Windows Firewall access.</small></span><Icon name="arrow" size={15}/></button>
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
            <button type="button" className="firewall-action" onClick={() => configureFirewall(settings)} disabled={busy || (!settings.lanAccess && !settings.tailscaleAccess)}><span>Allow app in Windows Firewall</span><Icon name="arrow" size={14}/></button>
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
    {setupOpen && settings && <div className="welcome-overlay" role="presentation">
      <section ref={welcomeDialogRef} className="welcome-dialog" role="dialog" aria-modal="true" aria-labelledby="welcome-title" aria-describedby="welcome-description">
        <header className="welcome-header">
          <div className="welcome-brand"><img src={ART} alt=""/><span>SimplySound <small>QUICK SETUP</small></span></div>
          <button type="button" className="welcome-later" onClick={completeSetup}>Set up later</button>
        </header>
        <div className="welcome-progress" role="list" aria-label={`Setup progress, step ${setupStep + 1} of 4`}>
          {['Welcome', 'Networks', 'Firewall', 'Ready'].map((label, index) => <div key={label} role="listitem" aria-current={index === setupStep ? 'step' : undefined} className={`welcome-progress-step ${index === setupStep ? 'current' : ''} ${index < setupStep ? 'done' : ''}`}><i aria-hidden="true">{index < setupStep ? <Icon name="check" size={13}/> : String(index + 1).padStart(2, '0')}</i><span>{label}</span></div>)}
        </div>
        <div className="welcome-content" key={setupStep} aria-live="polite" aria-atomic="true">
          {setupStep === 0 && <div className="welcome-intro">
            <span className="welcome-hero-mark"><img src={ART} alt=""/><i/><i/><i/></span>
            <p className="welcome-eyebrow">YOUR SOUND. YOUR SPACE.</p>
            <h1 id="welcome-title">Let’s get your board ready.</h1>
            <p id="welcome-description" className="welcome-lead">A quick introduction to SimplySound: set up phone access, allow your chosen network through Windows Firewall, and you’re ready to play.</p>
            <div className="welcome-feature-line"><span><Icon name="volume" size={16}/> Sounds play through your chosen output</span><span><Icon name="lock" size={16}/> Phone access stays on private networks</span></div>
          </div>}
          {setupStep === 1 && <div className="welcome-page">
            <p className="welcome-eyebrow">STEP 02 · CONNECTIONS</p>
            <h1 id="welcome-title">Choose how your phone connects.</h1>
            <p id="welcome-description" className="welcome-lead">Wi-Fi works when your phone and PC share a network. Tailscale is optional for connecting from elsewhere.</p>
            <div className="welcome-choice-list">
              <div className="welcome-choice"><span className="welcome-choice-icon"><Icon name="arrow" size={17}/></span><span><strong>Wi-Fi / LAN</strong><small>Allow phones and computers on your local network.</small></span><Switch label="Allow Wi-Fi and LAN access" checked={settings.lanAccess} disabled={busy} onChange={lanAccess => updateSetupAccess({ lanAccess })}/></div>
              <div className="welcome-choice"><span className="welcome-choice-icon tailscale"><Icon name="lock" size={17}/></span><span><strong>Tailscale <em>OPTIONAL</em></strong><small>Use your private Tailscale network when you’re away.</small></span><Switch label="Allow Tailscale access" checked={settings.tailscaleAccess} disabled={busy} onChange={tailscaleAccess => updateSetupAccess({ tailscaleAccess })}/></div>
            </div>
            {settings.tailscaleAccess && <div className="welcome-hint"><Icon name="check" size={15}/><span>Install and sign in to Tailscale on both this PC and your phone. We’ll add its network to the firewall in the next step.</span></div>}
            {!settings.lanAccess && !settings.tailscaleAccess && <div className="welcome-hint needs-attention"><Icon name="lock" size={15}/><span>No phone network is enabled. Turn on Wi-Fi/LAN or Tailscale to use the phone controller.</span></div>}
          </div>}
          {setupStep === 2 && <div className="welcome-page">
            <p className="welcome-eyebrow">STEP 03 · WINDOWS FIREWALL</p>
            <h1 id="welcome-title">Let your phone reach the app.</h1>
            <p id="welcome-description" className="welcome-lead">Windows blocks new incoming connections by default. Add a narrowly scoped rule for SimplySound’s current port and the networks you selected.</p>
            <div className={`firewall-result ${setupFirewallState}`} aria-live="polite">
              <span className="firewall-result-icon"><Icon name={setupFirewallState === 'allowed' ? 'check' : 'lock'} size={17}/></span>
              <span><strong>{setupFirewallState === 'allowed' ? 'Firewall access is ready' : setupFirewallState === 'denied' ? 'Permission wasn’t granted' : 'One-time administrator approval'}</strong><small>TCP port {port} · {[settings.lanAccess && 'Wi-Fi / LAN', settings.tailscaleAccess && 'Tailscale'].filter(Boolean).join(' + ') || 'No networks selected'}</small></span>
              {setupFirewallState === 'allowed' && <Icon name="check" size={17}/>}
            </div>
            <button type="button" className="welcome-firewall-button" onClick={() => configureFirewall(settings)} disabled={busy || (!settings.lanAccess && !settings.tailscaleAccess)}><span>{busy ? 'Waiting for Windows…' : setupFirewallState === 'allowed' ? 'Update firewall rule' : 'Allow SimplySound in Windows Firewall'}</span><Icon name="arrow" size={15}/></button>
            <div className="manual-firewall">
              <button type="button" className="manual-firewall-toggle" aria-expanded={showManualCommand} aria-controls="manual-command-panel" onClick={() => showManualCommand ? setShowManualCommand(false) : loadManualCommand()}><span><strong>Prefer to run it yourself?</strong><small>Copy the command and paste it into PowerShell opened as Administrator.</small></span><b>{showManualCommand ? '−' : '+'}</b></button>
              {showManualCommand && <div className="manual-command-panel" id="manual-command-panel">
                {manualCommand ? <textarea aria-label="PowerShell firewall command" readOnly value={manualCommand} onFocus={event => event.target.select()} onClick={event => event.currentTarget.select()}/> : <div className="manual-command-loading">Preparing a command for port {port}…</div>}
                <button type="button" className="manual-copy-button" onClick={copyManualCommand} disabled={!manualCommand}>{manualCopied ? <><Icon name="check" size={14}/> Copied</> : <><Icon name="copy" size={14}/> Copy command</>}</button>
                <p>Open <strong>Windows PowerShell</strong> with <strong>Run as administrator</strong>, paste the command, then press Enter. It only allows the networks selected above.</p>
                {manualCopied && <button type="button" className="manual-confirm-button" onClick={() => { setSetupFirewallState('allowed'); setSetupError(''); }}>I ran the command</button>}
              </div>}
            </div>
          </div>}
          {setupStep === 3 && <div className="welcome-page welcome-ready">
            <span className="welcome-ready-mark"><Icon name="check" size={30}/></span>
            <p className="welcome-eyebrow">SETUP COMPLETE</p>
            <h1 id="welcome-title">You’re ready to play.</h1>
            <p id="welcome-description" className="welcome-lead">Your soundboard runs on this PC. Scan the QR code with your phone while both devices are on the same Wi-Fi.</p>
            <div className="welcome-finish-card">
              {settings.lanAccess && wifiUrl && !wifiUrl.includes('127.0.0.1') ? <img src={`/api/phone/qr?v=${qrVersion}`} alt="Wi-Fi QR code for the SimplySound phone controller"/> : <div className="welcome-qr-placeholder"><Icon name="arrow" size={20}/></div>}
              <div><strong>Phone soundboard</strong><small>{settings.lanAccess ? wifiUrl || 'Connect this PC to Wi-Fi to get your link.' : 'Wi-Fi access is off.'}</small>{settings.tailscaleAccess && <small className="welcome-tail-link">Tailscale: {tailscaleUrl || 'Start Tailscale to see your phone link.'}</small>}</div>
            </div>
            {setupFirewallState !== 'allowed' && <div className="welcome-hint needs-attention"><Icon name="lock" size={15}/><span>Windows Firewall still needs approval for phone access. You can finish now and allow it later in Phone access settings.</span></div>}
          </div>}
        </div>
        {setupError && <p className="welcome-error" role="alert">{setupError}</p>}
        <footer className="welcome-footer">
          <button type="button" className="welcome-back" onClick={() => { setSetupError(''); setSetupStep(step => Math.max(0, step - 1)); }} disabled={setupStep === 0 || busy}>Back</button>
          {setupStep < 3 ? <button type="button" className="welcome-next" onClick={() => { setSetupError(''); setSetupStep(step => Math.min(3, step + 1)); }} disabled={busy}>{setupStep === 0 ? 'Start setup' : setupStep === 2 && setupFirewallState !== 'allowed' ? 'Continue anyway' : 'Continue'}<Icon name="arrow" size={15}/></button> : <button type="button" className="welcome-next" onClick={completeSetup}>Open SimplySound<Icon name="arrow" size={15}/></button>}
        </footer>
      </section>
    </div>}
  </main>;
}
