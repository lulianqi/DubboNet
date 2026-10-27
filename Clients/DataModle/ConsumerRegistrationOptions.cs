using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DubboNet.Clients.DataModle
{
    /// <summary>
    /// 进程级消费者注册配置；重复配置可热切换，默认关闭。
    /// EN: Process-wide consumer registration options. Reconfigure to switch at runtime; disabled by default.
    /// </summary>
    public sealed class ConsumerRegistrationOptions
    {
        /// <summary>是否注册消费者；关闭不影响 RPC 和服务引用跟踪。EN: Whether to register consumers; disabling preserves RPC and tracked service references.</summary>
        public bool Enabled { get; set; }

        /// <summary>应用名称；首次启用时必填，重新配置时留空继承旧值。EN: Application name, required on first enable; omitted values inherit the previous identity.</summary>
        public string ApplicationName { get; set; }

        /// <summary>上报的本机 IP；留空继承旧值，首次启用时自动选择本机 IPv4。EN: Advertised local IP; omitted values inherit the previous host or select a local IPv4 on first enable.</summary>
        public string Host { get; set; }

        /// <summary>进程实例标识；留空继承旧值，首次配置时自动生成。EN: Process instance identifier; omitted values inherit the previous ID or generate one initially.</summary>
        public string InstanceId { get; set; }

        /// <summary>失败重试基础间隔及 ZooKeeper 会话恢复检查间隔；重试指数退避，不是心跳间隔。EN: Base retry and ZooKeeper session check interval; retries use exponential backoff, not an additional heartbeat.</summary>
        public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>同步释放及进程退出时，最多等待消费者注销的时间。EN: Maximum wait for deregistration during synchronous disposal or process exit.</summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(3);

        internal ConsumerRegistrationOptions Snapshot(ConsumerRegistrationOptions previous = null)
        {
            if (RetryInterval < TimeSpan.FromSeconds(1) || RetryInterval > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(RetryInterval));
            if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
            string application = string.IsNullOrWhiteSpace(ApplicationName) ? previous?.ApplicationName : ApplicationName.Trim();
            if (Enabled && string.IsNullOrWhiteSpace(application))
                throw new ArgumentException("ApplicationName is required for consumer registration.", nameof(ApplicationName));
            string host = string.IsNullOrWhiteSpace(Host) ? previous?.Host : Host.Trim();
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
                ApplicationName = application,
                Host = host,
                InstanceId = string.IsNullOrWhiteSpace(InstanceId) ? previous?.InstanceId ?? Guid.NewGuid().ToString("N") : InstanceId.Trim(),
                RetryInterval = RetryInterval,
                ShutdownTimeout = ShutdownTimeout
            };
        }
    }
}
