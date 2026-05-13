using System.Net.Http.Json;
using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public class MessageQueueApiService(HttpClient httpClient) : IMessageQueueService
{
    public async Task<List<MessageQueueRecord>> GetAllAsync(int? statusFilter = null, string? search = null, int top = 500)
    {
        var query = $"?top={top}";
        if (statusFilter.HasValue) query += $"&status={statusFilter.Value}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";

        return await httpClient.GetFromJsonAsync<List<MessageQueueRecord>>($"api/messagequeue{query}") ?? [];
    }

    public async Task<Dictionary<int, int>> GetStatusCountsAsync()
    {
        return await httpClient.GetFromJsonAsync<Dictionary<int, int>>("api/messagequeue/status-counts") ?? [];
    }

    public async Task<List<MessageQueueRecord>> GetRecentFailedAsync(int top = 10)
    {
        return await httpClient.GetFromJsonAsync<List<MessageQueueRecord>>($"api/messagequeue/recent-failed?top={top}") ?? [];
    }

    public async Task<MessageQueueRecord?> GetByIdAsync(long id)
    {
        var response = await httpClient.GetAsync($"api/messagequeue/{id}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MessageQueueRecord>();
    }

    public async Task<List<MessageLogDetailRecord>> GetLogDetailsAsync(long messageId)
    {
        return await httpClient.GetFromJsonAsync<List<MessageLogDetailRecord>>($"api/messagequeue/{messageId}/log-details") ?? [];
    }

    public async Task<bool> UpdatePayloadAsync(long id, string payload)
    {
        var response = await httpClient.PutAsJsonAsync($"api/messagequeue/{id}/payload", payload);
        return response.IsSuccessStatusCode;
    }

    public async Task<(bool Success, string Message)> ReprocessAsync(long id)
    {
        var response = await httpClient.PostAsync($"api/messagequeue/{id}/reprocess", null);
        var body = await response.Content.ReadAsStringAsync();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
            return (success, message);
        }
        catch
        {
            return (response.IsSuccessStatusCode, body);
        }
    }

    public async Task<(bool Success, int Deleted, string Message)> CleanupAsync(int? status, int? olderThanDays)
    {
        var query = "?";
        if (status.HasValue) query += $"status={status.Value}&";
        if (olderThanDays.HasValue) query += $"olderThanDays={olderThanDays.Value}&";

        var response = await httpClient.PostAsync($"api/messagequeue/cleanup{query.TrimEnd('&', '?')}", null);
        var body = await response.Content.ReadAsStringAsync();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
            var deleted = doc.RootElement.TryGetProperty("deleted", out var d) ? d.GetInt32() : 0;
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? body : body;
            return (success, deleted, message);
        }
        catch
        {
            return (response.IsSuccessStatusCode, 0, body);
        }
    }
}
