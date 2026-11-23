using DeathNote.Wpf.Infrastructure;

namespace DeathNote.Wpf.Models;

public sealed class PlayerRecord : Observable
{
    private string _nickname = "", _note = "", _marker = "";
    private bool _favorite, _autokick;
    private long _wins, _losses, _leaves;
    public required string SteamId { get; init; }
    public string Nickname { get => _nickname; set => Set(ref _nickname, value); }
    public string Note { get => _note; set => Set(ref _note, value); }
    public string Marker
    {
        get => _marker;
        set
        {
            if (Set(ref _marker, value))
            {
                Notify(nameof(Rating));
                Notify(nameof(MarkerIconPath));
                Notify(nameof(HasMarker));
            }
        }
    }
    public string Rating => Marker switch { "good" => "Good", "warn" => "Questionable", "bad" => "Bad", _ => "Unrated" };
    public bool HasMarker => Marker is "good" or "warn" or "bad";
    public string? MarkerIconPath => Marker switch
    {
        "good" => "/Assets/markers/good.png",
        "warn" => "/Assets/markers/warn.png",
        "bad" => "/Assets/markers/bad.png",
        _ => null
    };
    public bool Favorite { get => _favorite; set { if (Set(ref _favorite, value)) Notify(nameof(FavoriteGlyph)); } }
    public string FavoriteGlyph => Favorite ? "★" : "☆";
    public bool Autokick { get => _autokick; set => Set(ref _autokick, value); }
    public bool IsBanned => Autokick;
    public long Wins { get => _wins; set => Set(ref _wins, value); }
    public long Losses { get => _losses; set => Set(ref _losses, value); }
    public long Leaves { get => _leaves; set => Set(ref _leaves, value); }
    public string BanUntil { get; set; } = "";
    public string PreviousNames { get; set; } = "";
    public IReadOnlyList<string> PreviousNamesList => PreviousNames
        .Split([','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(x => !string.Equals(x, Nickname, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public bool HasPreviousNames => PreviousNamesList.Count > 0;

    public void CopyFrom(PlayerRecord p)
    {
        Nickname = p.Nickname;
        Note = p.Note;
        Marker = p.Marker;
        Autokick = p.Autokick;
        Wins = p.Wins;
        Losses = p.Losses;
        Leaves = p.Leaves;
        Favorite = p.Favorite;
        if (PreviousNames != p.PreviousNames)
        {
            PreviousNames = p.PreviousNames;
            Notify(nameof(PreviousNames));
            Notify(nameof(PreviousNamesList));
            Notify(nameof(HasPreviousNames));
        }
    }
}
