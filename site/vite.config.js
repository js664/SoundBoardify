import { execFileSync } from 'node:child_process';
import { defineConfig } from 'vite';

const allowedHosts = [];

try {
  const status = JSON.parse(execFileSync('tailscale.exe', ['status', '--json'], {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 3000,
  }));
  const shortHostname = status.Self?.HostName?.toLowerCase();
  const hostname = status.Self?.DNSName?.replace(/\.$/, '');
  if (shortHostname) allowedHosts.push(shortHostname);
  if (hostname) allowedHosts.push(hostname);
} catch {
  // Tailscale is optional; Vite still allows localhost and IP addresses.
}

export default defineConfig({
  server: {
    allowedHosts,
  },
});
