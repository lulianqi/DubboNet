using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.Helper
{
    public class DubboSuiteConsistentHash:ConsistentHash<IPEndPoint>
    {
        public DubboSuiteConsistentHash(int replicate = 100):base(replicate) { }
    }
}
