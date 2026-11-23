using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Loen.Integration.SageSF.Sage;
using Loen.Integration.SageSF.Sync;
using System.Text.Json;

namespace Loen.Integration.SageSF.Worker;

public sealed class SyncWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SyncWorker> _logger;
    private readonly IReadOnlyDictionary<string, IOrchestration> _orchestrations;
    private readonly SyncLinkRepository _syncLinks;
    private readonly HashCalculator _hasher;
    private readonly SageReaderFactory _sageReaderFactory;

    public SyncWorker(
        IConfiguration config,
        ILogger<SyncWorker> logger,
        IReadOnlyDictionary<string, IOrchestration> orchestrations,
        SyncLinkRepository syncLinks,
        HashCalculator hasher,
        SageReaderFactory sageReaderFactory)
    {
        _config = config;
        _logger = logger;
        _orchestrations = orchestrations;
        _syncLinks = syncLinks;
        _hasher = hasher;
        _sageReaderFactory = sageReaderFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSec = _config.GetValue("Sync:IntervalSeconds", 300);
        var eimConnectionString = _config.GetConnectionString("EIMDB")
            ?? throw new InvalidOperationException("ConnectionStrings:EIMDB is not configured.");

        _logger.LogInformation(
            "SyncWorker starting. Poll interval: {Interval}s.", intervalSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(eimConnectionString, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sync cycle failed. Will retry next interval.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken);
        }
    }

    private async Task RunCycleAsync(string eimConnectionString, CancellationToken ct)
    {
        var entities = SyncEntityConfig.GetAll(_config);

        foreach (var entity in entities)
        {
            if (ct.IsCancellationRequested) break;

            if (entity.SageToSfEnabled)
            {
                await RunSageToSfAsync(entity, eimConnectionString, ct);
            }

            if (entity.SfToSageEnabled)
            {
                // TODO M2: SF→Sage direction (SOQL on SystemModstamp)
                _logger.LogDebug("SF→Sage not yet implemented for {Entity}.", entity.Name);
            }
        }
    }

    private async Task RunSageToSfAsync(
        SyncEntityConfig entity, string eimConnectionString, CancellationToken ct)
    {
        var orchestrationType = $"{entity.Name}_To_Salesforce";

        if (!_orchestrations.ContainsKey(orchestrationType))
        {
            _logger.LogWarning("No orchestration registered for {Type}. Skipping.", orchestrationType);
            return;
        }

        var integrationKey = $"Sage_{entity.Name}_SageToSf";

        // 1. Read watermark
        var lastRun = EIMWatermark.GetLastRun(eimConnectionString, integrationKey);
        var (watermarkDate, watermarkTime) = SageTimestamp.FromDateTime(lastRun, entity.OverlapSec);

        _logger.LogInformation(
            "Starting Sage→SF cycle for {Entity}. Watermark: {Date:yyyy-MM-dd} + {Time:F5}h.",
            entity.Name, watermarkDate, watermarkTime);

        // 2. Query Sage for delta records
        using var reader = _sageReaderFactory.Create("DEV");
        var records = entity.Name switch
        {
            "Customer" => await reader.ReadCustomerDeltaAsync(watermarkDate, watermarkTime, ct),
            "Contact" => await reader.ReadContactDeltaAsync(watermarkDate, watermarkTime, ct),
            "Item" => await reader.ReadItemDeltaAsync(watermarkDate, watermarkTime, ct),
            _ => throw new InvalidOperationException($"Unknown entity: {entity.Name}")
        };

        if (records.Count == 0)
        {
            _logger.LogDebug("No changes for {Entity}.", entity.Name);
            return;
        }

        _logger.LogInformation("Read {Count} delta records for {Entity}.", records.Count, entity.Name);

        // 3. Hash check + insert messages
        var controller = new EIMController(eimConnectionString, _logger, _orchestrations);
        var inserted = 0;
        var skippedEcho = 0;
        DateTime latestModified = lastRun ?? DateTime.MinValue;

        foreach (var record in records)
        {
            if (ct.IsCancellationRequested) break;

            var sageKey = GetSageKey(entity.Name, record);
            if (sageKey is null) continue;

            var hash = _hasher.Compute(record);
            var link = await _syncLinks.GetAsync(entity.Name, sageKey, ct);

            if (link?.SageHash == hash)
            {
                skippedEcho++;
                continue;
            }

            var payload = JsonSerializer.Serialize(record);
            var messageId = controller.InsertMessage(new MessageRequest
            {
                Environment = "DEV",
                SourceId = sageKey,
                SourceSystem = "Sage",
                DestinationSystem = "Salesforce",
                Type = orchestrationType,
                Payload = payload,
                SourceEntity = entity.Name,
                DestinationEntity = GetDestinationEntity(entity.Name),
                DedupeKey = $"{entity.Name}:{sageKey}:{hash}",
            });

            var recordModified = SageTimestamp.ToDateTime(record);
            await _syncLinks.UpsertAsync(new SyncLink
            {
                Entity = entity.Name,
                SageKey = sageKey,
                SfId = link?.SfId,
                SageHash = hash,
                SfHash = link?.SfHash,
                SageModified = recordModified,
                SfModified = link?.SfModified,
                LastSyncedAt = DateTime.UtcNow,
                LastDirection = "SageToSf",
                State = "active",
            }, ct);

            if (recordModified > latestModified)
                latestModified = recordModified;

            inserted++;
        }

        // 4. Advance watermark to latest record timestamp
        if (inserted > 0)
        {
            EIMWatermark.UpdateLastRun(eimConnectionString, integrationKey, latestModified);
        }

        _logger.LogInformation(
            "Sage→SF cycle for {Entity}: {Inserted} queued, {Skipped} echo-skipped, {Total} total.",
            entity.Name, inserted, skippedEcho, records.Count);
    }

    private static string? GetSageKey(string entityName, Dictionary<string, object?> record) =>
        entityName switch
        {
            "Customer" => record.GetValueOrDefault("CustomerNo")?.ToString(),
            "Contact" => $"{record.GetValueOrDefault("CustomerNo")}-{record.GetValueOrDefault("ContactCode")}",
            "Item" => record.GetValueOrDefault("ItemCode")?.ToString(),
            _ => null
        };

    private static string GetDestinationEntity(string entityName) =>
        entityName switch
        {
            "Customer" => "Account",
            "Contact" => "Contact",
            "Item" => "Product2",
            _ => entityName
        };
}

public enum SyncDirection
{
    SageToSf,
    SfToSage
}
