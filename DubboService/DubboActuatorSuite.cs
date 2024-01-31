using DubboNet.DubboService.DataModle.DubboInfo;
using MyCommonHelper;
using NetService.Telnet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using static NetService.Telnet.ExTelnet;

namespace DubboNet.DubboService
{
    public class DubboActuatorSuite : DubboActuator , IDisposable
    {
        internal class DubboSuiteCell
        {
            /// <summary>
            /// DubboActuator执行器
            /// </summary>
            public DubboActuator InnerDubboActuator {get;private set;}
            /// <summary>
            /// 标记当前DubboSuiteCell被使用过的次数
            /// </summary>
            internal int Version { get; set; } = 0;
            /// <summary>
            /// DubboSuiteCell创建时间
            /// </summary>
            public DateTime CreatTime {get;}=DateTime.Now;
            /// <summary>
            /// 最后激活即发送请求的时间
            /// </summary>
            public DateTime LastActivateTime => InnerDubboActuator?.LastActivateTime ?? default;
            /// <summary>
            /// 内部DubboActuator执行器是否处于连接状态
            /// </summary>
            public bool IsAlive => InnerDubboActuator?.IsConnected ?? false;
            /// <summary>
            /// 
            /// </summary>
            public bool IsFreeForQuery => IsAlive && (!InnerDubboActuator?.IsQuerySending ?? false);

            /// <summary>
            /// 初始化DubboSuiteCell
            /// </summary>
            /// <param name="dubboActuator">DubboActuator执行器</param>
            public DubboSuiteCell(DubboActuator dubboActuator) => InnerDubboActuator = dubboActuator;
        }

        public class DubboActuatorSuiteConf
        {
            public int MaxConnections { get; set; } = 20;
            public int AssistConnectionAliveTime { get; set; } = 60 * 5;
            public int DubboRequestTimeout { get; set; } = 10 * 1000;
            public string DefaultServiceName { get; set; } = null;

        }

        //内部DubboSuiteCell（只要DubboActuatorSuite初始化_actuatorSuiteCellList就不会为null）
        private List<DubboSuiteCell> _actuatorSuiteCellList;
        //标记当前Cruise是否正在进行中
        private bool _innerFlagForInCruiseTask = false;

        private EventWaitHandle _eventWaitHandle = null;

        /// <summary>
        /// 获取当前节点服务及Func信息
        /// </summary>
        public Dictionary<string, Dictionary<string, DubboFuncInfo>> DubboServiceFuncCollection { get; private set; }

        /// <summary>
        /// 获取默认服务的Func信息
        /// </summary>
        public Dictionary<string, DubboFuncInfo> DefaulDubboServiceFuncs { get; private set; }

        /// <summary>
        /// 获取当前DubboActuatorSuite最大连接数（最小为1，默认为20，更大的连接数可以让当前客户端拥有更高的并发能力，注意这里只是最大默认没有使用的执行单元不会连接，长时间未激活的连接也会主动关闭）
        /// </summary>
        public int MaxConnections { get;private set; } = 20;

        /// <summary>
        /// 辅助执行单元连接的最大保活时间（单位秒，默认300s）
        /// </summary>
        public int AssistConnectionAliveTime { get;private set; } = 60 * 5;


        /// <summary>
        /// 当前DubboActuatorSuite是否可用（节点地址错误，都会导致连接失败，且这种错误不能通过自动重试恢复，
        /// </summary>
        public bool IsRead { get; private set; } = true;


        /// <summary>
        /// 获取默认服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }

        /// <summary>
        /// 获取当前DubboActuatorSuite内所有DubboSuiteCell执行单元
        /// </summary>
        internal ReadOnlyCollection<DubboSuiteCell> ReadOnlyList => _actuatorSuiteCellList.AsReadOnly();
        /// <summary>
        /// 获取当前节点Status信息
        /// </summary>
        public DubboStatusInfo StatusInfo { get;private set; }

        #region 静态成员
        private const int InnetTimerInterval = 1000 * 10;
        //定时器是全局公用的，无论有多少DubboActuatorSuite实例正在运行，都将最多只有一个定时器
        private static System.Timers.Timer DubboSuiteTimer;
        protected delegate void DubboSuiteCruiseEventHandler(object sender, ElapsedEventArgs e);
        protected static event DubboSuiteCruiseEventHandler DubboSuiteCruiseEvent;

        /// <summary>
        /// 静态构造函数
        /// </summary>
        static DubboActuatorSuite()
        {
            DubboSuiteTimer = new System.Timers.Timer(InnetTimerInterval);
            DubboSuiteTimer.Elapsed += OnDubboSuiteTimedEvent;
            DubboSuiteTimer.AutoReset = true;
            //不用直接启动Timer，在每一个DubboActuatorSuite实例化时判断是否需要启动，释放过程中判断DubboSuiteTimer是否需要被复用，如果所有引用都被释放则自动停止，以尽可能减少定时器的存在。
            DubboSuiteTimer.Enabled = false;
        }

        /// <summary>
        /// DubboSuiteTimer 事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void OnDubboSuiteTimedEvent(object sender, ElapsedEventArgs e)
        {
            if (DubboSuiteCruiseEvent == null || DubboSuiteCruiseEvent.GetInvocationList().Length == 0)
            {
                DubboSuiteTimer.Stop();
            }
            else
            {
                //对于System.Timers.Timer来说,每一次Elapsed事件都是异步的，也就是说多次Elapsed是可能在同时运行的
                //不过对于Invoke来说DubboSuiteCruiseEvent如果被注册多次，Invoke是同步的轮流执行，这里需要在逻辑上避免Timer会有2个Elapsed同时执行
                DubboSuiteCruiseEvent.Invoke(sender, e);
            }
        }
        #endregion

        /// <summary>
        /// 初始化DubboActuatorSuite
        /// </summary>
        /// <param name="Address">地址（ip）</param>
        /// <param name="Port">端口</param>
        /// <param name="CommandTimeout">客户端请求命令的超时时间（毫秒为单位，默认10秒）</param>
        /// <param name="dubboActuatorSuiteConf">DubboActuatorSuiteConf配置</param>
        public DubboActuatorSuite(string Address, int Port, DubboActuatorSuiteConf dubboActuatorSuiteConf = null) : base(Address, Port, dubboActuatorSuiteConf?.DubboRequestTimeout?? 10 * 1000, dubboActuatorSuiteConf?.DefaultServiceName)
        {
            if(dubboActuatorSuiteConf!=null)
            {
                DefaultServiceName = dubboActuatorSuiteConf.DefaultServiceName;
                AssistConnectionAliveTime = dubboActuatorSuiteConf.AssistConnectionAliveTime;
                MaxConnections = dubboActuatorSuiteConf.MaxConnections;
            }
            _eventWaitHandle = new EventWaitHandle(true, EventResetMode.ManualReset);
            _actuatorSuiteCellList = new List<DubboSuiteCell> {new DubboSuiteCell(this)};
            if(MaxConnections<1) MaxConnections =1;
            for (int i = 1; i < MaxConnections; i++)
            {
                _actuatorSuiteCellList.Add(new DubboSuiteCell((DubboActuator)(base.Clone())));
            }
            DubboSuiteCruiseEvent += CruiseTaskEvent;
            if(!DubboSuiteTimer.Enabled) DubboSuiteTimer.Start();
        }

        /// <summary>
        /// 初始化DubboActuatorSuite
        /// </summary>
        /// <param name="iPEndPoint"></param>
        /// <param name="CommandTimeout">客户端请求命令的超时时间（毫秒为单位，默认10秒）</param>
        /// <param name="dubboActuatorSuiteConf">DubboActuatorSuiteConf配置</param>
        public DubboActuatorSuite(IPEndPoint iPEndPoint, DubboActuatorSuiteConf dubboActuatorSuiteConf = null):this(iPEndPoint.Address.ToString(), iPEndPoint.Port,dubboActuatorSuiteConf)
        {
        }

        /// <summary>
        /// Cruises事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CruiseTaskEvent(object sender, ElapsedEventArgs e)
        {
            if(_innerFlagForInCruiseTask)
            {
                MyLogger.LogWarning($"[{this}] last CruiseTaskEvent is not complete");
                return;
            }
            _innerFlagForInCruiseTask = true;
            try
            {
                if (IsRead)
                {
                    //维持DubboSuiteCell状态
                    foreach(DubboSuiteCell dubboSuiteCell in _actuatorSuiteCellList)
                    {
                        if(dubboSuiteCell.IsAlive)
                        {
                            if((e.SignalTime - dubboSuiteCell.LastActivateTime).TotalSeconds> AssistConnectionAliveTime)
                                dubboSuiteCell.InnerDubboActuator.DisConnect();
                        }
                    }
                    //获取最新节点信息
                    StatusInfo = this.GetDubboStatusInfoAsync().GetAwaiter().GetResult();
                    if(StatusInfo==null)
                    {
                        throw new Exception(this.NowErrorMes);
                    }
                }
            } 
            catch (Exception ex)
            {
                MyLogger.LogError($"[{this}]CruiseTaskEvent Exception", ex);
            }
            finally
            {
                _innerFlagForInCruiseTask = false;
            }
        }

        /// <summary>
        ///  异步获取一个可用的DubboActuator（因为有极限压测的场景，所有DubboSuiteCell可能都会被耗尽所以需要一个低消耗的异步等待）
        /// </summary>
        /// <param name="millisecondTimeout">超时时间，如果超过指定时间还没有可用DubboActuator，则直接返回null</param>
        /// <returns></returns>
        private async ValueTask<DubboActuator>  GetAvailableDubboActuatorAsync(int millisecondTimeout=1000*30)
        {
            if (millisecondTimeout < -1)
            {
                throw new Exception("millisecondTimeout can not less than 0");
            }
            DateTime startTime = DateTime.Now;
            DubboActuator nowDubboActuator = GetAvailableDubboActuator();
            while (nowDubboActuator == null)
            {
                int remainingTimeout = millisecondTimeout - (int)((DateTime.Now - startTime).TotalMilliseconds);
                if (remainingTimeout<=0)
                {
                    break;
                }
                //_eventWaitHandle.WaitOne();
                await _eventWaitHandle.WaitOneAsync(remainingTimeout);
                nowDubboActuator = GetAvailableDubboActuator();
            }
            return nowDubboActuator;
        }

        /// <summary>
        /// 获取一个可用的DubboActuator（如果没有则返回null）
        /// </summary>
        /// <returns></returns>
        private DubboActuator GetAvailableDubboActuator()
        {
            if (!IsRead)
            {
                return null;
            }
            if (!this.IsQuerySending)
            {
                //注意this的运行时类型是DubboActuatorSuite
                return this;
            }
            else
            {
                DubboSuiteCell nowDubboSuiteCell = _actuatorSuiteCellList.FirstOrDefault<DubboSuiteCell>((dsc) => dsc.IsFreeForQuery);
                if (nowDubboSuiteCell == null)
                {
                    nowDubboSuiteCell = _actuatorSuiteCellList.FirstOrDefault<DubboSuiteCell>((dsc) => !dsc.IsAlive);
                }
                if (nowDubboSuiteCell != null) nowDubboSuiteCell.Version++;
                return nowDubboSuiteCell?.InnerDubboActuator;
            }
        }

        /// <summary>
        /// 重写SendCommandAsync可用改变所有基类SendQuery行为，因为所有SendQuery最终出口都是SendCommandAsync
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        internal override async Task<TelnetRequestResult> SendCommandAsync(string command)
        {
            DubboActuator availableDubboActuator =await GetAvailableDubboActuatorAsync(base.DubboRequestTimeout);
            if(availableDubboActuator==null)
            {
                return null;
            }
            else
            {
                try
                {
                    TelnetRequestResult telnetRequestResult = null;
                    //因为GetAvailableDubboActuatorAsync返回的DubboActuator可能是DubboActuatorSuite，如果是DubboActuatorSuite会循环调用override的SendCommandAsync方法，这里需要区分开
                    if (availableDubboActuator == this)
                    {
                        telnetRequestResult = await base.SendCommandAsync(command);
                    }
                    else
                    {
                        telnetRequestResult = await availableDubboActuator.SendCommandAsync(command);
                    }
                        
                    if (telnetRequestResult == null)
                    {
                        throw new Exception(availableDubboActuator.NowErrorMes);
                    }
                    return telnetRequestResult;
                }
                catch (Exception ex)
                {
                    MyLogger.LogError("[DubboActuatorSuite -> SendCommandAsync] ", ex);
                    return null;
                }
                finally
                {
                    _eventWaitHandle.Set();
                }
            }
        }


        /// <summary>
        /// 初始化DubboTesterSuite，获取Func列表及详情
        /// </summary>
        /// <param name="serviceName">serviceName将被设置为DefaultServiceName，并且只会获取DefaultService里的Func（默认为空将使用DefaultServiceName，如果DefaultServiceName为空，将获取所有service里的Func列表,如果使用*将将DefaultServiceName设置为null）</param>
        /// <returns>是否成功（成功后DubboServiceFuncCollection将被跟新，否则DubboServiceFuncCollection被清空）</returns>
        public async ValueTask<bool> InitServiceAsync(string serviceName = null)
        {
            if (!string.IsNullOrEmpty(serviceName))
            {
                if (serviceName == "*")
                {
                    DefaultServiceName = null;
                }
                DefaultServiceName = serviceName;
            }
            DubboServiceFuncCollection = new Dictionary<string, Dictionary<string, DubboFuncInfo>>();
            //空的serviceNam且也没有默认值的情况下获取当前主机上所有服务提供的方法列表
            if (string.IsNullOrEmpty(DefaultServiceName))
            {
                //DefaulDubboServiceFuncs = new Dictionary<string, DubboFuncInfo>();
                List<string> tempSeviceList = (await GetDubboLsInfoAsync())?.Providers;
                if (tempSeviceList?.Count > 0)
                {
                    foreach (var nowService in tempSeviceList)
                    {
                        Dictionary<string, DubboFuncInfo> tempDc = await GetDubboServiceFuncAsync(nowService);
                        if (tempDc == null)
                        {
                            MyLogger.LogError($"GetDubboServiceFuncAsyncfailed in[InitServiceAsync] that Service is {nowService}");
                            continue;
                        }
                        DubboServiceFuncCollection.Add(nowService, tempDc);
                        //foreach(var tempFunc in tempDc)
                        //{
                        //    if (!DefaulDubboServiceFuncs.TryAdd(tempFunc.Key, tempFunc.Value))
                        //    {
                        //        ShowError($"DubboServiceFuncDc TryAdd failed in[InitServiceAsync] that key is {tempFunc.Key}");
                        //    }
                        //}
                    }
                }
                return DubboServiceFuncCollection?.Count > 0;
            }
            else
            {
                DefaulDubboServiceFuncs = await GetDubboServiceFuncAsync(DefaultServiceName);
                if (DefaulDubboServiceFuncs != null)
                {
                    DubboServiceFuncCollection.Add(DefaultServiceName, DefaulDubboServiceFuncs);
                }
                return DefaulDubboServiceFuncs != null;
            }
        }


        public new void Dispose()
        {
            //取消事件订阅，如果需要暂停DubboSuiteTimer
            DubboSuiteCruiseEvent -= CruiseTaskEvent;
            if (DubboSuiteCruiseEvent == null || DubboSuiteCruiseEvent.GetInvocationList().Length == 0)
            {
                DubboSuiteTimer.Stop();
            }
            //释放连接
            foreach(DubboSuiteCell dscItem in _actuatorSuiteCellList)
            {
                dscItem.InnerDubboActuator.Dispose();
            }
            base.Dispose();
        }

    }
}
