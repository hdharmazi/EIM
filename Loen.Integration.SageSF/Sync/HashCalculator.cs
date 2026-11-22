using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Loen.Integration.SageSF.Sync;

/// <summary>
/// SHA-256 hash of a canonicalised mapped payload for echo suppression (§7).
/// Fields are sorted by key, normalised (trimmed strings, 'NULL'/'.'→null,
/// fixed decimal scale, ISO dates), then hashed.
/// </summary>
public sealed class HashCalculator
{
    public string Compute(Dictionary<string, object?> fields)
    {
        var canonical = Canonicalise(fields);
        var json = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    private static SortedDictionary<string, object?> Canonicalise(
        Dictionary<string, object?> fields)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            sorted[key] = NormaliseValue(value);
        }
        return sorted;
    }

    private static object? NormaliseValue(object? value)
    {
        if (value is null) return null;

        if (value is string s)
        {
            s = s.Trim();
            if (s is "NULL" or "." or "") return null;
            return s;
        }

        if (value is decimal d)
            return decimal.Round(d, 4);

        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd");

        return value;
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
