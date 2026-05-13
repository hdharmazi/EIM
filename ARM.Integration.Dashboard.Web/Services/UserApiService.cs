using System.Net.Http.Json;
using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public class UserApiService(HttpClient httpClient) : IUserService
{
    public async Task<List<AppUser>> GetAllAsync()
    {
        return await httpClient.GetFromJsonAsync<List<AppUser>>("api/user") ?? [];
    }

    public async Task<AppUser?> GetByEmailAsync(string email)
    {
        var response = await httpClient.GetAsync($"api/user/{Uri.EscapeDataString(email)}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AppUser>();
    }

    public async Task CreateAsync(AppUser user)
    {
        var response = await httpClient.PostAsJsonAsync("api/user", user);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateAsync(string email, AppUser user)
    {
        var response = await httpClient.PutAsJsonAsync($"api/user/{Uri.EscapeDataString(email)}", user);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string email)
    {
        var response = await httpClient.DeleteAsync($"api/user/{Uri.EscapeDataString(email)}");
        response.EnsureSuccessStatusCode();
    }
}
