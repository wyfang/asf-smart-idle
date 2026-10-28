namespace SmartIdle;

// Avoid LINQ convenience overloads that are removed from ASF's trimmed distributions.
internal static class Sequence {
    internal static bool Equal(IReadOnlyList<uint> left, IReadOnlyList<uint> right) {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }

    internal static string? Argument(string[] arguments, int index) => index < arguments.Length ? arguments[index] : null;

    internal static uint[] Distinct(IEnumerable<uint> source) {
        var seen = new HashSet<uint>();
        var result = new List<uint>();
        foreach (uint id in source) if (seen.Add(id)) result.Add(id);
        return result.ToArray();
    }
}
