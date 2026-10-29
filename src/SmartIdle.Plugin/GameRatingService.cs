using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using SmartIdle.Core;

namespace SmartIdle;

/// <summary>Anonymous Steam review summaries, shared by every bot in one ASF configuration directory.</summary>
internal sealed class GameRatingService {
    private static readonly ConcurrentDictionary<string, GameRatingService> Services = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly RequestLimiter SharedLimiter = new(() => DateTimeOffset.UtcNow,
        delay => Task.Delay(delay), TimeSpan.FromSeconds(2));
    private readonly object Sync = new();
    private readonly SemaphoreSlim DiskGate = new(1, 1);
    private readonly string CachePath;
    private readonly HttpClient Http;
    private readonly Func<DateTimeOffset> Clock;
    private readonly RequestLimiter Limiter;
    private readonly Dictionary<uint, GameRating> Ratings = [];
    private readonly Dictionary<uint, Task<GameRating?>> InFlight = [];
    private readonly Dictionary<uint, RetryState> Retries = [];
    private Task? LoadTask;
    private bool CanPersist = true;
    private string? Warning;
    private string? CacheWarning;
    private int Requests;
    internal string? DiagnosticFailure { get; private set; }

    internal static GameRatingService ForDirectory(string configDirectory) {
        string directory = Path.GetFullPath(configDirectory);
        return Services.GetOrAdd(directory, path => new GameRatingService(path, Client, () => DateTimeOffset.UtcNow, SharedLimiter));
    }

    // Independent limiter and clock make retries, cancellation and cache persistence testable without Steam.
    internal GameRatingService(string configDirectory, HttpClient http, Func<DateTimeOffset> clock, RequestLimiter limiter) {
        CachePath = Path.Combine(Path.GetFullPath(configDirectory), "SmartIdle-ratings", "ratings.json");
        Http = http;
        Clock = clock;
        Limiter = limiter;
    }

    internal string Status {
        get {
            lock (Sync) return $"评分缓存 {Ratings.Count} 款，查询中 {InFlight.Count}，网络请求 {Requests} 次" +
                (Warning is null ? "" : $"；{Warning}") + (CacheWarning is null ? "。" : $"；{CacheWarning}");
        }
    }

    internal async Task<IReadOnlyDictionary<uint, GameRating>> RefreshAsync(IEnumerable<uint> appIds, int refreshHours,
        bool force, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(appIds);
        if (refreshHours is < 1 or > 8760) throw new ArgumentOutOfRangeException(nameof(refreshHours));
        var seen = new HashSet<uint>();
        var ids = new List<uint>();
        foreach (uint id in appIds) if (id != 0 && seen.Add(id)) ids.Add(id);
        ids.Sort();
        Task load;
        lock (Sync) load = LoadTask ??= Task.Run(LoadAsync);
        await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (uint id in ids) {
            cancellationToken.ThrowIfCancellationRequested();
            Task<GameRating?>? request = null;
            lock (Sync) {
                DateTimeOffset now = Clock();
                bool fresh = Ratings.TryGetValue(id, out GameRating? rating) &&
                    rating.FetchedAt > now.AddHours(-refreshHours);
                if ((!fresh || force) && (!Retries.TryGetValue(id, out var retry) || now >= retry.NextAttempt)) {
                    if (!InFlight.TryGetValue(id, out request)) {
                        // Run on another thread while holding Sync, so the task's
                        // completion cannot remove its entry before registration.
                        request = Task.Run(() => FetchAndCacheAsync(id));
                        InFlight.Add(id, request);
                    }
                }
            }
            // Cancelling one bot does not cancel another bot's shared HTTP request. The request is bounded
            // by HttpClient.Timeout, catches every operational failure, and always observes its completion.
            if (request is not null) await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return Snapshot(ids);
    }

    // Allows the scheduler to use completed summaries while a large first scan is still running.
    // This only reads memory; RefreshAsync starts the lazy disk load and background requests.
    internal IReadOnlyDictionary<uint, GameRating> Snapshot(IEnumerable<uint> appIds) {
        lock (Sync) {
            var snapshot = new Dictionary<uint, GameRating>();
            foreach (uint id in appIds) if (Ratings.TryGetValue(id, out var rating)) snapshot.TryAdd(id, rating);
            return snapshot;
        }
    }

    private async Task LoadAsync() {
        try {
            var info = new FileInfo(CachePath);
            if (!info.Exists) return;
            if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("评分缓存超过 16 MiB");
            var loaded = ReadCache(await File.ReadAllTextAsync(CachePath).ConfigureAwait(false));
            lock (Sync) foreach (var pair in loaded) Ratings[pair.Key] = pair.Value;
        } catch (Exception exception) {
            DisablePersistence(exception);
        }
    }

    private async Task<GameRating?> FetchAndCacheAsync(uint appId) {
        try {
            if (!await Limiter.EnterAsync().ConfigureAwait(false)) {
                RecordFailure(appId, "Steam 评分查询处于限流退避中", Limiter.BlockedUntil);
                return null;
            }
            GameRating rating;
            try {
                string input = JsonSerializer.Serialize(new {
                    appid = appId, languages = new[] { "all" }, review_type = 0, purchase_type = 0, day_range = 0, num_per_page = 1
                }, IdleJson.Options);
                var uri = new Uri("https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?input_json=" + Uri.EscapeDataString(input));
                lock (Sync) Requests++;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                    // The trimmed ASF host does not retain the typed RetryAfter accessor.
                    DateTimeOffset retryAt = Clock().AddMinutes(5);
                    if (response.Headers.TryGetValues("Retry-After", out var retryHeaders)) {
                        foreach (string value in retryHeaders) {
                            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)) {
                                retryAt = Clock().AddSeconds(seconds);
                                break;
                            }
                            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) {
                                retryAt = date;
                                break;
                            }
                        }
                    }
                    retryAt = retryAt < Clock().AddMinutes(5) ? Clock().AddMinutes(5) : retryAt;
                    Limiter.BlockUntil(retryAt);
                    RecordFailure(appId, "Steam 返回 HTTP 429，已延后评分查询", retryAt);
                    return null;
                }
                response.EnsureSuccessStatusCode();
                // Summary responses should be tiny. Bound reads even if the upstream returns an unexpected payload.
                await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0) {
                    if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Steam 评分响应过大");
                    buffer.Write(chunk, 0, count);
                }
                rating = ParseResponse(buffer.ToArray(), Clock());
            } finally { Limiter.Exit(); }
            lock (Sync) {
                Ratings[appId] = rating;
                Warning = null;
                // Force may bypass the cache age, but never this minimum interval or a failure backoff.
                Retries[appId] = new RetryState(0, Clock().AddMinutes(5));
            }
            await SaveAsync().ConfigureAwait(false);
            return rating;
        } catch (Exception exception) {
            lock (Sync) DiagnosticFailure = exception.ToString();
            RecordFailure(appId, $"评分查询失败（{exception.GetType().Name}），保留旧评分");
            return null;
        } finally {
            lock (Sync) InFlight.Remove(appId);
        }
    }

    private void RecordFailure(uint appId, string warning, DateTimeOffset? notBefore = null) {
        lock (Sync) {
            int failures = Retries.TryGetValue(appId, out var previous) ? Math.Min(previous.Failures + 1, 8) : 1;
            DateTimeOffset next = Clock().AddMinutes(Math.Min(360, 5 * Math.Pow(2, failures - 1)));
            if (notBefore > next) next = notBefore.Value;
            Retries[appId] = new RetryState(failures, next);
            Warning = warning;
        }
    }

    private async Task SaveAsync() {
        await DiskGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try {
            lock (Sync) if (!CanPersist) return;
            // Recheck existing bytes before replacement so an externally damaged cache is preserved too.
            if (File.Exists(CachePath)) {
                var info = new FileInfo(CachePath);
                if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("评分缓存超过 16 MiB");
                var existing = ReadCache(await File.ReadAllTextAsync(CachePath).ConfigureAwait(false));
                lock (Sync) foreach (var pair in existing) {
                    if (!Ratings.TryGetValue(pair.Key, out var current) || current.FetchedAt < pair.Value.FetchedAt) Ratings[pair.Key] = pair.Value;
                }
            }
            KeyValuePair<uint, GameRating>[] snapshot;
            lock (Sync) {
                var ordered = new List<KeyValuePair<uint, GameRating>>(Ratings.Count);
                foreach (var pair in Ratings) ordered.Add(pair);
                ordered.Sort(static (left, right) => left.Key.CompareTo(right.Key));
                snapshot = ordered.ToArray();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous)) {
                var ordered = new Dictionary<uint, GameRating>(snapshot.Length);
                foreach (var pair in snapshot) ordered.Add(pair.Key, pair.Value);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                    SchemaVersion = 1, Ratings = ordered
                }, IdleJson.Options));
                await file.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, CachePath, overwrite: true);
            temporary = null;
        } catch (Exception exception) {
            DisablePersistence(exception);
        } finally {
            if (temporary is not null) try { File.Delete(temporary); } catch (Exception) { }
            DiskGate.Release();
        }
    }

    private void DisablePersistence(Exception exception) {
        lock (Sync) {
            CanPersist = false;
            CacheWarning = $"评分缓存不可用（{exception.GetType().Name}），保留原文件并仅使用内存；挂机继续";
        }
    }

    internal static GameRating ParseResponse(byte[] bytes, DateTimeOffset fetchedAt) {
        using var document = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var summary = Field(Field(document.RootElement, "response"), "query_summary");
        int positive = Field(summary, "total_positive").GetInt32();
        int negative = Field(summary, "total_negative").GetInt32();
        int total = Field(summary, "total_reviews").GetInt32();
        if (negative < 0 || (long) positive + negative != total) throw new InvalidDataException("Steam 评价总数不一致");
        var rating = new GameRating(positive, total, fetchedAt);
        rating.Validate();
        return rating;
    }

    private static Dictionary<uint, GameRating> ReadCache(string text) {
        using var document = JsonDocument.Parse(text);
        if (Field(document.RootElement, "SchemaVersion").GetInt32() != 1) throw new InvalidDataException("评分缓存版本不兼容");
        var result = new Dictionary<uint, GameRating>();
        foreach (var property in Field(document.RootElement, "Ratings").EnumerateObject()) {
            if (!uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0) throw new InvalidDataException("评分缓存 AppID 无效");
            var value = property.Value;
            var rating = JsonSerializer.Deserialize<GameRating>(value.ToString(), IdleJson.Options)
                ?? throw new InvalidDataException("评分缓存记录为空");
            rating.Validate();
            if (!result.TryAdd(id, rating)) throw new InvalidDataException("评分缓存 AppID 重复");
        }
        return result;
    }

    // ASF trims JsonElement.GetProperty(string); enumeration is retained by its own JSON handling.
    private static JsonElement Field(JsonElement element, string name) {
        foreach (var property in element.EnumerateObject()) if (property.Name == name) return property.Value;
        throw new InvalidDataException("评分数据缺少字段：" + name);
    }

    private sealed record RetryState(int Failures, DateTimeOffset NextAttempt);

    internal sealed class RequestLimiter(Func<DateTimeOffset> clock, Func<TimeSpan, Task> delay, TimeSpan interval) {
        private readonly SemaphoreSlim Gate = new(1, 1);
        private readonly object Sync = new();
        private DateTimeOffset NextRequest;
        private DateTimeOffset Blocked;
        internal DateTimeOffset BlockedUntil { get { lock (Sync) return Blocked; } }
        internal void BlockUntil(DateTimeOffset time) { lock (Sync) if (time > Blocked) Blocked = time; }
        internal async Task<bool> EnterAsync() {
            await Gate.WaitAsync().ConfigureAwait(false);
            try {
                DateTimeOffset now = clock();
                if (now < BlockedUntil) { Gate.Release(); return false; }
                TimeSpan wait = NextRequest - now;
                if (wait > TimeSpan.Zero) await delay(wait).ConfigureAwait(false);
                NextRequest = clock().Add(interval);
                return true;
            } catch { Gate.Release(); throw; }
        }
        internal void Exit() => Gate.Release();
    }
}
