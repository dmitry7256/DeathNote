using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Views;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Opt-in synthetic profile. Does not initialize SQLite, game monitor, backup,
/// game bridge or update checks. All 5000 players and matches live in memory.
/// For meaningful run-to-run variance perform multiple passes in the same
/// process AND launch BENCHMARK_UI.bat for several independent processes.
/// </summary>
public static class UiPerformanceBenchmark
{
    public const int SyntheticPlayerCount = 5000;
    public const int DefaultRuns = 3;

    public static async Task RunAsync(
        Window window, MainViewModel vm, PlayersView players, MatchHistoryView matches,
        StatisticsView statistics, SettingsView settings, Action<UserControl> showPage,
        int runs = DefaultRuns)
    {
        runs=Math.Clamp(runs,1,8);
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeathNote.Wpf","benchmark","UI_BENCHMARK_REPORT.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var metrics=new BenchmarkMeasurements(window.Dispatcher);
        var frameIntervals=new List<double>();
        long previousFrame=0;
        void OnRendering(object? sender,EventArgs args)
        {
            long now=Stopwatch.GetTimestamp();
            if(previousFrame!=0)
            {
                double ms=(now-previousFrame)*1000.0/Stopwatch.Frequency;
                if(ms is >0.1 and <250)frameIntervals.Add(ms);
            }
            previousFrame=now;
        }
        CompositionTarget.Rendering+=OnRendering;
        try
        {
            window.Width=window.MinWidth;
            window.Height=window.MinHeight;
            await metrics.IdleAsync();
            var synthetic=Enumerable.Range(0,SyntheticPlayerCount).Select(i=>new PlayerRecord
            {
                SteamId=(76561198000000000L+i).ToString(CultureInfo.InvariantCulture),
                Nickname=$"Benchmark Player {i:00000}",
                Marker=i%3==0?"good":i%3==1?"warn":"bad",
                Note=$"Synthetic note {i}",Favorite=i%17==0,Autokick=i%41==0,
                Wins=i%28,Losses=i%31,Leaves=i%13
            }).ToArray();
            var syntheticMatches=Enumerable.Range(0,SyntheticPlayerCount).Select(i=>new MatchRecord
            {
                Id=$"benchmark-match-{i:00000}",Title=$"Synthetic match {i:00000}",
                Started=DateTimeOffset.UtcNow.AddDays(-(i%220)).ToString("O",CultureInfo.InvariantCulture),
                Result="Успешно",Map=i%2==0?"Пангея":"Континенты",
                LocalCiv=i%2==0?"AMERICA":"ROME",Participants=2,
                ParticipantNames="Benchmark host, Benchmark guest",Completed=true,
                WinnerId=i%3==0?"__local__":"",Note="Synthetic match note"
            }).ToArray();
            var table=players.FindName("PlayersGrid") as DataGrid
                ??throw new InvalidOperationException("PlayersGrid not found");
            var note=players.FindName("PlayerNote") as TextBox
                ??throw new InvalidOperationException("PlayerNote not found");
            var matchTable=matches.FindName("MatchesGrid") as DataGrid
                ??throw new InvalidOperationException("MatchesGrid not found");
            var matchNote=matches.FindName("MatchNote") as TextBox
                ??throw new InvalidOperationException("MatchNote not found");

            // Track cold first visual activation independently from subsequent
            // warm switches. Controls are constructed at startup, not yet rendered.
            var seen=new HashSet<UserControl> {players};
            UserControl[] pages=[matches,statistics,settings,players];
            for(int pass=1;pass<=runs;pass++)
            {
                table.SelectedItem=null;
                matchTable.SelectedItem=null;
                await metrics.IdleAsync();
                await metrics.MeasureAsync("5000 row collection + UI idle",()=>
                {
                    vm.Players.ReplaceAll(synthetic);
                    vm.RefreshPlayerFilters();
                },pass);
                table.SelectedItem=synthetic[0];
                await metrics.IdleAsync();
                for(int i=0;i<35;i++)
                {
                    var selected=synthetic[(i*151+7+pass)%SyntheticPlayerCount];
                    await metrics.MeasureAsync("player selection -> UI idle",()=>table.SelectedItem=selected,pass);
                }
                table.SelectedItem=synthetic[12];
                await metrics.IdleAsync();
                note.Text=new string('a',3500);
                await metrics.IdleAsync();
                for(int i=0;i<50;i++)
                    await metrics.MeasureAsync("long note: append one character -> UI idle",()=>note.AppendText("x"),pass);
                for(int i=0;i<30;i++)
                {
                    int index=(i*433+pass)%SyntheticPlayerCount;
                    await metrics.MeasureAsync("5000 row grid: scroll -> UI idle",()=>table.ScrollIntoView(synthetic[index]),pass);
                }
                for(int i=0;i<40;i++)
                {
                    int index=(i*4)%SyntheticPlayerCount;
                    await metrics.MeasureAsync("5000 player grid: nearby scroll -> UI idle",()=>table.ScrollIntoView(synthetic[index]),pass);
                }
                for(int i=0;i<4;i++)
                {
                    var direction=i%2==0?ListSortDirection.Ascending:ListSortDirection.Descending;
                    await metrics.MeasureAsync("5000 row grid: sort -> UI idle",()=>
                        FastGridSort.ApplyPlayers(vm.PlayerView,nameof(PlayerRecord.Nickname),direction),pass);
                }
                FastGridSort.ApplyPlayers(vm.PlayerView,null,null);
                await metrics.IdleAsync();

                var firstMatch=seen.Add(matches);
                await metrics.MeasureAsync(firstMatch?"cold tab activation -> UI idle":"warm tab activation -> UI idle",
                    ()=>showPage(matches),pass);
                await Task.Delay(190); // Let the existing 165 ms animation finish.
                await metrics.MeasureAsync("5000 match rows collection + UI idle",()=>
                {
                    vm.Matches.ReplaceAll(syntheticMatches);
                    vm.RefreshMatchFilters();
                },pass);
                await metrics.MeasureAsync("match statistics recalculation -> UI idle",
                    ()=>vm.Stats.Recalculate(syntheticMatches),pass);
                for(int i=0;i<28;i++)
                {
                    int index=(i*677+pass)%SyntheticPlayerCount;
                    await metrics.MeasureAsync("5000 match grid: scroll -> UI idle",()=>matchTable.ScrollIntoView(syntheticMatches[index]),pass);
                }
                for(int i=0;i<40;i++)
                {
                    int index=(i*4)%SyntheticPlayerCount;
                    await metrics.MeasureAsync("5000 match grid: nearby scroll -> UI idle",()=>matchTable.ScrollIntoView(syntheticMatches[index]),pass);
                }
                for(int i=0;i<4;i++)
                {
                    var direction=i%2==0?ListSortDirection.Ascending:ListSortDirection.Descending;
                    await metrics.MeasureAsync("5000 match grid: sort -> UI idle",()=>
                        FastGridSort.ApplyMatches(vm.MatchView,nameof(MatchRecord.DisplayTitle),direction),pass);
                }
                FastGridSort.ApplyMatches(vm.MatchView,null,null);
                matchTable.SelectedItem=syntheticMatches[12];
                await metrics.IdleAsync();
                matchNote.Text=new string('m',3500);
                await metrics.IdleAsync();
                for(int i=0;i<35;i++)
                    await metrics.MeasureAsync("match note: append character -> UI idle",()=>matchNote.AppendText("x"),pass);

                for(int i=0;i<12;i++)
                {
                    var page=pages[i%pages.Length];
                    var cold=seen.Add(page);
                    await metrics.MeasureAsync(cold?"cold tab activation -> UI idle":"warm tab activation -> UI idle",
                        ()=>showPage(page),pass);
                    // Allow the real WPF animation to finish, without counting
                    // a fixed delay as handler/dispatcher work.
                    await Task.Delay(190);
                }
            }

            var report=new StringBuilder();
            report.AppendLine("DEATH NOTE — WPF UI PERFORMANCE (synthetic, no SQLite writes)");
            report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine($"Version: {typeof(UiPerformanceBenchmark).Assembly.GetName().Version}");
            report.AppendLine($"Window: {window.Width:0} x {window.Height:0} DIP");
            report.AppendLine($"DPI scale: {VisualTreeHelper.GetDpi(window).DpiScaleX:0.00}");
            report.AppendLine($"Synthetic players: {SyntheticPlayerCount}");
            report.AppendLine($"Synthetic matches: {SyntheticPlayerCount}");
            report.AppendLine($"Runs in this process: {runs}");
            report.AppendLine("Scroll scenarios: random ScrollIntoView jumps and nearby 4-row steps measured separately.");
            report.AppendLine("Sorting: typed ListCollectionView.CustomSort, the same path as column clicks.");
            report.AppendLine("Cold = first visual activation, warm = subsequent activation; does not include process cold start.");
            report.AppendLine("Elapsed to ApplicationIdle is not GPU presentation time. Animation duration is excluded.");
            report.AppendLine();
            metrics.AppendReport(report);
            if(frameIntervals.Count>0)
                report.AppendLine($"WPF Rendering callback intervals (active test): n={frameIntervals.Count}, " +
                    $"p50={BenchmarkMeasurements.Pct(frameIntervals,50):F2} ms, " +
                    $"p95={BenchmarkMeasurements.Pct(frameIntervals,95):F2} ms, " +
                    $"max={frameIntervals.Max():F2} ms");
            report.AppendLine();
            report.AppendLine("Important: synthetic measurements do not include real SQLite or game-log processing.");
            report.AppendLine("For comparison run with the same display scale and window size each time.");
            File.WriteAllText(path,report.ToString(),Encoding.UTF8);
        }
        catch(Exception error)
        {
            File.WriteAllText(path,$"Benchmark failed: {error}\n",Encoding.UTF8);
        }
        finally
        {
            CompositionTarget.Rendering-=OnRendering;
            window.Close();
        }
    }
}
