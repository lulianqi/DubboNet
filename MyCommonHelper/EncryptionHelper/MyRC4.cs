using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyCommonHelper.EncryptionHelper
{
    public class MyRC4
    {
        /// <summary>
        /// 密钥初始化
        /// </summary>
        /// <param name="key">加密密钥不能超过265字节</param>
        /// <returns>返回不重复的256字节新密钥</returns>
        private static byte[] Rc4Init(byte[] key)
        {
            if (key.Length > 256 || key.Length==0)
            {
                throw new Exception("error leng of RC4 key"); 
            }
            byte[] k = new byte[256];   // new key   leng 256
            byte[] s = new byte[256];   //{0,1,2,3..........255}    return key
            int j = 0;
            for (int i = 0; i < 256;i++ )
            {
                s[i] = (byte)i;
                k[i] = key[i % key.Length];
            }
            byte tempByte;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + k[i]) % 256;
                tempByte = s[i];
                s[i] = s[j];
                s[j] = tempByte;
            }
            return s;
        }

        /// <summary>
        /// RC4加密（加解密实际是同一个方法）
        /// </summary>
        /// <param name="data">加密数据</param>
        /// <param name="key">加密密钥不能超过265字节</param>
        /// <returns>密文</returns>
        public static byte[] Encrypt(byte[] data, byte[] key)
        {
            byte[] retuenByte = new byte[data.Length];
            byte[] newKey;
            byte tempByte;
            int indexI = 0;
            int indexS = 0;
            int indexD = 0;
            newKey = Rc4Init(key);
            for (int i = 0; i < data.Length;i++ )
            {
                indexI = (indexI + 1) % 256;
                indexS = (indexS + newKey[indexI]) % 256;
                tempByte = newKey[indexI];
                newKey[indexI] = newKey[indexS];
                newKey[indexS] = tempByte;

                indexD = (newKey[indexI] + newKey[indexS]) % 256;
                retuenByte[i] = (byte)(data[i] ^ newKey[indexD]);
            }
            return retuenByte;
        }

        public static byte[] Encrypt(string data, string key, Encoding encoding)
        {
            return Encrypt(encoding.GetBytes(data), encoding.GetBytes(key));
        }

        public static byte[] Decrypt(byte[] data, byte[] key)
        {
            return Encrypt(data, key);
        }

        public static byte[] Decrypt(byte[] data, string key, Encoding encoding)
        {
            return Encrypt(data, encoding.GetBytes(key));
        }
    }
}
