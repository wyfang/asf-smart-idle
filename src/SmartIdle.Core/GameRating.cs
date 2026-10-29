using System.Text.Json.Serialization;

namespace SmartIdle.Core;

/// <summary>A cached Steam review summary, independent of any bot or library snapshot.</summary>
public sealed record GameRating([property: JsonRequired] int PositiveReviews, [property: JsonRequired] int TotalReviews, [property: JsonRequired] DateTimeOffset FetchedAt) {
    public void Validate() {
        if (PositiveReviews < 0 || TotalReviews < 0 || PositiveReviews > TotalReviews) {
            throw new ArgumentException("游戏评价数量无效。");
        }
        if (FetchedAt == default || FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5)) {
            throw new ArgumentException("游戏评价缓存时间无效。");
        }
    }

    public int Score(int reputationWeightPercent) {
        Validate();
        if (reputationWeightPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(reputationWeightPercent));
        if (TotalReviews == 0) return 50;
        double reputation = 100D * (PositiveReviews + 75D) / (TotalReviews + 100D);
        double popularity = Math.Min(100D, 100D * Math.Log(1D + TotalReviews) / Math.Log(100001D));
        double combined = (reputation * reputationWeightPercent + popularity * (100 - reputationWeightPercent)) / 100D;
        // Scores are non-negative. Adding 0.5 before truncation gives the same
        // rounding without the Math.Round overload removed by ASF's trimmed host.
        return Math.Max(1, Math.Min(100, (int) (combined + 0.5D)));
    }
}
