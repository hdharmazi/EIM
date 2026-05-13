using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public interface IMessageQueueService
{
    Task<List<MessageQueueRecord>> GetAllAsync(int? statusFilter = null, string? search = null, int top = 500);
    Task<Dictionary<int, int>> GetStatusCountsAsync();
    Task<List<MessageQueueRecord>> GetRecentFailedAsync(int top = 10);
    Task<MessageQueueRecord?> GetByIdAsync(long id);
    Task<List<MessageLogDetailRecord>> GetLogDetailsAsync(long messageId);
    Task<bool> UpdatePayloadAsync(long id, string payload);
    Task<(bool Success, string Message)> ReprocessAsync(long id);
    Task<(bool Success, int Deleted, string Message)> CleanupAsync(int? status, int? olderThanDays);
}
