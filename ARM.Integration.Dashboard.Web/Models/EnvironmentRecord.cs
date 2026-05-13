using System.ComponentModel.DataAnnotations;

namespace ARM.Integration.Dashboard.Web.Models;

public class EnvironmentRecord
{
    [Required]
    public string System { get; set; } = string.Empty;

    [Required]
    public string Environment { get; set; } = string.Empty;

    [Required]
    public string Value { get; set; } = string.Empty;
}
