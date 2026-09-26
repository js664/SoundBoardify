# Interface direction

Mode: Operate. The app is used during gaming, often at a glance in a dim room. Keep the interface dark, with high-contrast type, a clear readiness accent, and a muted red stop action.

The Electron desktop app is a focused, dark settings window rather than a second soundboard: a narrow sidebar switches between General, Audio, Phone access, and Updates. General groups the Wi-Fi QR code and selectable Wi-Fi or Tailscale phone link, followed by compact Playback and Connection settings. Use clear OS-style hierarchy, restrained blue actions, grouped surfaces, short purposeful transitions, and native window controls. Keep device names, IP addresses, ports, and connection status dynamic. The first-run guide uses the same dark and blue palette, with clear progress and calm firewall states.

The phone soundboard is a mobile-first artwork grid: every sound uses its chosen cover image or `assets/logo.png`, with its title at the bottom of the tile and editing behind a three-dot button. A flow-positioned sticky header keeps Add and Select visible without covering the tiles. Selection mode reveals bulk delete and per-tile checkmarks. The currently playing tile gains a lime outline and a small meter; tapping it again stops it. Editing uses a mobile bottom sheet and keeps trimming and playback behavior in expandable options.


The phone soundboard retains its established midnight-studio layout and styling, with its primary accent changed from lime to light blue. No layout, typography, spacing, or interaction changes are part of this accent update. DM Sans is bundled locally so the LAN interface loads without an external font service. Motion is brief and tied to tile entry, press feedback, playback, or editor presentation. Artwork shade is used only to keep button names readable. Color is reserved for readiness, playback, primary actions, and destructive actions.
