namespace ARM.Integration.Dashboard.Web.Models;

public class HomeViewModel
{
    public Dictionary<int, int> StatusCounts { get; set; } = [];
    public List<MessageQueueRecord> RecentFailed { get; set; } = [];

    public int GetCount(int status) => StatusCounts.GetValueOrDefault(status, 0);
    public int Total => StatusCounts.Values.Sum();
}
