using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SmartIdle.Core;

public static class IdleJson {
    // ASF's trimmed host preserves this explicit resolver and its options-based overloads.
    // Plugin assemblies are not trimmed. Avoid source-context overloads removed by the host.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Strict) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
