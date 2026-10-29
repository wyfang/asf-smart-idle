using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartIdle.Core;

int passed = 0;
int failed = 0;
var tests = new (string Name, Func<Task> Run)[] {
    ("评价分使用样本收缩、对数人气、整数范围与无样本中性回退", () => Sync(() => {
        var now = DateTimeOffset.UtcNow;
        Equal(74, new GameRating(90, 100, now).Score(80));
        Equal(83, new GameRating(90, 100, now).Score(100));
        Equal(100, new GameRating(1, 100000, now).Score(0));
        Equal(100, new GameRating(1, int.MaxValue, now).Score(0));
        Equal(1, new GameRating(0, 100000, now).Score(100));
        Equal(50, new GameRating(0, 0, now).Score(100));
        // A single positive review is held near the prior, not treated as 100%.
        Equal(75, new GameRating(1, 1, now).Score(100));
        new GameRating(0, 0, now.AddYears(-2)).Validate();
        Throws<ArgumentException>(() => new GameRating(-1, 1, now).Validate());
        Throws<ArgumentException>(() => new GameRating(2, 1, now).Validate());
        Throws<ArgumentException>(() => new GameRating(0, -1, now).Validate());
        Throws<ArgumentException>(() => new GameRating(1, 1, default).Validate());
        Throws<ArgumentException>(() => new GameRating(1, 1, now.AddDays(1)).Validate());
        Throws<ArgumentOutOfRangeException>(() => new GameRating(1, 1, now).Score(101));
    })),
    ("单槽有效服务长期接近90比60比30，不受Steam旧时长影响", () => Sync(() => {
        var planner = Weighted(1, (1, 90), (2, 60), (3, 30));
        planner.RefreshLibrary([Game(1, 1000000), Game(2, 1), Game(3, 100)]);
        RunWeighted(planner, 600);
        Near(300 * 120D, planner.State.PluginAllocatedMinutes[1], 240);
        Near(200 * 120D, planner.State.PluginAllocatedMinutes[2], 240);
        Near(100 * 120D, planner.State.PluginAllocatedMinutes[3], 240);
        Near(0.5, planner.State.QualityTargetShares[1]);
        Near(1D / 3, planner.State.QualityTargetShares[2]);
        Near(1D / 6, planner.State.QualityTargetShares[3]);
    })),
    ("多槽加权按每款最多占一槽封顶，常驻独立且低权重不被挤没", () => Sync(() => {
        var planner = Weighted(3, (1, 90), (2, 60), (3, 30));
        planner.Pin([99]);
        RunWeighted(planner, 600);
        Near(600 * 120D, planner.State.PluginAllocatedMinutes[1], 240);
        Near(400 * 120D, planner.State.PluginAllocatedMinutes[2], 240);
        Near(200 * 120D, planner.State.PluginAllocatedMinutes[3], 240);
        Near(600 * 120D, planner.State.PluginAllocatedMinutes[99]);
        Near(1, planner.State.QualityTargetShares[1]);
        Near(2D / 3, planner.State.QualityTargetShares[2]);
        Near(1D / 3, planner.State.QualityTargetShares[3]);
        True(!planner.State.QualityTargetShares.ContainsKey(99));
        planner.ValidateState();
    })),
    ("极端权重保持有限正份额，多槽饱和后低分共享剩余位置", () => Sync(() => {
        var planner = Weighted(2, (1, 100), (2, 1), (3, 1));
        planner = new IdlePlanner(planner.Options with { WeightStrength = 4 }, planner.State);
        RunWeighted(planner, 200);
        Near(1, planner.State.QualityTargetShares[1]);
        Near(0.5, planner.State.QualityTargetShares[2]);
        Near(0.5, planner.State.QualityTargetShares[3]);
        Near(200 * 120D, planner.State.PluginAllocatedMinutes[1]);
        Near(100 * 120D, planner.State.PluginAllocatedMinutes[2], 240);
        Near(100 * 120D, planner.State.PluginAllocatedMinutes[3], 240);
        var single = Weighted(1, (1, 100), (2, 1));
        single = new IdlePlanner(single.Options with { WeightStrength = 4 }, single.State);
        True(single.State.QualityTargetShares[2] > 0);
        single.AccruePlaytime(single.CurrentOrSelectBatch(), 60000000D * 7200);
        Sequence([2], single.Rotate());
        single.ValidateState();
    })),
    ("预览与手动换批不虚增服务，实际暂停前的部分运行按秒计入", () => Sync(() => {
        var planner = Weighted(1, (1, 90), (2, 60), (3, 30));
        for (int iteration = 0; iteration < 50; iteration++) {
            planner.CurrentOrSelectBatch();
            planner.Rotate();
        }
        Equal(0, planner.State.PluginAllocatedMinutes.Count);
        True(planner.State.QualityDeficitSeconds.Values.All(value => value == 0));
        var batch = planner.CurrentOrSelectBatch();
        planner.AccruePlaytime(batch, 90);
        Near(1.5, planner.State.PluginAllocatedMinutes[batch[0]]);
        double debt = planner.State.QualityDeficitSeconds[batch[0]];
        planner.Rotate();
        Near(debt, planner.State.QualityDeficitSeconds[batch[0]]);
        Near(1.5, planner.State.PluginAllocatedMinutes.Values.Sum());
    })),
    ("评分及手动权重改变保持当前批，只重置公平欠额且不追讨旧时长", () => Sync(() => {
        var planner = Weighted(1, (1, 90), (2, 10));
        RunWeighted(planner, 25);
        var batch = planner.CurrentOrSelectBatch().ToArray();
        double total = planner.State.PluginAllocatedMinutes.Values.Sum();
        planner.SetWeight(2, 100);
        Sequence(batch, planner.CurrentOrSelectBatch());
        Near(total, planner.State.PluginAllocatedMinutes.Values.Sum());
        True(planner.State.QualityDeficitSeconds.Values.All(value => value == 0));
        Near(100D / 190, planner.State.QualityTargetShares[2]);
        planner.SetWeight(1, null);
        Equal(50, planner.WeightScore(1));
        Sequence(batch, planner.CurrentOrSelectBatch());
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.SetWeight(3, 80);
        True(!planner.CurrentOrSelectBatch().Contains(3U));
        planner.Approve([3]);
        Sequence(batch, planner.CurrentOrSelectBatch());
        True(planner.State.QualityDeficitSeconds.Values.All(value => value == 0));
        True(!planner.State.PluginAllocatedMinutes.ContainsKey(3));
        planner.Exclude([3]);
        True(!planner.State.QualityTargetShares.ContainsKey(3));
        Near(total, planner.State.PluginAllocatedMinutes.Values.Sum());
    })),
    ("评分未知与陈旧缓存可解释，手动覆盖和恢复自动均不绕过批准", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "QualityWeighted", ConcurrentGames = 1 });
        planner.RefreshLibrary([Game(1), Game(2)]);
        Equal(50, planner.WeightScore(1));
        True(planner.ExplainWeight(1).Contains("中性"));
        planner.SetWeight(2, 90);
        Equal(0, planner.CurrentOrSelectBatch().Count);
        planner.SetRatings(new Dictionary<uint, GameRating> { [1] = new(90, 100, DateTimeOffset.UtcNow.AddDays(-8)) });
        Equal(74, planner.WeightScore(1));
        Equal(90, planner.WeightScore(2));
        True(planner.ExplainWeight(1).Contains("FetchedAt="));
        True(planner.ExplainWeight(1).Contains("陈旧"));
        True(planner.ExplainWeight(2).Contains("手动覆盖 90"));
        planner.SetWeight(2, null);
        Equal(50, planner.WeightScore(2));
        planner.ApproveAll();
        var batch = planner.CurrentOrSelectBatch().ToArray();
        planner.SetRatings(new Dictionary<uint, GameRating> { [2] = new(1000, 1000, DateTimeOffset.UtcNow) });
        Sequence(batch, planner.CurrentOrSelectBatch());
        Equal(74, planner.WeightScore(1));
        planner.SetRatings(new Dictionary<uint, GameRating>());
        Equal(74, planner.WeightScore(1));
        Throws<ArgumentException>(() => planner.SetRatings(new Dictionary<uint, GameRating> { [1] = new(2, 1, DateTimeOffset.UtcNow) }));
        Equal(74, planner.WeightScore(1));
        planner.SetRatings(new Dictionary<uint, GameRating> { [1] = new(0, 0, DateTimeOffset.UtcNow) });
        Equal(50, planner.WeightScore(1));
    })),
    ("加权公平状态与自动分跨重启保存，缓存加载不清空未结欠额", async () => {
        await WithTemporaryDirectory(async directory => {
            var planner = Create(new IdleOptions { SelectionMode = "QualityWeighted", ConcurrentGames = 1 });
            planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
            var ratings = new Dictionary<uint, GameRating> {
                [1] = new(900, 1000, DateTimeOffset.UtcNow),
                [2] = new(50, 100, DateTimeOffset.UtcNow)
            };
            planner.SetRatings(ratings);
            planner.ApproveAll();
            RunWeighted(planner, 11);
            planner.AccruePlaytime(planner.CurrentOrSelectBatch(), 123);
            planner.State.RemainingRotationSeconds = 321;
            string path = Path.Combine(directory, "quality.json");
            await IdleStateStore.SaveAsync(path, planner.State);
            var restored = new IdlePlanner(planner.Options, await IdleStateStore.LoadAsync(path));
            Sequence(planner.CurrentOrSelectBatch(), restored.CurrentOrSelectBatch());
            Equal(321D, restored.State.RemainingRotationSeconds);
            foreach (var pair in planner.State.QualityDeficitSeconds) Near(pair.Value, restored.State.QualityDeficitSeconds[pair.Key]);
            restored.SetRatings(ratings);
            foreach (var pair in planner.State.QualityDeficitSeconds) Near(pair.Value, restored.State.QualityDeficitSeconds[pair.Key]);
            for (int iteration = 0; iteration < 30; iteration++) {
                Sequence(planner.CurrentOrSelectBatch(), restored.CurrentOrSelectBatch());
                planner.AccruePlaytime(planner.CurrentOrSelectBatch(), 7200);
                restored.AccruePlaytime(restored.CurrentOrSelectBatch(), 7200);
                Sequence(planner.Rotate(), restored.Rotate());
            }
        });
    }),
    ("候选都能同时运行时权重不减少运行时间，并明确解释", () => Sync(() => {
        var planner = Weighted(5, (1, 100), (2, 1), (3, 50));
        RunWeighted(planner, 10);
        foreach (uint id in new uint[] { 1, 2, 3 }) {
            Near(1200, planner.State.PluginAllocatedMinutes[id]);
            Near(1, planner.State.QualityTargetShares[id]);
            True(planner.ExplainWeight(id).Contains("全部同时运行"));
        }
    })),
    ("加权字段校验拒绝非法配置和损坏状态，旧schema1字段可全部缺省", async () => {
        foreach (var options in new IdleOptions[] {
            new() { ReputationWeightPercent = -1 }, new() { ReputationWeightPercent = 101 },
            new() { WeightStrength = 0.249 }, new() { WeightStrength = 4.001 },
            new() { WeightStrength = double.NaN }, new() { WeightStrength = double.PositiveInfinity },
            new() { RatingRefreshHours = 0 }, new() { RatingRefreshHours = 8761 }
        }) Throws<ArgumentException>(() => Create(options));
        var planner = Weighted(1, (1, 90), (2, 60));
        Throws<ArgumentException>(() => planner.SetWeight(0, 50));
        Throws<ArgumentOutOfRangeException>(() => planner.SetWeight(1, 0));
        Throws<ArgumentOutOfRangeException>(() => planner.SetWeight(1, 101));
        Throws<InvalidDataException>(() => new IdleState { ManualWeights = new() { [1] = 0 } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { QualityAutomaticScores = new() { [1] = 101 } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { QualityTargetShares = new() { [1] = 1.1 } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { QualityDeficitSeconds = new() { [1] = double.NaN } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { PluginAllocatedMinutes = new() { [1] = -1 } }.Validate());
        await WithTemporaryDirectory(async directory => {
            var old = JsonNode.Parse(JsonSerializer.Serialize(planner.State))!.AsObject();
            foreach (string field in new[] { "ManualWeights", "PluginAllocatedMinutes", "QualityAutomaticScores", "QualityTargetShares", "QualityDeficitSeconds" }) old.Remove(field);
            old["LastSelectionMode"] = "RoundRobin";
            string path = Path.Combine(directory, "old.json");
            await File.WriteAllTextAsync(path, old.ToJsonString());
            var loaded = await IdleStateStore.LoadAsync(path);
            Equal(0, loaded.ManualWeights.Count);
            Equal(0, loaded.PluginAllocatedMinutes.Count);
            Equal(0, loaded.QualityTargetShares.Count);
            loaded.Validate();
        });
    }),
    ("播放权限在提交前失效时取消应用，保留当前配置和原名单恢复能力", async () => {
        await WithTemporaryDirectory(async directory => {
            foreach (bool alreadyManaged in new[] { false, true }) {
                string path = Path.Combine(directory, alreadyManaged ? "ManagedGuard.json" : "FirstGuard.json");
                var original = NativeConfig([10, 730]);
                await File.WriteAllTextAsync(path, original.ToJsonString());
                if (alreadyManaged) await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
                string before = await File.ReadAllTextAsync(path);
                int permissionChecks = 0;
                await ThrowsAsync<OperationCanceledException>(() => NativeIdleConfigStore.ApplyAsync(path, new uint[] { 440 }, canCommit: () => {
                    permissionChecks++;
                    return false;
                }));
                True(permissionChecks > 0);
                Equal(before, await File.ReadAllTextAsync(path));
                if (alreadyManaged) {
                    // The failed replacement must not corrupt LastAppliedGames: an
                    // immediate restore must still recognize the successful old batch.
                    await NativeIdleConfigStore.RestoreAsync(path, canCommit: () => true);
                    True(JsonNode.DeepEquals(original, await ReadObject(path)));
                }
                await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 400, 620 }, canCommit: () => true);
                await NativeIdleConfigStore.RestoreAsync(path, canCommit: () => true);
                True(JsonNode.DeepEquals(original, await ReadObject(path)));
            }
        });
    }),
    ("还原提交权限失效时保留配置和托管备份，权限恢复后可完成还原禁用", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "RestoreGuard.json");
            var expected = NativeConfig([10, 730]);
            await File.WriteAllTextAsync(path, expected.ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570, 440 });
            string applied = await File.ReadAllTextAsync(path);
            string backupPath = NativeIdleConfigStore.BackupPath(path);
            string backup = await File.ReadAllTextAsync(backupPath);
            int permissionChecks = 0;
            await ThrowsAsync<OperationCanceledException>(() => NativeIdleConfigStore.RestoreAsync(path, disablePlugin: true, canCommit: () => {
                permissionChecks++;
                return false;
            }));
            True(permissionChecks > 0);
            Equal(applied, await File.ReadAllTextAsync(path));
            Equal(backup, await File.ReadAllTextAsync(backupPath));
            expected["SmartIdle"]!["Enabled"] = false;
            await NativeIdleConfigStore.RestoreAsync(path, disablePlugin: true, canCommit: () => true);
            True(JsonNode.DeepEquals(expected, await ReadObject(path)));
        });
    }),
    ("待空闲还原请求可跨重启保存，旧状态无此字段时不误触发", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "state.json");
            var state = new IdleState { Paused = true, RestoreOriginalOnIdle = true };
            await IdleStateStore.SaveAsync(path, state);
            True((await IdleStateStore.LoadAsync(path)).RestoreOriginalOnIdle);
            var old = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            old.Remove("RestoreOriginalOnIdle");
            await File.WriteAllTextAsync(path, old.ToJsonString());
            True(!(await IdleStateStore.LoadAsync(path)).RestoreOriginalOnIdle);
            Throws<InvalidDataException>(() => new IdleState { RestoreOriginalOnIdle = true }.Validate());
        });
    }),
    ("原生配置模式只改游戏名单，保留文案、挂卡偏好、凭据及未知字段", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "BotName.json");
            var original = NativeConfig([10, 730]);
            await File.WriteAllTextAsync(path, original.ToJsonString());
            True(await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570, 440 }));
            var changed = await ReadObject(path);
            Sequence([570, 440], ReadGames(changed));
            changed.Remove("GamesPlayedWhileIdle");
            original.Remove("GamesPlayedWhileIdle");
            True(JsonNode.DeepEquals(original, changed));
            True(await NativeIdleConfigStore.MatchesAsync(path, new uint[] { 570, 440 }));
            True(!await NativeIdleConfigStore.MatchesAsync(path, new uint[] { 10, 730 }));
        });
    }),
    ("重复应用相同名单不写配置，不制造额外重连触发", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Same.json");
            await File.WriteAllTextAsync(path, NativeConfig([10]).ToJsonString());
            True(await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570, 440 }));
            string applied = await File.ReadAllTextAsync(path);
            DateTime sentinel = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, sentinel);
            True(!await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570, 440 }));
            Equal(applied, await File.ReadAllTextAsync(path));
            Equal(sentinel, File.GetLastWriteTimeUtc(path));
        });
    }),
    ("多次换批后还原首次原名单，不把上一批当作原设置", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "BotName.json");
            var original = NativeConfig([10, 730]);
            await File.WriteAllTextAsync(path, original.ToJsonString());
            Equal(Path.Combine(directory, "SmartIdle-native", "BotName.json"), NativeIdleConfigStore.BackupPath(path));
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570, 440 });
            True(File.Exists(NativeIdleConfigStore.BackupPath(path)));
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 400, 620 });
            await NativeIdleConfigStore.RestoreAsync(path);
            True(JsonNode.DeepEquals(original, await ReadObject(path)));
        });
    }),
    ("原配置没有游戏字段时还原会移除字段，保留其他设置", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Absent.json");
            var original = NativeConfig([10]);
            original.Remove("GamesPlayedWhileIdle");
            await File.WriteAllTextAsync(path, original.ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            True((await ReadObject(path)).ContainsKey("GamesPlayedWhileIdle"));
            await NativeIdleConfigStore.RestoreAsync(path);
            var restored = await ReadObject(path);
            True(!restored.ContainsKey("GamesPlayedWhileIdle"));
            True(JsonNode.DeepEquals(original, restored));
        });
    }),
    ("原配置显式空游戏数组时还原仍保留空数组", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Empty.json");
            var original = NativeConfig([]);
            await File.WriteAllTextAsync(path, original.ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            await NativeIdleConfigStore.RestoreAsync(path);
            var restored = await ReadObject(path);
            True(restored.ContainsKey("GamesPlayedWhileIdle"));
            Equal(0, ReadGames(restored).Length);
            True(JsonNode.DeepEquals(original, restored));
        });
    }),
    ("取消配置写入和还原不覆盖原文件或已有备份", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Cancelled.json");
            string original = NativeConfig([10]).ToJsonString();
            await File.WriteAllTextAsync(path, original);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 }, cancellation.Token));
            Equal(original, await File.ReadAllTextAsync(path));
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            string applied = await File.ReadAllTextAsync(path);
            string backupPath = NativeIdleConfigStore.BackupPath(path);
            string backup = await File.ReadAllTextAsync(backupPath);
            await ThrowsAsync<OperationCanceledException>(() => NativeIdleConfigStore.ApplyAsync(path, new uint[] { 440 }, cancellation.Token));
            await ThrowsAsync<OperationCanceledException>(() => NativeIdleConfigStore.RestoreAsync(path, false, cancellation.Token));
            Equal(applied, await File.ReadAllTextAsync(path));
            Equal(backup, await File.ReadAllTextAsync(backupPath));
        });
    }),
    ("损坏原名单备份时拒绝还原并保留当前配置原字节", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Corrupt.json");
            await File.WriteAllTextAsync(path, NativeConfig([10]).ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            string applied = await File.ReadAllTextAsync(path);
            string backupPath = NativeIdleConfigStore.BackupPath(path);
            await File.WriteAllTextAsync(backupPath, "{broken backup");
            await ThrowsAsync<InvalidDataException>(() => NativeIdleConfigStore.RestoreAsync(path));
            Equal(applied, await File.ReadAllTextAsync(path));
            Equal("{broken backup", await File.ReadAllTextAsync(backupPath));
        });
    }),
    ("用户已另行修改原生游戏名单时还原不覆盖其新名单", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "External.json");
            await File.WriteAllTextAsync(path, NativeConfig([10]).ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            var external = await ReadObject(path);
            external["GamesPlayedWhileIdle"] = new JsonArray(999U, 1000U);
            string edited = external.ToJsonString();
            await File.WriteAllTextAsync(path, edited);
            await NativeIdleConfigStore.RestoreAsync(path);
            Equal(edited, await File.ReadAllTextAsync(path));
            // A later restore must not reuse a relinquished claim to overwrite the edit.
            await NativeIdleConfigStore.RestoreAsync(path);
            Equal(edited, await File.ReadAllTextAsync(path));
        });
    }),
    ("还原并禁用只额外修改 SmartIdle.Enabled，保留其他插件配置", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Disable.json");
            var expected = NativeConfig([10, 730]);
            await File.WriteAllTextAsync(path, expected.ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            expected["SmartIdle"]!["Enabled"] = false;
            await NativeIdleConfigStore.RestoreAsync(path, disablePlugin: true);
            True(JsonNode.DeepEquals(expected, await ReadObject(path)));
        });
    }),
    ("还原时合并保留接管期间用户修改的其他配置字段", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "Merge.json");
            await File.WriteAllTextAsync(path, NativeConfig([10, 730]).ToJsonString());
            await NativeIdleConfigStore.ApplyAsync(path, new uint[] { 570 });
            var changed = await ReadObject(path);
            changed["CustomGamePlayedWhileIdle"] = "USER NEW TEXT";
            changed["FarmingPreferences"] = 17;
            changed["UserNewField"] = new JsonObject { ["nested"] = "保留" };
            await File.WriteAllTextAsync(path, changed.ToJsonString());
            changed["GamesPlayedWhileIdle"] = new JsonArray(10U, 730U);
            await NativeIdleConfigStore.RestoreAsync(path);
            True(JsonNode.DeepEquals(changed, await ReadObject(path)));
        });
    }),
    ("登录完成即可接管，不添加固定的原生恢复等待", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        True(!lifecycle.CanPlay(now.AddDays(1)));
        lifecycle.OnConnected(now);
        True(lifecycle.Connected);
        Equal(now, lifecycle.ReadyAfter);
        True(lifecycle.CanPlay(now));
        True(lifecycle.CanPlay(now.AddMilliseconds(1)));
    })),
    ("游戏解锁只等待配置的占用冷却，到期即可接管", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        lifecycle.OnConnected(now);
        True(lifecycle.OnPlayingBlocked(true, now.AddSeconds(10), 60));
        True(lifecycle.PlayingBlocked);
        True(!lifecycle.CanPlay(now.AddHours(1)));
        True(lifecycle.OnPlayingBlocked(false, now.AddSeconds(20), 60));
        Equal(now.AddSeconds(80), lifecycle.ReadyAfter);
        True(!lifecycle.CanPlay(now.AddSeconds(79.999)));
        True(lifecycle.CanPlay(now.AddSeconds(80)));
    })),
    ("共享库与本人游玩分别阻塞，解除一种不能解除另一种", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        lifecycle.OnConnected(now);
        True(lifecycle.OnPlayingBlocked(true, now.AddSeconds(5), 5));
        True(lifecycle.OnLibraryBlocked(true, now.AddSeconds(6), 5));
        True(lifecycle.OnPlayingBlocked(false, now.AddSeconds(10), 5));
        True(!lifecycle.PlayingBlocked && lifecycle.LibraryBlocked);
        True(!lifecycle.CanPlay(now.AddHours(1)));
        True(lifecycle.OnLibraryBlocked(false, now.AddSeconds(20), 5));
        True(!lifecycle.LibraryBlocked);
        True(!lifecycle.CanPlay(now.AddSeconds(24.999)));
        True(lifecycle.CanPlay(now.AddSeconds(25)));
    })),
    ("重叠的解锁回调保留较晚期限，不缩短既有冷却", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        lifecycle.OnConnected(now);
        lifecycle.OnPlayingBlocked(true, now.AddSeconds(4), 60);
        lifecycle.OnLibraryBlocked(true, now.AddSeconds(5), 5);
        lifecycle.OnPlayingBlocked(false, now.AddSeconds(10), 60);
        lifecycle.OnLibraryBlocked(false, now.AddSeconds(11), 5);
        Equal(now.AddSeconds(70), lifecycle.ReadyAfter);
        True(!lifecycle.CanPlay(now.AddSeconds(69.999)));
        True(lifecycle.CanPlay(now.AddSeconds(70)));
        lifecycle.OnLibraryBlocked(true, now.AddSeconds(71), 60);
        lifecycle.OnLibraryBlocked(false, now.AddSeconds(72), 60);
        Equal(now.AddSeconds(132), lifecycle.ReadyAfter);
    })),
    ("重复占用通知不延长冷却，低配置值只保留 5 秒下限", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        lifecycle.OnConnected(now);
        True(!lifecycle.OnPlayingBlocked(false, now.AddSeconds(4), 60));
        True(!lifecycle.OnLibraryBlocked(false, now.AddSeconds(4), 60));
        Equal(now, lifecycle.ReadyAfter);
        True(lifecycle.OnPlayingBlocked(true, now.AddSeconds(5), 0));
        True(!lifecycle.OnPlayingBlocked(true, now.AddSeconds(6), 60));
        True(lifecycle.OnPlayingBlocked(false, now.AddSeconds(7), 0));
        Equal(now.AddSeconds(12), lifecycle.ReadyAfter);
        True(!lifecycle.OnPlayingBlocked(false, now.AddSeconds(8), 60));
        Equal(now.AddSeconds(12), lifecycle.ReadyAfter);
        lifecycle.OnLibraryBlocked(true, now.AddSeconds(13), -20);
        True(!lifecycle.OnLibraryBlocked(true, now.AddSeconds(14), 60));
        lifecycle.OnLibraryBlocked(false, now.AddSeconds(15), -20);
        True(!lifecycle.OnLibraryBlocked(false, now.AddSeconds(16), 60));
        Equal(now.AddSeconds(20), lifecycle.ReadyAfter);
        True(!lifecycle.CanPlay(now.AddSeconds(19.999)));
        True(lifecycle.CanPlay(now.AddSeconds(20)));
    })),
    ("断线立即禁止播放，重连清除旧会话占用与冷却", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lifecycle = new PlaybackLifecycle();
        lifecycle.OnConnected(now);
        lifecycle.OnPlayingBlocked(true, now.AddSeconds(4), 60);
        lifecycle.OnPlayingBlocked(false, now.AddSeconds(5), 60);
        lifecycle.OnLibraryBlocked(true, now.AddSeconds(6), 60);
        lifecycle.OnDisconnected();
        True(!lifecycle.Connected);
        True(!lifecycle.CanPlay(now.AddDays(1)));
        lifecycle.OnConnected(now.AddSeconds(10));
        True(!lifecycle.PlayingBlocked && !lifecycle.LibraryBlocked);
        Equal(now.AddSeconds(10), lifecycle.ReadyAfter);
        True(lifecycle.CanPlay(now.AddSeconds(10)));
        lifecycle.OnDisconnected();
        True(!lifecycle.CanPlay(now.AddSeconds(11)));
    })),
    ("同时解锁的通知顺序不影响完整占用冷却", () => Sync(() => {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (bool playingFirst in new[] { true, false }) {
            var lifecycle = new PlaybackLifecycle();
            lifecycle.OnConnected(now);
            lifecycle.OnPlayingBlocked(true, now.AddSeconds(1), 60);
            lifecycle.OnLibraryBlocked(true, now.AddSeconds(2), 5);
            if (playingFirst) {
                lifecycle.OnPlayingBlocked(false, now.AddSeconds(10), 60);
                lifecycle.OnLibraryBlocked(false, now.AddSeconds(10), 5);
            } else {
                lifecycle.OnLibraryBlocked(false, now.AddSeconds(10), 5);
                lifecycle.OnPlayingBlocked(false, now.AddSeconds(10), 60);
            }
            Equal(now.AddSeconds(70), lifecycle.ReadyAfter);
            True(!lifecycle.PlayingBlocked && !lifecycle.LibraryBlocked);
            True(!lifecycle.CanPlay(now.AddSeconds(69.999)));
            True(lifecycle.CanPlay(now.AddSeconds(70)));
        }
    })),
    ("自定义状态占一个位置，保留 31 个游戏和常驻，始终不超过 32", () => Sync(() => {
        Equal(32, PlaylistCapacity.GameSlots(32, null));
        Equal(32, PlaylistCapacity.GameSlots(32, ""));
        Equal(31, PlaylistCapacity.GameSlots(32, "WYFANG.NET"));
        Equal(10, PlaylistCapacity.GameSlots(10, "WYFANG.NET"));
        var planner = Create(new IdleOptions { ConcurrentGames = PlaylistCapacity.GameSlots(32, "WYFANG.NET"), PinnedGames = Enumerable.Range(1, 10).Select(x => (uint)x).ToArray() });
        planner.RefreshLibrary(Enumerable.Range(1, 80).Select(x => Game((uint)x)));
        planner.ApproveAll();
        Equal(31, planner.Rotate().Count);
        Equal(21, planner.State.CurrentRotating.Count);
    })),
    ("默认填满 32 个位置，常驻保留并用最低时长填充剩余位置", () => Sync(() => {
        var planner = Create(new IdleOptions { PinnedGames = [1] });
        planner.RefreshLibrary(Enumerable.Range(1, 40).Select(id => Game((uint)id, id == 1 ? 9999U : (uint)id)));
        planner.ApproveAll();
        var batch = planner.CurrentOrSelectBatch();
        Equal(32, batch.Count);
        Equal(1U, batch[0]);
        Sequence(Enumerable.Range(1, 32).Select(id => (uint)id), batch);
        Equal(32, batch.Distinct().Count());
    })),
    ("低时长优先使用有效在线估算，重复 ID 不会重复累计", () => Sync(() => {
        var planner = Create(new IdleOptions { ConcurrentGames = 1, SelectionMode = "LeastPlaytime" });
        planner.RefreshLibrary([Game(1, 1), Game(2, 2)]);
        planner.ApproveAll();
        Sequence([1], planner.CurrentOrSelectBatch());
        planner.AccruePlaytime([1, 1], 120);
        Equal(3D, planner.EffectivePlaytimeMinutes(1));
        Sequence([1], planner.CurrentOrSelectBatch());
        Sequence([2], planner.Rotate());
    })),
    ("Steam 时长追上估算时不重复增加，外部真实游玩取较大值", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1, 100)]);
        planner.ApproveAll();
        planner.AccruePlaytime([1], 1200);
        Equal(120D, planner.EffectivePlaytimeMinutes(1));
        planner.RefreshLibrary([Game(1, 110)]);
        Equal(120D, planner.EffectivePlaytimeMinutes(1));
        planner.RefreshLibrary([Game(1, 120)]);
        Equal(120D, planner.EffectivePlaytimeMinutes(1));
        planner.AccruePlaytime([1], 600);
        Equal(130D, planner.EffectivePlaytimeMinutes(1));
        planner.RefreshLibrary([Game(1, 150)]);
        Equal(150D, planner.EffectivePlaytimeMinutes(1));
        planner.AccruePlaytime([1], 60);
        Equal(151D, planner.EffectivePlaytimeMinutes(1));
    })),
    ("低时长模式的相同时长按公平队列轮换", () => Sync(() => {
        var planner = Create(new IdleOptions { ConcurrentGames = 1 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        Sequence([1], planner.CurrentOrSelectBatch());
        Sequence([2], planner.Rotate());
        Sequence([3], planner.Rotate());
        Sequence([1], planner.Rotate());
    })),
    ("当前批次在低时长数据刷新及重启后保持，到换批时才重新排名", () => Sync(() => {
        var options = new IdleOptions { ConcurrentGames = 1 };
        var planner = Create(options);
        planner.RefreshLibrary([Game(1, 1), Game(2, 2)]);
        planner.ApproveAll();
        planner.State.RemainingRotationSeconds = 123;
        planner.RefreshLibrary([Game(1, 100), Game(2, 2)]);
        Sequence([1], planner.CurrentOrSelectBatch());
        var restored = new IdlePlanner(options, JsonSerializer.Deserialize<IdleState>(JsonSerializer.Serialize(planner.State))!);
        Sequence([1], restored.CurrentOrSelectBatch());
        Equal(123D, restored.State.RemainingRotationSeconds);
        Sequence([2], restored.Rotate());
    })),
    ("完全随机每批无重复且填满 32 位，保留 10 个常驻", () => Sync(() => {
        var options = new IdleOptions { SelectionMode = "Random", PinnedGames = Enumerable.Range(1, 10).Select(id => (uint)id).ToArray(), Blacklist = [1001] };
        var planner = Create(options);
        planner.RefreshLibrary(Enumerable.Range(1, 80).Select(id => Game((uint)id)).Concat([Game(1000, 0), Game(1001)]));
        planner.ApproveAll();
        var observed = new HashSet<uint>();
        for (int iteration = 0; iteration < 100; iteration++) {
            var batch = iteration == 0 ? planner.CurrentOrSelectBatch() : planner.Rotate();
            Equal(32, batch.Count);
            Equal(32, batch.Distinct().Count());
            for (uint pin = 1; pin <= 10; pin++) True(batch.Contains(pin));
            foreach (uint id in batch) {
                True(id is >= 1 and <= 80);
                if (id > 10) observed.Add(id);
            }
        }
        True(observed.Count > 22);
        string saved = JsonSerializer.Serialize(planner.State);
        var restored = new IdlePlanner(options, JsonSerializer.Deserialize<IdleState>(saved)!);
        Sequence(planner.CurrentOrSelectBatch(), restored.CurrentOrSelectBatch());
    })),
    ("随机模式允许跨批重复，不为了换批而空出可用位置", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "Random", ConcurrentGames = 3 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        SetEqual([1, 2, 3], planner.Rotate());
        SetEqual([1, 2, 3], planner.Rotate());
    })),
    ("切换模式重新选择批次并重置时长，同模式重启不重置", () => Sync(() => {
        var options = new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 1 };
        var planner = Create(options);
        planner.RefreshLibrary([Game(1, 1), Game(2, 100)]);
        planner.ApproveAll();
        Sequence([2], planner.Rotate());
        planner.State.RemainingRotationSeconds = 12;
        var changed = new IdlePlanner(options with { SelectionMode = "LeastPlaytime" }, planner.State);
        Sequence([1], changed.CurrentOrSelectBatch());
        Equal(7200D, changed.State.RemainingRotationSeconds);
        changed.State.RemainingRotationSeconds = 55;
        var same = new IdlePlanner(changed.Options, changed.State);
        Equal(55D, same.State.RemainingRotationSeconds);
        Sequence([1], same.CurrentOrSelectBatch());
    })),
    ("估算输入及保存值验证，未知游戏不会生成虚假记录", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1, 10)]);
        Throws<ArgumentOutOfRangeException>(() => planner.AccruePlaytime([1], double.NaN));
        Throws<ArgumentOutOfRangeException>(() => planner.AccruePlaytime([1], double.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => planner.AccruePlaytime([1], -1));
        Throws<ArgumentException>(() => planner.AccruePlaytime([0], 60));
        planner.AccruePlaytime([999], 60);
        Equal(0, planner.State.EstimatedPlaytimeMinutes.Count);
        Throws<InvalidDataException>(() => new IdleState { EstimatedPlaytimeMinutes = new() { [0] = 1 } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { EstimatedPlaytimeMinutes = new() { [1] = double.PositiveInfinity } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { EstimatedPlaytimeMinutes = new() { [1] = -1 } }.Validate());
        Throws<InvalidDataException>(() => new IdleState { EstimatedPlaytimeMinutes = null! }.Validate());
        Throws<InvalidDataException>(() => new IdleState { LastSelectionMode = "random" }.Validate());
    })),
    ("v0.1 状态缺少新增字段仍能加载，旧轮换保留且升级模式可重新选批", async () => {
        await WithTemporaryDirectory(async directory => {
            var options = new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 1 };
            var planner = Create(options);
            planner.RefreshLibrary([Game(1, 1), Game(2, 100)]);
            planner.ApproveAll();
            planner.Rotate();
            planner.State.RemainingRotationSeconds = 123;
            var old = JsonNode.Parse(JsonSerializer.Serialize(planner.State))!.AsObject();
            old.Remove("EstimatedPlaytimeMinutes");
            old.Remove("LastSelectionMode");
            string path = Path.Combine(directory, "v01.json");
            await File.WriteAllTextAsync(path, old.ToJsonString());
            var loaded = await IdleStateStore.LoadAsync(path);
            Equal(0, loaded.EstimatedPlaytimeMinutes.Count);
            True(loaded.LastSelectionMode is null);
            var roundRobin = new IdlePlanner(options, loaded);
            Sequence([2], roundRobin.CurrentOrSelectBatch());
            Equal(123D, roundRobin.State.RemainingRotationSeconds);
            var least = new IdlePlanner(options with { SelectionMode = "LeastPlaytime" }, await IdleStateStore.LoadAsync(path));
            Sequence([1], least.CurrentOrSelectBatch());
            Equal(7200D, least.State.RemainingRotationSeconds);
            least.AccruePlaytime([1], 120);
            await IdleStateStore.SaveAsync(path, least.State);
            var restored = new IdlePlanner(least.Options, await IdleStateStore.LoadAsync(path));
            Equal(3D, restored.EffectivePlaytimeMinutes(1));
            Equal("LeastPlaytime", restored.State.LastSelectionMode);
        });
    }),
    ("核心程序集不引用可被宿主裁剪的 System.Linq", () => Sync(() => {
        foreach (var assembly in typeof(IdlePlanner).Assembly.GetReferencedAssemblies()) {
            True(assembly.Name is not "System.Linq" and not "System.Linq.Queryable" and not "System.Linq.Parallel");
        }
    })),
    ("配置解析器保留缺省值并拒绝未知字段", () => Sync(() => {
        var options = JsonSerializer.Deserialize<IdleOptions>("{\"Enabled\":true}", IdleJson.Options)!;
        True(options.Enabled);
        Equal(32, options.ConcurrentGames);
        Equal("LeastPlaytime", options.SelectionMode);
        Equal(120, options.RotationMinutes);
        Equal(0, options.RefreshHours);
        options.Validate();
        Throws<JsonException>(() => JsonSerializer.Deserialize<IdleOptions>("{\"Enabledd\":true}", IdleJson.Options));
    })),
    ("首次读取只生成待加入名单，0 分钟不进入", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1, 0), Game(2, 1), Game(3, 600)]);
        SetEqual([2, 3], planner.State.Pending);
        Equal(0, planner.State.Approved.Count);
        Equal(0, planner.CurrentOrSelectBatch().Count);
        True(planner.State.Initialized);
    })),
    ("批准操作整体校验，混入零时长不会部分批准", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1, 0), Game(2, 30)]);
        Throws<ArgumentException>(() => planner.Approve([2, 1]));
        Equal(0, planner.State.Approved.Count);
        SetEqual([2], planner.State.Pending);
    })),
    ("新增与后来有时长的游戏仍须人工批准", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1), Game(2, 0)]);
        planner.ApproveAll();
        planner.RefreshLibrary([Game(1), Game(2, 15), Game(3)]);
        SetEqual([1], planner.CurrentOrSelectBatch());
        SetEqual([2, 3], planner.State.Pending);
    })),
    ("拒绝决定不会在下次扫描消失", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1), Game(2)]);
        planner.Reject([2]);
        planner.RefreshLibrary([Game(1), Game(2, 100)]);
        SetEqual([1], planner.State.Pending);
        planner.ApproveAll();
        SetEqual([1], planner.CurrentOrSelectBatch());
        planner.Approve([2]);
        SetEqual([1, 2], planner.State.Approved);
    })),
    ("常驻保留且只轮换剩余位置", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 3 });
        planner.RefreshLibrary(Enumerable.Range(1, 7).Select(id => Game((uint)id)));
        planner.ApproveAll();
        planner.Pin([1]);
        Sequence([1, 2, 3], planner.CurrentOrSelectBatch());
        Sequence([1, 4, 5], planner.Rotate());
        Sequence([1, 6, 7], planner.Rotate());
        Sequence([1, 2, 3], planner.Rotate());
    })),
    ("手动常驻允许零时长和工具，自动轮换仍须批准有时长", () => Sync(() => {
        var planner = Create(new IdleOptions { ConcurrentGames = 4 });
        planner.RefreshLibrary([Game(1), Game(2, 0), Game(3)]);
        planner.Pin([1, 2, 1840]);
        SetEqual([1, 2, 1840], planner.CurrentOrSelectBatch());
        Equal(0, planner.State.Approved.Count);
        planner.Approve([3]);
        Equal(4, planner.CurrentOrSelectBatch().Count);
        Throws<ArgumentException>(() => planner.Approve([2]));
        planner.Unpin([2]);
        True(!planner.CurrentOrSelectBatch().Contains(2U));
        True(!planner.State.Library.ContainsKey(1840));
        planner.ValidateState();
    })),
    ("未知常驻工具可以排除与拒绝，配置黑名单不可绕过", () => Sync(() => {
        var planner = Create(new IdleOptions { PinnedGames = [1840, 250820], Blacklist = [440] });
        SetEqual([1840, 250820], planner.CurrentOrSelectBatch());
        Throws<ArgumentException>(() => planner.Pin([440]));
        planner.Reject([1840]);
        planner.Exclude([250820]);
        Equal(0, planner.CurrentOrSelectBatch().Count);
        planner.Unexclude([250820]);
        Equal(0, planner.State.Pending.Count);
        Equal(0, planner.State.Library.Count);
    })),
    ("常驻占满时没有轮换，不会静默丢常驻", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 2 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        planner.Pin([1, 2]);
        Sequence([1, 2], planner.Rotate());
        Equal(0, planner.State.CurrentRotating.Count);
        Throws<ArgumentException>(() => planner.Pin([3]));
        Sequence([1, 2], planner.State.PinnedGames);
    })),
    ("取消常驻后重新进入公平队列", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 2 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        planner.Pin([1]);
        planner.Unpin([1]);
        var served = new HashSet<uint>(planner.CurrentOrSelectBatch());
        served.UnionWith(planner.Rotate());
        SetEqual([1, 2, 3], served);
    })),
    ("小队列跨尾部轮换不会重复占位", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 3 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3), Game(4)]);
        planner.ApproveAll();
        Sequence([1, 2, 3], planner.CurrentOrSelectBatch());
        Sequence([4, 1, 2], planner.Rotate());
        Sequence([3, 4, 1], planner.Rotate());
    })),
    ("不同库规模与槽位均无饥饿、重复或越限", () => Sync(() => {
        for (int count = 1; count <= 65; count++) {
            for (int slots = 1; slots <= Math.Min(32, count + 1); slots++) {
                var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = slots });
                planner.RefreshLibrary(Enumerable.Range(1, count).Select(id => Game((uint)id)));
                planner.ApproveAll();
                var served = new HashSet<uint>();
                for (int i = 0; i < (count + slots - 1) / slots; i++) {
                    var batch = i == 0 ? planner.CurrentOrSelectBatch() : planner.Rotate();
                    True(batch.Count <= slots && batch.Count <= 32);
                    Equal(batch.Count, batch.Distinct().Count());
                    served.UnionWith(batch);
                }
                Equal(count, served.Count);
            }
        }
    })),
    ("手动常驻不因库快照遗漏而消失，新增轮换仍须确认", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 3 });
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        planner.Pin([1]);
        planner.RefreshLibrary([Game(2), Game(3), Game(4)]);
        SetEqual([1, 2, 3], planner.CurrentOrSelectBatch());
        SetEqual([4], planner.State.Pending);
        planner.RefreshLibrary([Game(1), Game(2), Game(3), Game(4)]);
        SetEqual([1, 2, 3], planner.CurrentOrSelectBatch());
    })),
    ("新配置禁止名单盖过已有批准", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1), Game(2)]);
        planner.ApproveAll();
        var changed = new IdlePlanner(new IdleOptions { Blacklist = [1] }, planner.State);
        SetEqual([2], changed.CurrentOrSelectBatch());
        Throws<ArgumentException>(() => changed.Pin([1]));
    })),
    ("禁止与解除禁止都不会绕过人工批准", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1), Game(2)]);
        planner.ApproveAll();
        planner.Pin([1]);
        planner.Exclude([1]);
        SetEqual([2], planner.CurrentOrSelectBatch());
        Equal(0, planner.State.PinnedGames.Count);
        planner.Unexclude([1]);
        SetEqual([1], planner.State.Pending);
        SetEqual([2], planner.CurrentOrSelectBatch());
    })),
    ("状态重建保留批次、公平顺序、暂停和有效剩余时间", () => Sync(() => {
        var options = new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 2 };
        var planner = Create(options);
        planner.RefreshLibrary([Game(1), Game(2), Game(3), Game(4), Game(5)]);
        planner.ApproveAll();
        planner.Rotate();
        planner.State.Paused = true;
        planner.State.RemainingRotationSeconds = 1234.5;
        string json = JsonSerializer.Serialize(planner.State);
        var restored = new IdlePlanner(options, JsonSerializer.Deserialize<IdleState>(json)!);
        Sequence([3, 4], restored.CurrentOrSelectBatch());
        Equal(1234.5, restored.State.RemainingRotationSeconds);
        True(restored.State.Paused);
        Sequence([5, 1], restored.Rotate());
    })),
    ("状态查询不会重置到期计时或轮换", () => Sync(() => {
        var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 1 });
        planner.RefreshLibrary([Game(1), Game(2)]);
        planner.ApproveAll();
        planner.State.RemainingRotationSeconds = 0;
        Sequence([1], planner.CurrentOrSelectBatch());
        Equal(0D, planner.State.RemainingRotationSeconds);
        Sequence([2], planner.Rotate());
    })),
    ("命令修改的常驻在相同配置重启后保留", () => Sync(() => {
        var options = new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 2, PinnedGames = [1] };
        var planner = Create(options);
        planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
        planner.ApproveAll();
        planner.Unpin([1]);
        planner.Pin([2]);
        var restored = new IdlePlanner(options, planner.State);
        Sequence([2], restored.State.PinnedGames);
        var configured = new IdlePlanner(options with { PinnedGames = [3] }, restored.State);
        Sequence([3], configured.State.PinnedGames);
    })),
    ("损坏快照不会污染之前的已确认库", () => Sync(() => {
        var planner = Create();
        planner.RefreshLibrary([Game(1)]);
        planner.ApproveAll();
        Throws<ArgumentException>(() => planner.RefreshLibrary([Game(2), Game(2)]));
        SetEqual([1], planner.CurrentOrSelectBatch());
        Throws<ArgumentException>(() => planner.RefreshLibrary([Game(0)]));
        SetEqual([1], planner.CurrentOrSelectBatch());
    })),
    ("无效配置被明确拒绝", () => Sync(() => {
        IdleOptions[] invalid = [
            new() { ConcurrentGames = 0 }, new() { ConcurrentGames = 33 },
            new() { RotationMinutes = 4 }, new() { RefreshHours = -1 },
            new() { ConcurrentGames = 1, PinnedGames = [1, 2] },
            new() { PinnedGames = [1], Blacklist = [1] }, new() { PinnedGames = [0] },
            new() { Blacklist = [1, 1] }, new() { SelectionMode = "leastplaytime" },
            new() { SelectionMode = "" }, new() { SelectionMode = null! }
        ];
        foreach (var options in invalid) Throws<ArgumentException>(() => Create(options));
    })),
    ("状态结构冲突与未知版本被拒绝", () => Sync(() => {
        Throws<InvalidDataException>(() => new IdleState { SchemaVersion = 2 }.Validate());
        Throws<InvalidDataException>(() => new IdleState { Approved = [1], Pending = [1] }.Validate());
        Throws<InvalidDataException>(() => new IdleState { CurrentRotating = [1] }.Validate());
        Throws<InvalidDataException>(() => new IdleState { Queue = [1, 1] }.Validate());
        Throws<InvalidDataException>(() => new IdleState { RemainingRotationSeconds = double.NaN }.Validate());
    })),
    ("原子保存、加载与重启续队列", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            var empty = await IdleStateStore.LoadAsync(path);
            True(!empty.Initialized);
            var planner = new IdlePlanner(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 1 }, empty);
            planner.RefreshLibrary([Game(1), Game(2), Game(3)]);
            planner.ApproveAll();
            planner.Rotate();
            planner.State.RemainingRotationSeconds = 55;
            await IdleStateStore.SaveAsync(path, planner.State);
            var loaded = new IdlePlanner(planner.Options, await IdleStateStore.LoadAsync(path));
            Sequence([2], loaded.CurrentOrSelectBatch());
            Equal(55D, loaded.State.RemainingRotationSeconds);
            Sequence([3], loaded.Rotate());
            if (!OperatingSystem.IsWindows()) {
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        });
    }),
    ("损坏文件保持原样，不回退为空名单", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            string[] invalid = ["{broken", "null", "{\"SchemaVersion\":99}", "{\"UnknownField\":1}", "{\"Approved\":null}"];
            foreach (string original in invalid) {
                await File.WriteAllTextAsync(path, original);
                await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
                Equal(original, await File.ReadAllTextAsync(path));
            }
        });
    }),
    ("缺少任何必需持久字段都拒绝加载并保留原文件", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            var planner = Create();
            planner.RefreshLibrary([Game(1), Game(2)]);
            planner.Approve([1]);
            string complete = JsonSerializer.Serialize(planner.State);
            foreach (string field in JsonNode.Parse(complete)!.AsObject().Select(pair => pair.Key).ToArray()) {
                if (field is "EstimatedPlaytimeMinutes" or "LastSelectionMode" or "RestoreOriginalOnIdle" or
                    "ManualWeights" or "PluginAllocatedMinutes" or "QualityAutomaticScores" or "QualityTargetShares" or "QualityDeficitSeconds") continue;
                var incomplete = JsonNode.Parse(complete)!.AsObject();
                incomplete.Remove(field);
                string original = incomplete.ToJsonString();
                await File.WriteAllTextAsync(path, original);
                await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
                Equal(original, await File.ReadAllTextAsync(path));
            }
            await File.WriteAllTextAsync(path, "{}");
            await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
            Equal("{}", await File.ReadAllTextAsync(path));
        });
    }),
    ("游戏记录缺失时长不默认变成 0 分钟", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            var planner = Create();
            planner.RefreshLibrary([Game(1)]);
            planner.ApproveAll();
            var document = JsonNode.Parse(JsonSerializer.Serialize(planner.State))!;
            document["Library"]!["1"]!.AsObject().Remove("PlaytimeMinutes");
            string original = document.ToJsonString();
            await File.WriteAllTextAsync(path, original);
            await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
            Equal(original, await File.ReadAllTextAsync(path));
        });
    }),
    ("完整的未来版本状态与决定冲突均被拒绝", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            var state = new IdleState { SchemaVersion = 2 };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state));
            await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
            state.SchemaVersion = 1;
            state.Approved.Add(1);
            state.Pending.Add(1);
            string original = JsonSerializer.Serialize(state);
            await File.WriteAllTextAsync(path, original);
            await ThrowsAsync<InvalidDataException>(() => IdleStateStore.LoadAsync(path));
            Equal(original, await File.ReadAllTextAsync(path));
        });
    }),
    ("取消保存不覆盖旧状态，不遗留临时文件", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            await IdleStateStore.SaveAsync(path, new IdleState { Paused = true });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => IdleStateStore.SaveAsync(path, new IdleState(), cancellation.Token));
            True((await IdleStateStore.LoadAsync(path)).Paused);
            Equal(1, Directory.GetFiles(directory).Length);
        });
    })
};

foreach (var test in tests) {
    try {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed++;
    } catch (Exception exception) {
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
        failed++;
    }
}

if (args.Contains("--benchmark")) {
    var library = Enumerable.Range(1, 10000).Select(id => Game((uint)id)).ToArray();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long before = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();
    var planner = Create(new IdleOptions { SelectionMode = "RoundRobin", ConcurrentGames = 32 });
    planner.RefreshLibrary(library);
    planner.ApproveAll();
    for (int i = 0; i < 100; i++) planner.Rotate();
    stopwatch.Stop();
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Console.WriteLine($"BENCH 10,000 games + 100 rotations: {stopwatch.Elapsed.TotalMilliseconds:F2} ms; total allocations {allocated / 1024D / 1024D:F2} MiB (not steady RSS).");
    await WithTemporaryDirectory(async directory => {
        string path = Path.Combine(directory, "benchmark.json");
        await IdleStateStore.SaveAsync(path, planner.State);
        Console.WriteLine($"BENCH 10,000-game state JSON: {new FileInfo(path).Length:N0} bytes.");
    });
}
Console.WriteLine($"Tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static IdlePlanner Create(IdleOptions? options = null) => new(options ?? new IdleOptions(), new IdleState());
static OwnedGame Game(uint id, uint minutes = 60) => new(id, $"Game {id}", minutes);
static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true."); }
static void Near(double expected, double actual, double tolerance = 1E-8) {
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected} ± {tolerance}, got {actual}.");
}
static IdlePlanner Weighted(int slots, params (uint Id, int Score)[] games) {
    var planner = Create(new IdleOptions { SelectionMode = "QualityWeighted", ConcurrentGames = slots });
    planner.RefreshLibrary(games.Select(game => Game(game.Id)));
    foreach (var game in games) planner.SetWeight(game.Id, game.Score);
    planner.ApproveAll();
    return planner;
}
static void RunWeighted(IdlePlanner planner, int batches) {
    for (int iteration = 0; iteration < batches; iteration++) {
        var batch = planner.CurrentOrSelectBatch();
        True(batch.Count <= planner.Options.ConcurrentGames);
        Equal(batch.Count, batch.Distinct().Count());
        planner.AccruePlaytime(batch, planner.Options.RotationMinutes * 60D);
        planner.Rotate();
    }
}
static void Equal<T>(T expected, T actual) {
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
static void SetEqual(IEnumerable<uint> expected, IEnumerable<uint> actual) {
    if (!expected.ToHashSet().SetEquals(actual)) throw new InvalidOperationException($"Expected set [{string.Join(',', expected)}], got [{string.Join(',', actual)}].");
}
static void Sequence(IEnumerable<uint> expected, IEnumerable<uint> actual) {
    if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected sequence [{string.Join(',', expected)}], got [{string.Join(',', actual)}].");
}
static void Throws<TException>(Action action) where TException : Exception {
    try { action(); } catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception {
    try { await action(); } catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
static async Task WithTemporaryDirectory(Func<string, Task> action) {
    string directory = Path.Combine(Path.GetTempPath(), $"smartidle-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try { await action(directory); } finally { Directory.Delete(directory, recursive: true); }
}

static JsonObject NativeConfig(uint[] games) => new() {
    ["Enabled"] = true,
    ["SteamLogin"] = "dummy-test-login",
    ["SteamPassword"] = "dummy-test-password-not-a-real-secret",
    ["CustomGamePlayedWhileIdle"] = "CUSTOM TEST TEXT",
    ["FarmingPreferences"] = 129,
    ["GamesPlayedWhileIdle"] = new JsonArray(games.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
    ["SmartIdle"] = new JsonObject { ["Enabled"] = true, ["RotationMinutes"] = 120, ["SelectionMode"] = "LeastPlaytime" },
    ["OtherPlugin"] = new JsonObject { ["Enabled"] = true, ["Text"] = "原有设置", ["Values"] = new JsonArray(1, 2, 3) }
};
static async Task<JsonObject> ReadObject(string path) => JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
static uint[] ReadGames(JsonObject config) => config["GamesPlayedWhileIdle"]!.AsArray().Select(value => value!.GetValue<uint>()).ToArray();
