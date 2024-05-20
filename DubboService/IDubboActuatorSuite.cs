using DubboNet.DubboService.DataModle;
using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DubboNet.DubboService
{
    public enum DubboActuatorProtocolType
    {
        Telnet,
        Http,
        Grpc
    }
    public interface IDubboActuatorSuite
    {
        /// <summary>
        /// 当前执行器选用的协议类型
        /// </summary>
        public DubboActuatorProtocolType ProtocolType { get; }
        /// <summary>
        ///  发送Query请求[返回DubboRequestResult结果](返回不会为null，dubboRequestResult.ServiceElapsed 为 -1 时即代表错误，通过dubboRequestResult.ErrorMeaasge获取错误详情)
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <returns></returns>
        public Task<DubboRequestResult> SendQuery(string endPoint);
        /// <summary>
        ///  发送Query请求[返回DubboRequestResult结果](返回不会为null，dubboRequestResult.ServiceElapsed 为 -1 时即代表错误，通过dubboRequestResult.ErrorMeaasge获取错误详情)
        /// </summary>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数，如果有多个参数参数间用,隔开（这里是request的原始数据，实际是[par1,par2,par3]的数组形式[]不用包括中req里，里面的par是json对象）（null也是一种参数对象，没有任何参数填空""即可）</param>
        /// <returns></returns>
        public Task<DubboRequestResult> SendQuery(string endPoint, string req);
        /// <summary>
        /// 发送Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果]
        /// </summary>
        /// <typeparam name="T_Rsp"></typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req">请求参数，如果有多个参数参数间用,隔开（实际是[par1,par2,par3]的数组形式[]不用包括中req里）（null也是一种参数对象，没有任何参数填空""即可）</param>
        /// <returns></returns>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req);
        /// <summary>
        /// 发送Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果](请求为无请求参数的版本)
        /// </summary>
        /// <typeparam name="T_Rsp"></typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <returns></returns>
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint);

        /// <summary>
        /// 发送指定类型的结构化数据Query请求，并将返回指定类型的结构化数据[返回DubboRequestResult<T_Rsp>结果]
        /// </summary>
        /// <typeparam name="T_Rsp">响应类型</typeparam>
        /// <typeparam name="T_Req">请求类型</typeparam>
        /// <param name="endPoint">服务人口</param>
        /// <param name="req"></param>
        /// <returns></returns>
        public  Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(string endPoint, T_Req1 req1, T_Req2 req2);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8);
        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9);
    }
}
