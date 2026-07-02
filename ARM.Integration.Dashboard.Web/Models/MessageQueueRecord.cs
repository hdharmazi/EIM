namespace ARM.Integration.Dashboard.Web.Models;

public class MessageQueueRecord
{
    public long Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceEntity { get; set; } = string.Empty;
    public string DestinationSystem { get; set; } = string.Empty;
    public string DestinationEntity { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Queue { get; set; } = string.Empty;
    public string ResponseQueue { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string MessageLog { get; set; } = string.Empty;
    public int Status { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedDateTime { get; set; }

    public string StatusLabel => Status switch
    {
        0 => "NEW",
        1 => "PROCESSING",
        2 => "COMPLETED",
        3 => "FAILED",
        4 => "DEAD",
        5 => "AWAITING D365",
        _ => "UNKNOWN"
    };

    public string StatusBadgeClass => Status switch
    {
        0 => "badge bg-secondary",
        1 => "badge bg-primary",
        2 => "badge bg-success",
        3 => "badge bg-warning text-dark",
        4 => "badge bg-danger",
        5 => "badge bg-info text-dark",
        _ => "badge bg-dark"
    };

    public bool HasError => Status is 3 or 4 && !string.IsNullOrWhiteSpace(MessageLog);
}
