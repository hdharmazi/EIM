using ARM.Integration.Core.Model;

namespace ARM.Integration.EIM.Orchestration
{
    /// <summary>
    /// D365 FSC → Capture: Maps customer address data events to CaptureCustomerAddressMessageContract.
    /// </summary>
    public class CustomerAddress_To_Capture : D365EventForwardOrchestration<CaptureCustomerAddressMessageContract>
    {
        protected override string EntityName => "CustomerAddress";

        protected override string GetSourceId(QueueMessageContract data, EventContext context)
            => data.MessageId;

        protected override CaptureCustomerAddressMessageContract MapFields(Func<string, bool, string?> getField, string action)
        {
            return new CaptureCustomerAddressMessageContract
            {
                CustomerLegalEntityId = getField("mserp_customerlegalentityid", true),
                CustomerAccountNumber = getField("mserp_customeraccountnumber", true),
                AddressLocationId = action == "New" ? null : getField("mserp_addresslocationid", true),
                AddressDescription = getField("mserp_addressdescription", false),
                AddressCity = getField("mserp_addresscity", false),
                AddressZipCode = getField("mserp_addresszipcode", false),
                AddressStreet = getField("mserp_addressstreet", false),
                AddressCountryRegionId = getField("mserp_addresscountryregionid", false),
                AddressState = getField("mserp_addressstate", false),
            };
        }

        protected override object CreateEnvelope(CaptureCustomerAddressMessageContract data, string sourceId, string action, string? createdDateTime)
        {
            return new CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>
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
