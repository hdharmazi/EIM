using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARM.Integration.EIM
{
    public class EnvironmentD365FSCContract
    {
        public string tenant { get; set; }
        public string clientid { get; set; }
        public string secret { get; set; }
        public string url { get; set; }
        public string authurl { get; set; }
        public string onpremise { get; set; }

    }

}
