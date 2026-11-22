using Microsoft.Data.SqlClient;

namespace Loen.Integration.SageSF.Sage;

/// <summary>
/// Reads delta records from Sage SQL tables using DateUpdated + TimeUpdated timestamps.
/// Queries via EIM_LNI_TEST synonyms (dev) or EIM_LNI/MAS_LNI (prod).
/// </summary>
public sealed class SageReader : IDisposable
{
    private readonly SqlConnection _connection;

    public SageReader(string connectionString)
    {
        _connection = new SqlConnection(connectionString);
    }

    /// <summary>
    /// Reads customer records modified after the given watermark.
    /// TimeUpdated is varchar(8) decimal hours — cast to decimal before comparing.
    /// </summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ReadCustomerDeltaAsync(
        DateTime watermarkDate, decimal watermarkTime, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                CustomerNo, CustomerName, AddressLine1, AddressLine2, City,
                State, ZipCode, CountryCode, TelephoneNo, FaxNo, EmailAddress,
                URLAddress, CustomerType, PriceLevel, SalespersonNo,
                TermsCode, CreditLimit, TaxSchedule, PrimaryShipToCode,
                CustomerStatus, DateUpdated, TimeUpdated
            FROM AR_Customer
            WHERE DateUpdated > @d
               OR (DateUpdated = @d AND TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5)) >= @h)
            ORDER BY DateUpdated, TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5))
            """;

        return await ExecuteDeltaQueryAsync(sql, watermarkDate, watermarkTime, ct);
    }

    /// <summary>
    /// Reads item records modified after the given watermark.
    /// </summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ReadItemDeltaAsync(
        DateTime watermarkDate, decimal watermarkTime, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                ItemCode, ItemCodeDesc, ItemType, ProductLine, ProductType,
                Category1, Category2, Category3, Category4,
                StandardUnitOfMeasure, StandardUnitPrice, AverageUnitCost,
                InactiveItem, DateUpdated, TimeUpdated
            FROM CI_Item
            WHERE DateUpdated > @d
               OR (DateUpdated = @d AND TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5)) >= @h)
            ORDER BY DateUpdated, TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5))
            """;

        return await ExecuteDeltaQueryAsync(sql, watermarkDate, watermarkTime, ct);
    }

    /// <summary>
    /// Reads contact records modified after the given watermark.
    /// </summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> ReadContactDeltaAsync(
        DateTime watermarkDate, decimal watermarkTime, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                CustomerNo, ContactCode, ContactName, ContactTitle,
                EmailAddress, TelephoneNo1, TelephoneExt1,
                DateUpdated, TimeUpdated
            FROM AR_CustomerContact
            WHERE DateUpdated > @d
               OR (DateUpdated = @d AND TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5)) >= @h)
            ORDER BY DateUpdated, TRY_CAST(NULLIF(TimeUpdated,'') AS decimal(9,5))
            """;

        return await ExecuteDeltaQueryAsync(sql, watermarkDate, watermarkTime, ct);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteDeltaQueryAsync(
        string sql, DateTime watermarkDate, decimal watermarkTime, CancellationToken ct)
    {
        await _connection.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@d", watermarkDate.Date);
        cmd.Parameters.AddWithValue("@h", watermarkTime);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<Dictionary<string, object?>>();

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(row);
        }

        return results;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class SageReaderFactory
{
    private readonly IConfiguration _config;

    public SageReaderFactory(IConfiguration config) => _config = config;

    public SageReader Create(string environment)
    {
        var connStr = _config.GetConnectionString($"Sage_{environment}")
            ?? throw new InvalidOperationException($"ConnectionStrings:Sage_{environment} is not configured.");
        return new SageReader(connStr);
    }
}
