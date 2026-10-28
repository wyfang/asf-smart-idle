using System.Composition;
using System.Text.Json;
using ArchiSteamFarm.Plugins.Interfaces;
using SmartIdle.Core;
using SteamKit2;
using SteamKit2.Internal;

namespace SmartIdle.HostProbe;

// Test-only plugin. Uses synthetic games and an isolated temporary state directory.
// It never registers a bot handler, touches account configuration, or sends Steam requests.
[Export(typeof(IPlugin))]
public sealed class HostProbePlugin : IPlugin {
    public string Name => "SmartIdle.HostProbe";
    public Version Version => typeof(HostProbePlugin).Assembly.GetName().Version!;

    public async Task OnLoaded() {
        string phase = "temporary-directory";
        string directory = Path.Combine(Path.GetTempPath(), "smartidle-host-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            phase = "native-config-store";
            string nativeConfig = Path.Combine(directory, "SyntheticBot.json");
            string originalConfig = "{\"GamesPlayedWhileIdle\":[10,730],\"CustomGamePlayedWhileIdle\":\"WYFANG.NET\",\"FarmingPreferences\":129,\"SteamLogin\":\"synthetic\",\"SmartIdle\":{\"Enabled\":true}}";
            await File.WriteAllTextAsync(nativeConfig, originalConfig).ConfigureAwait(false);
            Check(await NativeIdleConfigStore.ApplyAsync(nativeConfig, new uint[] { 570, 440 }).ConfigureAwait(false), "Native playlist was not applied.");
            Check(!await NativeIdleConfigStore.ApplyAsync(nativeConfig, new uint[] { 570, 440 }).ConfigureAwait(false), "Identical playlist caused another write.");
            Check(await NativeIdleConfigStore.MatchesAsync(nativeConfig, new uint[] { 570, 440 }).ConfigureAwait(false), "Managed config does not match batch.");
            Check(await NativeIdleConfigStore.RestoreAsync(nativeConfig).ConfigureAwait(false), "Native playlist was not restored.");
            Check(await NativeIdleConfigStore.MatchesAsync(nativeConfig, new uint[] { 10, 730 }).ConfigureAwait(false), "Original playlist was lost.");
            using (var nativeJson = JsonDocument.Parse(await File.ReadAllTextAsync(nativeConfig).ConfigureAwait(false))) {
                Check(JsonField(nativeJson.RootElement, "CustomGamePlayedWhileIdle").GetString() == "WYFANG.NET" && JsonField(nativeJson.RootElement, "FarmingPreferences").GetInt32() == 129, "Unrelated native configuration changed.");
            }

            phase = "playback-packet-parsing";
            uint libraryOwner = 0;
            var session = new SteamSession(id => libraryOwner = id);
            var libraryPacket = new ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus>(EMsg.ClientSharedLibraryLockStatus);
            libraryPacket.Body.own_library_locked_by = 123;
            session.HandleMsg(new PacketClientMsgProtobuf(EMsg.ClientSharedLibraryLockStatus, libraryPacket.Serialize()));
            Check(libraryOwner == 123, "Library-lock packet was not dispatched.");
            var prior = new ProbeNetworkListener();
            var trace = new SteamSession.PlaybackTrace(prior);
            trace.OnOutgoingNetworkMessage(EMsg.ClientHeartBeat, []);
            Check(trace.Describe().Contains("未观察到"), "Trace inspected unrelated traffic.");
            var playbackPacket = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob);
            playbackPacket.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(11390) });
            playbackPacket.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
                game_extra_info = "WYFANG.NET", game_id = new GameID { AppType = GameID.GameType.Shortcut, ModID = uint.MaxValue }
            });
            trace.OnOutgoingNetworkMessage(EMsg.ClientGamesPlayedWithDataBlob, playbackPacket.Serialize());
            string traced = trace.Describe();
            Check(traced.Contains("WYFANG.NET") && traced.Contains("11390") && traced.Contains("ASF/其他插件"), "Trace failed to parse playback.");
            trace.Close();
            trace.OnOutgoingNetworkMessage(EMsg.ClientGamesPlayedWithDataBlob, playbackPacket.Serialize());
            trace.OnIncomingNetworkMessage(EMsg.ClientHeartBeat, []);
            Check(trace.Describe() == traced && prior.Outgoing == 3 && prior.Incoming == 1, "Trace lifetime or listener forwarding invalid.");

            phase = "playback-lifecycle";
            var lifecycle = new PlaybackLifecycle();
            var connectedAt = DateTimeOffset.UtcNow;
            lifecycle.OnConnected(connectedAt);
            Check(lifecycle.CanPlay(connectedAt), "Semantic idle ownership should not require a login takeover delay.");
            lifecycle.OnPlayingBlocked(true, connectedAt.AddSeconds(4), 60);
            lifecycle.OnLibraryBlocked(true, connectedAt.AddSeconds(5), 60);
            lifecycle.OnPlayingBlocked(false, connectedAt.AddSeconds(6), 60);
            Check(!lifecycle.CanPlay(connectedAt.AddHours(1)), "Shared-library lock was cleared by a play unlock.");
            lifecycle.OnLibraryBlocked(false, connectedAt.AddSeconds(7), 60);
            Check(!lifecycle.CanPlay(connectedAt.AddSeconds(66.5)) && lifecycle.CanPlay(connectedAt.AddSeconds(67)), "Occupation cooldown invalid.");
            Check(!lifecycle.OnLibraryBlocked(false, connectedAt.AddSeconds(8), 60) && lifecycle.ReadyAfter == connectedAt.AddSeconds(67), "Duplicate unlock extended cooldown.");
            lifecycle.OnDisconnected();
            Check(!lifecycle.CanPlay(connectedAt.AddHours(1)), "Disconnected lifecycle allowed playback.");

            phase = "config-json";
            var defaults = JsonSerializer.Deserialize<IdleOptions>("{\"Enabled\":true}", IdleJson.Options)
                ?? throw new InvalidOperationException("Configuration deserialized to null.");
            Check(defaults.Enabled && defaults.ConcurrentGames == 32 && defaults.RotationMinutes == 120 && defaults.RefreshHours == 0 && defaults.SelectionMode == "LeastPlaytime",
                "Configuration defaults were not preserved.");
            defaults.Validate();
            Check(PlaylistCapacity.GameSlots(32, "WYFANG.NET") == 31 && PlaylistCapacity.GameSlots(32, null) == 32, "Custom status capacity invalid.");
            Expect<JsonException>(() => JsonSerializer.Deserialize<IdleOptions>("{\"Enabledd\":true}", IdleJson.Options));

            phase = "config-validation";
            IdleOptions[] invalidOptions = [
                new() { ConcurrentGames = 0 },
                new() { ConcurrentGames = 33 },
                new() { RotationMinutes = 4 },
                new() { RefreshHours = -1 },
                new() { PinnedGames = [0] },
                new() { Blacklist = [1, 1] },
                new() { PinnedGames = [1], Blacklist = [1] },
                new() { ConcurrentGames = 1, PinnedGames = [1, 2] }
            ];
            foreach (var invalid in invalidOptions) Expect<ArgumentException>(invalid.Validate);
            var options = new IdleOptions { Enabled = true, ConcurrentGames = 3, RotationMinutes = 5, RefreshHours = 24, SelectionMode = "RoundRobin", Blacklist = [9] };
            options.Validate();
            var configRoundtrip = JsonSerializer.Deserialize<IdleOptions>(JsonSerializer.Serialize(options, IdleJson.Options), IdleJson.Options)!;
            configRoundtrip.Validate();
            Check(configRoundtrip.Enabled && configRoundtrip.ConcurrentGames == 3 && configRoundtrip.Blacklist.Length == 1 && configRoundtrip.Blacklist[0] == 9,
                "Configuration roundtrip lost values.");

            phase = "empty-state-load";
            string statePath = Path.Combine(directory, "synthetic-state.json");
            var empty = await IdleStateStore.LoadAsync(statePath).ConfigureAwait(false);
            empty.Validate();
            Check(!empty.Initialized && empty.Approved.Count == 0, "Missing state was not empty.");

            phase = "initial-library-filter";
            var planner = new IdlePlanner(options, empty);
            OwnedGame[] initialLibrary = [
                Game(1, 30), Game(2, 60), Game(3, 120), Game(4, 180),
                Game(5, 240), Game(6, 300), Game(7, 0), Game(9, 600)
            ];
            planner.RefreshLibrary(initialLibrary);
            planner.ValidateState();
            Check(planner.Options.ConcurrentGames == 3 && planner.State.Initialized, "Planner public properties changed.");
            Check(planner.State.Pending.Count == 6 && planner.State.Approved.Count == 0, "First scan bypassed approval.");
            Check(planner.CurrentOrSelectBatch().Count == 0, "An unapproved batch was selected.");
            Check(planner.IsEligible(1) && !planner.IsEligible(7) && !planner.IsEligible(9) && !planner.IsEligible(999), "Eligibility filter failed.");
            Check(planner.Explain(7) == "累计时长为 0" && planner.Explain(9) == "禁止名单" &&
                  planner.Explain(999) == "当前游戏库中没有此游戏" && planner.Explain(1) == "待确认加入", "Eligibility explanations failed.");

            phase = "approval";
            Expect<ArgumentException>(() => planner.Approve([1, 7]));
            Check(planner.State.Approved.Count == 0, "Invalid mixed approval partially applied.");
            planner.Approve([1, 2, 3]);
            Check(planner.State.Approved.Count == 3, "Explicit approval failed.");
            planner.ApproveAll();
            Check(planner.State.Approved.Count == 6 && planner.State.Pending.Count == 0, "Approve-all failed.");

            phase = "pins-and-rotation";
            planner.Pin([1]);
            Sequence([1, 2, 3], planner.CurrentOrSelectBatch());
            Check(planner.Explain(1) == "手动常驻", "Pinned explanation failed.");
            Sequence([1, 4, 5], planner.Rotate());
            Sequence([1, 6, 2], planner.Rotate());
            planner.Unpin([1]);
            Check(planner.State.PinnedGames.Count == 0, "Unpin failed.");
            Check(planner.CurrentOrSelectBatch().Count == 3, "Unpin did not fill rotating slots.");
            planner.ValidateState();

            phase = "reject-and-reapprove";
            planner.Reject([2]);
            Check(planner.State.Rejected.Contains(2) && !planner.State.Approved.Contains(2), "Reject failed.");
            Check(planner.Explain(2) == "已拒绝加入", "Rejected explanation failed.");
            planner.RefreshLibrary(initialLibrary);
            Check(!planner.State.Pending.Contains(2), "Rejected game returned to pending.");
            planner.Approve([2]);
            Check(planner.State.Approved.Contains(2) && !planner.State.Rejected.Contains(2), "Reapproval failed.");

            phase = "exclude-and-unexclude";
            planner.Exclude([3]);
            Check(!planner.IsEligible(3) && !planner.State.Approved.Contains(3) && planner.Explain(3) == "禁止名单", "Exclude failed.");
            planner.Unexclude([3]);
            Check(planner.State.Pending.Contains(3) && !planner.State.Approved.Contains(3), "Unexclude bypassed approval.");
            planner.Approve([3]);
            planner.Unexclude([9]);
            Check(!planner.State.Pending.Contains(9), "Configuration blacklist was overridden.");

            phase = "newcomer-and-snapshot-integrity";
            OwnedGame[] expandedLibrary = [
                Game(1, 31), Game(2, 61), Game(3, 121), Game(4, 181),
                Game(5, 241), Game(6, 301), Game(7, 10), Game(8, 50), Game(9, 601)
            ];
            planner.RefreshLibrary(expandedLibrary);
            Check(planner.State.Pending.Contains(7) && planner.State.Pending.Contains(8) && planner.State.Pending.Count == 2,
                "Newcomers or newly played games bypassed pending.");
            Expect<ArgumentException>(() => planner.RefreshLibrary([Game(4, 1), Game(4, 2)]));
            Check(planner.State.Library.Count == 9 && planner.State.Library.ContainsKey(1), "Bad library polluted the snapshot.");
            planner.Pin([1]);
            planner.State.Paused = true;
            planner.State.RemainingRotationSeconds = 123.5;
            planner.ValidateState();

            phase = "state-validation";
            Expect<InvalidDataException>(() => new IdleState { SchemaVersion = 999 }.Validate());
            Expect<InvalidDataException>(() => new IdleState { Approved = [1], Pending = [1] }.Validate());
            Expect<InvalidDataException>(() => new IdleState { RemainingRotationSeconds = double.NaN }.Validate());
            Expect<InvalidDataException>(() => new IdleState { Queue = [1, 1] }.Validate());

            phase = "state-save-roundtrip";
            uint[] expectedBatch = Copy(planner.CurrentOrSelectBatch());
            uint[] expectedQueue = Copy(planner.State.Queue);
            await IdleStateStore.SaveAsync(statePath, planner.State).ConfigureAwait(false);
            var restoredState = await IdleStateStore.LoadAsync(statePath).ConfigureAwait(false);
            restoredState.Validate();
            var restored = new IdlePlanner(options, restoredState);
            restored.ValidateState();
            Sequence(expectedBatch, restored.CurrentOrSelectBatch());
            Sequence(expectedQueue, restored.State.Queue);
            Check(restored.State.Paused && restored.State.RemainingRotationSeconds == 123.5 && restored.State.PinnedGames.Count == 1,
                "Roundtrip lost pause, timer, or pins.");
            Check(restored.State.Library[7].Name == "Synthetic 7" && restored.State.Library[7].PlaytimeMinutes == 10 &&
                  restored.State.Pending.Count == 2 && restored.State.Approved.Count == 6, "Roundtrip lost game or approval data.");
            restored.State.Paused = false;
            await IdleStateStore.SaveAsync(statePath, restored.State).ConfigureAwait(false);
            Check(!(await IdleStateStore.LoadAsync(statePath).ConfigureAwait(false)).Paused, "Overwrite save failed.");

            phase = "configured-pin-replacement";
            var changed = new IdlePlanner(options with { PinnedGames = [4] }, restored.State);
            Sequence([4], changed.State.PinnedGames);
            changed.ValidateState();

            phase = "selection-modes-and-local-credit";
            var lowest = new IdlePlanner(new IdleOptions { ConcurrentGames = 1 }, new IdleState());
            lowest.RefreshLibrary([Game(1, 1), Game(2, 2)]);
            lowest.ApproveAll();
            Sequence([1], lowest.CurrentOrSelectBatch());
            lowest.AccruePlaytime([1], 120);
            Sequence([2], lowest.Rotate());
            var random = new IdlePlanner(new IdleOptions { ConcurrentGames = 2, SelectionMode = "Random", PinnedGames = [1840] }, new IdleState());
            random.RefreshLibrary([Game(1, 1), Game(2, 2), Game(3, 0)]);
            random.ApproveAll();
            for (int i = 0; i < 4; i++) {
                var batch = random.Rotate();
                Check(batch.Count == 2 && batch[0] == 1840 && (batch[1] == 1 || batch[1] == 2), "Random batch or manual tool pin invalid.");
            }
            string extraPath = Path.Combine(directory, "modes.json");
            await IdleStateStore.SaveAsync(extraPath, lowest.State).ConfigureAwait(false);
            var modeState = await IdleStateStore.LoadAsync(extraPath).ConfigureAwait(false);
            Check(modeState.LastSelectionMode == "LeastPlaytime" && modeState.EstimatedPlaytimeMinutes[1] == 3, "Mode or local credit was not persisted.");

            phase = "cancelled-save-retains-state";
            string beforeCancellation = await File.ReadAllTextAsync(statePath).ConfigureAwait(false);
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel();
                await ExpectAsync<OperationCanceledException>(() => IdleStateStore.SaveAsync(statePath, new IdleState(), cancelled.Token)).ConfigureAwait(false);
            }
            Check(await File.ReadAllTextAsync(statePath).ConfigureAwait(false) == beforeCancellation, "Cancelled save replaced the file.");

            phase = "corrupt-state-retains-file";
            string[] corruptStates = ["{broken", "null", "{}", "{\"UnknownField\":1}"];
            foreach (string corrupt in corruptStates) {
                await File.WriteAllTextAsync(statePath, corrupt).ConfigureAwait(false);
                await ExpectAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(statePath)).ConfigureAwait(false);
                Check(await File.ReadAllTextAsync(statePath).ConfigureAwait(false) == corrupt, "Corrupt file was replaced.");
            }

            phase = "temporary-cleanup";
            Directory.Delete(directory, recursive: true);
            Console.WriteLine("SMARTIDLE_HOST_PROBE_PASS: configuration, approval, scheduling, all planner operations, and state persistence passed.");
        } catch (Exception exception) {
            Console.WriteLine($"SMARTIDLE_HOST_PROBE_FAIL phase={phase}: {exception}");
            throw new InvalidOperationException($"SmartIdle host probe failed in phase {phase}.", exception);
        } finally {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static OwnedGame Game(uint id, uint minutes) => new(id, "Synthetic " + id, minutes);

    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static uint[] Copy(IReadOnlyCollection<uint> values) {
        var result = new uint[values.Count];
        int index = 0;
        foreach (uint value in values) result[index++] = value;
        return result;
    }

    private static void Sequence(IReadOnlyList<uint> expected, IReadOnlyList<uint> actual) {
        Check(expected.Count == actual.Count, "Sequence length differs.");
        for (int index = 0; index < expected.Count; index++) {
            Check(expected[index] == actual[index], $"Sequence differs at {index}: {expected[index]} != {actual[index]}.");
        }
    }

    private static void Expect<TException>(Action action) where TException : Exception {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static JsonElement JsonField(JsonElement root, string name) {
        foreach (var property in root.EnumerateObject()) if (property.Name == name) return property.Value;
        throw new InvalidOperationException("Missing synthetic field: " + name);
    }

    private sealed class ProbeNetworkListener : IDebugNetworkListener {
        internal int Incoming;
        internal int Outgoing;
        public void OnIncomingNetworkMessage(EMsg msgType, byte[] data) => Incoming++;
        public void OnOutgoingNetworkMessage(EMsg msgType, byte[] data) => Outgoing++;
    }

    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception {
        try { await action().ConfigureAwait(false); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }
}
