using System.Diagnostics;
using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Services;
using ARM.Integration.EIM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.DashboardUser)]
public class MessageQueueController(
    MessageQueueRepository repo,
    ILogger<MessageQueueController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? status,
        [FromQuery] string? search,
        [FromQuery] int top = 500)
    {
        var records = await repo.GetAllAsync(statusFilter: status, search: search, top: top);
        return Ok(records);
    }

    [HttpGet("status-counts")]
    public async Task<IActionResult> GetStatusCounts()
    {
        var counts = await repo.GetStatusCountsAsync();
        return Ok(counts);
    }

    [HttpGet("recent-failed")]
    public async Task<IActionResult> GetRecentFailed([FromQuery] int top = 10)
    {
        var records = await repo.GetRecentFailedAsync(top: top);
        return Ok(records);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var record = await repo.GetByIdAsync(id);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpGet("{id:long}/log-details")]
    public async Task<IActionResult> GetLogDetails(long id)
    {
        var records = await repo.GetLogDetailsAsync(id);
        return Ok(records);
    }

    [HttpPut("{id:long}/payload")]
    public async Task<IActionResult> UpdatePayload(long id, [FromBody] string payload)
    {
        var record = await repo.GetByIdAsync(id);
        if (record is null) return NotFound();

        await repo.UpdatePayloadAsync(id, payload);
        return NoContent();
    }

    [HttpPost("{id:long}/reprocess")]
    public async Task<IActionResult> Reprocess(long id)
    {
        var record = await repo.GetByIdAsync(id);
        if (record is null) return NotFound();

        // Previously ungated: reprocessing a Completed message re-sent it to the destination.
        // Held is included so an operator can canary one message while its route stays paused.
        if (!MessageStatus.Reprocessable.Contains(record.Status))
        {
            return Conflict(new
            {
                success = false,
                message = $"Message {id} is at status {record.Status} and cannot be reprocessed. "
                          + $"Reprocess is allowed from status {string.Join(", ", MessageStatus.Reprocessable)}."
            });
        }

        try
        {
            var responseMessage = await repo.ReprocessAsync(id);

            // ProcessMessageAsync swallows processing errors and returns an "ERROR:..."
            // string, so treat that as a failure rather than a green success.
            var succeeded = !responseMessage.TrimStart()
                .StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);

            return Ok(new { success = succeeded, message = responseMessage });
        }
        catch (Exception ex)
        {
            return Fault(ex, "Reprocess failed for message {Id}.", id);
        }
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(
        [FromQuery] int? status,
        [FromQuery] int? olderThanDays,
        [FromQuery] bool acknowledgeProtected = false)
    {
        if (!status.HasValue && !olderThanDays.HasValue)
            return BadRequest(new { message = "At least one filter (status or olderThanDays) is required." });

        var scope = CleanupEligibility.Resolve(status, acknowledgeProtected);
        if (scope.IsRefused)
        {
            // "refused" separates a protected status from a filter that simply matched nothing.
            return BadRequest(new { success = false, refused = true, message = scope.Refusal });
        }

        try
        {
            var result = await repo.CleanupAsync(scope.Statuses, olderThanDays);

            return Ok(new
            {
                success = true,
                refused = false,
                deleted = result.Deleted,
                remaining = result.Remaining,
                message = $"Deleted {result.Deleted} record(s)."
            });
        }
        catch (Exception ex)
        {
            return Fault(ex, "Cleanup failed for status {Status}, olderThanDays {Days}.", status, olderThanDays);
        }
    }

    /// <summary>Logs the detail and returns a safe response carrying the correlation id.</summary>
    private IActionResult Fault(Exception ex, string logMessage, params object?[] args)
    {
        var correlationId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        logger.LogError(ex, logMessage + " CorrelationId={CorrelationId}", [.. args, correlationId]);

        return BadRequest(new
        {
            success = false,
            message = "The operation failed. Quote the correlation id when reporting this.",
            correlationId,
        });
    }
}
