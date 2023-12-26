#define LogDubg

using System;
using System.Reflection;

namespace MyCommonHelper
{
    public class CommonLog
    {
        public static void LogInfo(string info)
        {
            Console.WriteLine(info);
        }
        public static void LogWarning(string info)
        {
            Console.WriteLine(info);
        }

        public static void LogError(Exception ex ,string flag=null)
        {
            System.Reflection.MethodBase methodInfo = new System.Diagnostics.StackFrame(1).GetMethod();
            if(string.IsNullOrEmpty(flag))
            {
                Console.WriteLine($"[{methodInfo.Name}]{ex}");
            }
            else
            {
                Console.WriteLine($"[{flag}][{methodInfo.Name}]{ex}");
            }
        }


        public static void LogError(string error, string flag = null)
        {
            if (string.IsNullOrEmpty(flag))
            {
                Console.WriteLine(error);
            }
            else
            {
                Console.WriteLine($"[{flag}]{error}");
            }
        }

        public static void LogDebug(string debugInfo)
        {
#if LogDubg
            Console.WriteLine(debugInfo);
#endif
        }
    }
}
