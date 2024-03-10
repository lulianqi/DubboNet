using DubboNet.Clients.DataModle;
using DubboNet.DubboService;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using static DubboNet.Clients.DubboClient;
using static DubboNet.DubboService.DubboActuatorSuite;

namespace DubboNet.Clients
{
    /// <summary>
    /// 管理当个服务的所有连接器（单个服务可以有N个服务节点，每个服务节点可以有N个连接）
    /// </summary>
    internal class DubboServiceDriver:IDisposable
    {
        internal class DubboServiceDriverConf
        {
            public int DubboActuatorSuiteMaxConnections { get; set; } = 20;
            public int DubboActuatorSuiteAssistConnectionAliveTime { get; set; } = 60 * 5;
            public int DubboActuatorSuiteMasterConnectionAliveTime { get; set; } = 60 * 20;
            public int DubboRequestTimeout { get; set; } = 60 * 1000;
        }

        /// <summary>
        /// 服务名称
        /// </summary>
        public string ServiceName { get; private set; }

        /// <summary>
        /// 最后激活时间
        /// </summary>
        public DateTime LastActivateTime { get; private set; } = default(DateTime);

        /// <summary>
        /// 当前DubboServiceDriver可使用的各EndPoint的DubboActuatorSuite集合
        /// </summary>
        public Dictionary<IPEndPoint, DubboServiceEndPointInfo> InnerActuatorSuites { get; private set; }

        /// <summary>
        /// DubboClient的源ActuatorSuiteCollection（不要直接使用，保留的这份引用是为了释放时同时清理）
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;

        private DubboServiceDriverConf _innerDubboServiceDriverConf = null;

        private int TotalWeightForActuatorSuites = 0;

        /// <summary>
        /// 初始化DubboServiceDriver
        /// </summary>
        /// <param name="serviceName">当前服务的serviceName</param>
        /// <param name="dbEpList">当前服务节点列表</param>
        /// <param name="dubboActuatorSuiteCollection">内部dubboActuatorSuiteCollection</param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public DubboServiceDriver(string serviceName, List<DubboServiceEndPointInfo> dbEpList, Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection , DubboServiceDriverConf dubboServiceDriverConf = null)
        {
            ServiceName = serviceName;
            LastActivateTime = DateTime.Now;
            _sourceDubboActuatorSuiteCollection = dubboActuatorSuiteCollection;
            _innerDubboServiceDriverConf = dubboServiceDriverConf ?? new DubboServiceDriverConf();
            InnerActuatorSuites = new Dictionary<IPEndPoint, DubboServiceEndPointInfo>();
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            if (dubboActuatorSuiteCollection == null)
            {
                throw new ArgumentNullException(nameof(dubboActuatorSuiteCollection));
            }
            foreach (DubboServiceEndPointInfo ep in dbEpList)
            {
                AddActuatorSuite(ep);
            }
            UpdateTotalWeight();
        }

        /// <summary>
        /// 更新TotalWeightForActuatorSuites
        /// </summary>
        private void UpdateTotalWeight()
        {
            if (InnerActuatorSuites != null)
            {
                TotalWeightForActuatorSuites = InnerActuatorSuites.Sum(item => item.Value.Weight);
            }
            else
            {
                TotalWeightForActuatorSuites = 0;
            }
        }

        /// <summary>
        /// 通过IPEndPoint添加ActuatorSuite (内部函数)
        /// </summary>
        /// <param name="ep">IPEndPoint</param>
        /// <returns>是否添加成功</returns>
        private bool AddActuatorSuite(DubboServiceEndPointInfo ep)
        {
            if (_sourceDubboActuatorSuiteCollection.ContainsKey(ep.EndPoint))
            {
                //InnerActuatorSuites.Add(ep, _sourceDubboActuatorSuiteCollection[ep].ActuatorSuite);
                ep.InnerDubboActuatorSuite=_sourceDubboActuatorSuiteCollection[ep.EndPoint].ActuatorSuite;
                if(InnerActuatorSuites.TryAdd(ep.EndPoint, ep))
                {
                    _sourceDubboActuatorSuiteCollection[ep.EndPoint].ReferenceCount++;
                    UpdateTotalWeight();
                    return true;
                }
            }
            else
            {
                DubboActuatorSuiteEndPintInfo dubboActuatorSuiteEndPintInfo = new DubboActuatorSuiteEndPintInfo()
                {
                    EndPoint = ep.EndPoint,
                    ActuatorSuite = new DubboActuatorSuite(ep.EndPoint,new DubboActuatorSuiteConf() { 
                        AssistConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteAssistConnectionAliveTime, 
                        MasterConnectionAliveTime = _innerDubboServiceDriverConf.DubboActuatorSuiteMasterConnectionAliveTime,
                        DubboRequestTimeout=_innerDubboServiceDriverConf.DubboRequestTimeout, 
                        MaxConnections=_innerDubboServiceDriverConf.DubboActuatorSuiteMaxConnections }),
                    ReferenceCount = 0
                };
                ep.InnerDubboActuatorSuite = dubboActuatorSuiteEndPintInfo.ActuatorSuite;
                if (InnerActuatorSuites.TryAdd(ep.EndPoint, ep))
                {
                    dubboActuatorSuiteEndPintInfo.ReferenceCount++;
                    _sourceDubboActuatorSuiteCollection.Add(ep.EndPoint, dubboActuatorSuiteEndPintInfo);
                    UpdateTotalWeight();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 更新DubboServiceDriver服务节点
        /// </summary>
        /// <param name="dbEpList"></param>
        /// <returns>返回更新的节点数</returns>    
        /// <exception cref="ArgumentException"></exception>
        public int UpdateActuatorSuiteEndPoints(List<DubboServiceEndPointInfo> dbEpList)
        {
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            int changeCount=0;
            //移出多余节点
            foreach(var insItem in InnerActuatorSuites)
            {
                
                //if (!dbEpList.Contains(insItem.Key))
                if(dbEpList.FirstOrDefault((it) => it.EndPoint.Equals(insItem.Key)) == null)
                {
                    InnerActuatorSuites.Remove(insItem.Key);
                    changeCount++;
                    if(_sourceDubboActuatorSuiteCollection.ContainsKey(insItem.Key))
                    {
                        _sourceDubboActuatorSuiteCollection[insItem.Key].ReferenceCount--;
                        if(_sourceDubboActuatorSuiteCollection[insItem.Key].ReferenceCount<=0)
                        {
                            _sourceDubboActuatorSuiteCollection[insItem.Key].ActuatorSuite.Dispose();
                            _sourceDubboActuatorSuiteCollection.Remove(insItem.Key);
                        }
                    }
                    else
                    {
                        MyLogger.LogError($"[UpdateEqualIPEndPoints]_sourceDubboActuatorSuiteCollection not contain {insItem.Key}");
                    }
                }
            }
            //添加新增节点
            foreach(var epItem in dbEpList)
            {
                if(!InnerActuatorSuites.ContainsKey(epItem.EndPoint))
                {
                    if(AddActuatorSuite(epItem)) changeCount++;
                }
            }
            if(changeCount!=0)
            {
                UpdateTotalWeight();
            }
            return changeCount;
        }

        public DubboActuatorSuite GetDubboActuatorSuite(LoadBalanceMode loadBalanceMode)
        {
            LastActivateTime = DateTime.Now;
            if (InnerActuatorSuites.Count==0)
            {
                return null;
            }
            if (InnerActuatorSuites.Count == 1)
            {
                return InnerActuatorSuites.First().Value.InnerDubboActuatorSuite;
            }
            DubboServiceEndPointInfo dubboServiceEndPointInfo = null;
            switch (loadBalanceMode)
            {
                case LoadBalanceMode.Random:
                    Random random = new Random();
                    int randomNumber = random.Next(0, TotalWeightForActuatorSuites);
                    foreach(var weightedItem in InnerActuatorSuites)
                    {
                        if(randomNumber < weightedItem.Value.Weight)
                        {
                            dubboServiceEndPointInfo = weightedItem.Value;
                            break;
                        }
                    }
                    if(dubboServiceEndPointInfo ==null)
                    {
                        throw new Exception("[GetDubboActuatorSuite] fialed get DubboActuatorSuite ,that dubboServiceEndPointInfo is null");
                    }
                    break;
                case LoadBalanceMode.RoundRobin:
                    break;
                default: 
                    break;
            }
            //未实现
            return InnerActuatorSuites.First().Value.InnerDubboActuatorSuite;
        }

        public void Dispose()
        {
            if (InnerActuatorSuites != null)
            {
                foreach(var actuatorSuiteItem in InnerActuatorSuites)
                {
                    if(_sourceDubboActuatorSuiteCollection.ContainsKey(actuatorSuiteItem.Key))
                    {
                        _sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ReferenceCount--;
                        if(_sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ReferenceCount<=0)
                        {
                            _sourceDubboActuatorSuiteCollection[actuatorSuiteItem.Key].ActuatorSuite.Dispose();
                            _sourceDubboActuatorSuiteCollection.Remove(actuatorSuiteItem.Key);
                        }
                    }
                }
            }
            InnerActuatorSuites.Clear();
            InnerActuatorSuites = null;
            //_sourceDubboActuatorSuiteCollection是外部传入的公用对象，不要在这里做Clear操作
            _sourceDubboActuatorSuiteCollection = null;
        }
    }

}
