namespace DeathNote.Wpf.Models;

/// <summary>A lossless, read-only projection of an observed nickname from SQLite.</summary>
public sealed record NicknameHistoryEntry(
    int Number,
    string Nickname,
    string FirstSeen,
    DateTimeOffset? FirstSeenSortKey,
    bool IsCurrent)
{
    public string Status => IsCurrent ? "Current" : "Previous";
}
