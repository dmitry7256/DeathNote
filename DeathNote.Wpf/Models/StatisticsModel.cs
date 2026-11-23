using System.Collections.ObjectModel;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Data;

namespace DeathNote.Wpf.Models;

public sealed record CountRow(string Name,int Count);
public sealed record MonthCountRow(string Label,int Count);
public sealed class StatisticsModel : Observable
{
    private int _total,_wins,_decided;
    public int Total {get=>_total;set=>Set(ref _total,value);}
    public int Wins {get=>_wins;set {if(Set(ref _wins,value))Notify(nameof(WinRate));}}
    public int Decided {get=>_decided;set {if(Set(ref _decided,value))Notify(nameof(WinRate));}}
    public string WinRate => Decided==0?"—":(100.0*Wins/Decided).ToString("0.0")+"%";
    public ObservableCollection<CountRow> Results {get;}=[];
    public ObservableCollection<MonthCountRow> Months {get;}=[];
    public ObservableCollection<CountRow> Maps {get;}=[];
    public ObservableCollection<CountRow> Civilizations {get;}=[];

    public void Recalculate(IEnumerable<MatchRecord> matches)
    {
        var completed=matches.Where(m=>!m.Deleted && m.Completed).ToList();
        Total=completed.Count;
        Wins=completed.Count(m=>m.WinnerId=="__local__");
        Decided=completed.Count(m=>m.WinnerId=="__local__" ||
                                   (!string.IsNullOrEmpty(m.WinnerId) && m.PlayerDetails.Any(p=>p.SteamId==m.WinnerId)) ||
                                   (!string.IsNullOrEmpty(m.WinnerId) && m.ParticipantNames.Length>0 && m.WinnerName!="Not selected"));
        Results.Clear();
        foreach(var item in completed.GroupBy(m=>m.DisplayResult).OrderByDescending(g=>g.Count()))
            Results.Add(new CountRow(item.Key,item.Count()));
        // As in the Python 2.09 statistics, count one map and the local
        // player's civilization per completed, non-deleted match.
        // Unknown maps remain visible; unknown civilizations are not guessed.
        Maps.Clear();
        foreach(var group in completed
            .GroupBy(m=>MapNames.Format(m.Map),StringComparer.OrdinalIgnoreCase)
            .Select(g=>new CountRow(g.Key,g.Count()))
            .OrderByDescending(x=>x.Count).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
            Maps.Add(group);
        Civilizations.Clear();
        foreach(var group in completed
            .Where(m=>!string.IsNullOrWhiteSpace(m.LocalCiv))
            .GroupBy(m=>CivilizationNames.Format(m.LocalCiv.Trim()),StringComparer.OrdinalIgnoreCase)
            .Select(g=>new CountRow(g.Key,g.Count()))
            .OrderByDescending(x=>x.Count).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
            Civilizations.Add(group);
        // Anchor the activity chart to the latest recorded match, even for old
        // databases. Unknown date strings are ignored instead of guessed.
        var months = completed
            .Select(m => DateTimeOffset.TryParse(m.Started,out var dt) ? (DateTime?)dt.LocalDateTime : null)
            .Where(d => d.HasValue).Select(d => new DateTime(d!.Value.Year,d.Value.Month,1))
            .GroupBy(d => d).ToDictionary(g => g.Key,g => g.Count());
        Months.Clear();
        if(months.Count>0)
        {
            DateTime last=months.Keys.Max();
            for(int i=7;i>=0;i--)
            {
                DateTime month=last.AddMonths(-i);
                Months.Add(new MonthCountRow(month.ToString("MMM",System.Globalization.CultureInfo.InvariantCulture),
                    months.GetValueOrDefault(month)));
            }
        }
    }
}
