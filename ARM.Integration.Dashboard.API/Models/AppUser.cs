namespace ARM.Integration.Dashboard.API.Models;

public class AppUser
{
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "Viewer";
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDateTime { get; set; }
}
