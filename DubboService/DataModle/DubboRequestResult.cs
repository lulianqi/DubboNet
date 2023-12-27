﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MyCommonHelper;

namespace DubboNet.DubboService.DataModle
{
    /*示例原数据
    Use default service com.account.api.account.AccountInfoRemoteSerevice.
    result: { "code":200,"data":57,"message":"successful","success":true}
    elapsed: 7 ms.
    */
    public class DubboRequestResult
    {
        private const string _dubboResultSpit_result = "result: ";
        private const string _dubboResultSpit_elapsed = "\nelapsed: ";
        private const string _dubboResultSpit_ms = " ms.";

        /// <summary>
        /// 请求结果
        /// </summary>
        public string Result { get; set; }
        /// <summary>
        /// 服务处理时间，-1表示解析失败（毫秒）
        /// </summary>
        public int ServiceElapsed { get; set; }
        /// <summary>
        /// 请求时间，包含网络时间（毫秒）
        /// </summary>
        public int RequestElapsed { get; set; }

        /// <summary>
        /// 请求的异常错误信息（请求已经有了返回数据，返回数据非法或反序列化异常时等异常情况，此处记录错误详情）
        /// DubboRequestResult是否正常应以ServiceElapsed是否为-1为准，因为可能存在请求正常返回，但是反序列化失败的场景
        /// </summary>
        public string ErrorMeaasge { get; set; } = null;

        /// <summary>
        /// 通过dubbo telnet原始返回 获取DubboRequestResult
        /// </summary>
        /// <param name="queryResultStr"></param>
        /// <returns></returns>
        public static DubboRequestResult GetRequestResultFormStr(string queryResultStr)
        {
            DubboRequestResult dubboRequestResult = new DubboRequestResult();
            int nowStartFlag = 0;
            int nowEndFlag = 0;
            if (queryResultStr.Contains(_dubboResultSpit_result))
            {
                //get Result
                if (!queryResultStr.StartsWith(_dubboResultSpit_result))
                {
                    nowStartFlag = queryResultStr.IndexOf(_dubboResultSpit_result);
                }
                nowStartFlag = nowStartFlag + _dubboResultSpit_result.Length;
                //get Elapsed
                //需要指定StringComparison.Ordinal，不然\n在\r\n中将不能被找到，详见 https://learn.microsoft.com/zh-cn/dotnet/core/extensions/globalization-icu
                nowEndFlag = queryResultStr.IndexOf(_dubboResultSpit_elapsed, nowStartFlag, StringComparison.Ordinal);
                if (nowEndFlag > 0)
                {
                    dubboRequestResult.Result = queryResultStr.Substring(nowStartFlag, nowEndFlag - nowStartFlag);
                    nowStartFlag = nowEndFlag + _dubboResultSpit_elapsed.Length;
                    nowEndFlag = queryResultStr.IndexOf(_dubboResultSpit_ms, nowStartFlag);
                    int tempElapsed = -1;
                    if (nowEndFlag > 0)
                    {
                        if (!int.TryParse(queryResultStr.Substring(nowStartFlag, nowEndFlag - nowStartFlag), out tempElapsed))
                        {
                            //TryParse 失败 out 值会赋写为0
                            tempElapsed = -1;
                            dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_ms]:{_dubboResultSpit_ms} in queryResultStr";
                        }
                    }
                    dubboRequestResult.ServiceElapsed = tempElapsed;
                }
                else
                {
                    dubboRequestResult.Result = queryResultStr.Substring(nowStartFlag);
                    dubboRequestResult.ServiceElapsed = -1;
                    dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_elapsed]:{_dubboResultSpit_elapsed} in queryResultStr";
                }

            }
            else
            {
                dubboRequestResult.Result = queryResultStr;
                dubboRequestResult.ServiceElapsed = -1;
                dubboRequestResult.ErrorMeaasge = $"can not find [_dubboResultSpit_elapsed]:{_dubboResultSpit_result} in queryResultStr";
            }
            return dubboRequestResult;
        }

    }

    public class DubboRequestResult<T>:DubboRequestResult  //where T :class
    {
        private T _resultModle = default;

        private bool hasSetResultModle =false;

        /// <summary>
        /// T Modle 类型数据
        /// </summary>
        public T ResultModle
        { 
            get
            {
                if(!hasSetResultModle)
                {
                    if(!string.IsNullOrEmpty(Result))
                    {
                        try
                        {
                            _resultModle = JsonSerializer.Deserialize<T>(Result);
                        }
                        catch(Exception ex)
                        {
                            _resultModle =default;
                            ErrorMeaasge = ex.Message;
                            MyLogger.LogError("Get ResultModle Error",ex);
                        }
                    }
                    hasSetResultModle = true;
                }
                return _resultModle;
            }
        }

        public DubboRequestResult()
        {
        }

        public DubboRequestResult(DubboRequestResult sourceDubboRequestResult)
        {
            this.Result = sourceDubboRequestResult.Result;
            this.ServiceElapsed = sourceDubboRequestResult.ServiceElapsed;
            this.RequestElapsed = sourceDubboRequestResult.RequestElapsed;
            //如果原始数据有问题，即放弃反序列化
            if (ServiceElapsed >= 0)
            {
                _ = ResultModle;
            }
            else
            {
                hasSetResultModle = true;
            }
        }

        public void ResetResultModle()
        {
            _resultModle = default;
            hasSetResultModle = false;
        }
    }

}
