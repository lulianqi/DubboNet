﻿using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Timers;

namespace DubboNet.DubboService
{
    public class DubboActuatorSuite : DubboActuator , IDisposable
    {
        internal class DubboSuiteCell
        { 
            public DubboActuator InnerDubboActuator {get;private set;}
            public DateTime CreatTime {get;}=DateTime.Now;
            public DateTime ConnectedTime{get;set;}
            public DateTime LastActivateTime => InnerDubboActuator?.LastActivateTime ?? default;
            public bool NeedKeepAlive{get;set;}=false;
            public DubboSuiteCell(DubboActuator dubboActuator) => InnerDubboActuator = dubboActuator;
        }

        public class DubboActuatorSuiteConf
        {
            public int MaxConnections { get; set; } = 20;
            public int AssistConnectionAliveTime { get; set; } = 60 * 5;
            public string DefaultServiceName { get; set; } = null;

        }

        private List<DubboSuiteCell> _actuatorSuiteCellList;

        
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
        private static Timer DubboSuiteTimer;
        protected delegate void DubboSuiteCruiseEventHandler(object sender, ElapsedEventArgs e);
        protected static event DubboSuiteCruiseEventHandler DubboSuiteCruiseEvent;

        /// <summary>
        /// 静态构造函数
        /// </summary>
        static DubboActuatorSuite()
        {
            DubboSuiteTimer = new Timer(InnetTimerInterval);
            DubboSuiteTimer.Elapsed += OnDubboSuiteTimedEvent;
            DubboSuiteTimer.AutoReset = true;
            //不用直接启动Timer，在每一个DubboActuatorSuite实例化时判断是否需要启动，释放过程中判断DubboSuiteTimer是否需要被复用，如果所有引用都被释放则自动停止，以尽可能减少定时器的存在。
            DubboSuiteTimer.Enabled = false;
        }

        private static void OnDubboSuiteTimedEvent(object sender, ElapsedEventArgs e)
        {
            if (DubboSuiteCruiseEvent == null || DubboSuiteCruiseEvent.GetInvocationList().Length == 0)
            {
                DubboSuiteTimer.Stop();
            }
            else
            {
                DubboSuiteCruiseEvent.Invoke(sender, e);
            }
        } 
        #endregion

        public DubboActuatorSuite(string Address, int Port, int CommandTimeout = 10 * 1000, string defaultServiceName = null) : base(Address, Port, CommandTimeout, defaultServiceName)
        {
            DefaultServiceName = defaultServiceName;
            _actuatorSuiteCellList = new List<DubboSuiteCell> {new DubboSuiteCell(this)};
            DubboSuiteCruiseEvent += CruiseTaskEvent;
            if(!DubboSuiteTimer.Enabled) DubboSuiteTimer.Start();
        }

        private void CruiseTaskEvent(object sender, ElapsedEventArgs e)
        {
            if(IsRead)
            {
                StatusInfo = this.GetDubboStatusInfoAsync().GetAwaiter().GetResult();
            }
        }


        /// <summary>
        /// 初始化DubboTesterSuite，获取Func列表及详情
        /// </summary>
        /// <param name="serviceName">serviceName将被设置为DefaultServiceName，并且只会获取DefaultService里的Func（默认为空将使用DefaultServiceName，如果DefaultServiceName为空，将获取所有sevice里的Func列表,如果使用*将将DefaultServiceName设置为null）</param>
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
            DubboSuiteCruiseEvent -= CruiseTaskEvent;
            if (DubboSuiteCruiseEvent == null || DubboSuiteCruiseEvent.GetInvocationList().Length == 0)
            {
                DubboSuiteTimer.Stop();
            }
            base.Dispose();
        }

    }
}
