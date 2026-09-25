# Contributing

Thanks for helping improve Soundboardify. Bug reports, accessibility fixes, documentation, audio compatibility, and small focused pull requests are welcome.

## Before opening a pull request

- Describe the user problem and the behavior you changed.
- Run `dotnet test tests/SoundBoardify.Tests/SoundBoardify.Tests.csproj -c Release -r win-x64` on Windows.
- Run `npm ci` and `npm run dist` from `electron/` for changes to the packaged app or web UI.
- Do not commit build output (`bin/`, `obj/`, `Web/`, `dist-electron/`) or personal sound files and logs.
- Keep network access private by default for new transports, and never log pairing tokens or full paired URLs.
- Keep audio work on the existing serialized audio execution context and avoid blocking the playback callback.

Please include reproduction steps and Windows version, audio endpoint format, and logs with personal information removed for audio or device bugs.
