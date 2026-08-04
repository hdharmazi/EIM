namespace ARM.Integration.Dashboard.API.Models;

/// <summary>Field data types understood by the sales-order Excel parser.</summary>
public enum SalesOrderFieldType
{
    Text,
    Int,
    Decimal,
    Date,
    YesNo
}

/// <summary>
/// One column definition in a block schema: the canonical target JSON name, its type,
/// and whether a value is required. Header matching against this name (and any aliases) is
/// case-insensitive; output always uses <see cref="Name"/> casing.
/// </summary>
public record SalesOrderFieldSpec(string Name, SalesOrderFieldType Type, bool Required = false, string[]? Aliases = null);

/// <summary>A cell-level parse/validation error pointing at the offending sheet and cell.</summary>
public class SalesOrderParseError
{
    public string Sheet { get; set; } = "";
    public string Cell { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Lightweight summary shown in the sheet picker before a sheet is selected.</summary>
public class SalesOrderSheetPreview
{
    public string PBLId { get; set; } = "";
    public string CustName { get; set; } = "";
    public int Quantity { get; set; }
    public int LineCount { get; set; }
}

/// <summary>Result of parsing a single worksheet: either a preview + mapped order, or an error.</summary>
public class SalesOrderSheetResult
{
    public string SheetName { get; set; } = "";
    public bool Ok { get; set; }
    public SalesOrderSheetPreview? Preview { get; set; }
    /// <summary>The mapped Capture sales-order JSON (compact string) when <see cref="Ok"/> is true.</summary>
    public string? OrderJson { get; set; }
    public SalesOrderParseError? Error { get; set; }
}

/// <summary>Result of parsing an uploaded workbook — one entry per worksheet.</summary>
public class SalesOrderParseResponse
{
    public List<SalesOrderSheetResult> Sheets { get; set; } = [];
}
