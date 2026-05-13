namespace ARM.Integration.Dashboard.Web.Models;

public class MessageQueueViewModel
{
    public List<MessageQueueRecord> Messages { get; set; } = [];
    public int? StatusFilter { get; set; }
    public string? SearchTerm { get; set; }
    public int TotalCount { get; set; }
}
