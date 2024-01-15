using DubboNet.DubboService;
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
                if (dubboActuatorSuiteCollection.ContainsKey(ep))
                {
                    InnerActuatorSuites.Add(ep, dubboActuatorSuiteCollection[ep].ActuatorSuite);
                    dubboActuatorSuiteCollection[ep].ReferenceCount++;
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
                    dubboActuatorSuiteCollection.Add(ep, dubboActuatorSuiteEndPintInfo);
                }
            }
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
