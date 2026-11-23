-- CIV5_TRACKER_INGAME_BEGIN
-- Death Note: read-only observations in a running multiplayer session.
do
    print("[CIV5_MATCH_DIAG] InGame loaded v8.8")
    local started, ended = false, false
    local slots, disconnected = {}, {}
    local function esc(x)
        local s = tostring(x or '')
        return (s:gsub('%%','%%25'):gsub('|','%%7C'):gsub('\r','%%0D'):gsub('\n','%%0A'))
    end
    local function stamp()
        if os and os.time then local ok,t=pcall(os.time); if ok then return t end end
        return ''
    end
    local function emit(kind, ...)
        local fields = { '[CIV5_MATCH] '..kind, tostring(stamp()) }
        for _,v in ipairs({...}) do fields[#fields+1]=esc(v) end
        print(table.concat(fields,'|'))
    end
    local function civ(id)
        local ok, value = pcall(function()
            local p=Players[id]; local c=p and GameInfo.Civilizations[p:GetCivilizationType()]
            return c and Locale.ConvertTextKey(c.ShortDescription) or ''
        end)
        return ok and value or ''
    end
    local function mapName()
        local ok,value=pcall(function()
            local script=PreGame.GetMapScript()
            for row in GameInfo.MapScripts{FileName=script} do return Locale.ConvertTextKey(row.Name) end
            return script or ''
        end)
        return ok and value or ''
    end
    local function roster()
        local turn=Game.GetGameTurn()
        local list=Matchmaking.GetPlayerList() or {}
        local localID=Game.GetActivePlayer()
        for _,p in ipairs(list) do
            local id=p.playerID
            if type(id)=='number' and id~=localID then
                local ok,steam=pcall(Steam.GetPlayerSteamID,id)
                if ok and type(steam)=='table' and type(steam.ID)=='string' and #steam.ID==17 and steam.ID:match('^%d+$') then
                    local nick=p.playerName or steam.PlayerName or steam.ID
                    slots[id]=steam.ID
                    emit('PLAYER',turn,steam.ID,nick,civ(id))
                    local connectedOK, connected=pcall(Network.IsPlayerConnected,id)
                    if disconnected[steam.ID] and connectedOK and connected then
                        disconnected[steam.ID]=nil
                        emit('RETURN',turn,steam.ID)
                    end
                end
            end
        end
    end
    local modeLogged = false
    local function isNetworkGame()
        local ok, value = pcall(function() return Game.IsNetworkMultiPlayer() end)
        if not ok then ok,value = pcall(function() return Game.IsNetworkMultiPlayer() end) end
        if not ok then ok,value = pcall(function() return PreGame.IsInternetGame() and not PreGame.IsHotSeatGame() end) end
        if not modeLogged then
            print('[CIV5_MATCH_DIAG] network=' .. tostring(value) .. ' ok=' .. tostring(ok))
            modeLogged=true
        end
        return ok and value == true
    end
    local function tick()
        if ended or not isNetworkGame() then return end
        local turn=Game.GetGameTurn()
        if not started then
            started=true
            emit('START',turn,mapName(),civ(Game.GetActivePlayer()))
        end
        emit('TURN',turn)
        roster()
    end
    local function leave(id)
        if not started or ended then return end
        local sid=slots[id]
        if sid and not disconnected[sid] then
            disconnected[sid]=true
            emit('LEAVE',Game.GetGameTurn(),sid)
        end
    end
    local function reconnect(id)
        if not started or ended then return end
        roster()
        -- roster detects return only after a recorded disconnect.
    end
    local function finish()
        if not started or ended then return end
        ended=true
        emit('END',Game.GetGameTurn())
    end
    local function safe(fn)
        return function(...)
            local ok,err=pcall(fn,...)
            if not ok then print('[CIV5_MATCH_ERROR] '..tostring(err)) end
        end
    end
    local function bind(name,fn)
        local ok,err=pcall(function() if Events[name] then Events[name].Add(safe(fn)) end end)
        if not ok then print('[CIV5_MATCH_ERROR] bind '..name..': '..tostring(err)) end
    end
    bind('SequenceGameInitComplete',tick)
    bind('LoadScreenClose',tick)
    bind('ActivePlayerTurnStart',tick)
    bind('ActivePlayerTurnEnd',tick)
    bind('NewGameTurn',tick)
    bind('MultiplayerGamePlayerDisconnected',leave)
    bind('ConnectedToNetworkHost',reconnect)
    bind('MultiplayerGamePlayerUpdated',function() if started and not ended then roster() end end)
    bind('ExitToMainMenu',finish)
    bind('EndGameShow',finish)
    bind('MultiplayerGameAbandoned',finish)
    safe(tick)()
end
-- CIV5_TRACKER_INGAME_END
