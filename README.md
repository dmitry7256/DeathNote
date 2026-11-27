# Death Note 3.04 — Civilization V multiplayer tracker

Windows C# + WPF app for tracking players, nicknames, bans, reviews and Civ V matches. Includes SQLite import/export, game module checking, and GitHub installer updates.

## Install

Download `DeathNote_Setup_3.04_x64.exe` from GitHub Releases. Close Death Note before running Setup. Do not run `INSTALL_FROM_SOURCE.bat` over an existing Inno Setup installation.

### Configure Civilization V integration

- **Settings → Check integrity** checks the SQLite database
- **Settings → Check modules** checks Lua/XML integration, logging and module versions
- **Choose game folder:** root of the Civilization V installation (`Steam → Civilization V → Manage → Browse local files`)
- **Choose config.ini:** under `Documents\My Games\Sid Meier's Civilization 5\config.ini` (or `OneDrive\Documents` on redirected profiles)
- Close the game, then run **Repair / install** only when Check modules reports a repair is needed

### Saved games and desyncs

In 3.04 a high-turn reload without a new lobby is recognized as a continuation only when the prior match is a qualifying crashed/unspecified one-on-one game, and opponent Steam ID, map, civilization, turns, and time window agree. Ambiguous cases are kept separate. Existing historical duplicates are not modified automatically.

### Player data

The SQLite database and backups live under `%LOCALAPPDATA%\DeathNote\data`. Use **Settings → Export database** before upgrades and **Settings → Import database** to restore a saved `.db`. Never upload personal databases to public GitHub repositories.

See `RELEASE_NOTES_3.04.md` for the patch details.
