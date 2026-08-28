namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Where a classified failure is recorded and how it is reported.
/// </summary>
/// <param name="Status">The MESSAGEQUEUETABLE.STATUS value to write.</param>
/// <param name="Message">The response message to record, including any explanatory suffix.</param>
/// <param name="LogAsError">Whether the failure warrants an Error-level log rather than a Warning.</param>
public readonly record struct FailureOutcome(int Status, string Message, bool LogAsError);
