using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Storage;
using SmartIdle.Core;

namespace SmartIdle;

internal sealed class BotCoordinator(Bot bot, IdleOptions options, Func<SteamSession?> getSession) : IAsyncDisposable {
    private static readonly ConcurrentDictionary<string, BotCoordinator> AccountLeases = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim Gate = new(1, 1);
    private readonly object SessionLock = new();
    private readonly CancellationTokenSource Stop = new();
    private Task? Worker;
    private IdlePlanner? Planner;
    private string? StatePath;
    private string? LeasePath;
    private ulong StateAccount;
    private string? FatalError;
    private volatile bool ForceScan = true;
    private volatile bool LibraryVerified;
    private volatile bool HasLibrarySnapshot;
    private volatile bool SessionOwned;
    private volatile bool Blocked;
    private volatile bool FarmerYield;
    private volatile bool Reassert = true;
    private long InterruptedAt;
    private long ActiveSince;
    private long ConnectionGeneration;
    private long PlaybackGeneration;
    private DateTimeOffset ResumeAfter;
    private DateTimeOffset NextScan = DateTimeOffset.MaxValue;
    private DateTimeOffset NextSave;
    private DateTimeOffset LastWarning;
    private DateTimeOffset NextManualScan;
    private int LibraryRequests;
    private bool StartupFarmerPause = bot.BotConfig.FarmingPreferences.HasFlag(BotConfig.EFarmingPreferences.FarmingPausedByDefault);
    private uint[] SentGames = [];

    internal void Start() => Worker = RunAsync();
    internal void OnConnected() {
        lock (SessionLock) {
            Interlocked.Increment(ref ConnectionGeneration);
            LibraryVerified = HasLibrarySnapshot;
            ForceScan = !HasLibrarySnapshot;
            Blocked = false;
            FarmerYield = bot.CardsFarmer.NowFarming;
            InvalidateSession();
        }
    }
    internal void OnDisconnected() {
        lock (SessionLock) {
            Interlocked.Increment(ref ConnectionGeneration);
            LibraryVerified = false;
            InvalidateSession();
        }
    }
    internal void OnFarmerChanged(bool started) {
        lock (SessionLock) {
            FarmerYield = started;
            InvalidateSession();
        }
    }
    internal void OnPlayingBlocked(bool blocked) {
        lock (SessionLock) {
            if (Blocked == blocked) return;
            Blocked = blocked;
            if (!blocked) {
                ResumeAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, (int) (ASF.GlobalConfig?.MinFarmingDelayAfterBlock ?? 60)));
            }
            InvalidateSession();
        }
    }

    private void InvalidateSession() {
        PlaybackGeneration++;
        Interlocked.CompareExchange(ref InterruptedAt, Stopwatch.GetTimestamp(), 0);
        SessionOwned = false;
        Reassert = true;
    }

    private async Task RunAsync() {
        try {
            await Task.Delay(TimeSpan.FromSeconds(3), Stop.Token).ConfigureAwait(false);
            while (!Stop.IsCancellationRequested) {
                await Gate.WaitAsync(Stop.Token).ConfigureAwait(false);
                try { await TickAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (Stop.IsCancellationRequested) { break; }
                catch (Exception exception) {
                    // Do not continue sending after a state/IO failure. Retain the state file for repair.
                    FatalError = exception.Message;
                    await StopOwnedSessionAsync().ConfigureAwait(false);
                    bot.ArchiLogger.LogGenericError($"SmartIdle 已停止：{exception.Message}");
                } finally { Gate.Release(); }
                await Task.Delay(TimeSpan.FromSeconds(5), Stop.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
    }

    private async Task TickAsync() {
        Accrue();
        if (FatalError is not null) return;
        if (!bot.IsConnectedAndLoggedOn) { SessionOwned = false; return; }
        // Preserve ASF's configured startup pause. Once the user resumes the farmer,
        // any later pause is manual and must not be bypassed by the plugin.
        if (!bot.CardsFarmer.Paused) StartupFarmerPause = false;
        if (Planner is null || StateAccount != bot.SteamID) {
            if (Planner is not null) {
                await StopOwnedSessionAsync().ConfigureAwait(false);
                await SaveAsync().ConfigureAwait(false);
            }
            ReleaseLease();
            StateAccount = bot.SteamID;
            var directory = Path.GetDirectoryName(Path.GetFullPath(bot.GetFilePath(Bot.EFileType.Config)))!;
            StatePath = Path.GetFullPath(Path.Combine(directory, "SmartIdle-state", $"{StateAccount.ToString(CultureInfo.InvariantCulture)}.json"));
            if (!AccountLeases.TryAdd(StatePath, this)) {
                throw new InvalidOperationException("同一配置目录下的此 Steam 账号已由另一个 SmartIdle 机器人管理；请仅为一个机器人启用插件。");
            }
            LeasePath = StatePath;
            Planner = new IdlePlanner(options, await IdleStateStore.LoadAsync(StatePath, Stop.Token).ConfigureAwait(false));
            HasLibrarySnapshot = Planner.State.Initialized;
            LibraryVerified = HasLibrarySnapshot;
            ForceScan = !HasLibrarySnapshot;
            NextScan = options.RefreshHours == 0 ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(options.RefreshHours);
            NextSave = DateTimeOffset.UtcNow.AddMinutes(5);
        }

        var now = DateTimeOffset.UtcNow;
        if (ForceScan || now >= NextScan) {
            ForceScan = false;
            long generation = Interlocked.Read(ref ConnectionGeneration);
            IReadOnlyList<OwnedGame>? games = null;
            try {
                LibraryRequests++;
                games = getSession() is { } session ? await session.ReadLibraryAsync(bot.SteamID, Stop.Token).ConfigureAwait(false) : null;
                if (games is null) throw new IOException("无法获取游戏库，保留已有清单。");
            } catch (OperationCanceledException) when (Stop.IsCancellationRequested) { throw; }
            catch (Exception exception) {
                games = null;
                NextScan = HasLibrarySnapshot && options.RefreshHours == 0 ? DateTimeOffset.MaxValue : now.AddMinutes(5);
                if (now >= LastWarning.AddMinutes(5)) {
                    LastWarning = now;
                    bot.ArchiLogger.LogGenericWarning($"SmartIdle 刷新失败：{exception.Message}");
                }
            }
            Accrue();
            if (games is not null) {
                lock (SessionLock) {
                    if (!bot.IsConnectedAndLoggedOn || generation != Interlocked.Read(ref ConnectionGeneration) || StateAccount != bot.SteamID) return;
                    Planner.RefreshLibrary(games);
                    HasLibrarySnapshot = true;
                    LibraryVerified = true;
                    Reassert = true;
                }
                NextScan = options.RefreshHours == 0 ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(options.RefreshHours);
                // Persistence errors are fatal and must not be swallowed as a transient Steam error.
                await SaveAsync().ConfigureAwait(false);
                bot.ArchiLogger.LogGenericInfo($"SmartIdle 游戏库已刷新：{games.Count} 款，已批准 {Planner.State.Approved.Count} 款，待加入 {Planner.State.Pending.Count} 款。");
            }
        }

        if (!CanPlay()) {
            await StopOwnedSessionAsync().ConfigureAwait(false);
            if (DateTimeOffset.UtcNow >= NextSave) await SaveAsync().ConfigureAwait(false);
            return;
        }
        var batch = Planner.CurrentOrSelectBatch();
        if (batch.Count == 0) { await StopOwnedSessionAsync().ConfigureAwait(false); return; }
        if (Planner.State.RemainingRotationSeconds <= 0) {
            lock (SessionLock) {
                Accrue();
                ActiveSince = 0;
                batch = Planner.Rotate();
            }
            await SaveAsync().ConfigureAwait(false);
            Reassert = true;
        }
        bool needsPlayback;
        lock (SessionLock) {
            needsPlayback = Reassert || !SessionOwned || !Sequence.Equal(batch, SentGames);
        }
        bool sent = needsPlayback && await PlayBatchAsync(batch).ConfigureAwait(false);
        if (sent) {
            await SaveAsync().ConfigureAwait(false);
            bot.ArchiLogger.LogGenericInfo($"SmartIdle 当前挂时长：{string.Join(", ", batch)}。");
        }
        if (DateTimeOffset.UtcNow >= NextSave) await SaveAsync().ConfigureAwait(false);
    }

    private bool CanPlay() {
        lock (SessionLock) {
            return !Stop.IsCancellationRequested && FatalError is null && Planner is not null && !Planner.State.Paused && LibraryVerified &&
                bot.IsConnectedAndLoggedOn && bot.IsPlayingPossible && !Blocked && !FarmerYield &&
                !bot.CardsFarmer.NowFarming && (!bot.CardsFarmer.Paused || StartupFarmerPause) && DateTimeOffset.UtcNow >= ResumeAfter;
        }
    }

    private void Accrue() {
        lock (SessionLock) {
            if (ActiveSince == 0 || Planner is null) return;
            long end = Interlocked.Read(ref InterruptedAt);
            if (end == 0) end = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(0, (end - ActiveSince) / (double) Stopwatch.Frequency);
            Planner.AccruePlaytime(SentGames, elapsed);
            Planner.State.RemainingRotationSeconds = Math.Max(0, Planner.State.RemainingRotationSeconds - elapsed);
            ActiveSince = SessionOwned ? end : 0;
        }
    }

    private async Task<bool> PlayBatchAsync(IReadOnlyCollection<uint> batch) {
        string? customName = bot.BotConfig.CustomGamePlayedWhileIdle;
        SteamSession? session = null;
        long generation = 0;
        return await PlaybackTransition.ApplyAsync(customName, () => {
            lock (SessionLock) {
                if (!CanPlay() || (session = getSession()) is null) return false;
                Accrue();
                generation = PlaybackGeneration;
                Reassert = true;
                if (!string.IsNullOrEmpty(customName)) {
                    if (!session.SendGames([])) return false;
                    // We still own this cleared session, so disposal can restore ASF's list.
                    // No game receives locally estimated time during the reset gap.
                    ActiveSince = 0;
                    SessionOwned = true;
                }
                return true;
            }
        }, () => {
            lock (SessionLock) {
                // Callbacks never await Gate. A disconnect, farmer or block event during
                // the delay invalidates this transition, even if it clears again quickly.
                if (generation != PlaybackGeneration || !ReferenceEquals(session, getSession()) || !CanPlay()) return false;
                if (session?.SendGames(batch, customName) != true) return false;
                SentGames = new List<uint>(batch).ToArray();
                Interlocked.Exchange(ref InterruptedAt, 0);
                Reassert = false;
                SessionOwned = true;
                ActiveSince = Stopwatch.GetTimestamp();
                return true;
            }
        }, Stop.Token).ConfigureAwait(false);
    }

    private bool CanRestoreIdle() => bot.IsConnectedAndLoggedOn && bot.IsPlayingPossible && !Blocked &&
        !bot.CardsFarmer.NowFarming && !FarmerYield && (!bot.CardsFarmer.Paused || StartupFarmerPause);

    private async Task StopOwnedSessionAsync() {
        SteamSession? session;
        long generation;
        var fallback = new List<uint>();
        string? customName = bot.BotConfig.CustomGamePlayedWhileIdle;
        lock (SessionLock) {
            Accrue();
            // Sending an empty list must never stop a farmer or a human-controlled Steam session.
            // Native ASF pause and play both set CardsFarmer.Paused. They cannot be distinguished
            // here; relinquish control without clearing a potentially manual play list. To stop
            // plugin games deterministically, use SIDLE PAUSE before native pause/play commands.
            session = SessionOwned && CanRestoreIdle() ? getSession() : null;
            generation = ++PlaybackGeneration;
            SessionOwned = false;
            ActiveSince = 0;
            Reassert = true;
            if (session is null) return;
            int capacity = PlaylistCapacity.GameSlots(32, customName);
            foreach (uint id in bot.BotConfig.GamesPlayedWhileIdle) {
                if (fallback.Count >= capacity) break;
                fallback.Add(id);
            }
        }
        try {
            await PlaybackTransition.ApplyAsync(customName, () => {
                lock (SessionLock) {
                    if (generation != PlaybackGeneration || !ReferenceEquals(session, getSession()) || !CanRestoreIdle()) return false;
                    return string.IsNullOrEmpty(customName) || session.SendGames([]);
                }
            }, () => {
                lock (SessionLock) {
                    if (generation != PlaybackGeneration || !ReferenceEquals(session, getSession()) || !CanRestoreIdle()) return false;
                    return session.SendGames(fallback, customName);
                }
            }).ConfigureAwait(false);
        } catch (Exception exception) {
            bot.ArchiLogger.LogGenericWarning($"SmartIdle 停止播放请求失败：{exception.Message}");
        }
    }

    private async Task SaveAsync() {
        if (Planner is null || StatePath is null) return;
        await IdleStateStore.SaveAsync(StatePath, Planner.State, Stop.Token).ConfigureAwait(false);
        NextSave = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    internal async Task<string?> CommandAsync(string[] args) {
        await Gate.WaitAsync().ConfigureAwait(false);
        try {
            if (Stop.IsCancellationRequested) return "SmartIdle：此机器人模块已关闭或重载，请重新执行命令。";
            Accrue();
            if (FatalError is not null) return $"SmartIdle 已停止：{FatalError}；原状态文件已保留，请修复后重载机器人。";
            string action = Sequence.Argument(args, 1)?.ToUpperInvariant() ?? "STATUS";
            if (action == "HELP") return Help;
            if (Planner is null) return "SmartIdle 正在等待 Steam 登录和状态初始化，请稍后重试。";
            var state = Planner.State;
            string response;
            bool save = false;
            switch (action) {
                case "STATUS":
                    response = $"状态：{DescribeStatus()}；模式 {options.SelectionMode}；批准 {state.Approved.Count}，待加入 {state.Pending.Count}，常驻目标 {state.PinnedGames.Count}；当前批次 {Planner.CurrentOrSelectBatch().Count}/{options.ConcurrentGames}；本批剩余 {Math.Ceiling(state.RemainingRotationSeconds / 60)} 分钟；游戏库查询 {LibraryRequests} 次（本次模块运行，未含 ASF 自身通信）。";
                    break;
                case "SCAN":
                    if (DateTimeOffset.UtcNow < NextManualScan) return "SmartIdle：刷新请求过于频繁，请稍后再试（至少间隔 5 分钟）。";
                    NextManualScan = DateTimeOffset.UtcNow.AddMinutes(5);
                    ForceScan = true; response = "已安排刷新游戏库；新增候选只进入待加入清单。"; break;
                case "LIST": response = List(Sequence.Argument(args, 2), Sequence.Argument(args, 3)); break;
                case "WHY":
                    var reasons = new List<string>();
                    foreach (uint id in ParseIds(args)) reasons.Add($"{id}：{Planner.Explain(id)}");
                    response = string.Join('\n', reasons); break;
                case "PREVIEW":
                    var batch = Planner.CurrentOrSelectBatch();
                    var waiting = new List<uint>();
                    foreach (uint id in state.Queue) {
                        if (!state.CurrentRotating.Contains(id)) waiting.Add(id);
                        if (waiting.Count >= 32) break;
                    }
                    response = $"候选播放列表（不代表已启动）：\n{FormatGames(batch)}\n余下轮换队列：{string.Join(", ", waiting)}";
                    break;
                case "APPROVE":
                    if (!LibraryVerified) throw new ArgumentException("请先等待本次连接的游戏库刷新成功。");
                    if (args.Length == 3 && args[2].Equals("ALL", StringComparison.OrdinalIgnoreCase)) Planner.ApproveAll();
                    else Planner.Approve(ParseIds(args));
                    response = "已确认加入。"; save = true; break;
                case "REJECT": Planner.Reject(ParseIds(args)); response = "已拒绝加入，不再自动提示这些游戏。"; save = true; break;
                case "PIN": Planner.Pin(ParseIds(args)); response = "已设为手动常驻；常驻不受 0 时长筛选限制，未返回游戏库资料的项目请确认许可证与计时。"; save = true; break;
                case "UNPIN": Planner.Unpin(ParseIds(args)); response = "已取消常驻，符合条件时回到轮换池。"; save = true; break;
                case "EXCLUDE": Planner.Exclude(ParseIds(args)); response = "已加入禁止名单。"; save = true; break;
                case "UNEXCLUDE": Planner.Unexclude(ParseIds(args)); response = "已移出运行时禁止名单；符合条件的游戏进入待加入。配置 Blacklist 仍然有效。"; save = true; break;
                case "PAUSE":
                    state.Paused = true; await StopOwnedSessionAsync().ConfigureAwait(false);
                    response = "插件已暂停，重启后仍保持暂停；已交还 ASF 原有空闲播放设置，不能用此命令停止原有挂机列表。";
                    save = true; break;
                case "RESUME": state.Paused = false; response = "插件已恢复；仍遵守挂卡、ASF 手动暂停和本人游玩的优先级。"; save = true; break;
                case "ROTATE":
                    lock (SessionLock) { ActiveSince = 0; Planner.Rotate(); }
                    response = "已选择下一批；常驻游戏保留，等待可运行时播放。"; save = true; break;
                default: return "SmartIdle：未知命令。" + Help;
            }
            if (save) {
                Reassert = true;
                await SaveAsync().ConfigureAwait(false);
            }
            return "SmartIdle：" + response;
        } catch (ArgumentException exception) { return "SmartIdle：" + exception.Message; }
        catch (Exception exception) {
            FatalError = exception.Message;
            await StopOwnedSessionAsync().ConfigureAwait(false);
            return $"SmartIdle 已停止：{exception.Message}；请检查状态文件写入权限及日志。";
        } finally { Gate.Release(); }
    }

    private string DescribeStatus() {
        lock (SessionLock) {
            if (Planner!.State.Paused) return "插件已暂停";
            if (!bot.IsConnectedAndLoggedOn) return "Steam 未连接";
            if (!LibraryVerified) return "等待游戏库校验";
            if (!bot.IsPlayingPossible || Blocked) return "本人游玩或游戏库被占用";
            if (bot.CardsFarmer.Paused && !StartupFarmerPause) return "ASF 已暂停或手动播放；插件停止接管";
            if (bot.CardsFarmer.NowFarming || FarmerYield) return "挂卡优先，等待挂卡结束";
            if (DateTimeOffset.UtcNow < ResumeAfter) return "等待游玩结束后的冷却";
            return SessionOwned ? "正在挂时长" : "等待调度";
        }
    }

    private string List(string? category, string? pageText) {
        var state = Planner!.State;
        IEnumerable<uint> ids = category?.ToUpperInvariant() switch {
            null or "PENDING" => state.Pending,
            "APPROVED" => state.Approved,
            "PINNED" => state.PinnedGames,
            "EXCLUDED" => ExcludedIds(),
            "REJECTED" => state.Rejected,
            "ALL" => state.Library.Keys,
            _ => throw new ArgumentException("清单类型：PENDING、APPROVED、PINNED、EXCLUDED、REJECTED、ALL。")
        };
        int page = 1;
        if (pageText is not null && (!int.TryParse(pageText, out page) || page is < 1 or > 1000000)) throw new ArgumentException("页码必须是正整数，最大 1000000。");
        var sorted = new List<uint>(ids);
        sorted.Sort();
        int pages = Math.Max(1, (sorted.Count + 9) / 10);
        int start = Math.Min(sorted.Count, (page - 1) * 10);
        return $"{category?.ToUpperInvariant() ?? "PENDING"}，共 {sorted.Count} 款，第 {page}/{pages} 页\n" + FormatGames(sorted.GetRange(start, Math.Min(10, sorted.Count - start)));
    }

    private HashSet<uint> ExcludedIds() {
        var result = new HashSet<uint>(Planner!.State.Excluded);
        foreach (uint id in options.Blacklist) result.Add(id);
        return result;
    }

    private string FormatGames(IEnumerable<uint> ids) {
        var lines = new List<string>();
        foreach (uint id in ids) {
            string name = Planner!.State.Library.TryGetValue(id, out var game) ? game.Name.Replace('\r', ' ').Replace('\n', ' ') : "未知游戏";
            if (name.Length > 60) name = name[..60];
            lines.Add($"{id} · {name} · {game?.PlaytimeMinutes ?? 0} 分钟 · {Planner.Explain(id)}");
        }
        return string.Join('\n', lines);
    }

    private static uint[] ParseIds(string[] args) {
        var parts = string.Join(" ", args, 2, Math.Max(0, args.Length - 2)).Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("请提供一个或多个 AppID。");
        var ids = new List<uint>();
        foreach (string part in parts) {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0) throw new ArgumentException($"无效 AppID：{part}");
            ids.Add(id);
        }
        return Sequence.Distinct(ids);
    }

    internal const string Help = "\nSIDLE STATUS | SCAN | PREVIEW\nSIDLE LIST <PENDING|APPROVED|PINNED|EXCLUDED|REJECTED|ALL> [页码]\nSIDLE APPROVE <ALL|AppID...>\nSIDLE REJECT|PIN|UNPIN|EXCLUDE|UNEXCLUDE|WHY <AppID...>\nSIDLE PAUSE | RESUME | ROTATE";

    private void ReleaseLease() {
        if (LeasePath is { } path) {
            AccountLeases.TryRemove(new KeyValuePair<string, BotCoordinator>(path, this));
            LeasePath = null;
        }
    }

    public async ValueTask DisposeAsync() {
        await Stop.CancelAsync().ConfigureAwait(false);
        try {
            if (Worker is not null) await Worker.ConfigureAwait(false);
            await Gate.WaitAsync().ConfigureAwait(false);
            try {
                await StopOwnedSessionAsync().ConfigureAwait(false);
                if (Planner is not null && StatePath is not null && FatalError is null) await IdleStateStore.SaveAsync(StatePath, Planner.State).ConfigureAwait(false);
            } catch (Exception exception) { bot.ArchiLogger.LogGenericError($"SmartIdle 退出保存失败：{exception.Message}"); }
            finally { Gate.Release(); }
        } finally { ReleaseLease(); }
        // A command may have already obtained this coordinator from the plugin dictionary.
        // Keep these tiny synchronization objects valid until that last reference is collected.
    }
}
