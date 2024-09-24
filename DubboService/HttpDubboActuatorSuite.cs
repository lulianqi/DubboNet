using DubboNet.DubboService.DataModle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DubboNet.DubboService.TelnetDubboActuatorSuite;

namespace DubboNet.DubboService
{
    public class HttpDubboActuatorSuite : IDubboActuatorSuite
    {
        private bool disposedValue;

        private HttpClient _innerHttpClient = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 10000
        });

        public DubboActuatorProtocolType ProtocolType => DubboActuatorProtocolType.Http;

        public DubboActuatorSuiteStatus ActuatorSuiteStatusInfo { get; private set; } = new DubboActuatorSuiteStatus();

        public DateTime LastActivateTime { get; private set; } = DateTime.Now;

        /// <summary>
        /// 获取默认服务名称
        /// </summary>
        public new string DefaultServiceName { get; private set; }

        /// <summary>
        /// 初始化DubboActuatorSuite
        /// </summary>
        /// <param name="Address">地址（ip）</param>
        /// <param name="Port">端口</param>
        /// <param name="CommandTimeout">客户端请求命令的超时时间（毫秒为单位，默认10秒）</param>
        /// <param name="dubboActuatorSuiteConf">DubboActuatorSuiteConf配置</param>
        public HttpDubboActuatorSuite(string Address, int Port, DubboActuatorSuiteConf dubboActuatorSuiteConf = null) 
        {
            _innerHttpClient.BaseAddress = new Uri($"http://{Address}:{Port}");
            if (dubboActuatorSuiteConf != null)
            {
                DefaultServiceName = dubboActuatorSuiteConf.DefaultServiceName;
                if (dubboActuatorSuiteConf.DubboRequestTimeout > 0)
                {
                    _innerHttpClient.Timeout =  TimeSpan.FromMilliseconds(dubboActuatorSuiteConf.DubboRequestTimeout);
                }
            }
        }

        public Task<DubboRequestResult> SendQuery(string endPoint)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult> SendQuery(string endPoint, string req)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint, string req)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp>(string endPoint)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req>(string endPoint, T_Req req)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2>(string endPoint, T_Req1 req1, T_Req2 req2)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8)
        {
            throw new NotImplementedException();
        }

        public Task<DubboRequestResult<T_Rsp>> SendQuery<T_Rsp, T_Req1, T_Req2, T_Req3, T_Req4, T_Req5, T_Req6, T_Req7, T_Req8, T_Req9>(string endPoint, T_Req1 req1, T_Req2 req2, T_Req3 req3, T_Req4 req4, T_Req5 req5, T_Req6 req6, T_Req7 req7, T_Req8 req8, T_Req9 req9)
        {
            throw new NotImplementedException();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)
                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                disposedValue = true;
            }
        }

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~HttpDubboActuatorSuite()
        // {
        //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
