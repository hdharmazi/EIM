using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public interface IUserService
{
    Task<List<AppUser>> GetAllAsync();
    Task<AppUser?> GetByEmailAsync(string email);
    Task CreateAsync(AppUser user);
    Task UpdateAsync(string email, AppUser user);
    Task DeleteAsync(string email);
}
