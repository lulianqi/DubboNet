using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace DubboNet.DubboService.DataModle
{
    [DataContract]
    public class ZNode : IEnumerable,ICloneable, IDisposable
    {
        [DataContract]
        public enum ZNodeType
        {
            [DataMember]
            Node,
            [DataMember]
            Error,
            [DataMember]
            Unknow
        }

        [DataMember]
        private int _version;
        [DataMember]
        private List<ZNode> _zNodeChildren;
        private bool disposedValue;

        //即使在内部也不要直接修改ZNodeChildren集合，请使用内部提供的函数修改集合以保证Tree结构不被破坏
        private List<ZNode> ZNodeChildren
        {
            get { return _zNodeChildren; }
            set
            {
                _zNodeChildren = value;
                if (_zNodeChildren?.Count > 0)
                {
                    foreach (var child in _zNodeChildren)
                    {
                        child.ParentZNode = this;
                    }
                    if (this.IsLeafNode) this.IsLeafNode = false;
                }
            }
        }

        /// <summary>
        /// 获取父节点
        /// </summary>
        [DataMember]
        public ZNode ParentZNode { get; private set; }
        /// <summary>
        /// 获取节点路径（外部不可修改）
        /// </summary>
        [DataMember]
        public string Path { get; private set; }
        /// <summary>
        /// 获取或设置节点备注
        /// </summary>
        [DataMember]
        public string ReMark { get; set; }
        /// <summary>
        /// 获取或设置节点值
        /// </summary>
        [DataMember]
        public string Value { get; set; }
        /// <summary>
        /// 获取或设置节点Tag
        /// </summary>
        public object Tag { get; set; }
        /// <summary>
        /// 获取或设置节点类型
        /// </summary>
        [DataMember]
        public ZNodeType Type { get; set; }
        /// <summary>
        /// 获取或设置节点数据类型 （数据类型由ZNode存储或标识决定，由应用方设置及使用）
        /// </summary>
        [DataMember]
        public string NodeDataType { get; set; }
        /// <summary>
        /// 当前节点是否为叶子节点
        /// </summary>
        public bool IsLeafNode { get; private set; }
        /// <summary>
        /// 获取当前Znode的版本，对其子节点/孙节点的新增/删除操作后该值+1 （注意修改节点的值不会影响当前Version）
        /// </summary>
        public int Version => this._version;
        /// <summary>
        /// 是否含义子节点
        /// </summary>
        public bool HasChildren => this._zNodeChildren?.Count > 0;
        /// <summary>
        /// 当前节点是否为根节点
        /// </summary>
        public bool IsRootNode => this.ParentZNode == null;
        /// <summary>
        /// 获取子节点列表（只读，请不要直接修改改列表）
        /// </summary>
        public IReadOnlyList<ZNode> Children => (IReadOnlyList<ZNode>)_zNodeChildren;
        /// <summary>
        /// 获取当前节点的完整路径（所有父节点路径拼接）
        /// </summary>
        public string FullPath
        {
            get
            {
                if (ParentZNode == null)
                {
                    return Path;
                }
                List<ZNode> nodes = new List<ZNode>();
                nodes.Add(this);
                ZNode tempParent = ParentZNode;
                while (tempParent != null)
                {
                    nodes.Add(tempParent);
                    tempParent = tempParent.ParentZNode;
                }
                //nodes.Reverse();//使用for的索引可以避免倒叙操作
                StringBuilder sb = new StringBuilder();
                for (int i = nodes.Count - 1; i >= 0; i--)
                {
                    sb.Append(nodes[i].Path);
                    if (i > 0 && nodes[i].Path != "/") sb.Append("/");
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 获取根节点
        /// </summary>
        public ZNode RootZNode
        {
            get
            {
                ZNode tempRootNode = this;
                while (tempRootNode.ParentZNode != null)
                {
                    tempRootNode = tempRootNode.ParentZNode;
                }
                return tempRootNode;
            }
        }


        public ZNode(List<ZNode> zNodeChildren = null, string path = null, string value = null, ZNodeType type = ZNodeType.Unknow)
        {
            ZNodeChildren = zNodeChildren;
            Path = path;
            Value = value;
            Type = type;
            IsLeafNode = !(zNodeChildren?.Count>0);
        }

        public ZNode():this(null,null,null,ZNodeType.Unknow)
        {

        }

        /// <summary>
        /// 更新节点最新修改的版本值
        /// </summary>
        private void UpdataVersion()
        {
            ZNode updataNode = this;
            this._version++;
            updataNode = this.ParentZNode;
            while (updataNode!=null)
            {
                updataNode._version++;
                updataNode = updataNode.ParentZNode;
            }
        }

        /// <summary>
        /// 清空当前节点的子节点
        /// </summary>
        public void ClearChildren()
        {
            _zNodeChildren?.Clear();
            UpdataVersion();
            this.IsLeafNode = true;
        }

        public ZNode GetZNodeByPath(string path)
        {
            if (path == null)
            {
                throw new ArgumentException("znode is null");
            }
            string[] pathAr = path.Split('/'); // /byrobot-schedule/node
            if(pathAr[0]=="")
            {
                pathAr[0] = "/";
            }
            if(this.Path!= pathAr[0])
            {
                return null;
            }
            ZNode resultNode = this;
            for (int i = 1; i < pathAr.Length; i++)
            {
                resultNode = resultNode.ZNodeChildren.Find(nd => nd.Path == pathAr[i]);
                if(resultNode==null)
                {
                    break;
                }
            }
            return resultNode;
        }
        
        /// <summary>
        /// 为当前node添加子节点
        /// </summary>
        /// <param name="znode"></param>
        public void AddChildren(ZNode znode)
        {
            if (znode == null)
            {
                throw new ArgumentException("znode is null");
            }
            if (ZNodeChildren == null)
            {
                ZNodeChildren = new List<ZNode>();
            }
            znode.ParentZNode = this;
            ZNodeChildren.Add(znode);
            UpdataVersion();
            if (this.IsLeafNode) this.IsLeafNode = false;
        }

        /// <summary>
        /// 删除当前Node中的指定节点
        /// </summary>
        /// <param name="znode"></param>
        /// <returns></returns>
        public bool RemoveChildren([System.Diagnostics.CodeAnalysis.NotNull] ZNode znode)
        {
            if (znode == null)
            {
                throw new ArgumentException("znode is null");
            }
            if (ZNodeChildren?.Contains(znode) ?? false)
            {
                if (ZNodeChildren.Remove(znode))
                {
                    UpdataVersion();
                    if (ZNodeChildren.Count == 0) this.IsLeafNode = true;
                    return true;
                }
            }
            return false;
        }





        /// <summary>
        /// 删除Tree中任意node （根节点无法删除）
        /// </summary>
        /// <param name="isCheckTreeList">是否需要遍历确认node是否在Tree里，如果需要频繁调用请设置为false</param>
        /// <param name="znode"></param>
        /// <param name="isCheckTreeList">是否检查znode是否属于当前znode，如果有可能不属于请保持默认值</param>
        /// <param name="isPromoteChildren">删除节点后是否将被删除的节点的子节点保留并上移</param>
        /// <returns>是否完成移除</returns>
        public bool RemoveAny([System.Diagnostics.CodeAnalysis.NotNull] ZNode znode ,bool isCheckTreeList = true ,bool isPromoteChildren = false)
        {
            if (znode == null)
            {
                throw new ArgumentException("znode is null");
            }
            if (!isCheckTreeList || (this.ToList()?.Contains(znode) ?? false))
            {
                if (znode.ParentZNode == null)
                {
                    //当前要删除的节点是Root节点
                    return false;
                }
                else
                {
                    ZNode tempParent = znode.ParentZNode;
                    bool removeResult = znode.ParentZNode.RemoveChildren(znode);
                    if (isPromoteChildren && removeResult && znode.HasChildren)
                    {
                        foreach (ZNode tempChild in znode.ZNodeChildren)
                        {
                            tempParent.AddChildren(tempChild);
                        }
                    }
                    return removeResult;
                }
            }
            return false;
        }

        /// <summary>
        /// 删除符合条件的所有节点
        /// </summary>
        /// <param name="removeNodeFilterFunc">筛选函数满足条件的将被移除</param>
        /// <param name="isPromoteChildren">删除节点后是否将被删除的节点的子节点保留并上移</param>
        /// <returns></returns>
        public bool RemoveAny([System.Diagnostics.CodeAnalysis.NotNull] Func<ZNode, bool> removeNodeFilterFunc, bool isPromoteChildren = false)
        {
            List<ZNode> willRemoveNodeList = new List<ZNode>();
            foreach(ZNode tempNode in this)
            {
                if(removeNodeFilterFunc(tempNode))
                {
                    willRemoveNodeList.Add(tempNode);
                }
            }
            foreach (ZNode tempNode in willRemoveNodeList)
            {
                this.RemoveAny(tempNode, false, isPromoteChildren);
            }
            return willRemoveNodeList.Count > 0;
        }


        /// <summary>
        /// 将Tree转换为List
        /// </summary>
        /// <returns></returns>
        public List<ZNode> ToList()
        {
            List<ZNode> zNodes = new List<ZNode>();
            foreach(ZNode node in this)
            {
                zNodes.Add(node);
            }
            return zNodes;
        }

        /// <summary>
        /// 获取Tree中叶子节点列表(注意如果只有一个根节点，该节点也会被当作叶子节点处理)
        /// </summary>
        /// <returns></returns>
        public List<ZNode> GetLeafNodeList()
        {
            List<ZNode> leafNodeList = new List<ZNode>();
            foreach (ZNode tempNode in this)
            {
                if (!tempNode.HasChildren)
                {
                    leafNodeList.Add(tempNode);
                }
            }
            return leafNodeList;
        }

        /// <summary>
        /// 使用指定筛选函数筛选Znode叶子节点，符合筛选条件的叶子节点及其父节点链将会保留，其他的删除(注意筛选对象仅包含叶子节点)(如果所有叶子节点都不匹配，会仅保留根节点)
        /// </summary>
        /// <param name="leafNodeFilterFunc">筛选器表达式（注意：满足条件将保留,请处理您传入函数的异常）</param>
        /// <param name="filterNodeDataType">设置匹配中节点数据类型（非必填，仅设置匹配节点，其上级节点虽然也会被保留但不会被设置该节点类型）</param>
        /// <returns>返回结果树（将会是一个新的ZNode，筛选不会改变当前树的结构）</returns>
        public ZNode FilterLeafNode([System.Diagnostics.CodeAnalysis.NotNull] Func<ZNode,bool> leafNodeFilterFunc,string filterNodeDataType = null)
        {
            ZNode filterResultNode = this.DeepClone();
            List<ZNode> leafs = filterResultNode.GetLeafNodeList();
            List<ZNode> skipLeafs = new List<ZNode>();
            List<ZNode> willDelNodeList = filterResultNode.ToList();
            foreach(var tempNode in leafs)
            {
                if(skipLeafs.Contains(tempNode))
                {
                    continue;
                }
                if(leafNodeFilterFunc(tempNode))
                {
                    willDelNodeList.Remove(tempNode);
                    if(!string.IsNullOrEmpty(filterNodeDataType))
                    {
                        tempNode.NodeDataType = filterNodeDataType;
                    }
                    if (tempNode.ParentZNode!=null)
                    {
                        //如果该叶子需要保留，则统一先处理他的兄弟节点，提前删除掉，避免反复遍历(因为如果后面发现兄弟节点也符合条件会反复保留其父节点)
                        foreach (ZNode brotherNode in tempNode.ParentZNode.ZNodeChildren)
                        {
                            if(brotherNode == tempNode)
                            {
                                continue;
                            }
                            if(brotherNode.HasChildren)
                            {
                                continue;
                            }
                            if(leafNodeFilterFunc(brotherNode))
                            {
                                willDelNodeList.Remove(brotherNode);
                                if (!string.IsNullOrEmpty(filterNodeDataType))
                                {
                                    brotherNode.NodeDataType = filterNodeDataType;
                                }
                            }
                            //leafs.Remove(brotherNode);//新版本net BLC Dictionary删除元素版本已经不更新了，可以在遍历里删除,List 还是不行
                            skipLeafs.Add(brotherNode);
                        }

                        //需要保留的叶子节点其父节点都需要保留
                        ZNode tempParent = tempNode.ParentZNode;
                        while(tempParent != null)
                        {
                            willDelNodeList.Remove(tempParent);
                            tempParent = tempParent.ParentZNode;
                        }
                    }
                }
            }

            foreach(ZNode delNode in willDelNodeList)
            {
                filterResultNode.RemoveAny(delNode, false);
            }
            return filterResultNode;
        }

        public object Clone()
        {
            return DeepClone();
        }

        /// <summary>
        /// 深度克隆(被深度克隆出来的对象因避免对源对象或源对象里成员的引用)
        /// </summary>
        /// <returns></returns>
        public ZNode DeepClone()
        {
            ZNode cloneNode = new ZNode(null, this.Path, this.Value, this.Type);
            cloneNode.NodeDataType = this.NodeDataType;
            cloneNode.ParentZNode = null;//被克隆出来的node不保有父级关系
            if(cloneNode.Tag is ICloneable)
            {
                cloneNode.Tag = ((ICloneable)this.Tag).Clone();
            }
            else
            {
                cloneNode.Tag = this.Tag;
            }
            cloneNode._version = 0;//克隆Znode版本全部重置为0
            if (this.ZNodeChildren?.Count > 0)
            {
                cloneNode.ZNodeChildren = new List<ZNode>();
                foreach (ZNode childNode in this.ZNodeChildren)
                {
                    cloneNode.AddChildren(childNode.DeepClone());
                }
            }
            return cloneNode;
        }

        #region 迭代器实现

        public IEnumerator GetEnumerator()
        {
            return new ZnodeEnumerator(this);
        }

        internal class ZnodeEnumerator : IEnumerator
        {
            private ZNode _rootZNode;
            private int _index;
            private int _listIndex;
            private readonly int _version;
            private ZNode _current;
            private IEnumerator innerEnumerator = null;
            private List<IEnumerator> nowEnumeratorList = null; //List<ZNode>.Enumerator
            private List<IEnumerator> nextEnumeratorList = null;

            object IEnumerator.Current
            {
                get
                {
                    return Current;
                }
            }

            public ZNode Current => _current;

            public ZnodeEnumerator(ZNode zNode)
            {
                _rootZNode = zNode;
                _index = 0;
                _listIndex = 0;
                _version = zNode._version;
                _current = default;
                innerEnumerator = null;
                nowEnumeratorList = new List<IEnumerator>();
                nextEnumeratorList = new List<IEnumerator>();

            }

            public bool MoveNext()
            {
                if (_version != _rootZNode._version)
                {
                    throw new InvalidOperationException("InvalidOperation_EnumFailedVersion（Tree 数据已经被更新）");
                }
                if (_index == 0)
                {
                    _current = _rootZNode;
                    nowEnumeratorList.Clear();
                    if (_current.HasChildren)
                    {
                        nowEnumeratorList.Add(_current.ZNodeChildren.GetEnumerator());
                    }
                    _index++;
                    return true;
                }
                else
                {
                    if (!(nowEnumeratorList?.Count > 0))
                    {
                        return false;
                    }
                    //using (var tempNowEnumerator = nowEnumeratorList[_listIndex]) { }//BCL里默认迭代器是struct，这里只能是值传递，tempNowEnumerator的MoveNext不会影响nowEnumeratorList[_listIndex]
                    IEnumerator tempNowEnumerator = nowEnumeratorList[_listIndex];
                    if (tempNowEnumerator.MoveNext())
                    {
                        _current = (ZNode)tempNowEnumerator.Current;
                        if (_current.HasChildren)
                        {
                            nextEnumeratorList.Add(_current.ZNodeChildren.GetEnumerator());
                        }
                        return true;
                    }
                    //当前Enumerator到头了
                    else
                    {
                        _listIndex++;
                        //nowEnumeratorList移动到下一个Enumerator
                        if (_listIndex < nowEnumeratorList.Count)
                        {
                            return MoveNext();
                        }
                        //nowEnumeratorList交换为nextEnumeratorList
                        else
                        {
                            nowEnumeratorList = nextEnumeratorList;
                            nextEnumeratorList = new List<IEnumerator>();
                            _listIndex = 0;
                            _index++;
                            return MoveNext();
                        }

                    }
                    
                }
            }

            public void Reset()
            {
                _index = 0;
                _listIndex = 0;
                _current = default;
                innerEnumerator = null;
                nowEnumeratorList = new List<IEnumerator>();
                nextEnumeratorList = new List<IEnumerator>();
            }
        }
        #endregion

        #region Disposes实现
        public bool IsDisposed
        {
            get;
            private set;
        } = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (this.HasChildren)
                {
                    foreach (var child in this.ZNodeChildren)
                    {
                        child.Dispose();
                    }
                }
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)
                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                IsDisposed = true;
            }
        }

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~ZNode()
        // {
        //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        } 
        #endregion

    }
}
