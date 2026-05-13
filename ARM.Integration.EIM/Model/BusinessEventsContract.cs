using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARM.Integration.EIM
{
    public class BusinessEventsContract
    {
        public string BusinessEventId { get; set; }
        public string BusinessEventLegalEntity { get; set; }
        public string actiontype { get; set; }
        public string payload { get; set; }
        public string transactiontype { get; set; }
        public string EventId { get; set; }
    }

}
