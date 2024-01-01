using DubboNet.DubboService.DataModle;
using org.apache.zookeeper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DubboNet.DubboService.DubboActuator;

namespace DubboNet.DubboService
{
    public class DubboClient
    {
        public class DubboManCollection
        {
            private List<DubboActuator> dubboActuators = new List<DubboActuator>();

            public int MaxUsersNum { get; set; } = 0;
            public int CommandTimeout { get; set; } = 10 * 1000;
            public string DefaultServiceName { get; set; }

            public int Count { get { return dubboActuators?.Count ?? 0; } }

            public bool IsInclude(string host, int port)
            {
                foreach (var man in dubboActuators)
                {
                    if (man.DubboHost == host && man.DubboPort == port)
                    {
                        return true;
                    }
                }
                return false;
            }

            public DubboActuator GetDubboActuator()
            {
                return dubboActuators.FirstOrDefault();
                foreach (var actuator in dubboActuators)
                {
                    //if(actuator.IsQuerySending)
                }
            }

            public async Task<DubboRequestResult> SendRequestAsync(string funcEntrance, string req)
            {
                DubboActuator nowDubboActuator = GetDubboActuator();
                return await nowDubboActuator.SendQuery(funcEntrance, req);
            }

            public void AddDubboMan(string address, int port)
            {
                dubboActuators.Add(new DubboActuator(address, port, CommandTimeout, DefaultServiceName));
            }

            public void RemoveByFilter(Func<DubboActuator, bool> filterFunc)
            {
                DubboActuator[] dubboArr = dubboActuators.ToArray();
                foreach (DubboActuator dbm in dubboArr)
                {
                    if (filterFunc(dbm))
                    {
                        dubboActuators.Remove(dbm);
                    }
                }
            }
        }

        public enum LoadBalanceMode
        {
            Random,
            ShortestResponse,
            RoundRobin,
            LeastActive,
            ConsistentHash
        }

        private MyZookeeper _innerMyZookeeper;

        private DubboManCollection _dubboManCollection = new DubboManCollection();

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
        public DubboClient(string zookeeperCoonectString, string defaultServiceName , string defaultFuncName) : this(zookeeperCoonectString)
        {
            DefaultServiceName = defaultServiceName;
            DefaultFuncName = defaultFuncName;
            if (string.IsNullOrEmpty(DefaultServiceName)&& !string.IsNullOrEmpty(DefaultFuncName))
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
        public DubboClient(string zookeeperCoonectString, string endPointFuncFullName):this(zookeeperCoonectString)
        {
            if(DefaultFuncName.Contains('#'))
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
            if (_dubboManCollection.Count == 0)
            {
                await InitServiceHost();
            }
            return await _dubboManCollection.SendRequestAsync($"{DefaultServiceName}.{DefaultFuncName}", req);
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
            if (_dubboManCollection.Count > 0)
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
                _dubboManCollection.RemoveByFilter(filterFunc);
            }
            //添加新节点
            foreach (ZNode providerNode in providersNodes)
            {
                string path = System.Web.HttpUtility.UrlDecode(providerNode.Path, System.Text.Encoding.UTF8);
                Uri uri = new Uri(path);
                if (!_dubboManCollection.IsInclude(uri.Host, uri.Port))
                {
                    _dubboManCollection.AddDubboMan(uri.Host, uri.Port);
                }
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
