using System;
using System.Collections.Generic;
using System.Globalization;
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

        public override int AddNodeHash(IPEndPoint node, int replicateIndex)
        {
            // HashCode.Combine uses a process-random seed; a routing ring must not.
            return BetterHash("[" + node.Address + "]:" + node.Port.ToString(CultureInfo.InvariantCulture)
                + "#" + replicateIndex.ToString(CultureInfo.InvariantCulture));
        }

    }
}
