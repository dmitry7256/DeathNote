# Death Note 3.04 — Continued Save / Desync Detection

### Fixes
- Recognize a confirmed **one-on-one saved game continuation** after a Civilization V desynchronization or crash
- A new high-turn START without a fresh lobby LAUNCH can reuse the existing unfinished/crashed match when the opponent Steam ID, map, civilization, adjacent turns and time window agree
- Preserve the original match start, lobby name, notes, player details and prior turns
- Prevent accidental merging into completed, deleted, manually edited or otherwise unrelated matches
- Keep match awards tied to a single match so wins/losses are not duplicated
- Add a regression self-test covering continuation, a single resulting loss, and a different opponent

### Note about already duplicated matches
Version 3.04 **does not automatically rewrite historical duplicate records**. If this problem already occurred, export your database, keep a backup and use a checked merged copy for that particular game.

### Windows installation
1. Build the application on Windows with `.NET 10 SDK` and `Inno Setup 6`, running `BUILD_INSTALLER.bat`
2. Run `DeathNote_Setup_3.04_x64.exe` instead of `INSTALL_FROM_SOURCE.bat` for an existing Inno Setup installation
3. Export your SQLite data before updating, and run **Settings → Check integrity** afterward
4. No **Repair / install** for Civilization V is required; Lua/XML/DDS assets are unchanged

Note: full WPF compilation and in-game desync reproduction were not performed in the artifact environment; validate on Windows before a public release.
