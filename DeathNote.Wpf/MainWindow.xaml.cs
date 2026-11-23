using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Views;
using DeathNote.Wpf.Services;

namespace DeathNote.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm=new();
    private UserControl? _active;
    private bool _warmed;
    private bool _closingAfterAutoSave;
    private NicknameHistoryWindow? _nicknameHistoryWindow;
    public MainWindow()
    {
        InitializeComponent();DataContext=_vm;
        PreviewKeyDown += MainKeyDown;
        _active=PlayersPage;
        UpdateNavigation(PlayersPage);
        PlayersPage.OpenHistoryRequested+=sid=>
        {
            MatchEditor.HideEditor();
            _vm.MatchPlayerId=sid;
            _vm.ReviewOnly=false;
            ShowPage(MatchesPage);
        };
        PlayersPage.NicknameHistoryRequested+=async player=>
        {
            var entries=await _vm.LoadNicknameHistoryAsync(player);
            if(entries is null || !IsLoaded)return;
            _nicknameHistoryWindow ??= new NicknameHistoryWindow {Owner=this};
            _nicknameHistoryWindow.Present(player,entries);
        };
        MatchesPage.EditRequested+=match=>{if(_active==MatchesPage)MatchEditor.Open(match);};
        MatchesPage.ParticipantSelected+=steamId=>
        {
            var player=_vm.Players.FirstOrDefault(p=>p.SteamId==steamId);
            if(player is null)
            {
                MessageBox.Show("This participant has no player record yet.","Death Note",
                    MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            _vm.ResetPlayerFilters();
            _vm.SelectedPlayer=player;
            ShowPage(PlayersPage);
            PlayersPage.RevealPlayer(player);
        };
        _vm.MatchReviewRequested+=async id=>
        {
            if(!IsLoaded || _closingAfterAutoSave)return;
            if(!await PlayersPage.FlushPendingAsync(flushAll:true) ||
               !await MatchesPage.FlushPendingAsync())return;
            var details=await _vm.LoadMatchDetailsAsync(id);
            if(details is null)return;
            _vm.MatchPlayerId=null;
            _vm.ReviewOnly=false;
            ShowPage(MatchesPage);
            _vm.SelectedMatch=_vm.Matches.FirstOrDefault(m=>m.Id==id);
            MatchEditor.Open(details, forReview: true);
            if(WindowState==System.Windows.WindowState.Minimized)WindowState=System.Windows.WindowState.Normal;
            Activate();
        };
        SettingsPage.ImportRequested+=async path=>
        {
            PageHost.IsEnabled=false;
            try
            {
                bool playersSaved=await PlayersPage.FlushPendingAsync(flushAll:true);
                bool matchesSaved=await MatchesPage.FlushPendingAsync();
                if(!playersSaved || !matchesSaved)
                {
                    MessageBox.Show("Pending changes could not be saved. Import cancelled.",
                        "Death Note",MessageBoxButton.OK,MessageBoxImage.Warning);
                    return;
                }
                if(await _vm.ImportDatabaseAsync(path))
                {
                    MessageBox.Show("Database imported. Restart Death Note to load the new data.",
                        "Death Note",MessageBoxButton.OK,MessageBoxImage.Information);
                    _closingAfterAutoSave=true;
                    Close();
                }
            }
            finally{PageHost.IsEnabled=true;}
        };
        Loaded+=async (_,_)=>
        {
            if (App.UiBenchmarkMode)
            {
                // Synthetic profiling mode never opens a user SQLite database,
                // starts the game monitor, or checks for releases.
                await UiPerformanceBenchmark.RunAsync(this, _vm, PlayersPage, MatchesPage,
                    StatisticsPage, SettingsPage, ShowPage, App.BenchmarkRuns);
                return;
            }
            if (App.NativeRepairSelfTestMode)
            {
                await Civ5RepairSelfTest.RunAsync(App.GameSelfTestReportPath);
                Application.Current.Shutdown();
                return;
            }
            if (App.GameSelfTestMode)
            {
                await GameIntegrationSelfTest.RunAsync(App.GameSelfTestReportPath);
                Application.Current.Shutdown();
                return;
            }
            if (App.RealBenchmarkMode)
            {
                await RealDataBenchmark.RunAsync(this,_vm,PlayersPage,MatchesPage,
                    StatisticsPage,SettingsPage,ShowPage,
                    App.BenchmarkDatabaseFile,App.BenchmarkLogFile,App.BenchmarkRuns);
                return;
            }
            await _vm.InitializeAsync();
            if (_vm.Ready && _vm.FreshDatabaseCreated && !App.IsShuttingDown)
            {
                if (MessageBox.Show("Your database is ready. Open Game Integration settings to configure the Civilization V tracker?",
                    "Death Note — first launch", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes) ShowPage(SettingsPage);
            }
            _ = Dispatcher.BeginInvoke((Action)PrewarmPages,DispatcherPriority.ApplicationIdle);
        };
        Closing += OnWindowClosing;
    }
    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingAfterAutoSave)
        {
            _nicknameHistoryWindow?.CloseForShutdown();
            _vm.Dispose();
            return;
        }
        // WPF's Closing event is synchronous. Cancel the first close while pending
        // edits are committed, then close again on the dispatcher.
        e.Cancel = true;
        bool playersSaved = await PlayersPage.FlushPendingAsync(flushAll: true);
        bool matchesSaved = await MatchesPage.FlushPendingAsync();
        bool saved = playersSaved && matchesSaved;
        if (!saved && MessageBox.Show(
                "Some player or match changes could not be saved. Close without these changes?",
                "Death Note — unsaved changes", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _closingAfterAutoSave = true;
        _ = Dispatcher.BeginInvoke((Action)Close, DispatcherPriority.Normal);
    }

    private void PrewarmPages()
    {
        if(_warmed)return;
        _warmed=true;
        foreach(UserControl page in new UserControl[]{MatchesPage,StatisticsPage,SettingsPage})
        {
            if(page==_active)continue;
            page.Opacity=0;page.Visibility=Visibility.Visible;
            page.UpdateLayout();page.Visibility=Visibility.Collapsed;
            page.Opacity=1;
        }
        // Measure the persistent inline editor once, invisibly, during idle time.
        MatchEditor.Opacity=0;
        MatchEditor.Visibility=Visibility.Visible;
        MatchEditor.UpdateLayout();
        MatchEditor.Visibility=Visibility.Collapsed;
        MatchEditor.Opacity=1;
        // The nickname dialog is intentionally created on first use, then reused.
        // Instantiating an unused window at startup could surface resource failures
        // before the user opens the nickname history.
    }
    private void MainKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_active == MatchesPage) MatchesPage.FocusSearch();
            else if (_active == PlayersPage) PlayersPage.FocusSearch();
            else { ShowPage(PlayersPage); PlayersPage.FocusSearch(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_active == MatchesPage && MatchesPage.SearchIsFocused)
                MatchesPage.ClearSearch();
            else if (_active == PlayersPage && PlayersPage.SearchIsFocused)
                PlayersPage.ClearSearch();
        }
    }

    private void ShowPage(UserControl target)
    {
        MatchEditor.HideEditor();
        UpdateNavigation(target);
        if(target==_active)return;
        if(_active is not null)_active.Visibility=Visibility.Collapsed;
        target.Visibility=Visibility.Visible;
        target.Opacity=0;
        target.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(105))
        {EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});
        // Render-only translation avoids re-running WPF's layout pass during animation.
        var translation=target.RenderTransform as TranslateTransform??new TranslateTransform();
        target.RenderTransform=translation;
        translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5,0,TimeSpan.FromMilliseconds(105))
            {EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});
        _active=target;
    }
    // Nav selection is a persistent state, not a hover-only effect.
    private void UpdateNavigation(UserControl target)
    {
        PlayersButton.Tag = target == PlayersPage ? "Active" : null;
        MatchesButton.Tag = target == MatchesPage ? "Active" : null;
        StatisticsButton.Tag = target == StatisticsPage ? "Active" : null;
        SettingsButton.Tag = target == SettingsPage ? "Active" : null;
    }
    // The custom titlebar retains mouse-drag, double click to maximize and native resize edges.
    private void DragWindowTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        try { DragMove(); } catch (InvalidOperationException) { /* Ignore a lost drag. */ }
    }
    private void ToggleMaximize() => this.WindowState = this.WindowState == System.Windows.WindowState.Maximized
        ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized;
    private void ToggleWindowMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void MinimizeWindow(object sender, RoutedEventArgs e) => this.WindowState = System.Windows.WindowState.Minimized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowStateChanged(object sender, EventArgs e)
    {
        if (MaximizeButton is not null)
        {
            MaximizeButton.Content = this.WindowState == System.Windows.WindowState.Maximized ? "❐" : "□";
            MaximizeButton.ToolTip = this.WindowState == System.Windows.WindowState.Maximized ? "Restore" : "Maximize";
        }
    }
    private void ShowPlayers(object sender,RoutedEventArgs e)=>ShowPage(PlayersPage);
    private void ShowMatches(object sender,RoutedEventArgs e)
    {
        _vm.MatchPlayerId=null;_vm.ReviewOnly=false;ShowPage(MatchesPage);
    }
    private void ShowStatistics(object sender,RoutedEventArgs e)=>ShowPage(StatisticsPage);
    private void ShowSettings(object sender,RoutedEventArgs e)=>ShowPage(SettingsPage);

}
