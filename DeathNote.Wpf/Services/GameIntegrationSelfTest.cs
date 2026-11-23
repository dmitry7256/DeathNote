using System.Globalization;
using System.Text;
using System.Windows;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;

namespace DeathNote.Wpf.Services;

/// <summary>Runs against a disposable copy of the bundled EMPTY database.
/// Never opens or modifies the user's existing DeathNote.db or Lua.log.</summary>
public static class GameIntegrationSelfTest
{
    public static async Task RunAsync(string? reportPath)
    {
        var dir=Path.Combine(Path.GetTempPath(),"DeathNote-game-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string report="GAME INTEGRATION SELF-TEST — "+DateTimeOffset.Now.ToString("O")+Environment.NewLine;
        try
        {
            string db=Path.Combine(dir,"test.db");
            var resource=Application.GetResourceStream(new Uri("/Assets/empty-deathnote-v3.db",UriKind.Relative))
                ?? throw new InvalidDataException("Empty SQLite template missing.");
            using(var source=resource.Stream)
            using(var output=new FileStream(db,FileMode.CreateNew))await source.CopyToAsync(output);
            var repo=new SqliteRepository(db);
            var tracker=new GameLogIngestionService(repo,Path.Combine(dir,"Lua.log"));
            long epoch=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string sid="76561198070436086";
            var lines=new List<GameLogLine>
            {
                new(0,"[CIV5_PLAYERS] BEGIN|1",true),
                new(25,"[CIV5_PLAYERS] NAME|Friends%20Weekend",true),
                new(30,"[CIV5_PLAYERS] PLAYER|"+sid+"|Wolfaff",true),
                new(100,"[CIV5_PLAYERS] END|1",true),
                new(135,$"[CIV5_MATCH] LAUNCH|{epoch}|Запад против востока|Friends%20Weekend",true),
                new(210,$"[CIV5_MATCH] START|{epoch}|0|Запад против востока|Майя",true),
                new(300,$"[CIV5_MATCH] PLAYER|{epoch}|0|{sid}|Wolfaff|Ирокезы",true),
                new(390,$"[CIV5_MATCH] TURN|{epoch}|1",true),
                new(460,$"[CIV5_MATCH] END|{epoch+120}|1",true)
            };
            var first=await tracker.IngestAsync(lines,"Host");
            if(!first.PlayersChanged || !first.MatchesChanged || first.NewReviews.Count!=1)
                throw new InvalidOperationException("Missing inserted players, matches or review notification.");
            var snapshot=await repo.ReadSnapshotAsync();
            if(snapshot.Players.Count!=1 || snapshot.Players[0].Nickname!="Wolfaff")
                throw new InvalidOperationException("Lobby player not saved to SQLite.");
            if(snapshot.Matches.Count!=1 || !snapshot.Matches[0].Completed ||
               !snapshot.Matches[0].ReviewPending || snapshot.Matches[0].PlayerDetails.Count!=1 ||
               snapshot.Matches[0].Title!="Friends Weekend" || snapshot.Matches[0].LastTurn!=1 ||
               snapshot.Matches[0].EndTurnDisplay!="1" ||
               snapshot.Matches[0].PlayerDetails[0].SteamId!=sid ||
               snapshot.Matches[0].Map!="West vs East" ||
               snapshot.Matches[0].LocalCiv!="Maya" ||
               snapshot.Matches[0].PlayerDetails[0].Civilization!="Iroquois")
                throw new InvalidOperationException("Match or participant/review was not persisted.");
            // A replay of the same log lines must not create a duplicate match or popup.
            var replay=new GameLogIngestionService(repo,Path.Combine(dir,"Lua.log"));
            var again=await replay.IngestAsync(lines.Select(x=>x with {Live=false}).ToArray(),"Host");
            var snapshotAfter=await repo.ReadSnapshotAsync();
            if(again.NewReviews.Count!=0 || snapshotAfter.Players.Count!=1 || snapshotAfter.Matches.Count!=1)
                throw new InvalidOperationException("Replaying Lua.log created duplicates.");
            // A subsequent lobby rename must retain the user's note and bans.
            var player=snapshot.Players[0];
            player.Note="must stay after rename";player.Autokick=true;
            await repo.SavePlayerWithRevisionAsync(player,snapshot.PlayersRevision);
            var nameChange=await tracker.IngestAsync([
                new GameLogLine(800,"[CIV5_PLAYERS] BEGIN|1",true),
                new GameLogLine(850,"[CIV5_PLAYERS] PLAYER|"+sid+"|New%20name",true),
                new GameLogLine(920,"[CIV5_PLAYERS] END|1",true)],"Host");
            var final=await repo.ReadSnapshotAsync();
            if(!nameChange.PlayersChanged || final.Players.Count!=1 ||
               final.Players[0].Nickname!="New name" ||
               final.Players[0].Note!="must stay after rename" || !final.Players[0].Autokick)
                throw new InvalidOperationException("Rename overwrote user notes or auto-ban.");
            if((await repo.ReadNicknameHistoryAsync(sid,"New name")).Count<2)
                throw new InvalidOperationException("Nickname history not preserved.");
            // The updated game Lua passes a title directly with LAUNCH; the
            // new parser must also retain compatibility with old 1-argument LAUNCH.
            var secondGame=await tracker.IngestAsync([
                new GameLogLine(1050,$"[CIV5_MATCH] LAUNCH|{epoch+180}|West%20vs%20East|Second%20Lobby",true),
                new GameLogLine(1130,$"[CIV5_MATCH] START|{epoch+181}|0|West%20vs%20East|Maya",true),
                new GameLogLine(1210,$"[CIV5_MATCH] END|{epoch+300}|4",true)
            ],"Host");
            var twoMatches=await repo.ReadSnapshotAsync();
            if(!secondGame.MatchesChanged || twoMatches.Matches.Count!=2 ||
               !twoMatches.Matches.Any(m=>m.Title=="Second Lobby" && m.EndTurnDisplay=="4"))
                throw new InvalidOperationException("LAUNCH title and end turn were not persisted.");
            var oldProtocol=await tracker.IngestAsync([
                new GameLogLine(1290,"[CIV5_PLAYERS] BEGIN|1",true),
                new GameLogLine(1330,"[CIV5_PLAYERS] NAME|Legacy%20Lobby",true),
                new GameLogLine(1370,"[CIV5_PLAYERS] PLAYER|"+sid+"|New%20name",true),
                new GameLogLine(1400,"[CIV5_PLAYERS] END|1",true),
                new GameLogLine(1450,$"[CIV5_MATCH] LAUNCH|{epoch+320}|West%20vs%20East",true),
                new GameLogLine(1490,$"[CIV5_MATCH] END|{epoch+400}|5",true)
            ],"Host");
            var threeMatches=await repo.ReadSnapshotAsync();
            if(!oldProtocol.MatchesChanged || threeMatches.Matches.Count!=3 ||
               !threeMatches.Matches.Any(m=>m.Title=="Legacy Lobby" && m.EndTurnDisplay=="5"))
                throw new InvalidOperationException("Old LAUNCH compatibility or roster NAME fallback failed.");
            // A lobby title may be published after a match starts. It should
            // fill a missing title on that same observation, without duplicating a match.
            var lateTitleLaunch=await tracker.IngestAsync([
                new GameLogLine(1510,"[CIV5_PLAYERS] EXIT",true),
                new GameLogLine(1520,$"[CIV5_MATCH] LAUNCH|{epoch+460}|West%20vs%20East|",true)
            ],"Host");
            var lateTitleName=await tracker.IngestAsync([
                new GameLogLine(1600,"[CIV5_PLAYERS] BEGIN|1",true),
                new GameLogLine(1630,"[CIV5_PLAYERS] NAME|Late%20Lobby%20Title",true),
                new GameLogLine(1660,"[CIV5_PLAYERS] PLAYER|"+sid+"|New%20name",true),
                new GameLogLine(1690,"[CIV5_PLAYERS] END|1",true),
                new GameLogLine(1760,$"[CIV5_MATCH] END|{epoch+500}|7",true)
            ],"Host");
            var lateSnapshot=await repo.ReadSnapshotAsync();
            if(!lateTitleLaunch.MatchesChanged || !lateTitleName.MatchesChanged ||
               lateSnapshot.Matches.Count!=4 ||
               !lateSnapshot.Matches.Any(m=>m.Title=="Late Lobby Title" && m.EndTurnDisplay=="7"))
                throw new InvalidOperationException("Late lobby name did not populate the match title.");
            // Name markup is presentation-only: never rewrite the SQLite identities.
            var styledNick = "[ICON_MOVES][COLOR_CYAN]ISP[ENDCOLOR]";
            var rendered = Civ5MarkupText.Parse(styledNick);
            if (Civ5MarkupText.Plain(styledNick) != "➤ISP" || rendered.Count != 2 ||
                rendered[0].Text != "➤" || rendered[1].Text != "ISP" ||
                rendered[1].Color != "#FF40E5EF" ||
                Civ5MarkupText.Plain("[ICON_RELIGION][COLOR_RED]Test[ENDCOLOR]") != "✦Test" ||
                Civ5MarkupText.Plain("[VIP]Nickname") != "[VIP]Nickname" ||
                Civ5MarkupText.Plain("[COLOR_RED] sTaRK [END_COLOR]") != " sTaRK " ||
                Civ5MarkupText.Parse("[COLOR_RED] sTaRK [END_COLOR]").Any(s => s.Text.Contains("END_COLOR", StringComparison.Ordinal)) ||
                Civ5MarkupText.Parse("[COLOR_RED] sTaRK [END_COLOR]")[0].Color != "#FFFF7878" ||
                Civ5MarkupText.Plain("[COLOR:25:150:255:255]Sky[END_COLOR]") != "Sky" ||
                Civ5MarkupText.Parse("[COLOR_RED]Red[COLOR_CYAN]Cyan[END_COLOR]Red[END_COLOR]")[2].Color != "#FFFF7878" ||
                Civ5MarkupText.Parse("[ICON_MOVES]")[0].IconKey != "MOVES" ||
                Civ5MarkupText.Parse("[ICON_RES_IRON]")[0].IconKey != "RES_IRON" ||
                Civ5MarkupText.Parse("[ICON_RELIGION_BUDDHISM]")[0].IconKey != "RELIGION_BUDDHISM" ||
                Civ5MarkupText.Parse("[ICON_TEAM_USA]")[0].IconKey != "TEAM_USA" ||
                Civ5MarkupText.Plain("[ICON_NOT_A_CIV5_ICON]XYZ") != "[ICON_NOT_A_CIV5_ICON]XYZ")
                throw new InvalidOperationException("Civ V nickname markup rendered incorrectly.");
            // Cover the entire set of Civilization V emblems, not just Turkey.
            // The caller may supply a saved English name, a game CIVILIZATION_ key,
            // or a localized TXT_KEY_CIV_..._SHORT_DESC value.
            string[] bundledCivilizations =
            [
                "AMERICA",
                "ARABIA",
                "ASSYRIA",
                "AUSTRIA",
                "AZTEC",
                "BABYLON",
                "BRAZIL",
                "BYZANTIUM",
                "CARTHAGE",
                "CELTS",
                "CHINA",
                "DENMARK",
                "EGYPT",
                "ENGLAND",
                "ETHIOPIA",
                "FRANCE",
                "GERMANY",
                "GREECE",
                "HUNS",
                "INCA",
                "INDIA",
                "INDONESIA",
                "IROQUOIS",
                "JAPAN",
                "KOREA",
                "MAYA",
                "MONGOLIA",
                "MOROCCO",
                "NETHERLANDS",
                "OTTOMAN",
                "PERSIA",
                "POLAND",
                "POLYNESIA",
                "PORTUGAL",
                "ROME",
                "RUSSIA",
                "SHOSHONE",
                "SIAM",
                "SONGHAI",
                "SPAIN",
                "SWEDEN",
                "VENICE",
                "ZULU"
            ];
            foreach (var civ in bundledCivilizations)
            {
                var expected = "/Assets/civilizations/"+civ+".png";
                foreach (var form in new[]
                {
                    civ,
                    "CIVILIZATION_"+civ,
                    "civilization_"+civ.ToLowerInvariant(),
                    "TXT_KEY_CIV_"+civ+"_SHORT_DESC",
                    "txt_key_civ_"+civ.ToLowerInvariant()+"_short_desc"
                })
                    if (CivilizationEmblems.Icon(form)!=expected)
                        throw new InvalidOperationException("Civilization emblem mapping failed: "+form);
            }
            foreach (var (alias, expected) in new (string Name, string Icon)[]
            {
                ("Азтеки", "AZTEC"), ("Османская империя", "OTTOMAN"),
                ("Турция", "OTTOMAN"), ("Византия", "BYZANTIUM"),
                ("Майя", "MAYA"), ("Нидерланды", "NETHERLANDS"),
                ("The Huns", "HUNS"), ("Ottoman Empire", "OTTOMAN")
            })
                if (CivilizationEmblems.Icon(alias)!="/Assets/civilizations/"+expected+".png")
                    throw new InvalidOperationException("Localized civilization mapping failed: "+alias);
            // The game packs the Ottoman empire emblem; local match metadata may
            // refer to that civilization as Turkey. Use its existing artwork rather
            // than a fallback badge, but never rewrite the saved name.
            var turkeyMatch=new MatchRecord{Id="sample", LocalCiv="Турция"};
            if (CivilizationNames.Format("Турция")!="Turkey" ||
                turkeyMatch.LocalCivEnglish!="Turkey" ||
                turkeyMatch.LocalCiv!="Турция" ||
                turkeyMatch.LocalCivIcon!="/Assets/civilizations/OTTOMAN.png" ||
                !turkeyMatch.HasLocalCivIcon || turkeyMatch.HasLocalCivFallback ||
                CivilizationEmblems.Icon("Turkey")!=turkeyMatch.LocalCivIcon)
                throw new InvalidOperationException("Turkey did not resolve to the bundled Ottoman emblem.");
            // Truly unknown modded civilizations retain their text fallback.
            var modCiv=new MatchRecord{Id="mod",LocalCiv="Unknown Mod Civilization"};
            if (!modCiv.HasLocalCivFallback || modCiv.HasLocalCivIcon)
                throw new InvalidOperationException("Unknown modded civilization lost its fallback.");
            // Even old matches with no civilization should display an honest status
            // in their detail panel, rather than inventing a nation or emblem.
            if ((new MatchRecord{Id="empty"}).LocalCivEnglish!="Not detected")
                throw new InvalidOperationException("Missing civilization status was not explicit.");
            if(!await repo.CheckIntegrityAsync())
                throw new InvalidOperationException("SQLite integrity or foreign keys failed.");
            report+="PASS: lobby player was inserted; launch title, participants and last turn persisted.\n"+
                    "PASS: review pending; replay is idempotent.\n"+
                    "PASS: nickname update preserved note, ban and history.\n"+
                    "PASS: updated, legacy and late TITLE protocols retain lobby title and final turn.\n"+
                    "PASS: Russian civilization and map names are normalized to English.\n"+
                    "PASS: Civilization V ICON/COLOR inline markup displayed without changing raw identities.\n"+
                    "PASS: SQLite integrity checks.\n";
        }
        catch(Exception error)
        {
            report+="FAILED: "+error+"\n";
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir,recursive:true); } catch(IOException) { }
        }
        string path=reportPath ?? Path.Combine(App.Workspace,"GAME_INTEGRATION_TEST_REPORT.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path,report,Encoding.UTF8);
    }
}
