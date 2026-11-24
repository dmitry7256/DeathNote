using System.Text;

namespace DeathNote.Wpf.Services;

/// <summary>Exercises the real native repair against disposable game/config files; never touches a Steam install.</summary>
public static class Civ5RepairSelfTest
{
    public static async Task RunAsync(string? reportFile)
    {
        string root = Path.Combine(Path.GetTempPath(), "DeathNote-native-repair-test-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "Sid Meier's Civilization V");
        string lobby = Path.Combine(game, "Assets", "UI", "FrontEnd", "Multiplayer");
        string inGame = Path.Combine(game, "Assets", "UI", "InGame.lua");
        string[] allGameScripts = [
            inGame,
            Path.Combine(game, "Assets", "UI", "Other", "InGame.lua"),
            Path.Combine(game, "Assets", "DLC", "Expansion", "InGame.lua"),
            Path.Combine(game, "Assets", "DLC", "Expansion2", "InGame.lua")
        ];
        string config = Path.Combine(root, "My Games", "Sid Meier's Civilization 5", "config.ini");
        string report = "DEATH NOTE 3.02 NATIVE GAME INTEGRATION SELF-TEST\n";
        Directory.CreateDirectory(lobby);
        Directory.CreateDirectory(Path.GetDirectoryName(inGame)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(lobby, "StagingRoom.lua"),
                "function OnGameLaunched() end\nfunction RefreshPlayerList() end\nfunction ShowHideHandler() end\n-- PlayerNameLabel\n");
            await File.WriteAllTextAsync(Path.Combine(lobby, "StagingRoom.xml"),
                "<Root><Container ID=\"SlotRoot\" Size=\"280,36\"><Container ID=\"PlayerNameBox\" Size=\"180,26\"><Label ID=\"PlayerNameLabel\" /></Container></Container></Root>");
            for (int i = 0; i < allGameScripts.Length; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(allGameScripts[i])!);
                await File.WriteAllTextAsync(allGameScripts[i],
                    i == 3 ? "-- stock script with LF\n" : "-- stock script with Windows CRLF\r\n");
            }
            await File.WriteAllTextAsync(config,
                "[Config]\nLoggingEnabled = 0\nEnableTuner = 0\nEnableLuaDebugLibrary = 0\n");
            var before = Civ5IntegrationService.Inspect(game, config);
            if (before.Ready) throw new InvalidOperationException("Missing original integration was not detected.");
            var repaired = Civ5IntegrationService.Repair(game, config);
            if (repaired.FilesChanged < 6) throw new InvalidOperationException("Repair did not install every module.");
            var after = Civ5IntegrationService.Inspect(game, config);
            if (!after.Ready) throw new InvalidOperationException("Installed integration did not pass inspection: " + after.Summary);
            string stockXml = await File.ReadAllTextAsync(Path.Combine(lobby,"StagingRoom.xml"));
            if(stockXml.Contains("DNBadgeStrip",StringComparison.Ordinal) ||
               stockXml.Contains("DEATHNOTE_BADGES_BEGIN",StringComparison.Ordinal))
                throw new InvalidOperationException("Lobby badges must not be installed.");
            string newLobbyLua=await File.ReadAllTextAsync(Path.Combine(lobby,"StagingRoom.lua"));
            if(newLobbyLua.Contains("DN_PaintName",StringComparison.Ordinal) ||
                !newLobbyLua.Contains("SetToolTipString(tip)",StringComparison.Ordinal))
                throw new InvalidOperationException("Tooltip-only lobby tracking was not installed.");
            // Simulate upgrading an older badge-enabled integration.
            string legacyXml=stockXml.Replace("</Root>",
                "<!-- DEATHNOTE_BADGES_BEGIN --><Container ID=\"DNBadgeStrip\" />"+
                "<!-- DEATHNOTE_BADGES_END --></Root>",StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(lobby,"StagingRoom.xml"),legacyXml);
            string obsoletePath=Path.Combine(lobby,"DN_good.dds");
            await File.WriteAllBytesAsync(obsoletePath,[1,2,3,4]);
            if(Civ5IntegrationService.Inspect(game,config).Ready)
                throw new InvalidOperationException("Obsolete lobby badges were not detected.");
            var cleanup=Civ5IntegrationService.Repair(game,config);
            if(cleanup.FilesChanged<2 || File.Exists(obsoletePath) ||
                !Civ5IntegrationService.Inspect(game,config).Ready)
                throw new InvalidOperationException("Old lobby badges were not removed.");
            if(!File.Exists(Path.Combine(cleanup.BackupDirectory,"game","DN_good.dds")))
                throw new InvalidOperationException("Removed DDS was not backed up.");
            var scriptsStatus = after.Components.Single(c => c.Name == "Match tracking (InGame.lua)");
            if (!scriptsStatus.Ready || scriptsStatus.Detail != "4/4 scripts installed")
                throw new InvalidOperationException("Mixed CRLF/LF scripts were incorrectly reported: " + scriptsStatus.Detail);
            // Verify the inspector still catches an actually missing tracker.
            await File.WriteAllTextAsync(allGameScripts[1], "-- tracker removed by Steam Verify\r\n");
            var partial = Civ5IntegrationService.Inspect(game, config);
            var partialStatus = partial.Components.Single(c => c.Name == "Match tracking (InGame.lua)");
            if (partialStatus.Ready || partialStatus.Detail != "3/4 scripts installed")
                throw new InvalidOperationException("Partial tracker installation was not detected: " + partialStatus.Detail);
            if (!Civ5IntegrationService.Inspect(game, config).Ready)
            {
                Civ5IntegrationService.Repair(game, config);
                if (!Civ5IntegrationService.Inspect(game, config).Ready)
                    throw new InvalidOperationException("Partial tracker restoration failed.");
            }
            if (Civ5IntegrationService.Repair(game, config).FilesChanged != 0)
                throw new InvalidOperationException("Repeat repair was not idempotent.");
            if (!File.Exists(Path.Combine(repaired.BackupDirectory, "game", "StagingRoom.lua")))
                throw new InvalidOperationException("Original Lua backup is missing.");
            if (!File.Exists(Path.Combine(repaired.BackupDirectory, "settings", "config.ini")))
                throw new InvalidOperationException("Original config.ini backup is missing.");
            // Simulate Steam Verify replacing the two main original game files.
            await File.WriteAllTextAsync(Path.Combine(lobby, "StagingRoom.lua"),
                "function OnGameLaunched() end\nfunction RefreshPlayerList() end\nfunction ShowHideHandler() end\n-- PlayerNameLabel\n");
            await File.WriteAllTextAsync(inGame, "-- Steam stock InGame.lua\n");
            if (Civ5IntegrationService.Inspect(game, config).Ready)
                throw new InvalidOperationException("Steam verification regression was not detected.");
            var restored = Civ5IntegrationService.Repair(game, config);
            if (restored.FilesChanged < 2 || !Civ5IntegrationService.Inspect(game, config).Ready)
                throw new InvalidOperationException("Integration could not recover after Steam verify.");
            // Fail safely for unsupported game builds; don't change files when validation fails.
            await File.WriteAllTextAsync(Path.Combine(lobby, "StagingRoom.lua"), "-- invalid game script\n");
            byte[] unsupported = await File.ReadAllBytesAsync(Path.Combine(lobby, "StagingRoom.lua"));
            bool rejected = false;
            try { Civ5IntegrationService.Repair(game, config); }
            catch (InvalidDataException) { rejected = true; }
            // Complete the asynchronous read before comparing the byte arrays.
            // A ReadOnlySpan<byte> must not live across an await (CS4007).
            byte[] afterRejectedRepair = await File.ReadAllBytesAsync(Path.Combine(lobby, "StagingRoom.lua"));
            if (!rejected || !unsupported.SequenceEqual(afterRejectedRepair))
                throw new InvalidOperationException("Unsupported game build was not rejected without modification.");
            report += "PASS: no lobby badges, tooltip-only behavior, old XML/DDS cleanup with backups.\n"+
                "PASS: 4/4 mixed CRLF/LF InGame.lua scripts detected correctly.\n"+
                "PASS: a missing tracker is reported as 3/4 and can be restored.\n"+
                "PASS: stock game detected as missing integration.\n"+
                "PASS: Lua tooltip/auto-kick, XML cleanup, InGame.lua and config.ini installed.\n"+
                "PASS: backup, repeat run idempotence, recovery after Steam verify.\n"+
                "PASS: unsupported game build rejected without modification.\n";
        }
        catch(Exception error)
        {
            report += "FAIL: " + error + "\n";
        }
        finally
        {
            try { Directory.Delete(root, true); } catch(IOException) { }
        }
        var path = reportFile ?? Path.Combine(App.Workspace, "NATIVE_REPAIR_TEST_REPORT.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, report, Encoding.UTF8);
    }
}
