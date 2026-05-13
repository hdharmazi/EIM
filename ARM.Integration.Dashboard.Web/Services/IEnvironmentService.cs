using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public interface IEnvironmentService
{
    Task<List<EnvironmentRecord>> GetAllAsync(string? system = null, string? environment = null);
    Task<EnvironmentRecord?> GetByKeyAsync(string system, string environment);
    Task CreateAsync(EnvironmentRecord record);
    Task UpdateAsync(string system, string environment, string value);
    Task DeleteAsync(string system, string environment);
}
