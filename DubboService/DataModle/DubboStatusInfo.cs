using MyCommonHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        /// <summary>
        /// DubboStatusInfo 原数据基类
        /// </summary>
        public abstract class ResourceStatus
        {
            private string _message = null;
            public string Status { get; set; }
            public string Message { get { return _message; } set { _message = value;  InitStatusResource(); } }

            /// <summary>
            /// 根据Message初始化结构化数据，不用单独调用该方法，改方法会在Message的set中自动触发（需要定制化实现）
            /// </summary>
            /// <returns></returns>
            protected abstract bool InitStatusResource();

            /// <summary>
            /// 提供一个工具方法用于将message数据转换为Dictionary<string, string>（方便派生类实现InitStatusResource时初步处理数据）
            /// </summary>
            /// <param name="message"></param>
            /// <returns></returns>
            /// <exception cref="ArgumentException"></exception>
            protected Dictionary<string, string> GetKeyValuePairs(string message = null)
            {
                const string MES_SPLIT = ",";
                const string MESVAL_SPLIT = ":";
                Dictionary<string, string> keyValuePairs = new Dictionary<string, string>();
                if(message == null)
                {
                    message = _message;
                }
                if (string.IsNullOrEmpty(message))
                {
                    throw new ArgumentException($"“{nameof(message)}”can not be null 。", nameof(message));
                }
                string[] parts = message.Split(MES_SPLIT, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string part in parts) 
                {
                    var keyValue = part.Trim().Split(MESVAL_SPLIT, 2,StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (keyValue.Length != 2)
                    {
                        MyLogger.LogWarning("Each part of the status text should contain a single ':' character.");
                        continue;
                    }
                    keyValuePairs.TryAdd(keyValue[0], keyValue[1]);
                }
                return keyValuePairs;
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

            protected override bool InitStatusResource()
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return false;
                }
                Dictionary<string, string> kps = GetKeyValuePairs();
                foreach(KeyValuePair<string, string> kv in kps)
                {
                    try
                    {
                        switch (kv.Key.ToLower())
                        {
                            case "pool status":
                                Status = kv.Value;
                                break;
                            case "max":
                                Max = int.Parse(kv.Value);
                                break;
                            case "core":
                                Core = int.Parse(kv.Value);
                                break;
                            case "largest":
                                Largest = int.Parse(kv.Value);
                                break;
                            case "active":
                                Active = int.Parse(kv.Value);
                                break;
                            case "task":
                                Task = int.Parse(kv.Value);
                                break;
                            case "service port":
                                Port = int.Parse(kv.Value);
                                break;
                            default:
                                MyLogger.LogWarning($"Unknown key '{kv.Value}' found in status text.");
                                break;
                        }
                    }
                    catch(Exception ex)
                    {
                        MyLogger.LogError("ThreadPoolInfo InitStatusResource error", ex);
                        return false;
                    }
                }
                return true;
            }
        }

        public class DataSourceInfo:ResourceStatus
        {
            public Dictionary<string, string> DataSources { get; set; }

            protected override bool InitStatusResource()
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return false;
                }
                DataSources = GetKeyValuePairs();
                return true;
            }
        }

        public class LoadInfo : ResourceStatus
        {
            public Double Load { get; set; }
            public int Cpu { get; set; }

            protected override bool InitStatusResource()
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return false;
                }
                Dictionary<string, string> kps = GetKeyValuePairs();
                foreach (KeyValuePair<string, string> kv in kps)
                {
                    try
                    {
                        switch (kv.Key.ToLower())
                        {
                            case "load":
                                Load = Double.Parse(kv.Value);
                                break;
                            case "cpu":
                                Cpu = int.Parse(kv.Value);
                                break;
                            default:
                                MyLogger.LogWarning($"Unknown key '{kv.Value}' found in status text.");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        MyLogger.LogError("LoadInfo InitStatusResource error", ex);
                        return false;
                    }
                }
                return true;
            }
        }

        public class MemoryInfo : ResourceStatus
        {
            public string Max {  get; set; }
            public string Total { get; set; }
            public string Used { get; set; }
            public string Free { get; set; }

            protected override bool InitStatusResource()
            {
                if (string.IsNullOrEmpty(Message))
                {
                    return false;
                }
                Dictionary<string, string> kps = GetKeyValuePairs();
                foreach (KeyValuePair<string, string> kv in kps)
                {
                    switch (kv.Key.ToLower())
                    {
                        case "max":
                            Max = kv.Value;
                            break;
                        case "total":
                            Total = kv.Value;
                            break;
                        case "used":
                            Used = kv.Value;
                            break;
                        case "free":
                            Free = kv.Value;
                            break;
                        default:
                            MyLogger.LogWarning($"Unknown key '{kv.Value}' found in status text.");
                            break;
                    }
                }
                return true;
            }
        }

        public class RegistryInfo : ResourceStatus
        {
            protected override bool InitStatusResource()
            {
                return true;
            }
        }

        public class ServerInfo : ResourceStatus
        {
            protected override bool InitStatusResource()
            {
                return true;
            }
        }

        public class SummaryInfo : ResourceStatus
        {
            protected override bool InitStatusResource()
            {
                return true;
            }
        }

        public Dictionary<string, Tuple<string, string>> SourceData { get; private set; } = new Dictionary<string, Tuple<string, string>>();

        public ThreadPoolInfo ThreadPool { get; set; }
        public DataSourceInfo Datasource { get; set; }
        public LoadInfo Load { get; set; }
        public MemoryInfo Memory { get; set; }
        public RegistryInfo Registry { get; set; }
        public ServerInfo Server { get; set; }
        public SummaryInfo Summary { get; set; }

        public static DubboStatusInfo GetDubboStatusInfo(string source)
        {
            const string DATA_SPLIT = "|";
            const string DATA_NEWLINE = "\r\n";
            DubboStatusInfo dubboStatusInfo = new DubboStatusInfo();
            string[] sourceLineArr = source.Split(DATA_NEWLINE, StringSplitOptions.RemoveEmptyEntries);
            foreach (string oneLine in sourceLineArr)
            {
                if (oneLine.StartsWith(DATA_SPLIT))
                {
                    string nowKey, nowStatus, nowMessage = null;
                    int tempStartIndex = 1;
                    int tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex < 0)
                    {
                        MyLogger.LogWarning($"GetDubboStatusInfo key from {oneLine} fail");
                        continue;
                    }
                    nowKey = oneLine.Substring(tempStartIndex, tempEndIndex - tempStartIndex).Trim();
                    tempStartIndex = tempEndIndex + 1;
                    tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex < 0)
                    {
                        MyLogger.LogWarning($"GetDubboStatusInfo status from {oneLine} fail");
                        continue;
                    }
                    nowStatus = oneLine.Substring(tempStartIndex, tempEndIndex - tempStartIndex).Trim();
                    tempStartIndex = tempEndIndex + 1;
                    tempEndIndex = oneLine.IndexOf(DATA_SPLIT, tempStartIndex);
                    if (tempEndIndex < 0)
                    {
                        MyLogger.LogWarning($"GetDubboStatusInfo message from {oneLine} fail");
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
                        //标题栏
                        break;
                    case "threadpool":
                        dubboStatusInfo.ThreadPool = new ThreadPoolInfo() { Status = statusInfo.Value.Item1, Message= statusInfo.Value.Item2 };
                        break;
                    case "datasource":
                        dubboStatusInfo.Datasource = new DataSourceInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    case "load":
                        dubboStatusInfo.Load = new LoadInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    case "memory":
                        dubboStatusInfo.Memory = new MemoryInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    case "registry":
                        dubboStatusInfo.Registry = new RegistryInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    case "server":
                        dubboStatusInfo.Server = new ServerInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    case "summary":
                        dubboStatusInfo.Summary = new SummaryInfo() { Status = statusInfo.Value.Item1, Message = statusInfo.Value.Item2 };
                        break;
                    default:
                        MyLogger.LogWarning($"Unknown key '{statusInfo.Key}' found in GetDubboStatusInfo text.");
                        break;
                }
            }
            return dubboStatusInfo;
        }
    }

}