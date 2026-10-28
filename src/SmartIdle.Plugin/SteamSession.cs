using SmartIdle.Core;
using SteamKit2;
using SteamKit2.Internal;

namespace SmartIdle;

// Uses the bot's existing Steam connection. There are no store lookups or Web API keys.
public sealed class SteamSession : ClientMsgHandler {
    public override void HandleMsg(IPacketMsg packetMsg) { }

    internal async Task<IReadOnlyList<OwnedGame>?> ReadLibraryAsync(ulong steamId, CancellationToken cancellationToken) {
        if (Client is not { IsConnected: true } || steamId == 0) return null;
        var player = Client.GetHandler<SteamUnifiedMessages>()?.CreateService<Player>();
        if (player is null) return null;
        var request = new CPlayer_GetOwnedGames_Request {
            steamid = steamId,
            include_appinfo = true,
            include_free_sub = true,
            include_played_free_games = true,
            skip_unvetted_apps = false
        };
        var response = await player.GetOwnedGames(request).ToTask().WaitAsync(TimeSpan.FromSeconds(40), cancellationToken).ConfigureAwait(false);
        if (response.Result != EResult.OK) return null;
        var games = new List<OwnedGame>();
        foreach (var game in response.Body.games) {
            games.Add(new OwnedGame((uint) game.appid, game.name ?? "", (uint) Math.Max(0, game.playtime_forever)));
        }
        return games;
    }

    // Raw send only. Custom-name callers must use the guarded PlaybackTransition reset first.
    internal bool SendGames(IReadOnlyCollection<uint> appIds, string? customName = null) {
        if (appIds.Count > PlaylistCapacity.GameSlots(32, customName)) throw new ArgumentException("游戏数量无效。");
        foreach (uint id in appIds) if (id == 0) throw new ArgumentException("AppID 无效。");
        if (Client is not { IsConnected: true }) return false;
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
        if (!string.IsNullOrEmpty(customName)) {
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
                game_extra_info = customName,
                game_id = new GameID { AppType = GameID.GameType.Shortcut, ModID = uint.MaxValue }
            });
        }
        foreach (uint id in Sequence.Distinct(appIds)) {
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(id) });
        }
        Client.Send(message);
        return true;
    }
}
