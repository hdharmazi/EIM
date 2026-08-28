namespace ARM.Integration.EIM.Retry;

/// <summary>
/// A classified failure: whether it is worth attempting again, and when it is not, why not.
/// </summary>
public readonly record struct FailureVerdict(FailureDisposition Disposition, TerminalReason Reason)
{
    public bool IsTerminal => Disposition == FailureDisposition.Terminal;

    public static FailureVerdict Retryable { get; } =
        new(FailureDisposition.Retryable, TerminalReason.None);

    public static FailureVerdict Rejected { get; } =
        new(FailureDisposition.Terminal, TerminalReason.Rejected);

    public static FailureVerdict Unclassified { get; } =
        new(FailureDisposition.Terminal, TerminalReason.Unclassified);
}
