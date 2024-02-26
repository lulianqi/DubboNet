using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetService.Telnet

{
    public class TelnetMemoryStream:IDisposable
    {
        private MemoryStream memoryStream;
        private readonly object memoryStreamLock = new object();
        private AutoResetEvent autoResetEvent = new AutoResetEvent(true);
        public int MaxLength { get; set; } = 1024 * 128;

        /// <summary>
        ///获取当前流长度，如果流未准备好则返回-1
        /// </summary>
        public long Length
        {
            get
            {
                return memoryStream?.Length ?? -1;
            }
        }

        /// <summary>
        /// 是否已经被释放
        /// </summary>
        internal bool IsDisposed { get; private set; } = false;

        /// <summary>
        /// 初始化TelnetMemoryStream
        /// </summary>
        /// <param name="maxLength">预期保持数据的长度，数据可能会短时间超过该值</param>
        public TelnetMemoryStream(int maxLength = 1024 * 128)
        {
            MaxLength = maxLength;
            memoryStream = new MemoryStream();
            memoryStream.Position = 0;
            autoResetEvent.Set();
        }

        /// <summary>
        /// 抛弃历史数据，仅保留MaxLength一半的数据
        /// </summary>
        private async Task DropHistoricalData()
        {
            int keepLength = MaxLength / 2;
            if (memoryStream.Length> keepLength)
            {
                autoResetEvent.WaitOne();
                if(IsDisposed) return;
                byte[] tempBytes = new byte[keepLength];
                memoryStream.Position = memoryStream.Position - keepLength;
                await memoryStream.ReadAsync(tempBytes, 0, tempBytes.Length);
                memoryStream.Position = 0;
                await memoryStream.WriteAsync(tempBytes, 0, tempBytes.Length);
                memoryStream.SetLength(keepLength);
                autoResetEvent.Set();
            }
        }

        /// <summary>
        /// 添加数据
        /// </summary>
        /// <param name="bytes">数据</param>
        /// <returns></returns>
        public async Task AddDataAsync(byte[] bytes)
        {
            if(memoryStream.Length+ bytes.Length> MaxLength)
            {
                await DropHistoricalData();
            }
            autoResetEvent.WaitOne();
            if (IsDisposed) return;
            await memoryStream.WriteAsync(bytes, 0, bytes.Length);
            autoResetEvent.Set();
        }

      
        /// <summary>
        /// 查找指定字节数组在流中的位置
        /// </summary>
        /// <param name="findBytes">需要查找的位置</param>
        /// <param name="startIndex">开始的位置（默认为0）</param>
        /// <returns>查找结果首次出现的位置，如果没有找到则返回-1</returns>
        public long FindPosition(byte[] findBytes ,long startIndex=0)
        {
            if(findBytes == null || findBytes.Length==0)
            {
                throw new ArgumentNullException(nameof(findBytes));
            }
            if(findBytes.Length < startIndex)
            {
                throw new Exception("error startIndex");
            }
            if(findBytes.Length> memoryStream.Length)
            {
                return  -1;
            }
            long findIndx = 0;
            autoResetEvent.WaitOne();
            if (IsDisposed) return -1;
            byte[] buffer = new byte[findBytes.Length];
            bool tempFind = true;
            for (findIndx = startIndex; findIndx< memoryStream.Length- findBytes.Length +1; findIndx++)
            {
                memoryStream.Position = findIndx;
                for(int i =0;i< findBytes.Length;i++)
                {
                    if (memoryStream.ReadByte() != findBytes[i])
                    {
                        continue;
                    }
                    else
                    {
                        tempFind = false;
                        break;
                    }
                }
                if(tempFind)
                {
                    break;
                }
            }
            if(!tempFind)
            {
                findIndx = -1;
            }
            autoResetEvent.Set();
            return findIndx;
        }

        /// <summary>
        /// 是否获取到标记结尾
        /// </summary>
        /// <param name="endFlagBytes">结尾标记</param>
        /// <returns>是否找到</returns>
        public bool IsGetEndFlag(byte[] endFlagBytes)
        {
            if (endFlagBytes == null || endFlagBytes.Length==0)
            {
                throw new ArgumentNullException(nameof(endFlagBytes));
            }
            if (endFlagBytes.Length> memoryStream.Length)
            {
                return false;
            }
            autoResetEvent.WaitOne();
            if (IsDisposed) return false;
            memoryStream.Position = memoryStream.Position - endFlagBytes.Length;
            for(int i =0;i< endFlagBytes.Length;i++)
            {
                if(memoryStream.ReadByte() != endFlagBytes[i])
                {
                    memoryStream.Position = memoryStream.Length;
                    autoResetEvent.Set();
                    return false;
                }
            }
            autoResetEvent.Set();
            return true;
        }

        /// <summary>
        /// 获取全部流数据 （可以选择是否排除结尾标记）
        /// </summary>
        /// <param name="endFlagBytes">结尾标记（默认为空，表示没有需要排除的结尾标记）</param>
        /// <returns>返回数据</returns>
        public async Task<byte[]> GetMemoryDataAsync(byte[] endFlagBytes = null)
        {
            bool isRemoveEndFlag = false;
            if(endFlagBytes!=null && endFlagBytes.Length>0)
            {
                isRemoveEndFlag = IsGetEndFlag(endFlagBytes);
            }
            autoResetEvent.WaitOne();
            if (IsDisposed) return null;
            long tempLength = memoryStream.Length - (isRemoveEndFlag ? endFlagBytes.Length:0);
            byte[] resultBytes = new byte[tempLength];
            memoryStream.Position = 0;
            await memoryStream.ReadAsync(resultBytes, 0, resultBytes.Length);
            memoryStream.Position = memoryStream.Length;
            autoResetEvent.Set();
            return resultBytes;
        }

        /// <summary>
        /// 清空memoryStream，清空后可以复用用于下一个数据缓存
        /// </summary>
        public void Clear()
        {
            autoResetEvent.WaitOne();
            memoryStream.SetLength(0);
            memoryStream.Position = 0;
            autoResetEvent.Set();
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                if (!autoResetEvent.WaitOne(0))
                {
                    autoResetEvent.Set();
                    Thread.Yield();
                }
                autoResetEvent.Dispose();
                autoResetEvent = null;
                memoryStream?.Dispose();
                memoryStream = null;
            }
        }
    }
}
