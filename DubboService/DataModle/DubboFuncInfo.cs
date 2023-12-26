using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.DubboService.DataModle
{
    public class DubboFuncInfo
    {
        public string ServiceName { get; set; }
        public string FuncName { get; set; }
        public List<string> FuncInputParameterDefinition { get; set; }
        public string FuncOutputResultDefinition { get; set; }
        public string FuncExample { get; set; }
        public string UserRemark { get; set; }

        public DubboFuncInfo()
        {
            FuncInputParameterDefinition = new List<string>();
        }
    }
}
