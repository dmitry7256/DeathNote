using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Models;
using DeathNote.Wpf.Services;

namespace DeathNote.Wpf.Infrastructure;

public sealed class MainViewModel : Observable, IDisposable
{
    private SqliteRepository _repo=new(DataWorkspace.DatabaseFile);
    private readonly DispatcherTimer _revisionTimer;
    private readonly DispatcherTimer _playerSearchTimer;
    private readonly DispatcherTimer _matchSearchTimer;
    private readonly DispatcherTimer _bridgeTimer;
    private readonly SemaphoreSlim _bridgeGate=new(1,1);
    private readonly SemaphoreSlim _gameIngestGate=new(1,1);
    private string _bridgeStatus="Waiting for Civ V lobby bridge";
    private bool _autoKickEnabled;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly SemaphoreSlim _reloadGate=new(1,1);
    private GameMonitorService? _monitor;
    private GameLogIngestionService? _ingestor;
    private readonly DispatcherTimer _gameExitTimer;
    private bool _gameSeenRunning;
    private int _missingGameChecks;
    public event Action<string>? MatchReviewRequested;
    private bool _reviewNotificationsEnabled=true;
    private PlayerRecord? _selectedPlayer;
    private MatchRecord? _selectedMatch;
    private string _playerQuery="",_matchQuery="",_gameStatus="Waiting for the game",_dataStatus="Connecting to database…";
    private string _localNickname="";
    private string? _matchPlayerId;
    private bool _onlyFavorites,_onlyBanned,_onlyInLobby,_matchFavoritesOnly,_reviewOnly,_initialized;
    private bool _updateAvailable,_updateCheckInProgress;
    private string _updateNotice="No WPF update detected.";
    private string? _updateDownloadUrl;
    private int _pendingReviewCount;
    private readonly SemaphoreSlim _updateGate=new(1,1);
    private HashSet<string> _lobbyIds=new(StringComparer.Ordinal);
    private bool _lobbyActive;
    private bool _gameInProgress;
    public bool FreshDatabaseCreated { get; private set; }
    private string _playerRatingFilter="all";
    private long _playerRevision=-1,_gameRevision=-1,_prefRevision=-1;
    private int _liveEventCount;
    private int _shownCount;
    private int _visibleMatchCount;
    private GameBridgeService.PlayerState[]? _bridgeCachedPlayers;
    private long _bridgeCachedPlayerRevision=long.MinValue;
    private long _bridgeCachedPreferenceRevision=long.MinValue;
    public BatchObservableCollection<PlayerRecord> Players {get;}=[];
    public BatchObservableCollection<MatchRecord> Matches {get;}=[];
    public ICollectionView PlayerView {get;}
    public ICollectionView MatchView {get;}
    public StatisticsModel Stats {get;}=new();
    public string Workspace=>App.Workspace;
    public string GameStatus {get=>_gameStatus;private set=>Set(ref _gameStatus,value);}
    public string DataStatus {get=>_dataStatus;private set=>Set(ref _dataStatus,value);}
    public int LiveEventCount {get=>_liveEventCount;private set=>Set(ref _liveEventCount,value);}
    public bool AutoKickEnabled {get=>_autoKickEnabled;private set {if(Set(ref _autoKickEnabled,value))Notify(nameof(AutoKickStatus));}}
    public string AutoKickStatus => AutoKickEnabled ? "Auto-kick: on" : "Auto-kick: off";
    public string BridgeStatus {get=>_bridgeStatus;private set=>Set(ref _bridgeStatus,value);}
    public int DatabaseCount => Players.Count;
    public int ShownCount => _shownCount;
    public int GameCount => Stats.Total;
    public int LobbyCount => _lobbyActive ? _lobbyIds.Count : 0;
    public int BannedCount => Players.Count(p=>p.Autokick);
    public string PlayerCountLabel=>$"{_shownCount} / {Players.Count} players";
    public string MatchCountLabel=>$"{_visibleMatchCount} / {Matches.Count} matches";
    public int PendingReviewCount { get=>_pendingReviewCount; private set
    {
        if (Set(ref _pendingReviewCount,value)) Notify(nameof(HasPendingReviews));
    }}
    public bool HasPendingReviews => PendingReviewCount>0;
    public bool UpdateAvailable { get=>_updateAvailable; private set=>Set(ref _updateAvailable,value); }
    public string UpdateNotice { get=>_updateNotice; private set=>Set(ref _updateNotice,value); }
    public string? UpdateDownloadUrl { get=>_updateDownloadUrl; private set=>Set(ref _updateDownloadUrl,value); }
    public bool UpdateCheckInProgress { get=>_updateCheckInProgress; private set=>Set(ref _updateCheckInProgress,value); }

    public async Task CheckForUpdatesAsync()
    {
        if (!_updateGate.Wait(0)) return;
        try
        {
            UpdateCheckInProgress=true;
            var result=await UpdateCheckService.CheckAsync(_lifetime.Token);
            UpdateDownloadUrl=result.DownloadUrl;
            UpdateNotice=result.Description;
            UpdateAvailable=result.DownloadUrl is not null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // Network failure is not an available update. Keep the last verified notice.
            UpdateNotice="Update check unavailable. Try again in Settings.";
        }
        finally
        {
            UpdateCheckInProgress=false;
            _updateGate.Release();
        }
    }
    public string LocalNickname {get=>_localNickname;set=>Set(ref _localNickname,value);}
    public string? MatchPlayerId {get=>_matchPlayerId;set
    {
        if(Set(ref _matchPlayerId,value))
        {
            Notify(nameof(MatchPlayerFilterLabel));
            RefreshMatchFilters();
        }
    }}
    public string MatchPlayerFilterLabel => string.IsNullOrEmpty(MatchPlayerId)
        ? "All matches" : "Player matches: "+(Players.FirstOrDefault(p=>p.SteamId==MatchPlayerId)?.Nickname??MatchPlayerId);
    public PlayerRecord? SelectedPlayer {get=>_selectedPlayer;set
    {
        if (Set(ref _selectedPlayer,value)) Notify(nameof(PreviousNameSearchHint));
    }}
    public string PreviousNameSearchHint => _selectedPlayer is not null &&
        !string.IsNullOrWhiteSpace(_playerQuery) &&
        !_selectedPlayer.Nickname.Contains(_playerQuery,StringComparison.OrdinalIgnoreCase) &&
        _selectedPlayer.PreviousNames.Contains(_playerQuery,StringComparison.OrdinalIgnoreCase)
        ? "Found via previous nickname" : string.Empty;
    public MatchRecord? SelectedMatch {get=>_selectedMatch;set=>Set(ref _selectedMatch,value);}
    public bool OnlyFavorites {get=>_onlyFavorites;set {if(Set(ref _onlyFavorites,value))RefreshPlayerFilters();}}
    public bool OnlyBanned {get=>_onlyBanned;set {if(Set(ref _onlyBanned,value))RefreshPlayerFilters();}}
    public bool OnlyInLobby {get=>_onlyInLobby;set {if(Set(ref _onlyInLobby,value))RefreshPlayerFilters();}}
    public string LobbyStatusLabel => _gameInProgress ? "In game" : "In lobby";
    public string LobbyStateLabel => LobbyStatusLabel;
    public string PlayerRatingFilter { get=>_playerRatingFilter; set { if(Set(ref _playerRatingFilter,value)) RefreshPlayerFilters(); } }
    public bool MatchFavoritesOnly {get=>_matchFavoritesOnly;set {if(Set(ref _matchFavoritesOnly,value))RefreshMatchFilters();}}
    public bool ReviewOnly {get=>_reviewOnly;set {if(Set(ref _reviewOnly,value))RefreshMatchFilters();}}
    public string PlayerQuery {get=>_playerQuery;set {if(Set(ref _playerQuery,value)) { _playerSearchTimer.Stop(); _playerSearchTimer.Start(); }}}
    public string MatchQuery {get=>_matchQuery;set {if(Set(ref _matchQuery,value)) { _matchSearchTimer.Stop(); _matchSearchTimer.Start(); }}}
    // Diagnostic UI events only. The real-data profiler never initializes the
    // repository or game bridge. No database mutation is possible here.
    public void SetBenchmarkGameEvents(int count)
    {
        if (_initialized) return;
        LiveEventCount=count;
        GameStatus="Log events scanned (read-only)";
    }
    public bool Ready => _initialized;

    public MainViewModel()
    {
        PlayerView=CollectionViewSource.GetDefaultView(Players);
        PlayerView.Filter=x=> x is PlayerRecord p &&
            (!OnlyFavorites || p.Favorite) &&
            (!OnlyBanned || p.Autokick) &&
            (!OnlyInLobby || (_lobbyActive && _lobbyIds.Contains(p.SteamId))) &&
            (PlayerRatingFilter switch
            {
                "good" => p.Marker=="good",
                "warn" => p.Marker=="warn",
                "bad" => p.Marker=="bad",
                "unrated" => string.IsNullOrWhiteSpace(p.Marker),
                _ => true
            }) &&
            (string.IsNullOrWhiteSpace(PlayerQuery) || p.Nickname.Contains(PlayerQuery,StringComparison.OrdinalIgnoreCase) ||
             p.SteamId.Contains(PlayerQuery,StringComparison.OrdinalIgnoreCase) || p.PreviousNames.Contains(PlayerQuery,StringComparison.OrdinalIgnoreCase));
        MatchView=CollectionViewSource.GetDefaultView(Matches);
        MatchView.Filter=x=>x is MatchRecord m && !m.Deleted &&
            (string.IsNullOrEmpty(MatchPlayerId) || m.PlayerDetails.Any(p=>p.SteamId==MatchPlayerId)) &&
            (!ReviewOnly || m.ReviewPending) && (!MatchFavoritesOnly || m.Favorite) &&
            (string.IsNullOrWhiteSpace(MatchQuery) ||
             m.DisplayTitle.Contains(MatchQuery,StringComparison.OrdinalIgnoreCase) ||
             m.DisplayResult.Contains(MatchQuery,StringComparison.OrdinalIgnoreCase) ||
             m.ParticipantNames.Contains(MatchQuery,StringComparison.OrdinalIgnoreCase) ||
             m.Map.Contains(MatchQuery,StringComparison.OrdinalIgnoreCase));
        // The source collection is already newest-first; no active sort is shown until a user clicks a column.
        // Search is debounced independently per table: typing in Players must
        // not rebuild the hidden Match History view and vice versa.
        _playerSearchTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(200)};
        _playerSearchTimer.Tick+=(_,_)=>{_playerSearchTimer.Stop();RefreshPlayerFilters();};
        _matchSearchTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(200)};
        _matchSearchTimer.Tick+=(_,_)=>{_matchSearchTimer.Stop();RefreshMatchFilters();};
        _revisionTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(3)};
        _revisionTimer.Tick+=async (_,_)=>await Safe(async()=>await ReloadIfChangedAsync(),false);
        _bridgeTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(8)};
        _bridgeTimer.Tick+=async (_,_)=>await SyncGameBridgeAsync();
        _gameExitTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(10)};
        _gameExitTimer.Tick+=async (_,_)=>await CheckGameExitAsync();
    }

    public async Task InitializeAsync()
    {
        if(!DataWorkspace.Available)
        {
            // A new installation always starts from an empty template unless
            // the user explicitly chooses to import existing SQLite data.
            // Installed files can be replaced without touching this directory.
            var safe=App.Workspace;
            var file=DataWorkspace.DatabaseFile;
            var oldPreview=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeathNote.Wpf","preview","data","DeathNote.db");
            string? importSource=null;
            bool useEmpty=false;

            // Existing 3.20 portable installations have a separate preview database.
            // Offer a non-destructive migration once; never overwrite it or silently
            // replace an already initialized permanent database.
            if(File.Exists(oldPreview) &&
                !Path.GetFullPath(oldPreview).Equals(Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase))
            {
                var migrate=MessageBox.Show(
                    "A database from an earlier Death Note WPF preview was found.\n\n"+
                    "Yes — copy it safely to the installed version (original stays intact).\n"+
                    "No — choose another database or start empty.\n"+
                    "Cancel — exit.",
                    "Death Note — existing data",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
                if(migrate==MessageBoxResult.Cancel){Application.Current.Shutdown();return;}
                if(migrate==MessageBoxResult.Yes)importSource=oldPreview;
            }
            if(importSource is null)
            {
                var choice=MessageBox.Show(
                    "Welcome to Death Note!\n\n"+
                    "Yes — import an existing DeathNote.db (players, matches, notes and bans).\n"+
                    "No — create a new EMPTY database.\n"+
                    "Cancel — exit.\n\n"+
                    "Your database will be stored in your Windows user profile, separate from the installed program.",
                    "Death Note — first launch",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
                if(choice==MessageBoxResult.Cancel){Application.Current.Shutdown();return;}
                useEmpty=choice==MessageBoxResult.No;
                if(!useEmpty)
                {
                    var dialog=new Microsoft.Win32.OpenFileDialog
                    {
                        Title="Import DeathNote.db from another installation",
                        Filter="Death Note SQLite (*.db)|*.db",CheckFileExists=true,Multiselect=false
                    };
                    if(dialog.ShowDialog()!=true){Application.Current.Shutdown();return;}
                    importSource=dialog.FileName;
                }
            }
            Directory.CreateDirectory(safe);
            string staged=Path.Combine(safe,".deathnote-first-run-"+Guid.NewGuid().ToString("N")+".db");
            try
            {
                if(File.Exists(file))throw new IOException("A database already exists: "+file);
                if(importSource is not null)
                {
                    var original=new SqliteRepository(importSource);
                    if(!await original.CheckIntegrityAsync(_lifetime.Token))
                        throw new InvalidDataException("The selected SQLite database failed integrity checks.");
                    // Validate required tables, then make a WAL-safe online backup.
                    await original.ReadSnapshotAsync(_lifetime.Token);
                    await original.CreateBackupAsync(staged,_lifetime.Token);
                }
                else
                {
                    var resource=Application.GetResourceStream(new Uri("/Assets/empty-deathnote-v3.db",UriKind.Relative));
                    if(resource is null)throw new InvalidDataException("The empty database template is missing.");
                    using(var source=resource.Stream)
                    using(var target=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        await source.CopyToAsync(target,_lifetime.Token);
                }
                var candidate=new SqliteRepository(staged);
                if(!await candidate.CheckIntegrityAsync(_lifetime.Token))
                    throw new InvalidDataException("The prepared database failed its integrity check.");
                await candidate.ReadSnapshotAsync(_lifetime.Token);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                // Atomic same-volume move, no overwrite. Older user data is untouched.
                File.Move(staged,file);
                FreshDatabaseCreated=true;
                _repo=new SqliteRepository(file);
                Notify(nameof(Workspace));
            }
            catch(Exception error)
            {
                DataStatus="Could not prepare database: "+error.Message;
                MessageBox.Show(error.Message,"Death Note — database",MessageBoxButton.OK,MessageBoxImage.Error);
                return;
            }
            finally {try{File.Delete(staged);}catch(IOException){}}
        }
        try
        {
            await ReloadIfChangedAsync(force:true);
            _initialized=true;
            // Update checks are user initiated in Settings; the old notification alert is removed.
            _revisionTimer.Start();
            _bridgeTimer.Start();
            _=SyncGameBridgeAsync();
            string logfile=Civ5IntegrationService.GetLogFilePath();
            var ingestor=new GameLogIngestionService(_repo,logfile);
            _ingestor=ingestor;
            _monitor=new GameMonitorService(logfile,(number,status)=>
                Application.Current.Dispatcher.BeginInvoke(()=>
                {
                    if(_lifetime.IsCancellationRequested)return;
                    if(number>0)LiveEventCount+=number;
                    GameStatus=status;
                }), roster=>Application.Current.Dispatcher.BeginInvoke(()=>
                {
                    if(_lifetime.IsCancellationRequested)return;
                    _lobbyActive=roster is not null;
                    _gameInProgress=_monitor?.InGame==true;
                    _lobbyIds=roster is null ? new HashSet<string>(StringComparer.Ordinal) :
                        new HashSet<string>(roster,StringComparer.Ordinal);
                    Notify(nameof(LobbyStatusLabel));
                    Notify(nameof(LobbyCount));
                    if(OnlyInLobby)RefreshPlayerFilters();
                }),async entries=>
                {
                    await _gameIngestGate.WaitAsync(_lifetime.Token);
                    try
                    {
                        var result=await ingestor.IngestAsync(entries,_localNickname,_lifetime.Token);
                        if(result.PlayersChanged || result.MatchesChanged)
                            _ = Application.Current.Dispatcher.BeginInvoke((Action)(()=>
                                _ = HandleGameImportAsync(result)),DispatcherPriority.Background);
                    }
                    finally { _gameIngestGate.Release(); }
                });
            _monitor.Start();
            _gameExitTimer.Start();
            // An online daily snapshot is asynchronous and never blocks window opening.
            _=Safe(async()=>await new DailyBackupService(_repo).EnsureTodayAsync(_lifetime.Token),false);
        }
        catch(Exception e){DataStatus="Read error: "+e.Message;}
    }

    private async Task CheckGameExitAsync()
    {
        if(_ingestor is null || !_ingestor.HasActiveMatch || _lifetime.IsCancellationRequested)
        { _missingGameChecks=0;return; }
        try
        {
            // Do not infer a completed match merely because Lua.log went idle.
            // Only finish after the Civ V process was positively observed and
            // then found absent in two separate checks.
            bool running=await Task.Run(() =>
            {
                foreach(var name in new[]{"CivilizationV", "CivilizationV_DX11", "CivilizationV_Tablet"})
                {
                    var processes=System.Diagnostics.Process.GetProcessesByName(name);
                    try { if(processes.Length>0)return true; }
                    finally { foreach(var process in processes)process.Dispose(); }
                }
                return false;
            },_lifetime.Token);
            if(running){_gameSeenRunning=true;_missingGameChecks=0;return;}
            if(!_gameSeenRunning || ++_missingGameChecks<2)return;
            await _gameIngestGate.WaitAsync(_lifetime.Token);
            GameImportResult result;
            try { result=await _ingestor.FinishAfterGameExitAsync(_lifetime.Token); }
            finally { _gameIngestGate.Release(); }
            _missingGameChecks=0;_gameSeenRunning=false;
            // Release the preserved lobby roster when the game disappears
            // without a Lua END event (normal END already clears it).
            _monitor?.ClearActiveGame();
            if(result.PlayersChanged || result.MatchesChanged)await HandleGameImportAsync(result);
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception error) { GameStatus="Game exit detection: "+error.Message; }
    }

    private async Task HandleGameImportAsync(GameImportResult result)
    {
        if(_lifetime.IsCancellationRequested)return;
        await Safe(async()=>
        {
            await ReloadIfChangedAsync(force:true);
            if(_reviewNotificationsEnabled && result.NewReviews.Count>0)
            {
                // The game may be foregrounded; the main window raises its
                // inline match editor after the new row is committed to SQLite.
                MatchReviewRequested?.Invoke(result.NewReviews[^1]);
            }
        },notify:false);
    }

    public async Task ReloadIfChangedAsync(bool force=false)
    {
        var ct=_lifetime.Token;
        if(force)await _reloadGate.WaitAsync(ct);
        else if(!_reloadGate.Wait(0))return;
        try
        {
            if(!DataWorkspace.Available)return;
            if (!force)
            {
                var (pr,gr,pref)=await _repo.ReadRevisionsAsync(ct);
                if(pr==_playerRevision && gr==_gameRevision && pref==_prefRevision)return;
            }
            // ReadSnapshotAsync already reads revisions in its transaction;
            // force refreshes no longer open a second connection just to recheck.
            DataStatus="Refreshing data…";
            var snapshot=await _repo.ReadSnapshotAsync(ct,cachedPlayers:_playerRevision,cachedGames:_gameRevision);
            string? sid=SelectedPlayer?.SteamId,mid=SelectedMatch?.Id;
            long oldPrefRevision=_prefRevision;
            bool playersChanged=snapshot.PlayersRevision!=_playerRevision;
            bool gamesChanged=snapshot.GamesRevision!=_gameRevision;
            if(playersChanged)MergePlayers(snapshot.Players);
            if(gamesChanged)MergeMatches(snapshot.Matches);
            // Preferences can change independently of the player collection.
            if(snapshot.PreferencesRevision!=_prefRevision && !playersChanged)
                foreach(var player in Players)player.Favorite=snapshot.Favorites.Contains(player.SteamId);
            SelectedPlayer=sid is null?SelectedPlayer:Players.FirstOrDefault(x=>x.SteamId==sid);
            SelectedMatch=mid is null?SelectedMatch:Matches.FirstOrDefault(x=>x.Id==mid);
            LocalNickname=snapshot.LocalNickname;
            AutoKickEnabled=snapshot.AutoKickEnabled;
            _reviewNotificationsEnabled=snapshot.ReviewNotifications;
            _playerRevision=snapshot.PlayersRevision;_gameRevision=snapshot.GamesRevision;_prefRevision=snapshot.PreferencesRevision;
            if (playersChanged || snapshot.PreferencesRevision!=oldPrefRevision) RefreshPlayerFilters();
            if (gamesChanged) RefreshMatchFilters();
            // Populate the detail panels on first launch; do not reset a user's
            // deliberate selection (or lack of one) during later refreshes.
            if (!_initialized)
            {
                if (SelectedPlayer is null) SelectedPlayer = PlayerView.Cast<PlayerRecord>().FirstOrDefault();
                if (SelectedMatch is null) SelectedMatch = MatchView.Cast<MatchRecord>().FirstOrDefault();
            }
            if(gamesChanged)
            {
                Stats.Recalculate(Matches);
                PendingReviewCount=Matches.Count(m=>!m.Deleted && m.ReviewPending);
            }
            Notify(nameof(DatabaseCount));Notify(nameof(ShownCount));
            Notify(nameof(BannedCount));Notify(nameof(GameCount));
            if (_initialized && (playersChanged || gamesChanged || snapshot.PreferencesRevision!=oldPrefRevision))
                _=SyncGameBridgeAsync();
            DataStatus=$"SQLite v3 · {Players.Count} players · {Matches.Count} matches";
        }
        finally {_reloadGate.Release();}
    }

    private void MergePlayers(IReadOnlyList<PlayerRecord> incoming)
    {
        var existing=Players.ToDictionary(x=>x.SteamId,StringComparer.Ordinal);
        List<PlayerRecord> merged=new(incoming.Count);
        foreach(var p in incoming)
        {
            if(existing.TryGetValue(p.SteamId,out var old))
            {
                old.CopyFrom(p);
                merged.Add(old);
            }
            else merged.Add(p);
        }
        // In-place changes preserve selection, scrolling, and row visuals.
        // Structural changes send one Reset, never N separate Add/Remove signals.
        if(!Players.SequenceEqual(merged))Players.ReplaceAll(merged);
        if(!string.IsNullOrEmpty(MatchPlayerId))Notify(nameof(MatchPlayerFilterLabel));
    }
    private void MergeMatches(IReadOnlyList<MatchRecord> incoming)
    {
        var existing=Matches.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        List<MatchRecord> merged=new(incoming.Count);
        foreach(var m in incoming)
        {
            if(existing.TryGetValue(m.Id,out var old))
            {
                old.CopyFrom(m);
                merged.Add(old);
            }
            else merged.Add(m);
        }
        if(!Matches.SequenceEqual(merged))Matches.ReplaceAll(merged);
    }
    public void RefreshFilters()
    {
        RefreshPlayerFilters();
        RefreshMatchFilters();
    }

    // Reset all related properties in one batch. Previously the Reset button
    // refreshed a 5,000-row view once for each changed checkbox/search field.
    public void ResetPlayerFilters()
    {
        _playerSearchTimer.Stop();
        Set(ref _playerQuery,string.Empty,nameof(PlayerQuery));
        Set(ref _onlyFavorites,false,nameof(OnlyFavorites));
        Set(ref _onlyBanned,false,nameof(OnlyBanned));
        Set(ref _onlyInLobby,false,nameof(OnlyInLobby));
        Set(ref _playerRatingFilter,"all",nameof(PlayerRatingFilter));
        RefreshPlayerFilters();
    }

    public void ResetMatchFilters()
    {
        _matchSearchTimer.Stop();
        Set(ref _matchQuery,string.Empty,nameof(MatchQuery));
        Set(ref _matchFavoritesOnly,false,nameof(MatchFavoritesOnly));
        Set(ref _reviewOnly,false,nameof(ReviewOnly));
        if (Set(ref _matchPlayerId,null,nameof(MatchPlayerId)))
            Notify(nameof(MatchPlayerFilterLabel));
        RefreshMatchFilters();
    }

    public void RefreshPlayerFilters()
    {
        var playerId=SelectedPlayer?.SteamId;
        PlayerView.Refresh();
        if (playerId is not null && SelectedPlayer?.SteamId != playerId)
        {
            var player=Players.FirstOrDefault(x=>x.SteamId==playerId);
            if (player is not null && PlayerView.Contains(player)) SelectedPlayer=player;
        }
        _shownCount=PlayerView.Cast<object>().Count();
        Notify(nameof(PlayerCountLabel));
        Notify(nameof(ShownCount));
        Notify(nameof(PreviousNameSearchHint));
    }

    public void RefreshMatchFilters()
    {
        var matchId=SelectedMatch?.Id;
        MatchView.Refresh();
        if (matchId is not null && SelectedMatch?.Id != matchId)
        {
            var match=Matches.FirstOrDefault(x=>x.Id==matchId);
            if (match is not null && MatchView.Contains(match)) SelectedMatch=match;
        }
        _visibleMatchCount=MatchView.Cast<object>().Count();
        Notify(nameof(MatchCountLabel));
    }
    public async Task<bool> SavePlayerCheckedAsync(PlayerRecord modified)
    {
        if (!_initialized) return false;
        // Coordinate with the 3-second revision poll so a concurrent refresh
        // cannot replace the committed revision with an older snapshot.
        try { await _reloadGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        try
        {
            long newRevision=await _repo.SavePlayerWithRevisionAsync(modified,_playerRevision,_lifetime.Token);
            _playerRevision=newRevision;
            var current=Players.FirstOrDefault(x=>x.SteamId==modified.SteamId);
            if (current is not null)
            {
                bool banChanged=current.Autokick!=modified.Autokick;
                bool markerChanged=!string.Equals(current.Marker,modified.Marker,StringComparison.Ordinal);
                bool noteChanged=!string.Equals(current.Note,modified.Note,StringComparison.Ordinal);
                bool winsChanged=current.Wins!=modified.Wins;
                bool lossesChanged=current.Losses!=modified.Losses;
                bool leavesChanged=current.Leaves!=modified.Leaves;
                current.CopyFrom(modified);
                if (banChanged && modified.Autokick) current.BanUntil=string.Empty;
                if (banChanged) Notify(nameof(BannedCount));
                // A full CollectionView refresh is only needed when a filter or
                // active sort key is affected. Plain Note typing no longer
                // rebuilds a 5,000-row filtered view on every autosave.
                bool needsFilterRefresh=(OnlyBanned && banChanged) ||
                    (PlayerRatingFilter!="all" && markerChanged);
                bool needsSortRefresh=FastGridSort.ActivePlayerMember(PlayerView) switch
                {
                    nameof(PlayerRecord.Autokick) => banChanged,
                    nameof(PlayerRecord.Marker) => markerChanged,
                    nameof(PlayerRecord.Note) => noteChanged,
                    nameof(PlayerRecord.Wins) => winsChanged,
                    nameof(PlayerRecord.Losses) => lossesChanged,
                    nameof(PlayerRecord.Leaves) => leavesChanged,
                    _ => false
                };
                if (needsFilterRefresh || needsSortRefresh) RefreshPlayerFilters();
            }
            // The game bridge checks the revision to rebuild its snapshot only
            // on a change; no full SQLite re-read is required here.
            _ = Application.Current.Dispatcher.BeginInvoke(
                (Action)(() => { if (!_lifetime.IsCancellationRequested) _ = SyncGameBridgeAsync(); }),
                DispatcherPriority.ApplicationIdle);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            DataStatus = "Save failed: " + error.Message;
            MessageBox.Show(error.Message,"Death Note",MessageBoxButton.OK,MessageBoxImage.Error);
            return false;
        }
        finally { _reloadGate.Release(); }
    }
    public async Task SavePlayerAsync(PlayerRecord modified) => await SavePlayerCheckedAsync(modified);
    public Task SetFavoriteAsync(string id,bool value)=>Safe(async()=>
    {
        if(!_initialized)return;
        await _repo.SaveFavoriteAsync(id,value,_prefRevision,_lifetime.Token);
        await ReloadIfChangedAsync(force:true);
    });
    public Task SetAutoKickEnabledAsync(bool enabled)=>Safe(async()=>
    {
        if(!_initialized)return;
        await _repo.SaveAutoKickEnabledAsync(enabled,_prefRevision,_lifetime.Token);
        await ReloadIfChangedAsync(force:true);
        await SyncGameBridgeAsync();
    });

    private async Task SyncGameBridgeAsync()
    {
        if (!_initialized || !_bridgeGate.Wait(0)) return;
        try
        {
            var log = Civ5IntegrationService.GetLogFilePath();
            // Reuse the immutable bridge snapshot across the 8-second polls.
            // Capturing thousands of UI-bound player objects on every tick
            // caused allocations and occasional input latency spikes.
            if (_bridgeCachedPlayers is null ||
                _bridgeCachedPlayerRevision!=_playerRevision ||
                _bridgeCachedPreferenceRevision!=_prefRevision)
            {
                _bridgeCachedPlayers = Players.Select(p => new GameBridgeService.PlayerState(
                    p.SteamId,p.Nickname,p.Note,p.Marker,p.Favorite,p.Autokick,
                    p.BanUntil,p.Wins,p.Losses,p.Leaves)).ToArray();
                _bridgeCachedPlayerRevision=_playerRevision;
                _bridgeCachedPreferenceRevision=_prefRevision;
            }
            BridgeStatus = await GameBridgeService.SynchronizeAsync(
                log,_bridgeCachedPlayers,AutoKickEnabled,_lifetime.Token);
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception) { BridgeStatus = "Lobby bridge unavailable; retrying"; }
        finally { _bridgeGate.Release(); }
    }

    public Task SetLocalNicknameAsync()=>Safe(async()=>
    {
        if(!_initialized)return;
        await _repo.SaveLocalNicknameAsync(LocalNickname.Trim(),_prefRevision,_lifetime.Token);
        await ReloadIfChangedAsync(force:true);
    });
    public async Task<IReadOnlyList<NicknameHistoryEntry>?> LoadNicknameHistoryAsync(PlayerRecord player)
    {
        try { return await _repo.ReadNicknameHistoryAsync(player.SteamId,player.Nickname,_lifetime.Token); }
        catch(Exception e)
        {
            DataStatus="Nickname history unavailable: "+e.Message;
            MessageBox.Show(e.Message,"Nickname history",MessageBoxButton.OK,MessageBoxImage.Error);
            return null;
        }
    }

    public async Task<MatchRecord?> LoadMatchDetailsAsync(string id)
    {
        try{return await _repo.ReadMatchDetailsAsync(id,_lifetime.Token);}
        catch(Exception e){MessageBox.Show(e.Message,"Match details",MessageBoxButton.OK,MessageBoxImage.Error);return null;}
    }
    public async Task<bool> SaveMatchCheckedAsync(string id,MatchPatch patch)
    {
        if(!_initialized)return false;
        try
        {
            await _repo.SaveMatchAsync(id,patch,_gameRevision,_lifetime.Token);
            await ReloadIfChangedAsync(force:true);
            return true;
        }
        catch(Exception e)
        {
            DataStatus="Error: "+e.Message;
            MessageBox.Show(e.Message,"Death Note",MessageBoxButton.OK,MessageBoxImage.Error);
            return false;
        }
    }
    public async Task SaveMatchAsync(string id,MatchPatch patch) => await SaveMatchCheckedAsync(id,patch);
    /// <summary>Export a consistent copy; never copy the live SQLite file or its WAL manually.</summary>
    public async Task ExportDatabaseAsync(string destination)
    {
        if(!Ready)throw new InvalidOperationException("No database is loaded.");
        if(Path.GetFullPath(destination).Equals(Path.GetFullPath(DataWorkspace.DatabaseFile),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cannot export over the active database.");
        var folder=Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        var staged=Path.Combine(folder,".deathnote-export-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            await _repo.CreateBackupAsync(staged,_lifetime.Token);
            if(!await new SqliteRepository(staged).CheckIntegrityAsync(_lifetime.Token))
                throw new InvalidDataException("The database export did not pass SQLite integrity checks.");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Move(staged,destination,overwrite:true);
        }
        finally {try{File.Delete(staged);}catch(IOException){}}
    }

    /// <summary>Validate imported v3 data and preserve a complete recovery copy before restoration.
    /// The caller closes the application on success; relaunch to avoid stale SQLite caches.</summary>
    public async Task<bool> ImportDatabaseAsync(string selectedFile)
    {
        if(!Ready)return false;
        if(Path.GetFullPath(selectedFile).Equals(Path.GetFullPath(DataWorkspace.DatabaseFile),StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The selected file is the active database.","Death Note",MessageBoxButton.OK,MessageBoxImage.Warning);
            return false;
        }
        _revisionTimer.Stop();_bridgeTimer.Stop();
        await _reloadGate.WaitAsync(_lifetime.Token);
        var staged=Path.Combine(App.Workspace,".deathnote-import-"+Guid.NewGuid().ToString("N")+".db");
        string? recovery=null;
        var succeeded=false;
        try
        {
            var imported=new SqliteRepository(selectedFile);
            if(!await imported.CheckIntegrityAsync(_lifetime.Token))
                throw new InvalidDataException("The chosen database failed integrity verification.");
            await imported.ReadSnapshotAsync(_lifetime.Token); // Validate required tables and schema.
            await imported.CreateBackupAsync(staged,_lifetime.Token);
            var verified=new SqliteRepository(staged);
            if(!await verified.CheckIntegrityAsync(_lifetime.Token))
                throw new InvalidDataException("The staged import failed SQLite integrity verification.");
            recovery=Path.Combine(App.Workspace,"backups","sqlite",
                "before-import-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff")+".db");
            await _repo.CreateBackupAsync(recovery,_lifetime.Token);
            try
            {
                await _repo.RestoreFromBackupAsync(staged,_lifetime.Token);
                succeeded=true;
            }
            catch
            {
                // Restore the untouched pre-import snapshot when possible.
                try{await _repo.RestoreFromBackupAsync(recovery,CancellationToken.None);}catch(Exception restoreError)
                {
                    throw new IOException("Import and rollback failed. Recovery backup: "+recovery,restoreError);
                }
                throw;
            }
            return true;
        }
        catch(Exception ex)
        {
            MessageBox.Show(ex.Message+(recovery is null ? "" : "\nRecovery backup: "+recovery),
                "Death Note — import failed",MessageBoxButton.OK,MessageBoxImage.Error);
            return false;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try{File.Delete(staged);}catch(IOException){}
            _reloadGate.Release();
            if(!succeeded){_revisionTimer.Start();_bridgeTimer.Start();}
        }
    }

    public Task CreateBackupAsync()=>Safe(async()=>
    {
        var path=Path.Combine(App.Workspace,"backups","sqlite","wpf-manual-"+
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff")+".db");
        await _repo.CreateBackupAsync(path,_lifetime.Token);
        MessageBox.Show("Backup created:\n"+path,"Death Note",MessageBoxButton.OK,MessageBoxImage.Information);
    });
    public Task CheckIntegrityAsync()=>Safe(async()=>
    {
        bool ok=await _repo.CheckIntegrityAsync(_lifetime.Token);
        MessageBox.Show(ok?"SQLite quick_check and foreign_key_check: OK":"Database integrity problems were detected.",
            "Death Note",MessageBoxButton.OK,ok?MessageBoxImage.Information:MessageBoxImage.Warning);
    });
    public async Task Safe(Func<Task> action,bool notify=true)
    {
        try{await action();}
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception e)
        {
            DataStatus="Error: "+e.Message;
            if(notify)MessageBox.Show(e.Message,"Death Note",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }
    public void Dispose()
    {
        _revisionTimer.Stop();_playerSearchTimer.Stop();_matchSearchTimer.Stop();_bridgeTimer.Stop();_gameExitTimer.Stop();
        _lifetime.Cancel();_monitor?.Dispose();_lifetime.Dispose();
    }
}
