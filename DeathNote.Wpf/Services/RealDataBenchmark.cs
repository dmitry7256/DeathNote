using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Views;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Explicit opt-in read-only integration benchmark. Exercises the repository's
/// actual v3 deserialization, player/match UI binding and existing Lua event
/// parser. Does NOT invoke MainViewModel.InitializeAsync, SQLite writes,
/// daily backups, update checks, the game bridge, or live monitors.
/// The report contains aggregate counts and timings, NEVER player identities,
/// notes, game log contents or the supplied file paths.
/// </summary>
public static class RealDataBenchmark
{
    public static async Task RunAsync(Window window,MainViewModel vm,PlayersView players,
        MatchHistoryView matches,StatisticsView statistics,SettingsView settings,
        Action<UserControl> showPage,string? database,string? logPath,int runs)
    {
        var reportPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeathNote.Wpf","benchmark","REAL_BENCHMARK_REPORT.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var metrics=new BenchmarkMeasurements(window.Dispatcher);
        runs=Math.Clamp(runs,1,8);
        var notes=new List<string>();
        var seen=new HashSet<UserControl>{players};
        try
        {
            if(string.IsNullOrWhiteSpace(database) || !File.Exists(database))
                throw new FileNotFoundException("Choose an existing DeathNote.db for the read-only benchmark.");
            var repo=new SqliteRepository(Path.GetFullPath(database));
            window.Width=window.MinWidth;
            window.Height=window.MinHeight;
            await metrics.IdleAsync();
            DatabaseSnapshot? latest=null;
            for(int pass=1;pass<=runs;pass++)
            {
                if(pass>1)
                {
                    showPage(players);
                    await Task.Delay(190);
                }
                vm.SelectedPlayer=null;
                vm.SelectedMatch=null;
                await metrics.IdleAsync();
                await metrics.MeasureAwaitedAsync(pass==1?"cold SQLite revisions read":"warm SQLite revisions read",
                    async()=>{_ = await repo.ReadRevisionsAsync();},pass);
                await metrics.MeasureAwaitedAsync(pass==1?"cold SQLite full snapshot read":"warm SQLite full snapshot read",
                    async()=>{latest=await repo.ReadSnapshotAsync();},pass);
                if(latest is null)throw new InvalidOperationException("SQLite snapshot was not read.");
                var data=latest;
                await metrics.MeasureAsync("real players collection + UI idle",()=>
                {
                    vm.Players.ReplaceAll(data.Players);
                    vm.RefreshPlayerFilters();
                },pass);
                await metrics.MeasureAsync("real matches collection + UI idle",()=>
                {
                    vm.Matches.ReplaceAll(data.Matches);
                    vm.RefreshMatchFilters();
                },pass);
                await metrics.MeasureAsync("real stats recalculation + UI idle",
                    ()=>vm.Stats.Recalculate(data.Matches),pass);

                // Exercise the real list and bound detail pane without edits.
                if(data.Players.Count>0)
                {
                    var table=players.FindName("PlayersGrid") as DataGrid
                        ?? throw new InvalidOperationException("PlayersGrid not found");
                    for(int i=0;i<Math.Min(15,data.Players.Count);i++)
                    {
                        int index=i*149%data.Players.Count;
                        var row=data.Players[index];
                        await metrics.MeasureAsync("real player selection -> UI idle",
                            ()=>table.SelectedItem=row,pass);
                    }
                }
                UserControl[] pages=[matches,statistics,settings,players];
                foreach(var page in pages)
                {
                    var cold=seen.Add(page);
                    await metrics.MeasureAsync(cold?"cold real tab -> UI idle":"warm real tab -> UI idle",
                        ()=>showPage(page),pass);
                    await Task.Delay(190);
                }
                if(data.Matches.Count>0)
                {
                    showPage(matches);
                    await Task.Delay(190);
                    var table=matches.FindName("MatchesGrid") as DataGrid
                        ?? throw new InvalidOperationException("MatchesGrid not found");
                    for(int i=0;i<Math.Min(15,data.Matches.Count);i++)
                    {
                        int index=i*113%data.Matches.Count;
                        await metrics.MeasureAsync("real match selection -> UI idle",
                            ()=>table.SelectedItem=data.Matches[index],pass);
                    }
                }
                notes.Add($"run {pass}: players={data.Players.Count}, matches={data.Matches.Count}, " +
                    $"pending reviews={data.Matches.Count(m=>m.ReviewPending && !m.Deleted)}");
            }

            if(!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                GameMonitorService.LogInspection? inspected=null;
                for(int pass=1;pass<=runs;pass++)
                {
                    await metrics.MeasureAwaitedAsync(pass==1?"cold real Lua.log scan":"warm real Lua.log scan",
                        async()=>{inspected=await GameMonitorService.InspectRecentAsync(logPath);},pass);
                    if(inspected is not null)
                    {
                        var number=inspected.Events;
                        await metrics.MeasureAsync("game event UI notification -> UI idle",
                            ()=>vm.SetBenchmarkGameEvents(number),pass);
                    }
                }
                if(inspected is not null)notes.Add($"Game log (last 256 KiB): lines={inspected.CompleteLines}, " +
                    $"match events={inspected.MatchEvents}, lobby events={inspected.LobbyEvents}, " +
                    $"complete lobby rosters={inspected.CompleteRosters}");
            }
            else notes.Add("Game log not found / not supplied: event parsing not measured.");

            var report=new StringBuilder();
            report.AppendLine("DEATH NOTE — READ-ONLY SQLITE + LUA DIAGNOSTIC");
            report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine($"Version: {typeof(RealDataBenchmark).Assembly.GetName().Version}");
            report.AppendLine($"Window: {window.Width:0} x {window.Height:0} DIP");
            report.AppendLine($"DPI scale: {VisualTreeHelper.GetDpi(window).DpiScaleX:0.00}");
            report.AppendLine($"Runs: {runs}");
            report.AppendLine("Input: explicit existing SQLite v3 (read-only connection) and optional Lua.log (read-only stream).");
            report.AppendLine("No writes to user data, game commands, backups, or update checks in this mode.");
            report.AppendLine("Report deliberately excludes paths, player names, Steam IDs, notes and log contents.");
            report.AppendLine();
            foreach(var n in notes)report.AppendLine(n);
            report.AppendLine();
            metrics.AppendReport(report);
            report.AppendLine("Cold SQLite = first read in this process; warm may benefit from OS filesystem cache.");
            report.AppendLine("Log scan covers only last 256 KiB, not a full play session or continuous real-time monitoring.");
            report.AppendLine("SQLite snapshot times include background decoding, not the normal startup update/backup network work.");
            File.WriteAllText(reportPath,report.ToString(),Encoding.UTF8);
        }
        catch(Exception error)
        {
            // Avoid writing an exception path or full SQLite content into the report.
            File.WriteAllText(reportPath,$"Real-data benchmark failed ({error.GetType().Name}). " +
                "Check the selected SQLite schema v3, permissions and file availability.\n",Encoding.UTF8);
        }
        finally {window.Close();}
    }
}
