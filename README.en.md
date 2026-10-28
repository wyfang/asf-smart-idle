# ASF SmartIdle

An ArchiSteamFarm plugin with manual pins and a fixed approved rotation pool, using ASF’s native `GamesPlayedWhileIdle` playback with lowest-playtime, random and round-robin selection.

[Build downloads](https://github.com/wyfang/asf-smart-idle/actions/workflows/build.yml) · [简体中文](./README.md)

## Features

- The initial library scan puts games with recorded playtime into a pending list. Rotation only uses explicitly approved games.
- By default only the initial scan runs. Restarts, reconnects and rotation reuse the local snapshot; newcomers discovered by manual scans remain pending.
- Manual pins bypass zero-playtime and library-discovery filters; verify their licenses yourself. The chosen strategy fills the remaining slots: up to 32 games without a custom status, or 31 with it.
- Includes exclusions, per-game explanations, batch previews, manual rotation, and persistent queue and pause state.
- Manages ASF’s native idle playlist for selected bots, preserving custom text, farming preferences and other account settings; backs up the original playlist for restoration.
- Does not update the idle list during card farming or personal gameplay. Interruptions do not consume the batch timer; changed batches reload the selected bot’s configuration and briefly reconnect it.
- Does not detect free games, query store prices, or distinguish personal gameplay from historical ASF card-farming time.

## Usage

### Requirements and installation

Current version: **0.3.0**, targeting **ASF 6.3.10.6 / .NET 10**. Loading and isolated smoke tests passed in generic and trimmed `linux-x64` hosts. Native idle-list rotation, outgoing custom text, original-list backup and restoration, and yielding to card farming were also verified with a live account in the official `linux-x64` Docker image. Farming checks covered starting, continuing, stopping, and deferring restoration during farming; a full card-drop cycle was not observed. Other ASF versions and long-running operation remain unverified.

1. Use the build command below to create `SmartIdle.zip`. If downloading a GitHub Actions artifact, extract the outer artifact archive first.
2. Stop ASF and extract `SmartIdle.zip` into its `plugins/` directory. The resulting paths must include `plugins/SmartIdle/SmartIdle.dll` and `plugins/SmartIdle/SmartIdle.Core.dll`.
3. Merge the following fields into the target bot's JSON configuration, retaining its existing account and other settings. See also the [configuration fragment](./examples/bot.config.fragment.json).

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

4. Start ASF, confirm that SmartIdle loaded in the log, and wait for the bot to log in and retrieve its library.
5. Execute commands with Master or Owner access to the target bot. Replace `BotName` below with the bot's name, which is its configuration filename without `.json`. Steam chat uses the default `!` prefix; omit that prefix at the IPC command endpoint.

```text
!SIDLE BotName SCAN
!SIDLE BotName STATUS
!SIDLE BotName LIST PENDING
!SIDLE BotName APPROVE ALL
!SIDLE BotName PREVIEW
```

`SCAN` schedules an asynchronous refresh, with at least 5 minutes between manual requests. The initial successful scan happens automatically. Inspect `STATUS` and the paginated pending list first. `APPROVE ALL` approves every pending game at that time; use `APPROVE 570,730` for individual games. Rotation uses approved games; manual pins are a separate explicit choice. The command format is `SIDLE [BotName] <action>`; omitting the name targets the receiving bot. Specify the name explicitly through IPC, for example `SIDLE BotName STATUS`.

### Configuration

| Field | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Enable the plugin for this bot |
| `ConcurrentGames` | `32` | Requested game slots, 1–32 including pins; effective limit is 31 with custom status |
| `SelectionMode` | `LeastPlaytime` | `LeastPlaytime`, `Random`, or `RoundRobin` |
| `RotationMinutes` | `120` | Active minutes per batch, 5–525600 |
| `RefreshHours` | `0` | 0 disables periodic scans and reuses snapshots across restart/reconnect; 1–8760 enables an interval in hours |
| `PinnedGames` | `[]` | Explicit manual AppIDs; no prior approval or recorded playtime required; licenses are not automatically acquired |
| `Blacklist` | `[]` | Configuration exclusions, which take precedence |

Configuration fields are case-sensitive; unknown fields fail validation. Pins must fit the effective game capacity and cannot conflict with exclusions. Pins always occupy their transmitted slots, but actual Steam credit depends on licenses and platform rules; missing library metadata is not proof of credited playtime. Configuration pins apply initially and whenever the list changes. Command pins survive restarts while the configured list remains unchanged.

`LeastPlaytime` ranks each new batch using the greater of reported snapshot time and the locally estimated cumulative time, breaking ties by queue order. High-playtime games may wait a long time. `Random` selects without duplicates within a batch, allowing repeats across batches. `RoundRobin` serves all eligible entries in sequence. Changing the mode reselects and resets the batch timer; restarting with the same mode preserves the batch.

Without a custom status, 10 pins leave 22 rotating slots out of 32; with a status, the split is 10 plus 21. Set or remove manual pins:

```text
!SIDLE BotName PIN 570,730
!SIDLE BotName UNPIN 730
```

During installation, merge only the new `SmartIdle` object and retain existing ASF settings. When enabled, the plugin first saves the original `GamesPlayedWhileIdle` to `SmartIdle-native/<BotName>.json` beneath ASF’s configuration directory, then writes the selected pins and rotating batch to that bot’s `GamesPlayedWhileIdle`. The default batch lasts 120 active minutes. An unchanged playlist does not trigger another configuration write. `CustomGamePlayedWhileIdle`, `FarmingPreferences`, credentials and other configuration values are preserved. ASF itself handles playback, custom text and card farming.

An actual playlist change makes ASF reload the selected bot’s configuration, which may briefly disconnect and reconnect that account. Other bots do not need to reconnect. Recovery depends on Steam and the network; no fixed duration is guaranteed. An internal batch reload preserves the previous farmer pause state; a complete ASF restart still follows its configured startup policy. Idle-list changes are deferred during card farming or personal occupation.

`SIDLE BotName PAUSE` persistently pauses the plugin and restores the backed-up idle list; `SIDLE BotName RESUME` manages the list again. Pausing the plugin does not stop all ASF idling: the original list may continue playing. Run `SIDLE PAUSE` before ASF’s native `pause`/`play` commands. Plugin `RESUME` does not unpause ASF’s farmer. With `FarmingPausedByDefault`, the plugin may manage idle playback during that configured startup pause.

Before uninstalling, run `SIDLE BotName RESTORE` to restore the original list and set that bot’s `SmartIdle.Enabled` to `false`. If the user has independently changed `GamesPlayedWhileIdle`, restoration preserves that new list and relinquishes management instead of overwriting it from the backup. A corrupt backup stops restoration and leaves the current configuration in place for inspection.

### Commands

All commands require Master or Owner access to the target bot. The table omits the optional `BotName`; insert it after `SIDLE` when using IPC. Separate multiple AppIDs with spaces, commas, or semicolons.

| Command | Effect |
| --- | --- |
| `SIDLE HELP` / `SIDLE STATUS` | Help / current status |
| `SIDLE SCAN` | Schedule a library refresh; candidates only become pending |
| `SIDLE LIST PENDING [page]` | Pending list, 10 games per page |
| `SIDLE LIST APPROVED\|PINNED\|EXCLUDED\|REJECTED\|ALL [page]` | Other lists; choose one category |
| `SIDLE APPROVE ALL` / `SIDLE APPROVE <AppID...>` | Approve all pending / specified eligible games |
| `SIDLE REJECT <AppID...>` | Reject and remove approval; suppress automatic pending reminders |
| `SIDLE PIN <AppID...>` / `SIDLE UNPIN <AppID...>` | Add / remove manual pins; removed pins only rotate if approved with recorded time |
| `SIDLE EXCLUDE <AppID...>` / `SIDLE UNEXCLUDE <AppID...>` | Add / remove runtime exclusions |
| `SIDLE WHY <AppID...>` / `SIDLE PREVIEW` | Explain eligibility / preview the batch and remaining queue |
| `SIDLE PAUSE` / `SIDLE RESUME` | Pause and restore the original list / manage the idle list again |
| `SIDLE RESTORE` | Restore the original list and disable the plugin before uninstalling |
| `SIDLE TRACE` | Temporarily sample 15 seconds of actual outgoing playback lists and custom text; unrelated traffic is not recorded |
| `SIDLE ROTATE` | Select the next batch while retaining pins |

Removing a runtime exclusion does not override configuration `Blacklist` entries or approve the game. `PREVIEW` shows a candidate playlist; it does not prove that Steam accepted playback or credited playtime.

### Build and tests

Install the .NET 10 SDK, Python 3, and curl on macOS or Linux, then run from the project root:

```sh
./scripts/build.sh
```

The script downloads the pinned official ASF 6.3.10.6 generic release and checks SHA512, then builds and runs tests before creating the installation archive. Reference dependencies are cached at `~/Library/Caches/asf-smart-idle/6.3.10.6/` on macOS or `${XDG_CACHE_HOME:-$HOME/.cache}/asf-smart-idle/6.3.10.6/` on Linux. The package contains no ASF, .NET, test probe, or game files. Its default destination is `~/Documents/Codex/outputs/asf-smart-idle/v0.3.0/SmartIdle.zip`.

Run `python3 scripts/smoke-test.py` to check loading and synthetic scheduling/persistence in the real generic host. It uses a temporary directory, disabled synthetic bots and loopback-only IPC, without logging in to Steam. GitHub Actions repeats the check with the official trimmed `linux-x64` release, catching incompatibilities hidden by a complete runtime.

Use `DOTNET` to specify the dotnet executable and `OUTPUT_DIR` to choose the package directory. An `ASF_REFERENCE_DIR` override is still checked against the official pinned files. GitHub Actions runs the same build and uploads an artifact; it does not create a Release or deploy the plugin.

## Notes

- ASF’s native functionality sends playback and custom text. The plugin includes no Harmony dependency and does not modify ASF’s program. Configuration reloads and Steam friend-state refresh may produce a brief gap; offline and paused time do not count toward local running estimates.

- Library/playtime data use the bot's existing Steam connection, without store lookups or Web API keys. With default `RefreshHours: 0`, an existing snapshot is reused across restarts, reconnects and rotations. Use `SCAN` to calibrate or discover newcomers. Basic login, heartbeat and playback-status traffic still exists.
- No games are launched or downloaded. Steam playtime can update late; the plugin timer schedules rotation and does not represent Steam-confirmed credited minutes.
- Initial scans retry after 5 minutes when no snapshot exists. A failed manual refresh with a cached snapshot retains it without automatic retry in manual mode. With automatic refresh disabled, later real playtime or license changes only appear after a manual scan. ASF's occupation cooldown is observed after personal gameplay ends.
- Original idle-list backups are stored at `SmartIdle-native/<BotName>.json` beneath ASF’s configuration directory. An absent field and an explicit empty array are distinguished and restored in their original form. When renaming a bot configuration, rename its playlist backup as well.
- Per-account state is stored at `SmartIdle-state/<SteamID>.json` beneath ASF's configuration directory. It contains game names and playtime, approved and pending lists, pins, queue, and pause state. Lists are bound to the SteamID rather than shared by bot name.
- Enable `SmartIdle.Enabled` only in the bot configurations you select; it defaults to off. Only one SmartIdle bot may manage the same Steam account within a configuration directory. Duplicate instances refuse to take control.
- State writes are atomic. Checkpoints occur every 5 minutes and on list changes, rotation, and normal shutdown. A forced termination can repeat roughly the last 5 unsaved minutes of the batch; power-loss durability also depends on the filesystem.
- Corrupt or incompatible state, or a write failure, stops scheduling and preserves the existing file. Do not delete state while troubleshooting unless you intend to rebuild the approved list. Back up the complete account state file.
- Logs use ASF's existing logging system and retention settings. The plugin does not run a separate database or web service.
- To uninstall, first run `SIDLE BotName RESTORE` while the plugin is still installed. Confirm that the original list is restored and the plugin is disabled, then stop ASF, remove `plugins/SmartIdle/` and the bot’s `SmartIdle` configuration, and restart. Retain `SmartIdle-state/` and `SmartIdle-native/` for recovery; do not delete the original-list backup first.

## License

No open-source license is granted for this project; all rights to original code are reserved. See [LICENSE](./LICENSE). ArchiSteamFarm and SteamKit2 are separate third-party dependencies governed by their upstream terms; the installation package does not redistribute those host dependencies. This is not an official Valve or ASF plugin.
