namespace ARM.Integration.Dashboard.Web.Models;

public class AppUser
{
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string Role { get; set; } = "Viewer";
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDateTime { get; set; }

    public string RoleBadgeClass => Role switch
    {
        "Admin" => "badge bg-danger",
        "Editor" => "badge bg-primary",
        _ => "badge bg-secondary"
    };
}
