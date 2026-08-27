namespace ARM.Integration.Dashboard.API.Models;

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

    /// <summary>Attempts made so far. Only meaningful at Retry Scheduled or Dead Letter.</summary>
    public int RetryCount { get; set; }

    public DateTime? NextAttemptDateTime { get; set; }
}
