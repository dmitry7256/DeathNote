using System.Collections;
using System.ComponentModel;
using System.Windows.Data;
using DeathNote.Wpf.Models;

namespace DeathNote.Wpf.Infrastructure;

/// <summary>
/// Typed comparers avoid invoking PropertyDescriptor on every comparison of
/// thousands of WPF DataGrid records. The default ListCollectionView is kept,
/// including filtering, selection and virtualization; no parallel table is made.
/// </summary>
public static class FastGridSort
{
    public static void ApplyPlayers(ICollectionView view, string? member, ListSortDirection? direction)
    {
        if (view is ListCollectionView list)
        {
            using (list.DeferRefresh())
            {
                list.SortDescriptions.Clear();
                list.CustomSort=direction.HasValue && member is not null
                    ? new PlayerComparer(member,direction.Value) : null;
            }
        }
        else ApplyFallback(view,member,direction);
    }

    public static void ApplyMatches(ICollectionView view, string? member, ListSortDirection? direction)
    {
        if (view is ListCollectionView list)
        {
            using (list.DeferRefresh())
            {
                list.SortDescriptions.Clear();
                list.CustomSort=direction.HasValue && member is not null
                    ? new MatchComparer(member,direction.Value) : null;
            }
        }
        else ApplyFallback(view,member,direction);
    }

    private static void ApplyFallback(ICollectionView view, string? member, ListSortDirection? direction)
    {
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            if (direction.HasValue && member is not null)
                view.SortDescriptions.Add(new SortDescription(member,direction.Value));
        }
    }

    public static string? ActivePlayerMember(ICollectionView view) =>
        (view as ListCollectionView)?.CustomSort is PlayerComparer comparer
            ? comparer.Member
            : view.SortDescriptions.Count>0 ? view.SortDescriptions[0].PropertyName : null;

    private sealed class PlayerComparer(string member,ListSortDirection direction) : IComparer
    {
        public string Member { get; }=member;
        private readonly int _sign=direction==ListSortDirection.Ascending ? 1 : -1;
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x,y)) return 0;
            if (x is not PlayerRecord a) return -1;
            if (y is not PlayerRecord b) return 1;
            int result=Member switch
            {
                nameof(PlayerRecord.Favorite) => a.Favorite.CompareTo(b.Favorite),
                nameof(PlayerRecord.Autokick) => a.Autokick.CompareTo(b.Autokick),
                nameof(PlayerRecord.Wins) => a.Wins.CompareTo(b.Wins),
                nameof(PlayerRecord.Losses) => a.Losses.CompareTo(b.Losses),
                nameof(PlayerRecord.Leaves) => a.Leaves.CompareTo(b.Leaves),
                nameof(PlayerRecord.Marker) => CompareText(a.Marker,b.Marker),
                nameof(PlayerRecord.Note) => CompareText(a.Note,b.Note),
                _ => CompareText(a.Nickname,b.Nickname)
            };
            return result*_sign;
        }
    }

    private sealed class MatchComparer(string member,ListSortDirection direction) : IComparer
    {
        private readonly int _sign=direction==ListSortDirection.Ascending ? 1 : -1;
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x,y)) return 0;
            if (x is not MatchRecord a) return -1;
            if (y is not MatchRecord b) return 1;
            int result=member switch
            {
                nameof(MatchRecord.Favorite) => a.Favorite.CompareTo(b.Favorite),
                nameof(MatchRecord.Participants) => a.Participants.CompareTo(b.Participants),
                nameof(MatchRecord.Started) => string.Compare(a.Started,b.Started,StringComparison.Ordinal),
                nameof(MatchRecord.LocalCivSortKey) => CompareText(a.LocalCivSortKey,b.LocalCivSortKey),
                nameof(MatchRecord.DisplayResult) => CompareText(a.DisplayResult,b.DisplayResult),
                nameof(MatchRecord.Map) => CompareText(a.Map,b.Map),
                _ => CompareText(a.DisplayTitle,b.DisplayTitle)
            };
            return result*_sign;
        }
    }

    private static int CompareText(string? a,string? b) =>
        string.Compare(a,b,StringComparison.CurrentCultureIgnoreCase);
}
