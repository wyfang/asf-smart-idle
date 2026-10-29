using System.Text.Json.Serialization;

namespace SmartIdle.Core;

public sealed record OwnedGame(
    [property: JsonRequired] uint AppId,
    [property: JsonRequired] string Name,
    [property: JsonRequired] uint PlaytimeMinutes
);

public sealed class IdleState {
    public const int CurrentSchemaVersion = 1;
    [JsonRequired] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonRequired] public bool Initialized { get; set; }
    [JsonRequired] public bool PinsInitialized { get; set; }
    [JsonRequired] public bool Paused { get; set; }
    [JsonRequired] public Dictionary<uint, OwnedGame> Library { get; set; } = [];
    [JsonRequired] public HashSet<uint> Approved { get; set; } = [];
    [JsonRequired] public HashSet<uint> Pending { get; set; } = [];
    [JsonRequired] public HashSet<uint> Rejected { get; set; } = [];
    [JsonRequired] public HashSet<uint> Excluded { get; set; } = [];
    [JsonRequired] public List<uint> PinnedGames { get; set; } = [];
    [JsonRequired] public List<uint> LastConfiguredPins { get; set; } = [];
    [JsonRequired] public List<uint> Queue { get; set; } = [];
    [JsonRequired] public List<uint> CurrentRotating { get; set; } = [];
    [JsonRequired] public double RemainingRotationSeconds { get; set; }
    // Optional additions retain compatibility with version 0.1's schema-1 files.
    public Dictionary<uint, double> EstimatedPlaytimeMinutes { get; set; } = [];
    public string? LastSelectionMode { get; set; }
    public bool RestoreOriginalOnIdle { get; set; }
    // Optional scheduling additions keep existing schema-1 state files readable.
    public Dictionary<uint, int> ManualWeights { get; set; } = [];
    public Dictionary<uint, double> PluginAllocatedMinutes { get; set; } = [];
    public Dictionary<uint, int> QualityAutomaticScores { get; set; } = [];
    public Dictionary<uint, double> QualityTargetShares { get; set; } = [];
    public Dictionary<uint, double> QualityDeficitSeconds { get; set; } = [];

    public void Validate() {
        if (SchemaVersion != CurrentSchemaVersion) {
            throw new InvalidDataException($"不支持的 SmartIdle 状态版本：{SchemaVersion}。");
        }
        if (Library is null || Approved is null || Pending is null || Rejected is null || Excluded is null ||
            PinnedGames is null || LastConfiguredPins is null || Queue is null || CurrentRotating is null || EstimatedPlaytimeMinutes is null ||
            ManualWeights is null || PluginAllocatedMinutes is null || QualityAutomaticScores is null || QualityTargetShares is null || QualityDeficitSeconds is null) {
            throw new InvalidDataException("SmartIdle 状态包含 null 清单。");
        }
        if (!double.IsFinite(RemainingRotationSeconds) || RemainingRotationSeconds < 0) {
            throw new InvalidDataException("SmartIdle 剩余轮换时间无效。");
        }
        if (LastSelectionMode is not null && !IdleOptions.IsValidSelectionMode(LastSelectionMode)) {
            throw new InvalidDataException("SmartIdle 保存的轮换模式无效。");
        }
        if (RestoreOriginalOnIdle && !Paused) throw new InvalidDataException("还原原始名单的请求必须暂停插件轮换。");
        foreach (var pair in EstimatedPlaytimeMinutes) {
            if (pair.Key == 0 || !double.IsFinite(pair.Value) || pair.Value < 0) {
                throw new InvalidDataException("SmartIdle 保存的估算时长无效。");
            }
        }
        foreach (var scores in new[] { ManualWeights, QualityAutomaticScores }) {
            foreach (var pair in scores) {
                if (pair.Key == 0 || pair.Value is < 1 or > 100) throw new InvalidDataException("SmartIdle 保存的游戏权重无效。");
            }
        }
        foreach (var pair in PluginAllocatedMinutes) {
            if (pair.Key == 0 || !double.IsFinite(pair.Value) || pair.Value < 0) throw new InvalidDataException("SmartIdle 保存的插件分配时长无效。");
        }
        foreach (var pair in QualityTargetShares) {
            if (pair.Key == 0 || !double.IsFinite(pair.Value) || pair.Value is <= 0 or > 1) throw new InvalidDataException("SmartIdle 保存的加权目标份额无效。");
        }
        foreach (var pair in QualityDeficitSeconds) {
            if (pair.Key == 0 || !double.IsFinite(pair.Value)) throw new InvalidDataException("SmartIdle 保存的加权服务欠额无效。");
        }
        foreach (var pair in Library) {
            uint id = pair.Key;
            OwnedGame game = pair.Value;
            if (id == 0 || game is null || game.AppId != id || game.Name is null) {
                throw new InvalidDataException("SmartIdle 游戏库记录无效。");
            }
        }
        IEnumerable<uint>[] collections = [Approved, Pending, Rejected, Excluded, PinnedGames, LastConfiguredPins, Queue, CurrentRotating];
        foreach (var ids in collections) {
            foreach (uint id in ids) {
                if (id == 0) throw new InvalidDataException("SmartIdle 状态不能包含 AppID 0。");
            }
        }
        List<uint>[] lists = [PinnedGames, LastConfiguredPins, Queue, CurrentRotating];
        foreach (var ids in lists) {
            var seen = new HashSet<uint>();
            foreach (uint id in ids) {
                if (!seen.Add(id)) throw new InvalidDataException("SmartIdle 状态包含重复队列项。");
            }
        }
        if (Approved.Overlaps(Pending) || Approved.Overlaps(Rejected) || Pending.Overlaps(Rejected) ||
            Excluded.Overlaps(Approved) || Excluded.Overlaps(Pending) || Excluded.Overlaps(Rejected)) {
            throw new InvalidDataException("SmartIdle 游戏决定存在冲突。");
        }
        if (PinnedGames.Count > 32 || LastConfiguredPins.Count > 32 || CurrentRotating.Count > 32) {
            throw new InvalidDataException("SmartIdle 常驻或当前队列状态无效。");
        }
        foreach (uint id in CurrentRotating) {
            if (!Queue.Contains(id) || PinnedGames.Contains(id)) {
                throw new InvalidDataException("SmartIdle 常驻或当前队列状态无效。");
            }
        }
    }
}
