using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DubboNet.DubboService.Native
{
    /// <summary>
    /// 将 Hessian2 值读取为动态 .NET 列表和字典，用于 CLR 返回类型未知的 Dubbo 泛化响应。
    /// <para>EN: Reads Hessian2 values into dynamic .NET lists and dictionaries for generic Dubbo responses whose CLR result type is unknown.</para>
    /// </summary>
    internal sealed class DubboHessian2Reader
    {
        private sealed class ClassDefinition
        {
            public string TypeName { get; set; }
            public string[] Fields { get; set; }
        }

        private readonly byte[] _buffer;
        private readonly List<object> _references = new List<object>();
        private readonly List<string> _typeReferences = new List<string>();
        private readonly List<ClassDefinition> _classDefinitions = new List<ClassDefinition>();
        private int _position;

        /// <summary>
        /// 创建读取指定 Hessian2 数据的解析器。
        /// <para>EN: Creates a reader for the supplied Hessian2 payload.</para>
        /// </summary>
        public DubboHessian2Reader(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <summary>
        /// 读取一个 Hessian2 整数。
        /// <para>EN: Reads one Hessian2 integer.</para>
        /// </summary>
        public int ReadInt()
        {
            return ReadInt(ReadByte());
        }

        /// <summary>
        /// 读取一个 Hessian2 字符串。
        /// <para>EN: Reads one Hessian2 string.</para>
        /// </summary>
        public string ReadString()
        {
            return ReadString(ReadByte());
        }

        /// <summary>
        /// 读取下一个动态 Hessian2 值。
        /// <para>EN: Reads the next dynamic Hessian2 value.</para>
        /// </summary>
        public object ReadValue()
        {
            byte tag = ReadByte();
            if (tag == (byte)'N') return null;
            if (tag == (byte)'T') return true;
            if (tag == (byte)'F') return false;

            if (IsIntTag(tag)) return ReadInt(tag);
            if (IsLongTag(tag)) return ReadLong(tag);
            if (IsDoubleTag(tag)) return ReadDouble(tag);

            if (tag == 0x4a)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ReadInt64()).UtcDateTime;
            }
            if (tag == 0x4b)
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)ReadInt32() * 60).UtcDateTime;
            }

            if (IsStringTag(tag)) return ReadString(tag);
            if (IsBinaryTag(tag)) return ReadBinary(tag);

            if (IsListTag(tag)) return ReadList(tag);
            if (tag == (byte)'C') return ReadClassDefinitionAndValue();
            if (IsMapOrObjectTag(tag)) return ReadMapOrObject(tag);

            if (tag == 0x51)
            {
                int reference = ReadInt();
                if (reference < 0 || reference >= _references.Count)
                {
                    throw new InvalidDataException($"Invalid Hessian2 reference index {reference}.");
                }
                return _references[reference];
            }

            throw new InvalidDataException($"Unsupported Hessian2 tag 0x{tag:x2}.");
        }

        private object ReadClassDefinitionAndValue()
        {
            string typeName = ReadString();
            int fieldCount = ReadInt();
            if (fieldCount < 0)
            {
                throw new InvalidDataException($"Invalid Hessian2 class field count {fieldCount}.");
            }

            string[] fields = new string[fieldCount];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = ReadString();
            }
            _classDefinitions.Add(new ClassDefinition
            {
                TypeName = typeName,
                Fields = fields
            });
            return ReadValue();
        }

        private object ReadMapOrObject(byte tag)
        {
            if (tag == (byte)'H' || tag == (byte)'M')
            {
                string typeName = tag == (byte)'M' ? ReadType() : null;
                Dictionary<object, object> map = NewMap(typeName);
                _references.Add(map);
                while (PeekByte() != (byte)'Z')
                {
                    object key = ReadValue();
                    object value = ReadValue();
                    map[key ?? "null"] = value;
                }
                _position++;
                return map;
            }

            int definitionIndex = tag == (byte)'O' ? ReadInt() : tag - 0x60;
            if (definitionIndex < 0 || definitionIndex >= _classDefinitions.Count)
            {
                throw new InvalidDataException(
                    $"Invalid Hessian2 class definition index {definitionIndex}.");
            }

            ClassDefinition definition = _classDefinitions[definitionIndex];
            Dictionary<object, object> instance = NewMap(definition.TypeName);
            _references.Add(instance);
            foreach (string field in definition.Fields)
            {
                instance[field] = ReadValue();
            }
            return instance;
        }

        private static Dictionary<object, object> NewMap(string typeName)
        {
            Dictionary<object, object> result = new Dictionary<object, object>();
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                result["class"] = typeName;
            }
            return result;
        }

        private object ReadList(byte tag)
        {
            bool hasEnd;
            int length;

            switch (tag)
            {
                case 0x55:
                    _ = ReadType();
                    hasEnd = true;
                    length = -1;
                    break;
                case 0x56:
                    _ = ReadType();
                    hasEnd = false;
                    length = ReadInt();
                    break;
                case 0x57:
                    hasEnd = true;
                    length = -1;
                    break;
                case 0x58:
                    hasEnd = false;
                    length = ReadInt();
                    break;
                default:
                    hasEnd = false;
                    if (tag >= 0x70 && tag <= 0x77)
                    {
                        length = tag - 0x70;
                        _ = ReadType();
                    }
                    else
                    {
                        length = tag - 0x78;
                    }
                    break;
            }

            if (length < -1)
            {
                throw new InvalidDataException($"Invalid Hessian2 list length {length}.");
            }

            List<object> list = length > 0 ? new List<object>(length) : new List<object>();
            _references.Add(list);
            if (hasEnd)
            {
                while (PeekByte() != (byte)'Z')
                {
                    list.Add(ReadValue());
                }
                _position++;
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    list.Add(ReadValue());
                }
            }
            return list;
        }

        private string ReadType()
        {
            byte tag = PeekByte();
            if (IsIntTag(tag))
            {
                int index = ReadInt();
                if (index < 0 || index >= _typeReferences.Count)
                {
                    throw new InvalidDataException($"Invalid Hessian2 type reference index {index}.");
                }
                return _typeReferences[index];
            }

            string typeName = ReadString();
            _typeReferences.Add(typeName);
            return typeName;
        }

        private int ReadInt(byte tag)
        {
            if (tag >= 0x80 && tag <= 0xbf)
            {
                return tag - 0x90;
            }
            if (tag >= 0xc0 && tag <= 0xcf)
            {
                return ((tag - 0xc8) << 8) + ReadByte();
            }
            if (tag >= 0xd0 && tag <= 0xd7)
            {
                return ((tag - 0xd4) << 16) + (ReadByte() << 8) + ReadByte();
            }
            if (tag == (byte)'I')
            {
                return ReadInt32();
            }
            throw new InvalidDataException($"Hessian2 tag 0x{tag:x2} is not an integer.");
        }

        private long ReadLong(byte tag)
        {
            if (tag >= 0xd8 && tag <= 0xef)
            {
                return tag - 0xe0;
            }
            if (tag >= 0xf0)
            {
                return ((tag - 0xf8) << 8) + ReadByte();
            }
            if (tag >= 0x38 && tag <= 0x3f)
            {
                return ((long)(tag - 0x3c) << 16) + (ReadByte() << 8) + ReadByte();
            }
            if (tag == 0x59)
            {
                return ReadInt32();
            }
            if (tag == (byte)'L')
            {
                return ReadInt64();
            }
            throw new InvalidDataException($"Hessian2 tag 0x{tag:x2} is not a long.");
        }

        private double ReadDouble(byte tag)
        {
            switch (tag)
            {
                case 0x5b:
                    return 0d;
                case 0x5c:
                    return 1d;
                case 0x5d:
                    return (sbyte)ReadByte();
                case 0x5e:
                    return unchecked((short)ReadUInt16());
                case 0x5f:
                    return ReadInt32() * 0.001d;
                case (byte)'D':
                    return BitConverter.Int64BitsToDouble(ReadInt64());
                default:
                    throw new InvalidDataException($"Hessian2 tag 0x{tag:x2} is not a double.");
            }
        }

        private string ReadString(byte tag)
        {
            if (tag == (byte)'N') return null;
            if (tag <= 0x1f) return ReadUtf8Chars(tag);
            if (tag >= 0x30 && tag <= 0x33)
            {
                return ReadUtf8Chars(((tag - 0x30) << 8) + ReadByte());
            }
            if (tag == (byte)'S')
            {
                return ReadUtf8Chars(ReadUInt16());
            }
            if (tag == (byte)'R')
            {
                StringBuilder builder = new StringBuilder();
                builder.Append(ReadUtf8Chars(ReadUInt16()));
                builder.Append(ReadString());
                return builder.ToString();
            }
            throw new InvalidDataException($"Hessian2 tag 0x{tag:x2} is not a string.");
        }

        private string ReadUtf8Chars(int charCount)
        {
            StringBuilder builder = new StringBuilder(charCount);
            int charsRead = 0;
            while (charsRead < charCount)
            {
                byte first = ReadByte();
                if (first < 0x80)
                {
                    builder.Append((char)first);
                    charsRead++;
                }
                else if ((first & 0xe0) == 0xc0)
                {
                    int code = ((first & 0x1f) << 6) | ReadContinuationByte();
                    builder.Append((char)code);
                    charsRead++;
                }
                else if ((first & 0xf0) == 0xe0)
                {
                    int code = ((first & 0x0f) << 12)
                        | (ReadContinuationByte() << 6)
                        | ReadContinuationByte();
                    builder.Append((char)code);
                    charsRead++;
                }
                else if ((first & 0xf8) == 0xf0)
                {
                    int scalar = ((first & 0x07) << 18)
                        | (ReadContinuationByte() << 12)
                        | (ReadContinuationByte() << 6)
                        | ReadContinuationByte();
                    builder.Append(char.ConvertFromUtf32(scalar));
                    charsRead += 2;
                }
                else
                {
                    throw new InvalidDataException($"Invalid Hessian2 UTF-8 lead byte 0x{first:x2}.");
                }
            }

            if (charsRead != charCount)
            {
                throw new InvalidDataException("Hessian2 string length split a Unicode surrogate pair.");
            }
            return builder.ToString();
        }

        private int ReadContinuationByte()
        {
            byte value = ReadByte();
            if ((value & 0xc0) != 0x80)
            {
                throw new InvalidDataException($"Invalid Hessian2 UTF-8 continuation byte 0x{value:x2}.");
            }
            return value & 0x3f;
        }

        private byte[] ReadBinary(byte tag)
        {
            if (tag >= 0x20 && tag <= 0x2f)
            {
                return ReadBytes(tag - 0x20);
            }
            if (tag >= 0x34 && tag <= 0x37)
            {
                return ReadBytes(((tag - 0x34) << 8) + ReadByte());
            }
            if (tag == (byte)'B')
            {
                return ReadBytes(ReadUInt16());
            }
            if (tag == (byte)'A')
            {
                byte[] first = ReadBytes(ReadUInt16());
                byte[] rest = ReadBinary(ReadByte());
                byte[] combined = new byte[first.Length + rest.Length];
                Buffer.BlockCopy(first, 0, combined, 0, first.Length);
                Buffer.BlockCopy(rest, 0, combined, first.Length, rest.Length);
                return combined;
            }
            throw new InvalidDataException($"Hessian2 tag 0x{tag:x2} is not binary data.");
        }

        private byte[] ReadBytes(int length)
        {
            EnsureAvailable(length);
            byte[] result = new byte[length];
            Buffer.BlockCopy(_buffer, _position, result, 0, length);
            _position += length;
            return result;
        }

        private int ReadInt32()
        {
            EnsureAvailable(4);
            int value = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        private long ReadInt64()
        {
            EnsureAvailable(8);
            long value = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        private int ReadUInt16()
        {
            EnsureAvailable(2);
            int value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        private byte PeekByte()
        {
            EnsureAvailable(1);
            return _buffer[_position];
        }

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return _buffer[_position++];
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _position > _buffer.Length - count)
            {
                throw new EndOfStreamException("Unexpected end of Hessian2 payload.");
            }
        }

        private static bool IsIntTag(byte tag)
        {
            return tag == (byte)'I' || (tag >= 0x80 && tag <= 0xd7);
        }

        private static bool IsLongTag(byte tag)
        {
            return tag == (byte)'L'
                || tag == 0x59
                || (tag >= 0x38 && tag <= 0x3f)
                || tag >= 0xd8;
        }

        private static bool IsDoubleTag(byte tag)
        {
            return tag == (byte)'D' || (tag >= 0x5b && tag <= 0x5f);
        }

        private static bool IsStringTag(byte tag)
        {
            return tag == (byte)'S'
                || tag == (byte)'R'
                || tag <= 0x1f
                || (tag >= 0x30 && tag <= 0x33);
        }

        private static bool IsBinaryTag(byte tag)
        {
            return tag == (byte)'A'
                || tag == (byte)'B'
                || (tag >= 0x20 && tag <= 0x2f)
                || (tag >= 0x34 && tag <= 0x37);
        }

        private static bool IsListTag(byte tag)
        {
            return (tag >= 0x55 && tag <= 0x58)
                || (tag >= 0x70 && tag <= 0x7f);
        }

        private static bool IsMapOrObjectTag(byte tag)
        {
            return tag == (byte)'H'
                || tag == (byte)'M'
                || tag == (byte)'O'
                || (tag >= 0x60 && tag <= 0x6f);
        }
    }
}
