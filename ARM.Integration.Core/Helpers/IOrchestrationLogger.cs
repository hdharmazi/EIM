using Microsoft.Extensions.Logging;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Logs orchestration steps to both the MESSAGELOGDETAIL table and <see cref="ILogger"/>.
/// </summary>
public interface IOrchestrationLogger
{
    /// <summary>Logs a step to the table and writes an Information-level ILogger message.</summary>
    void Log(string name, string? detail = null);

    /// <summary>Logs a step to the table and writes a Warning-level ILogger message.</summary>
    void LogWarning(string name, string? detail = null);

    /// <summary>Logs a step to the table and writes an Error-level ILogger message.</summary>
    void LogError(string name, string? detail = null);

    /// <summary>The underlying <see cref="ILogger"/> for cases where you need it directly.</summary>
    ILogger Logger { get; }
}
