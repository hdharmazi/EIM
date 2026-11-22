using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Routing;
using Loen.Integration.SageSF.Sage;
using Loen.Integration.SageSF.Sync;
using Microsoft.Data.SqlClient;

namespace Loen.Integration.SageSF.Worker;

/// <summary>
/// Polling BackgroundService that runs sync cycles for each entity on a timer.
/// Reads from Sage SQL and Salesforce SOQL, dispatches through the EIM orchestration pipeline.
/// </summary>
public sealed class SyncWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SyncWorker> _logger;
    private readonly IReadOnlyDictionary<string, IOrchestration> _orchestrations;
    private readonly SyncLinkRepository _syncLinks;
    private readonly HashCalculator _hasher;

    public SyncWorker(
        IConfiguration config,
        ILogger<SyncWorker> logger,
        IReadOnlyDictionary<string, IOrchestration> orchestrations,
        SyncLinkRepository syncLinks,
        HashCalculator hasher)
    {
        _config = config;
        _logger = logger;
        _orchestrations = orchestrations;
        _syncLinks = syncLinks;
        _hasher = hasher;
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
                await RunDirectionAsync(
                    entity, SyncDirection.SageToSf, eimConnectionString, ct);
            }

            if (entity.SfToSageEnabled)
            {
                await RunDirectionAsync(
                    entity, SyncDirection.SfToSage, eimConnectionString, ct);
            }
        }
    }

    private async Task RunDirectionAsync(
        SyncEntityConfig entity,
        SyncDirection direction,
        string eimConnectionString,
        CancellationToken ct)
    {
        var orchestrationType = $"{entity.Name}_To_{(direction == SyncDirection.SageToSf ? "Salesforce" : "Sage")}";

        if (!_orchestrations.ContainsKey(orchestrationType))
        {
            _logger.LogWarning(
                "No orchestration registered for {Type}. Skipping.", orchestrationType);
            return;
        }

        _logger.LogInformation(
            "Starting {Direction} cycle for {Entity}.", direction, entity.Name);

        var controller = new EIMController(
            eimConnectionString,
            _logger,
            _orchestrations);

        // TODO M2: read watermark, query source, hash-check, insert messages, advance watermark
        _logger.LogDebug(
            "Cycle placeholder for {Entity} {Direction}. Implementation in M2.",
            entity.Name, direction);

        await Task.CompletedTask;
    }
}

public enum SyncDirection
{
    SageToSf,
    SfToSage
}
