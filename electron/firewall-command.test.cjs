'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { buildFirewallCommand } = require('./firewall-command.cjs');

test('manual firewall command limits LAN access to the current app and port', () => {
  const command = buildFirewallCommand({ program: 'C:\\Apps\\SimplySound\\audio.exe', port: 6769, lanAccess: true, tailscaleAccess: false });
  assert.match(command, /-LocalPort 6769/);
  assert.match(command, /-Program 'C:\\Apps\\SimplySound\\audio\.exe'/);
  assert.match(command, /@\('LocalSubnet'\)/);
  assert.doesNotMatch(command, /100\.64\.0\.0\/10/);
});

test('Tailscale firewall access is included only when selected', () => {
  const command = buildFirewallCommand({ program: 'C:\\Apps\\SimplySound\\audio.exe', port: 6769, lanAccess: true, tailscaleAccess: true });
  assert.match(command, /'LocalSubnet', '100\.64\.0\.0\/10'/);
});

test('manual firewall command rejects missing access, unsafe paths, and invalid ports', () => {
  assert.throws(() => buildFirewallCommand({ program: 'audio.exe', port: 6769, lanAccess: false, tailscaleAccess: false }), /Enable Wi-Fi\/LAN or Tailscale/);
  assert.throws(() => buildFirewallCommand({ program: 'bad\npath', port: 6769, lanAccess: true, tailscaleAccess: false }), /path is unavailable/);
  assert.throws(() => buildFirewallCommand({ program: 'audio.exe', port: 80, lanAccess: true, tailscaleAccess: false }), /port is not ready/);
});
