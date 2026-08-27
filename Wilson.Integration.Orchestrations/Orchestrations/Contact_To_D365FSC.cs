using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// Capture → D365 FSC: Maps CaptureContactMessageContract to CDSContactPersonsV2 CRUD.
    /// </summary>
    public class Contact_To_D365FSC : D365CrudOrchestration<CaptureContactMessageContract, ContactPersonEntity>
    {
        protected override string D365EntityName => "ContactPersons";
        protected override string ResponseEntityName => "Contact";

        protected override (string? SourceId, string? Action, CaptureContactMessageContract Data) DeserializePayload(string payload)
        {
            var envelope = JsonSerializer.Deserialize<CaptureMessageEnvelope<CaptureContactMessageContract>>(payload)
                ?? throw new JsonException("Envelope is null.");
            return (envelope.SourceId, envelope.Action, envelope.Data ?? throw new JsonException("Data is null."));
        }

        protected override ContactPersonEntity MapToTarget(CaptureContactMessageContract source)
        {
            return new ContactPersonEntity
            {
                DataAreaId = source.DataAreaId,
                ContactPersonId = source.ContactPersonId,
                FirstName = source.FirstName,
                LastName = source.LastName,
                MiddleName = source.MiddleName,
                AssociatedPartyNumber = source.AssociatedPartyNumber

            };
        }

        protected override EntityKey BuildEntityKey(ContactPersonEntity d365Entity)
        {
            return EntityKey.Create("dataAreaId", d365Entity.DataAreaId)
                .Add("ContactPersonId", d365Entity.ContactPersonId);
        }

        protected override CaptureContactMessageContract BuildResponseData(
            CaptureContactMessageContract source, ContactPersonEntity? d365Result, string action)
        {
            if (d365Result != null && action.Equals("New", StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureContactMessageContract
                {
                    ContactPersonId = d365Result.ContactPersonId,
                    FirstName = d365Result.FirstName,
                    LastName = d365Result.LastName,
                    MiddleName = d365Result.MiddleName,
                    AssociatedPartyNumber = source.AssociatedPartyNumber
                };
            }

            return new CaptureContactMessageContract
            {
                ContactPersonId = source.ContactPersonId,
                FirstName = source.FirstName,
                LastName = source.LastName,
                MiddleName = source.MiddleName,
                AssociatedPartyNumber = source.AssociatedPartyNumber
            };
        }
    }
}
