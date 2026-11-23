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
        string report = "DEATH NOTE 3.00 NATIVE GAME INTEGRATION SELF-TEST\n";
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
            if (repaired.FilesChanged < 9) throw new InvalidOperationException("Repair did not install every module.");
            var after = Civ5IntegrationService.Inspect(game, config);
            if (!after.Ready) throw new InvalidOperationException("Installed integration did not pass inspection: " + after.Summary);
            string badgeXml = await File.ReadAllTextAsync(Path.Combine(lobby, "StagingRoom.xml"));
            if (badgeXml.Split("Size=\"16,16\"").Length - 1 != 3 ||
                !badgeXml.Contains("ID=\"DNBadgeStrip\"", StringComparison.Ordinal) ||
                !badgeXml.Contains("</Container>", StringComparison.Ordinal) ||
                badgeXml.IndexOf("PlayerNameLabel", StringComparison.Ordinal) >
                badgeXml.IndexOf("<!-- DEATHNOTE_BADGES_BEGIN -->", StringComparison.Ordinal))
                throw new InvalidOperationException("Lobby badge size or paint order not installed.");
            // Prevent the earlier bug: a 32x32 DDS with an 18x18 XML image shows
            // only a partial top-left region in Civilization V, NOT a scaled icon.
            foreach (string badge in new[] { "favorite", "good", "warn", "bad", "autokick" })
            {
                string texturePath = Path.Combine(lobby, "DN_" + badge + ".dds");
                byte[] dds = await File.ReadAllBytesAsync(texturePath);
                if (dds.Length < 128 || !dds.AsSpan(0, 4).SequenceEqual("DDS "u8) ||
                    BitConverter.ToInt32(dds, 12) != 16 || BitConverter.ToInt32(dds, 16) != 16)
                    throw new InvalidOperationException("Clipped badge: DDS dimensions disagree with XML image size: " + badge);
            }
            // Simulate a stale 32px texture left behind by an older installer.
            string oldTexturePath = Path.Combine(lobby, "DN_good.dds");
            byte[] oldTexture = await File.ReadAllBytesAsync(oldTexturePath);
            BitConverter.GetBytes(32).CopyTo(oldTexture, 12);
            BitConverter.GetBytes(32).CopyTo(oldTexture, 16);
            await File.WriteAllBytesAsync(oldTexturePath, oldTexture);
            if (Civ5IntegrationService.Inspect(game, config).Components.Single(c => c.Name == "Icon good").Ready)
                throw new InvalidOperationException("Old 32px cropped texture was not detected.");
            if (Civ5IntegrationService.Repair(game, config).FilesChanged < 1 ||
                !Civ5IntegrationService.Inspect(game, config).Ready)
                throw new InvalidOperationException("Old 32px cropped texture was not replaced.");
            string newLobbyLua = await File.ReadAllTextAsync(Path.Combine(lobby, "StagingRoom.lua"));
            if (!newLobbyLua.Contains("icon:SetOffsetVal(count*DN_BADGE_STEP, 0)", StringComparison.Ordinal) ||
                !newLobbyLua.Contains("slot.DNBadgeStrip:SetHide(false)", StringComparison.Ordinal))
                throw new InvalidOperationException("Lobby badges use the wrong vertical anchor offset.");
            // Older 24px badges drawn before the label must be detected as outdated.
            string obsoleteXml = badgeXml.Replace("Size=\"16,16\"", "Size=\"20,20\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(lobby, "StagingRoom.xml"), obsoleteXml);
            if (Civ5IntegrationService.Inspect(game, config).Components.Single(c => c.Name == "Lobby XML / badge slots").Ready)
                throw new InvalidOperationException("Obsolete clipped lobby badges were not detected.");
            var badgeRepair = Civ5IntegrationService.Repair(game, config);
            if (badgeRepair.FilesChanged < 1 || !Civ5IntegrationService.Inspect(game, config).Ready)
                throw new InvalidOperationException("Badge layout was not repaired successfully.");
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
            report += "PASS: pixel-exact 20px DDS, center alignment, old XML and texture repair.\n"+
                "PASS: 4/4 mixed CRLF/LF InGame.lua scripts detected correctly.\n"+
                "PASS: a missing tracker is reported as 3/4 and can be restored.\n"+
                "PASS: stock game detected as missing integration.\n"+
                "PASS: Lua, XML, DDS, InGame.lua and config.ini installed.\n"+
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
