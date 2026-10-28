using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartIdle.Core;

int passed = 0;
int failed = 0;
var tests = new (string Name, Func<Task> Run)[] {
    ("自定义状态先清空，等待官方 500 毫秒，再提交游戏列表", async () => {
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        bool result = await PlaybackTransition.ApplyAsync("WYFANG.NET", () => {
            events.Add("prepare");
            return true;
        }, () => {
            events.Add("commit");
            return true;
        }, cancellation.Token, (duration, token) => {
            Equal(TimeSpan.FromMilliseconds(500), duration);
            Equal(cancellation.Token, token);
            Equal("prepare", string.Join(',', events));
            events.Add("delay");
            return Task.CompletedTask;
        });
        True(result);
        Equal("prepare,delay,commit", string.Join(',', events));
    }),
    ("未设置自定义状态时直接提交，无清空等待", async () => {
        foreach (string? customName in new string?[] { null, "" }) {
            var events = new List<string>();
            bool result = await PlaybackTransition.ApplyAsync(customName, () => {
                events.Add("prepare");
                return true;
            }, () => {
                events.Add("commit");
                return true;
            }, delay: (_, _) => throw new InvalidOperationException("普通播放不应等待。"));
            True(result);
            Equal("prepare,commit", string.Join(',', events));
        }
    }),
    ("准备阶段拒绝接管时不等待也不发送游戏列表", async () => {
        bool result = await PlaybackTransition.ApplyAsync("WYFANG.NET", () => false,
            () => throw new InvalidOperationException("准备失败后不应提交。"),
            delay: (_, _) => throw new InvalidOperationException("准备失败后不应等待。"));
        True(!result);
    }),
    ("重载取消发生在清空等待中时不得提交旧列表", async () => {
        using var cancellation = new CancellationTokenSource();
        bool prepared = false;
        bool committed = false;
        await ThrowsAsync<OperationCanceledException>(() => PlaybackTransition.ApplyAsync("WYFANG.NET", () => {
            prepared = true;
            return true;
        }, () => {
            committed = true;
            return true;
        }, cancellation.Token, (_, _) => {
            cancellation.Cancel();
            // A timer completion can race with cancellation. ApplyAsync must recheck
            // the token even when the injected wait completes successfully.
            return Task.CompletedTask;
        }));
        True(prepared);
        True(!committed);
    }),
    ("等待中会话发生变化再恢复时仍拒绝旧发送", async () => {
        long generation = 1;
        long expected = 0;
        bool sent = false;
        bool result = await PlaybackTransition.ApplyAsync("WYFANG.NET", () => {
            expected = generation;
            return true;
        }, () => {
            if (generation != expected) return false;
            sent = true;
            return true;
        }, delay: (_, _) => {
            // A farming/connection lifecycle can start and finish inside the gap.
            // The final permission may be true, but the previous epoch is stale.
            generation += 2;
            return Task.CompletedTask;
        });
        True(!result);
        True(!sent);
    }),
    ("等待期间本人开始游玩时提交阶段重新检查权限", async () => {
        bool canPlay = true;
        bool sent = false;
        bool result = await PlaybackTransition.ApplyAsync("WYFANG.NET", () => canPlay, () => {
            if (!canPlay) return false;
            sent = true;
            return true;
        }, delay: (_, _) => {
            canPlay = false;
            return Task.CompletedTask;
        });
        True(!result);
        True(!sent);
    }),
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
    ("缺少任何持久字段都拒绝加载并保留原文件", async () => {
        await WithTemporaryDirectory(async directory => {
            string path = Path.Combine(directory, "bot.json");
            var planner = Create();
            planner.RefreshLibrary([Game(1), Game(2)]);
            planner.Approve([1]);
            string complete = JsonSerializer.Serialize(planner.State);
            foreach (string field in JsonNode.Parse(complete)!.AsObject().Select(pair => pair.Key).ToArray()) {
                if (field is "EstimatedPlaytimeMinutes" or "LastSelectionMode") continue;
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
