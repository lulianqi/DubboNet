using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetService.Telnet

{
    /// <summary>
    /// 为 Telnet 接收数据提供有界、线程同步的内存缓冲区。
    /// EN: Provides a bounded, thread-synchronized in-memory buffer for Telnet receive data.
    /// </summary>
    public class TelnetMemoryStream:IDisposable
    {
        private MemoryStream memoryStream;
        private readonly object memoryStreamLock = new object();
        private AutoResetEvent autoResetEvent = new AutoResetEvent(true);
        public int MaxLength { get; set; } = 1024 * 128;

        /// <summary>
        /// 获取当前流长度，如果流未准备好则返回 -1。
        /// EN: Gets the current stream length, or -1 when the stream is unavailable.
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
        /// EN: Whether this bounded memory stream has been disposed.
        /// </summary>
        internal bool IsDisposed { get; private set; } = false;

        /// <summary>
        /// 初始化 <see cref="TelnetMemoryStream"/>。
        /// EN: Initializes a new <see cref="TelnetMemoryStream"/> instance.
        /// </summary>
        /// <param name="maxLength">预期保持数据的长度，数据可能会短时间超过该值。EN: The target retained-data length; the buffer may temporarily exceed it.</param>
        public TelnetMemoryStream(int maxLength = 1024 * 128)
        {
            MaxLength = maxLength;
            memoryStream = new MemoryStream();
            memoryStream.Position = 0;
            autoResetEvent.Set();
        }

        /// <summary>
        /// 抛弃历史数据，仅保留MaxLength一半的数据
        /// EN: Drops old data and retains approximately half of MaxLength.
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
        /// 向缓冲区追加数据。
        /// EN: Appends data to the buffer.
        /// </summary>
        /// <param name="bytes">要追加的数据。EN: The data to append.</param>
        /// <returns>表示异步追加操作的任务。EN: A task representing the asynchronous append operation.</returns>
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
        /// 查找指定字节数组在流中的位置。
        /// EN: Finds the position of a byte sequence in the stream.
        /// </summary>
        /// <param name="findBytes">要查找的字节序列。EN: The byte sequence to locate.</param>
        /// <param name="startIndex">开始位置（默认为 0）。EN: The starting position, defaulting to 0.</param>
        /// <returns>首次出现的位置；未找到时返回 -1。EN: The first matching position, or -1 when no match is found.</returns>
        public long FindPosition(byte[] findBytes ,long startIndex=0)
        {
            if(findBytes == null || findBytes.Length==0)
            {
                throw new ArgumentNullException(nameof(findBytes));
            }
            if(startIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            }

            autoResetEvent.WaitOne();
            try
            {
                if (IsDisposed)
                {
                    return -1;
                }
                if (startIndex > memoryStream.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                }
                if (findBytes.Length > memoryStream.Length - startIndex)
                {
                    return -1;
                }

                long originalPosition = memoryStream.Position;
                try
                {
                    for (long findIndex = startIndex; findIndex <= memoryStream.Length - findBytes.Length; findIndex++)
                    {
                        bool isMatch = true;
                        memoryStream.Position = findIndex;
                        for (int i = 0; i < findBytes.Length; i++)
                        {
                            if (memoryStream.ReadByte() != findBytes[i])
                            {
                                isMatch = false;
                                break;
                            }
                        }
                        if (isMatch)
                        {
                            return findIndex;
                        }
                    }
                    return -1;
                }
                finally
                {
                    memoryStream.Position = originalPosition;
                }
            }
            finally
            {
                autoResetEvent.Set();
            }
        }

        /// <summary>
        /// 判断缓冲区是否以指定标记结尾。
        /// EN: Determines whether the buffer ends with the specified marker.
        /// </summary>
        /// <param name="endFlagBytes">结尾标记。EN: The end marker.</param>
        /// <returns>找到结尾标记时为 <see langword="true"/>。EN: <see langword="true"/> when the end marker is present.</returns>
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
        /// 获取全部流数据，并可选择排除结尾标记。
        /// EN: Gets all buffered data and optionally removes the trailing end marker.
        /// </summary>
        /// <param name="endFlagBytes">结尾标记；默认为空，表示不排除任何标记。EN: The optional trailing marker to exclude.</param>
        /// <returns>缓冲区数据。EN: The buffered data.</returns>
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
        /// 清空底层内存流，以便复用于下一次数据缓存。
        /// EN: Clears the underlying memory stream so it can be reused for the next response.
        /// </summary>
        public void Clear()
        {
            autoResetEvent.WaitOne();
            memoryStream.SetLength(0);
            memoryStream.Position = 0;
            autoResetEvent.Set();
        }

        /// <summary>
        /// 释放内存流和同步资源。
        /// EN: Releases the memory stream and synchronization resources.
        /// </summary>
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
