using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Contract for all integration orchestrations.
/// Implementations handle the business logic for a specific integration direction
/// (e.g., D365 → External System, or External System → D365).
/// </summary>
public interface IOrchestration
{
    /// <summary>
    /// Processes a queue message by executing the integration logic for the specific entity and direction.
    /// </summary>
    /// <param name="message">The deserialized queue message containing routing info and payload.</param>
    /// <param name="connectionString">The EIM database connection string for environment lookups.</param>
    /// <param name="stepLog">Logger for writing step-level orchestration logs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="OrchestrationResult"/> carrying the resulting message status, a log message,
    /// and an optional destination correlation id.
    /// </returns>
    Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract message,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default);
}
