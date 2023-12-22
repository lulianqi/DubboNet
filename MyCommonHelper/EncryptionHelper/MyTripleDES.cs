using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MyCommonHelper.EncryptionHelper
{
    class MyTripleDES
    {
        /// <summary>
        /// TripleDES加密
        /// </summary>
        /// <param name="PlainText">被加密字节流</param>
        /// <param name="yourKey">密钥（DES支持64位）</param>
        /// <param name="yourIV">初始化向量EBC模式下不需要该参数可以传null</param>
        /// <param name="yourCipherMode">加密模式</param>
        /// <param name="yourPaddingMode">填充模式</param>
        /// <returns>密文（可能会抛出异常）</returns>
        public static byte[] Encrypt(byte[] PlainText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode)
        {
            return MyEncryption.SymmetricEncrypt(PlainText, yourKey, yourIV, yourCipherMode, yourPaddingMode, SymmetricAlgorithmType.TripleDES);
        }

        /// <summary>
        /// TripleDES解密
        /// </summary>
        /// <param name="CypherText">密文</param>
        /// <param name="yourKey">密钥（DES支持64位）</param>
        /// <param name="yourIV">初始化向量EBC模式下不需要该参数可以传null</param>
        /// <param name="yourCipherMode">加密模式</param>
        /// <param name="yourPaddingMode">填充模式</param>
        /// <returns>明文（可能会抛出异常）</returns>
        public static byte[] Decrypt(byte[] CypherText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode)
        {
            return MyEncryption.SymmetricDecrypt(CypherText, yourKey, yourIV, yourCipherMode, yourPaddingMode, SymmetricAlgorithmType.TripleDES);
        }
    }
}
