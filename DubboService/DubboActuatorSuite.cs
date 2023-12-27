using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DubboNet.DubboService
{
    public class DubboActuatorSuite : DubboActuator
    {

        /// <summary>
        /// 默认服务名称
        /// </summary>
        public string DefaultServiceName { get; private set; }

        public Dictionary<string, Dictionary<string, DubboFuncInfo>> DubboServiceFuncCollection { get; private set; }

        public Dictionary<string, DubboFuncInfo> DefaulDubboServiceFuncs { get; private set; }

        public DubboActuatorSuite(string Address, int Port, int CommandTimeout = 10 * 1000, string defaultServiceName = null) : base(Address, Port, CommandTimeout, defaultServiceName)
        {
            DefaultServiceName = defaultServiceName;
        }

        private void ShowError(string mes)
        {
            MyLogger.LogDiagnostics(mes, "DubboTesterSevice", true);
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
                List<string> tempSeviceList = await GetAllDubboServiceAsync();
                if (tempSeviceList?.Count > 0)
                {
                    foreach (var nowService in tempSeviceList)
                    {
                        Dictionary<string, DubboFuncInfo> tempDc = await GetDubboServiceFuncAsync(nowService);
                        if (tempDc == null)
                        {
                            ShowError($"GetDubboServiceFuncAsyncfailed in[InitServiceAsync] that Service is {nowService}");
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

    }
}
