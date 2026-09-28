using MyCommonHelper;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DubboNet.Clients.RegistryClient
{
    /// <summary>
    /// 管理可自动重连、支持认证并可异步释放的 ZooKeeper 会话。
    /// <para>EN: Manages an authenticated ZooKeeper session with automatic reconnection and asynchronous disposal.</para>
    /// </summary>
    public class MyZookeeper : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// 可克隆的 ZooKeeper 节点状态。
        /// <para>EN: A cloneable ZooKeeper node status.</para>
        /// </summary>
        public class MyStat : Stat, ICloneable
        {
            /// <summary>
            /// 从 ZooKeeper 状态创建副本。
            /// <para>EN: Creates a copy from a ZooKeeper status value.</para>
            /// </summary>
            /// <param name="stat">源状态。<para>EN: Source status.</para></param>
            public MyStat(Stat stat) : base(stat.getCzxid(), stat.getMzxid(), stat.getCtime(), stat.getMtime(), stat.getVersion(), stat.getCversion(), stat.getAversion(), stat.getEphemeralOwner(), stat.getDataLength(), stat.getNumChildren(), stat.getPzxid())
            {

            }

            /// <summary>
            /// 创建保留 <see cref="MyStat"/> 实际类型的副本。
            /// <para>EN: Creates a copy that preserves the concrete <see cref="MyStat"/> type.</para>
            /// </summary>
            /// <returns>状态副本。<para>EN: A status copy.</para></returns>
            public object Clone()
            {
                // 保留 MyStat 的实际类型，避免克隆后再次失去 ICloneable 能力。
                return new MyStat(this);
            }
        }

        /// <summary>
        /// ZooKeeper 认证方案及认证数据。
        /// <para>EN: ZooKeeper authentication scheme and credential bytes.</para>
        /// </summary>
        public class MyAuthInfo
        {
            /// <summary>
            /// 获取或设置认证方案，例如 <c>digest</c>。
            /// <para>EN: Gets or sets the authentication scheme, such as <c>digest</c>.</para>
            /// </summary>
            public string Scheme{get; set;}

            /// <summary>
            /// 获取或设置认证数据。
            /// <para>EN: Gets or sets the authentication credential bytes.</para>
            /// </summary>
            public byte[] Auth{get; set;}
        }

        /// <summary>
        /// 获取zookeeper路径树的最大进入深度
        /// <para>EN: Maximum traversal depth for a ZooKeeper node tree.</para>
        /// </summary>
        private const int _maxDepth = 100;
        /// <summary>
        /// 并行读取节点树时允许同时在途的最大请求数，避免大树瞬间压垮 ZooKeeper。
        /// <para>EN: Maximum concurrent requests while reading a node tree.</para>
        /// </summary>
        private const int _maxParallelTreeRequests = 16;
        /// <summary>
        /// 请求异常时，该标记设置为true （异常失去连接后，zooKeeper.getState() 感知不到，导致连接状态判断不即时）
        /// <para>EN: Tracks connection loss that may not yet be reflected by the low-level client state.</para>
        /// </summary>
        private volatile bool innerLossConnectionFlag = false;

        // 连接建立必须串行化。旧实现使用 bool + Monitor 存在任务尚未赋值、异常后状态无法复原等竞态。
        private readonly SemaphoreSlim _connectSemaphore = new SemaphoreSlim(1, 1);
        private ZooKeeper _zooKeeper;
        private int _disposeState;
        private int _forceNewConnection;

        /// <summary>
        /// 当前底层客户端。仅保留只读访问用于兼容已有调用方，连接生命周期由 MyZookeeper 统一管理。
        /// <para>EN: Gets the current low-level client for compatibility; this instance owns its connection lifecycle.</para>
        /// </summary>
        public ZooKeeper zooKeeper => Volatile.Read(ref _zooKeeper);
        private MyWatcher defaultWatch;


        /// <summary>
        /// ZooKeeper 连接字符串，采用 <c>host:port</c> 格式，多个地址之间使用逗号分隔。
        /// <para>EN: Gets the ZooKeeper connection string; separate multiple <c>host:port</c> entries with commas.</para>
        /// </summary>
        public string ConnectionString { get; internal set; }

        /// <summary>
        /// 获取会话超时时间（毫秒）。
        /// <para>EN: Gets the session timeout in milliseconds.</para>
        /// </summary>
        public int SessionTimeOut { get; internal set; } = 10000;

        /// <summary>
        /// 获取当前会话是否已经连接 ZooKeeper（只读连接也视为已连接）。
        /// <para>EN: Gets whether the session is connected to ZooKeeper; read-only connections also count as connected.</para>
        /// </summary>
        public bool IsConnected
        {
            get
            {
                ZooKeeper client = Volatile.Read(ref _zooKeeper);
                return Volatile.Read(ref _disposeState) == 0
                    && client != null
                    && !innerLossConnectionFlag
                    && IsClientConnected(client);
            }
        }

        /// <summary>
        /// 获取当前 ZooKeeper 会话是否可写。
        /// <para>EN: Gets whether the current ZooKeeper session is writable.</para>
        /// </summary>
        public bool CanWrite
        {
            get
            {
                ZooKeeper client = Volatile.Read(ref _zooKeeper);
                return Volatile.Read(ref _disposeState) == 0
                    && client != null
                    && !innerLossConnectionFlag
                    && client.getState() == ZooKeeper.States.CONNECTED;
            }
        }

        /// <summary>
        /// 获取或设置节点数据的默认文本编码。
        /// <para>EN: Gets or sets the default text encoding for node data.</para>
        /// </summary>
        public Encoding NowEncoding { get; set; } = Encoding.UTF8;

        /// <summary>
        /// 获取或设置连接时使用的认证信息。
        /// <para>EN: Gets or sets the authentication information used when connecting.</para>
        /// </summary>
        public MyAuthInfo AuthInfo { get; set; } = null;

        /// <summary>
        /// 创建 ZooKeeper 客户端包装器；实际网络连接在首次请求或显式连接时建立。
        /// <para>EN: Creates a ZooKeeper client wrapper; the network session is established lazily or by an explicit connect call.</para>
        /// </summary>
        /// <param name="connectionString">ZooKeeper 地址列表。<para>EN: ZooKeeper endpoint list.</para></param>
        /// <param name="sessionTimeOut">会话超时时间（毫秒）。<para>EN: Session timeout in milliseconds.</para></param>
        /// <param name="encoding">节点数据文本编码；默认 UTF-8。<para>EN: Text encoding for node data; UTF-8 by default.</para></param>
        /// <param name="authInfo">可选认证信息。<para>EN: Optional authentication information.</para></param>
        public MyZookeeper(string connectionString, int sessionTimeOut = 10000, Encoding encoding = null ,MyAuthInfo authInfo = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("connectionString can not be empty", nameof(connectionString));
            }
            if (sessionTimeOut <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionTimeOut), "sessionTimeOut must be greater than zero");
            }
            if (encoding != null)
            {
                NowEncoding = encoding;
            }
            ConnectionString = connectionString.Trim();
            SessionTimeOut = sessionTimeOut;
            AuthInfo = authInfo;
            // 默认 watcher 同步维护连接标志，不能只打印事件，否则自动恢复后状态可能永久停留在断线。
            defaultWatch = new MyWatcher("DefaultWatch", ProcessDefaultWatcherEvent);
        }

        private void ReportMessage(string mes)
        {
            // 统一走库日志入口，避免组件代码绕过日志级别直接写控制台。
            MyLogger.LogWarning($"[MyZookeeper] {mes}");
        }

        internal static void ShowError(Exception ex)
        {
            System.Reflection.MethodBase methodInfo = new StackFrame(1).GetMethod();
            ShowError($"[{methodInfo.Name}] {ex.ToString()}");
        }

        internal static void ShowError(string log)
        {
            MyLogger.LogError($"[MyZookeeper] {log}");
        }

        internal static void ShowLog(string log)
        {
            MyLogger.LogDebug($"[MyZookeeper] {log}");
        }

        /// <summary>
        /// 检查当前连接状态，如果没有连接立即尝试连接
        /// <para>EN: Checks the current state and attempts an immediate connection when disconnected.</para>
        /// </summary>
        /// <returns>连接状态</returns>
        private async Task<bool> CheckConnectState()
        {
            ThrowIfDisposed();
            if (!IsConnected)
            {
                if (!await ConnectZooKeeperAsync().ConfigureAwait(false))
                {
                    System.Reflection.MethodBase methodInfo = new StackFrame(1).GetMethod();
                    ReportMessage($"[{methodInfo.Name}][CheckConnectState] 连接失败");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 连接服务。并行调用会按顺序复用已经建立成功的连接，不会重复创建底层客户端。
        /// <para>EN: Connects to ZooKeeper. Concurrent calls serialize and reuse an established session.</para>
        /// </summary>
        /// <returns>是否连接成功。<para>EN: <see langword="true"/> when connected successfully.</para></returns>
        public Task<bool> ConnectZooKeeperAsync()
        {
            return ConnectZooKeeperAsync(CancellationToken.None);
        }

        /// <summary>
        /// 连接服务，并允许调用方取消等待连接锁或连接建立过程。
        /// <para>EN: Connects to ZooKeeper and allows cancellation while waiting for the connection gate or handshake.</para>
        /// </summary>
        /// <param name="cancellationToken">取消令牌。<para>EN: Cancellation token.</para></param>
        /// <returns>是否连接成功。<para>EN: <see langword="true"/> when connected successfully.</para></returns>
        public async Task<bool> ConnectZooKeeperAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            // 使用异步信号量替代手工 Monitor 状态机，确保异常和取消时也一定释放连接锁。
            await _connectSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _connectSemaphore.Release();
            }
        }

        /// <summary>
        /// 连接服务（为避免应用层并行连接，请不要直接调用该方法，尝试使用ConnectZooKeeperAsync进行连接）
        /// <para>EN: Performs the serialized connection workflow used by <see cref="ConnectZooKeeperAsync()"/>.</para>
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            ShowLog("ConnectAsync");
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            ZooKeeper currentClient = Volatile.Read(ref _zooKeeper);
            bool forceNewConnection = Volatile.Read(ref _forceNewConnection) != 0;
            if (currentClient != null && !forceNewConnection && IsClientConnected(currentClient))
            {
                // ZooKeeper 可能已经自行恢复连接；此时必须同步清除本地断线标志。
                innerLossConnectionFlag = false;
                return true;
            }

            currentClient = Interlocked.Exchange(ref _zooKeeper, null);
            if (currentClient != null)
            {
                try
                {
                    await currentClient.closeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 旧连接关闭失败不应阻止创建新会话，但需要保留诊断信息。
                    ShowError(exception);
                }
            }

            ThrowIfDisposed();
            if (defaultWatch == null)
            {
                defaultWatch = new MyWatcher("DefaultWatch", ProcessDefaultWatcherEvent);
            }

            ZooKeeper newClient = new ZooKeeper(ConnectionString, SessionTimeOut, defaultWatch);
            if(AuthInfo!=null && AuthInfo.Scheme!=null && AuthInfo.Auth!=null)
            {
                newClient.addAuthInfo(AuthInfo.Scheme, AuthInfo.Auth);
            }

            Volatile.Write(ref _zooKeeper, newClient);
            try
            {
                // 连接等待时间跟随会话超时，避免旧实现固定轮询约 10 秒造成配置失真。
                Stopwatch connectWatch = Stopwatch.StartNew();
                int connectTimeout = Math.Max(1000, SessionTimeOut);
                while (newClient.getState() == ZooKeeper.States.CONNECTING
                    && connectWatch.ElapsedMilliseconds < connectTimeout)
                {
                    ThrowIfDisposed();
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }

                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                ZooKeeper.States state = newClient.getState();
                if (state != ZooKeeper.States.CONNECTED
                    && state != ZooKeeper.States.CONNECTEDREADONLY)
                {
                    ReportMessage("连接失败：" + state);
                    // 多次连接失败后主动释放，避免 ZooKeeper 客户端继续在后台无限重连。
                    await CloseAndDetachClientAsync(newClient).ConfigureAwait(false);
                    return false;
                }

                Interlocked.Exchange(ref _forceNewConnection, 0);
                innerLossConnectionFlag = false;
                return true;
            }
            catch
            {
                // 连接期间被释放或发生异常时，确保候选客户端不会残留在后台。
                await CloseAndDetachClientAsync(newClient).ConfigureAwait(false);
                throw;
            }
        }

        private static bool IsClientConnected(ZooKeeper client)
        {
            if (client == null)
            {
                return false;
            }
            ZooKeeper.States state = client.getState();
            return state == ZooKeeper.States.CONNECTED
                || state == ZooKeeper.States.CONNECTEDREADONLY;
        }

        private async Task CloseAndDetachClientAsync(ZooKeeper client)
        {
            if (client == null)
            {
                return;
            }

            Interlocked.CompareExchange(ref _zooKeeper, null, client);
            try
            {
                await client.closeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        }

        private void ProcessDefaultWatcherEvent(WatchedEvent @event)
        {
            if (@event == null || @event.get_Type() != Watcher.Event.EventType.None)
            {
                return;
            }

            if (@event.getState() == Watcher.Event.KeeperState.Disconnected
                || @event.getState() == Watcher.Event.KeeperState.AuthFailed)
            {
                innerLossConnectionFlag = true;
            }
            else if (@event.getState() == Watcher.Event.KeeperState.Expired)
            {
                // Session Expired 后原会话及其 watcher 均已失效，下一次连接必须重建客户端。
                innerLossConnectionFlag = true;
                Interlocked.Exchange(ref _forceNewConnection, 1);
            }
            else if ((@event.getState() == Watcher.Event.KeeperState.SyncConnected
                    || @event.getState() == Watcher.Event.KeeperState.ConnectedReadOnly)
                && Volatile.Read(ref _forceNewConnection) == 0)
            {
                // 普通网络抖动由 ZooKeeper 自动恢复后，及时恢复本地连接状态。
                innerLossConnectionFlag = false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                throw new ObjectDisposedException(nameof(MyZookeeper));
            }
        }

        /// <summary>
        /// 并行读取指定路径下的完整节点树。
        /// <para>EN: Reads the complete node tree below a path in parallel.</para>
        /// </summary>
        /// <param name="rootPath">根路径。<para>EN: Root path.</para></param>
        /// <returns>节点树；路径不存在或无法连接时返回 <see langword="null"/>。<para>EN: The node tree, or <see langword="null"/> when the path is absent or the connection cannot be established.</para></returns>
        public async Task<ZNode> GetZNodeTreeEx(string rootPath = "/")
        {
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException("rootPath is null");
            }
            if (!await CheckConnectState().ConfigureAwait(false))
            {
                return null;
            }
            Stat tempStat = await ExistsAsync(rootPath).ConfigureAwait(false);
            if (tempStat == null)
            {
                ReportMessage("[GetDubboFuncTree] 路径错误");
                return null;
            }
            ZNode rootNode = new ZNode(null, rootPath, null, ZNode.ZNodeType.Node) { Tag = tempStat };
            await FillZNodeTreeParallelAsync(rootNode).ConfigureAwait(false);
            stopWatch.Stop();
            ShowLog($"😊😊😊😊😊😊😊😊😊GetZNodeTreeEx:{stopWatch.ElapsedMilliseconds} Version:{rootNode.Version}😊😊😊😊😊😊😊😊😊😊");
            return rootNode;
        }

        /// <summary>
        /// 使用限流的逐层并发方式填充节点树。
        /// <para>EN: Populates a node tree breadth-first with bounded concurrency.</para>
        /// </summary>
        private async Task FillZNodeTreeParallelAsync(ZNode rootNode)
        {
            if (rootNode == null)
            {
                throw new ArgumentNullException(nameof(rootNode));
            }

            // 每一层只收集自己的结果，不再让多个递归任务并发修改同一个 List<Task>。
            IReadOnlyList<ZNode> currentLevel = new[] { rootNode };
            using SemaphoreSlim requestLimiter = new SemaphoreSlim(
                _maxParallelTreeRequests,
                _maxParallelTreeRequests);

            for (int depth = 0; currentLevel.Count > 0; depth++)
            {
                if (depth >= _maxDepth)
                {
                    throw new InvalidOperationException($"ZNode tree exceeded maximum depth {_maxDepth}.");
                }

                Task<IReadOnlyList<ZNode>>[] levelTasks = currentLevel
                    .Where(node => node != null)
                    .Select(node => GetZNodeChildrenLimitedAsync(node, requestLimiter))
                    .ToArray();
                IReadOnlyList<ZNode>[] childGroups = await Task.WhenAll(levelTasks)
                    .ConfigureAwait(false);
                currentLevel = childGroups
                    .Where(children => children != null)
                    .SelectMany(children => children)
                    .Where(node => node != null)
                    .ToArray();
            }
        }

        private async Task<IReadOnlyList<ZNode>> GetZNodeChildrenLimitedAsync(
            ZNode node,
            SemaphoreSlim requestLimiter)
        {
            await requestLimiter.WaitAsync().ConfigureAwait(false);
            try
            {
                return await GetZNodeChildren(node).ConfigureAwait(false);
            }
            finally
            {
                requestLimiter.Release();
            }
        }

        /// <summary>
        /// 重新读取并更新一个局部节点树；节点应来自 <see cref="GetZNodeTreeEx"/> 或 <see cref="GetZNodeTree"/>。
        /// <para>EN: Reloads a subtree whose node came from <see cref="GetZNodeTreeEx"/> or <see cref="GetZNodeTree"/>.</para>
        /// </summary>
        /// <param name="yourNode">需要更新的节点。<para>EN: Node to refresh.</para></param>
        /// <returns>表示异步操作的任务。<para>EN: A task representing the asynchronous operation.</para></returns>
        public async Task UpdateZNode(ZNode yourNode)
        {
            ThrowIfDisposed();
            await FillZNodeTreeParallelAsync(yourNode).ConfigureAwait(false);
        }

        /// <summary>
        /// 串行读取指定路径下的完整节点树。
        /// <para>EN: Reads the complete node tree below a path sequentially.</para>
        /// </summary>
        /// <param name="rootPath">根路径。<para>EN: Root path.</para></param>
        /// <returns>节点树；路径不存在或无法连接时返回 <see langword="null"/>。<para>EN: The node tree, or <see langword="null"/> when the path is absent or the connection cannot be established.</para></returns>
        public async Task<ZNode> GetZNodeTree(string rootPath = "/")
        {
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException("rootPath is null");
            }
            if (!await CheckConnectState().ConfigureAwait(false))
            {
                return null;
            }
            Stat tempStat = await ExistsAsync(rootPath).ConfigureAwait(false);
            if (tempStat == null)
            {
                ReportMessage("[GetDubboFuncTree] 路径错误");
                return null;
            }

            ZNode rootNode = new ZNode(null, rootPath, null, ZNode.ZNodeType.Node) { Tag = tempStat };
            IReadOnlyList<ZNode> endNodes = await GetZNodeChildren(rootNode).ConfigureAwait(false);
            int maxLoop = _maxDepth;
            while (endNodes != null && endNodes.Count > 0)
            {
                maxLoop--;
                if (maxLoop < 0)
                {
                    throw new InvalidOperationException($"ZNode tree exceeded maximum depth {_maxDepth}.");
                }
                endNodes = await GetZNodeChildren(endNodes).ConfigureAwait(false);
            }

            //foreach (ZNode node in rootNode)
            //{
            //    ShowLog($"{node.FullPath}:{(await ExistsAsync(node.FullPath)).getNumChildren()}" );
            //}

            stopWatch.Stop();
            ShowLog($"😊😊😊😊😊😊😊😊😊GetZNodeTree:{stopWatch.ElapsedMilliseconds} Version:{rootNode.Version}😊😊😊😊😊😊😊😊😊😊");
            return rootNode;
        }

        /// <summary>
        /// 获取/填充指定Node数组的所有子节点（注意会直将子节点加到yourNode下，如果已经被填充了则会更新，但是不更新孙节点）（如果获取错误会将当前节点类型设置为ZNodeType.Error）
        /// <para>EN: Reloads the direct children of one node and marks the node as an error when loading fails.</para>
        /// </summary>
        /// <param name="yourNode">当前节点</param>
        /// <returns></returns>
        private async Task<IReadOnlyList<ZNode>> GetZNodeChildren(ZNode yourNode)
        {
            if (yourNode == null)
            {
                throw new ArgumentException("yourNode is null");
            }
            ChildrenResult childrenResult = await GetChildrenAsync(yourNode.FullPath).ConfigureAwait(false);
            //ShowLog($"---------{yourNode.Path}----------\r\n{childrenResult?.Children.MyToString("\n")}");
            yourNode.ClearChildren();
            if (childrenResult == null)
            {
                yourNode.Type = ZNode.ZNodeType.Error;
            }
            else
            {
                // 节点从错误状态恢复后要同步恢复类型，否则成功刷新后仍会被标记为 Error。
                yourNode.Type = ZNode.ZNodeType.Node;
                foreach (var tempChild in childrenResult.Children ?? new List<string>())
                {
                    yourNode.AddChildren(new ZNode(null, tempChild, null, ZNode.ZNodeType.Node));
                }
            }
            return yourNode.Children;
        }

        /// <summary>
        /// 获取/填充指定Node数组的所有子节点（注意会直将子节点加到yourNode下，如果已经被填充了则会更新，但是不更新孙节点）（如果获取错误会将当前节点类型设置为ZNodeType.Error）
        /// <para>EN: Reloads direct children for a set of nodes and returns the next traversal level.</para>
        /// </summary>
        /// <param name="yourNodes">节点列表</param>
        /// <returns></returns>
        private async Task<IReadOnlyList<ZNode>> GetZNodeChildren(IReadOnlyList<ZNode> yourNodes)
        {
            if (yourNodes == null)
            {
                throw new ArgumentException("yourNodes is null");
            }
            //如果连接不了，避免后面遍历连接
            if (!await CheckConnectState().ConfigureAwait(false))
            {
                ShowError($"GetZNodeChildren 重连接失败");
                return null;
            }
            List<ZNode> resultChildrenList = new List<ZNode>();

            foreach (ZNode node in yourNodes)
            {
                IReadOnlyList<ZNode> zs = await GetZNodeChildren(node).ConfigureAwait(false);
                if (zs != null)
                {
                    resultChildrenList.AddRange(zs);
                }
            }
            return resultChildrenList;
        }


        /// <summary>
        /// 获取子节点并自动重连；无子节点时返回空列表，路径不存在时返回 <see langword="null"/>。
        /// <para>EN: Gets child nodes with automatic reconnection; returns an empty list for a leaf and <see langword="null"/> for a missing path.</para>
        /// </summary>
        /// <param name="path">节点完整路径。<para>EN: Full node path.</para></param>
        /// <param name="watcher">可选的一次性 watcher。<para>EN: Optional one-shot watcher.</para></param>
        /// <param name="retryTime">首次失败后的额外重试次数。<para>EN: Additional retry count after the first failure.</para></param>
        /// <returns>子节点结果。<para>EN: Child-node result.</para></returns>
        public async Task<ChildrenResult> GetChildrenAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            //System.Diagnostics.Debug.WriteLine($"---------{path}----------\r\n{Thread.CurrentThread.ManagedThreadId}");
            if (watcher == null)
            {
                return await InnerDoZkRequest(
                    path,
                    (client, agr) => client.getChildrenAsync(agr),
                    retryTime).ConfigureAwait(false);
            }
            else
            {
                return await InnerDoZkRequest(
                    path,
                    (client, agr) => client.getChildrenAsync(agr, watcher),
                    retryTime).ConfigureAwait(false);
            }
        }


        /// <summary>
        /// 获取节点数据并自动重连；路径不存在时返回 <see langword="null"/>。
        /// <para>EN: Gets node data with automatic reconnection; returns <see langword="null"/> for a missing path.</para>
        /// </summary>
        /// <param name="path">节点完整路径。<para>EN: Full node path.</para></param>
        /// <param name="watcher">可选的一次性 watcher。<para>EN: Optional one-shot watcher.</para></param>
        /// <param name="retryTime">首次失败后的额外重试次数。<para>EN: Additional retry count after the first failure.</para></param>
        /// <returns>节点数据结果。<para>EN: Node-data result.</para></returns>
        public async Task<DataResult> GetDataAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            return await InnerDoZkRequest(
                path,
                (client, agr) => client.getDataAsync(agr, watcher),
                retryTime).ConfigureAwait(false);
        }

        /// <summary>
        /// 检查节点是否存在并自动重连。
        /// <para>EN: Checks whether a node exists, with automatic reconnection.</para>
        /// </summary>
        /// <param name="path">节点完整路径。<para>EN: Full node path.</para></param>
        /// <param name="watcher">可选的一次性 watcher。<para>EN: Optional one-shot watcher.</para></param>
        /// <param name="retryTime">首次失败后的额外重试次数。<para>EN: Additional retry count after the first failure.</para></param>
        /// <returns>节点状态；路径不存在时返回 <see langword="null"/>。<para>EN: Node status, or <see langword="null"/> when the path does not exist.</para></returns>
        public async Task<Stat> ExistsAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            return await InnerDoZkRequest(
                path,
                (client, agr) => client.existsAsync(agr, watcher),
                retryTime).ConfigureAwait(false);
        }


        /// <summary>
        /// 内部ZooKeeper执行方法 （内置可复用的连接及重连逻辑，用于让ZooKeeper执行实际网络请求）
        /// <para>EN: Executes a ZooKeeper request through the shared connection with bounded reconnection retries.</para>
        /// </summary>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <param name="path">路径（将传递到Func的入参）</param>
        /// <param name="func">具体执行方法</param>
        /// <param name="retryTime">首次请求失败后的额外重试次数</param>
        /// <returns>返回结果</returns>
        private async Task<T> InnerDoZkRequest<T>(
            string path,
            Func<ZooKeeper, string, Task<T>> func,
            int retryTime = 2)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("ZooKeeper path can not be empty", nameof(path));
            }
            if (retryTime < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retryTime));
            }

            Exception lastConnectionException = null;
            // 使用有界循环代替递归，避免 retryTime-- 把旧值传入导致无限重试。
            for (int attempt = 0; attempt <= retryTime; attempt++)
            {
                ThrowIfDisposed();
                try
                {
                    if (!await CheckConnectState().ConfigureAwait(false))
                    {
                        lastConnectionException = new InvalidOperationException(
                            $"Can not connect to ZooKeeper '{ConnectionString}'.");
                    }
                    else
                    {
                        // 使用局部快照执行请求，避免连接字段在判空后被其他线程替换造成空引用。
                        ZooKeeper client = Volatile.Read(ref _zooKeeper);
                        if (client == null)
                        {
                            lastConnectionException = new InvalidOperationException(
                                $"ZooKeeper client is unavailable for path '{path}'.");
                        }
                        else
                        {
                            return await func(client, path).ConfigureAwait(false);
                        }
                    }
                }
                catch (KeeperException.NoNodeException)
                {
                    // NoNode 是正常的查询结果，只有该异常可以转换为 default/null。
                    ShowLog($"节点不存在 path:{path}");
                    return default;
                }
                catch (KeeperException.SessionExpiredException exception)
                {
                    innerLossConnectionFlag = true;
                    Interlocked.Exchange(ref _forceNewConnection, 1);
                    lastConnectionException = exception;
                    ShowLog($"ZooKeeper 会话过期，准备重建连接 path:{path}");
                }
                catch (KeeperException.ConnectionLossException exception)
                {
                    innerLossConnectionFlag = true;
                    lastConnectionException = exception;
                    ShowLog($"ZooKeeper 连接中断，准备重新连接 path:{path}");
                }

                if (attempt < retryTime)
                {
                    // 简单退避可避免多个调用方在断线时同时高速冲击注册中心。
                    int delayMilliseconds = Math.Min(1000, 50 * (1 << Math.Min(attempt, 4)));
                    await Task.Delay(delayMilliseconds).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(
                $"ZooKeeper request failed after {retryTime + 1} attempt(s), path: '{path}'.",
                lastConnectionException);
        }

        /// <summary>
        /// 同步读取指定节点的文本数据；主要用于兼容旧调用方和诊断。
        /// <para>EN: Synchronously reads node text for legacy compatibility and diagnostics.</para>
        /// </summary>
        /// <param name="mes">节点路径。<para>EN: Node path.</para></param>
        /// <returns>节点文本或空数据提示。<para>EN: Node text or an empty-data message.</para></returns>
        public string TestFunc(string mes)
        {
            // 保留同步入口兼容旧调用方；实际逻辑统一走异步请求封装和重试策略。
            return TestFuncAsync(mes).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步读取指定节点的文本数据；主要用于诊断。
        /// <para>EN: Asynchronously reads node text, primarily for diagnostics.</para>
        /// </summary>
        /// <param name="path">节点路径。<para>EN: Node path.</para></param>
        /// <returns>节点文本或空数据提示。<para>EN: Node text or an empty-data message.</para></returns>
        public async Task<string> TestFuncAsync(string path)
        {
            DataResult dataResult = await GetDataAsync(path).ConfigureAwait(false);
            if (dataResult?.Data == null)
            {
                return "dataResult.Data is null";
            }
            // 使用构造函数配置的编码，修复原先固定 UTF-8 导致 NowEncoding 不生效的问题。
            string dt = NowEncoding.GetString(dataResult.Data);
            return dt;
        }

        /// <summary>
        /// 同步关闭 ZooKeeper 会话并释放资源。
        /// <para>EN: Synchronously closes the ZooKeeper session and releases resources.</para>
        /// </summary>
        public void Dispose()
        {
            // IDisposable 无法异步等待，只在兼容入口同步等待；异步调用方应优先使用 DisposeAsync。
            DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 异步关闭 ZooKeeper 会话并释放资源。
        /// <para>EN: Asynchronously closes the ZooKeeper session and releases resources.</para>
        /// </summary>
        /// <returns>表示异步释放操作的值任务。<para>EN: A value task representing asynchronous disposal.</para></returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            // 与连接建立共用同一信号量，保证释放期间不会发布新的 ZooKeeper 客户端。
            await _connectSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                defaultWatch = null;
                ZooKeeper client = Interlocked.Exchange(ref _zooKeeper, null);
                if (client != null)
                {
                    await client.closeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _connectSemaphore.Release();
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// 记录 ZooKeeper 事件并将事件转发给可选回调。
    /// <para>EN: Logs ZooKeeper events and forwards them to an optional callback.</para>
    /// </summary>
    public class MyWatcher : Watcher
    {
        private readonly Action<WatchedEvent> _eventHandler;

        /// <summary>
        /// 获取 watcher 名称。
        /// <para>EN: Gets the watcher name.</para>
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// 创建仅记录事件的 watcher。
        /// <para>EN: Creates a watcher that logs received events.</para>
        /// </summary>
        /// <param name="name">watcher 名称。<para>EN: Watcher name.</para></param>
        public MyWatcher(string name)
            : this(name, null)
        {
        }

        internal MyWatcher(string name, Action<WatchedEvent> eventHandler)
        {
            Name = name;
            _eventHandler = eventHandler;
        }

        /// <summary>
        /// 处理一个 ZooKeeper 事件。
        /// <para>EN: Processes a ZooKeeper event.</para>
        /// </summary>
        /// <param name="event">收到的事件。<para>EN: Received event.</para></param>
        /// <returns>已完成的任务。<para>EN: A completed task.</para></returns>
        public override Task process(WatchedEvent @event)
        {
            MyLogger.LogInfo(
                $"{Name} receive: Path-{@event?.getPath()} " +
                $"State-{@event?.getState()} Type-{@event?.get_Type()}");
            try
            {
                // watcher 回调不能把异常抛回 ZooKeeper 事件线程，否则后续事件可能无法继续分发。
                _eventHandler?.Invoke(@event);
            }
            catch (Exception exception)
            {
                MyZookeeper.ShowError(exception);
            }
            return Task.CompletedTask;
        }
    }
}
