using DubboNet.DubboService.DataModle.DubboInfo;
using MyCommonHelper;
using NetService.Telnet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
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
            /// 获取InnerDubboActuator是否处于请求发送中状态
            /// </summary>
            public bool IsFreeForQuery => IsAlive && (!InnerDubboActuator?.IsQuerySending ?? false);
            /// <summary>
            /// 获取InnerDubboActuator是否处于使用队列中
            /// </summary>
            public bool IsInUsedQueue => InnerDubboActuator?.IsInUsedQueue ?? false;

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
            public int MasterConnectionAliveTime { get; set; } = 60 * 20;
            public int DubboRequestTimeout { get; set; } = 10 * 1000;
            public string DefaultServiceName { get; set; } = null;

        }

        //内部DubboSuiteCell（只要DubboActuatorSuite初始化_actuatorSuiteCellList就不会为null）
        private List<DubboSuiteCell> _actuatorSuiteCellList;
        //标记当前Cruise是否正在进行中
        private bool _innerFlagForInCruiseTask = false;
        //在高并发场景下用于通知GetAvailableDubboActuatorAsync获取资源的合适时机
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
        /// 辅助执行单元连接的最大保活时间（单位秒，默认300s，0表示永久保活）
        /// </summary>
        public int AssistConnectionAliveTime { get;private set; } = 60 * 5;

        /// <summary>
        /// 主执行单元连接的最大保活时间（单位秒，默认1200s ，0表示永久保活）
        /// </summary>
        public int MasterConnectionAliveTime { get;private set; } = 60 * 20;


        /// <summary>
        /// 当前DubboActuatorSuite是否可用（节点地址错误，都会导致连接失败，且这种错误不能通过自动重试恢复，
        /// </summary>
        public bool IsRead { get; private set; } = true;

        /// <summary>
        /// 最后激活时间，覆盖基类DubboActuator中的LastActivateTime属性，不是里面每个套接字的最后激活时间
        /// 这里的LastActivateTime是整个DubboActuatorSuite的最后激活时间（只关心调用DubboActuatorSuite发送请求，不关心内部诊断请求，而DubboActuator中的LastActivateTime是成功调用发送命令的时间，包括诊断请求）
        /// </summary>
        public new DateTime LastActivateTime { get; private set; }=DateTime.Now;

        /// <summary>
        /// 获取默认服务名称
        /// </summary>
        public new string DefaultServiceName { get; private set; }

        /// <summary>
        /// 获取当前DubboActuatorSuite内所有DubboSuiteCell执行单元
        /// </summary>
        internal ReadOnlyCollection<DubboSuiteCell> ReadOnlyList => _actuatorSuiteCellList.AsReadOnly();
        /// <summary>
        /// 获取当前节点Status信息
        /// </summary>
        public DubboStatusInfo StatusInfo { get;private set; }

        #region 静态成员
        //DubboSuiteTimer执行的job间隔时间（单位毫秒）
        private const int InnetTimerInterval = 1000 * 10;
        //定时器是全局公用的，无论有多少DubboActuatorSuite实例正在运行，都将最多只有一个定时器
        private static System.Timers.Timer DubboSuiteTimer;
        //StatusInfo更新时间间隔
        private const int StatusInfoIntervalTime = 1000 * 20;
        //StatusInfo更新进入休眠状态需要的时间
        private const int StatusInfoDormantIntervalTime = 1000 * 120;
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
                MasterConnectionAliveTime = dubboActuatorSuiteConf.MasterConnectionAliveTime;
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
                            //主连接
                            if(dubboSuiteCell.InnerDubboActuator==this)
                            {
                                if(MasterConnectionAliveTime==0)
                                {
                                    continue;
                                }
                                if((e.SignalTime - this.LastActivateTime).TotalSeconds > MasterConnectionAliveTime)
                                {
                                    dubboSuiteCell.InnerDubboActuator.DisConnect();
                                    //主连接关闭后，自动关闭所有辅助连接
                                    foreach(DubboSuiteCell cell in _actuatorSuiteCellList)
                                    {
                                        if(dubboSuiteCell.IsAlive)
                                        {
                                            cell.InnerDubboActuator.DisConnect();
                                        }
                                    }
                                    break;
                                    
                                }
                            }
                            else if(AssistConnectionAliveTime!=0 && (e.SignalTime - dubboSuiteCell.LastActivateTime).TotalSeconds > AssistConnectionAliveTime)
                            {
                                dubboSuiteCell.InnerDubboActuator.DisConnect();
                            }
                        }
                    }
                    //是否需要静默,静默不再更新StatusInfo
                    bool isShouldSilent = MasterConnectionAliveTime!=0 && (e.SignalTime - this.LastActivateTime).TotalSeconds > MasterConnectionAliveTime;
                    if(isShouldSilent)
                    {
                        return;
                    }
                    //更新StatusInfo，有以下3种情况需要更新StatusInfo
                    //StatusInfo为null时，首次更新
                    //当LastActivateTime没有超过StatusInfoDormantIntervalTime时，活跃状态状态，更新间隔为StatusInfoIntervalTime
                    //当LastActivateTime已经超过StatusInfoDormantIntervalTime时，进入休眠状态，更新间隔为StatusInfoDormantIntervalTime
                    if((StatusInfo==null) || 
                    ((DateTime.Now - LastActivateTime).TotalMilliseconds <=  StatusInfoDormantIntervalTime && (DateTime.Now-StatusInfo.InfoCreatTime).TotalMilliseconds > StatusInfoIntervalTime )||
                    ((DateTime.Now - LastActivateTime).TotalMilliseconds >  StatusInfoDormantIntervalTime && (DateTime.Now-StatusInfo.InfoCreatTime).TotalMilliseconds > StatusInfoDormantIntervalTime ) )
                    {

                        //获取最新节点信息（GetDubboStatusInfoAsync调用的是基类的SendCommandAsync，所以一定是由主节点执行，同时不会更新重写的LastActivateTime属性）
                        //StatusInfo = base.GetDubboStatusInfoAsync().GetAwaiter().GetResult();
                        StatusInfo = this.GetDubboStatusInfoAsync().GetAwaiter().GetResult();
                        if(StatusInfo==null)
                        {
                            throw new Exception(this.NowErrorMes);
                        }
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
                //如果GetAvailableDubboActuator已经不能返回可用资源，马上再次GetAvailableDubboActuator大概率同样无法有空闲资源，使用_eventWaitHandle通知（SendCommandAsync完成后会有信号，这个时候同时意味着有资源被释放）可以避免高频调用，同时带来即时性能
                //_eventWaitHandle.WaitOne();
                await _eventWaitHandle.WaitOneAsync(remainingTimeout);
                nowDubboActuator = GetAvailableDubboActuator();
            }
            return nowDubboActuator;
        }

        /// <summary>
        /// 获取一个可用的DubboActuator（如果没有则返回null,返回的DubboActuator的IsInUsedQueue属性会被设置为true）
        /// </summary>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private DubboActuator GetAvailableDubboActuator()
        {
            if (!IsRead)
            {
                return null;
            }
            if (!this.IsInUsedQueue && !this.IsQuerySending)
            {
                MyLogger.LogDebug($"[GetAvailableDubboActuator]B[{this.DubboActuatorGUID}]{DateTime.Now.Millisecond}-{DateTime.Now.Ticks}");
                //注意this的运行时类型是DubboActuatorSuite
                this.IsInUsedQueue=true;
                return this;
            }
            else
            {
                //空闲且处于连接状态的DubboActuator
                DubboSuiteCell nowDubboSuiteCell = _actuatorSuiteCellList.FirstOrDefault<DubboSuiteCell>((dsc) => !dsc.IsInUsedQueue && dsc.IsFreeForQuery);
                if (nowDubboSuiteCell == null)
                {
                    //选择未连接的DubboActuator
                    nowDubboSuiteCell = _actuatorSuiteCellList.FirstOrDefault<DubboSuiteCell>((dsc) => !dsc.IsInUsedQueue && !dsc.IsAlive);
                }
                if (nowDubboSuiteCell != null)
                { 
                    nowDubboSuiteCell.Version++;
                    nowDubboSuiteCell.InnerDubboActuator.IsInUsedQueue=true;
                }
                MyLogger.LogDebug($"[GetAvailableDubboActuator]A[{nowDubboSuiteCell.InnerDubboActuator.DubboActuatorGUID}]{DateTime.Now.Millisecond}-{DateTime.Now.Ticks}");
                return nowDubboSuiteCell?.InnerDubboActuator;
            }
        }

        /// <summary>
        /// 重写SendCommandAsync可用改变所有基类SendQuery行为，因为所有SendQuery最终出口都是SendCommandAsync
        /// </summary>
        /// <param name="command"></param>
        /// <param name="isDiagnosisCommand">是否为诊断命令，默认false（内部包装好的的非invoke控制命令）</param>
        /// <returns></returns>
        internal override async Task<TelnetRequestResult> SendCommandAsync(string command ,bool isDiagnosisCommand = false)
        {
            DubboActuator availableDubboActuator =await GetAvailableDubboActuatorAsync(base.DubboRequestTimeout);
            if(availableDubboActuator==null)
            {
                return null;
            }
            else
            {
                //诊断命令不更新DubboActuatorSuite的LastActivateTime
                if(!isDiagnosisCommand)
                {
                    LastActivateTime = DateTime.Now;
                }
                try
                {
                    MyLogger.LogDebug($"[SendCommandAsync]{DateTime.Now}-{availableDubboActuator.DubboActuatorGUID}");
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
                    availableDubboActuator.IsInUsedQueue=false;
                    _eventWaitHandle.Set();
                }
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
