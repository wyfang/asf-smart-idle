# ASF SmartIdle

为 ArchiSteamFarm 提供手动常驻名单与固定清单轮换，通过 ASF 原生 `GamesPlayedWhileIdle` 播放，支持低时长优先、完全随机、公平队列和 1～100 分加权轮换。

[构建下载](https://github.com/wyfang/asf-smart-idle/actions/workflows/build.yml) · [English](./README.en.md)

## 功能

- 首次读取游戏库，有累计时长的游戏进入待加入清单；轮换只使用人工批准的游戏。
- 默认只在首次读取；之后的重启、重连和轮换使用本地快照，手动刷新后新候选仍须批准。
- 手动常驻不受零时长或游戏库遗漏限制，需自行确认许可证；剩余位置按选定策略填满。无自定义状态时最多 32 款，保留自定义状态时最多 31 款。
- 支持禁止名单、逐款解释、播放预览、手动换批，以及重启后继续队列和暂停状态。
- 可按 Steam 口碑和评价数量自动计算 1～100 分权重，让高分游戏获得更多轮换时间；支持手动覆盖、偏向强度和共享评分缓存。
- 仅在选定机器人上管理 ASF 原生空闲游戏列表，保留自定义文案、挂卡偏好和账号等其他配置；原名单先备份，支持还原。
- 挂卡或本人游玩期间不更新空闲列表，中断期间不消耗本批轮换时间；换批通过该机器人的配置重载生效，会短暂重连。
- 不识别免费游戏，不查询商店价格，不区分本人游玩与 ASF 挂卡产生的历史时长。

## 使用

### 运行条件与安装

当前版本 **0.4.0**，编译目标为 **ASF 6.3.10.6 / .NET 10**。0.3.0 曾在 generic 宿主和裁剪后的 `linux-x64` 宿主验证加载与隔离烟测，并在官方 `linux-x64` Docker 的真实账号上验证原生清单换批、自定义文案发送、原名单备份还原及挂卡让位。挂卡测试覆盖开始、持续、停止及期间延后还原，未等待完整掉卡周期。0.4.0 已通过核心与评分服务测试、generic 宿主隔离烟测及官方评分接口请求验证；并在裁剪后的 `linux-x64` 宿主验证评分缓存和加权调度的重启恢复；新增加权调度尚未进行真实账号长期验证。其他 ASF 版本与长时间运行尚未验证。

从旧版本升级前，备份机器人配置和 `SmartIdle-state/`；回退旧版时同时恢复对应备份，0.3.0 无法读取新增的持久字段。

1. 运行下文构建命令，得到 `SmartIdle.zip`；若从 GitHub Actions 下载构建产物，先解开外层 artifact 压缩包。
2. 停止 ASF，把 `SmartIdle.zip` 解压到 ASF 的 `plugins/`，最终应有 `plugins/SmartIdle/SmartIdle.dll` 和 `plugins/SmartIdle/SmartIdle.Core.dll`。
3. 将以下字段合并进目标机器人的 JSON 配置，保留原有账号和其他字段。示例也见 [配置片段](./examples/bot.config.fragment.json)。

```json
{
  "SmartIdle": {
    "Enabled": true,
    "ConcurrentGames": 32,
    "SelectionMode": "LeastPlaytime",
    "RotationMinutes": 120,
    "RefreshHours": 0,
    "PinnedGames": [],
    "Blacklist": []
  }
}
```

4. 启动 ASF，确认日志显示 SmartIdle 加载成功，等待机器人登录并完成游戏库读取。
5. 使用具有目标机器人 Master 或 Owner 权限的账号执行命令。把下例 `BotName` 替换为机器人名称，即配置文件名去掉 `.json`；Steam 聊天使用默认前缀 `!`，IPC 命令入口省略该前缀。

```text
!SIDLE BotName SCAN
!SIDLE BotName STATUS
!SIDLE BotName LIST PENDING
!SIDLE BotName APPROVE ALL
!SIDLE BotName PREVIEW
```

`SCAN` 安排异步刷新，不代表立即完成，手动请求至少间隔 5 分钟。首次成功读取会自动发生，不必额外执行 `SCAN`。先通过 `STATUS` 和分页清单检查待加入游戏；`APPROVE ALL` 会批准当时所有待加入游戏。也可用 `APPROVE 570,730` 逐款批准。之后轮换只使用已批准清单；手动常驻是独立的明确选择。命令格式为 `SIDLE [BotName] <操作>`；省略名称时作用于接收命令的机器人。IPC 中请显式指定名称，例如 `SIDLE BotName STATUS`。

### 配置

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `Enabled` | `false` | 启用这个机器人的插件 |
| `ConcurrentGames` | `32` | 请求的游戏并发数，1～32，包含常驻；有自定义状态时有效上限为 31 |
| `SelectionMode` | `LeastPlaytime` | `LeastPlaytime` 低时长优先、`Random` 完全随机、`RoundRobin` 公平队列、`QualityWeighted` 评分加权 |
| `RotationMinutes` | `120` | 每批有效运行分钟数，5～525600 |
| `RefreshHours` | `0` | 0 禁用定期读取，重启重连复用快照；1～8760 为可选定时刷新小时数 |
| `ReputationWeightPercent` | `80` | 自动评分中口碑的占比，0～100；其余部分使用评价数量衡量人气，仅影响 `QualityWeighted` |
| `WeightStrength` | `1` | 分数偏向强度，0.25～4；1 按分数比例，较大值扩大高低分差距，仅影响 `QualityWeighted` |
| `RatingRefreshHours` | `168` | 评分缓存刷新间隔，1～8760 小时；与游戏库 `RefreshHours` 独立，仅 `QualityWeighted` 查询评分 |
| `PinnedGames` | `[]` | 手动常驻 AppID，不必先批准，不受 0 时长筛选；不会自动获取许可证 |
| `Blacklist` | `[]` | 配置禁止名单，禁止名单优先 |

配置字段区分大小写，未知字段会使配置校验失败。常驻数量不能超过有效游戏并发数，也不能与禁止名单冲突；占满时没有轮换位置。常驻项始终占位发送，但 Steam 是否计时取决于其许可证和平台规则，缺少游戏库资料不等于已计时。配置常驻清单在首次使用或变化时生效；配置不变时，命令修改的常驻跨重启保留。

`LeastPlaytime` 在每次换批时按 Steam 快照时长与本地估算累计时长的较大值排序，相同时按队列先后选择；高时长游戏可能很久轮不到。`Random` 每批随机且不重复，跨批允许重复。`RoundRobin` 保证符合条件的清单依次轮到。模式改变会重新选择本批并重置计时，同模式重启保留当前批次。

### 评分加权轮换

把 `SelectionMode` 设为 `QualityWeighted` 后，高分游戏会更频繁地进入固定时长批次，累计获得更多挂机时间。可从以下配置开始；已有常驻和禁止名单请保留：

```json
{
  "SmartIdle": {
    "Enabled": true,
    "ConcurrentGames": 8,
    "SelectionMode": "QualityWeighted",
    "RotationMinutes": 120,
    "RefreshHours": 0,
    "ReputationWeightPercent": 80,
    "WeightStrength": 1,
    "RatingRefreshHours": 168,
    "PinnedGames": [],
    "Blacklist": []
  }
}
```

自动分数为 1～100 的整数，每一分都参与调度。有评价时，口碑使用好评率，并加入相当于 100 条、75% 好评的先验，减少小样本 100% 好评的影响；人气使用评价总数的对数，在 100000 条时达到 100 分。两者按 `ReputationWeightPercent` 混合后四舍五入，限制在 1～100 分；没有评价时使用中性分数 50。这里的人气是累计评价数量，不是实时在线人数；分数是插件的调度依据，不是 Steam 官方百分制评分。

有效调度权重为 `分数 ^ WeightStrength`。例如强度为 1 时，90、60、30 分对应 3∶2∶1 的目标比例；强度为 2 时对应 9∶4∶1。强度越高，低分游戏的等待可能越长。插件单独记录实际分配时间，以加权公平策略选择后续批次，不用账号过去的 Steam 总时长抵扣份额。并发容量会限制实际比例：同一款每批最多占一个位置；仅在轮换候选数超过轮换位置时启用加权；候选不足时保留原模式和并发数，避免所有候选同时运行而无法拉开时长差距。常驻项始终占位，不参与加权。

评分只查询已批准的轮换候选，通过官方 [GetAppReviews](https://partner.steamgames.com/doc/webapi/IUserReviewsService) 获取全语言、Steam 购买口径的公开评价汇总，默认过滤无关评测；不需要 API Key，不发送 SteamID 或账号凭据。同一 ASF 配置目录共享 `SmartIdle-ratings/ratings.json` 缓存，默认每周异步分批刷新；`RefreshHours: 0` 只关闭游戏库定时读取。查询失败保留旧评分，没有可用数据时使用中性分数 50；缓存损坏时保留原文件、使用内存缓存并提示警告，评分服务异常不会停止挂机。请求在整个 ASF 进程中串行执行，开始间隔至少 2 秒；大游戏库首次查询需要较长时间，已获取的评分会逐款生效。评分请求使用 .NET 默认 HTTP 网络和进程代理设置。

```text
!SIDLE BotName WEIGHT 620 95
!SIDLE BotName WEIGHT 620 AUTO
!SIDLE BotName RATINGS
!SIDLE BotName WHY 620
```

`WEIGHT` 为单个 AppID 保存手动分数，`AUTO` 移除覆盖并恢复自动计算；手动设置不会批准游戏或绕过禁止名单。`RATINGS` 安排异步刷新评分，手动请求至少间隔 5 分钟，仍遵守服务退避，不代表立即完成。`WHY` 显示评分来源、样本、自动分、手动覆盖、最终分、强度与缓存时间；`STATUS` 显示评分刷新状态。分数变化在后续批次生效，现有批次继续运行。

### 原生播放与还原

例：无自定义状态时，32 个游戏位置中常驻 10 款，剩余 22 位轮换；有自定义状态时为 10＋21 款。设置或取消手动常驻：

```text
!SIDLE BotName PIN 570,730
!SIDLE BotName UNPIN 730
```

安装时只合并新增的 `SmartIdle` 对象，保留已有 ASF 设置。启用后，插件先把原始 `GamesPlayedWhileIdle` 保存到 ASF 配置目录下的 `SmartIdle-native/<BotName>.json`，再将选中的常驻和轮换批次写入该机器人的 `GamesPlayedWhileIdle`。默认每 120 分钟有效运行时间换一批；列表没有变化时不重复写配置。`CustomGamePlayedWhileIdle`、`FarmingPreferences`、凭据及其他配置值保持不变。播放状态、自定义文案和挂卡由 ASF 自身处理。

每次实际修改游戏列表后，ASF 会重载该机器人的配置，账号可能短暂断开并重连；其他机器人不需要重连。恢复时间取决于 Steam 和网络，不能保证固定秒数。插件内部换批重载会保留重载前的挂卡暂停状态；完整重启 ASF 仍遵循原有启动策略。挂卡运行或本人占用期间，插件暂缓更新空闲列表。

`SIDLE BotName PAUSE` 先持久暂停插件并恢复备份中的原空闲列表；`SIDLE BotName RESUME` 重新管理列表。暂停插件不是停止所有 ASF 挂机，原空闲名单仍可能继续播放。使用 ASF 原生 `pause`／`play` 命令前先执行 `SIDLE PAUSE`；插件 `RESUME` 不会解除 ASF 挂卡模块的暂停。配置为 `FarmingPausedByDefault` 时，插件可在该启动暂停期间管理空闲播放。

卸载前使用 `SIDLE BotName RESTORE`，恢复原列表并把此机器人的 `SmartIdle.Enabled` 设为 `false`。如果接管期间用户已自行修改 `GamesPlayedWhileIdle`，还原会保留用户新名单并解除管理，不用备份覆盖它；备份损坏时停止还原并保留当前配置，需先检查备份。

### 命令

以下命令均要求目标机器人的 Master 或 Owner 权限。表内省略可选的 `BotName`，使用 IPC 时在 `SIDLE` 后加上名称。多个 AppID 可用空格、逗号或分号分隔。

| 命令 | 用途 |
| --- | --- |
| `SIDLE HELP` / `SIDLE STATUS` | 帮助 / 当前状态 |
| `SIDLE SCAN` | 安排刷新游戏库；候选仅进入待加入 |
| `SIDLE RATINGS` | 在 `QualityWeighted` 模式安排异步刷新评分；至少间隔 5 分钟，并遵守服务退避 |
| `SIDLE WEIGHT <AppID> <1..100\|AUTO>` | 保存手动权重 / 恢复自动评分；不批准游戏、不绕过禁止名单 |
| `SIDLE LIST PENDING [页码]` | 待加入清单，每页 10 款 |
| `SIDLE LIST APPROVED\|PINNED\|EXCLUDED\|REJECTED\|ALL [页码]` | 其他清单，选择一个类型 |
| `SIDLE APPROVE ALL` / `SIDLE APPROVE <AppID...>` | 批准所有待加入 / 指定合格游戏 |
| `SIDLE REJECT <AppID...>` | 拒绝并移出批准清单；不再自动提示 |
| `SIDLE PIN <AppID...>` / `SIDLE UNPIN <AppID...>` | 手动常驻 / 取消；取消后仅已批准且有时长的项目回轮换 |
| `SIDLE EXCLUDE <AppID...>` / `SIDLE UNEXCLUDE <AppID...>` | 加入 / 移出运行时禁止名单 |
| `SIDLE WHY <AppID...>` / `SIDLE PREVIEW` | 解释筛选与评分依据 / 查看候选批次和余下队列 |
| `SIDLE PAUSE` / `SIDLE RESUME` | 暂停并恢复原名单 / 重新管理空闲列表 |
| `SIDLE RESTORE` | 等待安全空闲后恢复原名单并禁用插件，用于卸载 |
| `SIDLE TRACE` | 临时采样 15 秒实际发送的播放消息；只记录游戏列表和文案，不记录其他流量 |
| `SIDLE ROTATE` | 手动选择下一批，保留常驻游戏 |

移出运行时禁止名单不会移除配置 `Blacklist` 的限制，也不会自动批准游戏。`PREVIEW` 展示候选播放列表，不能证明 Steam 已接受播放或计入时长。

### 构建与测试

在 macOS 或 Linux 安装 .NET 10 SDK、Python 3 和 curl，然后在项目根目录运行：

```sh
./scripts/build.sh
```

脚本从官方发布下载固定的 ASF 6.3.10.6 generic 包，验证 SHA512，先编译并运行测试，再生成安装包。引用依赖存放在 macOS 的 `~/Library/Caches/asf-smart-idle/6.3.10.6/` 或 Linux 的 `${XDG_CACHE_HOME:-$HOME/.cache}/asf-smart-idle/6.3.10.6/`。安装包不包含 ASF、.NET、测试探针或游戏文件，默认输出到 `~/Documents/Codex/outputs/asf-smart-idle/v0.4.0/SmartIdle.zip`。

运行 `python3 scripts/smoke-test.py` 可在真实 generic 宿主中执行加载与清单读写探针；它使用临时目录、禁用的合成机器人和仅监听本机的 IPC，不登录 Steam。GitHub Actions 还会用官方 `linux-x64` 裁剪发布包执行同一检查，避免仅在完整运行时通过。

可通过 `DOTNET` 指定 dotnet 可执行文件，通过 `OUTPUT_DIR` 指定安装包目录。设置 `ASF_REFERENCE_DIR` 时仍会将引用文件与官方固定版本校验。GitHub Actions 执行相同构建流程，只上传 artifact，不创建 Release、不发布安装。

## 说明

- 播放与自定义文案由 ASF 原生功能发送。插件不包含 Harmony，也不修改 ASF 程序；配置重载和 Steam 好友状态刷新可能造成短暂空档，离线和暂停时间不计入本地运行估算。

- 游戏库和时长通过机器人现有 Steam 连接读取，不需要 Web API Key。默认 `RefreshHours: 0`，已有快照时重启、重连、换批均不重新读取游戏库；`SCAN` 可手动校准和发现新候选。只有 `QualityWeighted` 模式额外查询公开评价接口。登录、保活和发送播放状态等基础通信仍然存在。
- 没有实际启动或下载游戏。Steam 时长可能延迟更新；插件计时只用于调度，不等于经 Steam 确认的新增时长。
- 首次没有快照且读取失败时每 5 分钟重试；已有快照的手动刷新失败则继续使用旧数据，手动模式不自动重试。关闭自动刷新意味着之后的真实游玩时长或许可证变化要等手动刷新才会反映。本人游玩结束后等待 ASF 的占用冷却再恢复。
- 原空闲列表的备份保存在 ASF 配置目录下的 `SmartIdle-native/<BotName>.json`；原字段不存在与显式空数组会分别保留，还原时恢复原有形式。重命名机器人配置时，应同时重命名对应的名单备份。
- 每个账号的状态保存在 ASF 配置目录下的 `SmartIdle-state/<SteamID>.json`，包含游戏名称、时长、批准和待加入清单、常驻、队列、暂停状态、手动权重与加权分配记录。按 SteamID 隔离，不以机器人名称共享清单；公开评分缓存单独按配置目录共享。
- 只在需要的机器人配置中启用 `SmartIdle.Enabled`；默认关闭。同一配置目录中，同一个 Steam 账号只能由一个 SmartIdle 机器人管理，重复实例会拒绝接管。
- 状态原子写入，每 5 分钟检查点保存，并在清单变更、换批和正常退出时保存。强制终止可能让最后未保存的约 5 分钟重复计时；断电时的持久性还取决于文件系统。
- 状态损坏、版本不兼容或无法写入时停止调度并保留原文件。不要删除状态来排障，除非确实需要重新建立批准清单；备份时保存整个账号状态文件。
- 日志写入 ASF 现有日志系统，保留策略由 ASF 配置负责。插件不提供独立数据库或网页服务。
- 卸载顺序：在插件仍安装时执行 `SIDLE BotName RESTORE`，确认原名单恢复且插件已禁用，再停止 ASF、删除 `plugins/SmartIdle/` 并移除机器人中的 `SmartIdle` 配置，最后重启。保留 `SmartIdle-state/` 和 `SmartIdle-native/` 便于恢复；不要先删除原名单备份。

## 版权说明

本项目未提供开源许可，原创代码保留所有权利。详见 [LICENSE](./LICENSE)。ArchiSteamFarm 和 SteamKit2 为独立第三方依赖，分别遵循其上游条款；安装包不重新分发这些宿主依赖。项目不是 Valve 或 ASF 的官方插件。
