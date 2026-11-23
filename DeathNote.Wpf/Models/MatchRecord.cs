using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Data;

namespace DeathNote.Wpf.Models;

public sealed class MatchRecord : Observable
{
    private string _title="",_result="",_winnerId="",_note="";
    private bool _favorite, _deleted, _reviewPending;
    public required string Id { get; init; }
    public string Title { get => _title; set { if (Set(ref _title,value)) Notify(nameof(DisplayTitle)); } }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled match" : Title;
    public string Result { get => _result; set { if (Set(ref _result,value)) Notify(nameof(DisplayResult)); } }
    public string DisplayResult => Result switch
    {
        "Успешно" => "Completed", "Игра сломалась" => "Game crashed",
        "Все игроки вышли" => "All players left", "Рестарт" => "Restart",
        "Не указано" or "" => "Unspecified", _ => Result
    };
    public string WinnerId { get => _winnerId; set => Set(ref _winnerId, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public bool Favorite { get => _favorite; set { if (Set(ref _favorite,value)) Notify(nameof(FavoriteGlyph)); } }
    public string FavoriteGlyph => Favorite ? "★" : "☆";
    public bool Deleted { get => _deleted; set => Set(ref _deleted,value); }
    public bool ReviewPending { get => _reviewPending; set => Set(ref _reviewPending,value); }
    public string Started { get; set; } = "";
    public string LastAt { get; set; } = "";
    public long? StartTurn { get; set; }
    public long? LastTurn { get; set; }
    public string Ended { get; set; } = "";
    public string Map { get; set; } = "";
    public string MapEnglish => MapNames.Format(Map);
    public string LocalCiv { get; set; } = "";
    public string LocalCivEnglish => string.IsNullOrWhiteSpace(LocalCiv)
        ? "Not detected" : CivilizationNames.Format(LocalCiv);
    // Sort by the same normalized civilization name used for the emblems,
    // so Russian/English aliases group together alphabetically.
    private string _civSortSource=string.Empty;
    private string _civSortKey=string.Empty;
    public string LocalCivSortKey
    {
        get
        {
            if (!string.Equals(_civSortSource,LocalCiv,StringComparison.Ordinal))
            {
                _civSortSource=LocalCiv;
                _civSortKey=CivilizationNames.Format(LocalCiv);
            }
            return _civSortKey;
        }
    }
    public string? LocalCivIcon => CivilizationEmblems.Icon(LocalCiv);
    public bool HasLocalCivIcon => LocalCivIcon is not null;
    // A custom/modded civilization may not have a bundled emblem. Never display
    // an empty Civ cell when the original civilization name was actually saved.
    public bool HasLocalCivFallback => !HasLocalCivIcon && !string.IsNullOrWhiteSpace(LocalCiv);
    public string LocalCivAbbreviation
    {
        get
        {
            var name=LocalCiv.Trim();
            return name.Length<=2 ? name.ToUpperInvariant() : name[..2].ToUpperInvariant();
        }
    }
    public string LocalNick { get; set; } = "";
    public string LocalParticipantLabel => (string.IsNullOrWhiteSpace(LocalNick) ? "You" : LocalNick) + " (you)";
    public string ParticipantNames { get; set; } = "";
    public int Participants { get; set; }
    public string WinnerName { get; set; } = "Not selected";
    public IReadOnlyList<MatchParticipant> PlayerDetails { get; set; } = [];
    public bool Completed { get; set; }
    public string EndTurnDisplay => Completed ?
        (LastTurn.HasValue ? LastTurn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—") :
        "In progress";
    public string DisplayDate => DateTimeOffset.TryParse(Started,out var d) ? d.LocalDateTime.ToString("dd.MM.yyyy HH:mm") : Started;
    // Table shows the calendar date only; sidebar keeps the full timestamp.
    public string DisplayDateOnly => DateTimeOffset.TryParse(Started,out var d)
        ? d.LocalDateTime.ToString("dd.MM.yyyy")
        : (Started.Length>=10 ? Started[..10] : Started);
    /// <summary>
    /// Keep the existing row instance, but only invalidate bindings that really
    /// changed. Previously every game-revision refresh notified every visible
    /// row about Civ, dates, participants and winner even when identical.
    /// </summary>
    public void CopyFrom(MatchRecord m)
    {
        Title=m.Title; Result=m.Result; WinnerId=m.WinnerId; Note=m.Note;
        Favorite=m.Favorite; Deleted=m.Deleted; ReviewPending=m.ReviewPending;
        if (!string.Equals(Started,m.Started,StringComparison.Ordinal))
        {
            Started=m.Started;
            Notify(nameof(DisplayDate));
            Notify(nameof(DisplayDateOnly));
        }
        LastAt=m.LastAt;
        StartTurn=m.StartTurn;
        if(LastTurn!=m.LastTurn)
        {
            LastTurn=m.LastTurn;
            Notify(nameof(EndTurnDisplay));
        }
        Ended=m.Ended;
        if (!string.Equals(Map,m.Map,StringComparison.Ordinal))
        {
            Map=m.Map;
            Notify(nameof(Map));
            Notify(nameof(MapEnglish));
        }
        if (!string.Equals(LocalCiv,m.LocalCiv,StringComparison.Ordinal))
        {
            LocalCiv=m.LocalCiv;
            Notify(nameof(LocalCiv));
            Notify(nameof(LocalCivEnglish));
            Notify(nameof(LocalCivIcon));
            Notify(nameof(HasLocalCivIcon));
            Notify(nameof(HasLocalCivFallback));
            Notify(nameof(LocalCivAbbreviation));
            Notify(nameof(LocalCivSortKey));
        }
        if (!string.Equals(LocalNick,m.LocalNick,StringComparison.Ordinal))
        {
            LocalNick=m.LocalNick;
            Notify(nameof(LocalParticipantLabel));
        }
        if (!string.Equals(ParticipantNames,m.ParticipantNames,StringComparison.Ordinal))
        {
            ParticipantNames=m.ParticipantNames;
            Notify(nameof(ParticipantNames));
        }
        if (Participants!=m.Participants)
        {
            Participants=m.Participants;
            Notify(nameof(Participants));
        }
        if (!string.Equals(WinnerName,m.WinnerName,StringComparison.Ordinal))
        {
            WinnerName=m.WinnerName;
            Notify(nameof(WinnerName));
        }
        if (!PlayerDetails.SequenceEqual(m.PlayerDetails))
        {
            PlayerDetails=m.PlayerDetails;
            Notify(nameof(PlayerDetails));
        }
        if(Completed!=m.Completed)
        {
            Completed=m.Completed;
            Notify(nameof(EndTurnDisplay));
        }
    }

}

public sealed record MatchParticipant(string SteamId,string Nickname,string Civilization)
{
    public string? CivilizationIcon => CivilizationEmblems.Icon(Civilization);
    public bool HasCivilizationIcon => CivilizationIcon is not null;
    public string CivilizationEnglish => CivilizationNames.Format(Civilization);
}
public sealed record WinnerOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record DatabaseSnapshot(long PlayersRevision,long GamesRevision,long PreferencesRevision,
    IReadOnlyList<PlayerRecord> Players,IReadOnlyList<MatchRecord> Matches,IReadOnlySet<string> Favorites,
    string LocalNickname, bool ReviewNotifications, bool AutoKickEnabled);
