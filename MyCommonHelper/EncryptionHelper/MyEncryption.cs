using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

namespace MyCommonHelper.EncryptionHelper
{
    public enum SymmetricAlgorithmType
    {
        DES,
        AES,
        TripleDES
    }

    public class MyEncryption
    {
      
        /// <summary>
        /// MD5计算
        /// </summary>
        /// <param name="data">加密数据</param>
        /// <returns>加密结果</returns>
        public static string CreateMD5Key(string data)
        {
            byte[] result = Encoding.UTF8.GetBytes(data);
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] output = md5.ComputeHash(result);
            return BitConverter.ToString(output).Replace("-", "");
        }


        



        /// <summary>
        /// 加密器
        /// </summary>
        /// <param name="PlainText">被加密内容</param>
        /// <param name="key">加密器</param>
        /// <param name="errorMes">是否返回有错误，null为无错误</param>
        /// <returns>加密后的字节流</returns>
        public static byte[] SymmetricEncrypt(byte[] PlainText, SymmetricAlgorithm key)
        {
            try
            {
                MemoryStream ms = new MemoryStream();
                CryptoStream encStream = new CryptoStream(ms, key.CreateEncryptor(), CryptoStreamMode.Write);

                encStream.Write(PlainText, 0, PlainText.Length);
                encStream.Close();
                byte[] buffer = ms.ToArray();
                ms.Close();
                return buffer;
            }
            catch (Exception ex)
            {
                //return Encoding.Default.GetBytes("error");
                throw ex;
            }
        }

        public static byte[] SymmetricDecrypt(byte[] CypherText, SymmetricAlgorithm key)
        {
            try
            {
                // Create a memory stream to the passed buffer.
                MemoryStream ms = new MemoryStream(CypherText);

                // Create a CryptoStream using the memory stream and the 
                // CSP DES key. 
                CryptoStream encStream = new CryptoStream(ms, key.CreateDecryptor(), CryptoStreamMode.Read);

                // Create a StreamReader for reading the stream.
                //StreamReader sr = new StreamReader(encStream);

                // Read the stream as a string.
                
                byte[] buffer = new byte[CypherText.Length];
                int length = encStream.Read(buffer, 0, buffer.Length);
                if (buffer.Length > length)
                {
                    Array.Resize(ref buffer, length);
                }
                //byte[] buffer = ms.ToArray();

                // Close the streams.

                //sr.Close();
                encStream.Close();
                ms.Close();

                return buffer;
            }
            catch (Exception ex)
            {
                //return Encoding.Default.GetBytes("error");
                throw ex;
            }
        }


        public static byte[] SymmetricEncrypt(byte[] PlainText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode,SymmetricAlgorithmType yourType)
        {
            byte[] outBytes;
            SymmetricAlgorithm key;
            switch (yourType)
            {
                case SymmetricAlgorithmType.DES:
                    key = new DESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.TripleDES:
                    key = new TripleDESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.AES:
                    key = new AesCryptoServiceProvider();
                    break;
                default:
                    throw new Exception("not support this encrypt type");
            }
            key.Key = yourKey;
            key.Mode = yourCipherMode;
            key.Padding = yourPaddingMode;
            if (key.Mode != CipherMode.ECB)
            {
                key.IV = yourIV;
            }
            outBytes = SymmetricEncrypt(PlainText, key);
            key.Clear();
            return outBytes;
        }

        public static byte[] SymmetricDecrypt(byte[] CypherText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode, SymmetricAlgorithmType yourType)
        {
            byte[] outBytes;
            SymmetricAlgorithm key;
            switch (yourType)
            {
                case SymmetricAlgorithmType.DES:
                    key = new DESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.TripleDES:
                    key = new TripleDESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.AES:
                    key = new AesCryptoServiceProvider();
                    break;
                default:
                    throw new Exception("not support this encrypt type");
            }
            key.Key = yourKey;
            key.Mode = yourCipherMode;
            key.Padding = yourPaddingMode;
            if (key.Mode != CipherMode.ECB)
            {
                key.IV = yourIV;
            }
            outBytes = SymmetricDecrypt(CypherText, key);
            key.Clear();
            return outBytes;
        }

        public static byte[] GenerateKey(SymmetricAlgorithmType yourType,int keySize)
        {
            byte[] outBytes;
            SymmetricAlgorithm key;
            switch (yourType)
            {
                case SymmetricAlgorithmType.DES:
                    key = new DESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.TripleDES:
                    key = new TripleDESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.AES:
                    key = new AesCryptoServiceProvider();
                    break;
                default:
                    throw new Exception("not support this encrypt type");
            }
            if(keySize>0)
            {
                key.KeySize = keySize;
            }
            key.GenerateKey();
            outBytes = key.Key;
            key.Clear();
            return outBytes;
        }

        public static byte[] GenerateIV(SymmetricAlgorithmType yourType)
        {
            byte[] outBytes;
            SymmetricAlgorithm key;
            switch (yourType)
            {
                case SymmetricAlgorithmType.DES:
                    key = new DESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.TripleDES:
                    key = new TripleDESCryptoServiceProvider();
                    break;
                case SymmetricAlgorithmType.AES:
                    key = new AesCryptoServiceProvider();
                    break;
                default:
                    throw new Exception("not support this encrypt type");
            }
            outBytes = key.IV;
            key.Clear();
            return outBytes;
        }


        public static CipherMode[] GetCipherModes()
        {
            return (CipherMode[])(Enum.GetValues(typeof(CipherMode)));
        }

        public static PaddingMode[] GetPaddingModes()
        {
            return (PaddingMode[])(Enum.GetValues(typeof(PaddingMode)));
        }

    }
}
