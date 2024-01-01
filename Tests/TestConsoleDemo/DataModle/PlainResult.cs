using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestConsoleDemo.DataModle
{
    public class PlainResult
    {
        public int code { get; set; }
        public Object data { get; set; }
        public string message { get; set; }
        public string requestId { get; set; }
        public bool success { get; set; }
    }

    public class PlainResult<T>
    {
        public int code { get; set; }
        public T data { get; set; }
        public string message { get; set; }
        public string requestId { get; set; }
        public bool success { get; set; }
    }

}
