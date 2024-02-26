#define PrintRecievedData

using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Threading;
using System.IO;
using System.Threading.Tasks;


/*******************************************************************************
* Copyright (c) 2017 lijie
* All rights reserved.
* 
* 文件名称: 
* 内容摘要: mycllq@hotmail.com
* 
* 历史记录:
* 日	  期:   20170827           创建人: 李杰 15158155511
* 描    述: 创建
*******************************************************************************/

namespace NetService.Telnet
{
    public class ExTelnet:IDisposable
    {
        private class StateObject
        {
            public Socket workSocket = null;
            public byte[] buffer = null;
        }

        /// <summary>
        /// 用于请求命令返回的数据结构 内部类
        /// </summary>
        public class TelnetRequestResult
        {
            public bool IsGetTargetIdentification { get; internal set; }
            public long ElapsedMilliseconds { get; internal set; }
            public string Result { get; internal set; }

        }

        /// <summary>
        /// 确认Telnet业务层包活命令发送时机的内部类
        /// </summary>
        private class LatestCommand
        {
            public bool IsSendCommand { get; set; }
            public DateTime LatestTime { get; set; }
            public byte[] Command { get; set; }

            public int BeatPeriod { get;private set; }

            public LatestCommand(int beatPeriod)
            {
                if(beatPeriod<0)
                {
                    throw new Exception("beatPeriod illegal");
                }
                BeatPeriod = beatPeriod;
            }

            public void Resaet()
            {
                IsSendCommand = false;
            }

            public void SetNewCommand(byte[] cmd)
            {
                IsSendCommand = true;
                LatestTime = DateTime.Now;
                Command = cmd;
            }

            public int GetNextPeriod()
            {
                if(IsSendCommand)
                {
                    TimeSpan timeSpan = DateTime.Now - LatestTime; //public static DateTime operator - (DateTime d, TimeSpan t)
                    int milliseconds = (int)timeSpan.TotalMilliseconds;
                    return BeatPeriod - milliseconds;
                }
                return -1;
            }
        }

        private AutoResetEvent sendDone = new AutoResetEvent(true); //false 非终止状态(阻塞状态)
        private AutoResetEvent receiveDone = new AutoResetEvent(true);
        private AutoResetEvent getReceiveData = new AutoResetEvent(true);
        private readonly object nowShowDataLock = new object();
        private volatile bool isInRequest = false;
      
        private int maxMaintainDataLength = 1024 * 1024 * 8;
        byte[] telnetReceiveBuff = new byte[1024 * 128];
        //private ArrayList optionsList = new ArrayList();

        private Socket mySocket;
        //private AsyncCallback recieveData;

        private IPEndPoint iep;
        private Encoding encoding = Encoding.UTF8;

        private string nowErrorMes;
        private Timer _telnetKeepliveTimer;
        private byte[] _telnetBeatData;
        private LatestCommand _latestCommandForBeat;

        //private StringBuilder nowShowData = new StringBuilder();
        //private StringBuilder allShowData = new StringBuilder();

        private TelnetMemoryStream requestStream = new TelnetMemoryStream(1024 * 1024 * 8);
        private TelnetMemoryStream terminalStream = new TelnetMemoryStream(1024 * 1024 * 8);

        public delegate void delegateDataOut(string mesStr, TelnetMessageType mesType);
        /// <summary>
        /// telnet接收到新消息后返回（请区分TelnetMessageType）
        /// </summary>
        public event delegateDataOut OnMesageReport;

        /// <summary>
        /// 是否已经被释放
        /// </summary>
        internal bool IsDisposed { get; private set; } = false;

        /// <summary>
        /// 获取当前Telnet的IPEndPoint
        /// </summary>
        public IPEndPoint TelnetEndPoint
        {
            get { return iep; }
        }

        /// <summary>
        /// 获取最近的错误信息
        /// </summary>
        public string NowErrorMes
        {
            get { return nowErrorMes; }
        }

        /// <summary>
        /// 获取或设置当前保持返回数据的最大长度（超过该值后开始清除历史数据，但是并不保证终端缓存数据一定小于该值）
        /// </summary>
        public int MaxMaintainDataLength {
            get { return maxMaintainDataLength; }
            set
            {
                maxMaintainDataLength = value;
                requestStream.MaxLength = maxMaintainDataLength;
                terminalStream.MaxLength = maxMaintainDataLength;
            }
        } 

        /// <summary>
        /// 获取获设置telnet接收缓存大小（默认1024 * 128 ，未连接状态可以设置）
        /// </summary>
        public int ReceiveBuffLength
        {
            get { return telnetReceiveBuff.Length; }
            set
            {
                if(IsConnected)
                {
                    throw new Exception("can not set ReceiveBuffLength when IsConnected is true");
                }
                telnetReceiveBuff = new byte[value];
            }
        }

        /// <summary>
        /// 获取或设置查找打印时的最大超时WaitExpectPattern WaitStr 时使用（单位为毫秒）
        /// </summary>
        public int DefaWaitTimeout { get; set; } = 2000;

        /// <summary>
        /// 获取或设置当前终端使用的编码（默认为UTF8）
        /// </summary>
        public Encoding Encoding
        {
            get { return encoding; }
            set { encoding = value; }
        }

        /// <summary>
        /// 获取或设置ExpectPattern（用于时标shell命令结算）
        /// </summary>
        public string DefautExpectPattern { get; set; } = null;
      

        /// <summary>
        /// 是否保存所有终端数据（MaxMaintainDataLength为最大值，如果不需要以终端形式使用请设置为false 以提高效能）
        /// </summary>
        public bool IsSaveTerminalData { get; set; } = false;

        /// <summary>
        /// 是否正在等待前一个命令,置true时如果已经为true，将抛出异常 （Request被限制在半双工模式下，直接使用WriteLine可以让Telnet运行在全双工模式）
        /// </summary>
        public bool IsInRequest
        {
            get { return isInRequest; }
            private set 
            {
                if(isInRequest==true && value==true)
                {
                    throw new Exception("is already in request , just wait previous command request end");
                }
                isInRequest = value;
            } 
        }

        /// <summary>
        /// 获取当前telnet的连接状态
        /// </summary>
        public bool IsConnected
        {
            get 
            {  
                if(mySocket?.Connected ==true)
                {
                    //s.Poll returns true if
                    //connection is closed, reset, terminated or pending(meaning no active connection)
                    //connection is active and there is data available for reading
                    //s.Available returns number of bytes available for reading
                    //if both are true:
                    //there is no data available to read so connection is not active
                    if (mySocket.Poll(1000, SelectMode.SelectRead) && (mySocket.Available == 0))
                    {
                        DisConnect();
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }
        }


        /// <summary>
        /// 通知Telnet消息，如果使用者有订阅OnMesageReport事件，会收到通知消息
        /// </summary>
        /// <param name="mesInfo"></param>
        /// <param name="mesType"></param>
        private void ReportMes(object mesInfo, TelnetMessageType mesType)
        {
            TelnetOptionHelper.ShowDebugLog($"{mesType} {mesInfo}" , "ReportMes");
            if (OnMesageReport != null)
            {
                if (mesInfo is string)
                {
                    OnMesageReport((string)mesInfo, mesType);
                }
                else if (mesInfo is Exception)
                {
                    OnMesageReport(mesType.ToString(), mesType);
                }
                else if (mesInfo is StringBuilder)
                {
                    OnMesageReport(((StringBuilder)mesInfo).ToString(), mesType);
                }
                else if(mesInfo is byte[])
                {
                    OnMesageReport(encoding.GetString((byte[])mesInfo), mesType);
                }
                else
                {
                    throw new Exception("mesInfo type error");
                }
            }
        }

        /// <summary>
        /// 可能会引发异常
        /// </summary>
        /// <param name="Address">主机ip地址 (可以使用Dns.GetHostEntry(host)获取使用主机名的ip)</param>
        /// <param name="Port">端口</param>
        /// <param name="CommandTimeout">查询字符串超时时间，单位毫秒（默认2000 ；0表示不超时）</param>
        public ExTelnet(string Address, int Port, int CommandTimeout =2000)
        {
            iep = new IPEndPoint(IPAddress.Parse(Address), Port);
            DefaWaitTimeout = CommandTimeout;
            //recieveData = new AsyncCallback(OnRecievedData); //简写 recieveData = OnRecievedData;
        }

        public ExTelnet(IPEndPoint yourEp, int CommandTimeout =2000)
        {
            iep = yourEp;
            DefaWaitTimeout = CommandTimeout;
            //recieveData = new AsyncCallback(OnRecievedData);
        }

        //public ExTelnet(IPEndPoint yourEp) : this(yourEp, 50000) { }

        /// <summary>
        /// 设置业务心跳（如果在构造函数中已经设置该选项，调用此方法可以即时修改）
        /// </summary>
        /// <param name="period">心跳间隔（离上次发送业务数据的时机间隔，请不要设置过小的值）(小等于于0，表示销毁当前包活)</param>
        /// <param name="beatData">心跳内容，默认为null 即会使用\r\n</param>
        public void SetTelnetHeartbeat(int period = 30000,byte[] beatData = null)
        {
            if(period<=0)
            {
                _telnetKeepliveTimer?.Dispose();
            }
            if(beatData==null|| beatData.Length==0)
            {
                //\r\n is 0x0d 0x0a 
                //_telnetBeatData = TelnetOptionHelper.NopOPerationBytes;
                _telnetBeatData = new byte[] { 0x0d, 0x0a };
            }

            _latestCommandForBeat = new LatestCommand(period) { IsSendCommand = false };

            if(_telnetKeepliveTimer==null)
            {
                _telnetKeepliveTimer = new Timer(TelnetHeartbeatProc, _latestCommandForBeat, 1000, period);
            }
            else
            {
                _telnetKeepliveTimer.Change(1000, period);
            }
        }

        /// <summary>
        /// 业务心跳执行任务
        /// </summary>
        /// <param name="state"></param>
        private void TelnetHeartbeatProc(object state)
        {
            LatestCommand latestCommand = (LatestCommand)state;
            int tnterval = latestCommand.GetNextPeriod();
            if (tnterval > 0)
            {
                _telnetKeepliveTimer.Change(tnterval, latestCommand.BeatPeriod);
            }
            else
            {
                if (!IsInRequest)
                {
                    if(!WriteRawDataAsync(_telnetBeatData).GetAwaiter().GetResult())
                    {
                        DisConnect();
                    }
                }
                else
                {
                    TelnetOptionHelper.ShowDebugLog("TelnetHeartbeatProc when IsInRequest", "TelnetHeartbeatProc");
                }
            }
            _latestCommandForBeat.Resaet();
        }

        /// <summary>        
        /// 连接telnet (如果IsConnected，仅执行SetSocketKeepAlive，SetTelnetAlive直接返回true)    
        /// </summary> 
        /// <param name="keepAliveTime">TCP 保活计时器，默认-1 小于100表示不设置，使用TCP默认值2h</param>
        /// <param name="telnetAlivePeriod">telnet 业务保护间隔，默认-1 小于100表示不设置 （也可独立调用SetTelnetHeartbeat设置该项，并可以设置保活数据）</param>
        /// <param name="connectTimeOut">应用侧连接超时时间，默认为0表示默认值不设置默认超时将会是2MSL （MSL根据操作系统不同实现会有差距普遍会超过30s，使用不设置该项超时等待时间会超过1min），设置该值会减少无效连接等待时间，如果对连接情况不能完全控制，不建议设置该项</param>
        /// <returns>连接是否成功</returns>
        public async Task<bool> ConnectAsync(int keepAliveTime = -1,int telnetAlivePeriod=-1 ,int connectTimeOut = 0)
        {
            if(IsConnected)
            {
                SetSocketKeepAlive(keepAliveTime);
                SetTelnetAlive(keepAliveTime);
                return true;
            }
            //启动socket 进行telnet操作   
            try
            {
                // Try a blocking connection to the server
                //socketType参数指定类的类型 Socket ， protocolType 参数指定所使用的协议 Socket 。 这两个参数不是独立的。 通常， Socket 类型在协议中是隐式的。 如果 Socket 类型类型和协议类型的组合导致无效，则 Socket 此构造函数引发 SocketException
                mySocket?.Dispose();
                mySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                //mySocket.Connect(iep);//如果需要同步连接的方法，可以使用Connect
                //如果设置过连接超时
                if(connectTimeOut>0)
                {
                    CancellationTokenSource tokenSource = new CancellationTokenSource();
                    CancellationToken ct = tokenSource.Token;
                    Task cancelTask = Task.Delay(connectTimeOut);
                    Task connectTask = mySocket.ConnectAsync(iep,ct).AsTask(); // 带CancellationToken的重载返回的是ValueTask，WhenAny需要Task

                    //double await so if cancelTask throws exception, this throws it
                    await await Task.WhenAny(connectTask, cancelTask);
                    if(!connectTask.IsCompleted)
                    {
                        tokenSource.Cancel();
                        throw new Exception($"Connect timed out for specify  time : {connectTimeOut}ms");
                    }
                }
                else
                {
                    await mySocket.ConnectAsync(iep);
                }
                receiveDone.Set();
                sendDone.Set();
                SetSocketKeepAlive(keepAliveTime);
                SetTelnetAlive(telnetAlivePeriod);
                //接收数据
                //BeginReceive必须通过调用方法完成异步操作 EndReceive 。 通常，方法由 callback 委托调用。
                //在操作完成之前，此方法不会被阻止。若要在操作完成之前一直阻止，请使用 Receive 方法重载之一。
                mySocket.BeginReceive(telnetReceiveBuff, 0, telnetReceiveBuff.Length, SocketFlags.None, OnRecievedData, new StateObject() { workSocket = mySocket, buffer = telnetReceiveBuff });
                ReportMes("Connected", TelnetMessageType.StateChange);
                return true;
            }
            catch (Exception ex)
            {
                nowErrorMes = ex.Message;
                DisConnect();
                ReportMes($"Connect fail : {ex.Message}", TelnetMessageType.StateChange);
                return false;
            }
        }

        #region MyFunction
        /// <summary>
        /// 设置Socket 传输层保活
        /// </summary>
        /// <param name="KeepAliveTime">保护间隔 大于100ms</param>
        /// <returns></returns>
        private bool SetSocketKeepAlive(int KeepAliveTime)
        {
            if (IsConnected && KeepAliveTime > 100)
            {
                try
                {
                    mySocket.SetSocketKeepAliveOption();
                    return true;
                }
                catch (Exception ex)
                {
                    nowErrorMes = ex.Message;
                    TelnetOptionHelper.ShowDebugLog(ex.ToString(), "SetSocketKeepAliveValues");
                    ReportMes(ex, TelnetMessageType.Warning);
                }
            }
            return false;
        }

        /// <summary>
        /// 设置Telnet 应用层业务保活 （用户在Telnet连接成功后，也可以通过SetTelnetHeartbeat修改）
        /// </summary>
        /// <param name="period">保护间隔 大于100ms</param>
        /// <returns></returns>
        private bool SetTelnetAlive(int period )
        {
            if (IsConnected && period > 100)
            {
                SetTelnetHeartbeat(period);
                return true;
            }
            return false;
        }


        /// <summary>
        /// 原始socket消息异步接收处理器
        /// </summary>
        /// <param name="ar"></param>
        private void OnRecievedData(IAsyncResult ar)
        {
            //打印调试信息收到数据报文的处理线程
            //TelnetOptionHelper.ShowDebugLog($"-----------------OnRecievedData CallBack CurrentThread Id:{Thread.CurrentThread.ManagedThreadId}--------------------");
            //关闭或释放连接时会直接促发OnRecievedData
            if (IsDisposed)
            {
                return;
            }
            receiveDone.WaitOne(); 
            StateObject so = (StateObject)ar.AsyncState;
            Socket nowSocket = so.workSocket;
            if (nowSocket == null)
            {
                receiveDone.Set();
                return;
            }
            if (!nowSocket.Connected)
            {
                receiveDone.Set();
                return;
            }

            int recLen = 0;

            try
            {
                //EndReceive方法为结束挂起的异步读取         
                //在 EndReceive 数据可用之前，将阻止该方法
                // 断线 的情况， 发送数据一段时间后会抛异常（可能是1分钟）
                recLen = nowSocket.EndReceive(ar);
            }
            catch(SocketException ex)
            {
                TelnetOptionHelper.ShowDebugLog( $"EndReceive 断线 {ex}","OnRecievedData");
                ReportMes(ex, TelnetMessageType.Error);
            }
            catch (Exception ex)
            {
                TelnetOptionHelper.ShowDebugLog($"EndReceive 未知异常 {ex}", "OnRecievedData");
                ReportMes(ex, TelnetMessageType.Error);
            }
            finally
            {
                if (recLen == 0)
                {
                    //在 EndReceive 数据可用之前，将阻止该方法。(也就是说连接正常一定会等到有数据) 如果使用的是无连接协议， EndReceive 将读取传入网络缓冲区中的第一个排队数据报。
                    //如果远程主机 Socket 使用方法关闭连接 Shutdown 并且收到所有可用数据，则该 EndReceive 方法将立即完成并返回零字节。
                    //如果没有接收到任何数据， 关闭连接 
                    DisConnect();
                    receiveDone.Set();
                }
            }

            //如果有接收到数据的话            
            if (recLen > 0)
            {

                if (recLen > so.buffer.Length)
                {
                    ReportMes(string.Format("ReceiveBuff is out of memory [{0}] [{1}]", so.buffer.Length, recLen), TelnetMessageType.Error);
                    TelnetOptionHelper.ShowDebugLog($"ReceiveBuff is out of memory", "OnRecievedData",true);
                    recLen = so.buffer.Length;
                }

                byte[] tempByte = new byte[recLen];
                Array.Copy(so.buffer, 0, tempByte, 0, recLen);

#if PrintRecievedData
                //打印收到的报文内容
                //TelnetOptionHelper.ShowDebugLog(tempByte, "OnRecievedData");
                TelnetOptionHelper.ShowDebugLog(encoding.GetString(tempByte), "OnRecievedData");
#endif

                try
                {
                    ArrayList optionsList;
                    byte[] tempShowByte = TelnetOptionHelper.DealRawBytes(tempByte,out optionsList);
                    if (tempShowByte.Length > 0)
                    {
                        //string tempNowStr = encoding.GetString(tempShowByte);
                        ReportMes(tempShowByte, TelnetMessageType.ShowData);
                        if (IsInRequest)
                        {
                            AddRequestData(tempShowByte);
                        }
                        if(IsSaveTerminalData)
                        {
                            AddTerminalData(tempShowByte);
                        }
                        getReceiveData.Set();
                    }
                    //超过接收缓存的数据也不可是选项数据，即不用考虑选项被截断的情况
                    DealOptions(optionsList).Wait();
                }
                catch (Exception ex)
                {
                    throw new Exception("控制选项错误 " + ex.Message);
                }
                finally
                {
                    receiveDone.Set(); //要在数据处理完成后开锁，然后在接收完成后也会有可能出现数据错位
                }

                //可以在OnRecievedData直接调用BeginReceive，因为有同步线程锁，msdn示例也是如此
                try
                {
                    //有了数据才会触发callBack委托
                    //执行callBack的托管线程可能与当前线程是同一个线程，当大数据时callBack会马上被执行，BeginReceive后面的代码执行可能会在后面，请考虑锁的互相影响
                    //这里已经通过EventWaitHandle严格控制了时序，所以可以一直复用telnetReceiveBuff，如果不能完全控制时序，OnRecievedData 使用的 StateObject里的byte[]需要是一个新的对象
                    mySocket.BeginReceive(telnetReceiveBuff, 0, telnetReceiveBuff.Length, SocketFlags.None, OnRecievedData, new StateObject() { workSocket = mySocket, buffer = telnetReceiveBuff });
                }
                catch (Exception ex) //BeginReceive时Socket可能被异步关闭
                {
                    ReportMes(ex.Message, TelnetMessageType.Error);
                    TelnetOptionHelper.ShowDebugLog($"BeginReceive Exception :{ex}", "OnRecievedData",true);
                }

            }
           

        }


        /// <summary>        
        ///  处理收到的telnet协商数据，并回复这些协商数据      
        /// </summary>        
        private async Task DealOptions(ArrayList optionsList)
        {
            if (optionsList?.Count > 0)
            {
                byte[] sendResponseOption = null;
                byte[] nowResponseOption = null;
                foreach (byte[] tempOption in optionsList)
                {
                    nowResponseOption = TelnetOptionHelper.GetResponseOption(tempOption,ReportMes);
                    if (nowResponseOption != null)
                    {
                        if (sendResponseOption == null)
                        {
                            sendResponseOption = nowResponseOption;
                        }
                        else
                        {
                            Array.Resize(ref sendResponseOption, sendResponseOption.Length + nowResponseOption.Length);
                            nowResponseOption.CopyTo(sendResponseOption, sendResponseOption.Length - nowResponseOption.Length);
                        }
                    }
                }
                if (sendResponseOption != null)
                {
                    await WriteRawDataAsync(sendResponseOption);
                }
                optionsList.Clear();
            }
        }

        /// <summary>
        /// 清除当前显示缓存
        /// </summary>
        private void ClearShowData()
        {
            if (requestStream.Length > 0)
            {
                requestStream.Clear();
            }
        }

        /// <summary>
        /// 获取当前显示数据（获取之后即从该缓存中移除）
        /// </summary>
        /// <param name="removeEnd">需要被移除的结束标示（默认null 使用DefautExpectPattern ，强制使用“”空 表示不移除）</param>
        /// <returns></returns>
        private async Task<string> GetAndClearShowDataAsync(string removeEnd = null)
        {
            if (removeEnd == null)
            {
                removeEnd = DefautExpectPattern ?? "";
            }
            byte[] tempOutBytes = await requestStream.GetMemoryDataAsync(string.IsNullOrEmpty(removeEnd) ? null : encoding.GetBytes(removeEnd));
            ClearShowData();
            return encoding.GetString(tempOutBytes);
        }

        private void AddRequestData(byte[] yourData)
        {
            lock (nowShowDataLock)
            {
                requestStream.AddDataAsync(yourData).Wait();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private void AddTerminalData(byte[] yourData)//StringBuilder
        {
            if (!IsSaveTerminalData)
            {
                return;
            }
            terminalStream.AddDataAsync(yourData).Wait();
        }

        /// <summary>
        /// 指定时间内等待指定的字符串 （若期望获取较高性能应尽量避免使用WaitStr, 请使用WaitExpectPattern，该方法会有较高性能）
        /// </summary>
        /// <param name="waitStr">等待字符串</param>
        /// <param name="waitTime">等待时间，默认或小于0表示使用默认CommandTimeout，0表示不等待(单位为毫秒)</param>
        /// <returns>查询到返回true，否则为false</returns>
        private async Task<bool> WaitStrAsync(string waitStr, int waitTime = -1)
        {
            if (string.IsNullOrEmpty(waitStr))
            {
                throw new ArgumentNullException(nameof(waitStr));
            }
            if (waitTime < 0)
            {
                waitTime = DefaWaitTimeout;
            }
            if (waitTime > 0)
            {
                long endTicks = DateTime.Now.AddMilliseconds(waitTime).Ticks;
                while (requestStream.FindPosition(encoding.GetBytes(waitStr))<0)
                {
                    if (DateTime.Now.Ticks > endTicks)
                    {
                        return false;
                    }
                    await Task.Delay(waitTime > 5000 ? 1000 : 100);
                }
                return true;
            }
            else
            {
                return requestStream.FindPosition(encoding.GetBytes(waitStr))>0;
            }
        }


        /// <summary>
        /// 等待结束标示（经历使用该方法进行数据等待）
        /// </summary>
        /// <param name="expectPattern">expectPattern只能出现在接收数据的尾部</param>
        /// <param name="waitTime">等待时间，默认或小于0表示使用默认CommandTimeout（注意这个时间是会在有新数据收到后，再等待指定时间，所以最大可能会等待2*waitTime），0表示不等待(单位为毫秒)</param>
        /// <returns></returns>
        private async Task<bool> WaitExpectPatternAsync(string expectPattern, int waitTime = -1)
        {
            if(string.IsNullOrEmpty(expectPattern))
            {
                throw new ArgumentNullException(nameof(expectPattern));
            }
            bool isFind = false;
            byte[] expectPatternBytes = encoding.GetBytes(expectPattern);
            if (waitTime < 0)
            {
                waitTime = DefaWaitTimeout;
            }
            if (waitTime > 0)
            {
                //因为等待计时器精度原因，WaitOneAsync后的时间可能不能超过指定时间，这里超过10ms将目标时间-1
                long endTicks = DateTime.Now.AddMilliseconds(waitTime<10? waitTime: waitTime-1).Ticks;
                while (!isFind)
                {
                    //lock (nowShowDataLock)
                    //{
                    //    if (nowShowData.Length > expectPattern.Length)
                    //    {
                    //        //仅将缓存StringBuilder中尾部ToString可获得较高性能
                    //        isFind = nowShowData.ToString(nowShowData.Length - expectPattern.Length, expectPattern.Length) == expectPattern;
                    //    }
                    //}
                    isFind = requestStream.IsGetEndFlag(expectPatternBytes);
                    if (isFind)
                    {
                        return true;
                    }
                    if (DateTime.Now.Ticks > endTicks)
                    {
                        return false;
                    }
                    //getReceiveData.WaitOne(waitTime);
                    await getReceiveData?.WaitOneAsync(waitTime);//WaitHandle 的异步等待方法
                    if (IsDisposed) break;
                }
                return isFind;
            }
            //如果 为0 不等待
            else
            {
                isFind = requestStream.IsGetEndFlag(expectPatternBytes);
                return (isFind);
            }
        }
#endregion

        /// <summary>
        /// 获取当前显示数据（递增）
        /// </summary>
        public async Task<string> GetNowRequestDataAsync()
        {
            byte[] bytes = await requestStream.GetMemoryDataAsync();
            return encoding.GetString(bytes);
        }


        /// <summary>
        /// 获取整个输出（但超过最大长度后，会清除前面的内容）
        /// </summary>
        public async Task<string> GetTerminalDataAsync()
        {

            if (!IsSaveTerminalData)
            {
                throw new Exception("IsSaveTerminalData is false ,if you want use AllLogData just set IsSaveTerminalData true");
            }
            byte[] bytes = await terminalStream.GetMemoryDataAsync();
            return encoding.GetString(bytes);
            //return allShowData.ToString();
        }

        /// <summary>
        /// 发送原始数据 (发送数据的统一插口，请仅使用该方法发送Socket实际数据)
        /// </summary>
        /// <param name="yourData"></param>
        /// <returns></returns>
        private async Task<bool> WriteRawDataAsync(byte[] yourData)
        {
            if (!mySocket.Connected)
            {
                return false;
            }
            sendDone?.WaitOne();
            if (IsDisposed) return false;
            try
            {
                //SocketFlags可以设置Flag位
                //mySocket.Send(yourData, SocketFlags.None);
                await mySocket.SendAsync(yourData, SocketFlags.None);
                _latestCommandForBeat?.SetNewCommand(null);
            }
            catch (Exception ex)
            {
                nowErrorMes = ex.Message;
                TelnetOptionHelper.ShowDebugLog(ex.ToString());
                return false;
            }
            finally
            {
                sendDone.Set();
            }
            //BeginSend 可被 SendAsync 代替
            //mySocket.BeginSend(yourData, 0, yourData.Length, SocketFlags.None, new AsyncCallback((IAsyncResult ar) =>
            //{
            //    try
            //    {
            //        Socket client = (Socket)ar.AsyncState;
            //        int bytesSent = client.EndSend(ar);

            //        ShowDebugLog(string.Format("Sent {0} bytes to server.", bytesSent));
            //        ShowDebugLog(yourData);
            //    }
            //    catch (Exception ex)
            //    {
            //        ReportMes(string.Format("error in send data with :{0}", ex.Message), TelnetMessageType.Error);
            //    }
            //    finally
            //    {
            //        sendDone.Set();
            //    }

            //}), mySocket);
            return true;
        }

        /// <summary>
        /// 写入文本消息（使用设置的默认编码）（如非必要，外部谨慎调用）
        /// </summary>
        /// <param name="message">文本消息</param>
        /// <returns>是否发送/写入成功</returns>
        public async Task<bool> WriteAsync(string message)
        {
            return await WriteRawDataAsync(encoding.GetBytes(message));
        }

        /// <summary>
        /// 写入字节数组（如非必要，外部谨慎调用）
        /// </summary>
        /// <param name="bytes">字节数组</param>
        /// <returns>是否发送/写入成功</returns>
        public async Task<bool> WriteAsync(byte[] bytes)
        {
            return await WriteRawDataAsync(bytes);
        }

        /// <summary>
        /// 写入文本消息,并自动加入换行 TelnetOptionHelper.ENDOFLINE（使用设置的默认编码）
        /// </summary>
        /// <param name="message">文本消息</param>
        /// <returns>是否发送/写入成功</returns>
        public async Task<bool> WriteLineAsync(string message)
        {
            return await WriteRawDataAsync(encoding.GetBytes((message ?? "") + TelnetOptionHelper.ENDOFLINE));// "1"??""+"1"="1"
        }



        /// <summary>
        /// 发起一个命令并以阻塞的形式获取返回（获取指定查找字符串时返回,请避免使用该方法，请使用DoRequestAsync）
        /// 因为该方法使用场景特殊不具有普遍性，没有优化，性能比较低
        /// </summary>
        /// <param name="cmd">命令</param>
        /// <param name="waitStr">指定查找字符串</param>
        /// <returns>命令返回</returns>
        public async Task<TelnetRequestResult> DoRequestWithWaitStrAsync(string cmd, string waitStr)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                throw new ArgumentNullException(nameof(cmd));
            }
            TelnetRequestResult result = new TelnetRequestResult() { IsGetTargetIdentification = false };
            IsInRequest = true;
            ClearShowData();
            await WriteLineAsync(cmd);
            result.IsGetTargetIdentification = await WaitStrAsync(waitStr);
            IsInRequest = false;
            result.Result = await GetAndClearShowDataAsync();
            return result;
        }


        /// <summary>
        /// 发起一个命令并以阻塞的形式获取返回（获取expectPattern时返回）
        /// </summary>
        /// <param name="cmd">命令</param>
        /// <param name="expectPattern">expectPattern（如#$等）(不填或为null将使用DefaultExpectPattern；填""空字符串将不查找结束标识，直接等待waitTime后返回)</param>
        /// <param name="waitTime">最大超时等待时间（小于0则使用DefaWaitTimeout）</param>
        /// <returns></returns>
        public async Task<TelnetRequestResult> DoRequestAsync(string cmd, string expectPattern = null, int waitTime = -1)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                throw new ArgumentNullException(nameof(cmd));
            }
            TelnetRequestResult result = new TelnetRequestResult() { IsGetTargetIdentification = false };
            IsInRequest = true;
            ClearShowData();
            long startTicks = DateTime.UtcNow.Ticks; //一个计时周期表示一百纳秒，即一千万分之一秒。
            await WriteLineAsync(cmd);
            //确认expectPattern
            if (expectPattern==null)
            {
                expectPattern = DefautExpectPattern ?? "";
            }
            //等待命令完成
            if(expectPattern=="")
            {
                if (waitTime < 0) waitTime = DefaWaitTimeout;
                //Thread.Sleep(waitTime);
                await Task.Delay(waitTime);
            }
            else
            {
                result.IsGetTargetIdentification = await WaitExpectPatternAsync(expectPattern, waitTime);
            }
            IsInRequest = false;
            result.Result = await GetAndClearShowDataAsync();
            result.ElapsedMilliseconds = (DateTime.UtcNow.Ticks - startTicks) / 10000;
            return result;
        }

        public void DisConnect()
        {
            if (mySocket == null)
            {
                return;
            }
            if (mySocket.Connected)
            {
                try
                {
                    //Shutdown 禁用了 Send 或者 Receive 方法，具体取决与调用方法时提供的参数。它不会禁用底层的协议处理并且从不造成阻塞。
                    //如果 Send 被禁用，它仍会向底层的发送缓冲中入列一个零字节（zero - byte）数据包。当接收端收到这个数据包时，它将会知道发送端不会再发送任何数据。
                    //如果 Receive 被禁用，发送端尝试发送的任何数据都会丢失。
                    //如果只禁用了 Receive 但没有禁用 Send ，那么只会阻止Socket接收数据。因为没有发出零字节数据包，所以另一端对此不会有任何感知，除非它发送了某些Socket协议要求进行确认的信息。
                    //可以进入当工状态
                    mySocket.Shutdown(SocketShutdown.Both);
                    //首先，Disconnect 不等同于 Shutdown(SocketShutdown.Both) 。
                    //其次，它会造成阻塞，等待两件事：
                    //所有已入列的数据被发送。
                    //另一端确认零字节数据包（如果底层协议适用）。
                    //如果调用了 Disconnect(false) ，系统资源将会被释放。
                    //mySocket.Disconnect(true);

                }
                finally
                {
                    //会释放系统资源。它可能会突然停止发送已入列的数据。如果调用此方法时带有参数，那么他会在指定的超时时间内等待数据发送。
                    //不会抛异常
                    mySocket.Close();
                    ReportMes("DisConnect", TelnetMessageType.StateChange);
                }
            }
            if (_telnetKeepliveTimer != null)
            {
                _telnetKeepliveTimer.Dispose();
                _telnetKeepliveTimer = null;
            }
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                _telnetKeepliveTimer?.Dispose();
                DisConnect();
                mySocket?.Dispose();
                mySocket = null;
                requestStream?.Dispose();
                requestStream = null;
                terminalStream?.Dispose();
                terminalStream = null;

                if(!sendDone.WaitOne(0))
                {
                    sendDone.Set();
                    Thread.Yield();
                }
                if (!getReceiveData.WaitOne(0))
                {
                    getReceiveData.Set();
                    Thread.Yield();
                }

                sendDone.Dispose();
                sendDone = null;
                receiveDone.Dispose();
                receiveDone = null;
                getReceiveData.Dispose();
                getReceiveData = null;
            }
        }
    }
}