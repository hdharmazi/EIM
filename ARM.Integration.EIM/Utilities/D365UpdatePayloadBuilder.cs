using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ARM.Integration.EIM.Utilities;

/// <summary>
/// Composes the JSON body for a D365 FSC Update (PATCH) request by removing every property
/// marked with <see cref="D365ExcludeOnUpdateAttribute"/> from the serialized entity.
/// </summary>
public static class D365UpdatePayloadBuilder
{
    private static readonly ConcurrentDictionary<Type, string[]> ExcludedNamesByType = new();

    /// <summary>
    /// Returns the wire names of all properties on <paramref name="entityType"/> marked with
    /// <see cref="D365ExcludeOnUpdateAttribute"/>. The wire name is the property's
    /// <see cref="JsonPropertyNameAttribute"/> value when present, otherwise the CLR property name.
    /// Resolved once per type and cached.
    /// </summary>
    public static IReadOnlyList<string> GetExcludedFieldNames(Type entityType) =>
        ExcludedNamesByType.GetOrAdd(entityType, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(D365ExcludeOnUpdateAttribute), inherit: true))
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name ?? p.Name)
            .ToArray());

    /// <summary>
    /// Serializes <paramref name="entity"/> and removes the excluded fields from the result.
    /// Property matching is case-insensitive so that a casing difference between the attribute
    /// name and the emitted JSON cannot cause a silent miss.
    /// </summary>
    /// <returns>The filtered JSON body and the names of the fields actually removed.</returns>
    public static (string Json, IReadOnlyList<string> RemovedFields) BuildUpdatePayload<T>(T entity)
        where T : class
    {
        var json = JsonSerializer.Serialize(entity);

        var excluded = GetExcludedFieldNames(typeof(T));
        if (excluded.Count == 0)
            return (json, []);

        if (JsonNode.Parse(json) is not JsonObject payload)
            return (json, []);

        var removed = new List<string>();

        foreach (var name in excluded)
        {
            // JsonObject lookup is ordinal; match case-insensitively against the emitted names.
            var actual = payload
                .Select(p => p.Key)
                .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            if (actual is not null && payload.Remove(actual))
                removed.Add(actual);
        }

        return removed.Count == 0
            ? (json, [])
            : (payload.ToJsonString(), removed);
    }
}
