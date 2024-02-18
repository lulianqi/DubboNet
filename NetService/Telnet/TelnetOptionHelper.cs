#define INTEST

using MyCommonHelper;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetService.Telnet
{
    public static class TelnetOptionHelper
    {
        #region telnet的数据定义
        /// <summary>
        /// 表示希望开始使用或者确认所使用的是指定的选项。
        /// </summary>
        const byte WILL = 251;
        /// <summary>
        /// 表示拒绝使用或者继续使用指定的选项。
        /// </summary>
        const byte WONT = 252;
        /// <summary>        
        /// 表示一方要求另一方使用，或者确认你希望另一方使用指定的选项。
        /// </summary>        
        const byte DO = 253;
        /// <summary>
        /// 表示一方要求另一方停止使用，或者确认你不再希望另一方使用指定的选项。       
        /// </summary>       
        const byte DONT = 254;

        /// <summary>
        /// 空操作
        /// </summary>
        const byte NOP = 241;

        /// <summary>        
        /// 标志符,代表是一个TELNET 指令        
        /// </summary>        
        const byte IAC = 255;

        /// <summary>
        /// 表示后面所跟的是对需要的选项的子谈判
        /// </summary>
        const byte SB = 250;
        /// <summary>
        /// 子谈判参数的结束
        /// </summary>
        const byte SE = 240;

        //Assigned Number

        /// <summary>
        /// 回显
        /// </summary>
        const byte SHOWBACK = 1;
        /// <summary>
        /// 抑制继续进行
        /// </summary>
        const byte RESTRAIN = 3;
        /// <summary>
        /// 终端类型
        /// </summary>
        const byte TERMINAL = 24;


        //字选项协商

        // some constants
        const byte ESC = 27;
        const byte CR = 13;
        const byte LF = 10;
        const String F1 = "\033OP"; // function key
        const String F2 = "\033OQ";
        const String F3 = "\033OR";
        const String F4 = "\033OS";
        const String F5 = "\033[15~";
        const String F6 = "\033[17~";
        const String F7 = "\033[18~";
        const String F8 = "\033[19~";
        const String F9 = "\033[20~";
        const String F10 = "\033[21~";
        const String F11 = "\033[23~";
        const String F12 = "\033[24~";

        public const string ENDOFLINE = "\r\n"; // CR LF
        #endregion

        private const string infoLogPrefixStr = "-----------------------";
        private const string errorLogPrefixStr = "💔💔💔💔💔💔💔💔💔💔";

        /// <summary>
        /// 打印调试数据，发布时请关闭INTEST，以禁止打印
        /// </summary>
        /// <param name="debugLog"></param>
        public static void ShowDebugLog(string debugLog, string title = null, bool isErrorLog = false)
        {
#if INTEST && DEBUG
            string prefixStr = isErrorLog ? errorLogPrefixStr : infoLogPrefixStr;
            System.Diagnostics.Debug.WriteLine($"{prefixStr}{title??""}[{DateTime.Now.ToString("HH:mm:ss fff")}]{prefixStr}");
            System.Diagnostics.Debug.WriteLine(debugLog);
#endif
        }

        /// <summary>
        /// 打印调试数据，发布时请关闭INTEST，以禁止打印
        /// </summary>
        /// <param name="debugLog"></param>
        /// <param name="hexaDecimal"></param>
        public static void ShowDebugLog(byte[] debugLog, string title = null, HexaDecimal hexaDecimal = HexaDecimal.hex16, bool isErrorLog = false)
        {
#if INTEST && DEBUG
            string prefixStr = isErrorLog ? errorLogPrefixStr : infoLogPrefixStr;
            System.Diagnostics.Debug.WriteLine($"{prefixStr}{title ?? ""}[{DateTime.Now.ToString("HH:mm:ss fff")}]{prefixStr}");
            System.Diagnostics.Debug.WriteLine($"byte[] leng is : {debugLog.Length}");
            System.Diagnostics.Debug.WriteLine(MyBytes.ByteToHexString(debugLog, hexaDecimal, ShowHexMode.space));
            //System.Diagnostics.Debug.WriteLine(Encoding.ASCII.GetString(debugLog));
            System.Diagnostics.Debug.WriteLine(Encoding.UTF8.GetString(debugLog));
#endif
        }

        /// <summary>
        /// 生成协商答复（仅生成，不发送）
        /// </summary>
        /// <param name="optionBytes">协商</param>
        /// <param name="ReportMes">反馈解析中的错误或警告（默认值为null会自动生成错误处理Action）</param>
        /// <returns>答复（无法答复或错误返回null）</returns>
        public static byte[] GetResponseOption(byte[] optionBytes ,Action<object,TelnetMessageType> ReportMes=null)
        {
            if(ReportMes==null)
            {
                ReportMes = (mes, type) => {
                    if (type == TelnetMessageType.Error)
                    {
                        ShowDebugLog(mes.ToString(), "GetResponseOption",true);
                        throw new Exception(mes.ToString());
                    }
                    else
                    {
                        ShowDebugLog(mes.ToString(), "GetResponseOption");
                    }
                };
            }

            byte[] responseOption = new byte[3];
            responseOption[0] = IAC;
            //协商选项命令为3字节，附加选项超过3个             
            if (optionBytes.Length < 3)
            {
                ReportMes(string.Format("error option by errer length with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Error);
                return null;
            }
            if (optionBytes[0] == IAC)
            {
                switch (optionBytes[1])
                {
                    //WILL： 发送方本身将激活( e n a b l e )选项
                    case WILL:
                        if (optionBytes[2] == SHOWBACK || optionBytes[2] == RESTRAIN)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = DO;
                        }
                        else if (optionBytes[2] == TERMINAL)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        else
                        {
                            ReportMes(string.Format("unknow Assigned Number with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        break;
                    //DO ：发送方想叫接收端激活选项。
                    case DO:
                        if (optionBytes[2] == SHOWBACK || optionBytes[2] == RESTRAIN)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WILL;
                        }
                        else if (optionBytes[2] == TERMINAL)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        else
                        {
                            ReportMes(string.Format("unknow Assigned Number with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        break;
                    //WONT ：发送方本身想禁止选项。
                    case WONT:
                        if (optionBytes[2] == SHOWBACK || optionBytes[2] == RESTRAIN)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = DONT;
                        }
                        else if (optionBytes[2] == TERMINAL)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = DONT;
                        }
                        else
                        {
                            ReportMes(string.Format("unknow Assigned Number with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        break;
                    //DON’T：发送方想让接收端去禁止选项。
                    case DONT:
                        if (optionBytes[2] == SHOWBACK || optionBytes[2] == RESTRAIN)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        else if (optionBytes[2] == TERMINAL)
                        {
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        else
                        {
                            ReportMes(string.Format("unknow Assigned Number with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                            responseOption[2] = optionBytes[2];
                            responseOption[1] = WONT;
                        }
                        break;
                    //子选项协商 (暂不处理)
                    case SB:
                        ReportMes(string.Format("unsuport SB/SE option with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                        return null;
                    default:
                        ReportMes(string.Format("unknow option with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                        responseOption[2] = optionBytes[2];
                        responseOption[1] = WONT;
                        break;
                }

            }
            else
            {
                ReportMes(string.Format("error option by no IAC with :{0}", MyBytes.ByteToHexString(optionBytes, HexaDecimal.hex16, ShowHexMode.space)), TelnetMessageType.Warning);
                return null;
            }
            return responseOption;
        }

        /// <summary>        
        /// 处理原始报文，返回可显示数据，并提取控制命令（telnet协商数据）   
        ///</summary>       
        ///<param name="yourRawBytes">原始数据</param> 
        ///<param name="optionsList">解析得到的协商数据</param>
        /// <returns>可显示数据</returns>     
        public static byte[] DealRawBytes(byte[] yourRawBytes ,out ArrayList optionsList)
        {

            List<byte> showByteList = new List<byte>();
            optionsList = new ArrayList();

            for (int i = 0; i < yourRawBytes.Length; i++)
            {
                if (yourRawBytes[i] == IAC)
                {
                    if ((i + 1) >= yourRawBytes.Length)
                    {
                        throw new Exception("find error IAC data , no data after IAC");
                    }
                    byte nextByte = yourRawBytes[i + 1];
                    if (nextByte == DO || nextByte == DONT || nextByte == WILL || nextByte == WONT)
                    {
                        if ((i + 2) < yourRawBytes.Length)
                        {
                            byte[] tempOptionCmd = new byte[] { yourRawBytes[i], yourRawBytes[i + 1], yourRawBytes[i + 2] };
                            optionsList.Add(tempOptionCmd);
                        }
                        else
                        {
                            throw new Exception("find error IAC data ,it is less the 3 byte");
                        }
                        i = i + 2;
                    }
                    //如果IAC后面又跟了个IAC (255)  
                    else if (nextByte == IAC)
                    {
                        showByteList.Add(yourRawBytes[i]);
                        i = i + 1;
                    }
                    //如果IAC后面跟的是SB(250)子选项协商       
                    else if (nextByte == SB)
                    {
                        //SE 为子选项结束
                        int sbEndIndex = yourRawBytes.MyIndexOf(SE, i + 1);
                        if (sbEndIndex > 0)
                        {
                            byte[] tempSBOptionCmd = new byte[sbEndIndex - i];
                            Array.Copy(yourRawBytes, i, tempSBOptionCmd, 0, sbEndIndex - i);
                            optionsList.Add(tempSBOptionCmd);
                        }
                        else
                        {
                            throw new Exception("find error SB data ,can not find SE");
                        }
                    }
                    else
                    {
                        throw new Exception("find error IAC data ,the next byte is error");
                    }
                }
                else
                {
                    showByteList.Add(yourRawBytes[i]);
                }
            }
            return showByteList.ToArray();
        }
        
        public static byte[] NopOPerationBytes
        {
            get { return new byte[] { IAC, NOP, IAC}; }
        }
    }
}
