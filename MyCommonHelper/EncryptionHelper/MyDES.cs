using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MyCommonHelper.EncryptionHelper
{
    //DES 块长度64 密钥长度64
    //3DES 块长度 64 密钥长度 128/192
    //AES  块长度 128 密钥长度 128/192/256
    //IV长度与块大小维持一致
    //EBC模式不需要使用IV
    //若填充方式微none不填充，需要保证数据长度为块的整数倍


    /// <summary>
    /// DES加密
    /// </summary>
    public class MyDES
    {
        /// <summary>
        /// DES加密
        /// </summary>
        /// <param name="PlainText">被加密字节流</param>
        /// <param name="yourKey">密钥</param>
        /// <param name="yourIV">初始化向量EBC模式下不需要该参数可以传null</param>
        /// <param name="yourCipherMode">加密模式</param>
        /// <param name="yourPaddingMode">填充模式</param>
        /// <returns>密文（可能会抛出异常）</returns>
        public static byte[] Encrypt(byte[] PlainText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode)
        {
            return MyEncryption.SymmetricEncrypt(PlainText, yourKey, yourIV, yourCipherMode, yourPaddingMode, SymmetricAlgorithmType.DES);
        }

        /// <summary>
        /// DES解密
        /// </summary>
        /// <param name="CypherText">密文</param>
        /// <param name="yourKey">密钥（DES支持64位）</param>
        /// <param name="yourIV">初始化向量EBC模式下不需要该参数可以传null</param>
        /// <param name="yourCipherMode">加密模式</param>
        /// <param name="yourPaddingMode">填充模式</param>
        /// <returns>明文（可能会抛出异常）</returns>
        public static byte[] Decrypt(byte[] CypherText, byte[] yourKey, byte[] yourIV, CipherMode yourCipherMode, PaddingMode yourPaddingMode)
        {
            return MyEncryption.SymmetricDecrypt(CypherText, yourKey, yourIV, yourCipherMode, yourPaddingMode, SymmetricAlgorithmType.DES);
        }

       
    }
}
