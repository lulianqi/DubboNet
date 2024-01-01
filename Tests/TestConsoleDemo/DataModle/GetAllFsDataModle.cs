using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestConsoleDemo.DataModle
{
    internal class getAllFsDataModle
    {
    }

    public class GetAllFsDataModle
    {
        public int code { get; set; }
        public int count { get; set; }
        public Datum[] data { get; set; }
        public string message { get; set; }
        public string requestId { get; set; }
        public bool success { get; set; }
    }

    public class Datum
    {
        public bool available { get; set; }
        public string freeswitchHost { get; set; }
        public string freeswitchHostname { get; set; }
        public int freeswitchId { get; set; }
        public string freeswitchPasswd { get; set; }
        public int freeswitchPort { get; set; }
        public string freeswitchType { get; set; }
        public string publicIp { get; set; }
    }

}
