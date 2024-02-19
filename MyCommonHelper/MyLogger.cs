#define LogDiagnostics

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyCommonHelper
{
    public class MyLogger
    {
        public enum LogType
        {
            Info, Warn, Error,Debug
        }

        public static void Print(string info , LogType logType = LogType.Info)
        {
            Console.WriteLine(info);
        }

        public static void LogInfo(string mes)
        {
            Print($"[{DateTime.Now}] {mes}");
        }

        public static void LogWarning(string mes)
        {
            Print($"[{DateTime.Now}] {mes}" , LogType.Warn);
        }

        public static void LogError(string mes, Exception ex = null)
        {

            if (ex == null)
            {
                Print($"[{DateTime.Now}] {mes}" , LogType.Error);
            }
            else
            {
                Print($"[{DateTime.Now}] {mes} {ex.Message}" , LogType.Error);
            }
        }

        public static void LogDebug(string mes, Exception ex = null)
        {
            #if DEBUG
            if (ex == null)
            {
                Print($"[{DateTime.Now}] {mes}" , LogType.Debug);
            }
            else
            {
                Print($"[{DateTime.Now}] {mes} {ex.Message}" , LogType.Debug);
            }
            #endif
        }


        private const string infoLogPrefixStr = "-----------------------";
        private const string errorLogPrefixStr = "💔💔💔💔💔💔💔💔💔💔";

        /// <summary>
        /// 打印调试数据，发布时请关闭LogDiagnostics，以禁止打印
        /// </summary>
        /// <param name="debugLog"></param>
        public static void LogDiagnostics(string debugLog, string title = null, bool isErrorLog = false)
        {
#if LogDiagnostics
            string prefixStr = isErrorLog ? errorLogPrefixStr : infoLogPrefixStr;
            System.Diagnostics.Debug.WriteLine($"{prefixStr}{title ?? ""}[{DateTime.Now.ToString("HH:mm:ss fff")}]{prefixStr}");
            System.Diagnostics.Debug.WriteLine(debugLog);
#endif
        }

        /// <summary>
        /// 打印调试数据，发布时请关闭LogDiagnostics，以禁止打印
        /// </summary>
        /// <param name="debugLog"></param>
        /// <param name="hexaDecimal"></param>
        public static void LogDiagnostics(byte[] debugLog, string title = null, HexaDecimal hexaDecimal = HexaDecimal.hex16, bool isErrorLog = false)
        {
#if LogDiagnostics
            string prefixStr = isErrorLog ? errorLogPrefixStr : infoLogPrefixStr;
            System.Diagnostics.Debug.WriteLine($"{prefixStr}{title ?? ""}[{DateTime.Now.ToString("HH:mm:ss fff")}]{prefixStr}");
            System.Diagnostics.Debug.WriteLine($"byte[] leng is : {debugLog.Length}");
            System.Diagnostics.Debug.WriteLine(MyBytes.ByteToHexString(debugLog, hexaDecimal, ShowHexMode.space));
            //System.Diagnostics.Debug.WriteLine(Encoding.ASCII.GetString(debugLog));
            System.Diagnostics.Debug.WriteLine(Encoding.UTF8.GetString(debugLog));
#endif
        }
    }
}
