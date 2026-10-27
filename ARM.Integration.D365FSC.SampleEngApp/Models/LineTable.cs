namespace ARM.Integration.D365FSC.SampleEngApp.Models;

// Drop-in replacement for the LineTable built by the old LINQ-to-SQL query against the replicated database.
// The outer property names (SID, salesLine, salesTable, prodTable, trailData) and the upper-case column
// names deliberately mirror that query so existing consumer code needs as few edits as possible.
// Rename any property to match the generated classes your code actually used.

/// <summary>One sales line with its order header, production order and trailer data.</summary>
public sealed class LineTable
{
    public string SID { get; init; } = string.Empty;

    public SalesLineRow salesLine { get; init; } = new();

    public SalesTableRow salesTable { get; init; } = new();

    /// <summary>Null when the item has no production order (was <c>ProdTable.DefaultIfEmpty()</c>).</summary>
    public ProdTableRow? prodTable { get; init; }

    /// <summary>Null when the item has no trailer data (was <c>trailData.DefaultIfEmpty()</c>).</summary>
    public TrailerDataRow? trailData { get; init; }

    /// <summary>Value of an unset date, matching what the replicated database returned.</summary>
    public static readonly DateTime NoDate = new(1900, 1, 1);

    public static LineTable FromStatus(WtcEngineeringAppStatus s) => new()
    {
        SID = s.SalesId,
        salesLine = new SalesLineRow
        {
            RECID = s.SalesLineRecId,
            DATAAREAID = s.DataAreaId,
            SALESID = s.SalesId,
            LINENUM = s.LineNum,
            INVENTTRANSID = s.InventTransId,
            ITEMID = s.ItemId,
            SALESSTATUS = s.SalesLineStatus,
            CONFIRMEDDLV = ToDate(s.ConfirmedDelivery),
            SHIPPINGDATECONFIRMED = ToDate(s.ShippingDateConfirmed),
            ITEMBOMID = s.ItemBomId,
            ITEMROUTEID = s.ItemRouteId,
            CUSTGROUP = s.CustGroup,
            ENGINEERINGSTATUS = s.EngineeringStatus,
            PBLID = s.PblId
        },
        salesTable = new SalesTableRow
        {
            DATAAREAID = s.DataAreaId,
            SALESID = s.SalesId,
            SALESSTATUS = s.SalesTableStatus,
            CUSTACCOUNT = s.CustAccount,
            SALESNAME = s.SalesName,
            WORKERRESPONSIBLENAME = s.WorkerResponsibleName,
            DEALERCUSTINFO = s.DealerCustInfo,
            DRAFTERAPPROVAL = s.DrafterApproval,
            DRAFTERDETAIL = s.DrafterDetail,
            DRAWINGAPPROVED = s.DrawingApproved,
            DRAWINGREQUIRED = s.DrawingRequired,
            DRAWINGSENTDATE = ToDate(s.DrawingSentDate),
            SALESUSAGE = s.SalesUsage,
            DEPOSITREQD = s.DepositRequired
        },
        prodTable = string.IsNullOrEmpty(s.ProdId)
            ? null
            : new ProdTableRow
            {
                PRODID = s.ProdId,
                ITEMID = s.ItemId,
                PRODSTATUS = s.ProdStatus,
                ROUTECOMMENTS = s.RouteComments,
                PRODWHSRELEASEPOLICY = s.ProdWhsReleasePolicy,
                RELEASEDDATE = ToDate(s.ReleaseDate)
            },
        trailData = string.IsNullOrEmpty(s.Model) && string.IsNullOrEmpty(s.Make) &&
                    string.IsNullOrEmpty(s.Vin) && s.MfgYear == 0
            ? null
            : new TrailerDataRow
            {
                ITEMID = s.ItemId,
                MAKE = s.Make,
                MODEL = s.Model,
                MFGYEAR = s.MfgYear,
                VIN = s.Vin
            }
    };

    // D365 date fields arrive as UTC midnight/noon timestamps; unset ones as 1900-01-01T12:00:00Z.
    private static DateTime ToDate(DateTimeOffset? value) =>
        WtcEngineeringAppStatus.IsUnset(value) ? NoDate : value!.Value.UtcDateTime.Date;
}

public sealed class SalesLineRow
{
    public long RECID { get; init; }
    public string DATAAREAID { get; init; } = string.Empty;
    public string SALESID { get; init; } = string.Empty;
    public decimal LINENUM { get; init; }
    public string INVENTTRANSID { get; init; } = string.Empty;
    public string ITEMID { get; init; } = string.Empty;

    /// <summary>Enum label such as <c>Backorder</c>, not the integer stored in the database.</summary>
    public string SALESSTATUS { get; init; } = string.Empty;

    public DateTime CONFIRMEDDLV { get; init; }
    public DateTime SHIPPINGDATECONFIRMED { get; init; }
    public string ITEMBOMID { get; init; } = string.Empty;
    public string ITEMROUTEID { get; init; } = string.Empty;
    public string CUSTGROUP { get; init; } = string.Empty;
    public string ENGINEERINGSTATUS { get; init; } = string.Empty;
    public string PBLID { get; init; } = string.Empty;
}

public sealed class SalesTableRow
{
    public string DATAAREAID { get; init; } = string.Empty;
    public string SALESID { get; init; } = string.Empty;

    /// <summary>Enum label such as <c>Backorder</c>, not the integer stored in the database.</summary>
    public string SALESSTATUS { get; init; } = string.Empty;

    public string CUSTACCOUNT { get; init; } = string.Empty;
    public string SALESNAME { get; init; } = string.Empty;

    /// <summary>Full name of the sales responsible worker (the database held a worker RecId).</summary>
    public string WORKERRESPONSIBLENAME { get; init; } = string.Empty;

    public string DEALERCUSTINFO { get; init; } = string.Empty;
    public string DRAFTERAPPROVAL { get; init; } = string.Empty;
    public string DRAFTERDETAIL { get; init; } = string.Empty;

    /// <summary>Enum label (string) or null when unset.</summary>
    public string? DRAWINGAPPROVED { get; init; }

    /// <summary>Enum label (string) or null when unset.</summary>
    public string? DRAWINGREQUIRED { get; init; }

    public DateTime DRAWINGSENTDATE { get; init; }
    public string SALESUSAGE { get; init; } = string.Empty;

    /// <summary><c>Yes</c>/<c>No</c> or null when unset.</summary>
    public string? DEPOSITREQD { get; init; }
}

public sealed class ProdTableRow
{
    public string PRODID { get; init; } = string.Empty;
    public string ITEMID { get; init; } = string.Empty;

    /// <summary>Enum label such as <c>Started</c>, not the integer stored in the database.</summary>
    public string PRODSTATUS { get; init; } = string.Empty;

    public string ROUTECOMMENTS { get; init; } = string.Empty;
    public string PRODWHSRELEASEPOLICY { get; init; } = string.Empty;
    public DateTime RELEASEDDATE { get; init; }
}

public sealed class TrailerDataRow
{
    public string ITEMID { get; init; } = string.Empty;
    public string MAKE { get; init; } = string.Empty;
    public string MODEL { get; init; } = string.Empty;
    public int MFGYEAR { get; init; }
    public string VIN { get; init; } = string.Empty;
}
