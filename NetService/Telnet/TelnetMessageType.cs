using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NetService.Telnet
{
    public enum TelnetMessageType
    {
        Error,
        ShowData,
        Message,
        StateChange,
        Warning
    }
}
