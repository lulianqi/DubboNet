using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetService.Telnet
{
    internal static class TelnetStaticHelper
    {
        //from https://docs.microsoft.com/zh-cn/dotnet/standard/asynchronous-programming-patterns/interop-with-other-asynchronous-patterns-and-types?redirectedfrom=MSDN
        internal static Task WaitOneAsync(this WaitHandle waitHandle ,int millisecondsTimeOutInterval =-1)
        {
            if (waitHandle == null)
                throw new ArgumentNullException("waitHandle");

            var tcs = new TaskCompletionSource<bool>();
            var rwh = ThreadPool.RegisterWaitForSingleObject(waitHandle,
                delegate { tcs.TrySetResult(true); }, null, millisecondsTimeOutInterval, true);
            var t = tcs.Task;
            t.ContinueWith((antecedent) => rwh.Unregister(null));
            return t;
        }


        /// <summary>
        /// 返回查找byte[]中的一个出现指定字符的位置（从MyCommonHelper复制而来，为了独立发布）
        /// </summary>
        /// <param name="bytes">byte[]</param>
        /// <param name="targetByte">查找目标</param>
        /// <param name="startIndex">开始索引</param>
        /// <param name="leng">最大搜索长度</param>
        /// <returns></returns>
        public static int MyIndexOf(this byte[] bytes, byte targetByte, int startIndex, int leng)
        {
            for (int i = startIndex; i < leng; i++)
            {
                if (bytes[i] == targetByte)
                {
                    return startIndex + i;
                }
            }
            return -1;
        }

        public static int MyIndexOf(this byte[] bytes, byte targetByte)
        {

            return MyIndexOf(bytes, targetByte, 0, bytes.Length);
        }

        public static int MyIndexOf(this byte[] bytes, byte targetByte, int startIndex)
        {
            return MyIndexOf(bytes, targetByte, startIndex, bytes.Length - startIndex);
        }

        /// <summary>
        /// 使用IOControl设置SocketKeepAliveValues，可以设置以毫秒为精度（因为使用了IOControlCode，只能在windows平台上使用）
        /// </summary>
        /// <param name="instance">Socket</param>
        /// <param name="KeepAliveTime">多长时间开始第一次探测（毫秒）</param>
        /// <param name="KeepAliveInterval">探测时间间隔（毫秒）</param>
        public static void SetSocketKeepAliveValues(this Socket instance, int KeepAliveTime = 60*1000, int KeepAliveInterval=1000)
        {
            //KeepAliveTime: default value is 2hr
            //KeepAliveInterval: default value is 1s and Detect 5 times

            //the native structure
            //struct tcp_keepalive {
            //ULONG onoff;
            //ULONG keepalivetime;
            //ULONG keepaliveinterval;
            //};

            int size = Marshal.SizeOf(new uint());
            byte[] inOptionValues = new byte[size * 3]; // 4 * 3 = 12
            bool OnOff = true;

            BitConverter.GetBytes((uint)(OnOff ? 1 : 0)).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes((uint)KeepAliveTime).CopyTo(inOptionValues, size);//多长时间开始第一次探测
            BitConverter.GetBytes((uint)KeepAliveInterval).CopyTo(inOptionValues, size * 2);//探测时间间隔
            instance.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null); //仅支持windows平台
        }

        /// <summary>
        /// 使用SetSocketOption设置SocketKeepAliveValues，精度为秒（可跨平台使用）
        /// </summary>
        /// <param name="socket">Socket</param>
        /// <param name="KeepAliveTime">多长时间开始第一次探测（秒）</param>
        /// <param name="KeepAliveInterval">失败后探测时间间隔（秒）</param>
        /// <param name="retryTime">重试次数</param>
        public static void  SetSocketKeepAliveOption(this Socket socket, int KeepAliveTime =20, int KeepAliveInterval = 5 ,int retryTime = 3)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveInterval);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveTime);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, retryTime);
        }

    }

}
