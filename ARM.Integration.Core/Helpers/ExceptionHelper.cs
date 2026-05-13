namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Provides utility methods for extracting exception details.
/// </summary>
public static class ExceptionHelper
{
    /// <summary>
    /// Walks the <see cref="Exception.InnerException"/> chain and returns all
    /// distinct messages joined by " --> ".
    /// </summary>
    public static string GetMessages(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
        }

        return string.Join(" --> ", messages.Distinct());
    }
}
