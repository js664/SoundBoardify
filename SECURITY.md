# Security policy

SimplySound is intended for private LAN use. It does not provide public Internet hosting, account management, or end-to-end encryption. Tailscale support is optional and disabled by default. Pairing protection should be enabled when other people or devices can reach the local network.

The optional Firewall helper requests administrator permission and creates a TCP inbound rule for the installed audio service, the current Web UI port, and local subnet addresses. When Tailscale access is enabled, it also allows the Tailscale IPv4 range. The helper does not configure router port forwarding.

## Reporting a vulnerability

Please do not open a public issue containing an exploitable vulnerability or private pairing link. Use GitHub's private vulnerability reporting for the repository when it is enabled. If private reporting is unavailable, contact the repository maintainer privately and include affected versions, impact, and reproduction steps. Do not include sound files, pairing tokens, or personal network details unless required; redact them first.
