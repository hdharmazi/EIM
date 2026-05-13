namespace ARM.Integration.Dashboard.Web.Models;

public class MessageDetailViewModel
{
    public MessageQueueRecord Message { get; set; } = null!;
    public List<MessageLogDetailRecord> LogDetails { get; set; } = [];
}
