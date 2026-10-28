using System.Globalization;
using SmartIdle.Core;
using SteamKit2;
using SteamKit2.Internal;

namespace SmartIdle;

// Uses the bot's existing Steam connection. There are no store lookups or Web API keys.
public sealed class SteamSession(Action<uint>? libraryLockChanged = null) : ClientMsgHandler {
    private readonly object TraceLock = new();
    private bool TraceActive;

    public override void HandleMsg(IPacketMsg packetMsg) {
        if (packetMsg.MsgType != EMsg.ClientSharedLibraryLockStatus) return;
        var message = new ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus>(packetMsg);
        libraryLockChanged?.Invoke(message.Body.own_library_locked_by);
    }

    // Explicit, short-lived diagnostics only. Neither playback ownership nor scheduling
    // depends on this SDK debug interface or on any trace result.
    internal async Task<string> TracePlaybackAsync(CancellationToken cancellationToken) {
        var client = Client;
        if (client is not { IsConnected: true }) return "Steam 未连接，无法开始播放诊断。";
        PlaybackTrace trace;
        lock (TraceLock) {
            if (TraceActive) return "播放诊断正在运行，请等待本次 15 秒采样结束。";
            trace = new PlaybackTrace(client.DebugNetworkListener);
            client.DebugNetworkListener = trace;
            TraceActive = true;
        }
        try {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            trace.Close();
            return trace.Describe();
        } finally {
            trace.Close();
            lock (TraceLock) {
                // Respect another component replacing the listener while we sampled.
                if (ReferenceEquals(client.DebugNetworkListener, trace)) client.DebugNetworkListener = trace.Previous;
                TraceActive = false;
            }
        }
    }

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

    internal sealed class PlaybackTrace(IDebugNetworkListener? previous) : IDebugNetworkListener {
        internal IDebugNetworkListener? Previous { get; } = previous;
        private readonly object EventsLock = new();
        private readonly List<string> Events = new();
        private int Dropped;
        private volatile bool Closed;

        public void OnIncomingNetworkMessage(EMsg msgType, byte[] data) => Previous?.OnIncomingNetworkMessage(msgType, data);

        public void OnOutgoingNetworkMessage(EMsg msgType, byte[] data) {
            if (!Closed && msgType == EMsg.ClientGamesPlayedWithDataBlob) {
                try {
                    var packet = new PacketClientMsgProtobuf(msgType, data);
                    var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(packet);
                    var ids = new List<uint>();
                    var names = new List<string>();
                    int count = 0;
                    foreach (var game in message.Body.games_played) {
                        if (++count > 32) break;
                        var id = new GameID(game.game_id);
                        if (id.AppType == GameID.GameType.App && id.AppID != 0) ids.Add(id.AppID);
                        if (!string.IsNullOrEmpty(game.game_extra_info)) {
                            string name = game.game_extra_info.Replace('\r', ' ').Replace('\n', ' ');
                            names.Add(name.Length <= 128 ? name : name[..128]);
                        }
                    }
                    const string source = "ASF/其他插件";
                    string time = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    Record($"{time} UTC · {source} · 文案=[{string.Join(", ", names)}] · AppIDs=[{string.Join(", ", ids)}]");
                } catch (Exception exception) {
                    // Tracing must not disrupt sends; retain no raw packet or unrelated data.
                    Record($"播放消息解析失败：{exception.GetType().Name}");
                }
            }
            Previous?.OnOutgoingNetworkMessage(msgType, data);
        }

        private void Record(string value) {
            lock (EventsLock) {
                if (Closed) return;
                if (Events.Count < 32) Events.Add(value);
                else Dropped++;
            }
        }

        internal void Close() {
            lock (EventsLock) Closed = true;
        }

        internal string Describe() {
            lock (EventsLock) {
                string result = Events.Count == 0 ? "15 秒内未观察到发送播放列表。" : string.Join('\n', Events);
                if (Dropped > 0) result += $"\n另有 {Dropped} 条播放消息超出 32 条采样上限。";
                return "SmartIdle 播放诊断（仅播放消息；不包含其他网络数据）：\n" + result;
            }
        }
    }
}
