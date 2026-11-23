using System.Text;

namespace DeathNote.Wpf.Services;

/// <summary>
/// Incremental Lua.log reader: maintains complete lobby rosters and forwards
/// ordered, position-stable events to the persisted SQLite ingestion pipeline.
/// </summary>
public sealed class GameMonitorService : IDisposable
{
    private readonly string _log;
    private readonly long _initialLogBytes;
    private readonly Action<int,string> _onEvents;
    private readonly Func<IReadOnlyList<GameLogLine>,Task>? _onLines;
    // null means there is no active, complete lobby roster.
    private readonly Action<IReadOnlySet<string>?>? _onLobbyRoster;
    private HashSet<string>? _pendingLobby;
    private int _pendingCount = -1;
    private bool _hadLobby;
    private volatile bool _insideGame;
    public bool InGame => _insideGame;
    private readonly HashSet<string> _gameParticipants=new(StringComparer.Ordinal);
    private readonly HashSet<string> _lastCompleteLobby=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts=new();
    private Task? _job;
    private string? _lastStatus;

    private void Publish(int count,string status)
    {
        if(count==0 && status==_lastStatus)return;
        _lastStatus=status;
        _onEvents(count,status);
    }

    public GameMonitorService(string log,Action<int,string> callback,
        Action<IReadOnlySet<string>?>? onLobbyRoster=null,
        Func<IReadOnlyList<GameLogLine>,Task>? onLines=null)
    {
        _log=log;_onEvents=callback;_onLobbyRoster=onLobbyRoster;_onLines=onLines;
        try { _initialLogBytes=File.Exists(log)?new FileInfo(log).Length:0; }
        catch(IOException) { _initialLogBytes=0; }
    }

    private void ResetLobby()
    {
        _insideGame=false;
        _gameParticipants.Clear();
        _lastCompleteLobby.Clear();
        _pendingLobby=null;
        _pendingCount=-1;
        if (_hadLobby)
        {
            _hadLobby=false;
            _onLobbyRoster?.Invoke(null);
        }
    }

    // The game emits EXIT immediately after LAUNCH. That EXIT closes the
    // staging room, NOT the match. Keep the participants visible in the same
    // Lobby filter and footer count until END or a confirmed game exit.
    private void ProcessMatchLine(string line)
    {
        const string prefix="[CIV5_MATCH] ";
        int index=line.IndexOf(prefix,StringComparison.Ordinal);
        if(index<0)return;
        var fields=line[(index+prefix.Length)..].Trim().Split('|');
        if(fields.Length<2)return;
        if(fields[0] is "LAUNCH" or "START")
        {
            if(!_insideGame)
            {
                _insideGame=true;
                // Keep the last complete staging-room snapshot.
                _gameParticipants.Clear();
                if(_pendingLobby is not null) _gameParticipants.UnionWith(_pendingLobby);
                _gameParticipants.UnionWith(_lastCompleteLobby);
                // Notify even for a match with no remote Steam IDs yet. This
                // keeps the footer "In game" and ensures END resets it later.
                _hadLobby=true;
                _onLobbyRoster?.Invoke(new HashSet<string>(_gameParticipants,StringComparer.Ordinal));
            }
        }
        else if(fields[0]=="PLAYER" && _insideGame && fields.Length>=5 &&
                fields[3].Length==17 && fields[3].All(char.IsAsciiDigit))
        {
            if(_gameParticipants.Add(fields[3]))
            {
                _hadLobby=true;
                _onLobbyRoster?.Invoke(new HashSet<string>(_gameParticipants,StringComparer.Ordinal));
            }
        }
        else if(fields[0]=="END" && _insideGame)
        {
            ResetLobby();
        }
    }

    /// <summary>Called after Civ V disappears without a normal END event.</summary>
    public void ClearActiveGame() => ResetLobby();

    // The game's Lua script emits complete atomic rosters. Never expose
    // partial PLAYER records as an active lobby to the UI.
    private void ProcessLobbyLine(string line)
    {
        const string prefix="[CIV5_PLAYERS] ";
        int index=line.IndexOf(prefix,StringComparison.Ordinal);
        if(index<0)return;
        var message=line[(index+prefix.Length)..].Trim();
        if (message=="EXIT")
        {
            _pendingLobby=null;
            _pendingCount=-1;
            if(!_insideGame)ResetLobby();
            return;
        }
        if (message.StartsWith("BEGIN|",StringComparison.Ordinal))
        {
            _pendingLobby=null;
            _pendingCount=-1;
            if(int.TryParse(message.AsSpan(6),out int count) && count>=0 && count<=64)
            {
                _pendingCount=count;
                _pendingLobby=new HashSet<string>(StringComparer.Ordinal);
            }
            return;
        }
        if(message.StartsWith("PLAYER|",StringComparison.Ordinal) && _pendingLobby is not null)
        {
            var payload=message.AsSpan(7);
            int delimiter=payload.IndexOf('|');
            if(delimiter<=0)return;
            var id=payload[..delimiter];
            if(id.Length==17 && id.ToString().All(char.IsAsciiDigit))
                _pendingLobby.Add(id.ToString());
            return;
        }
        if(message.StartsWith("END|",StringComparison.Ordinal))
        {
            if(_pendingLobby is not null && int.TryParse(message.AsSpan(4),out int count)
               && count==_pendingCount && count==_pendingLobby.Count)
            {
                _lastCompleteLobby.Clear();
                _lastCompleteLobby.UnionWith(_pendingLobby);
                _hadLobby=true;
                if(_insideGame) _gameParticipants.UnionWith(_pendingLobby);
                _onLobbyRoster?.Invoke(new HashSet<string>(
                    _insideGame ? _gameParticipants : _pendingLobby,StringComparer.Ordinal));
            }
            _pendingLobby=null;
            _pendingCount=-1;
        }
    }

    public sealed record LogInspection(long BytesRead,int CompleteLines,int Events,int MatchEvents,
        int LobbyEvents,int CompleteRosters,int LastRosterSize);

    /// <summary>
    /// Bounded, read-only inspection of the same Lua event syntax processed by
    /// the live monitor. No game commands, DB connections or UI notifications.
    /// Discards a partial first line after seeking into a large log.
    /// </summary>
    public static async Task<LogInspection> InspectRecentAsync(string path,CancellationToken ct=default)
    {
        if(!File.Exists(path))throw new FileNotFoundException("Lua.log is missing.",path);
        int rosters=0,rosterSize=0,lines=0,events=0,matches=0,lobbies=0;
        using var inspector=new GameMonitorService(path,(_,_)=>{},roster=>
        {
            if(roster is not null){rosters++;rosterSize=roster.Count;}
        });
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,
            FileShare.ReadWrite|FileShare.Delete,16*1024,
            FileOptions.Asynchronous|FileOptions.SequentialScan);
        long start=Math.Max(0,stream.Length-256*1024);
        stream.Seek(start,SeekOrigin.Begin);
        using var reader=new StreamReader(stream,new UTF8Encoding(false),false,16*1024,leaveOpen:false);
        if(start>0)await reader.ReadLineAsync(ct); // first line may be incomplete
        string? line;
        while((line=await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();
            lines++;
            inspector.ProcessLobbyLine(line);
            if(line.Contains("[CIV5_MATCH]",StringComparison.Ordinal)) {events++;matches++;}
            else if(line.Contains("[CIV5_PLAYERS]",StringComparison.Ordinal)) {events++;lobbies++;}
        }
        return new LogInspection(stream.Position-start,lines,events,matches,lobbies,rosters,rosterSize);
    }

    public void Start() => _job=Task.Run(()=>Loop(_cts.Token));

    private async Task Loop(CancellationToken ct)
    {
        long offset=0;
        string incomplete="";
        var decoder=Encoding.UTF8.GetDecoder();
        var chars=new char[16*1024];
        DateTime previous=DateTime.MinValue;
        DateTime creation=DateTime.MinValue;
        long replayUntil=-1;
        bool firstScan=true;
        bool skipPartialFirstLine=false;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(!File.Exists(_log))
                {
                    ResetLobby();
                    // Game may recreate the log between sessions. Do not retain
                    // a byte offset from a now-deleted previous file.
                    offset=0;replayUntil=-1;incomplete="";decoder.Reset();
                    previous=DateTime.MinValue;creation=DateTime.MinValue;
                    Publish(0,"Lua.log not found");
                }
                else
                {
                    var info=new FileInfo(_log);
                    if(info.Length<offset || info.LastWriteTimeUtc<previous ||
                       (creation!=DateTime.MinValue && info.CreationTimeUtc!=creation))
                    {offset=0;incomplete="";decoder.Reset();ResetLobby();replayUntil=-1;skipPartialFirstLine=false;}
                    previous=info.LastWriteTimeUtc;
                    creation=info.CreationTimeUtc;
                    await using var stream=new FileStream(_log,FileMode.Open,FileAccess.Read,
                        FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.Asynchronous|FileOptions.SequentialScan);
                    if(stream.Length>offset)
                    {
                        // Do not scan huge existing logs on the UI thread. First startup
                        // reads the most recent 256KB, then follows from its offset.
                        if(replayUntil<0)
                        {
                            // Only bytes existing when the monitor was created
                            // are historical. A newly created/recreated Lua.log is live.
                            replayUntil=firstScan?Math.Min(_initialLogBytes,stream.Length):0;
                            firstScan=false;
                            if(offset==0 && stream.Length>256*1024)
                            {offset=stream.Length-256*1024;skipPartialFirstLine=true;}
                        }
                        stream.Seek(offset,SeekOrigin.Begin);
                        var buffer=new byte[16*1024];int processed=0,read;
                        while(processed<256*1024 && (read=await stream.ReadAsync(buffer,ct))>0)
                        {
                            long firstByte=offset;
                            processed+=read;offset+=read;
                            // The decoder holds a partial UTF-8 sequence across read buffers.
                            int n=decoder.GetChars(buffer,0,read,chars,0,flush:false);
                            string previousIncomplete=incomplete;
                            string chunk=previousIncomplete+new string(chars,0,n);
                            var lines=chunk.Split('\n');incomplete=lines[^1];
                            int events=0;string kind="Game events";
                            long linePosition=firstByte-Encoding.UTF8.GetByteCount(previousIncomplete);
                            var entries=new List<GameLogLine>();
                            for(int i=0;i<lines.Length-1;i++)
                            {
                                string line=lines[i];
                                bool skip=skipPartialFirstLine;
                                skipPartialFirstLine=false;
                                if(!skip)
                                {
                                    // Import before exposing the roster to the UI.
                                    if(line.Contains("[CIV5_MATCH]",StringComparison.Ordinal) ||
                                       line.Contains("[CIV5_PLAYERS]",StringComparison.Ordinal) ||
                                       line.Contains("InstalledPanel: Refreshing Mods",StringComparison.Ordinal) ||
                                       line.Contains("[DEATHNOTE_LOBBY]",StringComparison.Ordinal) ||
                                       line.Contains("[CIV5_MATCH_ERROR]",StringComparison.Ordinal))
                                        entries.Add(new GameLogLine(linePosition,line,linePosition>=replayUntil));
                                    if(line.Contains("[CIV5_MATCH]",StringComparison.Ordinal)) {events++;kind="Match event detected";}
                                    else if(line.Contains("[CIV5_PLAYERS]",StringComparison.Ordinal)) {events++;kind="Lobby event detected";}
                                }
                                linePosition+=Encoding.UTF8.GetByteCount(line)+1;
                            }
                            if(entries.Count>0 && _onLines is not null)
                            {
                                try { await _onLines(entries); }
                                catch(Exception ex) when(ex is not OperationCanceledException)
                                {
                                    // Retry the same complete lines on the next poll;
                                    // the database ingestor is transactionally idempotent.
                                    offset=firstByte;
                                    incomplete=previousIncomplete;
                                    decoder.Reset();
                                    Publish(0,"Game database import failed: "+ex.Message);
                                    break;
                                }
                            }
                            foreach(var entry in entries)
                            {
                                ProcessMatchLine(entry.Text);
                                ProcessLobbyLine(entry.Text);
                            }
                            if(entries.Any(entry=>entry.Text.Contains("InstalledPanel: Refreshing Mods",StringComparison.Ordinal)))
                                ResetLobby();
                            if(events>0)Publish(events,kind);
                        }
                        if(incomplete.Length>32*1024)incomplete=""; // maliciously long line
                    }
                    else if(offset==0){if(replayUntil<0)replayUntil=stream.Length;Publish(0,"Waiting for game events");}
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(IOException) {Publish(0,"Lua.log temporarily unavailable");}
            catch(UnauthorizedAccessException) {Publish(0,"Cannot access Lua.log");}
            try { await Task.Delay(1300,ct); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
        }
    }
    public void Dispose()
    {
        _cts.Cancel();_cts.Dispose();
    }
}
