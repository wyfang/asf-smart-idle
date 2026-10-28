namespace SmartIdle.Core;

public static class PlaylistCapacity {
    public static int GameSlots(int requested, string? customName) => Math.Min(requested, string.IsNullOrEmpty(customName) ? 32 : 31);
}
