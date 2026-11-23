using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Infrastructure;

namespace DeathNote.Wpf.Views;

/// <summary>One reusable native window, with the full dated nickname history.</summary>
public partial class NicknameHistoryWindow : Window
{
    private ICollectionView? _historyView;
    private bool _allowClose;
    private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private void ConfirmCopy(string message)
    {
        CopyFeedback.Text = message;
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    public NicknameHistoryWindow()
    {
        InitializeComponent();
        _copyTimer.Tick += (_, _) => { _copyTimer.Stop(); CopyFeedback.Text = string.Empty; };
        PreviewKeyDown += (sender, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { HistorySearch.Focus(); HistorySearch.SelectAll(); e.Handled = true; }
            if (e.Key == Key.Escape && HistorySearch.IsKeyboardFocused)
            { HistorySearch.Clear(); e.Handled = true; }
        };
        Closing += (_,e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    public void Present(PlayerRecord player,IReadOnlyList<NicknameHistoryEntry> entries)
    {
        Civ5MarkupText.SetMarkup(PlayerName, player.Nickname);
        PlayerId.Text = player.SteamId;
        TotalCount.Text = $"{entries.Count} recorded names";
        HistorySearch.Clear();
        CopyFeedback.Text = string.Empty;
        HistoryGrid.ItemsSource = entries;
        _historyView = CollectionViewSource.GetDefaultView(HistoryGrid.ItemsSource);
        _historyView.Filter = row => row is NicknameHistoryEntry item &&
            item.Nickname.Contains(HistorySearch.Text,StringComparison.OrdinalIgnoreCase);
        _historyView.Refresh();
        HistoryGrid.SelectedIndex = entries.Count>0 ? 0 : -1;
        if (!IsVisible) Show();
        Activate();
    }

    private void FilterChanged(object sender,TextChangedEventArgs e) => _historyView?.Refresh();

    private void CopySelectedNickname()
    {
        if(HistoryGrid.SelectedItem is NicknameHistoryEntry entry)
        {
            Clipboard.SetText(entry.Nickname);
            ConfirmCopy("Nickname copied");
        }
    }
    private void CopyNicknameClicked(object sender,RoutedEventArgs e) => CopySelectedNickname();
    private void CopyIdClicked(object sender,RoutedEventArgs e)
    {
        if(!string.IsNullOrWhiteSpace(PlayerId.Text))
        {
            Clipboard.SetText(PlayerId.Text);
            ConfirmCopy("Steam ID copied");
        }
    }
    private void CloseClicked(object sender,RoutedEventArgs e) => Hide();
    private void DragTitle(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left)return;
        try { DragMove(); } catch(InvalidOperationException) { }
    }
    public void CloseForShutdown()
    {
        _allowClose=true;
        Close();
    }
}
