using ARM.Integration.Core.Model;

namespace ARM.Integration.EIM.Orchestration
{
    /// <summary>
    /// D365 FSC → Capture: Maps CustomersV3 data events to CaptureCustomerMessageContract.
    /// </summary>
    public class Customer_To_Capture : D365EventForwardOrchestration<CaptureCustomerMessageContract>
    {
        protected override string EntityName => "Customer";

        protected override CaptureCustomerMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            return new CaptureCustomerMessageContract
            {
                DataAreaId = getField("mserp_dataareaid", true),
                CustomerAccount = getField("mserp_customeraccount", true),
                OrganizationName = getField("mserp_organizationname", true),
                NameAlias = getField("mserp_namealias", true),
                CustomerGroupId = getField("mserp_customergroupid", true),
                EmployeeResponsibleNumber = getField("mserp_employeeresponsiblenumber", true),
                Blocked = getField("mserp_blocked", true),
                PartyNumber = getField("mserp_partynumber", true),

                //DepositRequired = getField("mserp_depositrequired", true),
                //DotNumber = getField("mserp_dotnumber", true),
                //TaxStatus = getField("mserp_taxstatus", true)

                DepositRequired = "No",
                DotNumber = "X1234",
                TaxStatus = "30CHARSTATUS"
            };
        }

        protected override object CreateEnvelope(CaptureCustomerMessageContract data, string sourceId, string action, string? createdDateTime)
        {
            return new CaptureMessageEnvelope<CaptureCustomerMessageContract>
            {
                SourceId = sourceId,
                Action = action,
                Entity = EntityName,
                CreatedDateTime = createdDateTime,
                Data = data,
            };
        }
    }
}
