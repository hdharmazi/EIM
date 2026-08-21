using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration
{
    /// <summary>
    /// Capture → D365 FSC: Maps CapturePartyContactMessageContract to PartyContactsV3 CRUD.
    /// </summary>
    public class PartyContact_To_D365FSC : D365CrudOrchestration<CapturePartyContactMessageContract, PartyContactsEntity>
    {
        protected override string D365EntityName => "PartyContactsV3";
        protected override string ResponseEntityName => "PartyContact";

        protected override (string? SourceId, string? Action, CapturePartyContactMessageContract Data) DeserializePayload(string payload)
        {
            var envelope = JsonSerializer.Deserialize<CaptureMessageEnvelope<CapturePartyContactMessageContract>>(payload)
                ?? throw new JsonException("Envelope is null.");
            return (envelope.SourceId, envelope.Action, envelope.Data ?? throw new JsonException("Data is null."));
        }

        private static string? ResolvePartyNumber(CapturePartyContactMessageContract source) =>
            !string.IsNullOrWhiteSpace(source.ContactPartyNumber) ? source.ContactPartyNumber : source.CustomerPartyNumber;

        protected override PartyContactsEntity MapToTarget(CapturePartyContactMessageContract source)
        {
            return new PartyContactsEntity
            {
                PartyNumber = ResolvePartyNumber(source),
                ElectronicAddressId = source.ElectronicAddressId,
                Locator = source.Locator,
                LocatorExtension = source.LocatorExtension,
                IsPrimary = source.IsPrimary,
                Description = source.Description,
                Type = source.Type,
            };
        }

        protected override EntityKey BuildEntityKey(PartyContactsEntity d365Entity)
        {
            return EntityKey.Create("PartyNumber", d365Entity.PartyNumber)
                .Add("ElectronicAddressId", d365Entity.ElectronicAddressId);
        }

        protected override CapturePartyContactMessageContract BuildResponseData(
            CapturePartyContactMessageContract source, PartyContactsEntity? d365Result, string action)
        {
            if (d365Result != null && action.Equals("New", StringComparison.OrdinalIgnoreCase))
            {
                return new CapturePartyContactMessageContract
                {
                    ContactPartyNumber  = d365Result.PartyNumber,
                    CustomerPartyNumber = d365Result.PartyNumber,
                    ElectronicAddressId = d365Result.ElectronicAddressId,
                    Locator = d365Result.Locator,
                    LocatorExtension = d365Result.LocatorExtension,
                    IsPrimary = d365Result.IsPrimary,
                    Description = d365Result.Description,
                    Type = d365Result.Type,
                    IsInstantMessage = d365Result.IsInstantMessage,
                    IsMobilePhone = d365Result.IsMobilePhone,
                    CountryRegionCode = d365Result.CountryRegionCode,
                    Purpose = d365Result.Purpose,
                    LocationId = d365Result.LocationId,
                    IsPrivate = d365Result.IsPrivate,
                };
            }

            return new CapturePartyContactMessageContract
            {
                ContactPartyNumber  = source.ContactPartyNumber,
                CustomerPartyNumber = source.CustomerPartyNumber,
                ElectronicAddressId = source.ElectronicAddressId,
                Locator = source.Locator,
                LocatorExtension = source.LocatorExtension,
                IsPrimary = source.IsPrimary,
                Description = source.Description,
                Type = source.Type,
            };
        }
    }
}
