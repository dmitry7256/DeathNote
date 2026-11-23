using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Services;

namespace DeathNote.Wpf.Views;

public partial class PlayersView : UserControl
{
    private MainViewModel? _vm;
    private bool _pendingBan;
    private bool _updatingForm;
    // Only one SQLite transaction can be active. Never spin or block the UI thread.
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private const int NoteIdleMilliseconds = 1100;
    private DateTime _lastNoteEditUtc = DateTime.MinValue;
    private string? _noteEditedPlayerId;
    private string? _shownSteamId;
    private readonly DispatcherTimer _noteSaveDelay;
    // Drafts remain queued until SQLite confirms a successful transaction.
    // Retaining them by Steam ID prevents edits being lost when switching players.
    private readonly Dictionary<string, PlayerRecord> _pendingUpdates = new(StringComparer.Ordinal);

    public event Action<string>? OpenHistoryRequested;
    public event Action<PlayerRecord>? NicknameHistoryRequested;

    public PlayersView()
    {
        InitializeComponent();
        _noteSaveDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(NoteIdleMilliseconds)
        };
        _noteSaveDelay.Tick += async (_, _) =>
        {
            _noteSaveDelay.Stop();
            await FlushPendingAsync();
        };
        DataContextChanged += (_, _) => BindViewModel();
    }

    public void FocusSearch()
    {
        PlayerSearch.Focus();
        PlayerSearch.SelectAll();
    }

    public bool SearchIsFocused => PlayerSearch.IsKeyboardFocused;

    public void ClearSearch()
    {
        if (!string.IsNullOrEmpty(PlayerSearch.Text)) PlayerSearch.Clear();
    }

    private void BindViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnChanged;
        }
        UpdateSelection();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPlayer)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var p = _vm?.SelectedPlayer;
        if (_shownSteamId == p?.SteamId) return;
        if (_shownSteamId is not null)
        {
            // Start draining the previous player's edits before resetting the fields.
            _noteSaveDelay.Stop();
            _ = FlushPendingAsync(flushAll: true, onlySteamId: _shownSteamId);
        }
        _shownSteamId = p?.SteamId;
        _updatingForm = true;
        try
        {
            PlayerNote.Text = p is null ? string.Empty :
                (_pendingUpdates.TryGetValue(p.SteamId, out var pending) ? pending.Note : p.Note);
            RatingSelect.SelectedValue = p?.Marker ?? string.Empty;
            FavoriteButtonLabel.Text = p?.Favorite == true ? "Remove from favorites" : "Add to favorites";
            FavoriteButtonIcon.Text = p?.Favorite == true ? "★" : "☆";
            _pendingBan = p?.Autokick == true;
            PaintBanButton();
            AutoSaveStatus.Text = string.Empty;
            AutoSaveStatus.Visibility = Visibility.Collapsed;
        }
        finally { _updatingForm = false; }
    }

    private void IncWins(object s, RoutedEventArgs e) => Adjust("wins", 1);
    private void DecWins(object s, RoutedEventArgs e) => Adjust("wins", -1);
    private void IncLosses(object s, RoutedEventArgs e) => Adjust("losses", 1);
    private void DecLosses(object s, RoutedEventArgs e) => Adjust("losses", -1);
    private void IncLeaves(object s, RoutedEventArgs e) => Adjust("leaves", 1);
    private void DecLeaves(object s, RoutedEventArgs e) => Adjust("leaves", -1);

    private void Adjust(string counter, int delta)
    {
        var p = _vm?.SelectedPlayer;
        if (p is null) return;
        if (counter == "wins") p.Wins = Math.Max(0, p.Wins + delta);
        else if (counter == "losses") p.Losses = Math.Max(0, p.Losses + delta);
        else p.Leaves = Math.Max(0, p.Leaves + delta);
        QueueAutoSave(immediately: true);
    }

    private void PlayerNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingForm || _vm?.SelectedPlayer is not PlayerRecord p) return;
        // Do not mutate the sortable table model on every keystroke.
        // Debounce the editor buffer and commit only after typing becomes idle.
        _lastNoteEditUtc = DateTime.UtcNow;
        _noteEditedPlayerId = p.SteamId;
        QueueAutoSave(immediately: false);
    }

    private async void PlayerNoteLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_updatingForm) await FlushPendingAsync();
    }

    private void RatingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingForm || _vm?.SelectedPlayer is not PlayerRecord p) return;
        p.Marker = RatingSelect.SelectedValue as string ?? string.Empty;
        QueueAutoSave(immediately: true);
    }

    private void OpenSteamProfile(object sender, RoutedEventArgs e)
    {
        var id = _vm?.SelectedPlayer?.SteamId;
        if (id is null || id.Length != 17 || !id.All(char.IsAsciiDigit))
        {
            MessageBox.Show("This player does not have a valid Steam ID.",
                "Death Note", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Never use the nickname or untrusted log text as part of the URL.
        var url = "https://steamcommunity.com/profiles/" + id;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url, UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Could not open the Steam profile.\n" + url,
                "Death Note", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException)
        {
            MessageBox.Show("Could not open the Steam profile.\n" + url,
                "Death Note", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenPlayerHistory(object s, RoutedEventArgs e)
    {
        if (_vm?.SelectedPlayer is PlayerRecord player) OpenHistoryRequested?.Invoke(player.SteamId);
    }

    private void OpenNicknameHistory(object s, RoutedEventArgs e)
    {
        if (_vm?.SelectedPlayer is PlayerRecord player) NicknameHistoryRequested?.Invoke(player);
    }

    private PlayerRecord? CaptureCurrentDraft()
    {
        var p = _vm?.SelectedPlayer;
        if (p is null || !_vm!.Ready) return null;
        return new PlayerRecord
        {
            SteamId = p.SteamId,
            Nickname = p.Nickname,
            Note = PlayerNote.Text,
            Marker = RatingSelect.SelectedValue as string ?? string.Empty,
            Wins = p.Wins,
            Losses = p.Losses,
            Leaves = p.Leaves,
            Autokick = _pendingBan,
            Favorite = p.Favorite,
            PreviousNames = p.PreviousNames
        };
    }

    private void QueueAutoSave(bool immediately)
    {
        if (_updatingForm) return;
        var draft = CaptureCurrentDraft();
        if (draft is null) return;
        _pendingUpdates[draft.SteamId] = draft;
        _noteSaveDelay.Stop();
        if (immediately) _ = FlushPendingAsync();
        else _noteSaveDelay.Start();
    }

    /// <summary>
    /// One revision-checked transaction per quiet period. Drafts remain in memory
    /// until confirmed by SQLite; a newer draft is never replaced by an older save.
    /// On window close or player switch flushAll drains every pending player.
    /// </summary>
    public async Task<bool> FlushPendingAsync(bool flushAll = false, string? onlySteamId = null)
    {
        _noteSaveDelay.Stop();
        if (_vm is null) return _pendingUpdates.Count == 0;
        await _saveGate.WaitAsync();
        try
        {
            while (_pendingUpdates.Count > 0)
            {
                PlayerRecord? draft;
                if (onlySteamId is not null)
                {
                    if (!_pendingUpdates.TryGetValue(onlySteamId, out draft)) break;
                }
                else draft = _pendingUpdates.Values.First();
                var isActivelyTyping = !flushAll &&
                    draft.SteamId == _noteEditedPlayerId &&
                    _shownSteamId == draft.SteamId &&
                    PlayerNote.IsKeyboardFocusWithin &&
                    (DateTime.UtcNow - _lastNoteEditUtc).TotalMilliseconds < NoteIdleMilliseconds;
                if (isActivelyTyping)
                {
                    _noteSaveDelay.Start();
                    return true;
                }

                var saved = await _vm.SavePlayerCheckedAsync(draft);
                if (!saved)
                {
                    if (_vm.SelectedPlayer?.SteamId == draft.SteamId)
                    {
                        AutoSaveStatus.Text = "Save failed — changes pending";
                        AutoSaveStatus.Visibility = Visibility.Visible;
                    }
                    // Never lose the failed draft, even if a newer edit arrived.
                    _pendingUpdates.TryAdd(draft.SteamId, draft);
                    return false;
                }

                if (_pendingUpdates.TryGetValue(draft.SteamId, out var current) &&
                    ReferenceEquals(current, draft))
                    _pendingUpdates.Remove(draft.SteamId);

                if (_vm.SelectedPlayer?.SteamId == draft.SteamId &&
                    !_pendingUpdates.ContainsKey(draft.SteamId))
                {
                    AutoSaveStatus.Text = string.Empty;
                    AutoSaveStatus.Visibility = Visibility.Collapsed;
                }

                // New input may arrive while SQLite writes the previous draft.
                // Leave newer input queued for the next idle interval, not for
                // an immediate second transaction in the same flush.
                if (!flushAll) break;
            }
            if (_pendingUpdates.Count > 0 && (!flushAll || onlySteamId is not null))
                _noteSaveDelay.Start();
            return true;
        }
        finally { _saveGate.Release(); }
    }

    private async void ToggleFavorite(object s, RoutedEventArgs e)
    {
        var p = _vm?.SelectedPlayer;
        if (_vm is null || p is null) return;
        var id = p.SteamId;
        await _vm.SetFavoriteAsync(id, !p.Favorite);
        if (_vm.SelectedPlayer?.SteamId == id)
        {
            FavoriteButtonLabel.Text = p.Favorite ? "Remove from favorites" : "Add to favorites";
            FavoriteButtonIcon.Text = p.Favorite ? "★" : "☆";
        }
    }

    private void ToggleBan(object s, RoutedEventArgs e)
    {
        if (_vm?.SelectedPlayer is null) return;
        _pendingBan = !_pendingBan;
        PaintBanButton();
        QueueAutoSave(immediately: true);
    }

    private void PaintBanButton()
    {
        BanButtonLabel.Text = _pendingBan ? "Remove ban" : "Ban player";
    }

    /// <summary>Explicit navigation from Match History; ordinary sorting never autoscrolls.</summary>
    public void RevealPlayer(PlayerRecord player)
    {
        _vm!.SelectedPlayer=player;
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            if(_vm?.SelectedPlayer?.SteamId!=player.SteamId)return;
            PlayersGrid.SelectedItem=player;
            PlayersGrid.ScrollIntoView(player);
            PlayersGrid.UpdateLayout();
            PlayersGrid.ScrollIntoView(player);
        }),DispatcherPriority.Loaded);
    }

    private void PlayersGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(e.Column.SortMemberPath)) return;
        e.Handled = true;
        var column = e.Column;
        var next = column.SortDirection switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => (ListSortDirection?)null
        };
        FastGridSort.ApplyPlayers(_vm.PlayerView,column.SortMemberPath,next);
        foreach (var other in PlayersGrid.Columns) other.SortDirection = other == column ? next : null;
    }

    private void ClearSorting()
    {
        if (_vm is null) return;
        FastGridSort.ApplyPlayers(_vm.PlayerView,null,null);
        foreach (var column in PlayersGrid.Columns) column.SortDirection = null;
    }

    private void ResetFilters(object s, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ResetPlayerFilters();
        ClearSorting();
    }
}
