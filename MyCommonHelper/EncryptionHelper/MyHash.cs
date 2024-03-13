using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyCommonHelper.EncryptionHelper
{
    public class MyHash
    {
        public static uint GetMurmurHash2(byte[] data, uint seed = 0x9747b28c)
        {
            const uint m = 0x5bd1e995;
            const int r = 24;

            uint len = (uint)data.Length;
            uint h = seed ^ len;

            int currentIndex = 0;
            while (len >= 4)
            {
                uint k = (uint)(data[currentIndex++] | data[currentIndex++] << 8 | data[currentIndex++] << 16 | data[currentIndex++] << 24);
                k *= m;
                k ^= k >> r;
                k *= m;

                h *= m;
                h ^= k;
                len -= 4;
            }

            switch (len)
            {
                case 3:
                    h ^= (ushort)(data[currentIndex++] | data[currentIndex++] << 8);
                    h ^= (uint)data[currentIndex] << 16;
                    h *= m;
                    break;
                case 2:
                    h ^= (ushort)(data[currentIndex++] | data[currentIndex] << 8);
                    h *= m;
                    break;
                case 1:
                    h ^= data[currentIndex];
                    h *= m;
                    break;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;

            return h;
        }
    }
}
