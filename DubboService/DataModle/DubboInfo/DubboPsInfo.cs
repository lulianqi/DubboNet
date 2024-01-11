using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle.DubboInfo
{
    /*   dubbo>ps -l 20882
    /172.16.189.88:40992 -> /172.16.69.118:20882
    /172.16.67.106:55998 -> /172.16.69.118:20882
    /172.16.189.61:41458 -> /172.16.69.118:20882
    /172.16.246.67:59054 -> /172.16.69.118:20882
    /172.16.193.136:59222 -> /172.16.69.118:20882
    */
    public class DubboPsInfo : DubboInfoBase
    {
        public List<KeyValuePair<IPEndPoint, IPEndPoint>> Lines { get; set; } = new List<KeyValuePair<IPEndPoint, IPEndPoint>>();

        public static DubboPsInfo GetDubboPsInfo(string source)
        {
            const string IP_START = "/";
            const string IP_SPIT = " -> /";
            const string IP_NEWLINE = "\r\n";

            DubboPsInfo dubboPsInfo = new DubboPsInfo();
            if (string.IsNullOrEmpty(source)) return null;
            string[] sourceLineArr = source.Split(IP_NEWLINE, StringSplitOptions.RemoveEmptyEntries);
            foreach (string oneLine in sourceLineArr)
            {
                if (oneLine.StartsWith(IP_START))
                {
                    int tempEnd = oneLine.IndexOf(IP_SPIT);
                    string epFrom = oneLine.Substring(1, tempEnd - 1);
                    string epTo = oneLine.Substring(tempEnd + IP_SPIT.Length);
                    IPEndPoint iPEndPointFrom, iPEndPointTo = null;
                    if (IPEndPoint.TryParse(epFrom, out iPEndPointFrom) && IPEndPoint.TryParse(epTo, out iPEndPointTo))
                    {
                        dubboPsInfo.Lines.Add(new KeyValuePair<IPEndPoint, IPEndPoint>(iPEndPointFrom, iPEndPointTo));
                    }
                }
            }
            return dubboPsInfo;
        }
    }

}
