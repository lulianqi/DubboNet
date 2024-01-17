using DubboNet.DubboService;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using static DubboNet.Clients.DubboClient;

namespace DubboNet.Clients
{
    internal class DubboServiceDriver:IDisposable
    {
        public string ServiceName { get; private set; }

        /// <summary>
        /// 当前DubboServiceDriver可使用的各EndPoint的DubboActuatorSuite集合
        /// </summary>
        public Dictionary<IPEndPoint, DubboActuatorSuite> InnerActuatorSuites { get; private set; }

        /// <summary>
        /// DubboClient的源ActuatorSuiteCollection（不要直接使用，保留的这份引用是为了释放时同时清理）
        /// </summary>
        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _sourceDubboActuatorSuiteCollection;
        
        /// <summary>
        /// 初始化DubboServiceDriver
        /// </summary>
        /// <param name="serviceName">当前服务的serviceName</param>
        /// <param name="dbEpList">当前服务节点列表</param>
        /// <param name="dubboActuatorSuiteCollection">内部dubboActuatorSuiteCollection</param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        public DubboServiceDriver(string serviceName, List<IPEndPoint> dbEpList, Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> dubboActuatorSuiteCollection)
        {
            ServiceName = serviceName;
            _sourceDubboActuatorSuiteCollection = dubboActuatorSuiteCollection;
            InnerActuatorSuites = new Dictionary<IPEndPoint, DubboActuatorSuite>();
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            if (dubboActuatorSuiteCollection == null)
            {
                throw new ArgumentNullException(nameof(dubboActuatorSuiteCollection));
            }
            foreach (IPEndPoint ep in dbEpList)
            {
                AddActuatorSuite(ep);
            }
        }

        /// <summary>
        /// 通过IPEndPoint添加ActuatorSuite (内部函数)
        /// </summary>
        /// <param name="ep">IPEndPoint</param>
        private void AddActuatorSuite(IPEndPoint ep)
        {
            if (_sourceDubboActuatorSuiteCollection.ContainsKey(ep))
            {
                InnerActuatorSuites.Add(ep, _sourceDubboActuatorSuiteCollection[ep].ActuatorSuite);
                _sourceDubboActuatorSuiteCollection[ep].ReferenceCount++;
            }
            else
            {
                DubboActuatorSuiteEndPintInfo dubboActuatorSuiteEndPintInfo = new DubboActuatorSuiteEndPintInfo()
                {
                    EndPoint = ep,
                    ActuatorSuite = new DubboActuatorSuite(ep),
                    ReferenceCount = 0
                };
                InnerActuatorSuites.Add(ep, dubboActuatorSuiteEndPintInfo.ActuatorSuite);
                dubboActuatorSuiteEndPintInfo.ReferenceCount++;
                _sourceDubboActuatorSuiteCollection.Add(ep, dubboActuatorSuiteEndPintInfo);
            }
        }

        /// <summary>
        /// 更新DubboServiceDriver服务节点
        /// </summary>
        /// <param name="dbEpList"></param>
        /// <exception cref="ArgumentException"></exception>
        public void UpdateEqualIPEndPoints(List<IPEndPoint> dbEpList)
        {
            if (!(dbEpList?.Count > 0))
            {
                throw new ArgumentException("dbEpList can not be empty", nameof(dbEpList));
            }
            //移出多余节点
            foreach(var insItem in  InnerActuatorSuites)
            {
                if(!dbEpList.Contains(insItem.Key))
                {
                    InnerActuatorSuites.Remove(insItem.Key);
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
                        MyLogger.LogWarning($"[UpdateEqualIPEndPoints]_sourceDubboActuatorSuiteCollection not contain {insItem.Key}");
                    }
                }
            }
            //添加新增节点
            foreach(var epItem in dbEpList)
            {
                if(!InnerActuatorSuites.ContainsKey(epItem))
                {
                    AddActuatorSuite(epItem);
                }
            }

        }

        public DubboActuatorSuite GetDubboActuatorSuite(LoadBalanceMode loadBalanceMode)
        {
            //未实现
            return InnerActuatorSuites.First().Value;
        }

        public void Dispose()
        {
            if (InnerActuatorSuites != null)
            {
                foreach(var ias in InnerActuatorSuites)
                {
                    if(_sourceDubboActuatorSuiteCollection.ContainsKey(ias.Key))
                    {
                        _sourceDubboActuatorSuiteCollection[ias.Key].ReferenceCount--;
                        if(_sourceDubboActuatorSuiteCollection[ias.Key].ReferenceCount<=0)
                        {
                            _sourceDubboActuatorSuiteCollection[ias.Key].ActuatorSuite.Dispose();
                            _sourceDubboActuatorSuiteCollection.Remove(ias.Key);
                        }
                    }
                }
            }
            InnerActuatorSuites = null;
            _sourceDubboActuatorSuiteCollection = null;
        }
    }

}
