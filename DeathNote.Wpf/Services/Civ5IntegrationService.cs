using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Complete native .NET Civilization V integration inspection and repair.
/// This class has no Python/PowerShell dependency and never touches the user's SQLite database.
/// Repair is an explicit user action; every original file is backed up before the first write.
/// </summary>
public static class Civ5IntegrationService
{
    private const string LobbyRelative = "Assets/UI/FrontEnd/Multiplayer";
    private const string GameFolderName = "Sid Meier's Civilization V";
    private const string RosterBegin = "-- CIV5_PLAYERS_BEGIN: read-only lobby roster";
    private const string RosterEnd = "-- CIV5_PLAYERS_END";
    private const string BadgeBegin = "-- DEATHNOTE_LOBBY_BEGIN";
    private const string BadgeEnd = "-- DEATHNOTE_LOBBY_END";
    private const string MatchBegin = "-- CIV5_TRACKER_INGAME_BEGIN";
    private const string MatchEnd = "-- CIV5_TRACKER_INGAME_END";
    private const string XmlBegin = "<!-- DEATHNOTE_BADGES_BEGIN -->";
    private const string XmlEnd = "<!-- DEATHNOTE_BADGES_END -->";
    private static readonly string[] Icons = ["favorite", "good", "warn", "bad", "autokick"];
    private static readonly string[] Processes = ["CivilizationV", "CivilizationV_DX11", "CivilizationV_Tablet"];
    private const string SourcePrefix = "DeathNote.Wpf.GameIntegration.";

    public sealed record Component(string Name, bool Ready, string Detail, bool Optional = false);
    public sealed record Inspection(string GameDirectory, string? ConfigFile, IReadOnlyList<Component> Components)
    {
        public bool Ready => Components.Count > 0 && Components.All(c => c.Ready || c.Optional);
        public string Summary => string.Join(Environment.NewLine,
            Components.Select(c => $"{(c.Ready ? "✓" : c.Optional ? "·" : "!")} {c.Name}: {c.Detail}"));
    }
    public sealed record RepairResult(int FilesChanged, string BackupDirectory);
    private sealed record Change(string Target, byte[] Bytes, string RelativeBackupPath);
    private sealed record ExistingFile(Change Change, byte[]? Before);

    public static bool IsGameRunning() => Processes.Any(name => Process.GetProcessesByName(name).Length != 0);

    public static string SavedGamePathFile => Path.Combine(App.Workspace, "civ5-install-path.txt");
    public static string SavedConfigPathFile => Path.Combine(App.Workspace, "civ5-config-path.txt");

    public static void SaveConfigPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !string.Equals(Path.GetFileName(full), "config.ini", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose Civilization V's config.ini file.");
        Directory.CreateDirectory(App.Workspace);
        File.WriteAllText(SavedConfigPathFile, full, new UTF8Encoding(false));
    }

    public static void SaveGamePath(string path)
    {
        string full = Path.GetFullPath(path);
        // Validate the installation ROOT, not the presence of a particular Lua file.
        // Steam verification or a broken installation may remove the script that
        // Check modules must be able to report as missing.
        if (!Directory.Exists(full) ||
            !Directory.Exists(Path.Combine(full, LobbyRelative.Replace('/', Path.DirectorySeparatorChar))))
            throw new DirectoryNotFoundException(
                "Choose the Civilization V installation folder containing Assets\\UI\\FrontEnd\\Multiplayer (not its Assets subfolder).");
        Directory.CreateDirectory(App.Workspace);
        File.WriteAllText(SavedGamePathFile, full, new UTF8Encoding(false));
    }

    public static string? DiscoverGameDirectory()
    {
        var candidates = new List<string>();
        if (File.Exists(SavedGamePathFile))
        {
            try { candidates.Add(File.ReadAllText(SavedGamePathFile).Trim()); }
            catch (IOException) { /* Fall back to Steam. */ }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath) candidates.Add(steamPath);
                if (key?.GetValue("InstallPath") is string installPath) candidates.Add(installPath);
            }
            catch (System.Security.SecurityException) { }
        }
        foreach (var programFolder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                                 Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrWhiteSpace(programFolder)) candidates.Add(Path.Combine(programFolder, "Steam"));
        var libraries = new List<string>(candidates);
        foreach (string root in candidates.ToArray())
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\\\"path\\\"\\s*\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (IOException) { }
        }
        foreach (string candidate in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string path in new[] { candidate, Path.Combine(candidate, "steamapps", "common", GameFolderName) })
            {
                try
                {
                    if (File.Exists(Path.Combine(path, LobbyRelative.Replace('/', Path.DirectorySeparatorChar), "StagingRoom.lua")))
                        return Path.GetFullPath(path);
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    public static string GetLogFilePath()
    {
        string folder = DiscoverConfigFile() is string config
            ? Path.GetDirectoryName(config)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", GameFolderName);
        return Path.Combine(folder, "Logs", "Lua.log");
    }

    public static string? DiscoverConfigFile()
    {
        if (File.Exists(SavedConfigPathFile))
        {
            try
            {
                string path = File.ReadAllText(SavedConfigPathFile).Trim();
                if (File.Exists(path)) return path;
            }
            catch (IOException) { }
        }
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { docs,
            Path.Combine(home, "Documents"),
            Path.Combine(home, "OneDrive", "Documents"),
            Path.Combine(home, "OneDrive", "Документы") }.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            string path = Path.Combine(dir, "My Games", GameFolderName, "config.ini");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static Inspection Inspect(string? selectedGamePath = null, string? selectedConfigPath = null)
    {
        string game = selectedGamePath ?? DiscoverGameDirectory() ?? "";
        string? config = selectedConfigPath ?? DiscoverConfigFile();
        var components = new List<Component>();
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
        {
            components.Add(new Component("Civilization V", false, "Game folder not found. Select the Civilization V installation folder."));
            return new Inspection(game, config, components);
        }
        string lobby = Path.Combine(game, LobbyRelative.Replace('/', Path.DirectorySeparatorChar));
        string lua = Path.Combine(lobby, "StagingRoom.lua");
        string xml = Path.Combine(lobby, "StagingRoom.xml");
        if (!File.Exists(lua) || !File.Exists(xml))
        {
            components.Add(new Component("Civilization V", false, "StagingRoom.lua/XML missing at the selected location."));
            return new Inspection(game, config, components);
        }
        try
        {
            string text = Decode(File.ReadAllBytes(lua)).Text;
            byte[] expectedLua = Asset("StagingRoom.lua");
            bool good = File.ReadAllBytes(lua).AsSpan().SequenceEqual(expectedLua);
            bool marked = text.Contains(RosterBegin, StringComparison.Ordinal) &&
                text.Contains(RosterEnd, StringComparison.Ordinal) &&
                text.Contains(BadgeBegin, StringComparison.Ordinal) &&
                text.Contains(BadgeEnd, StringComparison.Ordinal) &&
                text.Contains("DN_Bind(", StringComparison.Ordinal);
            components.Add(new Component("Lobby Lua / player tracking", good,
                good ? "Installed (current version)" : marked ? "Older/modified integration — reinstall available" : "Original game file — integration missing"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            components.Add(new Component("Lobby Lua / player tracking", false, e.Message));
        }
        try
        {
            string markup = Decode(File.ReadAllBytes(xml)).Text;
            int badgeGroups = Regex.Matches(markup, Regex.Escape(XmlBegin)).Count;
            bool ready = badgeGroups > 0 &&
                Regex.Matches(markup, Regex.Escape(XmlEnd)).Count == badgeGroups &&
                Enumerable.Range(1, 3).All(i => Regex.Matches(markup,
                    "<Image\\b(?=[^>]*\\bID=\"DNBadge" + i + "\")(?=[^>]*\\bSize=\"16,16\")[^>]*>",
                    RegexOptions.IgnoreCase).Count == badgeGroups) &&
                markup.Contains("ID=\"DNBadgeStrip\"", StringComparison.Ordinal) &&
                markup.Contains("Size=\"54,22\" NoClip=\"1\"", StringComparison.Ordinal) &&
                markup.IndexOf("PlayerNameLabel", StringComparison.Ordinal) < markup.IndexOf(XmlBegin, StringComparison.Ordinal);
            components.Add(new Component("Lobby XML / badge slots", ready,
                ready ? "Three 16px badges in a dedicated overlay strip" : "Badge layout missing or outdated — repair available"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            components.Add(new Component("Lobby XML / badge slots", false, e.Message));
        }
        foreach (string name in Icons)
        {
            string filename = "DN_" + name + ".dds";
            string target = Path.Combine(lobby, filename);
            bool exists = File.Exists(target);
            bool ready = false;
            if (exists)
            {
                byte[] installed = File.ReadAllBytes(target);
                ready = HasCorrectBadgeDimensions(installed) &&
                    installed.AsSpan().SequenceEqual(Asset("lobby_icons." + filename));
            }
            components.Add(new Component("Icon " + name, ready, ready ? "Installed" : exists ? "Older version" : "Missing"));
        }
        string[] gameScripts = InGameFiles(game).ToArray();
        if (gameScripts.Length == 0)
            components.Add(new Component("Match tracking (InGame.lua)", false, "No InGame.lua found under Assets/UI or Assets/DLC"));
        else
        {
            // The embedded Lua uses LF, while stock Civilization V InGame.lua files
            // commonly use Windows CRLF. Repair preserves each game's line endings;
            // comparing raw strings incorrectly reported 0/N installed scripts.
            string addon = NormalizeLuaLineEndings(Decode(Asset("MatchTracker.lua")).Text);
            int good = gameScripts.Count(p =>
            {
                string source = Decode(File.ReadAllBytes(p)).Text;
                int start = source.IndexOf(MatchBegin, StringComparison.Ordinal);
                int end = source.IndexOf(MatchEnd, StringComparison.Ordinal);
                return start >= 0 && end > start &&
                    string.Equals(
                        NormalizeLuaLineEndings(source.Substring(start, end + MatchEnd.Length - start)),
                        addon, StringComparison.Ordinal);
            });
            components.Add(new Component("Match tracking (InGame.lua)", good == gameScripts.Length,
                $"{good}/{gameScripts.Length} scripts installed"));
        }
        if (config is null || !File.Exists(config))
            components.Add(new Component("Game logging (config.ini)", false, "File not found; start Civ V once or choose the config.ini file"));
        else
        {
            string body = Decode(File.ReadAllBytes(config)).Text;
            bool valid = new[] { "LoggingEnabled", "EnableTuner", "EnableLuaDebugLibrary" }.All(k =>
                Regex.IsMatch(body, @"(?im)^\s*" + Regex.Escape(k) + @"\s*=\s*1\s*(?:[;#].*)?$"));
            components.Add(new Component("Game logging (config.ini)", valid,
                valid ? "Lua logging enabled" : "Required logging options are disabled"));
        }
        string? log = config is null ? null : Path.Combine(Path.GetDirectoryName(config)!, "Logs", "Lua.log");
        if (log is not null && File.Exists(log))
        {
            try
            {
                string tail = ReadLogTail(log);
                bool lobbyEvent = tail.Contains("[CIV5_PLAYERS]", StringComparison.Ordinal);
                bool matchEvent = tail.Contains("[CIV5_MATCH]", StringComparison.Ordinal);
                components.Add(new Component("Runtime signals (Lua.log)", lobbyEvent && matchEvent,
                    $"{(lobbyEvent ? "Lobby seen" : "Lobby not seen")}, {(matchEvent ? "match seen" : "match not seen")}. Requires entering a game to verify live operation.", true));
            }
            catch (IOException e) { components.Add(new Component("Runtime signals (Lua.log)", false, e.Message, true)); }
        }
        else components.Add(new Component("Runtime signals (Lua.log)", false, "No log yet; open Civ V lobby after installing", true));
        return new Inspection(game, config, components);
    }

    private static string ReadLogTail(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(-Math.Min(f.Length, 256 * 1024), SeekOrigin.End);
        using var reader = new StreamReader(f, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> InGameFiles(string game)
    {
        foreach (string area in new[] { "UI", "DLC" })
        {
            var root = Path.Combine(game, "Assets", area);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "InGame.lua", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string NormalizeLuaLineEndings(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static byte[] Asset(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SourcePrefix + name)
            ?? throw new FileNotFoundException("Bundled native integration resource missing: " + name);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed record EncodedText(string Text, Encoding Encoding, byte[] Bom)
    {
        public byte[] Encode(string updated)
        {
            byte[] body = Encoding.GetBytes(updated);
            if (Bom.Length == 0) return body;
            byte[] result = new byte[Bom.Length + body.Length];
            Bom.CopyTo(result, 0);
            body.CopyTo(result, Bom.Length);
            return result;
        }
    }
    private static EncodedText Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new EncodedText(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new EncodedText(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, [0xFF, 0xFE]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new EncodedText(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, [0xFE, 0xFF]);
        // Civ V stock Lua/config frequently uses an 8-bit encoding. Latin1 round-trips unchanged bytes.
        return new EncodedText(Encoding.Latin1.GetString(bytes), Encoding.Latin1, []);
    }


    // Civ V's legacy UI can show only part of a non-power-of-two DDS.
    // Use native 16x16 power-of-two DDS and identical Image.Size dimensions.
    private static bool HasCorrectBadgeDimensions(byte[] dds) =>
        dds.Length >= 128 && dds[0] == (byte)'D' && dds[1] == (byte)'D' &&
        dds[2] == (byte)'S' && dds[3] == (byte)' ' &&
        BitConverter.ToInt32(dds, 12) == 16 && BitConverter.ToInt32(dds, 16) == 16;

    private static byte[] PatchXml(byte[] old)
    {
        var encoded = Decode(old);
        string xml = Regex.Replace(encoded.Text, @"(?s)<!-- DEATHNOTE_BADGES_BEGIN -->.*?<!-- DEATHNOTE_BADGES_END -->\s*", "");
        // Place the badges in their own container. Earlier revisions made each Image
        // a direct sibling of the name Label: parts of the sprites were obscured by
        // the label's render surface on several Civ V layouts and resolutions.
        const string controls = "<!-- DEATHNOTE_BADGES_BEGIN -->\n" +
            "<Container ID=\"DNBadgeStrip\" Anchor=\"L,C\" Offset=\"0,0\" Size=\"54,22\" NoClip=\"1\" Hidden=\"1\">\n" +
            "<Image ID=\"DNBadge1\" Anchor=\"L,C\" Offset=\"0,0\" Size=\"16,16\" NoClip=\"1\" TextureOffset=\"0,0\" Texture=\"DN_favorite.dds\" Hidden=\"1\" />\n" +
            "<Image ID=\"DNBadge2\" Anchor=\"L,C\" Offset=\"18,0\" Size=\"16,16\" NoClip=\"1\" TextureOffset=\"0,0\" Texture=\"DN_favorite.dds\" Hidden=\"1\" />\n" +
            "<Image ID=\"DNBadge3\" Anchor=\"L,C\" Offset=\"36,0\" Size=\"16,16\" NoClip=\"1\" TextureOffset=\"0,0\" Texture=\"DN_favorite.dds\" Hidden=\"1\" />\n" +
            "</Container>\n" +
            "<!-- DEATHNOTE_BADGES_END -->\n";
        int found = 0;
        // The game draws later XML siblings on top of earlier ones. Insert AFTER
        // PlayerNameLabel so its backing surface cannot cover badge pixels.
        string patched = Regex.Replace(xml,
            "<Label\\b(?=[^>]*\\bID\\s*=\\s*['\"]PlayerNameLabel['\"])[^>]*/\\s*>",
            m => { found++; return m.Value + "\n" + controls; }, RegexOptions.IgnoreCase);
        if (found == 0)
        {
            patched = Regex.Replace(xml,
                "(?s)<Label\\b(?=[^>]*\\bID\\s*=\\s*['\"]PlayerNameLabel['\"])[^>]*>.*?</Label\\s*>",
                m => { found++; return m.Value + "\n" + controls; }, RegexOptions.IgnoreCase);
        }
        if (found == 0) throw new InvalidDataException("StagingRoom.xml has no PlayerNameLabel. Repair stopped without changes.");
        XDocument.Parse(patched);
        return encoded.Encode(patched);
    }

    private static byte[] PatchGameScript(byte[] original, byte[] addonBytes)
    {
        var old = Decode(original);
        string content = old.Text;
        string addon = Decode(addonBytes).Text.Trim();
        int a = content.IndexOf(MatchBegin, StringComparison.Ordinal);
        int b = content.IndexOf(MatchEnd, StringComparison.Ordinal);
        if ((a == -1) != (b == -1)) throw new InvalidDataException("Incomplete InGame.lua integration markers. No files changed.");
        if (a >= 0)
        {
            if (b <= a || content.IndexOf(MatchBegin, a + 1, StringComparison.Ordinal) >= 0 ||
                content.IndexOf(MatchEnd, b + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Multiple or malformed InGame.lua tracker blocks. No files changed.");
            content = content[..a] + content[(b + MatchEnd.Length)..];
        }
        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return old.Encode(content.TrimEnd() + newline + newline + addon.Replace("\r\n", "\n").Replace("\n", newline) + newline);
    }

    private static byte[] PatchConfig(byte[] old)
    {
        var enc = Decode(old);
        string result = enc.Text;
        if (!Regex.IsMatch(result, @"(?im)^\s*\[Config\]\s*$"))
            throw new InvalidDataException("config.ini lacks [Config]. Repair stopped without changes.");
        string newline = result.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (string key in new[] { "LoggingEnabled", "EnableTuner", "EnableLuaDebugLibrary" })
        {
            string pattern = @"(?im)^(?<before>\s*" + Regex.Escape(key) + @"\s*=\s*)[^;#\r\n]*(?<comment>[;#][^\r\n]*)?$";
            if (Regex.IsMatch(result, pattern))
                result = Regex.Replace(result, pattern, m => m.Groups["before"].Value + "1 " + m.Groups["comment"].Value);
            else
            {
                // Insert into Config section rather than into an unrelated section.
                result = Regex.Replace(result, @"(?im)^\s*\[Config\]\s*$", m => m.Value + newline + key + " = 1", RegexOptions.None, TimeSpan.FromSeconds(1));
            }
        }
        return enc.Encode(result);
    }

    private static List<Change> Prepare(string game, string config)
    {
        string lobby = Path.Combine(game, LobbyRelative.Replace('/', Path.DirectorySeparatorChar));
        string lua = Path.Combine(lobby, "StagingRoom.lua");
        string xml = Path.Combine(lobby, "StagingRoom.xml");
        if (!File.Exists(lua) || !File.Exists(xml) || !File.Exists(config))
            throw new FileNotFoundException("Game lobby Lua/XML or config.ini is missing. Repair was not started.");
        string currentLua = Decode(File.ReadAllBytes(lua)).Text;
        // Refuse unexpected game variants rather than replacing an arbitrary script.
        foreach (string anchor in new[] { "function OnGameLaunched()", "function RefreshPlayerList()", "function ShowHideHandler(", "PlayerNameLabel" })
            if (!currentLua.Contains(anchor, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported StagingRoom.lua variant (missing " + anchor + "). No files changed.");
        byte[] luaAsset = Asset("StagingRoom.lua");
        byte[] xmlAsset = PatchXml(File.ReadAllBytes(xml));
        var targets = new List<Change>
        {
            new(lua, luaAsset, "game/StagingRoom.lua"),
            new(xml, xmlAsset, "game/StagingRoom.xml")
        };
        foreach (string name in Icons)
        {
            string filename = "DN_" + name + ".dds";
            targets.Add(new Change(Path.Combine(lobby, filename), Asset("lobby_icons." + filename), "game/" + filename));
        }
        var inGame = InGameFiles(game).ToArray();
        if (inGame.Length == 0) throw new InvalidDataException("Cannot locate InGame.lua in Civ V Assets/UI or Assets/DLC.");
        byte[] addon = Asset("MatchTracker.lua");
        foreach (string path in inGame)
        {
            string relative = Path.GetRelativePath(game, path);
            targets.Add(new Change(path, PatchGameScript(File.ReadAllBytes(path), addon), "game/" + relative));
        }
        targets.Add(new Change(config, PatchConfig(File.ReadAllBytes(config)), "settings/config.ini"));
        return targets;
    }

    public static RepairResult Repair(string game, string? configFile)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close Civilization V before repairing game modules.");
        if (string.IsNullOrWhiteSpace(configFile)) throw new FileNotFoundException("config.ini not found. Start Civ V once, close it, and select its config.ini.");
        game = Path.GetFullPath(game);
        string config = Path.GetFullPath(configFile);
        List<Change> planned = Prepare(game, config); // Validate everything BEFORE writing anything.
        var changed = new List<ExistingFile>();
        foreach (var c in planned)
        {
            byte[]? before = File.Exists(c.Target) ? File.ReadAllBytes(c.Target) : null;
            if (before is not null && before.AsSpan().SequenceEqual(c.Bytes)) continue;
            changed.Add(new ExistingFile(c, before));
        }
        if (changed.Count == 0) return new RepairResult(0, "No changes necessary");
        string backup = Path.Combine(App.Workspace, "integration-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(backup);
        // Backups include a manifest of added files so a future manual rollback is possible.
        foreach (var f in changed)
        {
            string dest = Path.Combine(backup, f.Change.RelativeBackupPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (f.Before is not null) File.WriteAllBytes(dest, f.Before);
        }
        File.WriteAllLines(Path.Combine(backup, "CHANGED_FILES.txt"), changed.Select(f =>
            (f.Before is null ? "NEW " : "MODIFIED ") + f.Change.Target));
        var applied = new List<ExistingFile>();
        try
        {
            foreach (var f in changed)
            {
                string temp = f.Change.Target + ".deathnote-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temp, f.Change.Bytes);
                    // Rename over target on the same volume (no partially written Lua/XML).
                    File.Move(temp, f.Change.Target, overwrite: true);
                    applied.Add(f);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        catch (Exception original)
        {
            var errors = new List<string>();
            foreach (var f in Enumerable.Reverse(applied))
            {
                try
                {
                    if (f.Before is null) File.Delete(f.Change.Target);
                    else File.WriteAllBytes(f.Change.Target, f.Before);
                }
                catch (Exception rollback) { errors.Add(f.Change.Target + ": " + rollback.Message); }
            }
            if (errors.Count > 0)
                throw new IOException("Repair failed; rollback incomplete. Backups: " + backup + "\n" + string.Join("\n", errors), original);
            throw new IOException("Repair failed; all applied changes rolled back. Backups: " + backup, original);
        }
        return new RepairResult(changed.Count, backup);
    }
}
