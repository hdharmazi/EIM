using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
namespace Wilson.Integration.Orchestrations;

/// <summary>
/// Capture → D365 FSC: submits a sales order to the D365 message queue via the
/// <c>WTC_SysMessageService/SendMessageWithResponse</c> service action.
///
/// The order body is forwarded as pass-through JSON (no field mapping). <c>dataAreaId</c> becomes
/// <c>_companyId</c> and is stripped from <c>_messageContent</c>. D365 processes the message in a batch;
/// the final status is reconciled later from the
/// <c>SysMessageProcessorMessageProcessedBusinessEvent</c> business event.
/// </summary>
public class SalesOrder_To_D365FSC : D365ServiceActionOrchestration
{
    protected override string ServiceMethod =>
        "api/services/WTC_Integration/WTC_SysMessageService/SendMessageWithResponse";

    protected override string MessageQueue => "CaptureIntegration";

    protected override string MessageType => "CaptureCreateSalesOrder";
}
