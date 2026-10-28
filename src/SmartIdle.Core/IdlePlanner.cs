namespace SmartIdle.Core;

/// <summary>Pure scheduling and approval policy. The ASF adapter owns session gates and active-time accounting.</summary>
public sealed class IdlePlanner {
    public IdleOptions Options { get; }
    public IdleState State { get; }
    private readonly HashSet<uint> ConfigurationBlacklist;

    public IdlePlanner(IdleOptions options, IdleState state) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        options.Validate();
        state.Validate();
        Options = options with { PinnedGames = (uint[]) options.PinnedGames.Clone(), Blacklist = (uint[]) options.Blacklist.Clone() };
        State = state;
        ConfigurationBlacklist = new HashSet<uint>();
        foreach (uint id in Options.Blacklist) ConfigurationBlacklist.Add(id);
        // Runtime pin commands survive restarts. A deliberate config pin change replaces that override.
        bool applyConfiguredPins = !state.PinsInitialized || !SameIds(state.LastConfiguredPins, Options.PinnedGames);
        var proposedPins = applyConfiguredPins ? Options.PinnedGames : state.PinnedGames.ToArray();
        bool conflictingPin = false;
        foreach (uint id in proposedPins) {
            if (ConfigurationBlacklist.Contains(id) || state.Excluded.Contains(id)) conflictingPin = true;
        }
        if (proposedPins.Length > Options.ConcurrentGames || conflictingPin) {
            throw new ArgumentException("常驻数量超过并发限制，或常驻游戏与禁止名单冲突。");
        }
        if (applyConfiguredPins) {
            State.PinnedGames = new List<uint>(Options.PinnedGames.Length);
            State.LastConfiguredPins = new List<uint>(Options.PinnedGames.Length);
            foreach (uint id in Options.PinnedGames) {
                State.PinnedGames.Add(id);
                State.LastConfiguredPins.Add(id);
            }
            State.PinsInitialized = true;
        }
        // Version 0.1 used round-robin and did not store a mode. An explicit mode
        // change reselects once, while an unchanged mode preserves its saved batch.
        bool modeChanged = (State.LastSelectionMode ?? "RoundRobin") != Options.SelectionMode;
        State.LastSelectionMode = Options.SelectionMode;
        if (modeChanged) State.CurrentRotating.Clear();
        if (!State.Initialized || modeChanged) {
            State.RemainingRotationSeconds = Options.RotationMinutes * 60D;
        }
        Normalize();
    }

    /// <summary>Replaces the owned-games snapshot. Eligible newcomers only enter Pending.</summary>
    public void RefreshLibrary(IEnumerable<OwnedGame> games) {
        ArgumentNullException.ThrowIfNull(games);
        var replacement = new Dictionary<uint, OwnedGame>();
        foreach (var game in games) {
            if (game is null || game.AppId == 0 || game.Name is null || !replacement.TryAdd(game.AppId, game)) {
                throw new ArgumentException("游戏库快照包含无效或重复记录。", nameof(games));
            }
        }
        State.Library = replacement;
        foreach (var game in replacement.Values) {
            if (State.EstimatedPlaytimeMinutes.TryGetValue(game.AppId, out double estimate)) {
                State.EstimatedPlaytimeMinutes[game.AppId] = Math.Max(estimate, game.PlaytimeMinutes);
            }
        }
        State.Initialized = true;
        State.Pending.RemoveWhere(id => !IsEligible(id) || State.Approved.Contains(id) || State.Rejected.Contains(id));
        foreach (var game in replacement.Values) {
            if (IsEligible(game.AppId) && !State.Approved.Contains(game.AppId) && !State.Rejected.Contains(game.AppId)) {
                State.Pending.Add(game.AppId);
            }
        }
        Normalize();
    }

    public void Approve(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        foreach (uint id in ids) {
            if (!IsEligible(id)) throw new ArgumentException("只能批准当前拥有、有累计时长且不在禁止名单中的游戏。");
        }
        foreach (var id in ids) {
            State.Approved.Add(id);
            State.Pending.Remove(id);
            State.Rejected.Remove(id);
        }
        Normalize();
    }

    public void ApproveAll() {
        var pending = new List<uint>(State.Pending.Count);
        foreach (uint id in State.Pending) pending.Add(id);
        Approve(pending);
    }

    public void Reject(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        // A manually pinned tool may not be returned by Steam's owned-games endpoint.
        foreach (uint id in ids) {
            if (!State.Library.ContainsKey(id) && !State.PinnedGames.Contains(id)) throw new ArgumentException("游戏不在当前游戏库或常驻清单中。");
        }
        foreach (var id in ids) {
            State.Pending.Remove(id);
            State.Approved.Remove(id);
            State.Excluded.Remove(id);
            State.Rejected.Add(id);
            State.PinnedGames.Remove(id);
        }
        Normalize();
    }

    public void Pin(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        foreach (uint id in ids) {
            if (ConfigurationBlacklist.Contains(id) || State.Excluded.Contains(id)) {
                throw new ArgumentException("常驻项不能位于禁止名单中。");
            }
        }
        var combined = new HashSet<uint>();
        foreach (uint id in State.PinnedGames) combined.Add(id);
        foreach (uint id in ids) combined.Add(id);
        if (combined.Count > Options.ConcurrentGames) {
            throw new ArgumentException("常驻游戏数量不能超过 ConcurrentGames。");
        }
        foreach (var id in ids) {
            if (!State.PinnedGames.Contains(id)) {
                State.PinnedGames.Add(id);
            }
        }
        Normalize();
    }

    public void Unpin(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        State.PinnedGames.RemoveAll(ids.Contains);
        Normalize();
    }

    public void Exclude(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        foreach (var id in ids) {
            State.Excluded.Add(id);
            State.Approved.Remove(id);
            State.Pending.Remove(id);
            State.Rejected.Remove(id);
            State.PinnedGames.Remove(id);
        }
        Normalize();
    }

    public void Unexclude(IEnumerable<uint> appIds) {
        var ids = Materialize(appIds);
        foreach (var id in ids) {
            State.Excluded.Remove(id);
            if (IsEligible(id) && !State.Approved.Contains(id) && !State.Rejected.Contains(id)) {
                State.Pending.Add(id);
            }
        }
        Normalize();
    }

    public bool IsEligible(uint appId) => State.Library.TryGetValue(appId, out var game) && game.PlaytimeMinutes > 0 &&
        !ConfigurationBlacklist.Contains(appId) && !State.Excluded.Contains(appId);

    public double EffectivePlaytimeMinutes(uint appId) {
        double reported = State.Library.TryGetValue(appId, out var game) ? game.PlaytimeMinutes : 0;
        return State.EstimatedPlaytimeMinutes.TryGetValue(appId, out double estimate) ? Math.Max(reported, estimate) : reported;
    }

    /// <summary>Credits actual active session seconds, independent of Steam's delayed reporting.</summary>
    public void AccruePlaytime(IEnumerable<uint> appIds, double seconds) {
        if (!double.IsFinite(seconds) || seconds < 0) {
            throw new ArgumentOutOfRangeException(nameof(seconds), "运行秒数必须为有限的非负数。");
        }
        var ids = Materialize(appIds);
        if (seconds == 0) return;
        var updates = new Dictionary<uint, double>();
        foreach (uint id in ids) {
            if (!State.Library.ContainsKey(id)) continue;
            double total = EffectivePlaytimeMinutes(id) + seconds / 60D;
            if (!double.IsFinite(total)) throw new ArgumentOutOfRangeException(nameof(seconds), "估算时长超过可保存范围。");
            updates.Add(id, total);
        }
        foreach (var pair in updates) State.EstimatedPlaytimeMinutes[pair.Key] = pair.Value;
    }

    public string Explain(uint appId) {
        if (ConfigurationBlacklist.Contains(appId) || State.Excluded.Contains(appId)) return "禁止名单";
        if (State.PinnedGames.Contains(appId)) return State.Library.ContainsKey(appId) ? "手动常驻" : "手动常驻，Steam 未返回游戏库资料，请确认许可证与计时";
        if (!State.Library.TryGetValue(appId, out var game)) return "当前游戏库中没有此游戏";
        if (game.PlaytimeMinutes == 0) return "累计时长为 0";
        if (State.Rejected.Contains(appId)) return "已拒绝加入";
        if (!State.Approved.Contains(appId)) return "待确认加入";
        if (State.CurrentRotating.Contains(appId)) return "已批准，本批轮换";
        return "已批准，等待轮换";
    }

    /// <summary>Gets the saved batch. Mutators fill vacancies; reads never rescan the library or advance the queue.</summary>
    public IReadOnlyList<uint> CurrentOrSelectBatch() {
        var batch = new List<uint>(Options.ConcurrentGames);
        foreach (uint id in State.PinnedGames) {
            if (!ConfigurationBlacklist.Contains(id) && !State.Excluded.Contains(id)) batch.Add(id);
        }
        batch.AddRange(State.CurrentRotating);
        return batch.ToArray();
    }

    /// <summary>Moves the just-served rotating games to the back. Pins never advance or consume rotating positions.</summary>
    public IReadOnlyList<uint> Rotate() {
        Normalize();
        var served = new HashSet<uint>();
        foreach (uint id in State.CurrentRotating) served.Add(id);
        State.Queue.RemoveAll(served.Contains);
        State.Queue.AddRange(State.CurrentRotating);
        State.CurrentRotating.Clear();
        State.RemainingRotationSeconds = Options.RotationMinutes * 60D;
        Normalize();
        return CurrentOrSelectBatch();
    }

    public void ValidateState() {
        State.Validate();
        if (State.PinnedGames.Count > Options.ConcurrentGames) {
            throw new InvalidDataException("状态中的常驻数量超过配置并发数。");
        }
    }

    private void Normalize() {
        var eligible = new HashSet<uint>();
        foreach (uint id in State.Approved) {
            if (IsEligible(id) && !State.PinnedGames.Contains(id)) eligible.Add(id);
        }
        var queue = new List<uint>(eligible.Count);
        var queued = new HashSet<uint>();
        foreach (uint id in State.Queue) {
            if (eligible.Contains(id) && queued.Add(id)) queue.Add(id);
        }
        var newcomers = new List<uint>();
        foreach (uint id in eligible) {
            if (!queued.Contains(id)) newcomers.Add(id);
        }
        newcomers.Sort(static (left, right) => left.CompareTo(right));
        queue.AddRange(newcomers);
        State.Queue = queue;
        // Pins are an explicit manual list, independent of automatic discovery and zero-hour filtering.
        int activePins = 0;
        foreach (uint id in State.PinnedGames) {
            if (!ConfigurationBlacklist.Contains(id) && !State.Excluded.Contains(id)) activePins++;
        }
        int slots = Options.ConcurrentGames - activePins;
        var rotating = new List<uint>(slots);
        var current = new HashSet<uint>();
        foreach (uint id in State.CurrentRotating) {
            if (rotating.Count >= slots) break;
            if (eligible.Contains(id) && current.Add(id)) rotating.Add(id);
        }
        if (rotating.Count < slots) {
            var candidates = new List<uint>();
            foreach (uint id in State.Queue) {
                if (!current.Contains(id)) candidates.Add(id);
            }
            if (Options.SelectionMode == "LeastPlaytime") {
                var position = new Dictionary<uint, int>();
                for (int index = 0; index < candidates.Count; index++) position.Add(candidates[index], index);
                candidates.Sort((left, right) => {
                    int comparison = EffectivePlaytimeMinutes(left).CompareTo(EffectivePlaytimeMinutes(right));
                    return comparison != 0 ? comparison : position[left].CompareTo(position[right]);
                });
            } else if (Options.SelectionMode == "Random") {
                // Fisher-Yates gives every eligible subset the same chance. Repeats
                // across batches are allowed; duplicates within a batch never are.
                for (int index = candidates.Count - 1; index > 0; index--) {
                    int other = Random.Shared.Next(0, index + 1);
                    (candidates[index], candidates[other]) = (candidates[other], candidates[index]);
                }
            }
            foreach (uint id in candidates) {
                if (rotating.Count >= slots) break;
                if (current.Add(id)) rotating.Add(id);
            }
        }
        State.CurrentRotating = rotating;
    }

    private void RequireKnown(IEnumerable<uint> ids) {
        foreach (uint id in ids) {
            if (!State.Library.ContainsKey(id)) throw new ArgumentException("游戏不在当前游戏库中。");
        }
    }

    private static HashSet<uint> Materialize(IEnumerable<uint> ids) {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new HashSet<uint>();
        foreach (uint id in ids) result.Add(id);
        if (result.Contains(0)) {
            throw new ArgumentException("AppID 不能为 0。", nameof(ids));
        }
        return result;
    }

    private static bool SameIds(IReadOnlyList<uint> left, IReadOnlyList<uint> right) {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++) {
            if (left[index] != right[index]) return false;
        }
        return true;
    }
}
