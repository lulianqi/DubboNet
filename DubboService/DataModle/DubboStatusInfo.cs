using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    /* source data
    +------------+--------+--------------------------------------------------------+
    | resource   | status | message                                                |
    +------------+--------+--------------------------------------------------------+
    | threadpool | OK     | Pool status:OK, max:200, core:200, largest:15, active:1, task:15, service port: 20890 |
    | datasource | OK     | dataSourcejdbc:mysql://pc-bp1it3ikd1-------5.7.28-log) |
    | load       | OK     | load:3.48,cpu:8                                        |
    | memory     | OK     | max:4915M,total:4915M,used:2316M,free:2599M            |
    | registry   | OK     | zk01.xxxxx.cn:2181(connected)                          |
    | server     | OK     | /172.16.248.12:20890(clients:4)                        |
    | summary    | OK     |                                                        |
    +------------+--------+--------------------------------------------------------+
    */

    public class DubboStatusInfo
    {
        public abstract class ResourceStatus
        {
            private string _message = null;
            public string Status { get; set; }
            public string Message { get { return _message; } set { _message = value;  InitStatusResource(); } }

            public abstract bool InitStatusResource();

            protected static Dictionary<string, string> GetKeyValuePairs(string message)
            {
                const string MES_SPLIT = ",";
                const string MESVAL_SPLIT = ":";
                Dictionary<string, string> keyValuePairs = new Dictionary<string, string>();
                if (string.IsNullOrEmpty(message))
                {
                    throw new ArgumentException($"“{nameof(message)}”can not be null 。", nameof(message));
                }
                string[] parts = message.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts) 
                { 
                }
            }
        }

        public class ThreadPoolInfo:ResourceStatus
        {
            public new string Status {  get; set; }
            public int Max { get; set; }
            public int Core { get; set; }
            public int Largest { get; set; }
            public int Active { get; set; }
            public int Task { get; set; }
            public int Port { get; set; }

            public override bool InitStatusResource()
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return false;
                }
                return true;
            }
        }

        public class LoadInfo
        {

        }

        public class MemoryInfo
        {

        }

        public class RegistryInfo
        {

        }

        public class ServerInfo
        {

        }

        public Dictionary<string, Tuple<string, string>> SourceData { get; private set; } = new Dictionary<string, Tuple<string, string>>();

        public ThreadPoolInfo ThreadPool { get; set; }
        public string Datasource { get; set; }
        public LoadInfo Load { get; set; }
        public MemoryInfo Memory { get; set; }
        public RegistryInfo Registry { get; set; }
        public ServerInfo Server { get; set; }
        public string Summary { get; set; }

        public static DubboStatusInfo GetDubboStatusInfo(string source)
        {
            const string DATA_SPLIT = "|";
            const string DATA_NEWLINE = "\r\n";
            DubboStatusInfo dubboStatusInfo = new DubboStatusInfo();
            string[] sourceLineArr = source.Split(DATA_NEWLINE, StringSplitOptions.RemoveEmptyEntries);
            foreach (string oneLine in sourceLineArr)
            {
                if (oneLine.StartsWith(DATA_NEWLINE))
                {
                    string nowKey, nowStatus, nowMessage = null;
                    int tempStartIndex = 1;
                    int tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex > 0)
                    {
                        CommonLog.LogDebug($"GetDubboStatusInfo key from {oneLine} fail");
                        continue;
                    }
                    nowKey = oneLine.Substring(tempStartIndex, tempEndIndex - tempStartIndex).Trim();
                    tempStartIndex = tempEndIndex + 1;
                    tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex > 0)
                    {
                        CommonLog.LogDebug($"GetDubboStatusInfo status from {oneLine} fail");
                        continue;
                    }
                    nowStatus = oneLine.Substring(tempStartIndex, tempEndIndex - tempStartIndex).Trim();
                    tempStartIndex = tempEndIndex + 1;
                    tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex > 0)
                    {
                        CommonLog.LogDebug($"GetDubboStatusInfo message from {oneLine} fail");
                        continue;
                    }
                    nowMessage = oneLine.Substring(tempStartIndex, tempEndIndex - tempStartIndex).Trim();
                    dubboStatusInfo.SourceData.TryAdd(nowKey, new Tuple<string, string>(nowStatus, nowMessage));
                }
            }
            foreach (var statusInfo in dubboStatusInfo.SourceData)
            {
                switch (statusInfo.Key)
                {
                    case "resource":
                        break;
                    case "threadpool":
                        break;
                    case "datasource":
                        break;
                    case "load":
                        break;
                    case "memory":
                        break;
                    case "registry":
                        break;
                    case "server":
                        break;
                    case "summary":
                        break;
                    default:
                        break;
                }
            }
            return dubboStatusInfo;
        }
    }

}