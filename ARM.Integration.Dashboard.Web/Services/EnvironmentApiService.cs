using System.Net.Http.Json;
using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public class EnvironmentApiService(HttpClient httpClient) : IEnvironmentService
{
    public async Task<List<EnvironmentRecord>> GetAllAsync(string? system = null, string? environment = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(system)) parts.Add($"system={Uri.EscapeDataString(system)}");
        if (!string.IsNullOrWhiteSpace(environment)) parts.Add($"environment={Uri.EscapeDataString(environment)}");

        var query = parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
        return await httpClient.GetFromJsonAsync<List<EnvironmentRecord>>($"api/environment{query}") ?? [];
    }

    public async Task<EnvironmentRecord?> GetByKeyAsync(string system, string environment)
    {
        var response = await httpClient.GetAsync(
            $"api/environment/{Uri.EscapeDataString(system)}/{Uri.EscapeDataString(environment)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnvironmentRecord>();
    }

    public async Task CreateAsync(EnvironmentRecord record)
    {
        var response = await httpClient.PostAsJsonAsync("api/environment", record);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateAsync(string system, string environment, string value)
    {
        var payload = new EnvironmentRecord { System = system, Environment = environment, Value = value };
        var response = await httpClient.PutAsJsonAsync(
            $"api/environment/{Uri.EscapeDataString(system)}/{Uri.EscapeDataString(environment)}", payload);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string system, string environment)
    {
        var response = await httpClient.DeleteAsync(
            $"api/environment/{Uri.EscapeDataString(system)}/{Uri.EscapeDataString(environment)}");
        response.EnsureSuccessStatusCode();
    }
}
