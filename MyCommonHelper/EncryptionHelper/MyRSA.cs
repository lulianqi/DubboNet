using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MyCommonHelper.EncryptionHelper
{
    public class MyRSA
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="PlainText"></param>
        /// <param name="modulus">n (p*q) [Big Endian]</param>
        /// <param name="publicExponent">e，公共指数 [Big Endian]</param>
        /// <returns></returns>
        public static byte[] Encrypt(byte[] PlainText, byte[] modulus,byte[] publicExponent)
        {
            if(PlainText==null || modulus==null || publicExponent==null)
            {
                new ArgumentNullException("Argument Is Null");
            }
            if(modulus.Length>0 && modulus[0]==0)
            {
                byte[] newModulus=new byte[modulus.Length-1];
                Array.Copy(modulus, 1, newModulus, 0, modulus.Length - 1);
                modulus = newModulus;
            }
            if (publicExponent.Length > 0 && publicExponent[0] == 0)
            {
                byte[] newPublicExponent = new byte[publicExponent.Length - 1];
                Array.Copy(publicExponent, 1, newPublicExponent, 0, publicExponent.Length - 1);
                publicExponent = newPublicExponent;
            }
            RSAParameters tempRSAKeyInfo = new RSAParameters();
            tempRSAKeyInfo.Modulus = modulus;
            tempRSAKeyInfo.Exponent = publicExponent;
            return RSAEncrypt(PlainText, tempRSAKeyInfo, false);
        }

        //only for test
        public static byte[] Encrypt(byte[] PlainText, byte[] PemDerKey )
        {
            
            byte[] modulus = new byte[PemDerKey.Length - 29];
            byte[] publicExponent = new byte[3]{0x01,0x00,0x01};
            Array.Copy(PemDerKey, 24, modulus, 0, modulus.Length);
            return Encrypt(PlainText, modulus, publicExponent);
        }

        public static byte[] RSAEncrypt(byte[] DataToEncrypt, RSAParameters RSAKeyInfo, bool DoOAEPPadding)
        {
            try
            {

                //Create a new instance of RSACryptoServiceProvider.
                RSACryptoServiceProvider RSA = new RSACryptoServiceProvider();

                //Import the RSA Key information. This only needs
                //toinclude the public key information.
                RSA.ImportParameters(RSAKeyInfo);

                //Encrypt the passed byte array and specify OAEP padding.  
                //OAEP padding is only available on Microsoft Windows XP or
                //later.  
                return RSA.Encrypt(DataToEncrypt, DoOAEPPadding);
            }
            //Catch and display a CryptographicException  
            //to the console.
            catch (Exception)
            {
                return null;
            }

        }

        public static bool VerifyPwdData(string dataEncrypted, string publicKey)
        {
            bool isVerify;
            RSAParameters _publicKey = LoadRsaPublicKey(publicKey);
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            byte[] bytes = Convert.FromBase64String(dataEncrypted);
            rsa.ImportParameters(_publicKey);
            isVerify= rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("MD5"), Encoding.UTF8.GetBytes("123456"));
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] output = md5.ComputeHash(Encoding.UTF8.GetBytes("123456"));
            output = md5.ComputeHash(output);
            isVerify= rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("MD5"), output);
            SHA1 sha1 = new SHA1CryptoServiceProvider();
            output = sha1.ComputeHash(Encoding.UTF8.GetBytes("123456"));
            isVerify = rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("SHA1"), output);
            return isVerify;
        }

        public static byte[] DecryptUsingPublic(string dataEncrypted, string publicKey)
        {
            if (dataEncrypted == null) throw new ArgumentNullException("dataEncrypted");
            if (publicKey == null) throw new ArgumentNullException("publicKey");
            //try
            //{
                RSAParameters _publicKey = LoadRsaPublicKey(publicKey);
                RSACryptoServiceProvider rsa = InitRSAProvider(_publicKey);

                byte[] bytes = Convert.FromBase64String(dataEncrypted);
                byte[] decryptedBytes = rsa.Decrypt(bytes, false);
                return decryptedBytes;
            //}
            //catch(Exception ex)
            //{
            //    return null;
            //}
        }

        public static RSAParameters LoadRsaPublicKey(String publicKey)
        {
            RSAParameters RSAKeyInfo = new RSAParameters();
            byte[] pubkey = Convert.FromBase64String(publicKey);
            byte[] SeqOID = { 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00 };
            byte[] seq = new byte[15];
            // ---------  Set up stream to read the asn.1 encoded SubjectPublicKeyInfo blob  ------
            MemoryStream mem = new MemoryStream(pubkey);
            BinaryReader binr = new BinaryReader(mem);    //wrap Memory Stream with BinaryReader for easy reading
            byte bt = 0;
            ushort twobytes = 0;

            try
            {

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130) //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                else if (twobytes == 0x8230)
                    binr.ReadInt16();   //advance 2 bytes
                else
                    return RSAKeyInfo;

                seq = binr.ReadBytes(15);       //read the Sequence OID
                if (!CompareBytearrays(seq, SeqOID))    //make sure Sequence for OID is correct
                    return RSAKeyInfo;

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8103) //data read as little endian order (actual data order for Bit String is 03 81)
                    binr.ReadByte();    //advance 1 byte
                else if (twobytes == 0x8203)
                    binr.ReadInt16();   //advance 2 bytes
                else
                    return RSAKeyInfo;

                bt = binr.ReadByte();
                if (bt != 0x00)     //expect null byte next
                    return RSAKeyInfo;

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130) //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                else if (twobytes == 0x8230)
                    binr.ReadInt16();   //advance 2 bytes
                else
                    return RSAKeyInfo;

                twobytes = binr.ReadUInt16();
                byte lowbyte = 0x00;
                byte highbyte = 0x00;

                if (twobytes == 0x8102) //data read as little endian order (actual data order for Integer is 02 81)
                    lowbyte = binr.ReadByte();  // read next bytes which is bytes in modulus
                else if (twobytes == 0x8202)
                {
                    highbyte = binr.ReadByte(); //advance 2 bytes
                    lowbyte = binr.ReadByte();
                }
                else
                    return RSAKeyInfo;
                byte[] modint = { lowbyte, highbyte, 0x00, 0x00 };   //reverse byte order since asn.1 key uses big endian order
                int modsize = BitConverter.ToInt32(modint, 0);

                byte firstbyte = binr.ReadByte();
                binr.BaseStream.Seek(-1, SeekOrigin.Current);

                if (firstbyte == 0x00)
                {   //if first byte (highest order) of modulus is zero, don't include it
                    binr.ReadByte();    //skip this null byte
                    modsize -= 1;   //reduce modulus buffer size by 1
                }

                byte[] modulus = binr.ReadBytes(modsize);   //read the modulus bytes

                if (binr.ReadByte() != 0x02)            //expect an Integer for the exponent data
                    return RSAKeyInfo;
                int expbytes = (int)binr.ReadByte();        // should only need one byte for actual exponent data (for all useful values)
                byte[] exponent = binr.ReadBytes(expbytes);


                RSAKeyInfo.Modulus = modulus;
                RSAKeyInfo.Exponent = exponent;

                return RSAKeyInfo;
            }
            catch (Exception)
            {
                return RSAKeyInfo;
            }

            finally { binr.Close(); }
            //return RSAparams;

        }

        private static RSACryptoServiceProvider InitRSAProvider(RSAParameters rsaParam)
        {
            //
            // Initailize the CSP
            //   Supresses creation of a new key
            //
            CspParameters csp = new CspParameters();
            //csp.KeyContainerName = "RSA Test (OK to Delete)";

            const int PROV_RSA_FULL = 1;
            csp.ProviderType = PROV_RSA_FULL;

            const int AT_KEYEXCHANGE = 1;
            // const int AT_SIGNATURE = 2;
            csp.KeyNumber = AT_KEYEXCHANGE;
            //
            // Initialize the Provider
            //
            RSACryptoServiceProvider rsa =
              new RSACryptoServiceProvider(csp);
            rsa.PersistKeyInCsp = false;

            //
            // The moment of truth...
            //
            rsa.ImportParameters(rsaParam);
            return rsa;
        }

        private static int GetIntegerSize(BinaryReader binr)
        {
            byte bt = 0;
            byte lowbyte = 0x00;
            byte highbyte = 0x00;
            int count = 0;
            bt = binr.ReadByte();
            if (bt != 0x02)     //expect integer
                return 0;
            bt = binr.ReadByte();

            if (bt == 0x81)
                count = binr.ReadByte();    // data size in next byte
            else
                if (bt == 0x82)
                {
                    highbyte = binr.ReadByte(); // data size in next 2 bytes
                    lowbyte = binr.ReadByte();
                    byte[] modint = { lowbyte, highbyte, 0x00, 0x00 };
                    count = BitConverter.ToInt32(modint, 0);
                }
                else
                {
                    count = bt;     // we already have the data size
                }

            while (binr.ReadByte() == 0x00)
            {   //remove high order zeros in data
                count -= 1;
            }
            binr.BaseStream.Seek(-1, SeekOrigin.Current);       //last ReadByte wasn't a removed zero, so back up a byte
            return count;
        }

        private static bool CompareBytearrays(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            int i = 0;
            foreach (byte c in a)
            {
                if (c != b[i])
                    return false;
                i++;
            }
            return true;
        }


    
    }
}
