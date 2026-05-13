namespace ARM.Integration.Dashboard.API.Models;

public class MessageLogDetailRecord
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTime CreatedDateTime { get; set; }
}
