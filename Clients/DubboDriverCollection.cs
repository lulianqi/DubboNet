using DubboNet.DubboService.DataModle;
using DubboNet.DubboService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using static DubboNet.Clients.DubboClient;

namespace DubboNet.Clients
{
    internal class DubboDriverCollection
    {
        internal class AvailableDubboActuatorInfo
        {
            public enum GetDubboActuatorSuiteResultType
            {
                Unkonw,
                GetDubboActuatorSuite,
                NoDubboServiceDriver,
                NoAvailableActuator,
                NetworkError
            }

            public GetDubboActuatorSuiteResultType ResultType { get; set; }= GetDubboActuatorSuiteResultType.Unkonw;
            public string ErrorMes { get; set; } = null;
            public DubboServiceDriver NowDubboServiceDriver { get; set; }
            public DubboActuatorSuite AvailableDubboActuatorSuite { get; set; }
        }

        /// <summary>
        /// 内部维持的DubboActuatorSuite（用于最大程度复用链接）
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;
        /// <summary>
        /// 内部维持的DubboServiceDriver（使用_sourceDubboActuatorSuiteCollection资源，复用服务资源）
        /// </summary>
        private Dictionary<string, DubboServiceDriver> _dubboServiceDriverCollection = null;

        public int MaxConnectionPerEndpoint{ get; set; } = 5;
        public int CommandTimeout { get; set; } = 10 * 1000;
        public string DefaultServiceName { get; set; }

        public int Count { get { return _dubboServiceDriverCollection?.Count ?? 0; } }

        public DubboDriverCollection(Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection)
        {
            if (dubboActuatorSuiteCollection == null)
            {
                throw new ArgumentNullException(nameof(dubboActuatorSuiteCollection));
            }
            _dubboServiceDriverCollection = new Dictionary<string, DubboServiceDriver>();
            _sourceDubboActuatorSuiteCollection = dubboActuatorSuiteCollection;
        }

        public void AddDubboServiceDriver(string serviceName, List<IPEndPoint> dbEpList)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            //发现可复用DubboServiceDriver
            if (_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                DubboServiceDriver tempDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                tempDubboServiceDriver.UpdateEqualIPEndPoints(dbEpList);
            }
            //需要创建新的DubboServiceDriver
            else
            {
                DubboServiceDriver tempDubboServiceDriver = new DubboServiceDriver(serviceName, dbEpList, _sourceDubboActuatorSuiteCollection);
                _dubboServiceDriverCollection.Add(serviceName, tempDubboServiceDriver);
            }
        }

        public AvailableDubboActuatorInfo GetDubboActuatorSuite(string serviceName , LoadBalanceMode loadBalanceMode = LoadBalanceMode.Random)
        {
            AvailableDubboActuatorInfo availableDubboActuatorInfo = new AvailableDubboActuatorInfo();
            if (string.IsNullOrEmpty(serviceName))
            {
                serviceName = DefaultServiceName;
            }
            if(string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            if(_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver;
                availableDubboActuatorInfo.ErrorMes = $"can not find {serviceName} in _dubboServiceDriverCollection";
            }
            else
            {
                availableDubboActuatorInfo.AvailableDubboActuatorSuite = _dubboServiceDriverCollection[serviceName].GetDubboActuatorSuite(loadBalanceMode);
                if(availableDubboActuatorInfo.AvailableDubboActuatorSuite == null)
                {
                    availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator;
                }
                else
                {
                    availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite;
                    availableDubboActuatorInfo.NowDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                }
            }
            return availableDubboActuatorInfo;
        }
        
        public async Task<DubboRequestResult> SendRequestAsync(string funcEntrance, string req)
        {
            DubboActuatorSuite nowDubboActuator =GetDubboActuatorSuite(null).AvailableDubboActuatorSuite;
            return await nowDubboActuator.SendQuery(funcEntrance, req);
        }

    }
}
