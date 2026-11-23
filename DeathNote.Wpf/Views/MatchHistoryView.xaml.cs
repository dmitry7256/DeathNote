using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Services;

namespace DeathNote.Wpf.Views;

public partial class MatchHistoryView : UserControl
{
    public event Action<MatchRecord>? EditRequested;
    public event Action<string>? ParticipantSelected;
    private MainViewModel? _vm;
    private string? _shownMatchId;
    private bool _updatingNote;
    private readonly DispatcherTimer _noteTimer;
    private readonly SemaphoreSlim _noteSaveGate = new(1, 1);
    private readonly Dictionary<string, (MatchRecord match, string note)> _pendingNotes = new(StringComparer.Ordinal);

    public MatchHistoryView()
    {
        InitializeComponent();
        _noteTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
        _noteTimer.Tick += async (_, _) => { _noteTimer.Stop(); await FlushPendingAsync(); };
        DataContextChanged += (_, _) => AttachViewModel();
    }

    public void FocusSearch()
    {
        MatchSearch.Focus();
        MatchSearch.SelectAll();
    }

    public bool SearchIsFocused => MatchSearch.IsKeyboardFocused;

    public void ClearSearch()
    {
        if (!string.IsNullOrEmpty(MatchSearch.Text)) MatchSearch.Clear();
    }

    private void AttachViewModel()
    {
        if (_vm is not null) _vm.PropertyChanged -= ViewModelChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += ViewModelChanged;
        UpdateNoteEditor();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedMatch)) UpdateNoteEditor();
    }

    private void UpdateNoteEditor()
    {
        var match = _vm?.SelectedMatch;
        if (_shownMatchId == match?.Id) return;
        var previousId = _shownMatchId;
        _shownMatchId = match?.Id;
        if (previousId is not null)
        {
            _noteTimer.Stop();
            _ = FlushPendingAsync(onlyId: previousId);
        }
        _updatingNote = true;
        try
        {
            MatchNote.Text = match is null ? "" :
                (_pendingNotes.TryGetValue(match.Id, out var draft) ? draft.note : match.Note);
            MatchNote.IsEnabled = match is not null;
        }
        finally { _updatingNote = false; }
    }

    private void MatchNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingNote || _vm?.Ready != true || _vm.SelectedMatch is not MatchRecord match) return;
        _pendingNotes[match.Id] = (match, MatchNote.Text);
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    private async void MatchNoteLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await FlushPendingAsync();
    }

    /// <summary>Debounced, revision-checked write; retain drafts on failure.</summary>
    public async Task<bool> FlushPendingAsync(string? onlyId = null)
    {
        if (_vm is null) return _pendingNotes.Count == 0;
        _noteTimer.Stop();
        await _noteSaveGate.WaitAsync();
        try
        {
            foreach (var key in _pendingNotes.Keys.ToArray())
            {
                if (onlyId is not null && key != onlyId) continue;
                if (!_pendingNotes.TryGetValue(key, out var draft)) continue;
                var m = draft.match;
                var patch = new MatchPatch(m.Title, draft.note, m.Result, m.WinnerId, m.Favorite, m.ReviewPending, m.Deleted);
                if (!await _vm.SaveMatchCheckedAsync(key, patch)) return false;
                if (_pendingNotes.TryGetValue(key, out var current) && current.note == draft.note)
                    _pendingNotes.Remove(key);
            }
            return true;
        }
        finally { _noteSaveGate.Release(); }
    }

    // Three clicks: ascending, descending, natural (newest-first) order.
    private void MatchesGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(e.Column.SortMemberPath)) return;
        e.Handled = true;
        var next = e.Column.SortDirection switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => (ListSortDirection?)null
        };
        FastGridSort.ApplyMatches(_vm.MatchView,e.Column.SortMemberPath,next);
        foreach (var column in MatchesGrid.Columns)
            column.SortDirection = column == e.Column ? next : null;
    }

    private void OpenMatchParticipant(object sender, RoutedEventArgs e)
    {
        if(sender is Button { CommandParameter: string sid } &&
           sid.Length==17 && sid.All(char.IsAsciiDigit))
            ParticipantSelected?.Invoke(sid);
    }

    private void ResetMatchSorting()
    {
        if (_vm is null) return;
        FastGridSort.ApplyMatches(_vm.MatchView,null,null);
        foreach (var column in MatchesGrid.Columns) column.SortDirection = null;
    }

    private void ResetMatchFilters(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ResetMatchFilters();
        ResetMatchSorting();
    }

    private async void EditMatch(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedMatch is not MatchRecord current) return;
        if (!await FlushPendingAsync()) return;
        var details = await _vm.LoadMatchDetailsAsync(current.Id);
        if (details is not null) EditRequested?.Invoke(details);
    }

    private async void CompleteReview(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedMatch is not MatchRecord current) return;
        if (!await FlushPendingAsync()) return;
        var patch = new MatchPatch(current.Title, current.Note, current.Result, current.WinnerId,
            current.Favorite, false, current.Deleted);
        await _vm.SaveMatchAsync(current.Id, patch);
    }

    private async void DeleteMatch(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedMatch is not MatchRecord current) return;
        if (!await FlushPendingAsync()) return;
        if (MessageBox.Show($"Delete match '{current.DisplayTitle}'?\nA recovery snapshot will be saved before the change.",
            "Death Note", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _vm.SaveMatchAsync(current.Id,
            new MatchPatch(current.Title, current.Note, current.Result, current.WinnerId,
                current.Favorite, current.ReviewPending, true));
    }
}
