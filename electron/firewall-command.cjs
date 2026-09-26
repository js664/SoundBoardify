'use strict';

function quotePowerShell(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

function buildFirewallCommand({ program, port, lanAccess, tailscaleAccess }) {
  if (typeof program !== 'string' || !program.trim() || /[\r\n]/.test(program)) throw new Error('The audio service path is unavailable. Restart SimplySound and try again.');
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('The SimplySound port is not ready yet.');
  const remoteAddresses = [];
  if (lanAccess === true) remoteAddresses.push("'LocalSubnet'");
  if (tailscaleAccess === true) remoteAddresses.push("'100.64.0.0/10'");
  if (remoteAddresses.length === 0) throw new Error('Enable Wi-Fi/LAN or Tailscale access before creating a firewall rule.');

  const group = quotePowerShell('SimplySound Web UI');
  const displayName = quotePowerShell('SimplySound Web UI');
  return [
    `Get-NetFirewallRule -Group ${group} -ErrorAction SilentlyContinue | Remove-NetFirewallRule`,
    `New-NetFirewallRule -DisplayName ${displayName} -Group ${group} -Direction Inbound -Action Allow -Protocol TCP -LocalPort ${port} -Program ${quotePowerShell(program)} -RemoteAddress @(${remoteAddresses.join(', ')}) -Profile Any -EdgeTraversalPolicy Block`,
  ].join('; ');
}

module.exports = { buildFirewallCommand };
