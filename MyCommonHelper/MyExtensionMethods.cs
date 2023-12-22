using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;


/*******************************************************************************
* Copyright (c) 2015 lijie
* All rights reserved.
* 
* 文件名称: 
* 内容摘要: mycllq@hotmail.com
* 
* 历史记录:
* 日	  期:   201505016           创建人: 李杰 15158155511
* 描    述: 创建
*******************************************************************************/


namespace MyCommonHelper
{
    public static class MyExtensionMethods
    {
        /// <summary>
        /// 返回查找byte[]中的一个出现指定字符的位置
        /// </summary>
        /// <param name="bytes">byte[]</param>
        /// <param name="targetByte">查找目标</param>
        /// <param name="startIndex">开始索引</param>
        /// <param name="leng">最大搜索长度</param>
        /// <returns></returns>
        public static int MyIndexOf(this byte[] bytes, byte targetByte, int startIndex, int leng)
        {
            for (int i = startIndex; i < leng; i++)
            {
                if (bytes[i] == targetByte)
                {
                    return startIndex + i;
                }
            }
            return -1;
        }

        public static int MyIndexOf(this byte[] bytes, byte targetByte)
        {

            return MyIndexOf(bytes, targetByte, 0, bytes.Length);
        }

        public static int MyIndexOf(this byte[] bytes, byte targetByte, int startIndex)
        {
            return MyIndexOf(bytes, targetByte, startIndex, bytes.Length - startIndex);
        }


        /// <summary>
        /// 以xml的数据要求格式化string中的特殊字符（null时返回""）
        /// </summary>
        /// <param name="str">String</param>
        /// <returns>格式化过后的字符串</returns>
        public static string ToXmlValue(this string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return "";
            }
            return System.Web.HttpUtility.HtmlEncode(str);
        }

        /// <summary>
        /// 获取String文本，为"null"时返回NULL
        /// </summary>
        /// <param name="str">String</param>
        /// <returns>String文本</returns>
        public static string MyValue(this string str)
        {
            if (str == null)
            {
                return "NULL";
            }
            else
            {
                return str;
            }
        }

        /// <summary>
        /// 向str中添加信息
        /// </summary>
        /// <param name="str">string</param>
        /// <param name="yourValue">your Value</param>
        /// <returns>添加后的结果</returns>
        public static string MyAddValue(this string str, string yourValue)
        {
            if (str == null)
            {
                return yourValue;
            }
            else if (str == "")
            {
                return str = yourValue;
            }
            else
            {
                return str + ("\r\n" + yourValue);
            }
        }

        /// <summary>
        /// 以指定字符将字符串分割并转换为int(eg: "10-11-12-13")
        /// </summary>
        /// <param name="str">指定字符串</param>
        /// <param name="yourSplitChar">分割字符</param>
        /// <param name="yourIntArray">转换结果</param>
        /// <returns>是否成功（任意一个转换失败都会返回False）</returns>
        public static bool MySplitToIntArray(this string str, char yourSplitChar, out int[] yourIntArray)
        {
            yourIntArray = null;
            if (str == null)
            {
                return false;
            }
            string[] strArray = str.Split(new char[] { yourSplitChar }, StringSplitOptions.None);
            yourIntArray = new int[strArray.Length];
            for (int i = 0; i < strArray.Length; i++)
            {
                if (!int.TryParse(strArray[i], out yourIntArray[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 以指定字符将字符串中末尾int数据提取出来(eg: "testdata-10")
        /// </summary>
        /// <param name="str">指定字符串</param>
        /// <param name="yourSplitChar">分割字符</param>
        /// <param name="yourStr">提取后的前半截字符串</param>
        /// <param name="yourInt">提取后的int</param>
        /// <returns>是否成功 int转换失败后也返回错误</returns>
        public static bool MySplitIntEnd(this string str, char yourSplitChar, out string yourStr, out int yourInt)
        {
            yourStr = null;
            yourInt = 0;
            if (str == null)
            {
                return false;
            }
            if (str.Contains(yourSplitChar))
            {
                int lastSplitCharIndex = str.LastIndexOf(yourSplitChar);
                if (lastSplitCharIndex == str.Length - 1) // 如果使用endwith会产生多余的string对象
                {
                    return false;
                }
                if (int.TryParse(str.Substring(lastSplitCharIndex + 1, str.Length - lastSplitCharIndex - 1), out yourInt))
                {
                    yourStr = str.Substring(0, lastSplitCharIndex);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 去除首尾指定字符串
        /// </summary>
        /// <param name="str">指定字符串</param>
        /// <param name="startValue">首部匹配（如果为null则忽略首部匹配）</param>
        /// <param name="endVaule">尾部匹配（如果为null则忽略尾部匹配）</param>
        /// <returns>返回结果</returns>
        public static string MyTrimStr(this string str, string startValue, string endVaule)
        {
            if (str != null)
            {
                if (startValue != null)
                {
                    if (startValue.Length <= str.Length)
                    {
                        int tempTrimStartIndex = str.IndexOf(startValue);
                        if (tempTrimStartIndex == 0)
                        {
                            str = str.Remove(0, startValue.Length);
                        }
                    }
                }
                if (endVaule != null)
                {
                    if (endVaule.Length <= str.Length)
                    {
                        int tempTrimEndIndex = str.LastIndexOf(endVaule);
                        if (tempTrimEndIndex == str.Length - endVaule.Length)
                        {
                            str = str.Remove(tempTrimEndIndex, endVaule.Length);
                        }
                    }
                }
            }
            return str;
        }


        /// <summary>
        /// 以指定字符串拼合List<string>
        /// </summary>
        /// <param name="lsStr">目标对象</param>
        /// <param name="splitStr">分割字符串</param>
        /// <returns>返回数据</returns>
        public static string MyToString(this IList<string> lsStr, string splitStr)
        {
            string outStr = null;
            if (lsStr != null)
            {
                if (lsStr.Count > 5)
                {
                    StringBuilder SbOutStr = new StringBuilder(lsStr.Count * ((lsStr[0].Length > lsStr[2].Length ? lsStr[0].Length : lsStr[1].Length) + splitStr.Length));
                    foreach (string tempStr in lsStr)
                    {
                        SbOutStr.Append(tempStr);
                        if (splitStr != null)
                        {
                            SbOutStr.Append(splitStr);
                        }
                    }
                    outStr = SbOutStr.ToString();
                }
                else
                {
                    foreach (string tempStr in lsStr)
                    {
                        if (splitStr != null)
                        {
                            outStr += (tempStr + splitStr);
                        }
                        else
                        {
                            outStr += tempStr;
                        }
                    }
                }
            }
            return outStr;
        }

        /// <summary>
        /// 添加键值，若遇到已有key则覆盖
        /// </summary>
        /// <param name="dc">Dictionary</param>
        /// <param name="yourKeyValuePair">KeyValuePair</param>
        public static void MyAdd(this Dictionary<string, string> dc, KeyValuePair<string, string> yourKeyValuePair)
        {
            if (dc.ContainsKey(yourKeyValuePair.Key))
            {
                dc[yourKeyValuePair.Key] = yourKeyValuePair.Value;
            }
            else
            {
                dc.Add(yourKeyValuePair.Key, yourKeyValuePair.Value);
            }
        }

        /// <summary>
        /// 添加键值，若遇到已有key则覆盖
        /// </summary>
        /// <param name="dc">Dictionary</param>
        /// <param name="yourKey">Key</param>
        /// <param name="yourValue">Value</param>
        public static void MyAdd(this Dictionary<string, string> dc, string yourKey, string yourValue)
        {
            if (dc.ContainsKey(yourKey))
            {
                dc[yourKey] = yourValue;
            }
            else
            {
                dc.Add(yourKey, yourValue);
            }
        }

        /// <summary>
        /// 【Dictionary<string, string>】添加键值，若遇到已有key则将Key改名(追加索引)
        /// </summary>
        /// <param name="dc">Dictionary</param>
        /// <param name="yourKey">Key</param>
        /// <param name="yourValue">Value</param>
        public static void MyAddEx(this Dictionary<string, string> dc, string yourKey, string yourValue)
        {
            if (dc.ContainsKey(yourKey))
            {
                int tempSameKeyIndex = 0;
                while (dc.ContainsKey(yourKey + "(" + tempSameKeyIndex + ")"))
                {
                    tempSameKeyIndex++;
                }
                dc.Add(yourKey + "(" + tempSameKeyIndex + ")", yourValue);
            }
            else
            {
                dc.Add(yourKey, yourValue);
            }
        }

        /// <summary>
        /// 【NameValueCollection】添加键值，若遇到已有key则将Key改名(追加索引)
        /// </summary>
        /// <param name="dc">NameValueCollection</param>
        /// <param name="yourKey">Key</param>
        /// <param name="yourValue">Value</param>
        public static void MyAddEx(this NameValueCollection dc, string yourKey, string yourValue)
        {
            if (dc.AllKeys.Contains<string>(yourKey))
            {
                int tempSameKeyIndex = 0;
                while (dc.AllKeys.Contains<string>(yourKey + "(" + tempSameKeyIndex + ")"))
                {
                    tempSameKeyIndex++;
                }
                dc.Add(yourKey + "(" + tempSameKeyIndex + ")", yourValue);
            }
            else
            {
                dc.Add(yourKey, yourValue);
            }
        }

        /// <summary>
        /// 【NameValueCollection】添加键值，检查NameValueCollection是否为null
        /// </summary>
        /// <param name="nvc">NameValueCollection</param>
        /// <param name="yourKey">Key</param>
        /// <param name="yourValue">Value</param>
        public static void myAdd(this NameValueCollection nvc, string yourName, string yourValue)
        {
            if (nvc != null)
            {
                nvc.Add(yourName, yourValue);
            }
        }

        /// <summary>
        ///  转换为{[key:value][key:value].......}
        /// </summary>
        /// <param name="nvc">NameValueCollection</param>
        /// <returns>{[key:value][key:value].......}</returns>
        public static string MyToFormatString(this NameValueCollection nvc)
        {
            if (nvc != null)
            {
                if (nvc.Count > 0)
                {
                    if (nvc.Count < 2)
                    {
                        return string.Format("{{ [{0}:{1}] }}", nvc.Keys[0], nvc[nvc.Keys[0]]);
                    }
                    else
                    {
                        StringBuilder tempStrBld = new StringBuilder("{ ", nvc.Count * 32);
                        foreach (string tempKey in nvc.Keys)
                        {
                            tempStrBld.AppendFormat("[{0}:{1}] ", tempKey, nvc[tempKey]);
                        }
                        tempStrBld.Append("}");
                        return tempStrBld.ToString();
                    }
                }
            }
            return "";
        }

        /// <summary>
        /// 转换为key:value/r/nkey:value.......
        /// </summary>
        /// <param name="nvc">NameValueCollection</param>
        /// <returns>key:value/r/nkey:value.....</returns>
        public static string MyToString(this NameValueCollection nvc)
        {
            if (nvc != null)
            {
                if (nvc.Count > 0)
                {
                    if (nvc.Count < 2)
                    {
                        return string.Format("{0}:{1}\r\n", nvc.Keys[0], nvc[nvc.Keys[0]]);
                    }
                    else
                    {
                        StringBuilder tempStrBld = new StringBuilder(nvc.Count * 32);
                        foreach (string tempKey in nvc.Keys)
                        {
                            tempStrBld.AppendFormat("{0}:{1}\r\n", tempKey, nvc[tempKey]);
                        }
                        return tempStrBld.ToString();
                    }
                }
            }
            return "";
        }


        /// <summary>
        /// 添加键值，若遇到已有则不添加
        /// </summary>
        /// <param name="myArratList">ArratList</param>
        /// <param name="yourIp">IPAddress</param>
        public static void MyAdd(this System.Collections.ArrayList myArratList, System.Net.IPAddress yourIp)
        {
            if (!myArratList.Contains(yourIp))
            {
                myArratList.Add(yourIp);
            }
        }

        /// <summary>
        /// 摘取List数组指定列重新生成数组（超出索引返回null）
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="myArratList"></param>
        /// <param name="yourIndex">指定列</param>
        /// <returns>重新生成的数组</returns>
        public static T[] MyGetAppointArray<T>(this List<T[]> myArratList, int yourIndex)
        {
            if (myArratList != null && yourIndex > -1)
            {
                try
                {
                    int myArLong = myArratList.Count;
                    T[] myTAr = new T[myArLong];
                    for (int i = 0; i < myArLong; i++)
                    {
                        myTAr[i] = myArratList[i][yourIndex];
                    }
                    return myTAr;
                }
                catch
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// 返回对象的深度克隆
        /// </summary>
        /// <param name="dc">目标Dictionary</param>
        /// <returns>对象的深度克隆</returns>
        public static Dictionary<string, ICloneable> MyClone(this Dictionary<string, ICloneable> dc)
        {
            Dictionary<string, ICloneable> cloneDc = new Dictionary<string, ICloneable>();
            foreach (KeyValuePair<string, ICloneable> tempKvp in dc)
            {
                cloneDc.Add(tempKvp.Key, (ICloneable)tempKvp.Value.Clone());
            }
            return cloneDc;
        }

        /// <summary>
        /// 返回对象的深度克隆
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="dc"></param>
        /// <returns>对象的深度克隆</returns>
        public static Dictionary<string, T> CloneEx<T>(this Dictionary<string, T> dc) where T : ICloneable
        {
            Dictionary<string, T> cloneDc = new Dictionary<string, T>();
            foreach (KeyValuePair<string, T> tempKvp in dc)
            {
                cloneDc.Add(tempKvp.Key, (T)tempKvp.Value.Clone());
            }
            return cloneDc;
        }

        /// <summary>
        /// 返回对象的浅度克隆(如果T为引用对象，浅度克隆将只复制其对象的引用)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="souceList"></param>
        /// <returns>对象的浅度克隆</returns>
        public static List<T> LightClone<T>(this List<T> souceList)
        {
            return new List<T>(souceList.ToArray());
        }

    }
}