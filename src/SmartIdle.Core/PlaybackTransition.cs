namespace SmartIdle.Core;

// Steam needs a clean non-playing session before accepting a custom game name.
// Callers guard both callbacks with their session lock and recheck ownership on commit.
public static class PlaybackTransition {
    public const int CustomStatusResetMilliseconds = 500; // ASF 6.3.10.6 Bot.CallbackSleep

    public static async Task<bool> ApplyAsync(string? customName, Func<bool> prepare, Func<bool> commit,
        CancellationToken cancellationToken = default, Func<TimeSpan, CancellationToken, Task>? delay = null) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepare()) return false;
        if (!string.IsNullOrEmpty(customName)) {
            var duration = TimeSpan.FromMilliseconds(CustomStatusResetMilliseconds);
            await (delay is null ? Task.Delay(duration, cancellationToken) : delay(duration, cancellationToken)).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return commit();
    }
}
