using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeathNote.Wpf.Data;

namespace DeathNote.Wpf.Services;

/// <summary>One complete Lua.log line at a stable byte offset. A replay never creates popups.</summary>
public sealed record GameLogLine(long Offset, string Text, bool Live);
public sealed record GameObservedPlayer(string SteamId, string Nickname, string SeenAt,bool Live);
public sealed record GameObservedParticipant(string SteamId, string Nickname, string Civilization);

/// <summary>State after processing the last log event for a session, not a speculative outcome.</summary>
public sealed record GameObservedMatch(
    string Id, string Started, string LastAt, string? Ended, string Clock,
    string Map, string LocalNick, string LocalCiv, long? FirstTurn, long? LastTurn,
    long LastOffset, IReadOnlyList<GameObservedParticipant> Participants,
    bool NotifyReview,string Title="",bool Resumed=false);

public sealed record GameImportBatch(IReadOnlyList<GameObservedPlayer> Players,
    IReadOnlyList<GameObservedMatch> Matches);
public sealed record GameImportResult(bool PlayersChanged, bool MatchesChanged,
    IReadOnlyList<string> NewReviews);

/// <summary>
/// Port of Civ V lobby and in-game event parsing from the legacy Python Reader.
/// Persistence is performed transactionally by SqliteRepository; parsing does not
/// use or depend on any WPF control. It consumes only complete log lines.
/// </summary>
public sealed class GameLogIngestionService
{
    private readonly SqliteRepository _repository;
    private readonly string _source;
    private Dictionary<string,string>? _roster;
    private Dictionary<string,string> _lastRoster=new(StringComparer.Ordinal);
    private int _expectedCount=-1;
    private string _lobbyNameDraft="",_lastLobbyName="";
    private Session? _active;

    public GameLogIngestionService(SqliteRepository repository,string logPath)
    { _repository=repository;_source=Path.GetFullPath(logPath).ToUpperInvariant(); }

    private sealed class Session
    {
        public string Id="",Started="",LastAt="",Clock="",Map="",LocalNick="",LocalCiv="",Title="";
        public string? Ended;
        public long? FirstTurn,LastTurn;
        public long LastOffset;
        public bool NotifyReview,Resumed,StartedWithoutLaunch;
        public readonly Dictionary<string,GameObservedParticipant> Players=new(StringComparer.Ordinal);
        public GameObservedMatch Snapshot() => new(Id,Started,LastAt,Ended,Clock,Map,LocalNick,
            LocalCiv,FirstTurn,LastTurn,LastOffset,Players.Values.ToArray(),NotifyReview,Title,Resumed);
        public Session Clone()
        {
            var clone=new Session{Id=Id,Started=Started,LastAt=LastAt,Ended=Ended,
                Clock=Clock,Map=Map,LocalNick=LocalNick,LocalCiv=LocalCiv,Title=Title,
                FirstTurn=FirstTurn,LastTurn=LastTurn,LastOffset=LastOffset,
                NotifyReview=NotifyReview,Resumed=Resumed,StartedWithoutLaunch=StartedWithoutLaunch};
            foreach(var (sid,part) in Players)clone.Players[sid]=part;
            return clone;
        }
    }

    public bool HasActiveMatch => _active is { Ended: null };

    public async Task<GameImportResult> FinishAfterGameExitAsync(CancellationToken ct=default)
    {
        if(_active is not { Ended: null } active)
            return new GameImportResult(false,false,Array.Empty<string>());
        var before=active.Clone();
        string now=DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
        active.Ended=now;active.LastAt=now;active.NotifyReview=true;
        // A sentinel offset is higher than the final observed game event.
        active.LastOffset++;
        try
        {
            var result=await _repository.ImportGameEventsAsync(
                new GameImportBatch(Array.Empty<GameObservedPlayer>(),[active.Snapshot()]),ct);
            _active=null;
            _lastLobbyName="";
            _lastRoster.Clear();
            return result;
        }
        catch { _active=before;throw; }
    }

    public async Task<GameImportResult> IngestAsync(IReadOnlyList<GameLogLine> entries,
        string localNickname,CancellationToken ct=default)
    {
        var beforeSession=_active?.Clone();
        var beforeRoster=_roster is null ? null : new Dictionary<string,string>(_roster,StringComparer.Ordinal);
        int beforeExpected=_expectedCount;
        string beforeLobbyName=_lastLobbyName,beforeLobbyDraft=_lobbyNameDraft;
        var beforeLastRoster=new Dictionary<string,string>(_lastRoster,StringComparer.Ordinal);
        try
        {
        var players=new Dictionary<string,GameObservedPlayer>(StringComparer.Ordinal);
        var matches=new Dictionary<string,GameObservedMatch>(StringComparer.Ordinal);
        foreach(var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var line=entry.Text;
            if(line.Contains("[CIV5_PLAYERS] ",StringComparison.Ordinal))
                ParseLobby(entry,players,matches);
            if(line.Contains("[CIV5_MATCH] ",StringComparison.Ordinal))
                ParseMatch(entry,players,matches,localNickname);
            if(line.Contains("InstalledPanel: Refreshing Mods",StringComparison.Ordinal) &&
               _active is { Ended: null } active)
            {
                // Only a return to the main menu after a detected session.
                active.Ended=DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
                active.LastAt=active.Ended;
                active.LastOffset=entry.Offset;
                active.NotifyReview=entry.Live;
                matches[active.Id]=active.Snapshot();
                _active=null;
            }
        }
        // A high-turn START without a lobby LAUNCH may be a desync/reload.
        // Wait for the actual opponent's Steam ID before deciding; the map,
        // civilization and turn alone cannot identify a multiplayer match.
        if(_active is { Resumed:false, StartedWithoutLaunch:true, FirstTurn:>=5 } maybeResume)
        {
            if(maybeResume.Players.Count==0 && maybeResume.Ended is null)
                matches.Remove(maybeResume.Id);
            else if(maybeResume.Players.Count==1)
            {
                var candidate=await _repository.FindSavedGameContinuationAsync(maybeResume.Snapshot(),ct);
                if(candidate is not null)
                {
                    string newSessionId=maybeResume.Id;
                    maybeResume.Id=candidate.MatchId;
                    maybeResume.Started=candidate.Started;
                    maybeResume.FirstTurn=candidate.FirstTurn;
                    if(!string.IsNullOrWhiteSpace(candidate.Title))maybeResume.Title=candidate.Title;
                    maybeResume.Resumed=true;
                    matches.Remove(newSessionId);
                    matches[maybeResume.Id]=maybeResume.Snapshot();
                }
            }
        }
        if(players.Count==0 && matches.Count==0)
            return new GameImportResult(false,false,Array.Empty<string>());
        return await _repository.ImportGameEventsAsync(
            new GameImportBatch(players.Values.ToArray(),matches.Values.ToArray()),ct);
        }
        catch
        {
            // The caller retries the same bytes if SQLite is busy/unavailable.
            // Restore parser state too, otherwise the retry would be ignored.
            _active=beforeSession;
            _roster=beforeRoster;
            _expectedCount=beforeExpected;
            _lastLobbyName=beforeLobbyName;
            _lobbyNameDraft=beforeLobbyDraft;
            _lastRoster=beforeLastRoster;
            throw;
        }
    }

    private static bool ValidId(string text) => text.Length==17 && text.All(char.IsAsciiDigit);
    private static string Decode(string text)
    {
        // Lua percent-encodes %, |, CR and LF, just as Python unquote does.
        try { return Uri.UnescapeDataString(text); }
        catch(UriFormatException) {return text;}
    }
    private static string SeenStamp()=>DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private void ParseLobby(GameLogLine entry,Dictionary<string,GameObservedPlayer> players,
        Dictionary<string,GameObservedMatch> matches)
    {
        string line=entry.Text;
        string message=line[(line.IndexOf("[CIV5_PLAYERS] ",StringComparison.Ordinal)+15)..].Trim();
        var fields=message.Split('|');
        switch(fields[0])
        {
            case "BEGIN" when fields.Length==2 && int.TryParse(fields[1],out int count)
                              && count>=0 && count<=64:
                _expectedCount=count;_roster=new Dictionary<string,string>(StringComparer.Ordinal);
                _lobbyNameDraft="";
                break;
            case "NAME" when fields.Length==2 && _roster is not null:
                var lobbyTitle=Decode(fields[1]).Trim();
                if(lobbyTitle.Length<=300)_lobbyNameDraft=lobbyTitle;
                break;
            case "PLAYER" when fields.Length==3 && _roster is not null && ValidId(fields[1]):
                var name=Decode(fields[2]);
                if(!string.IsNullOrWhiteSpace(name) && name.Length<=300)
                    _roster[fields[1]]=name;
                break;
            case "END":
                if(fields.Length==2 && int.TryParse(fields[1],out int total) &&
                   _roster is not null && total==_expectedCount && _roster.Count==total)
                {
                    string stamp=SeenStamp();
                    _lastRoster=new Dictionary<string,string>(_roster,StringComparer.Ordinal);
                    _lastLobbyName=_lobbyNameDraft;
                    // An atomic completed roster can arrive after LAUNCH. Supply the
                    // title only while the game is active and it remains empty.
                    if(_active is { Ended:null } active &&
                       string.IsNullOrWhiteSpace(active.Title) &&
                       !string.IsNullOrWhiteSpace(_lastLobbyName) &&
                       entry.Offset>active.LastOffset)
                    {
                        active.Title=_lastLobbyName;
                        active.LastOffset=entry.Offset;
                        matches[active.Id]=active.Snapshot();
                    }
                    foreach(var (sid,nick) in _lastRoster)
                        players[sid]=new GameObservedPlayer(sid,nick,stamp,entry.Live);
                }
                _roster=null;_expectedCount=-1;
                break;
            case "EXIT":
                _roster=null;_expectedCount=-1;
                // The staging name is no longer the next lobby's name. The
                // active match already owns its own title and participant list.
                _lastLobbyName="";
                break;
        }
    }

    private void ParseMatch(GameLogLine entry,
        Dictionary<string,GameObservedPlayer> players,
        Dictionary<string,GameObservedMatch> matches,string localNickname)
    {
        string line=entry.Text;
        var fields=line[(line.IndexOf("[CIV5_MATCH] ",StringComparison.Ordinal)+13)..].Trim().Split('|');
        if(fields.Length<3)return;
        string kind=fields[0];
        var args=fields.Skip(2).Select(Decode).ToArray();
        bool timestampValid=long.TryParse(fields[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out long epoch)
            && epoch>=0 && epoch<=253402300799L;
        string stamp=timestampValid ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("O",CultureInfo.InvariantCulture)
            : SeenStamp();
        string clock=timestampValid?"game":"observed";
        long turn=0;
        if(kind!="LAUNCH" && (args.Length==0 || !long.TryParse(args[0],out turn) || turn<0))return;
        if(kind=="LAUNCH" && args.Length is 1 or 2 || kind=="START" && args.Length==3)
        {
            if(kind=="START" && _active is { Ended:null } existing && existing.FirstTurn is null)
            {
                existing.FirstTurn=turn;existing.LastTurn=turn;
                if(!string.IsNullOrWhiteSpace(args[1]))existing.Map=MapNames.Format(args[1]);
                existing.LocalCiv=CivilizationNames.Format(args[2]);existing.LastOffset=entry.Offset;
                if(string.CompareOrdinal(stamp,existing.LastAt)>0)existing.LastAt=stamp;
                matches[existing.Id]=existing.Snapshot();
                return;
            }
            if(_active is { Ended:null } unfinished)
            {
                // A new LAUNCH/START ends the previous observed session.
                unfinished.Ended=stamp;unfinished.LastAt=stamp;
                unfinished.LastOffset=entry.Offset;
                unfinished.NotifyReview=entry.Live;
                matches[unfinished.Id]=unfinished.Snapshot();
            }
            string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                _source+"|"+entry.Offset.ToString(CultureInfo.InvariantCulture)+"|"+line))).ToLowerInvariant();
            _active=new Session
            {
                Id=id,Started=stamp,LastAt=stamp,Clock=clock,
                Map=MapNames.Format(kind=="LAUNCH"?args[0]:args[1]),
                LocalNick=localNickname,LocalCiv=kind=="START"?CivilizationNames.Format(args[2]):"",
                Title=kind=="LAUNCH" && args.Length==2 && !string.IsNullOrWhiteSpace(args[1])
                    ? args[1].Trim() : _lastLobbyName,
                FirstTurn=kind=="START"?turn:null,LastTurn=kind=="START"?turn:null,
                LastOffset=entry.Offset,StartedWithoutLaunch=kind=="START"
            };
            // Retain the complete lobby roster even if the in-game mod has not
            // emitted its first PLAYER event yet.
            foreach(var (sid,nick) in _lastRoster)
                _active.Players[sid]=new GameObservedParticipant(sid,nick,"");
            matches[id]=_active.Snapshot();
            return;
        }
        if(_active is not { Ended:null } session || entry.Offset<=session.LastOffset)return;
        switch(kind)
        {
            case "PLAYER" when args.Length==4 && ValidId(args[1]):
                string name=args[2];
                if(string.IsNullOrWhiteSpace(name) || name.Length>300)return;
                session.Players[args[1]]=new GameObservedParticipant(
                    args[1],name,CivilizationNames.Format(args[3]));
                players[args[1]]=new GameObservedPlayer(args[1],name,stamp,entry.Live);
                break;
            case "TURN" when args.Length==1:break;
            case "LEAVE" or "RETURN" when args.Length==2 && ValidId(args[1]):break;
            case "END" when args.Length==1:
                session.Ended=stamp;session.NotifyReview=entry.Live;
                break;
            default:return;
        }
        session.LastAt=string.CompareOrdinal(stamp,session.LastAt)>0?stamp:session.LastAt;
        session.FirstTurn??=turn;
        session.LastTurn=Math.Max(turn,session.LastTurn??turn);
        session.LastOffset=entry.Offset;
        matches[session.Id]=session.Snapshot();
        if(session.Ended is not null)
        {
            _active=null;
            _lastLobbyName="";
            _lastRoster.Clear();
        }
    }
}
