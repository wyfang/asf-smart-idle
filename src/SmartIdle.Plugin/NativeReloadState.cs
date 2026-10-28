using System.Collections.Concurrent;

namespace SmartIdle;

// Preserve native card-farmer pause state across a plugin-initiated bot reload.
// This is process-local: a complete ASF restart keeps its configured startup policy.
internal static class NativeReloadState {
    private static readonly ConcurrentDictionary<string, bool> Pending = new(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    internal static void Mark(string configPath, bool paused) => Pending[Path.GetFullPath(configPath)] = paused;
    internal static void Cancel(string configPath) => Pending.TryRemove(Path.GetFullPath(configPath), out _);
    internal static bool? Take(string configPath) => Pending.TryRemove(Path.GetFullPath(configPath), out bool paused) ? paused : null;
}
