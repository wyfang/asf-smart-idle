using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartIdle;

var tests = new (string Name, Func<Task> Run)[] {
    ("官方 response.query_summary 协议、全语言购买口径、无需密钥", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            using var http = Client(request => {
                Equal("api.steampowered.com", request.RequestUri!.Host);
                True(request.RequestUri.AbsolutePath.EndsWith("/IUserReviewsService/GetAppReviews/v1/", StringComparison.Ordinal));
                True(!request.RequestUri.Query.Contains("key=", StringComparison.Ordinal));
                string query = Uri.UnescapeDataString(request.RequestUri.Query[12..]); // ?input_json=
                using var json = JsonDocument.Parse(query);
                var input = json.RootElement;
                Equal(620u, input.GetProperty("appid").GetUInt32());
                Equal("all", input.GetProperty("languages")[0].GetString());
                Equal(0, input.GetProperty("purchase_type").GetInt32());
                Equal(0, input.GetProperty("review_type").GetInt32());
                Equal(0, input.GetProperty("day_range").GetInt32());
                return Task.FromResult(Success(90, 100));
            });
            var service = Create(directory, http, clock);
            var result = await service.RefreshAsync([620], 168, false, default);
            Equal(90, result[620].PositiveReviews);
            Equal(100, result[620].TotalReviews);
            Equal(clock.GetUtcNow(), result[620].FetchedAt);
        });
    }),
    ("缓存落盘原子替换并可重读，缓存命中不查询，force仍保留最短间隔", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            int calls = 0;
            using var http = Client(_ => { calls++; return Task.FromResult(Success(80, 100)); });
            var service = Create(directory, http, clock);
            await service.RefreshAsync([620], 168, false, default);
            string path = CachePath(directory);
            True(File.Exists(path));
            Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
            await service.RefreshAsync([620], 168, true, default);
            Equal(1, calls);
            var reloaded = Create(directory, http, clock);
            Equal(80, (await reloaded.RefreshAsync([620], 168, false, default))[620].PositiveReviews);
            Equal(1, calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await service.RefreshAsync([620], 168, true, default);
            Equal(2, calls);
        });
    }),
    ("HTTP失败保留旧评分且force无法绕过指数退避", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            int calls = 0;
            using var http = Client(_ => Task.FromResult(++calls == 1 ? Success(80, 100) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            var service = Create(directory, http, clock);
            var before = (await service.RefreshAsync([620], 168, false, default))[620];
            clock.Advance(TimeSpan.FromDays(8));
            var after = (await service.RefreshAsync([620], 168, false, default))[620];
            Equal(before, after);
            Equal(2, calls);
            await service.RefreshAsync([620], 168, true, default);
            Equal(2, calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await service.RefreshAsync([620], 168, true, default);
            Equal(3, calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await service.RefreshAsync([620], 168, true, default);
            Equal(3, calls);
            True(service.Status.Contains("保留旧评分", StringComparison.Ordinal));
        });
    }),
    ("429 Retry-After在其他App与force间共享退避", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            int calls = 0;
            using var http = Client(_ => {
                calls++;
                if (calls > 1) return Task.FromResult(Success(90, 100));
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddMinutes(15));
                return Task.FromResult(response);
            });
            var service = Create(directory, http, clock);
            Equal(0, (await service.RefreshAsync([620, 570], 168, true, default)).Count);
            Equal(1, calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await service.RefreshAsync([620, 570], 168, true, default);
            Equal(1, calls);
            clock.Advance(TimeSpan.FromMinutes(11));
            Equal(2, (await service.RefreshAsync([620, 570], 168, true, default)).Count);
            Equal(3, calls);
        });
    }),
    ("并发App查询去重，单调用取消不终止共享请求", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using var http = Client(_ => { calls++; entered.TrySetResult(); return completed.Task; });
            var service = Create(directory, http, clock);
            using var cancellation = new CancellationTokenSource();
            var first = service.RefreshAsync([620], 168, false, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.RefreshAsync([620], 168, false, default);
            await cancellation.CancelAsync();
            await ThrowsAsync<OperationCanceledException>(async () => await first);
            completed.SetResult(Success(75, 100));
            Equal(75, (await second)[620].PositiveReviews);
            Equal(1, calls);
        });
    }),
    ("大库扫描未完成时可读取已完成的部分评分", async () => {
        await WithDirectory(async directory => {
            var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            using var http = Client(_ => {
                if (++calls == 1) return Task.FromResult(Success(75, 100));
                secondEntered.TrySetResult();
                return completed.Task;
            });
            var service = Create(directory, http, new TestClock());
            var scan = service.RefreshAsync([570, 620], 168, false, default);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, service.Snapshot([570, 620]).Count);
            True(!scan.IsCompleted);
            completed.SetResult(Success(80, 100));
            Equal(2, (await scan).Count);
        });
    }),
    ("启动损坏缓存原样保留且可用内存评分继续", async () => {
        await WithDirectory(async directory => {
            string path = CachePath(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string corrupt = "{broken";
            await File.WriteAllTextAsync(path, corrupt);
            using var http = Client(_ => Task.FromResult(Success(75, 100)));
            var service = Create(directory, http, new TestClock());
            Equal(75, (await service.RefreshAsync([620], 168, false, default))[620].PositiveReviews);
            Equal(corrupt, await File.ReadAllTextAsync(path));
            True(service.Status.Contains("仅使用内存", StringComparison.Ordinal));
        });
    }),
    ("外部损坏缓存不会被后续查询覆盖", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            using var http = Client(_ => Task.FromResult(Success(75, 100)));
            var service = Create(directory, http, clock);
            await service.RefreshAsync([620], 168, false, default);
            await File.WriteAllTextAsync(CachePath(directory), "external-corruption");
            await service.RefreshAsync([570], 168, false, default);
            Equal("external-corruption", await File.ReadAllTextAsync(CachePath(directory)));
            True(service.Status.Contains("仅使用内存", StringComparison.Ordinal));
        });
    }),
    ("无效响应不污染缓存，无数据保持缺失", async () => {
        await WithDirectory(async directory => {
            var clock = new TestClock();
            int calls = 0;
            using var http = Client(_ => {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"response\":{\"query_summary\":{\"total_positive\":100,\"total_negative\":20,\"total_reviews\":50}}}")
                });
            });
            var service = Create(directory, http, clock);
            Equal(0, (await service.RefreshAsync([620], 168, false, default)).Count);
            Equal(1, calls);
            True(!File.Exists(CachePath(directory)));
            await service.RefreshAsync([620], 168, true, default);
            Equal(1, calls);
        });
    }),
    ("评分数值和空评论校验", () => {
        var now = DateTimeOffset.UtcNow;
        Equal(0, GameRatingService.ParseResponse(Encoding.UTF8.GetBytes(Payload(0, 0)), now).TotalReviews);
        Throws(() => GameRatingService.ParseResponse(Encoding.UTF8.GetBytes(Payload(-1, 20)), now));
        Throws(() => GameRatingService.ParseResponse(Encoding.UTF8.GetBytes("{\"success\":1,\"query_summary\":{}}"), now));
        return Task.CompletedTask;
    }),
    ("同一配置目录工厂返回同一服务", () => {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        True(ReferenceEquals(GameRatingService.ForDirectory(path), GameRatingService.ForDirectory(Path.Combine(path, "."))));
        return Task.CompletedTask;
    })
};
int failed = 0;
foreach (var test in tests) {
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"评分服务测试：{tests.Length - failed} 通过，{failed} 失败。");
return failed == 0 ? 0 : 1;

static GameRatingService Create(string directory, HttpClient http, TestClock clock) => new(directory, http, clock.GetUtcNow,
    new GameRatingService.RequestLimiter(clock.GetUtcNow, delay => { clock.Advance(delay); return Task.CompletedTask; }, TimeSpan.Zero));
static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => new(new Handler(handler)) { Timeout = TimeSpan.FromSeconds(5) };
static string CachePath(string directory) => Path.Combine(directory, "SmartIdle-ratings", "ratings.json");
static string Payload(int positive, int total) => $"{{\"response\":{{\"query_summary\":{{\"total_positive\":{positive},\"total_negative\":{total - positive},\"total_reviews\":{total}}}}}}}";
static HttpResponseMessage Success(int positive, int total) => new(HttpStatusCode.OK) { Content = new StringContent(Payload(positive, total)) };
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, actual {actual}"); }
static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected exception"); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task WithDirectory(Func<string, Task> action) {
    string directory = Path.Combine(Path.GetTempPath(), "SmartIdle-rating-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { await action(directory); } finally { Directory.Delete(directory, recursive: true); }
}
sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}
sealed class TestClock {
    private DateTimeOffset Now = DateTimeOffset.UtcNow.AddDays(-30);
    public DateTimeOffset GetUtcNow() => Now;
    internal void Advance(TimeSpan interval) => Now = Now.Add(interval);
}
