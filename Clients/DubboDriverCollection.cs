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
                NoActuatorInService,
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

        /// <summary>
        /// 添加&更新DubboDriverCollection中保持的DubboServiceDriver（如果需要根据dbEpList更新DubboServiceDriver里的节点信息）
        /// </summary>
        /// <param name="serviceName">服务名称</param>
        /// <param name="dbEpList">服务的节点信息</param>
        /// <returns>添加或更新是否对当前DubboDriverCollection的DubboServiceDriver有实际影响</returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public bool AddDubboServiceDriver(string serviceName, List<IPEndPoint> dbEpList)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                throw new ArgumentNullException(nameof(serviceName));
            }
            //dbEpList内容为空，服务节点都没有了也是有可能的，也要保留这个服务
            //if (!(dbEpList?.Count > 0))
            if (dbEpList==null)
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            //发现可复用DubboServiceDriver
            if (_dubboServiceDriverCollection.ContainsKey(serviceName))
            {
                DubboServiceDriver tempDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                return tempDubboServiceDriver.UpdateActuatorSuiteEndPoints(dbEpList)>0;
            }
            //需要创建新的DubboServiceDriver
            else
            {
                DubboServiceDriver tempDubboServiceDriver = new DubboServiceDriver(serviceName, dbEpList, _sourceDubboActuatorSuiteCollection);
                _dubboServiceDriverCollection.Add(serviceName, tempDubboServiceDriver);
                return true;
            }
        }

        /// <summary>
        /// 根据服务名称获取可用的DubboActuatorSuite
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="loadBalanceMode"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
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
                availableDubboActuatorInfo.AvailableDubboActuatorSuite = _dubboServiceDriverCollection[serviceName].GetDubboActuatorSuite(loadBalanceMode);
                if (availableDubboActuatorInfo.AvailableDubboActuatorSuite == null)
                {
                    availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoAvailableActuator;
                }
                else
                {
                    if (_dubboServiceDriverCollection[serviceName].InnerActuatorSuites.Count == 0)
                    {
                        availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoActuatorInService;
                    }
                    else
                    {
                        availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.GetDubboActuatorSuite;
                    }
                    availableDubboActuatorInfo.NowDubboServiceDriver = _dubboServiceDriverCollection[serviceName];
                }
            }
            else
            {
                availableDubboActuatorInfo.ResultType = AvailableDubboActuatorInfo.GetDubboActuatorSuiteResultType.NoDubboServiceDriver;
                availableDubboActuatorInfo.ErrorMes = $"can not find {serviceName} in _dubboServiceDriverCollection";
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
