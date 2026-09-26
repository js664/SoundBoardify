# Product context

SimplySound is a self-hosted Windows soundboard with a desktop library and a phone controller for the same local network. Users import and organize sounds on the PC, then trigger them from either the desktop or a phone. Sound effects play through the user's chosen output; local monitoring is optional and has its own output choice. Both outputs follow the Windows default playback device until the user selects another one. VRChat and Steam Link are optional use cases, not product requirements.

The PC desktop app is the setup and library management surface. The phone browser is a fast remote control on the same private LAN or Tailscale network. Success means a first-time user can launch the app, open its phone link, import an audio file, and play it through the selected output. Keep audio routing explicit and never silently change Windows device defaults.

Product constraints: Windows desktop distribution, Electron UI with a .NET 8/NAudio audio service, WASAPI shared mode, ASP.NET Core Kestrel, WebSockets, SQLite, private-network-only access, persistent per-sound trims and behavior, single active sound, and clear diagnostics for unavailable devices. Prepare the source for public hosting; never claim a published open-source release unless it has actually been published with a license.
