using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    /*
    dubbo>ls
    PROVIDER:
    com.xxxxx.service.api.WorkOrderProductRemoteService
    com.xxxxx.service.api.UserAccountRemoteService
    com.xxxxx.service.api.PhoneHomeLocationRemoteService
    CONSUMER:
    com.xxx.decision.api.record.MiniAppRecordReadRemoteServicecom.xxx.decision.api.dialoguebot.globalstrategy.TBotGlobalStrategyKnowledgeReadRemote
    //or
    dubbo>ls
    com.xxxxx.pay.api.PayRemoteService
    */
    public class DubboLsInfo
    {
        public List<string> Providers { get; set; } = new List<string>();
        public List<string> Consumers { get; set; } = new List<string>();

        public static DubboLsInfo GetDubboLsInfo(string source)
        {
            const string LS_NEWLINE = "\r\n";
            DubboLsInfo dubboLsInfo = new DubboLsInfo();
            if (string.IsNullOrEmpty(source)) return null;
            string[] sourceLineArr = source.Split(LS_NEWLINE,StringSplitOptions.RemoveEmptyEntries);
            if (sourceLineArr.Length > 0)
            {
                List<string> activeList = dubboLsInfo.Providers;
                for (int i = 0; i < sourceLineArr.Length; i++)
                {
                    if (sourceLineArr[i].StartsWith("PROVIDER:"))
                    {
                        activeList = dubboLsInfo.Providers;
                        activeList.Clear();
                    }
                    else if (sourceLineArr[i].StartsWith("CONSUMER:"))
                    {
                        activeList = dubboLsInfo.Consumers;
                        activeList.Clear();
                    }
                    else
                    {
                        activeList?.Add(sourceLineArr[i]);
                    }
                }
            }
            return dubboLsInfo;
        }

    }

}
