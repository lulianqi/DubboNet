using System;
using System.Collections.Generic;
using System.Linq;

namespace DubboNet.Clients.RegistryClient
{
    /// <summary>
    /// 按连接字符串在进程内复用 Nacos 客户端，并通过引用计数记录使用情况。
    /// <para>EN: Reuses Nacos clients by connection string for the process lifetime and tracks usage with reference counting.</para>
    /// </summary>
    internal sealed class MultiMyNacosStorage : IDisposable
    {
        internal sealed class MyNacosStorageInfo
        {
            public string ConnectionString { get; set; }
            public MyNacos NowMyNacos { get; set; }
            public int ReferenceCount { get; set; }
        }

        private readonly object _syncRoot = new object();
        private readonly int _capacity;
        private readonly Func<string, MyNacos> _factory;
        private Dictionary<string, MyNacosStorageInfo> _clients =
            new Dictionary<string, MyNacosStorageInfo>(StringComparer.Ordinal);

        // SDK 无法关闭 RPC worker：限制进程内不同配置的总数，不能通过驱逐后重建伪装成释放。
        // EN: The SDK cannot stop RPC workers. Bound distinct configurations instead of evicting and leaking them.
        public MultiMyNacosStorage(int capacity = 32, Func<string, MyNacos> factory = null)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _factory = factory ?? (address => new MyNacos(address));
        }

        /// <summary>
        /// 获取或创建同地址共享的 Nacos 客户端，并增加一次引用。
        /// <para>EN: Gets or creates a shared Nacos client for the address and increments its reference count.</para>
        /// </summary>
        public MyNacos GetMyNacos(string connectionString)
        {
            string storageKey = MyNacos.NormalizeConnectionString(connectionString);
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_clients.TryGetValue(storageKey, out MyNacosStorageInfo existing))
                {
                    existing.ReferenceCount++;
                    return existing.NowMyNacos;
                }

                if (_clients.Count >= _capacity)
                    throw new InvalidOperationException(
                        $"Nacos client pool limit ({_capacity} distinct connection configurations) reached. " +
                        "Reuse an existing server/namespace/credential configuration or restart the process. " +
                        "nacos-sdk-csharp 1.3.10 cannot stop its background workers; idle entries are retained to prevent connection leaks.");

                MyNacosStorageInfo created = new MyNacosStorageInfo
                {
                    ConnectionString = storageKey,
                    NowMyNacos = _factory(storageKey),
                    ReferenceCount = 1
                };
                _clients.Add(storageKey, created);
                return created.NowMyNacos;
            }
        }

        /// <summary>
        /// 释放一次客户端引用；空闲实例保留在进程级池中，以便同地址后续复用。
        /// <para>EN: Releases one client reference; an idle instance remains in the process-wide pool for later reuse of the same address.</para>
        /// </summary>
        public void RemoveMyNacos(MyNacos nacos)
        {
            if (nacos == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_clients == null)
                {
                    return;
                }
                KeyValuePair<string, MyNacosStorageInfo> match = _clients.FirstOrDefault(
                    item => ReferenceEquals(item.Value.NowMyNacos, nacos));
                if (match.Value != null)
                {
                    Release(match.Value);
                }
            }
        }

        private void Release(MyNacosStorageInfo info)
        {
            // nacos-sdk-csharp 1.3.x 的 ShutDown API 不会真正关闭内部 RPC worker。
            // 将空闲实例保留在进程级池中，可避免同一地址后续重新创建额外的后台连接。
            // EN: nacos-sdk-csharp 1.3.x does not actually stop its internal RPC workers from
            // ShutDown. Retaining an idle instance in the process-wide pool avoids creating
            // additional background connections when the same address is used again later.
            info.ReferenceCount = Math.Max(0, info.ReferenceCount - 1);
        }

        private void ThrowIfDisposed()
        {
            if (_clients == null)
            {
                throw new ObjectDisposedException(nameof(MultiMyNacosStorage));
            }
        }

        /// <summary>
        /// 取消池中全部 Nacos 客户端的订阅并释放其托管资源。
        /// <para>EN: Cancels subscriptions and releases managed resources for every Nacos client held by the pool.</para>
        /// </summary>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_clients == null)
                {
                    return;
                }
                foreach (MyNacosStorageInfo info in _clients.Values)
                {
                    info.NowMyNacos.Dispose();
                }
                _clients.Clear();
                _clients = null;
            }
        }
    }
}
