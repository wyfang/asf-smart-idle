namespace SmartIdle.Core;

// Tracks Steam occupation independently from playback ownership. Native idle
// restoration is coordinated at its semantic boundary, without a takeover timer.
// The coordinator serializes access to this state with its session lock.
public sealed class PlaybackLifecycle {
    public DateTimeOffset ReadyAfter { get; private set; }
    public bool Connected { get; private set; }
    public bool PlayingBlocked { get; private set; }
    public bool LibraryBlocked { get; private set; }

    public void OnConnected(DateTimeOffset now) {
        Connected = true;
        PlayingBlocked = false;
        LibraryBlocked = false;
        ReadyAfter = now;
    }

    public void OnDisconnected() => Connected = false;

    public bool OnPlayingBlocked(bool blocked, DateTimeOffset now, int cooldownSeconds) {
        if (PlayingBlocked == blocked) return false;
        PlayingBlocked = blocked;
        if (!blocked) ExtendAfterUnlock(now, cooldownSeconds);
        return true;
    }

    public bool OnLibraryBlocked(bool blocked, DateTimeOffset now, int cooldownSeconds) {
        if (LibraryBlocked == blocked) return false;
        LibraryBlocked = blocked;
        if (!blocked) ExtendAfterUnlock(now, cooldownSeconds);
        return true;
    }

    public bool CanPlay(DateTimeOffset now) => Connected && !PlayingBlocked && !LibraryBlocked && now >= ReadyAfter;

    private void ExtendAfterUnlock(DateTimeOffset now, int cooldownSeconds) =>
        ExtendUntil(now.AddSeconds(Math.Max(5, cooldownSeconds)));

    private void ExtendUntil(DateTimeOffset until) {
        if (until > ReadyAfter) ReadyAfter = until;
    }
}
