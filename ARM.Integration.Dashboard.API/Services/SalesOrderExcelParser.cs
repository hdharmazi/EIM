using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ARM.Integration.Dashboard.API.Models;
using ClosedXML.Excel;

namespace ARM.Integration.Dashboard.API.Services;

/// <summary>Thrown when a cell fails coercion/validation; carries the offending sheet and cell.</summary>
public sealed class SalesOrderCellException(string sheet, string cell, string message)
    : Exception(message)
{
    public string Sheet { get; } = sheet;
    public string Cell { get; } = cell;
}

/// <summary>
/// Parses an uploaded sales-order workbook into the Capture sales-order JSON shape.
///
/// Layout per sheet (one sheet = one order): R1 root header, R2 root data, R3/R4 optional
/// trade-in header/data, R5 line header, R6+ line rows. Headers are matched case-insensitively
/// to the canonical target field names; values are validated (not translated) and absent fields
/// are defaulted (0 numeric / "" string).
/// </summary>
public class SalesOrderExcelParser
{
    private static readonly JsonSerializerOptions _compact = new() { WriteIndented = false };

    // ── Fixed row anchors ──────────────────────────────────────────────
    private const int RootHeaderRow = 1;
    private const int RootDataRow   = 2;
    private const int TradeHeaderRow = 3;
    private const int TradeDataRow   = 4;
    private const int LineHeaderRow  = 5;
    private const int LineFirstDataRow = 6;

    // ── Block schemas (canonical target casing) ────────────────────────
    private static readonly SalesOrderFieldSpec[] RootSchema =
    [
        new("PBLId", SalesOrderFieldType.Text, Required: true),
        new("Revision", SalesOrderFieldType.Int),
        new("ModelId", SalesOrderFieldType.Text),
        new("Responsible", SalesOrderFieldType.Text, Aliases: ["ResponsibleName"]),
        new("UserGroupId", SalesOrderFieldType.Text),
        new("IsBOM", SalesOrderFieldType.Int),
        new("IsUsedTrailer", SalesOrderFieldType.Text),
        new("ExternalId", SalesOrderFieldType.Text),
        new("CustPONumber", SalesOrderFieldType.Text),
        new("Quantity", SalesOrderFieldType.Int),
        new("RequestDate", SalesOrderFieldType.Date, Aliases: ["RequestedDate"]),
        new("TotalPrice", SalesOrderFieldType.Decimal),
        new("Msg", SalesOrderFieldType.Text),
        new("CalcFederalTax", SalesOrderFieldType.Decimal),
        new("CalcNetAmount", SalesOrderFieldType.Decimal),
        new("Profit", SalesOrderFieldType.Decimal),
        new("SpecialDiscount", SalesOrderFieldType.Decimal),
        new("FreightAmount", SalesOrderFieldType.Decimal),
        new("GrandTotal", SalesOrderFieldType.Decimal),
        new("MiscTireFee", SalesOrderFieldType.Decimal),
        new("MiscPaperTrade", SalesOrderFieldType.Decimal),
        new("AmountTradeAllowance", SalesOrderFieldType.Decimal),
        new("SalesPrice", SalesOrderFieldType.Decimal),
        new("DealerCost", SalesOrderFieldType.Decimal),
        new("TireCredit", SalesOrderFieldType.Decimal),
        new("RouteId", SalesOrderFieldType.Text),
        new("SOUsage", SalesOrderFieldType.Text),
        new("MfgYear", SalesOrderFieldType.Int),
        new("BU", SalesOrderFieldType.Text),
        new("CustAccount", SalesOrderFieldType.Text),
        new("CustName", SalesOrderFieldType.Text),
        new("FreightTaxable", SalesOrderFieldType.YesNo),
        new("DrawingRequired", SalesOrderFieldType.YesNo),
        new("DepositReqd", SalesOrderFieldType.YesNo),
        new("DepositAmt", SalesOrderFieldType.Decimal),
        new("PaintSign", SalesOrderFieldType.YesNo),
        new("CustSuppTires", SalesOrderFieldType.YesNo),
        new("PriceLock", SalesOrderFieldType.YesNo),
        // UsedTrailer fields (1:1, merged into root; blank for new trailers)
        new("VIN", SalesOrderFieldType.Text),
        new("ProductLine", SalesOrderFieldType.Text),
        new("Make", SalesOrderFieldType.Text),
        new("Model", SalesOrderFieldType.Text),
        new("ItemId", SalesOrderFieldType.Text),
        new("Warehouse", SalesOrderFieldType.Text),
        new("Location", SalesOrderFieldType.Text),
    ];

    private static readonly SalesOrderFieldSpec[] LineSchema =
    [
        new("PBLId", SalesOrderFieldType.Text),
        new("LineNum", SalesOrderFieldType.Int, Required: true),
        new("Pick", SalesOrderFieldType.Text, Aliases: ["ValueString"]),
        new("ObjExcludeFromBOM", SalesOrderFieldType.Int),
        new("Qty", SalesOrderFieldType.Decimal, Aliases: ["Quantity", "ValueNumeric"]),
        new("Name", SalesOrderFieldType.Text, Required: true, Aliases: ["PickName"]),
        new("Price", SalesOrderFieldType.Decimal, Aliases: ["ValuePrice"]),
        new("Weight", SalesOrderFieldType.Decimal, Aliases: ["ValueWeight"]),
        new("Comment", SalesOrderFieldType.Text, Aliases: ["ValueComment"]),
    ];

    private static readonly SalesOrderFieldSpec[] TradeInSchema =
    [
        new("PBLId", SalesOrderFieldType.Text, Required: true),
        new("ItemId", SalesOrderFieldType.Text),
        new("LineId", SalesOrderFieldType.Int),
        new("ProductLine", SalesOrderFieldType.Text),
        new("Make", SalesOrderFieldType.Text),
        new("Model", SalesOrderFieldType.Text),
        new("MfgYear", SalesOrderFieldType.Int),
        new("VinNumber", SalesOrderFieldType.Text),
        new("Amount", SalesOrderFieldType.Decimal),
        new("Warehouse", SalesOrderFieldType.Text),
        new("LocationId", SalesOrderFieldType.Text),
    ];

    // Header signatures that must be present for a sheet to be considered valid.
    private static readonly string[] RootSignature = ["pblid"];
    private static readonly string[] LineSignature = ["linenum", "name", "pick"];

    /// <summary>Parses every worksheet in the workbook; each sheet yields a result or an error.</summary>
    public SalesOrderParseResponse Parse(Stream xlsx)
    {
        var response = new SalesOrderParseResponse();
        using var wb = new XLWorkbook(xlsx);

        foreach (var ws in wb.Worksheets)
        {
            var result = new SalesOrderSheetResult { SheetName = ws.Name };
            try
            {
                var order = ParseSheet(ws);
                result.Ok = true;
                result.OrderJson = order.ToJsonString(_compact);
                result.Preview = BuildPreview(order);
            }
            catch (SalesOrderCellException ex)
            {
                result.Ok = false;
                result.Error = new SalesOrderParseError { Sheet = ex.Sheet, Cell = ex.Cell, Message = ex.Message };
            }
            response.Sheets.Add(result);
        }

        return response;
    }

    private static JsonObject ParseSheet(IXLWorksheet ws)
    {
        var sheet = ws.Name;

        // Resolve header maps (lowercased header text -> column number)
        var rootHeaders = ReadHeaders(ws, RootHeaderRow);
        var lineHeaders = ReadHeaders(ws, LineHeaderRow);

        // Validate header signatures — fail fast, don't mis-map
        AssertSignature(sheet, rootHeaders, RootSignature, RootHeaderRow);
        AssertSignature(sheet, lineHeaders, LineSignature, LineHeaderRow);

        // Root object (R1/R2)
        var root = BuildObject(ws, sheet, rootHeaders, RootDataRow, RootSchema);

        // Trade-in (R3/R4) — present only when the header row carries content
        var tradeAllowance = new JsonArray();
        var tradeHeaders = ReadHeaders(ws, TradeHeaderRow);
        if (tradeHeaders.Count > 0 && RowHasContent(ws, TradeDataRow))
        {
            tradeAllowance.Add(BuildObject(ws, sheet, tradeHeaders, TradeDataRow, TradeInSchema));
        }
        root["TradeAllowance"] = tradeAllowance;

        // Line items (R6+)
        var lines = new JsonArray();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = LineFirstDataRow; r <= lastRow; r++)
        {
            if (!RowHasContent(ws, r)) continue;
            lines.Add(BuildObject(ws, sheet, lineHeaders, r, LineSchema));
        }
        root["Line"] = lines;

        return root;
    }

    /// <summary>Reads a header row into a case-insensitive map of header text -> column number.</summary>
    private static Dictionary<string, int> ReadHeaders(IXLWorksheet ws, int row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var xlRow = ws.Row(row);
        if (xlRow is null) return map;
        foreach (var cell in xlRow.CellsUsed())
        {
            var name = cell.GetString().Trim();
            if (name.Length == 0) continue;
            map[name] = cell.Address.ColumnNumber; // last-wins if duplicated (lowercased)
        }
        return map;
    }

    private static void AssertSignature(string sheet, Dictionary<string, int> headers, string[] signature, int headerRow)
    {
        foreach (var required in signature)
        {
            if (!headers.ContainsKey(required))
            {
                throw new SalesOrderCellException(
                    sheet, $"row {headerRow}",
                    $"Expected header '{required}' in the header row {headerRow} but it was not found. " +
                    "Check the sheet layout and column names.");
            }
        }
    }

    private static bool RowHasContent(IXLWorksheet ws, int row)
        => ws.Row(row)?.CellsUsed().Any(c => !string.IsNullOrWhiteSpace(c.GetString())) ?? false;

    /// <summary>Builds one JSON object from a data row using the given schema, defaulting absent fields.</summary>
    private static JsonObject BuildObject(
        IXLWorksheet ws, string sheet, Dictionary<string, int> headers, int dataRow, SalesOrderFieldSpec[] schema)
    {
        var obj = new JsonObject();
        foreach (var spec in schema)
        {
            if (TryResolveColumn(headers, spec, out var col))
            {
                var cell = ws.Cell(dataRow, col);
                obj[spec.Name] = CoerceCell(cell, spec, sheet);
            }
            else if (spec.Required)
            {
                throw new SalesOrderCellException(
                    sheet, $"row {dataRow}",
                    $"Required field '{spec.Name}' has no matching column.");
            }
            else
            {
                obj[spec.Name] = DefaultFor(spec.Type);
            }
        }
        return obj;
    }

    /// <summary>Resolves a schema field to a column by canonical name, then by any alias (case-insensitive).</summary>
    private static bool TryResolveColumn(Dictionary<string, int> headers, SalesOrderFieldSpec spec, out int col)
    {
        if (headers.TryGetValue(spec.Name, out col)) return true;
        if (spec.Aliases is not null)
        {
            foreach (var alias in spec.Aliases)
                if (headers.TryGetValue(alias, out col)) return true;
        }
        col = 0;
        return false;
    }

    private static JsonNode DefaultFor(SalesOrderFieldType type) => type switch
    {
        SalesOrderFieldType.Int => JsonValue.Create(0),
        SalesOrderFieldType.Decimal => JsonValue.Create(0m),
        SalesOrderFieldType.YesNo => JsonValue.Create("No"),
        _ => JsonValue.Create("")!, // Text and Date default to empty string
    };

    private static JsonNode CoerceCell(IXLCell cell, SalesOrderFieldSpec spec, string sheet)
    {
        var raw = cell.GetString().Trim();
        var address = cell.Address.ToString()!;

        // Treat a literal "NULL" (case-insensitive) as an empty cell
        if (string.Equals(raw, "NULL", StringComparison.OrdinalIgnoreCase))
            raw = "";

        var empty = raw.Length == 0;

        if (spec.Required && empty)
        {
            throw new SalesOrderCellException(sheet, address, $"Required field '{spec.Name}' is empty.");
        }

        switch (spec.Type)
        {
            case SalesOrderFieldType.Text:
                return JsonValue.Create(raw)!;

            case SalesOrderFieldType.Int:
                if (empty) return JsonValue.Create(0);
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return JsonValue.Create(i);
                throw new SalesOrderCellException(sheet, address,
                    $"Field '{spec.Name}' expected an integer but got '{raw}'.");

            case SalesOrderFieldType.Decimal:
                if (empty) return JsonValue.Create(0m);
                if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return JsonValue.Create(d);
                throw new SalesOrderCellException(sheet, address,
                    $"Field '{spec.Name}' expected a number but got '{raw}'.");

            case SalesOrderFieldType.YesNo:
                if (empty) return JsonValue.Create("No");
                if (string.Equals(raw, "Yes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase) ||
                    raw == "1")
                    return JsonValue.Create("Yes");
                if (string.Equals(raw, "No", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "False", StringComparison.OrdinalIgnoreCase) ||
                    raw == "0")
                    return JsonValue.Create("No");
                throw new SalesOrderCellException(sheet, address,
                    $"Field '{spec.Name}' expected Yes/No but got '{raw}'.");

            case SalesOrderFieldType.Date:
                if (empty) return JsonValue.Create("")!;
                // Real Excel date cell (or date-formatted number) -> normalize to ISO
                if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var dtCell))
                    return JsonValue.Create(dtCell.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))!;
                // Text already in ISO form
                if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return JsonValue.Create(raw)!;
                // Any other recognizable date text -> normalize to ISO
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtText))
                    return JsonValue.Create(dtText.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))!;
                throw new SalesOrderCellException(sheet, address,
                    $"Field '{spec.Name}' expected a date but got '{raw}'.");

            default:
                return JsonValue.Create(raw)!;
        }
    }

    private static SalesOrderSheetPreview BuildPreview(JsonObject order) => new()
    {
        PBLId = order["PBLId"]?.GetValue<string>() ?? "",
        CustName = order["CustName"]?.GetValue<string>() ?? "",
        Quantity = order["Quantity"]?.GetValue<int>() ?? 0,
        LineCount = (order["Line"] as JsonArray)?.Count ?? 0,
    };
}
