# ASF SmartIdle

为 ArchiSteamFarm 提供手动常驻名单与固定清单轮换：轮换池排除累计时长为 0 的游戏，支持低时长优先、完全随机和公平队列。

[构建下载](https://github.com/wyfang/asf-smart-idle/actions/workflows/build.yml) · [English](./README.en.md)

## 功能

- 首次读取游戏库，有累计时长的游戏进入待加入清单；轮换只使用人工批准的游戏。
- 默认只在首次读取；之后的重启、重连和轮换使用本地快照，手动刷新后新候选仍须批准。
- 手动常驻不受零时长或游戏库遗漏限制，需自行确认许可证；剩余位置按选定策略填满。无自定义状态时最多 32 款，保留自定义状态时最多 31 款。
- 支持禁止名单、逐款解释、播放预览、手动换批，以及重启后继续队列和暂停状态。
- 保留 ASF 原有配置和自定义状态；挂卡或本人游玩期间让出调度，中断期间不消耗本批轮换时间。
- 不识别免费游戏，不查询商店价格，不区分本人游玩与 ASF 挂卡产生的历史时长。

## 使用

### 运行条件与安装

当前编译目标为 **ASF 6.3.10.6 / .NET 10**。已在 generic 宿主和 `linux-x64` Docker 中验证插件加载、清单调度与状态读写，并在真实账号上验证游戏库读取、常驻与手动换批、暂停恢复。另已在真实账号上验证：ASF 启动挂卡时插件让出播放，挂卡期间不抢占列表，轮换倒计时与本地时长估算冻结，恢复原配置后继续原批次；未等待整轮掉卡结束。其他 ASF 版本与长时间运行尚未验证。

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
| `SelectionMode` | `LeastPlaytime` | `LeastPlaytime` 低时长优先、`Random` 完全随机、`RoundRobin` 公平队列 |
| `RotationMinutes` | `120` | 每批有效运行分钟数，5～525600 |
| `RefreshHours` | `0` | 0 禁用定期读取，重启重连复用快照；1～8760 为可选定时刷新小时数 |
| `PinnedGames` | `[]` | 手动常驻 AppID，不必先批准，不受 0 时长筛选；不会自动获取许可证 |
| `Blacklist` | `[]` | 配置禁止名单，禁止名单优先 |

配置字段区分大小写，未知字段会使配置校验失败。常驻数量不能超过有效游戏并发数，也不能与禁止名单冲突；占满时没有轮换位置。常驻项始终占位发送，但 Steam 是否计时取决于其许可证和平台规则，缺少游戏库资料不等于已计时。配置常驻清单在首次使用或变化时生效；配置不变时，命令修改的常驻跨重启保留。

`LeastPlaytime` 在每次换批时按 Steam 快照时长与本地估算累计时长的较大值排序，相同时按队列先后选择；高时长游戏可能很久轮不到。`Random` 每批随机且不重复，跨批允许重复。`RoundRobin` 保证符合条件的清单依次轮到。模式改变会重新选择本批并重置计时，同模式重启保留当前批次。

例：无自定义状态时，32 个游戏位置中常驻 10 款，剩余 22 位轮换；有自定义状态时为 10＋21 款。设置或取消手动常驻：

```text
!SIDLE BotName PIN 570,730
!SIDLE BotName UNPIN 730
```

保留 `GamesPlayedWhileIdle`、`CustomGamePlayedWhileIdle`、`FarmingPreferences` 等已有 ASF 配置，只合并新增的 `SmartIdle` 对象。插件运行时接管空闲播放，保留自定义状态；暂停或卸载时原配置仍可使用。插件不会修改这些配置字段，也不主动暂停或恢复挂卡模块。配置为 `FarmingPausedByDefault` 时，插件可在这个启动暂停期间挂时长；若挂卡模块运行，则挂卡优先。

暂停和恢复插件请使用 `SIDLE BotName PAUSE` / `SIDLE BotName RESUME`。暂停插件会交还 ASF 原空闲播放列表，并不是停止所有 ASF 挂机。原生启动暂停与后续手动 `pause`／`play` 无法在所有情况下区分，使用这些原生命令前先 `SIDLE PAUSE`，避免插件重新接管；本人在客户端开始游玩则由 Steam 占用状态让位。插件 `RESUME` 不会解除 ASF 的暂停。

### 命令

以下命令均要求目标机器人的 Master 或 Owner 权限。表内省略可选的 `BotName`，使用 IPC 时在 `SIDLE` 后加上名称。多个 AppID 可用空格、逗号或分号分隔。

| 命令 | 用途 |
| --- | --- |
| `SIDLE HELP` / `SIDLE STATUS` | 帮助 / 当前状态 |
| `SIDLE SCAN` | 安排刷新游戏库；候选仅进入待加入 |
| `SIDLE LIST PENDING [页码]` | 待加入清单，每页 10 款 |
| `SIDLE LIST APPROVED\|PINNED\|EXCLUDED\|REJECTED\|ALL [页码]` | 其他清单，选择一个类型 |
| `SIDLE APPROVE ALL` / `SIDLE APPROVE <AppID...>` | 批准所有待加入 / 指定合格游戏 |
| `SIDLE REJECT <AppID...>` | 拒绝并移出批准清单；不再自动提示 |
| `SIDLE PIN <AppID...>` / `SIDLE UNPIN <AppID...>` | 手动常驻 / 取消；取消后仅已批准且有时长的项目回轮换 |
| `SIDLE EXCLUDE <AppID...>` / `SIDLE UNEXCLUDE <AppID...>` | 加入 / 移出运行时禁止名单 |
| `SIDLE WHY <AppID...>` / `SIDLE PREVIEW` | 解释筛选原因 / 查看候选批次和余下队列 |
| `SIDLE PAUSE` / `SIDLE RESUME` | 持久暂停 / 恢复插件 |
| `SIDLE ROTATE` | 手动选择下一批，保留常驻游戏 |

移出运行时禁止名单不会移除配置 `Blacklist` 的限制，也不会自动批准游戏。`PREVIEW` 展示候选播放列表，不能证明 Steam 已接受播放或计入时长。

### 构建与测试

在 macOS 或 Linux 安装 .NET 10 SDK、Python 3 和 curl，然后在项目根目录运行：

```sh
./scripts/build.sh
```

脚本从官方发布下载固定的 ASF 6.3.10.6 generic 包，验证 SHA512，先编译并运行测试，再生成安装包。引用依赖存放在 macOS 的 `~/Library/Caches/asf-smart-idle/6.3.10.6/` 或 Linux 的 `${XDG_CACHE_HOME:-$HOME/.cache}/asf-smart-idle/6.3.10.6/`。安装包不包含 ASF、.NET、测试探针或游戏文件，默认输出到 `~/Documents/Codex/outputs/asf-smart-idle/v0.2.1/SmartIdle.zip`。

运行 `python3 scripts/smoke-test.py` 可在真实 generic 宿主中执行加载与清单读写探针；它使用临时目录、禁用的合成机器人和仅监听本机的 IPC，不登录 Steam。GitHub Actions 还会用官方 `linux-x64` 裁剪发布包执行同一检查，避免仅在完整运行时通过。

可通过 `DOTNET` 指定 dotnet 可执行文件，通过 `OUTPUT_DIR` 指定安装包目录。设置 `ASF_REFERENCE_DIR` 时仍会将引用文件与官方固定版本校验。GitHub Actions 执行相同构建流程，只上传 artifact，不创建 Release、不发布安装。

## 说明

- 配置自定义文案时，启动、轮换和交还原有播放列表会先清空游戏状态，等待 500 毫秒后再发送文案和游戏列表，沿用 ASF 6.3.10.6 的 Steam 兼容处理。账号不下线；好友列表的实际刷新还受网络和客户端缓存影响。空档不计入本地时长估算或批次倒计时。无自定义文案时不主动等待。

- 游戏库和时长通过机器人现有 Steam 连接读取，不查询商店、不需要 Web API Key。默认 `RefreshHours: 0`，已有快照时重启、重连、换批均不重新查询；`SCAN` 可手动校准和发现新候选。登录、保活和发送播放状态等基础通信仍然存在。
- 没有实际启动或下载游戏。Steam 时长可能延迟更新；插件计时只用于调度，不等于经 Steam 确认的新增时长。
- 首次没有快照且读取失败时每 5 分钟重试；已有快照的手动刷新失败则继续使用旧数据，手动模式不自动重试。关闭自动刷新意味着之后的真实游玩时长或许可证变化要等手动刷新才会反映。本人游玩结束后等待 ASF 的占用冷却再恢复。
- 每个账号的状态保存在 ASF 配置目录下的 `SmartIdle-state/<SteamID>.json`，包含游戏名称、时长、批准和待加入清单、常驻、队列及暂停状态。按 SteamID 隔离，不以机器人名称共享清单。
- 只在需要的机器人配置中启用 `SmartIdle.Enabled`；默认关闭。同一配置目录中，同一个 Steam 账号只能由一个 SmartIdle 机器人管理，重复实例会拒绝接管。
- 状态原子写入，每 5 分钟检查点保存，并在清单变更、换批和正常退出时保存。强制终止可能让最后未保存的约 5 分钟重复计时；断电时的持久性还取决于文件系统。
- 状态损坏、版本不兼容或无法写入时停止调度并保留原文件。不要删除状态来排障，除非确实需要重新建立批准清单；备份时保存整个账号状态文件。
- 日志写入 ASF 现有日志系统，保留策略由 ASF 配置负责。插件不提供独立数据库或网页服务。
- 卸载时停止 ASF，删除 `plugins/SmartIdle/` 并移除机器人里的 `SmartIdle` 配置，然后重启。保留状态目录便于恢复；重新启用后会继续使用原批准清单。

## 版权说明

本项目未提供开源许可，原创代码保留所有权利。详见 [LICENSE](./LICENSE)。ArchiSteamFarm 和 SteamKit2 为独立第三方依赖，分别遵循其上游条款；安装包不重新分发这些宿主依赖。项目不是 Valve 或 ASF 的官方插件。
