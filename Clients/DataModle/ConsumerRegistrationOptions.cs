using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 进程级消费者注册配置；必须在创建第一个 DubboClient 前设置。默认关闭。
    /// EN: Process-wide consumer registration options. Configure before creating the first DubboClient. Disabled by default.
    /// </summary>
    public sealed class ConsumerRegistrationOptions
    {
        /// <summary>是否注册消费者。EN: Whether consumer registration is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>应用名称；启用时必填。EN: Application name, required when enabled.</summary>
        public string ApplicationName { get; set; }

        /// <summary>上报的本机 IP；为空时自动选择本机 IPv4。EN: Advertised local IP; automatically selects a local IPv4 address when omitted.</summary>
        public string Host { get; set; }

        /// <summary>进程实例标识；为空时自动生成。EN: Process instance identifier, generated when omitted.</summary>
        public string InstanceId { get; set; }

        /// <summary>失败重试及 ZooKeeper 会话恢复检查间隔；不是心跳间隔。EN: Retry and ZooKeeper session recovery interval, not a heartbeat interval.</summary>
        public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>同步释放及进程退出时，最多等待消费者注销的时间。EN: Maximum wait for deregistration during synchronous disposal or process exit.</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(3);

        internal ConsumerRegistrationOptions Snapshot()
        {
            if (RetryInterval < TimeSpan.FromSeconds(1) || RetryInterval > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(RetryInterval));
            if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
            if (Enabled && string.IsNullOrWhiteSpace(ApplicationName))
                throw new ArgumentException("ApplicationName is required for consumer registration.", nameof(ApplicationName));
            string host = Host?.Trim();
            if (Enabled && string.IsNullOrEmpty(host))
            {
                try
                {
                    host = Dns.GetHostAddresses(Dns.GetHostName())
                        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        ?.ToString();
                }
                catch (SocketException) { /* Hostname resolution may be unavailable in containers. */ }
                host ??= IPAddress.Loopback.ToString();
            }
            if (!string.IsNullOrEmpty(host) && !IPAddress.TryParse(host, out _))
                throw new ArgumentException("Host must be an IP address.", nameof(Host));
            return new ConsumerRegistrationOptions
            {
                Enabled = Enabled,
                ApplicationName = ApplicationName?.Trim(),
                Host = host,
                InstanceId = string.IsNullOrWhiteSpace(InstanceId) ? Guid.NewGuid().ToString("N") : InstanceId.Trim(),
                RetryInterval = RetryInterval,
                ShutdownTimeout = ShutdownTimeout
            };
        }
    }
}
