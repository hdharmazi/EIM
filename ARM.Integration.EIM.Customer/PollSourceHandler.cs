using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Timer-based polling function that calls a source API on a schedule,
/// retrieves changed records, inserts each into EIMDB for audit/retry,
/// and processes them through the orchestration pipeline.
/// 
/// This is a TEMPLATE — duplicate and customize for each polling integration.
/// 
/// Flow: Timer → Poll Source API → Insert to DB → ProcessMessage → Orchestration → Destination
/// 
/// Configuration (local.settings.json):
///   "PollSchedule": "0 */5 * * * *"          ← CRON: every 5 minutes
///   "PollSourceApiUrl": "https://api.source.com/customers"
///   "PollSourceApiKey": "your-api-key"
///   "PollEnvironment": "BAT"
///   "PollDestinationSystem": "TARGET"
///   "PollSourceSystem": "SOURCE"
///   "PollOrchestration": "SourceCustomer_To_Target"
///   "PollDestinationQueue": "bat-target-customer"
/// </summary>
public class PollSourceHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{

    /*
    private readonly ILogger _logger = loggerFactory.CreateLogger<PollSourceHandler>();
    private static readonly HttpClient _httpClient = new();

    [Function(nameof(PollSourceHandler))]
    public async Task Run(
        [TimerTrigger("%PollSchedule%")] TimerInfo timer)
    {
        _logger.LogInformation("Poll triggered at {time}. Last run: {last}",
            DateTime.UtcNow, timer.ScheduleStatus?.Last);

        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB not configured.");

        // ── 1. Get watermark (last successful poll time) ────────────────
        var watermark = EIMWatermark.GetLastRun(connectionString, nameof(PollSourceHandler));
        var pollStart = DateTime.UtcNow;

        _logger.LogInformation("Watermark: {watermark}", watermark?.ToString("o") ?? "first run");

        // ── 2. Call source API with filter ──────────────────────────────
        var sourceUrl = Environment.GetEnvironmentVariable("PollSourceApiUrl")
            ?? throw new InvalidOperationException("PollSourceApiUrl not configured.");
        var sourceApiKey = Environment.GetEnvironmentVariable("PollSourceApiKey");

        // Build filter: only records modified since last poll
        var filterUrl = watermark.HasValue
            ? $"{sourceUrl}?$filter=modifiedDateTime gt {watermark.Value:yyyy-MM-ddTHH:mm:ssZ}"
            : sourceUrl;

        var request = new HttpRequestMessage(HttpMethod.Get, filterUrl);
        if (!string.IsNullOrWhiteSpace(sourceApiKey))
            request.Headers.Add("X-Api-Key", sourceApiKey);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var records = await response.Content.ReadFromJsonAsync<JsonElement>();

        // ── 3. Extract array of records ─────────────────────────────────
        // Adjust this based on source API response structure
        var items = records.ValueKind == JsonValueKind.Array
            ? records.EnumerateArray().ToList()
            : records.TryGetProperty("value", out var val) // OData style
                ? val.EnumerateArray().ToList()
                : [];

        _logger.LogInformation("Received {count} records from source API", items.Count);

        if (items.Count == 0)
        {
            EIMWatermark.UpdateLastRun(connectionString, nameof(PollSourceHandler), pollStart);
            return;
        }

        // ── 4. Insert each record into DB and process ───────────────────
        var environment = Environment.GetEnvironmentVariable("PollEnvironment") ?? "BAT";
        var destSystem = Environment.GetEnvironmentVariable("PollDestinationSystem") ?? "TARGET";
        var sourceSystem = Environment.GetEnvironmentVariable("PollSourceSystem") ?? "SOURCE";
        var orchestrationType = Environment.GetEnvironmentVariable("PollOrchestration") ?? "SourceCustomer_To_Target";
        var destQueue = Environment.GetEnvironmentVariable("PollDestinationQueue");

        var controller = new EIMController(connectionString, _logger, orchestrations);

        int success = 0, failed = 0;

        foreach (var item in items)
        {
            try
            {
                var payload = item.GetRawText();

                var messageId = controller.InsertMessage(new MessageRequest
                {
                    SourceId = ExtractSourceId(item),
                    Environment = environment,
                    DestinationSystem = destSystem,
                    SourceSystem = sourceSystem,
                    Payload = payload,
                    Type = orchestrationType,
                    SourceEntity = "Customer", // customize per integration
                    DestinationEntity = "",
                    Queue = destQueue,
                });

                await controller.ProcessMessageAsync(messageId);
                success++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process record");
                failed++;
            }
        }

        _logger.LogInformation("Poll complete. Success: {success}, Failed: {failed}", success, failed);

        // ── 5. Update watermark ─────────────────────────────────────────
        if (success > 0)
        {
            EIMWatermark.UpdateLastRun(connectionString, nameof(PollSourceHandler), pollStart);
        }
    }

    /// <summary>
    /// Extracts a unique source ID from the record. Customize per integration.
    /// Tries common patterns: "id", "Id", "ID", first string property.
    /// </summary>
    private static string ExtractSourceId(JsonElement item)
    {
        if (item.TryGetProperty("id", out var id)) return id.ToString();
        if (item.TryGetProperty("Id", out id)) return id.ToString();
        if (item.TryGetProperty("ID", out id)) return id.ToString();
        return Guid.NewGuid().ToString();
    }

    */
}
