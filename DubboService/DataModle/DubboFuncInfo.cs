using MyCommonHelper;
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

        /// <summary>
        /// 将ls -l命令返回值结果解析为DubboFuncInfo字典（内部使用）
        /// </summary>
        /// <param name="lslStr"></param>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        public static Dictionary<string, DubboFuncInfo> GetDubboFuncListIntro(string lslStr, string serviceName)
        {
            const string _funcListSplit = "\r\n";
            Dictionary<string, DubboFuncInfo> resultDc = new Dictionary<string, DubboFuncInfo>();
            if (!string.IsNullOrEmpty(lslStr) && lslStr.Contains(_funcListSplit))
            {
                string[] tempLines = lslStr.Split(_funcListSplit, StringSplitOptions.RemoveEmptyEntries);
                if (tempLines.Length > 1)
                {
                    for (int i = 1; i < tempLines.Length; i++)
                    {
                        string tempNowLine = tempLines[i];
                        string tempFuncOut;
                        string tempFuncName;
                        string tempFuncIn;
                        if (tempLines[i].StartsWith("\t"))
                        {
                            tempNowLine = tempNowLine.Remove(0, 1);
                            int tempIndex = tempNowLine.IndexOf(" ");
                            if (tempIndex < 0)
                            {
                                MyLogger.LogWarning($"[GetDubboFuncListIntro]:data error can not find FuncOut in {tempLines[i]} ");
                                continue;
                            }
                            tempFuncOut = tempNowLine.Substring(0, tempIndex).Trim();
                            int tempEndIndex = tempNowLine.IndexOf("(", tempIndex + 1);
                            if (tempEndIndex < 0)
                            {
                                MyLogger.LogWarning($"[GetDubboFuncListIntro]:data error can not find FuncName in {tempLines[i]} data error ");
                                continue;
                            }
                            tempFuncName = tempNowLine.Substring(tempIndex + 1, tempEndIndex - tempIndex - 1).Trim();
                            tempIndex = tempEndIndex;
                            tempEndIndex = tempNowLine.IndexOf(")", tempIndex + 1);
                            if (tempEndIndex < 0)
                            {
                                MyLogger.LogWarning($"[GetDubboFuncListIntro]:data error can not find FuncIn in {tempLines[i]} data error ");
                                continue;
                            }
                            tempFuncIn = tempNowLine.Substring(tempIndex + 1, tempEndIndex - tempIndex - 1).Trim();
                            string nowDcKey = $"{serviceName}.{tempFuncName}";
                            if (!resultDc.TryAdd(nowDcKey, new DubboFuncInfo()
                            {
                                ServiceName = serviceName,
                                FuncName = tempFuncName,
                                FuncInputParameterDefinition = new List<string>() { tempFuncIn },
                                FuncOutputResultDefinition = tempFuncOut
                            }))
                            {
                                if (resultDc.ContainsKey(nowDcKey))
                                {
                                    resultDc[nowDcKey].FuncInputParameterDefinition.Add(tempFuncIn);
                                }
                                else
                                {
                                    MyLogger.LogWarning($"[GetDubboFuncListIntro]:data error when TryAdd in {tempLines[i]} data error ");
                                    continue;
                                }
                            }
                        }
                    }
                }
            }
            return resultDc;
        }
    }
}
