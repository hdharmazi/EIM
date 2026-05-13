using System.Collections.Generic;

namespace ARM.Integration.Core.Model
{
    public class ServiceBusConnectionContract
    {
        public string? ConnectionString { get; set; }
        public List<ServiceBusParameterContract>? Parameters { get; set; }
    }

    public class ServiceBusParameterContract
    {
        public string? Name { get; set; }
        public string? Queue { get; set; }
        public string? ResponseQueue { get; set; }
    }
}
