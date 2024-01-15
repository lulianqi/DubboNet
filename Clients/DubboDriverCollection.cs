using DubboNet.DubboService.DataModle;
using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients
{
    internal class DubboDriverCollection
    {
        private Dictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = new Dictionary<string, DubboServiceDriver>();

        public int MaxUsersNum { get; set; } = 0;
        public int CommandTimeout { get; set; } = 10 * 1000;
        public string DefaultServiceName { get; set; }

        public int Count { get { return _dubboServiceDriverCollection?.Count ?? 0; } }


        public DubboActuator GetDubboActuator()
        {
            return default;
        }

        public async Task<DubboRequestResult> SendRequestAsync(string funcEntrance, string req)
        {
            DubboActuator nowDubboActuator = GetDubboActuator();
            return await nowDubboActuator.SendQuery(funcEntrance, req);
        }

    }
}
