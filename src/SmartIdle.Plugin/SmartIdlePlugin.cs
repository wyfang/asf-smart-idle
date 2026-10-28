using System.Collections.Concurrent;
using System.Composition;
using System.Text.Json;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Storage;
using SmartIdle.Core;
using SteamKit2;

namespace SmartIdle;

[Export(typeof(IPlugin))]
public sealed class SmartIdlePlugin : IBot, IBotModules, IBotConnection, IBotSteamClient, IBotCommand2, IBotCardsFarmerInfo {
    private readonly ConcurrentDictionary<Bot, BotCoordinator> Coordinators = new();
    private readonly ConcurrentDictionary<Bot, SteamSession> Sessions = new();
    private readonly ConcurrentDictionary<Bot, IDisposable> Subscriptions = new();
    private readonly ConcurrentDictionary<Bot, byte> ResumeFarmerAfterReload = new();

    public string Name => "SmartIdle";
    public Version Version => typeof(SmartIdlePlugin).Assembly.GetName().Version!;
    public Task OnLoaded() => Task.CompletedTask;
    public Task OnBotInit(Bot bot) => Task.CompletedTask;

    public async Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
        IdleOptions? options = null;
        try {
            if (additionalConfigProperties?.TryGetValue("SmartIdle", out var json) == true) {
                options = json.Deserialize<IdleOptions>(IdleJson.Options) ?? throw new ArgumentException("SmartIdle 配置为空。");
                options.Validate();
                if (options.Enabled) {
                    options = options with { ConcurrentGames = PlaylistCapacity.GameSlots(options.ConcurrentGames, bot.BotConfig.CustomGamePlayedWhileIdle) };
                    options.Validate();
                }
            }
        } catch (Exception exception) {
            bot.ArchiLogger.LogGenericError($"SmartIdle 未启用：{exception.Message}");
            options = null;
        }
        string configPath = Path.GetFullPath(bot.GetFilePath(Bot.EFileType.Config));
        bool? pausedBeforeReload = NativeReloadState.Take(configPath);
        ResumeFarmerAfterReload.TryRemove(bot, out _);
        if (Coordinators.TryRemove(bot, out var previous)) await previous.DisposeAsync().ConfigureAwait(false);
        if (pausedBeforeReload == true && !bot.CardsFarmer.Paused) await bot.Actions.Pause(permanent: true).ConfigureAwait(false);
        if (pausedBeforeReload == false) ResumeFarmerAfterReload[bot] = 0;
        if (options?.Enabled != true) {
            bool paused = pausedBeforeReload ?? bot.CardsFarmer.Paused;
            NativeReloadState.Mark(configPath, paused);
            try {
                if (!await NativeIdleConfigStore.RestoreAsync(configPath).ConfigureAwait(false)) NativeReloadState.Cancel(configPath);
            } catch (Exception exception) {
                NativeReloadState.Cancel(configPath);
                bot.ArchiLogger.LogGenericError($"SmartIdle 原始名单未能还原：{exception.Message}");
            }
            return;
        }
        try {
            var coordinator = new BotCoordinator(bot, options, () => Sessions.TryGetValue(bot, out var session) ? session : null);
            if (!Coordinators.TryAdd(bot, coordinator)) {
                await coordinator.DisposeAsync().ConfigureAwait(false);
                return;
            }
            coordinator.Start();
        } catch (Exception exception) {
            bot.ArchiLogger.LogGenericError($"SmartIdle 未启用：{exception.Message}");
        }
    }

    public async Task OnBotDestroy(Bot bot) {
        if (Coordinators.TryRemove(bot, out var coordinator)) await coordinator.DisposeAsync().ConfigureAwait(false);
        if (Subscriptions.TryRemove(bot, out var subscription)) subscription.Dispose();
        Sessions.TryRemove(bot, out _);
        ResumeFarmerAfterReload.TryRemove(bot, out _);
    }

    public Task OnBotLoggedOn(Bot bot) {
        Get(bot)?.OnConnected();
        if (ResumeFarmerAfterReload.TryRemove(bot, out _)) {
            var coordinator = Get(bot);
            long previousStamp = coordinator?.FarmerEventStamp ?? 0;
            var result = bot.Actions.Resume();
            if (result.Success) coordinator?.OnFarmerResumeRequested(previousStamp);
        }
        return Task.CompletedTask;
    }
    public Task OnBotDisconnected(Bot bot, EResult reason) { Get(bot)?.OnDisconnected(); return Task.CompletedTask; }
    public Task OnBotFarmingStarted(Bot bot) { Get(bot)?.OnFarmerChanged(started: true); return Task.CompletedTask; }
    public Task OnBotFarmingStopped(Bot bot) { Get(bot)?.OnFarmerChanged(started: false); return Task.CompletedTask; }
    public Task OnBotFarmingFinished(Bot bot, bool farmedSomething) { Get(bot)?.OnFarmerChanged(started: false); return Task.CompletedTask; }

    public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot) {
        var session = Sessions.GetOrAdd(bot, _ => new SteamSession(accountId => Get(bot)?.OnLibraryLockChanged(accountId)));
        return Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>([session]);
    }

    public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
        if (Subscriptions.TryRemove(bot, out var previous)) previous.Dispose();
        Subscriptions[bot] = callbackManager.Subscribe<SteamUser.PlayingSessionStateCallback>(callback => Get(bot)?.OnPlayingBlocked(callback.PlayingBlocked));
        return Task.CompletedTask;
    }

    public Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
        if (args.Length == 0 || !args[0].Equals("SIDLE", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<string?>(null);
        Bot target = bot;
        string[] routedArgs = args;
        if (args.Length > 1 && !CommandNames.Contains(args[1])) {
            var selected = Bot.GetBot(args[1]);
            if (selected is null) return Task.FromResult<string?>("SmartIdle：未知命令或机器人名称，请使用 SIDLE HELP。");
            target = selected;
            routedArgs = new string[args.Length - 1];
            routedArgs[0] = args[0];
            Array.Copy(args, 2, routedArgs, 1, args.Length - 2);
        }
        EAccess effectiveAccess = steamID == 0 ? access : target.GetAccess(steamID);
        if (effectiveAccess < EAccess.Master) return Task.FromResult<string?>("SmartIdle：需要目标机器人的 Master 或 Owner 权限。");
        var coordinator = Get(target);
        return coordinator is null ? Task.FromResult<string?>("SmartIdle：此机器人未启用插件，请检查配置和日志。") : coordinator.CommandAsync(routedArgs);
    }

    private static readonly HashSet<string> CommandNames = new(StringComparer.OrdinalIgnoreCase) {
        "STATUS", "SCAN", "LIST", "WHY", "PREVIEW", "TRACE", "APPROVE", "REJECT", "PIN", "UNPIN", "EXCLUDE", "UNEXCLUDE", "PAUSE", "RESUME", "ROTATE", "RESTORE", "HELP"
    };

    private BotCoordinator? Get(Bot bot) => Coordinators.TryGetValue(bot, out var coordinator) ? coordinator : null;

}
