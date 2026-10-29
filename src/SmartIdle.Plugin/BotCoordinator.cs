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
    private readonly PlaybackLifecycle Lifecycle = new();
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
    private volatile bool FarmerYield;
    private bool NativeConfigInitialized;
    private bool ConfigReloadPending;
    private readonly string ConfigPath = Path.GetFullPath(bot.GetFilePath(Bot.EFileType.Config));
    private long InterruptedAt;
    private long ActiveSince;
    private long ConnectionGeneration;
    private long NativeEpoch;
    private long FarmerEventSequence;
    private long LastObservedFarmerEvent;
    private bool LastFarmerPaused = bot.CardsFarmer.Paused;
    private CancellationTokenSource? ActiveConfigWrite;
    private DateTimeOffset NextScan = DateTimeOffset.MaxValue;
    private DateTimeOffset NextSave;
    private DateTimeOffset LastWarning;
    private DateTimeOffset NextManualScan;
    private int LibraryRequests;
    private bool StartupFarmerPause = bot.BotConfig.FarmingPreferences.HasFlag(BotConfig.EFarmingPreferences.FarmingPausedByDefault);
    private uint[] SentGames = [];
    private GameRatingService? RatingService;
    private Task<IReadOnlyDictionary<uint, GameRating>>? RatingRefresh;
    private IReadOnlyDictionary<uint, GameRating>? AppliedRatings;
    private DateTimeOffset NextRatings;
    private DateTimeOffset NextManualRatings;
    private bool ForceRatings;
    private string? RatingWarning;

    internal void Start() {
        if (bot.IsConnectedAndLoggedOn) OnConnected();
        Worker = RunAsync();
    }

    internal void OnConnected() {
        lock (SessionLock) {
            Interlocked.Increment(ref ConnectionGeneration);
            LibraryVerified = HasLibrarySnapshot;
            ForceScan = !HasLibrarySnapshot;
            Lifecycle.OnConnected(DateTimeOffset.UtcNow);
            if (FarmerEventSequence == 0) FarmerYield = !bot.CardsFarmer.Paused;
            else FarmerYield = FarmerYield || bot.CardsFarmer.NowFarming;
            LastFarmerPaused = bot.CardsFarmer.Paused;
            LastObservedFarmerEvent = FarmerEventSequence;
            InvalidateSession();
        }
    }
    internal void OnDisconnected() {
        lock (SessionLock) {
            Interlocked.Increment(ref ConnectionGeneration);
            LibraryVerified = false;
            Lifecycle.OnDisconnected();
            FarmerEventSequence = 0;
            LastObservedFarmerEvent = 0;
            InvalidateSession();
        }
    }
    internal void OnFarmerChanged(bool started) {
        lock (SessionLock) {
            FarmerEventSequence++;
            FarmerYield = started;
            InvalidateSession();
        }
    }
    internal void OnPlayingBlocked(bool blocked) {
        lock (SessionLock) {
            if (!Lifecycle.OnPlayingBlocked(blocked, DateTimeOffset.UtcNow, ASF.GlobalConfig?.MinFarmingDelayAfterBlock ?? 60)) return;
            InvalidateSession();
        }
    }

    internal void OnLibraryLockChanged(uint accountId) {
        lock (SessionLock) {
            bool blocked = accountId != 0 && accountId != new SteamKit2.SteamID(bot.SteamID).AccountID;
            if (!Lifecycle.OnLibraryBlocked(blocked, DateTimeOffset.UtcNow, ASF.GlobalConfig?.MinFarmingDelayAfterBlock ?? 60)) return;
            InvalidateSession();
        }
    }

    internal long FarmerEventStamp { get { lock (SessionLock) return FarmerEventSequence; } }

    internal void OnFarmerResumeRequested(long previousStamp) {
        lock (SessionLock) {
            if (FarmerEventSequence != previousStamp) return;
            FarmerYield = true;
            InvalidateSession();
        }
    }

    private void ObserveFarmerPause() {
        lock (SessionLock) {
            bool paused = bot.CardsFarmer.Paused;
            if (LastFarmerPaused && !paused && FarmerEventSequence == LastObservedFarmerEvent) {
                FarmerYield = true;
                InvalidateSession();
            }
            if (!paused) StartupFarmerPause = false;
            LastFarmerPaused = paused;
            LastObservedFarmerEvent = FarmerEventSequence;
        }
    }

    private void InvalidateSession() {
        NativeEpoch++;
        ActiveConfigWrite?.Cancel();
        Interlocked.CompareExchange(ref InterruptedAt, Stopwatch.GetTimestamp(), 0);
        SessionOwned = false;
    }

    private async Task RunAsync() {
        try {
            await Task.Delay(TimeSpan.FromSeconds(3), Stop.Token).ConfigureAwait(false);
            while (!Stop.IsCancellationRequested) {
                await Gate.WaitAsync(Stop.Token).ConfigureAwait(false);
                try { await TickAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (Stop.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { StopTracking(); }
                catch (Exception exception) {
                    // Do not continue sending after a state/IO failure. Retain the state file for repair.
                    FatalError = exception.Message;
                    StopTracking();
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
        ObserveFarmerPause();
        if (Planner is null || StateAccount != bot.SteamID) {
            if (Planner is not null) {
                StopTracking();
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
            AppliedRatings = null;
            if (options.SelectionMode == "QualityWeighted") RatingService = GameRatingService.ForDirectory(directory);
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
                            }
                NextScan = options.RefreshHours == 0 ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddHours(options.RefreshHours);
                // Persistence errors are fatal and must not be swallowed as a transient Steam error.
                await SaveAsync().ConfigureAwait(false);
                bot.ArchiLogger.LogGenericInfo($"SmartIdle 游戏库已刷新：{games.Count} 款，已批准 {Planner.State.Approved.Count} 款，待加入 {Planner.State.Pending.Count} 款。");
            }
        }

        PumpRatings();
        if (ConfigReloadPending) { StopTracking(); return; }
        if (Planner.State.Paused) {
            StopTracking();
            if (!NativeConfigInitialized || Planner.State.RestoreOriginalOnIdle) {
                await RestoreNativeConfigurationAsync(Planner.State.RestoreOriginalOnIdle).ConfigureAwait(false);
            }
            if (DateTimeOffset.UtcNow >= NextSave) await SaveAsync().ConfigureAwait(false);
            return;
        }
        if (!CanPlay()) {
            StopTracking();
            if (DateTimeOffset.UtcNow >= NextSave) await SaveAsync().ConfigureAwait(false);
            return;
        }
        var batch = Planner.CurrentOrSelectBatch();
        if (batch.Count == 0) {
            StopTracking();
            // No approved or pinned games: retain the user's original ASF idle list.
            await RestoreNativeConfigurationAsync(disablePlugin: false).ConfigureAwait(false);
            return;
        }
        if (Planner.State.RemainingRotationSeconds <= 0) {
            StopTracking();
            batch = Planner.Rotate();
            await SaveAsync().ConfigureAwait(false);
        }
        if (!NativeConfigInitialized || !NativeGamesMatch(batch) || !Sequence.Equal(batch, SentGames)) {
            if (await ApplyNativeBatchAsync(batch).ConfigureAwait(false)) return;
        }
        lock (SessionLock) {
            if (!CanPlay() || !NativeGamesMatch(batch)) return;
            if (!SessionOwned) {
                SentGames = new List<uint>(batch).ToArray();
                Interlocked.Exchange(ref InterruptedAt, 0);
                SessionOwned = true;
                ActiveSince = Stopwatch.GetTimestamp();
            }
        }
        if (DateTimeOffset.UtcNow >= NextSave) await SaveAsync().ConfigureAwait(false);
    }

    // Network and cache I/O run outside Gate. Only completed snapshots enter the planner,
    // so a slow or unavailable rating endpoint cannot hold commands or idle playback.
    private void PumpRatings() {
        if (Planner is null || RatingService is null) return;
        var candidates = new List<uint>(Planner.State.Queue);
        if (RatingRefresh?.IsCompleted == true) {
            try {
                RatingRefresh.GetAwaiter().GetResult();
                RatingWarning = null;
            } catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
            catch (Exception exception) {
                RatingWarning = $"评分刷新失败（{exception.GetType().Name}），继续使用旧评分";
                bot.ArchiLogger.LogGenericWarning($"SmartIdle {RatingWarning}");
            }
            RatingRefresh = null;
            NextRatings = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        var snapshot = RatingService.Snapshot(candidates);
        bool changed = AppliedRatings is null || snapshot.Count != AppliedRatings.Count;
        if (!changed) foreach (var pair in snapshot) {
            if (!AppliedRatings!.TryGetValue(pair.Key, out var previous) || previous != pair.Value) { changed = true; break; }
        }
        if (snapshot.Count > 0 && changed) {
            Planner.SetRatings(snapshot);
            AppliedRatings = snapshot;
        }
        if (RatingRefresh is not null || (DateTimeOffset.UtcNow < NextRatings && !ForceRatings) || candidates.Count == 0) return;
        bool force = ForceRatings;
        ForceRatings = false;
        var service = RatingService;
        RatingRefresh = Task.Run(() => service.RefreshAsync(candidates, options.RatingRefreshHours, force, Stop.Token));
    }

    private CancellationTokenSource? BeginConfigWrite(bool requireActive, out long epoch) {
        lock (SessionLock) {
            ObserveFarmerPause();
            epoch = NativeEpoch;
            if (!(requireActive ? CanPlay() : SafeToReloadConfiguration())) return null;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Stop.Token);
            ActiveConfigWrite = cancellation;
            return cancellation;
        }
    }

    private bool CanCommitConfig(CancellationTokenSource cancellation, long epoch, bool requireActive) {
        lock (SessionLock) {
            // Resume starts scanning before NowFarming or a farmer event changes.
            // Re-read Paused at the final commit boundary as well as at each tick.
            ObserveFarmerPause();
            return !cancellation.IsCancellationRequested && epoch == NativeEpoch &&
                (requireActive ? CanPlay() : SafeToReloadConfiguration());
        }
    }

    private void EndConfigWrite(CancellationTokenSource cancellation) {
        lock (SessionLock) {
            if (ReferenceEquals(ActiveConfigWrite, cancellation)) ActiveConfigWrite = null;
        }
        cancellation.Dispose();
    }

    private async Task<bool> ApplyNativeBatchAsync(IReadOnlyCollection<uint> batch) {
        var cancellation = BeginConfigWrite(requireActive: true, out long epoch);
        if (cancellation is null) return false;
        try {
            StopTracking();
            await SaveAsync().ConfigureAwait(false);
            NativeReloadState.Mark(ConfigPath, bot.CardsFarmer.Paused);
            bool changed;
            try {
                changed = await NativeIdleConfigStore.ApplyAsync(ConfigPath, batch, cancellation.Token,
                    () => CanCommitConfig(cancellation, epoch, requireActive: true)).ConfigureAwait(false);
            } catch { NativeReloadState.Cancel(ConfigPath); throw; }
            NativeConfigInitialized = true;
            if (changed || !NativeGamesMatch(batch)) {
                if (!changed) NativeReloadState.Cancel(ConfigPath);
                ConfigReloadPending = true;
                bot.ArchiLogger.LogGenericInfo($"SmartIdle 已更新 GamesPlayedWhileIdle：{string.Join(", ", batch)}；等待 ASF 重载此机器人。");
                return true;
            }
            NativeReloadState.Cancel(ConfigPath);
            return false;
        } finally { EndConfigWrite(cancellation); }
    }

    private async Task<bool> RestoreNativeConfigurationAsync(bool disablePlugin) {
        var cancellation = BeginConfigWrite(requireActive: false, out long epoch);
        if (cancellation is null) return false;
        try {
            NativeReloadState.Mark(ConfigPath, bot.CardsFarmer.Paused);
            bool changed;
            try {
                changed = await NativeIdleConfigStore.RestoreAsync(ConfigPath, disablePlugin, cancellation.Token,
                    () => CanCommitConfig(cancellation, epoch, requireActive: false)).ConfigureAwait(false);
            } catch { NativeReloadState.Cancel(ConfigPath); throw; }
            NativeConfigInitialized = true;
            if (disablePlugin && Planner is not null && StatePath is not null) {
                // Clear only after config and backup commit. A canceled request remains
                // persisted and will be retried after the next safe idle transition.
                Planner.State.RestoreOriginalOnIdle = false;
                await IdleStateStore.SaveAsync(StatePath, Planner.State).ConfigureAwait(false);
            }
            if (!changed) NativeReloadState.Cancel(ConfigPath);
            else {
                ConfigReloadPending = true;
                bot.ArchiLogger.LogGenericInfo("SmartIdle 已还原原始 GamesPlayedWhileIdle；等待 ASF 重载此机器人。");
            }
            return changed;
        } finally { EndConfigWrite(cancellation); }
    }

    private bool CanPlay() {
        lock (SessionLock) {
            return !Stop.IsCancellationRequested && FatalError is null && Planner is not null && !Planner.State.Paused && LibraryVerified &&
                SafeToReloadConfiguration() && (!bot.CardsFarmer.Paused || StartupFarmerPause);
        }
    }

    private bool SafeToReloadConfiguration() {
        lock (SessionLock) {
            return bot.IsConnectedAndLoggedOn && bot.IsPlayingPossible && Lifecycle.CanPlay(DateTimeOffset.UtcNow) &&
                !FarmerYield && !bot.CardsFarmer.NowFarming && !Stop.IsCancellationRequested;
        }
    }

    private bool NativeGamesMatch(IReadOnlyCollection<uint> games) {
        return new HashSet<uint>(games).SetEquals(bot.BotConfig.GamesPlayedWhileIdle);
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

    private void StopTracking() {
        lock (SessionLock) {
            Accrue();
            SessionOwned = false;
            ActiveSince = 0;
        }
    }

    private async Task SaveAsync() {
        if (Planner is null || StatePath is null) return;
        await IdleStateStore.SaveAsync(StatePath, Planner.State, Stop.Token).ConfigureAwait(false);
        NextSave = DateTimeOffset.UtcNow.AddMinutes(5);
    }

    internal async Task<string?> CommandAsync(string[] args) {
        // Diagnostics must not hold Gate while recording: the scheduler and native ASF
        // continue to run so this observes actual outgoing requests from both writers.
        if (Sequence.Argument(args, 1)?.Equals("TRACE", StringComparison.OrdinalIgnoreCase) == true) {
            try {
                return getSession() is { } session ? await session.TracePlaybackAsync(CancellationToken.None).ConfigureAwait(false) : "SmartIdle：Steam 会话未就绪。";
            } catch (OperationCanceledException) { return "SmartIdle：诊断因模块重载或关闭而结束。"; }
            catch (Exception exception) { return $"SmartIdle：诊断失败：{exception.Message}"; }
        }
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
                    if (RatingService is not null) response += $"\n{RatingService.Status}{RatingWarning}";
                    break;
                case "RATINGS":
                    if (RatingService is null) return "SmartIdle：请先将 SelectionMode 设为 QualityWeighted 并重载机器人。";
                    if (DateTimeOffset.UtcNow < NextManualRatings) return "SmartIdle：评分刷新请求至少间隔 5 分钟。";
                    NextManualRatings = DateTimeOffset.UtcNow.AddMinutes(5);
                    ForceRatings = true;
                    response = "已安排后台刷新已批准轮换游戏的评分；查询受共享限速及失败退避限制，当前批次继续运行。";
                    break;
                case "WEIGHT":
                    if (args.Length != 4 || !uint.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint weightedId) || weightedId == 0)
                        throw new ArgumentException("格式：SIDLE [BotName] WEIGHT <AppID> <1～100|AUTO>。");
                    int? weight = null;
                    if (!args[3].Equals("AUTO", StringComparison.OrdinalIgnoreCase)) {
                        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedWeight) || parsedWeight is < 1 or > 100)
                            throw new ArgumentException("手动权重必须在 1～100 之间，AUTO 恢复自动评分。");
                        weight = parsedWeight;
                    }
                    Planner.SetWeight(weightedId, weight);
                    response = $"{weightedId}：{Planner.ExplainWeight(weightedId)}；下次选择批次时生效，当前批次保持。";
                    if (options.SelectionMode != "QualityWeighted") response += " 当前模式不使用评分权重，请切换到 QualityWeighted。";
                    save = true;
                    break;
                case "SCAN":
                    if (DateTimeOffset.UtcNow < NextManualScan) return "SmartIdle：刷新请求过于频繁，请稍后再试（至少间隔 5 分钟）。";
                    NextManualScan = DateTimeOffset.UtcNow.AddMinutes(5);
                    ForceScan = true; response = "已安排刷新游戏库；新增候选只进入待加入清单。"; break;
                case "LIST": response = List(Sequence.Argument(args, 2), Sequence.Argument(args, 3)); break;
                case "WHY":
                    var reasons = new List<string>();
                    foreach (uint id in ParseIds(args)) reasons.Add($"{id}：{Planner.Explain(id)}；{Planner.ExplainWeight(id)}");
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
                    state.Paused = true; StopTracking();
                    response = "插件已暂停；将在安全空闲时还原原始名单，ASF 会重载此机器人；挂卡中则等待挂卡结束。";
                    save = true; break;
                case "RESTORE":
                    state.Paused = true; state.RestoreOriginalOnIdle = true; StopTracking();
                    response = "已安排还原原始名单并禁用 SmartIdle；挂卡中则等待挂卡结束，完成后可卸载插件。"; save = true; break;
                case "RESUME": state.Paused = false; state.RestoreOriginalOnIdle = false; response = "插件已恢复；仍遵守挂卡、ASF 手动暂停和本人游玩的优先级。"; save = true; break;
                case "ROTATE":
                    lock (SessionLock) { ActiveSince = 0; Planner.Rotate(); }
                    response = "已选择下一批；常驻游戏保留，等待可运行时播放。"; save = true; break;
                default: return "SmartIdle：未知命令。" + Help;
            }
            if (save) {
                NativeConfigInitialized = false;
                await SaveAsync().ConfigureAwait(false);
            }
            if (action is "APPROVE" or "UNPIN" or "UNEXCLUDE") NextRatings = DateTimeOffset.MinValue;
            return "SmartIdle：" + response;
        } catch (ArgumentException exception) { return "SmartIdle：" + exception.Message; }
        catch (Exception exception) {
            FatalError = exception.Message;
            StopTracking();
            return $"SmartIdle 已停止：{exception.Message}；请检查状态文件写入权限及日志。";
        } finally { Gate.Release(); }
    }

    private string DescribeStatus() {
        lock (SessionLock) {
            if (ConfigReloadPending) return "等待 ASF 重载配置（此机器人会短暂重连）";
            if (Planner!.State.Paused) return Planner.State.RestoreOriginalOnIdle ? "等待还原原名单并禁用插件" : "插件已暂停";
            if (!bot.IsConnectedAndLoggedOn) return "Steam 未连接";
            if (!LibraryVerified) return "等待游戏库校验";
            if (!bot.IsPlayingPossible || Lifecycle.PlayingBlocked || Lifecycle.LibraryBlocked) return "本人游玩或游戏库被占用";
            if (bot.CardsFarmer.Paused && !StartupFarmerPause) return "ASF 已暂停或手动播放；插件停止接管";
            if (bot.CardsFarmer.NowFarming || FarmerYield) return "挂卡优先，等待挂卡结束";
            if (!Lifecycle.CanPlay(DateTimeOffset.UtcNow)) return "等待游玩结束后的冷却";
            return SessionOwned ? "ASF 原生空闲清单已配置，正在挂时长" : "等待配置同步";
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
        var pageIds = new List<uint>();
        for (int index = start; index < Math.Min(sorted.Count, start + 10); index++) pageIds.Add(sorted[index]);
        return $"{category?.ToUpperInvariant() ?? "PENDING"}，共 {sorted.Count} 款，第 {page}/{pages} 页\n" + FormatGames(pageIds);
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
            string score = options.SelectionMode == "QualityWeighted" ? $" · 权重 {Planner.WeightScore(id)}/100" : "";
            lines.Add($"{id} · {name} · Steam 快照 {game?.PlaytimeMinutes ?? 0} 分钟 · 排序估算 {Planner.EffectivePlaytimeMinutes(id):F1} 分钟{score} · {Planner.Explain(id)}");
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

    internal const string Help = "\nSIDLE STATUS | SCAN | PREVIEW | TRACE | RATINGS\nSIDLE LIST <PENDING|APPROVED|PINNED|EXCLUDED|REJECTED|ALL> [页码]\nSIDLE APPROVE <ALL|AppID...>\nSIDLE REJECT|PIN|UNPIN|EXCLUDE|UNEXCLUDE|WHY <AppID...>\nSIDLE WEIGHT <AppID> <1～100|AUTO>\nSIDLE PAUSE | RESUME | ROTATE | RESTORE";

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
            if (RatingRefresh is not null) {
                try { await RatingRefresh.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { bot.ArchiLogger.LogGenericWarning($"SmartIdle 评分后台任务已结束：{exception.GetType().Name}"); }
            }
            await Gate.WaitAsync().ConfigureAwait(false);
            try {
                StopTracking();
                if (Planner is not null && StatePath is not null && FatalError is null) await IdleStateStore.SaveAsync(StatePath, Planner.State).ConfigureAwait(false);
            } catch (Exception exception) { bot.ArchiLogger.LogGenericError($"SmartIdle 退出保存失败：{exception.Message}"); }
            finally { Gate.Release(); }
        } finally { ReleaseLease(); }
        // A command may have already obtained this coordinator from the plugin dictionary.
        // Keep these tiny synchronization objects valid until that last reference is collected.
    }
}
