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
    public class MyZookeeper : IDisposable
    {
        public class MyStat : Stat, ICloneable
        {
            public MyStat(Stat stat) : base(stat.getCzxid(), stat.getMzxid(), stat.getCtime(), stat.getMtime(), stat.getVersion(), stat.getCversion(), stat.getAversion(), stat.getEphemeralOwner(), stat.getDataLength(), stat.getNumChildren(), stat.getPzxid())
            {

            }
            public object Clone()
            {
                return new Stat(getCzxid(), getMzxid(), getCtime(), getMtime(), getVersion(), getCversion(), getAversion(), getEphemeralOwner(), getDataLength(), getNumChildren(), getPzxid());
            }
        }

        /// <summary>
        /// 获取zookeeper路径树的最大进入深度
        /// </summary>
        private const int _maxDepth = 100;
        /// <summary>
        /// 请求异常时，该标记设置为true （异常失去连接后，zooKeeper.getState() 感知不到，导致连接状态判断不即时）
        /// </summary>
        private volatile bool innerLossConnectionFlag = false;

        public ZooKeeper zooKeeper;
        private MyWatcher defaultWatch;


        /// <summary>
        ///Zookeeper连接字符串，采用host:port格式，多个地址之间使用逗号（,）隔开
        /// </summary>
        public string ConnectionString { get; internal set; }

        /// <summary>
        /// 会话超时时间,单位毫秒
        /// </summary>
        public int SessionTimeOut { get; internal set; } = 10000;

        /// <summary>
        /// 是否已连接Zookeeper
        /// </summary>
        public bool IsConnected { get { return zooKeeper != null && !isInConnectTask && !innerLossConnectionFlag && (zooKeeper.getState() == ZooKeeper.States.CONNECTED || zooKeeper.getState() == ZooKeeper.States.CONNECTEDREADONLY); } }

        /// <summary>
        /// Zookeeper是否有写的权限
        /// </summary>
        public bool CanWrite { get { return zooKeeper != null && zooKeeper.getState() == ZooKeeper.States.CONNECTED; } }

        /// <summary>
        /// 数据编码
        /// </summary>
        public Encoding NowEncoding { get; set; } = Encoding.UTF8;

        public MyZookeeper(string connectionString, int sessionTimeOut = 10000, Encoding encoding = null)
        {
            if (encoding != null)
            {
                NowEncoding = encoding;
            }
            ConnectionString = connectionString;
            SessionTimeOut = sessionTimeOut;
            defaultWatch = new MyWatcher("DefaultWatch");
        }

        private void ReportMessage(string mes)
        {
            Console.WriteLine($"【ReportMessage】「{DateTime.Now.ToString("HH:mm:ss fff")}」:{mes}");
        }

        internal static void ShowError(Exception ex)
        {
            System.Reflection.MethodBase methodInfo = new StackFrame(1).GetMethod();
            ShowError($"[{methodInfo.Name}] {ex.ToString()}");
        }

        internal static void ShowError(string log)
        {
#if DEBUG
            Debug.WriteLine($"💔「{DateTime.Now.ToString("HH:mm:ss fff")}」----------------{log}-----------------");
#else
            Console.WriteLine($"💔「{DateTime.Now.ToString("HH:mm:ss fff")}」----------------{log}-----------------");
#endif
        }

        internal static void ShowLog(string log)
        {
#if DEBUG
            Debug.WriteLine($"「{DateTime.Now.ToString("HH:mm:ss fff")}」----------------{log}-----------------");
#else
            Console.WriteLine($"「{DateTime.Now.ToString("HH:mm:ss fff")}」----------------{log}-----------------");
#endif
        }

        /// <summary>
        /// 检查当前连接状态，如果没有连接立即尝试连接
        /// </summary>
        /// <returns>连接状态</returns>
        private async Task<bool> CheckConnectState()
        {
            if (!IsConnected)
            {
                if (!await ConnectZooKeeperAsync())
                {
                    System.Reflection.MethodBase methodInfo = new StackFrame(1).GetMethod();
                    ReportMessage($"[{methodInfo.Name}][CheckConnectState] 连接失败");
                    return false;
                }
            }
            return true;
        }

        private volatile bool isInConnectTask = false; //volatile保证其不被编译优化，只保证任何时候你读取到的都是最新值，但并不会保证线程安全性.(但是数据本身提供了原子性。32位系统下保证32位以下的数字和指针是原子性的，64位系统则是64位数字，以及对象指针。所以能用 volatile的地方基本上都是保证了原子性的)
        private object isInConnectTaskSyncTag = new object();
        private Task<bool> InnerConnectTask;

        /// <summary>
        /// 连接服务（如果并行重复调用不会创建新的连接，会直接返回当前正在继续的连接任务）
        /// </summary>
        /// <returns>是否连接成功</returns>
        public async Task<bool> ConnectZooKeeperAsync()
        {
            Monitor.Enter(isInConnectTaskSyncTag);
            if (isInConnectTask)
            {
                Monitor.Exit(isInConnectTaskSyncTag);
                ShowLog("ConnectZooKeeperAsync isInConnectTask is true");
                if (InnerConnectTask == null)
                {
                    return false;
                }
                return await InnerConnectTask;
            }
            else
            {
                //不要在设置isInConnectTask前执行耗时代码（如打印） (已经使用Monitor做同步操作了，不过出于最佳性能考虑，依然不应该在这里添加代码)
                isInConnectTask = true;
                Monitor.Exit(isInConnectTaskSyncTag);
                ShowLog("ConnectZooKeeperAsync isInConnectTask is false");
                InnerConnectTask = ConnectAsync();
                bool result = await InnerConnectTask;
                Monitor.TryEnter(isInConnectTaskSyncTag, 200); //避免isInConnectTask一直被其他线程占用（虽然几率极小）
                isInConnectTask = false;
                try
                {
                    Monitor.Exit(isInConnectTaskSyncTag);
                }
                catch (Exception ex)
                {
                    ShowError(ex.ToString());
                }
                return result;
            }
        }

        /// <summary>
        /// 连接服务（为避免应用层并行连接，请不要直接调用该方法，尝试使用ConnectZooKeeperAsync进行连接）
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ConnectAsync()
        {
            ShowLog("ConnectAsync");
            if (zooKeeper != null)//用？判空，后面会await null
            {
                if(zooKeeper.getState() == ZooKeeper.States.CONNECTED|| zooKeeper.getState()== ZooKeeper.States.CONNECTEDREADONLY)
                {
                    return true;
                }
                await zooKeeper?.closeAsync();
            }
            if (defaultWatch == null)
            {
                defaultWatch = new MyWatcher("DefaultWatch");
            }
            zooKeeper = new ZooKeeper(ConnectionString, SessionTimeOut, defaultWatch);
            await Task.Delay(20);
            int skipTag = 0;
            while (zooKeeper.getState() == ZooKeeper.States.CONNECTING)
            {
                if (skipTag++ > 500)
                {
                    break;
                }
                await Task.Delay(20);
            }
            var state = zooKeeper.getState();
            if (state != ZooKeeper.States.CONNECTED && state != ZooKeeper.States.CONNECTEDREADONLY)
            {
                ReportMessage("连接失败：" + state);
                //多次连接失败后要主动释放，不然会在后台不停重连
                await zooKeeper.closeAsync();
                zooKeeper = null;
                return false;
            }
            innerLossConnectionFlag = false;
            return true;
        }

        /// <summary>
        /// 获取指定路径的Tree结构 （并行查找树）
        /// </summary>
        /// <param name="rootPath"></param>
        /// <returns></returns>
        public async Task<ZNode> GetZNodeTreeEx(string rootPath = "/")
        {
            //int workerThreads;
            //int portThreads;
            //ThreadPool.GetMaxThreads(out workerThreads, out portThreads);
            //ThreadPool.GetMinThreads(out workerThreads, out portThreads);
            //ThreadPool.GetAvailableThreads(out workerThreads, out portThreads);
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            List<Task> taskList = new List<Task>();
            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException("rootPath is null");
            }
            if (!await CheckConnectState())
            {
                return null;
            }
            Stat tempStat = await ExistsAsync(rootPath);
            if (tempStat == null)
            {
                ReportMessage("[GetDubboFuncTree] 路径错误");
                return null;
            }
            ZNode rootNode = new ZNode(null, rootPath, null, ZNode.ZNodeType.Node) { Tag = tempStat };
            await FillZNodeChildren(rootNode, taskList);
            //await Task.WhenAll(taskList);

            while (taskList.Count > 0)
            {
                //Task[] nowTaskAr = taskList.Where(t=> t!=null).ToArray();
                Task[] nowTaskAr = taskList.ToArray();
                ShowLog($"befor WhenAll {nowTaskAr.Length} ThreadPool.ThreadCount {ThreadPool.ThreadCount} ");
                try
                {
                    await Task.WhenAll(nowTaskAr);
                }
                catch (Exception ex)
                {
                    ShowError(ex);
                }
                ShowLog($"after WhenAll {nowTaskAr.Length} ThreadPool.ThreadCount {ThreadPool.ThreadCount} ");
                foreach (var delTask in nowTaskAr)
                {
                    taskList.Remove(delTask);
                }
            }
            stopWatch.Stop();
            ShowLog($"😊😊😊😊😊😊😊😊😊GetZNodeTreeEx:{stopWatch.ElapsedMilliseconds} Version:{rootNode.Version}😊😊😊😊😊😊😊😊😊😊");
            return rootNode;
        }

        /// <summary>
        /// 填充当前节点并自动循环调用继续填充子节点（并不保证填充任务完成，但会将创建的填充任务Task加入到taskList，使用时请将执行完成的Task从列表中移除）
        /// </summary>
        /// <param name="yourNode"></param>
        /// <param name="taskList"></param>
        /// <returns></returns>
        private async Task FillZNodeChildren(ZNode yourNode, List<Task> taskList)
        {
            /*
            //使用ContinueWith无法确保ContinueWith里面的代码会在方法返回前执行完毕（即时里面没有await，几乎里面所有代码都会在当前线程同步执行并在返回前执行完毕，不过反复测试表面会有万分之五的概率ContinueWith没有执行完成方法就会提前返回）
            await GetZNodeChildren(yourNode).ContinueWith((nodes) => {
                if (nodes.Status!= TaskStatus.Faulted && nodes.Result != null)
                {
                    foreach (ZNode node in nodes.Result)
                    {
                        if(node==null)
                        {
                            ShowError($"FillZNodeChildren now FillNodeTask is null");
                            continue;
                        }
                        Task FillNodeTask = null;
                        try
                        {
                            FillNodeTask = FillZNodeChildren(node, taskList);
                        }
                        catch(Exception ex)
                        {
                            ShowError($"FillZNodeChildren Exception {ex.ToString()}");
                            continue;
                        }
                        if (FillNodeTask != null)
                        {
                            taskList.Add(FillNodeTask);
                        }
                        else
                        {
                            ShowError($"FillZNodeChildren now FillNodeTask is null");
                        }
                    }
                }
            });
            **/

            //不使用ContinueWith 速度大概下降了10%，不过数据没有了遗漏
            IReadOnlyList<ZNode> childrenResult = await GetZNodeChildren(yourNode);
            if (childrenResult == null)
            {
                return;
            }
            foreach (ZNode node in childrenResult)
            {
                if (node == null)
                {
                    ShowError($"FillZNodeChildren now FillNodeTask is null");
                    continue;
                }
                Task FillNodeTask = null;
                try
                {
                    FillNodeTask = FillZNodeChildren(node, taskList);
                }
                catch (Exception ex)
                {
                    ShowError($"FillZNodeChildren Exception {ex}");
                    continue;
                }
                if (FillNodeTask != null)
                {
                    taskList.Add(FillNodeTask);
                }
                else
                {
                    ShowError($"FillZNodeChildren now FillNodeTask is null");
                }
            }

        }

        /// <summary>
        /// 更新局部Znode节点信息（ZNode需要是来自GetZNodeTreeEx/GetZNodeTree的子节点）
        /// </summary>
        /// <param name="yourNode">需要更新的节点</param>
        /// <returns></returns>
        public async Task UpdateZNode(ZNode yourNode)
        {
            List<Task> taskList = new List<Task>();
            await FillZNodeChildren(yourNode, taskList);
            while (taskList.Count > 0)
            {
                Task[] nowTaskAr = taskList.ToArray();
                try
                {
                    await Task.WhenAll(nowTaskAr);
                }
                catch (Exception ex)
                {
                    ShowError(ex);
                }
                foreach (var delTask in nowTaskAr)
                {
                    taskList.Remove(delTask);
                }
            }
        }

        /// <summary>
        /// 获取指定路径的Tree结构 （串行查找树）
        /// </summary>
        /// <param name="rootPath"></param>
        /// <returns></returns>
        public async Task<ZNode> GetZNodeTree(string rootPath = "/")
        {
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException("rootPath is null");
            }
            if (!await CheckConnectState())
            {
                return null;
            }
            Stat tempStat = await ExistsAsync(rootPath);
            if (tempStat == null)
            {
                ReportMessage("[GetDubboFuncTree] 路径错误");
                return null;
            }

            ZNode rootNode = new ZNode(null, rootPath, null, ZNode.ZNodeType.Node) { Tag = tempStat };
            IReadOnlyList<ZNode> endNodes = await GetZNodeChildren(rootNode);
            int maxLoop = _maxDepth;
            while (endNodes != null && endNodes.Count > 0)
            {
                maxLoop--;
                if (maxLoop < 0)
                {
                    throw new Exception("exceeded _maxDepth");
                }
                endNodes = await GetZNodeChildren(endNodes);
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
        /// </summary>
        /// <param name="yourNode">当前节点</param>
        /// <returns></returns>
        private async Task<IReadOnlyList<ZNode>> GetZNodeChildren(ZNode yourNode)
        {
            if (yourNode == null)
            {
                throw new ArgumentException("yourNode is null");
            }
            ChildrenResult childrenResult = await GetChildrenAsync(yourNode.FullPath);
            //ShowLog($"---------{yourNode.Path}----------\r\n{childrenResult?.Children.MyToString("\n")}");
            yourNode.ClearChildren();
            if (childrenResult == null)
            {
                yourNode.Type = ZNode.ZNodeType.Error;
            }
            else if (childrenResult.Children?.Count > 0)
            {
                foreach (var tempChild in childrenResult.Children)
                {
                    yourNode.AddChildren(new ZNode(null, tempChild, null, ZNode.ZNodeType.Node));
                }
            }
            return yourNode.Children;
        }

        /// <summary>
        /// 获取/填充指定Node数组的所有子节点（注意会直将子节点加到yourNode下，如果已经被填充了则会更新，但是不更新孙节点）（如果获取错误会将当前节点类型设置为ZNodeType.Error）
        /// </summary>
        /// <param name="yourNodes">节点列表</param>
        /// <param name="isNewTask">是否使用并行任务的模式（默认false）</param>
        /// <returns></returns>
        private async Task<IReadOnlyList<ZNode>> GetZNodeChildren(IReadOnlyList<ZNode> yourNodes, bool isNewTask = false)
        {
            if (yourNodes == null)
            {
                throw new ArgumentException("yourNodes is null");
            }
            //如果连接不了，避免后面遍历连接
            if (!await CheckConnectState())
            {
                ShowError($"GetZNodeChildren 重连接失败");
                return null;
            }
            List<ZNode> resultChildrenList = new List<ZNode>();

            if (isNewTask)
            {
                List<Task> taskList = new List<Task>();
                foreach (ZNode node in yourNodes)
                {
                    Task<IReadOnlyList<ZNode>> taskGetChild = GetZNodeChildren(node);
                    taskList.Add(taskGetChild);
                }
                if (taskList.Count > 0)
                {
                    await Task.WhenAll(taskList.ToArray());
                    //Task.WaitAll(taskList.ToArray());
                    foreach (Task<IReadOnlyList<ZNode>> task in taskList)
                    {
                        if (task.Result != null)
                        {
                            resultChildrenList.AddRange(task.Result);
                        }
                    }

                }
            }
            else
            {
                foreach (ZNode node in yourNodes)
                {
                    IReadOnlyList<ZNode> zs = await GetZNodeChildren(node);
                    if (zs != null)
                    {
                        resultChildrenList.AddRange(zs);
                    }
                }
            }
            return resultChildrenList;
        }


        /// <summary>
        /// 对getChildrenAsync的再次包装,没有children返回ChildrenResult的List为长度为0的list，自动重连，path不存在返回null，重连失败或其他异常会抛出异常（path在使用中是可能被删除的，所以应用应该处理为返回null的情况）
        /// </summary>
        /// <param name="path">节点完全Path</param>
        /// <returns>ChildrenResult列表</returns>
        public async Task<ChildrenResult> GetChildrenAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            //System.Diagnostics.Debug.WriteLine($"---------{path}----------\r\n{Thread.CurrentThread.ManagedThreadId}");
            if (watcher == null)
            {
                return await InnerDoZkRequest(path, agr => zooKeeper.getChildrenAsync(agr), retryTime);
            }
            else
            {
                return await InnerDoZkRequest(path, agr => zooKeeper.getChildrenAsync(agr, watcher), retryTime);
            }
        }


        /// <summary>
        /// 对getDataAsync的再次包装，自动重连，path不存在返回null，重连失败或其他异常会抛出异常（path在使用中是可能被删除的，所以应用应该处理为返回null的情况）
        /// </summary>
        /// <param name="path"></param>
        /// <returns>DataResult</returns>
        public async Task<DataResult> GetDataAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            return await InnerDoZkRequest(path, agr => zooKeeper.getDataAsync(agr, watcher), retryTime);
        }

        /// <summary>
        /// 对existsAsync的再次包装
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public async Task<Stat> ExistsAsync(string path, Watcher watcher = null, int retryTime = 2)
        {
            return await InnerDoZkRequest(path, agr => zooKeeper.existsAsync(agr, watcher), retryTime);
        }


        /// <summary>
        /// 内部ZooKeeper执行方法 （内置可复用的连接及重连逻辑，用于让ZooKeeper执行实际网络请求）
        /// </summary>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <param name="path">路径（将传递到Func的入参）</param>
        /// <param name="func">具体执行方法</param>
        /// <param name="retryTime">重试次数</param>
        /// <returns>返回结果</returns>
        private async Task<T> InnerDoZkRequest<T>(string path, Func<string, Task<T>> func, int retryTime = 2)
        {
            T dataResult = default;
            try
            {
                for (int i = retryTime; i > 0; i--)
                {
                    if (await CheckConnectState())
                    {
                        dataResult = await func(path);
                        break;
                    }
                    await Task.Delay(20);
                }
            }
            catch (KeeperException.ConnectionLossException)
            {
                innerLossConnectionFlag = true;
                if (retryTime > 0)
                {
                    ShowLog($"断线准备重新连接LossException path:{path}");
                    return await InnerDoZkRequest(path, func, retryTime--);
                }
                else
                {
                    ShowError($"重连接失败 path:{path}");
                    throw;
                }
            }
            catch (KeeperException.SessionExpiredException)
            {
                innerLossConnectionFlag = true;
                if (retryTime > 0)
                {
                    return await InnerDoZkRequest(path, func, retryTime--);
                }
                else
                {
                    ShowLog($"断线准备重新连接LossException path:{path}");
                    ShowError($"重连接失败 path:{path}");
                    throw;
                }
            }
            catch (KeeperException.NoNodeException)//查询的时候节点被删除了
            {
                //这个时候childrenResult会是null，func也会返回null/0数量的List
                //yourNode.Type = ZNode.ZNodeType.Error;
                ShowError($"节点不存在 path:{path}");
            }
            catch (Exception ex)
            {
                ShowError(ex.ToString());
                throw;
            }
            return dataResult;
        }


        public string TestFunc(string mes)
        {
            if (!IsConnected)
            {
                if (!ConnectAsync().GetAwaiter().GetResult())
                {
                    return "连接失败";
                }
            }
            DataResult dataResult = zooKeeper.getDataAsync(mes).GetAwaiter().GetResult();
            if (dataResult.Data == null)
            {
                return "dataResult.Data is null";
            }
            var dt = Encoding.UTF8.GetString(dataResult.Data);
            return dt;
        }

        public async void Dispose()
        {
            defaultWatch = null;
            if (zooKeeper != null)//用？判空，后面会await null
            {
                await zooKeeper?.closeAsync();
                zooKeeper = null;
            }
        }
    }

    public class MyWatcher : Watcher
    {
        public string Name { get; private set; }

        public MyWatcher(string name)
        {
            Name = name;
        }

        public override Task process(WatchedEvent @event)
        {
            Console.WriteLine($"{Name} recieve: Path-{@event.getPath()}     State-{@event.getState()}    Type-{@event.get_Type()}");
            return Task.FromResult(0);
        }
    }
}
