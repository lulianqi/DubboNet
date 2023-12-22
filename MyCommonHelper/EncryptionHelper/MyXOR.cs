using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MyCommonHelper.EncryptionHelper
{
    public class MyXOR
    {
        /// <summary>
        /// i will get the XOR use 2 data
        /// </summary>
        /// <param name="buffer1">data1</param>
        /// <param name="buffer2">data2</param>
        /// <returns></returns>
        public static byte[] GetXor(byte[] buffer1, byte[] buffer2)
        {
            if (buffer1.Length != buffer2.Length)
            {
                return null;
            }
            else
            {
                byte[] myBuffer = new byte[buffer1.Length];
                for (int i = 0; i < buffer1.Length; i++)
                {
                    myBuffer[i] = (byte)(buffer1[i] ^ buffer2[i]);
                }
                return myBuffer;
            }
        }
    }
}
