using ARM.Integration.Core.Model;
using ARM.Integration.EIM;
using System.Text.Json;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// Covers reconciliation of D365 message-processor business events.
///
/// Both fixtures are production payloads. The defect these tests exist to prevent was a
/// hand-written mental model of the event disagreeing with the real one, so a synthetic fixture
/// would reproduce the same mistake. Only the failure fixture is edited, and only to trim its
/// MessageContent line array for size; every field the code reads is verbatim.
/// </summary>
public class BusinessEventReconcileTests
{
    private static string LoadFixture(string name = "SysMessageProcessorMessageProcessedBusinessEvent.json") =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static string LoadFailureFixture() =>
        LoadFixture("SysMessageProcessorMessageProcessedBusinessEvent.Failed.json");

    private static SysMessageBusinessEventsContract Deserialize(string json) =>
        JsonSerializer.Deserialize<SysMessageBusinessEventsContract>(json)!;

    [Fact]
    public void The_captured_payload_binds_the_fields_reconciliation_depends_on()
    {
        var evt = Deserialize(LoadFixture());

        Assert.Equal("SysMessageProcessorMessageProcessedBusinessEvent", evt.BusinessEventId);
        Assert.Equal(186021, evt.CreatedTransactionId);
        Assert.Equal("Processed", evt.MessageProcessingResult);
        Assert.Equal("Queued", evt.MessageState);
        Assert.Equal("200007", evt.MessageResponseKey);
        Assert.Equal("SalesId", evt.MessageResponseKeyFieldName);
    }

    [Fact]
    public void A_processed_message_reports_MessageState_Queued_so_status_must_not_come_from_it()
    {
        var evt = Deserialize(LoadFixture());

        // The original bug: mapping from MessageState made every success non-terminal, so rows sat
        // at AwaitingD365 until the sweep escalated successfully created orders to Dead Letter.
        var (status, _) = BusinessEventOutcome.Map(evt.MessageProcessingResult);

        Assert.Equal("Queued", evt.MessageState);
        Assert.Equal(MessageStatus.Completed, status);
    }

    [Fact]
    public void The_captured_failure_payload_binds_and_maps_to_Failed()
    {
        var evt = Deserialize(LoadFailureFixture());

        var (status, reason) = BusinessEventOutcome.Map(evt.MessageProcessingResult);

        Assert.Equal(186013, evt.CreatedTransactionId);
        Assert.Equal("Failed", evt.MessageProcessingResult);
        Assert.Equal("Failed", evt.MessageState);
        Assert.Equal(MessageStatus.Failed, status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.None, reason);
        Assert.Contains("The record already exists.", evt.MessageInfolog);
    }

    [Fact]
    public void A_failure_sends_empty_strings_rather_than_omitting_the_response_key()
    {
        var evt = Deserialize(LoadFailureFixture());

        Assert.Equal("", evt.MessageResponseKey);
        Assert.Equal("", evt.MessageResponseKeyFieldName);
        Assert.Null(BusinessEventResponse.DescribeResponseKey(evt));
    }

    [Theory]
    [InlineData("Processed")]
    [InlineData("processed")]
    [InlineData("PROCESSED")]
    [InlineData("  Processed  ")]
    public void Processed_maps_to_Completed(string result)
    {
        var (status, reason) = BusinessEventOutcome.Map(result);

        Assert.Equal(MessageStatus.Completed, status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.None, reason);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Error")]
    [InlineData("cancelled")]
    public void Failure_outcomes_map_to_Failed(string result)
    {
        var (status, reason) = BusinessEventOutcome.Map(result);

        Assert.Equal(MessageStatus.Failed, status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.None, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_outcome_is_non_terminal(string? result)
    {
        var (status, reason) = BusinessEventOutcome.Map(result);

        Assert.Null(status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.Absent, reason);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("InProcess")]
    [InlineData("SomethingD365AddedLater")]
    public void An_unrecognised_outcome_is_non_terminal_and_flagged(string result)
    {
        var (status, reason) = BusinessEventOutcome.Map(result);

        Assert.Null(status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.Unrecognised, reason);
    }

    [Fact]
    public void A_queued_event_with_no_processing_result_stays_non_terminal()
    {
        var (status, reason) = BusinessEventOutcome.Map(messageProcessingResult: null);

        Assert.Null(status);
        Assert.Equal(BusinessEventOutcome.NonTerminalReason.Absent, reason);
    }

    [Fact]
    public void A_quoted_CreatedTransactionId_still_binds()
    {
        var evt = Deserialize("""
            {"BusinessEventId":"SysMessageProcessorMessageProcessedBusinessEvent",
             "CreatedTransactionId":"186021","MessageProcessingResult":"Processed"}
            """);

        Assert.Equal(186021, evt.CreatedTransactionId);
    }

    [Fact]
    public void A_payload_without_a_response_key_still_maps_to_a_terminal_status()
    {
        var evt = Deserialize("""
            {"BusinessEventId":"SysMessageProcessorMessageProcessedBusinessEvent",
             "CreatedTransactionId":186021,"MessageProcessingResult":"Processed"}
            """);

        var (status, _) = BusinessEventOutcome.Map(evt.MessageProcessingResult);

        Assert.Equal(MessageStatus.Completed, status);
        Assert.Null(evt.MessageResponseKey);
    }

    [Fact]
    public void The_success_response_carries_the_sales_order_number_and_transaction_id()
    {
        var evt = Deserialize(LoadFixture());
        var awaiting = AwaitingRow();

        var envelope = BusinessEventResponse.Build(awaiting, evt, MessageStatus.Completed, "186021");

        Assert.Equal("Success", envelope.Status);
        Assert.Equal("Reconcile", envelope.Action);
        Assert.Equal("SO-1", envelope.SourceId);
        Assert.Equal("200007", envelope.Data!["SalesId"]!.GetValue<string>());
        Assert.Equal("186021", envelope.Data["CreatedTransactionId"]!.GetValue<string>());
    }

    [Fact]
    public void The_failure_response_carries_the_infolog()
    {
        var evt = Deserialize(LoadFailureFixture());

        var envelope = BusinessEventResponse.Build(AwaitingRow(), evt, MessageStatus.Failed, "186013");

        Assert.Equal("Failed", envelope.Status);
        Assert.Contains("The record already exists.", envelope.StatusMessage);
        Assert.False(envelope.Data!.ContainsKey("SalesId"));
    }

    [Fact]
    public void The_response_entity_comes_from_the_row_not_a_hardcoded_literal()
    {
        var evt = Deserialize(LoadFixture());

        var envelope = BusinessEventResponse.Build(AwaitingRow(), evt, MessageStatus.Completed, "186021");

        Assert.Equal("PurchaseOrder", envelope.Entity);
    }

    [Fact]
    public void The_response_entity_falls_back_to_the_destination_entity()
    {
        var evt = Deserialize(LoadFixture());
        var awaiting = AwaitingRow();
        awaiting.SourceEntity = "";

        var envelope = BusinessEventResponse.Build(awaiting, evt, MessageStatus.Completed, "186021");

        Assert.Equal("SalesOrder", envelope.Entity);
    }

    [Fact]
    public void A_missing_response_key_is_omitted_from_the_response_data()
    {
        var evt = Deserialize("""
            {"CreatedTransactionId":186021,"MessageProcessingResult":"Processed"}
            """);

        var envelope = BusinessEventResponse.Build(AwaitingRow(), evt, MessageStatus.Completed, "186021");

        Assert.False(envelope.Data!.ContainsKey("SalesId"));
        Assert.Equal("186021", envelope.Data["CreatedTransactionId"]!.GetValue<string>());
    }

    // Entities deliberately differ so the entity-source assertions cannot both pass by accident.
    private static QueueMessageContract AwaitingRow() => new()
    {
        Id = "42",
        SourceId = "SO-1",
        SourceSystem = "CAPTURE",
        SourceEntity = "PurchaseOrder",
        DestinationSystem = "D365FSC",
        DestinationEntity = "SalesOrder",
        Environment = "DEV2",
        ResponseQueue = "dev2-capture-to-d365fsc-response",
    };
}
