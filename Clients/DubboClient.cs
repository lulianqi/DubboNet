using DubboNet.DubboService;
using DubboNet.DubboService.DataModle;
using org.apache.zookeeper;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static DubboNet.DubboService.DubboActuator;

namespace DubboNet.Clients
{
    public class DubboClient
    {

        public enum LoadBalanceMode
        {
            Random,
            ShortestResponse,
            RoundRobin,
            LeastActive,
            ConsistentHash
        }

        private MyZookeeper _innerMyZookeeper;

        private DubboDriverCollection _dubboDriverCollection;

        private Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> _retainDubboActuatorSuiteCollection = new Dictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>();


        /// <summary>
        /// 获取只读的DubboActuatorSuiteCollection
        /// </summary>
        internal ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo> DubboActuatorSuiteCollection => new ReadOnlyDictionary<IPEndPoint, DubboActuatorSuiteEndPintInfo>(_retainDubboActuatorSuiteCollection);

        /// <summary>
        /// Zookeeper上默认的Dubbo跟路径，默认/dubbo/
        /// </summary>
        public string DubboRootPath { get; set; } = "/dubbo/";

        /// <summary>
        /// 默认当前Dubbo方法名称
        /// </summary>
        public string DefaultFuncName { get; private set; }

        /// <summary>
        /// 默认当前Dubbo服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }


        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString)
        {
            if (string.IsNullOrEmpty(zookeeperCoonectString))
            {
                throw new ArgumentException($"“{nameof(zookeeperCoonectString)}”不能为 null 或空。", nameof(zookeeperCoonectString));
            }
            _innerMyZookeeper = new MyZookeeper(zookeeperCoonectString);
            _dubboDriverCollection = new DubboDriverCollection(_retainDubboActuatorSuiteCollection);
            DefaultFuncName = null;
            DefaultServiceName = null;
        }

        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <param name="defaultServiceName">默认服务名称</param>
        /// <param name="defaultFuncName">默认方法名称</param>
        /// <exception cref="Exception"></exception>
        public DubboClient(string zookeeperCoonectString, string defaultServiceName, string defaultFuncName) : this(zookeeperCoonectString)
        {
            DefaultServiceName = defaultServiceName;
            DefaultFuncName = defaultFuncName;
            if (string.IsNullOrEmpty(DefaultServiceName) && !string.IsNullOrEmpty(DefaultFuncName))
            {
                throw new Exception("defaultServiceName can not be null unless defaultFuncName is null");
            }
        }

        /// <summary>
        /// 初始化DubboClient
        /// </summary>
        /// <param name="zookeeperCoonectString">zk连接字符串（多个地址,隔开）</param>
        /// <param name="endPointFuncFullName">默认方法入口（完整名称包括服务名称）</param>
        /// <exception cref="ArgumentException"></exception>
        public DubboClient(string zookeeperCoonectString, string endPointFuncFullName) : this(zookeeperCoonectString)
        {
            if (DefaultFuncName.Contains('#'))
            {
                int tempSpitIndex = DefaultFuncName.LastIndexOf('#');
                DefaultFuncName = DefaultFuncName.Substring(tempSpitIndex + 1);
                DefaultServiceName = DefaultFuncName.Remove(tempSpitIndex);
            }
            else if (DefaultFuncName.Contains('.'))
            {
                int tempSpitIndex = DefaultFuncName.LastIndexOf('.');
                DefaultFuncName = DefaultFuncName.Substring(tempSpitIndex + 1);
                DefaultServiceName = DefaultFuncName.Remove(tempSpitIndex);
            }
            else
            {
                throw new ArgumentException($"“{nameof(endPointFuncFullName)}” is error", nameof(endPointFuncFullName));
            }
        }


        public async Task<DubboRequestResult> SendRequestAsync(string req)
        {
            throw new NotImplementedException();
        }

        public async Task Test()
        {
            await InitServiceHost();
        }

        private async Task InitServiceHost()
        {
            string nowFuncPath = $"{DubboRootPath}{DefaultServiceName}";
            ZNode tempZNode = await _innerMyZookeeper.GetZNodeTree(nowFuncPath);
            if (tempZNode == null)
            {
                throw new Exception($"[GetServiceHost] error : can not GetZNodeTree from {DefaultServiceName} ");
            }
            List<ZNode> providersNodes = GetDubboProvidersNode(tempZNode);
            //删除已经无效的节点
            if (_dubboDriverCollection.Count > 0)
            {
                Func<DubboActuator, bool> filterFunc = new Func<DubboActuator, bool>((dt) =>
                {
                    bool isSholdAlive = false;
                    foreach (ZNode providerNode in providersNodes)
                    {
                        string path = System.Web.HttpUtility.UrlDecode(providerNode.Path, System.Text.Encoding.UTF8);
                        Uri uri = new Uri(path);
                        if (dt.DubboHost == uri.Host && dt.DubboPort == uri.Port)
                        {
                            isSholdAlive = true;
                            break;
                        }
                    }
                    return !isSholdAlive;
                });
                //_dubboDriverCollection.RemoveByFilter(filterFunc);
            }
            //添加新节点
            foreach (ZNode providerNode in providersNodes)
            {
                string path = System.Web.HttpUtility.UrlDecode(providerNode.Path, System.Text.Encoding.UTF8);
                Uri uri = new Uri(path);
                //if (!_dubboDriverCollection.IsInclude(uri.Host, uri.Port))
                //{
                //    _dubboDriverCollection.AddDubboMan(uri.Host, uri.Port);
                //}
            }

        }


        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private List<ZNode> GetDubboProvidersNode(ZNode zNode)
        {
            ZNode dubboNodes = zNode.FilterLeafNode(nd => nd.Path?.StartsWith("dubbo%3A%2F%2F") ?? false, "DubboNode");
            List<ZNode> resultZNodes = new List<ZNode>();
            foreach (var tn in dubboNodes.Children)
            {
                if (tn.Path == "providers")
                {
                    //resultZNodes.AddRange(tn.GetLeafNodeList());
                    resultZNodes.AddRange(tn.Children);
                }
            }
            return resultZNodes;
        }
    }
}
