using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MessageQueueController(MessageQueueRepository repo) : ControllerBase
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

        try
        {
            var responseMessage = await repo.ReprocessAsync(id);

            return Ok(new { success = true, message = responseMessage });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = $"ERROR: {ex.Message}" });
        }
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup([FromQuery] int? status, [FromQuery] int? olderThanDays)
    {
        if (!status.HasValue && !olderThanDays.HasValue)
            return BadRequest(new { message = "At least one filter (status or olderThanDays) is required." });

        try
        {
            var deleted = await repo.CleanupAsync(status, olderThanDays);
            return Ok(new { success = true, deleted, message = $"Deleted {deleted} record(s)." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = $"ERROR: {ex.Message}" });
        }
    }
}
