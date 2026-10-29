namespace SmartIdle.Core;

public sealed record IdleOptions {
    public bool Enabled { get; set; }
    public int ConcurrentGames { get; set; } = 32;
    public int RotationMinutes { get; set; } = 120;
    public int RefreshHours { get; set; }
    public string SelectionMode { get; set; } = "LeastPlaytime";
    public int ReputationWeightPercent { get; set; } = 80;
    public double WeightStrength { get; set; } = 1;
    public int RatingRefreshHours { get; set; } = 168;
    public uint[] PinnedGames { get; set; } = [];
    public uint[] Blacklist { get; set; } = [];

    public void Validate() {
        if (ConcurrentGames is < 1 or > 32) {
            throw new ArgumentException("ConcurrentGames 必须在 1～32 之间。");
        }
        if (RotationMinutes is < 5 or > 525600) {
            throw new ArgumentException("RotationMinutes 必须在 5～525600 之间。");
        }
        if (RefreshHours is < 0 or > 8760) {
            throw new ArgumentException("RefreshHours 必须在 0～8760 之间；0 表示只在首次或手动请求时读取。");
        }
        if (!IsValidSelectionMode(SelectionMode)) {
            throw new ArgumentException("SelectionMode 必须为 LeastPlaytime、Random、RoundRobin 或 QualityWeighted。");
        }
        if (ReputationWeightPercent is < 0 or > 100) {
            throw new ArgumentException("ReputationWeightPercent 必须在 0～100 之间。");
        }
        if (!double.IsFinite(WeightStrength) || WeightStrength is < 0.25 or > 4) {
            throw new ArgumentException("WeightStrength 必须为 0.25～4 之间的有限数字。");
        }
        if (RatingRefreshHours is < 1 or > 8760) {
            throw new ArgumentException("RatingRefreshHours 必须在 1～8760 之间。");
        }
        if (PinnedGames is null || Blacklist is null) {
            throw new ArgumentException("PinnedGames 和 Blacklist 必须是非空引用，且不能包含 AppID 0。");
        }
        var pins = new HashSet<uint>();
        var blacklist = new HashSet<uint>();
        bool duplicate = false;
        foreach (uint id in PinnedGames) {
            if (id == 0) throw new ArgumentException("PinnedGames 和 Blacklist 必须是非空引用，且不能包含 AppID 0。");
            if (!pins.Add(id)) duplicate = true;
        }
        foreach (uint id in Blacklist) {
            if (id == 0) throw new ArgumentException("PinnedGames 和 Blacklist 必须是非空引用，且不能包含 AppID 0。");
            if (!blacklist.Add(id)) duplicate = true;
        }
        if (duplicate) {
            throw new ArgumentException("PinnedGames 和 Blacklist 不能包含重复 AppID。");
        }
        if (PinnedGames.Length > ConcurrentGames) {
            throw new ArgumentException("常驻游戏数量不能超过 ConcurrentGames。");
        }
        foreach (uint id in pins) {
            if (blacklist.Contains(id)) {
                throw new ArgumentException("同一游戏不能同时在 PinnedGames 和 Blacklist 中。");
            }
        }
    }

    internal static bool IsValidSelectionMode(string? mode) => mode is "LeastPlaytime" or "Random" or "RoundRobin" or "QualityWeighted";
}
