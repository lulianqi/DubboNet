using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    public class DubboPsInfo
    {
        public List<KeyValuePair<IPEndPoint, IPEndPoint>> Lines { get; set; } = new List<KeyValuePair<IPEndPoint, IPEndPoint>>();

        public static DubboPsInfo GetDubboPsInfo(string source)
        {
            const string IP_START = "/";
            const string IP_SPIT = " -> /";
            const string IP_NEWLINE = "\r\n";

            DubboPsInfo dubboPsInfo = new DubboPsInfo();
            if (string.IsNullOrEmpty(source)) return null;
            string[] sourceLineArr = source.Split(IP_NEWLINE,StringSplitOptions.RemoveEmptyEntries);
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
