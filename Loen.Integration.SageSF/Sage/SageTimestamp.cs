namespace Loen.Integration.SageSF.Sage;

/// <summary>
/// Converts between .NET DateTime and Sage's two-part timestamp
/// (DateUpdated as date-only, TimeUpdated as varchar decimal hours).
/// </summary>
public static class SageTimestamp
{
    /// <summary>
    /// Splits a watermark DateTime into Sage's (date, decimal-hours) pair.
    /// Subtracts <paramref name="overlapSec"/> to re-read records near the boundary.
    /// Returns (DateTime.MinValue, 0) for a null watermark (first run = full load).
    /// </summary>
    public static (DateTime Date, decimal Time) FromDateTime(DateTime? watermark, int overlapSec)
    {
        if (watermark is null)
            return (DateTime.MinValue, 0m);

        var adjusted = watermark.Value.AddSeconds(-overlapSec);
        var date = adjusted.Date;
        var hours = (decimal)adjusted.TimeOfDay.TotalHours;
        return (date, Math.Round(hours, 5));
    }

    /// <summary>
    /// Reconstructs a DateTime from a Sage record's DateUpdated + TimeUpdated fields.
    /// </summary>
    public static DateTime ToDateTime(Dictionary<string, object?> record)
    {
        var dateVal = record.GetValueOrDefault("DateUpdated");
        if (dateVal is not DateTime date)
            return DateTime.MinValue;

        var timeStr = record.GetValueOrDefault("TimeUpdated")?.ToString();
        if (string.IsNullOrWhiteSpace(timeStr) || !decimal.TryParse(timeStr, out var hours))
            return date;

        return date.AddHours((double)hours);
    }
}
