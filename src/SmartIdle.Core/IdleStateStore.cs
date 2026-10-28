using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartIdle.Core;

public static class IdleStateStore {
    private const long MaxStateBytes = 32 * 1024 * 1024;

    public static async Task<IdleState> LoadAsync(string path, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return new IdleState();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaxStateBytes) throw new InvalidDataException("SmartIdle 状态文件超过 32 MB，拒绝读取。");
        IdleState state;
        try {
            state = await JsonSerializer.DeserializeAsync<IdleState>(stream, IdleJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("SmartIdle 状态为空。");
        } catch (JsonException exception) {
            throw new InvalidDataException("SmartIdle 状态损坏或包含未知字段，已保留原文件。", exception);
        }
        state.Validate();
        return state;
    }

    public static async Task SaveAsync(string path, IdleState state, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)) {
                if (!OperatingSystem.IsWindows()) {
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                byte[] data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, IdleJson.Options));
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaxStateBytes) throw new InvalidDataException("SmartIdle 状态超过 32 MB，保留旧文件。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        } finally {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
