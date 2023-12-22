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
            Info, Warn, Error
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
    }
}
