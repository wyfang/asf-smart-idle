namespace SmartIdle.Core;

/// <summary>Pure scheduling and approval policy. The ASF adapter owns session gates and active-time accounting.</summary>
public sealed class IdlePlanner {
    public IdleOptions Options { get; }
    public IdleState State { get; }
    private readonly HashSet<uint> ConfigurationBlacklist;
    private IReadOnlyDictionary<uint, GameRating> Ratings = new Dictionary<uint, GameRating>();

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
        if (modeChanged) {
            State.CurrentRotating.Clear();
            State.QualityTargetShares.Clear();
            State.QualityDeficitSeconds.Clear();
        }
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
        var allocations = new Dictionary<uint, double>();
        foreach (uint id in ids) {
            double allocated = State.PluginAllocatedMinutes.GetValueOrDefault(id) + seconds / 60D;
            if (!double.IsFinite(allocated)) throw new ArgumentOutOfRangeException(nameof(seconds), "插件分配时长超过可保存范围。");
            allocations.Add(id, allocated);
            if (State.Library.ContainsKey(id)) {
                double total = EffectivePlaytimeMinutes(id) + seconds / 60D;
                if (!double.IsFinite(total)) throw new ArgumentOutOfRangeException(nameof(seconds), "估算时长超过可保存范围。");
                updates.Add(id, total);
            }
        }
        Dictionary<uint, double>? deficits = null;
        if (Options.SelectionMode == "QualityWeighted") {
            var served = new HashSet<uint>();
            foreach (uint id in State.Queue) {
                if (ids.Contains(id)) served.Add(id);
            }
            if (served.Count > 0) {
                // Only real rotating service creates credit. A partial/native old
                // batch cannot accrue the credit of slots that were not running.
                var targets = ComputeQualityShares(State.Queue, served.Count);
                deficits = new Dictionary<uint, double>(targets.Count);
                foreach (var pair in targets) {
                    double value = State.QualityDeficitSeconds.GetValueOrDefault(pair.Key) + pair.Value * seconds - (served.Contains(pair.Key) ? seconds : 0);
                    if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(seconds), "加权服务欠额超过可保存范围。");
                    deficits.Add(pair.Key, value);
                }
            }
        }
        foreach (var pair in updates) State.EstimatedPlaytimeMinutes[pair.Key] = pair.Value;
        foreach (var pair in allocations) State.PluginAllocatedMinutes[pair.Key] = pair.Value;
        if (deficits is not null) State.QualityDeficitSeconds = deficits;
    }

    public void SetRatings(IReadOnlyDictionary<uint, GameRating> ratings) {
        ArgumentNullException.ThrowIfNull(ratings);
        var snapshot = new Dictionary<uint, GameRating>();
        foreach (var pair in Ratings) snapshot.Add(pair.Key, pair.Value);
        var scores = new Dictionary<uint, int>(State.QualityAutomaticScores);
        foreach (var pair in ratings) {
            if (pair.Key == 0 || pair.Value is null) throw new ArgumentException("游戏评价快照包含无效 AppID 或记录。", nameof(ratings));
            pair.Value.Validate();
            snapshot[pair.Key] = pair.Value;
            scores[pair.Key] = pair.Value.Score(Options.ReputationWeightPercent);
        }
        Ratings = snapshot;
        State.QualityAutomaticScores = scores;
        Normalize();
    }

    public void SetWeight(uint appId, int? score) {
        if (appId == 0) throw new ArgumentException("AppID 不能为 0。", nameof(appId));
        if (score is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(score), "手动权重必须在 1～100 之间。");
        if (score is int value) State.ManualWeights[appId] = value;
        else State.ManualWeights.Remove(appId);
        // Explicit weights never add approval or interrupt the saved batch.
        Normalize();
    }

    public int WeightScore(uint appId) => State.ManualWeights.TryGetValue(appId, out int manual) ? manual : AutomaticScore(appId);

    public string ExplainWeight(uint appId) {
        string source;
        if (Ratings.TryGetValue(appId, out var rating)) {
            string freshness = DateTimeOffset.UtcNow - rating.FetchedAt > TimeSpan.FromHours(Options.RatingRefreshHours) ? "，缓存陈旧，继续使用" : "";
            source = $"Steam 评价 {rating.PositiveReviews}/{rating.TotalReviews} 条好评，FetchedAt={rating.FetchedAt:O}{freshness}";
            if (rating.TotalReviews == 0) source += "，无评价样本，使用中性分";
        } else if (State.QualityAutomaticScores.ContainsKey(appId)) {
            source = "上次自动评分快照，评价明细暂不可用";
        } else {
            source = "缺少有效评分，使用中性分，评价样本未知，FetchedAt=无";
        }
        string manual = State.ManualWeights.TryGetValue(appId, out int value) ? $"手动覆盖 {value}" : "无手动覆盖";
        string capacity = Options.SelectionMode == "QualityWeighted" && State.Queue.Count <= Options.ConcurrentGames - State.PinnedGames.Count
            ? "；候选不超过轮换槽位，全部同时运行，权重不产生时长差异" : "";
        return $"{source}；自动分 {AutomaticScore(appId)}；{manual}；最终分 {WeightScore(appId)}/100；强度 {Options.WeightStrength:G}；插件分配 {State.PluginAllocatedMinutes.GetValueOrDefault(appId):F1} 分钟{capacity}";
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
        if (Options.SelectionMode == "QualityWeighted") SynchronizeQualityState(slots);
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
            } else if (Options.SelectionMode == "QualityWeighted") {
                var position = new Dictionary<uint, int>();
                for (int index = 0; index < candidates.Count; index++) position.Add(candidates[index], index);
                double horizon = Options.RotationMinutes * 60D;
                candidates.Sort((left, right) => {
                    double leftDue = State.QualityDeficitSeconds.GetValueOrDefault(left) + State.QualityTargetShares.GetValueOrDefault(left) * horizon;
                    double rightDue = State.QualityDeficitSeconds.GetValueOrDefault(right) + State.QualityTargetShares.GetValueOrDefault(right) * horizon;
                    int comparison = rightDue.CompareTo(leftDue);
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

    private int AutomaticScore(uint appId) => State.QualityAutomaticScores.GetValueOrDefault(appId, 50);

    private void SynchronizeQualityState(int slots) {
        var targets = ComputeQualityShares(State.Queue, slots);
        bool changed = targets.Count != State.QualityTargetShares.Count;
        foreach (var pair in targets) {
            if (!State.QualityTargetShares.TryGetValue(pair.Key, out double saved) || Math.Abs(pair.Value - saved) > 1E-12) changed = true;
        }
        var deficits = new Dictionary<uint, double>(targets.Count);
        if (changed) {
            // Changing membership or proportions starts a new fairness epoch.
            // Newcomers and changed weights never collect a historical backlog.
            foreach (uint id in targets.Keys) deficits.Add(id, 0D);
        } else {
            foreach (uint id in targets.Keys) deficits.Add(id, State.QualityDeficitSeconds.GetValueOrDefault(id));
        }
        State.QualityDeficitSeconds = deficits;
        State.QualityTargetShares = targets;
    }

    private Dictionary<uint, double> ComputeQualityShares(IEnumerable<uint> appIds, int slots) {
        var weighted = new List<(uint Id, double Weight)>();
        double remainingWeight = 0;
        foreach (uint id in appIds) {
            double weight = Math.Pow(WeightScore(id) / 100D, Options.WeightStrength);
            weighted.Add((id, weight));
            remainingWeight += weight;
        }
        weighted.Sort(static (left, right) => {
            int comparison = right.Weight.CompareTo(left.Weight);
            return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
        });
        var result = new Dictionary<uint, double>(weighted.Count);
        int remainingSlots = Math.Min(slots, weighted.Count);
        if (remainingSlots <= 0) return result;
        // Water filling enforces a game's physical upper bound: it can be
        // present once per batch, even when several slots run concurrently.
        for (int index = 0; index < weighted.Count; index++) {
            var game = weighted[index];
            if (remainingSlots == weighted.Count - index || game.Weight * remainingSlots >= remainingWeight) {
                result.Add(game.Id, 1D);
                remainingSlots--;
                remainingWeight -= game.Weight;
            } else {
                double scale = remainingSlots / remainingWeight;
                for (; index < weighted.Count; index++) result.Add(weighted[index].Id, weighted[index].Weight * scale);
                break;
            }
        }
        return result;
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
