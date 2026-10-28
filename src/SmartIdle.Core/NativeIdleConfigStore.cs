using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartIdle.Core;

// Manages only the native idle-list field. ASF remains the sole Steam playback
// writer and notices successful replacements through its configuration watcher.
public static class NativeIdleConfigStore {
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;
    private const string GamesProperty = "GamesPlayedWhileIdle";

    public static string BackupPath(string configPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        string fullPath = Path.GetFullPath(configPath);
        return Path.Combine(Path.GetDirectoryName(fullPath)!, "SmartIdle-native", Path.GetFileName(fullPath));
    }

    public static async Task<bool> ApplyAsync(string configPath, IReadOnlyCollection<uint> games, CancellationToken cancellationToken = default, Func<bool>? canCommit = null) {
        CancellationToken token = cancellationToken;
        ArgumentNullException.ThrowIfNull(games);
        var requested = new List<uint>();
        var distinct = new HashSet<uint>();
        foreach (uint id in games) {
            if (id == 0 || !distinct.Add(id)) throw new ArgumentException("原生播放清单不能包含 AppID 0 或重复项。", nameof(games));
            requested.Add(id);
            if (requested.Count > 32) throw new ArgumentException("原生播放清单不能超过 32 款游戏。", nameof(games));
        }
        string path = NormalizePath(configPath);
        var gate = Gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var snapshot = await ReadConfigAsync(path, token).ConfigureAwait(false);
            using var document = snapshot.Document;
            string backupPath = BackupPath(path);
            byte[]? priorBackupBytes = await ReadOptionalAsync(backupPath, token).ConfigureAwait(false);
            var backup = ReadBackup(priorBackupBytes, snapshot.IdentityHash);
            if (backup is null || !backup.Managed) {
                backup = new NativeIdleBackup {
                    OriginalPresent = snapshot.GamesPresent,
                    OriginalGames = snapshot.Games,
                    IdentityHash = snapshot.IdentityHash
                };
            }
            if (backup.PendingGames is { } pending && Matches(snapshot.GamesPresent, snapshot.Games, pending)) {
                // The last process may have committed the config and exited before
                // finalizing the journal. The matching pending list is already applied.
                backup.LastAppliedGames = pending;
            }
            backup.Managed = true;
            backup.PendingGames = requested;
            byte[] backupBytes = WriteBackup(backup);
            bool changed = !Matches(snapshot.GamesPresent, snapshot.Games, requested);
            byte[] configBytes = changed ? WriteConfig(document.RootElement, true, requested, false) : snapshot.Bytes;
            EnsureBackupDirectory(backupPath);
            // The baseline is durable before a replacement can affect ASF.
            await WriteAtomicallyAsync(backupPath, backupBytes, PrivateFileMode, async () => {
                await VerifyUnchangedAsync(path, snapshot.Bytes, token).ConfigureAwait(false);
                await VerifyUnchangedAsync(backupPath, priorBackupBytes, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (changed) {
                await WriteAtomicallyAsync(path, configBytes, snapshot.Mode, async () => {
                    await VerifyUnchangedAsync(path, snapshot.Bytes, token).ConfigureAwait(false);
                    await VerifyUnchangedAsync(backupPath, backupBytes, token).ConfigureAwait(false);
                    RequireCommitPermission(canCommit);
                }, token).ConfigureAwait(false);
            }
            // Once the config is committed (or already matches), complete this tiny
            // journal update without shutdown cancellation. If it fails, PendingGames
            // still identifies the committed list so Restore can recover after restart.
            backup.LastAppliedGames = requested;
            backup.PendingGames = null;
            await WriteAtomicallyAsync(backupPath, WriteBackup(backup), PrivateFileMode, async () => {
                await VerifyUnchangedAsync(path, configBytes, CancellationToken.None).ConfigureAwait(false);
                await VerifyUnchangedAsync(backupPath, backupBytes, CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            return changed;
        } finally { gate.Release(); }
    }

    public static async Task<bool> RestoreAsync(string configPath, bool disablePlugin = false, CancellationToken cancellationToken = default, Func<bool>? canCommit = null) {
        CancellationToken token = cancellationToken;
        string path = NormalizePath(configPath);
        var gate = Gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var snapshot = await ReadConfigAsync(path, token).ConfigureAwait(false);
            using var document = snapshot.Document;
            string backupPath = BackupPath(path);
            byte[]? priorBackupBytes = await ReadOptionalAsync(backupPath, token).ConfigureAwait(false);
            var backup = ReadBackup(priorBackupBytes, snapshot.IdentityHash);
            bool restoreGames = backup is { Managed: true } &&
                (Matches(snapshot.GamesPresent, snapshot.Games, backup.LastAppliedGames) ||
                 (backup.PendingGames is { } pending && Matches(snapshot.GamesPresent, snapshot.Games, pending)) ||
                 SameField(snapshot.GamesPresent, snapshot.Games, backup.OriginalPresent, backup.OriginalGames));
            bool gamesPresent = restoreGames ? backup!.OriginalPresent : snapshot.GamesPresent;
            IReadOnlyCollection<uint> games = restoreGames ? backup!.OriginalGames : snapshot.Games;
            bool changed = !SameField(snapshot.GamesPresent, snapshot.Games, gamesPresent, games) ||
                (disablePlugin && !PluginDisabled(document.RootElement));
            byte[] configBytes = changed ? WriteConfig(document.RootElement, gamesPresent, games, disablePlugin) : snapshot.Bytes;
            if (changed) {
                await WriteAtomicallyAsync(path, configBytes, snapshot.Mode, async () => {
                    await VerifyUnchangedAsync(path, snapshot.Bytes, token).ConfigureAwait(false);
                    await VerifyUnchangedAsync(backupPath, priorBackupBytes, token).ConfigureAwait(false);
                    RequireCommitPermission(canCommit);
                }, token).ConfigureAwait(false);
            }
            if (backup is { Managed: true }) {
                // If another actor changed the idle list, retain its value and simply
                // release management. The next Apply captures that as the new baseline.
                backup.Managed = false;
                backup.PendingGames = null;
                byte[] backupBytes = WriteBackup(backup);
                EnsureBackupDirectory(backupPath);
                // Once the config replacement commits, finish its bookkeeping even if
                // shutdown cancellation arrives between these two file replacements.
                var completionToken = changed ? CancellationToken.None : token;
                await WriteAtomicallyAsync(backupPath, backupBytes, PrivateFileMode, async () => {
                    await VerifyUnchangedAsync(path, configBytes, completionToken).ConfigureAwait(false);
                    await VerifyUnchangedAsync(backupPath, priorBackupBytes, completionToken).ConfigureAwait(false);
                }, completionToken).ConfigureAwait(false);
            }
            return changed;
        } finally { gate.Release(); }
    }

    public static async Task<bool> MatchesAsync(string configPath, IReadOnlyCollection<uint> games, CancellationToken cancellationToken = default) {
        CancellationToken token = cancellationToken;
        ArgumentNullException.ThrowIfNull(games);
        string path = NormalizePath(configPath);
        var gate = Gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var snapshot = await ReadConfigAsync(path, token).ConfigureAwait(false);
            using var document = snapshot.Document;
            ReadBackup(await ReadOptionalAsync(BackupPath(path), token).ConfigureAwait(false), snapshot.IdentityHash);
            return Matches(snapshot.GamesPresent, snapshot.Games, games);
        } finally { gate.Release(); }
    }

    private static string NormalizePath(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static async Task<ConfigSnapshot> ReadConfigAsync(string path, CancellationToken token) {
        byte[] bytes = await ReadBytesAsync(path, token).ConfigureAwait(false);
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException exception) { throw new InvalidDataException("ASF 机器人配置不是有效 JSON，保留原文件。", exception); }
        try {
            RequireObject(document.RootElement, "ASF 机器人配置");
            bool present = TryField(document.RootElement, GamesProperty, out var value);
            var games = present ? ReadGames(value, GamesProperty) : new List<uint>();
            string login = "";
            if (TryField(document.RootElement, "SteamLogin", out var loginValue)) {
                if (loginValue.ValueKind is not JsonValueKind.String and not JsonValueKind.Null) throw new InvalidDataException("SteamLogin 字段类型无效。");
                login = loginValue.GetString() ?? "";
            }
            string identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(login)));
            UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
            return new ConfigSnapshot(bytes, document, present, games, identityHash, mode);
        } catch { document.Dispose(); throw; }
    }

    private static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken token) {
        try { return await ReadBytesAsync(path, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static Task<byte[]> ReadBytesAsync(string path, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        // ASF's trimmed single-file host omits File.ReadAllBytesAsync. This exact
        // FileStream constructor and its fundamental Read override are preserved.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > int.MaxValue) throw new IOException("ASF 配置文件过大，拒绝读取。");
        var bytes = new byte[(int) stream.Length];
        int offset = 0;
        while (offset < bytes.Length) {
            token.ThrowIfCancellationRequested();
            int count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException("ASF 配置文件在读取期间被截断。");
            offset += count;
        }
        token.ThrowIfCancellationRequested();
        return Task.FromResult(bytes);
    }

    private static NativeIdleBackup? ReadBackup(byte[]? bytes, string identityHash) {
        if (bytes is null) return null;
        try {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            RequireObject(root, "SmartIdle 原生配置备份");
            var fields = new HashSet<string>(StringComparer.Ordinal) {
                "SchemaVersion", "OriginalPresent", "OriginalGames", "LastAppliedGames", "PendingGames", "Managed", "IdentityHash"
            };
            foreach (var property in root.EnumerateObject()) {
                if (!fields.Remove(property.Name)) throw new InvalidDataException("SmartIdle 原生配置备份包含未知字段。");
            }
            fields.Remove("PendingGames"); // Optional journal addition supports older schema-1 backups.
            if (fields.Count != 0 || RequireField(root, "SchemaVersion").GetInt32() != 1) throw new InvalidDataException("SmartIdle 原生配置备份缺少字段或版本不支持。");
            var backup = new NativeIdleBackup {
                OriginalPresent = RequireField(root, "OriginalPresent").GetBoolean(),
                OriginalGames = ReadGames(RequireField(root, "OriginalGames"), "OriginalGames"),
                LastAppliedGames = ReadGames(RequireField(root, "LastAppliedGames"), "LastAppliedGames"),
                Managed = RequireField(root, "Managed").GetBoolean(),
                IdentityHash = RequireField(root, "IdentityHash").GetString() ?? ""
            };
            if (TryField(root, "PendingGames", out var pendingValue) && pendingValue.ValueKind != JsonValueKind.Null) {
                backup.PendingGames = ReadGames(pendingValue, "PendingGames");
            }
            if ((!backup.OriginalPresent && backup.OriginalGames.Count != 0) || backup.IdentityHash.Length != 64) throw new InvalidDataException("SmartIdle 原生配置备份内容无效。");
            ValidateManagedGames(backup.LastAppliedGames);
            if (backup.PendingGames is not null) ValidateManagedGames(backup.PendingGames);
            foreach (char value in backup.IdentityHash) {
                if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) throw new InvalidDataException("SmartIdle 原生配置备份身份摘要无效。");
            }
            if (backup.IdentityHash != identityHash) throw new InvalidDataException("SteamLogin 与原生配置备份身份不符，拒绝自动覆盖配置或备份。");
            return backup;
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException) {
            throw new InvalidDataException("SmartIdle 原生配置备份损坏，保留原配置和备份。", exception);
        }
    }

    private static List<uint> ReadGames(JsonElement value, string field) {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{field} 必须是 AppID 数组。");
        var games = new List<uint>();
        foreach (var element in value.EnumerateArray()) {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out uint id)) throw new InvalidDataException($"{field} 包含无效 AppID。");
            games.Add(id);
        }
        return games;
    }

    private static void ValidateManagedGames(IReadOnlyCollection<uint> games) {
        var ids = new HashSet<uint>();
        if (games.Count > 32) throw new InvalidDataException("SmartIdle 原生配置备份中的托管清单超过 32 款。");
        foreach (uint id in games) {
            if (id == 0 || !ids.Add(id)) throw new InvalidDataException("SmartIdle 原生配置备份中的托管清单无效。");
        }
    }

    private static bool TryField(JsonElement root, string name, out JsonElement value) {
        foreach (var property in root.EnumerateObject()) {
            if (property.Name == name) {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static JsonElement RequireField(JsonElement root, string name) =>
        TryField(root, name, out var value) ? value : throw new InvalidDataException($"SmartIdle 原生配置备份缺少字段：{name}。");

    private static void RequireObject(JsonElement value, string description) {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{description} 必须是 JSON 对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) {
            if (!names.Add(property.Name)) throw new InvalidDataException($"{description} 含有重复字段，拒绝自动改写。");
        }
    }

    private static bool Matches(bool present, IReadOnlyCollection<uint> current, IReadOnlyCollection<uint> requested) =>
        present && new HashSet<uint>(current).SetEquals(requested);

    private static bool SameField(bool present, IReadOnlyCollection<uint> current, bool expectedPresent, IReadOnlyCollection<uint> expected) =>
        present == expectedPresent && (!present || new HashSet<uint>(current).SetEquals(expected));

    private static bool PluginDisabled(JsonElement root) {
        if (!TryField(root, "SmartIdle", out var plugin)) return false;
        RequireObject(plugin, "SmartIdle 配置");
        return TryField(plugin, "Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False;
    }

    private static byte[] WriteConfig(JsonElement root, bool gamesPresent, IReadOnlyCollection<uint> games, bool disablePlugin) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            bool wroteGames = false;
            bool wrotePlugin = false;
            foreach (var property in root.EnumerateObject()) {
                if (property.Name == GamesProperty) {
                    if (gamesPresent) WriteGames(writer, GamesProperty, games);
                    wroteGames = true;
                } else if (disablePlugin && property.Name == "SmartIdle") {
                    WriteDisabledPlugin(writer, property.Value);
                    wrotePlugin = true;
                } else {
                    WriteProperty(writer, property);
                }
            }
            if (gamesPresent && !wroteGames) WriteGames(writer, GamesProperty, games);
            if (disablePlugin && !wrotePlugin) {
                writer.WriteStartObject("SmartIdle");
                writer.WriteBoolean("Enabled", false);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteDisabledPlugin(Utf8JsonWriter writer, JsonElement plugin) {
        RequireObject(plugin, "SmartIdle 配置");
        writer.WriteStartObject("SmartIdle");
        bool wroteEnabled = false;
        foreach (var property in plugin.EnumerateObject()) {
            if (property.Name == "Enabled") {
                writer.WriteBoolean("Enabled", false);
                wroteEnabled = true;
            } else WriteProperty(writer, property);
        }
        if (!wroteEnabled) writer.WriteBoolean("Enabled", false);
        writer.WriteEndObject();
    }

    private static byte[] WriteBackup(NativeIdleBackup backup) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", 1);
            writer.WriteBoolean("OriginalPresent", backup.OriginalPresent);
            WriteGames(writer, "OriginalGames", backup.OriginalGames);
            WriteGames(writer, "LastAppliedGames", backup.LastAppliedGames);
            if (backup.PendingGames is null) {
                writer.WritePropertyName("PendingGames");
                writer.WriteNullValue();
            }
            else WriteGames(writer, "PendingGames", backup.PendingGames);
            writer.WriteBoolean("Managed", backup.Managed);
            writer.WriteString("IdentityHash", backup.IdentityHash);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteGames(Utf8JsonWriter writer, string name, IReadOnlyCollection<uint> games) {
        writer.WriteStartArray(name);
        foreach (uint id in games) writer.WriteNumberValue((ulong) id);
        writer.WriteEndArray();
    }

    private static void WriteProperty(Utf8JsonWriter writer, JsonProperty property) {
        // The host retains JsonElement.WriteTo but removes JsonProperty.WriteTo.
        writer.WritePropertyName(property.Name);
        property.Value.WriteTo(writer);
    }

    private static void EnsureBackupDirectory(string backupPath) {
        string directory = Path.GetDirectoryName(backupPath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, PrivateDirectoryMode);
    }

    private static async Task VerifyUnchangedAsync(string path, byte[]? expected, CancellationToken token) {
        byte[]? current = await ReadOptionalAsync(path, token).ConfigureAwait(false);
        if (current is null ? expected is not null : expected is null || !current.AsSpan().SequenceEqual(expected)) {
            throw new IOException("配置或原生清单备份在准备提交期间被其他操作修改，已停止覆盖。");
        }
    }

    private static void RequireCommitPermission(Func<bool>? canCommit) {
        if (canCommit?.Invoke() == false) throw new OperationCanceledException("ASF 播放状态已改变，取消本次配置替换，等待安全时机重试。");
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, UnixFileMode? mode, Func<Task> verify, CancellationToken token) {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)) {
                if (!OperatingSystem.IsWindows() && mode.HasValue) File.SetUnixFileMode(temporary, mode.Value);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            await verify().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class NativeIdleBackup {
        public bool OriginalPresent;
        public List<uint> OriginalGames = [];
        public List<uint> LastAppliedGames = [];
        public List<uint>? PendingGames;
        public bool Managed;
        public string IdentityHash = "";
    }

    private sealed record ConfigSnapshot(byte[] Bytes, JsonDocument Document, bool GamesPresent, List<uint> Games, string IdentityHash, UnixFileMode? Mode);
}
