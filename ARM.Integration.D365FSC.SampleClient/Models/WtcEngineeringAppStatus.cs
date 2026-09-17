using System.Text.Json.Serialization;

namespace ARM.Integration.D365FSC.SampleClient.Models;

/// <summary>One row of the D365 F&amp;O <c>WTC_EngineeringAppStatuses</c> data entity.</summary>
public sealed record WtcEngineeringAppStatus
{
    /// <summary>D365 returns this sentinel instead of null for unset dates.</summary>
    private static readonly DateTimeOffset UnsetDate = new(1900, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [JsonPropertyName("dataAreaId")]
    public string DataAreaId { get; init; } = string.Empty;

    [JsonPropertyName("SalesLineRecId")]
    public long SalesLineRecId { get; init; }

    [JsonPropertyName("SalesId")]
    public string SalesId { get; init; } = string.Empty;

    [JsonPropertyName("LineNum")]
    public int LineNum { get; init; }

    [JsonPropertyName("InventTransId")]
    public string InventTransId { get; init; } = string.Empty;

    [JsonPropertyName("SalesName")]
    public string SalesName { get; init; } = string.Empty;

    [JsonPropertyName("SalesUsage")]
    public string SalesUsage { get; init; } = string.Empty;

    [JsonPropertyName("SalesTableStatus")]
    public string SalesTableStatus { get; init; } = string.Empty;

    [JsonPropertyName("SalesLineStatus")]
    public string SalesLineStatus { get; init; } = string.Empty;

    [JsonPropertyName("CustAccount")]
    public string CustAccount { get; init; } = string.Empty;

    [JsonPropertyName("CustGroup")]
    public string CustGroup { get; init; } = string.Empty;

    [JsonPropertyName("DealerCustInfo")]
    public string DealerCustInfo { get; init; } = string.Empty;

    [JsonPropertyName("ItemId")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("ItemBOMId")]
    public string ItemBomId { get; init; } = string.Empty;

    [JsonPropertyName("ItemRouteId")]
    public string ItemRouteId { get; init; } = string.Empty;

    [JsonPropertyName("Make")]
    public string Make { get; init; } = string.Empty;

    [JsonPropertyName("Model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("MfgYear")]
    public int MfgYear { get; init; }

    [JsonPropertyName("VIN")]
    public string Vin { get; init; } = string.Empty;

    [JsonPropertyName("PBLId")]
    public string PblId { get; init; } = string.Empty;

    [JsonPropertyName("ProdId")]
    public string ProdId { get; init; } = string.Empty;

    [JsonPropertyName("ProdStatus")]
    public string ProdStatus { get; init; } = string.Empty;

    [JsonPropertyName("ProdWHSReleasePolicy")]
    public string ProdWhsReleasePolicy { get; init; } = string.Empty;

    [JsonPropertyName("EngineeringStatus")]
    public string EngineeringStatus { get; init; } = string.Empty;

    [JsonPropertyName("DrafterApproval")]
    public string DrafterApproval { get; init; } = string.Empty;

    [JsonPropertyName("DrafterDetail")]
    public string DrafterDetail { get; init; } = string.Empty;

    [JsonPropertyName("RouteComments")]
    public string RouteComments { get; init; } = string.Empty;

    [JsonPropertyName("WorkerResponsibleName")]
    public string WorkerResponsibleName { get; init; } = string.Empty;

    // D365 NoYes enums serialise as "Yes"/"No" strings and may be null when unset.
    [JsonPropertyName("DrawingRequired")]
    public string? DrawingRequired { get; init; }

    [JsonPropertyName("DrawingApproved")]
    public string? DrawingApproved { get; init; }

    [JsonPropertyName("DepositReqd")]
    public string? DepositRequired { get; init; }

    [JsonPropertyName("ConfirmedDlv")]
    public DateTimeOffset? ConfirmedDelivery { get; init; }

    [JsonPropertyName("ShippingDateConfirmed")]
    public DateTimeOffset? ShippingDateConfirmed { get; init; }

    [JsonPropertyName("DrawingSentDate")]
    public DateTimeOffset? DrawingSentDate { get; init; }

    [JsonPropertyName("ReleaseDate")]
    public DateTimeOffset? ReleaseDate { get; init; }

    public static bool IsUnset(DateTimeOffset? value) => value is null || value.Value == UnsetDate;

    public static string Format(DateTimeOffset? value) =>
        IsUnset(value) ? "-" : value!.Value.ToString("yyyy-MM-dd");
}
