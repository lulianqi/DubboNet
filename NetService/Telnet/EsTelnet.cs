using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Threading;
using MyCommonHelper;
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
    /// <summary>
    /// 该Telnet实现已经被弃用，仅用用于对比测试，请使用支持异步操作的ExTelnet
    /// </summary>
    public class EsTelnet
    {
        private class StateObject
        {
            public Socket workSocket = null;
            public byte[] buffer = null;
        }

        private AutoResetEvent sendDone = new AutoResetEvent(true); //false 非终止状态
        private AutoResetEvent receiveDone = new AutoResetEvent(true);
        private AutoResetEvent getReceiveData = new AutoResetEvent(true);
        private readonly object nowShowDataLock = new object();
        private volatile bool isInRequest = false;

        private int maxMaintainDataLength = 1024 * 1024 * 8;
        byte[] telnetReceiveBuff = new byte[1024 * 128];
        private ArrayList optionsList = new ArrayList();

        private Socket mySocket;
        //private AsyncCallback recieveData;

        private IPEndPoint iep;
        private Encoding encoding = Encoding.UTF8;

        private string nowErrorMes;

        private StringBuilder nowShowData = new StringBuilder();
        private StringBuilder allShowData = new StringBuilder();

        private TelnetMemoryStream requestStream = new TelnetMemoryStream(1024 * 1024 * 8);
        private TelnetMemoryStream terminalStream = new TelnetMemoryStream(1024 * 1024 * 8);


        public delegate void delegateDataOut(string mesStr, TelnetMessageType mesType);
        /// <summary>
        /// telnet接收到新消息后返回（请区分TelnetMessageType）
        /// </summary>
        public event delegateDataOut OnMesageReport;


        /// <summary>
        /// 获取当前显示数据（递增）
        /// </summary>
        public string NowShowData
        {
            get
            {
                return nowShowData.ToString();
            }
        }


        /// <summary>
        /// 获取整个输出（但超过最大长度后，会清除前面的内容）
        /// </summary>
        public string AllTerminalData
        {
            get
            {
                if (!IsSaveTerminalData)
                {
                    throw new Exception("IsSaveTerminalData is false ,if you want use AllLogData just set IsSaveTerminalData true");
                }
                return allShowData.ToString();
            }
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
        public int MaxMaintainDataLength
        {
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
                if (IsConnected)
                {
                    throw new Exception("can not set ReceiveBuffLength when IsConnected is true");
                }
                telnetReceiveBuff = new byte[value];
            }
        }

        /// <summary>
        /// 获取或设置查找打印时的最大超时WaitExpectPattern WaitStr 时使用（单位为毫秒）
        /// </summary>
        public int DefaWaitTimeout { get; set; } = 10000;

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
        /// 是否正在等待前一个命令 （Request被限制在半双工模式下，直接使用WriteLine可以让Telnet运行在全双工模式）
        /// </summary>
        public bool IsInRequest
        {
            get { return isInRequest; }
            private set
            {
                if (isInRequest == true && value == true)
                {
                    throw new Exception("is already in request , just wait previous command request end");
                }
                isInRequest = value;
            }
        }

        public bool IsConnected
        {
            get
            {
                if (mySocket?.Connected == true)
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
        /// 清除当前显示缓存，并将其移至全局缓存
        /// </summary>
        private void ClearShowData()
        {
            if (nowShowData.Length > 0)
            {
                lock (nowShowDataLock)
                {
                    nowShowData.Clear();
                }
            }
        }

        /// <summary>
        /// 获取当前显示数据（获取之后即从该缓存中移除）
        /// </summary>
        /// <param name="removeEnd">需要被移除的结束标示（默认null 使用DefautExpectPattern ，强制使用“”空 表示不移除）</param>
        /// <returns></returns>
        private string GetAndClearShowData(string removeEnd = null)
        {
            if (removeEnd == null)
            {
                removeEnd = DefautExpectPattern ?? "";
            }
            lock (nowShowDataLock)
            {

                if (removeEnd != "" && nowShowData.ToString(nowShowData.Length - removeEnd.Length, removeEnd.Length) == removeEnd)
                {
                    nowShowData.Remove(nowShowData.Length - removeEnd.Length, removeEnd.Length);
                }
                string tempOutStr = nowShowData.ToString();
                nowShowData.Clear();
                return tempOutStr;
            }
        }

        private void AddNowShowData(string yourData)
        {
            lock (nowShowDataLock)
            {
                //nowShowData是有可能短时间超过MaxMaintainDataLength的
                if ((nowShowData.Length + yourData.Length) > MaxMaintainDataLength)
                {
                    nowShowData.Remove(0, nowShowData.Length / 2);
                }
                nowShowData.Append(yourData);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        private void AddAllShowData(String yourData)//StringBuilder
        {
            if (!IsSaveTerminalData)
            {
                return;
            }
            if ((allShowData.Length + yourData.Length) > MaxMaintainDataLength)
            {
                //这里如果单次时间过大，长度会超过极限值（下次会被减半）
                allShowData.Remove(0, allShowData.Length / 2);
            }
            allShowData.Append(yourData);
        }

        /// <summary>
        /// 通知Telnet消息，如果使用者有订阅OnMesageReport事件，会收到通知消息
        /// </summary>
        /// <param name="mesInfo"></param>
        /// <param name="mesType"></param>
        private void ReportMes(object mesInfo, TelnetMessageType mesType)
        {
            if (OnMesageReport != null)
            {
                TelnetOptionHelper.ShowDebugLog(mesType.ToString() + mesInfo, "ReportMes");
                if (mesInfo is string)
                {
                    OnMesageReport((string)mesInfo, mesType);
                }
                else if (mesInfo is StringBuilder)
                {
                    OnMesageReport(((StringBuilder)mesInfo).ToString(), mesType);
                }
                else if (mesInfo is byte[])
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
        /// <param name="CommandTimeout">查询字符串超时时间，单位毫秒（0，为不超时）</param>
        public EsTelnet(string Address, int Port, int CommandTimeout)
        {
            iep = new IPEndPoint(IPAddress.Parse(Address), Port);
            DefaWaitTimeout = CommandTimeout;
            //recieveData = new AsyncCallback(OnRecievedData); //简写 recieveData = OnRecievedData;
        }

        public EsTelnet(IPEndPoint yourEp, int CommandTimeout)
        {
            iep = yourEp;
            DefaWaitTimeout = CommandTimeout;
            //recieveData = new AsyncCallback(OnRecievedData);
        }

        public EsTelnet(IPEndPoint yourEp) : this(yourEp, 50000) { }


        /// <summary>        
        /// 连接telnet     
        /// </summary>                                                                
        public bool Connect()
        {
            //启动socket 进行telnet操作   
            try
            {
                // Try a blocking connection to the server
                mySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                mySocket.Connect(iep);

                receiveDone.Set();
                sendDone.Set();
                //接收数据
                //BeginReceive必须通过调用方法完成异步操作 EndReceive 。 通常，方法由 callback 委托调用。
                //在操作完成之前，此方法不会被阻止。若要在操作完成之前一直阻止，请使用 Receive 方法重载之一。
                mySocket.BeginReceive(telnetReceiveBuff, 0, telnetReceiveBuff.Length, SocketFlags.None, OnRecievedData, new StateObject() { workSocket = mySocket, buffer = telnetReceiveBuff });
                return true;
            }
            catch (Exception ex)
            {
                nowErrorMes = ex.Message;
                return false;
            }
        }

        #region MyFunction

        private void OnRecievedData(IAsyncResult ar)
        {
            TelnetOptionHelper.ShowDebugLog($"-----------------OnRecievedData CallBack CurrentThread Id:{Thread.CurrentThread.ManagedThreadId}--------------------");
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
            catch (SocketException ex)
            {
                TelnetOptionHelper.ShowDebugLog($"EndReceive 断线 {ex}", "OnRecievedData");
            }
            catch (Exception ex)
            {
                TelnetOptionHelper.ShowDebugLog($"EndReceive 未知异常 {ex}", "OnRecievedData");
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
                    ReportMes("DisConnect with Exception", TelnetMessageType.Error);
                }
            }

            //如果有接收到数据的话            
            if (recLen > 0)
            {

                if (recLen > so.buffer.Length)
                {
                    ReportMes(string.Format("ReceiveBuff is out of memory [{0}] [{1}]", so.buffer.Length, recLen), TelnetMessageType.Error);
                    TelnetOptionHelper.ShowDebugLog($"ReceiveBuff is out of memory", "OnRecievedData", true);
                    recLen = so.buffer.Length;
                }

                byte[] tempByte = new byte[recLen];
                Array.Copy(so.buffer, 0, tempByte, 0, recLen);

                TelnetOptionHelper.ShowDebugLog(tempByte, "OnRecievedData");
                try
                {
                    byte[] tempShowByte = TelnetOptionHelper.DealRawBytes(tempByte, out optionsList);
                    if (tempShowByte.Length > 0)
                    {
                        string tempNowStr = encoding.GetString(tempShowByte);
                        ReportMes(tempNowStr, TelnetMessageType.ShowData);
                        if (IsInRequest)
                        {
                            AddNowShowData(tempNowStr);
                        }
                        if (IsSaveTerminalData)
                        {
                            AddAllShowData(tempNowStr);
                        }
                        getReceiveData.Set();
                    }
                    //超过接收缓存的数据也不可是选项数据，即不用考虑选项被截断的情况
                    DealOptions();
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
                    TelnetOptionHelper.ShowDebugLog($"BeginReceive Exception :{ex}", "OnRecievedData", true);
                }

            }


        }


        /// <summary>        
        ///  处理收到的telnet协商数据，并回复这些协商数据      
        /// </summary>        
        private void DealOptions()
        {
            if (optionsList.Count > 0)
            {
                byte[] sendResponseOption = null;
                byte[] nowResponseOption = null;
                foreach (byte[] tempOption in optionsList)
                {
                    nowResponseOption = TelnetOptionHelper.GetResponseOption(tempOption, ReportMes);
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
                    WriteRawData(sendResponseOption);
                }
                optionsList.Clear();
            }
        }


        /// <summary>
        /// 发送原始数据
        /// </summary>
        /// <param name="yourData"></param>
        /// <returns></returns>
        private bool WriteRawData(byte[] yourData)
        {
            if (!mySocket.Connected)
            {
                return false;
            }
            sendDone.WaitOne();
            try
            {
                //SocketFlags可以设置Flag位
                mySocket.Send(yourData, SocketFlags.None);
            }
            catch (Exception ex)
            {
                //fuxiao
                TelnetOptionHelper.ShowDebugLog(ex.ToString());
            }
            finally
            {
                sendDone.Set();
            }
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

        #endregion

        /// <summary>
        /// 指定时间内等待指定的字符串 （若期望获取较高性能应尽量避免使用wait）
        /// </summary>
        /// <param name="waitStr">等待字符串（不区分大小写）</param>
        /// <param name="waitTime">等待时间，默认或小于0表示使用默认CommandTimeout，0表示不等待(单位为毫秒)</param>
        /// <returns>查询到返回true，否则为false</returns>
        private bool WaitStr(string waitStr, int waitTime = -1)
        {
            if (waitTime < 0)
            {
                waitTime = DefaWaitTimeout;
            }
            if (waitTime > 0)
            {
                long endTicks = DateTime.Now.AddMilliseconds(waitTime).Ticks;
                while (((nowShowData.ToString()).ToLower()).IndexOf(waitStr.ToLower()) == -1)
                {
                    if (DateTime.Now.Ticks > endTicks)
                    {
                        return false;
                    }
                    Thread.Sleep(100);
                }
                return true;
            }
            else
            {
                return ((nowShowData.ToString()).ToLower().Contains(waitStr.ToLower()));
            }
        }


        /// <summary>
        /// 等待结束标示（经历使用该方法进行数据等待）
        /// </summary>
        /// <param name="expectPattern">expectPattern只能出现在接收数据的尾部</param>
        /// <param name="waitTime">等待时间，默认或小于0表示使用默认CommandTimeout，0表示不等待(单位为毫秒)</param>
        /// <returns></returns>
        private bool WaitExpectPattern(string expectPattern, int waitTime = -1)
        {
            bool isFind = false;
            if (waitTime < 0)
            {
                waitTime = DefaWaitTimeout;
            }
            if (waitTime > 0)
            {
                long endTicks = DateTime.Now.AddMilliseconds(waitTime).Ticks;
                while (!isFind)
                {
                    lock (nowShowDataLock)
                    {
                        if (nowShowData.Length > expectPattern.Length)
                        {
                            //仅将缓存StringBuilder中尾部ToString可获得较高性能
                            isFind = nowShowData.ToString(nowShowData.Length - expectPattern.Length, expectPattern.Length) == expectPattern;
                        }
                    }
                    if (isFind)
                    {
                        return true;
                    }
                    if (DateTime.Now.Ticks > endTicks)
                    {
                        return false;
                    }
                    getReceiveData.WaitOne(waitTime);
                }
                return isFind;
            }
            else
            {
                lock (nowShowDataLock)
                {
                    if (nowShowData.Length > expectPattern.Length)
                    {
                        isFind = nowShowData.ToString(nowShowData.Length - expectPattern.Length, expectPattern.Length) == expectPattern;
                    }
                    else
                    {
                        isFind = false;
                    }
                }
                return (isFind);
            }
        }

        public bool Write(string message)
        {
            return WriteRawData(encoding.GetBytes(message));
        }

        public bool Write(byte[] bytes)
        {
            return WriteRawData(bytes);
        }

        public bool WriteLine(string message)
        {
            return WriteRawData(encoding.GetBytes(message + TelnetOptionHelper.ENDOFLINE));
        }



        /// <summary>
        /// 发起一个命令并以阻塞的形式获取返回（获取指定查找字符串时返回）
        /// </summary>
        /// <param name="cmd">命令</param>
        /// <param name="waitStr">指定查找字符串</param>
        /// <returns>命令返回</returns>
        public string DoRequestWithWaitStr(string cmd, string waitStr)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                throw new ArgumentNullException(nameof(cmd));
            }
            IsInRequest = true;
            ClearShowData();
            WriteLine(cmd);
            WaitStr(waitStr);
            IsInRequest = false;
            return GetAndClearShowData();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cmd">命令</param>
        /// <param name="expectPattern">expectPattern（如#$等）</param>
        /// <returns>命令返回</returns>
        /// 


        /// <summary>
        /// 发起一个命令并以阻塞的形式获取返回（获取expectPattern时返回）
        /// </summary>
        /// <param name="cmd">命令</param>
        /// <param name="expectPattern">expectPattern（如#$等）(不填或为null将使用DefaultExpectPattern；填""空字符串将不查找结束标识，直接等待waitTime后返回)</param>
        /// <param name="waitTime">最大超时等待时间（小于0则使用DefaWaitTimeout）</param>
        /// <returns></returns>
        public string DoRequest(string cmd, string expectPattern = null, int waitTime = -1)
        {
            if (string.IsNullOrEmpty(cmd))
            {
                throw new ArgumentNullException(nameof(cmd));
            }
            IsInRequest = true;
            ClearShowData();
            WriteLine(cmd);
            if (expectPattern == null)
            {
                expectPattern = DefautExpectPattern ?? "";
            }
            if (expectPattern == "")
            {
                if (waitTime < 0) waitTime = DefaWaitTimeout;
                Thread.Sleep(waitTime);
            }
            else
            {
                WaitExpectPattern(expectPattern, waitTime);
            }
            IsInRequest = false;
            return GetAndClearShowData();
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
                }
            }
        }


    }
}