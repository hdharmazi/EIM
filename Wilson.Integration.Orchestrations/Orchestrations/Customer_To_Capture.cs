using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// D365 FSC -> Capture: Maps CustomersV3 data events to CaptureCustomerMessageContract.
    /// </summary>
    public class Customer_To_Capture : D365EventForwardOrchestration<CaptureCustomerMessageContract>
    {
        protected override string EntityName => "Customer";

        private static readonly Dictionary<int, string> OnHoldStatusMap = new()
        {
            [200000000] = "No",
            [200000001] = "Invoice",
            [200000002] = "All",
            [200000003] = "Payment",
            [200000004] = "Requisition",
            [200000005] = "Never",
            [200000006] = "PurchaseOrder",
        };

        private static string? ResolveOnHoldStatus(string? rawValue)
        {
            if (rawValue is null) return null;

            if (int.TryParse(rawValue, out var code))
                return OnHoldStatusMap.TryGetValue(code, out var label) ? label : rawValue;

            try
            {
                using var doc = JsonDocument.Parse(rawValue);
                if (doc.RootElement.TryGetProperty("Value", out var intVal)
                    && intVal.TryGetInt32(out var jsonCode))
                    return OnHoldStatusMap.TryGetValue(jsonCode, out var jsonLabel) ? jsonLabel : jsonCode.ToString();
            }
            catch { }

            return rawValue;
        }

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
                Blocked = ResolveOnHoldStatus(getField("mserp_onholdstatus", true)),
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