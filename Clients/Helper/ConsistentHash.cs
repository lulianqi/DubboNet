﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DubboNet.Clients.Helper
{
    public class ConsistentHash<T>
    {
        SortedDictionary<int, T> circle = new SortedDictionary<int, T>();
        int _replicate = 100;   //default _replicate count
        int[] ayKeys = null;    //cache the ordered keys for better performance

        //it's better you override the GetHashCode() of T.
        //we will use GetHashCode() to identify different node.

        public ConsistentHash(int replicate = 100)
        {
            _replicate = replicate;
        }

        public void Init(IEnumerable<T> nodes = null)
        {
            Init(nodes, _replicate);
        }

        public void Init(IEnumerable<T> nodes , int replicate)
        {
            _replicate = replicate;

            circle.Clear();
            ayKeys = null;
            if(nodes==null)
            {
                return;
            }
            foreach (T node in nodes)
            {
                this.Add(node, false);
            }
            ayKeys = circle.Keys.ToArray();
        }

        public void Add(T node)
        {
            Add(node, true);
        }

        private void Add(T node, bool updateKeyArray)
        {
            for (int i = 0; i < _replicate; i++)
            {
                //int hash = BetterHash(node.GetHashCode().ToString() + i);
                int hash = AddNodeHash(node,i);
                circle[hash] = node;
            }

            if (updateKeyArray)
            {
                ayKeys = circle.Keys.ToArray();
            }
        }

        public void Remove(T node)
        {
            for (int i = 0; i < _replicate; i++)
            {
                //int hash = BetterHash(node.GetHashCode().ToString() + i);
                int hash = AddNodeHash(node, i);
                if (!circle.Remove(hash))
                {
                    throw new Exception("can not remove a node that not added");
                }
            }
            ayKeys = circle.Keys.ToArray();
        }

        //we keep this function just for performance compare
        private T GetNode_slow(String key)
        {
            int hash = GetNodeHash(key);
            if (circle.ContainsKey(hash))
            {
                return circle[hash];
            }

            int first = circle.Keys.FirstOrDefault(h => h >= hash);
            if (first == new int())
            {
                first = ayKeys[0];
            }
            T node = circle[first];
            return node;
        }

        //return the index of first item that >= val.
        //if not exist, return 0;
        //ay should be ordered array.
        int First_ge(int[] ay, int val)
        {
            int begin = 0;
            int end = ay.Length - 1;

            if (ay[end] < val || ay[0] > val)
            {
                return 0;
            }

            int mid = begin;
            while (end - begin > 1)
            {
                mid = (end + begin) / 2;
                if (ay[mid] >= val)
                {
                    end = mid;
                }
                else
                {
                    begin = mid;
                }
            }

            if (ay[begin] > val || ay[end] < val)
            {
                throw new Exception("should not happen");
            }

            return end;
        }

        /// <summary>
        /// 获取目标节点
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public T GetNode(String key)
        {
            //return GetNode_slow(key);

            int hash = GetNodeHash(key);

            int first = First_ge(ayKeys, hash);

            //int diff = circle.Keys[first] - hash;

            return circle[ayKeys[first]];
        }

        public virtual int AddNodeHash(T node , int replicateIndex)
        {
            return BetterHash(node.GetHashCode().ToString() + replicateIndex);
        }

        public virtual int GetNodeHash(String key)
        {
            return BetterHash(key);
        }

        //default String.GetHashCode() can't well spread strings like "1", "2", "3"
        public static int BetterHash(String key)
        {
            uint hash = MyCommonHelper.EncryptionHelper.MyHash.GetMurmurHash2(Encoding.ASCII.GetBytes(key));
            return (int)hash;
        }
    }
}
