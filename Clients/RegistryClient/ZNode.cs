using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace DubboNet.Clients.RegistryClient
{
    /// <summary>
    /// 表示一个可组成树结构的 ZooKeeper 节点快照。
    /// EN: Represents a ZooKeeper node snapshot that can be organized as a tree.
    /// </summary>
    [DataContract]
    public class ZNode : IEnumerable, ICloneable, IDisposable
    {
        /// <summary>
        /// 指定节点的逻辑类型。
        /// EN: Specifies the logical type of a node.
        /// </summary>
        [DataContract]
        public enum ZNodeType
        {
            /// <summary>
            /// 普通节点。
            /// EN: A regular node.
            /// </summary>
            [DataMember]
            Node,
            /// <summary>
            /// 表示错误信息的节点。
            /// EN: A node that represents error information.
            /// </summary>
            [DataMember]
            Error,
            /// <summary>
            /// 类型未知的节点。
            /// EN: A node whose type is unknown.
            /// </summary>
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
                    if (IsLeafNode) IsLeafNode = false;
                }
            }
        }

        /// <summary>
        /// 获取父节点。
        /// EN: Gets the parent node.
        /// </summary>
        [DataMember]
        public ZNode ParentZNode { get; private set; }
        /// <summary>
        /// 获取节点路径；调用方不能直接修改该值。
        /// EN: Gets the node path; callers cannot modify this value directly.
        /// </summary>
        [DataMember]
        public string Path { get; private set; }
        /// <summary>
        /// 获取或设置节点备注。
        /// EN: Gets or sets the node remark.
        /// </summary>
        [DataMember]
        public string ReMark { get; set; }
        /// <summary>
        /// 获取或设置节点值。
        /// EN: Gets or sets the node value.
        /// </summary>
        [DataMember]
        public string Value { get; set; }
        /// <summary>
        /// 获取或设置与节点关联的自定义对象。
        /// EN: Gets or sets the custom object associated with the node.
        /// </summary>
        public object Tag { get; set; }
        /// <summary>
        /// 获取或设置节点类型。
        /// EN: Gets or sets the node type.
        /// </summary>
        [DataMember]
        public ZNodeType Type { get; set; }
        /// <summary>
        /// 获取或设置节点数据类型；该标识由应用方定义和使用。
        /// EN: Gets or sets the application-defined data type identifier for the node.
        /// </summary>
        [DataMember]
        public string NodeDataType { get; set; }
        /// <summary>
        /// 获取当前节点是否为叶子节点。
        /// EN: Gets whether the current node is a leaf node.
        /// </summary>
        public bool IsLeafNode { get; private set; }
        /// <summary>
        /// 获取当前节点的结构版本；当前节点或任意后代发生增删时递增，修改节点值不会改变该版本。
        /// EN: Gets the structural version, which increments when this node or any descendant is added or removed; changing a node value does not affect it.
        /// </summary>
        public int Version => _version;
        /// <summary>
        /// 获取当前节点是否包含子节点。
        /// EN: Gets whether the current node contains child nodes.
        /// </summary>
        public bool HasChildren => _zNodeChildren?.Count > 0;
        /// <summary>
        /// 获取当前节点是否为根节点。
        /// EN: Gets whether the current node is the root node.
        /// </summary>
        public bool IsRootNode => ParentZNode == null;
        /// <summary>
        /// 获取子节点的只读列表。
        /// EN: Gets the read-only list of child nodes.
        /// </summary>
        public IReadOnlyList<ZNode> Children => _zNodeChildren;
        /// <summary>
        /// 获取由当前节点及其所有父节点路径拼接而成的完整路径。
        /// EN: Gets the full path composed from this node and all of its parent paths.
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
        /// 获取当前树的根节点。
        /// EN: Gets the root node of the current tree.
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

        /// <summary>
        /// 使用子节点、路径、值和类型初始化节点。
        /// EN: Initializes a node with child nodes, a path, a value, and a node type.
        /// </summary>
        /// <param name="zNodeChildren">初始子节点。EN: The initial child nodes.</param>
        /// <param name="path">节点路径。EN: The node path.</param>
        /// <param name="value">节点值。EN: The node value.</param>
        /// <param name="type">节点类型。EN: The node type.</param>
        public ZNode(List<ZNode> zNodeChildren = null, string path = null, string value = null, ZNodeType type = ZNodeType.Unknow)
        {
            ZNodeChildren = zNodeChildren;
            Path = path;
            Value = value;
            Type = type;
            IsLeafNode = !(zNodeChildren?.Count > 0);
        }
        /// <summary>
        /// 初始化一个空节点。
        /// EN: Initializes an empty node.
        /// </summary>
        public ZNode() : this(null, null, null, ZNodeType.Unknow)
        {

        }

        /// <summary>
        /// 更新节点最新修改的版本值
        /// EN: Propagates a structural version increment from this node to its ancestors.
        /// </summary>
        private void UpdataVersion()
        {
            ZNode updataNode = this;
            _version++;
            updataNode = ParentZNode;
            while (updataNode != null)
            {
                updataNode._version++;
                updataNode = updataNode.ParentZNode;
            }
        }

        /// <summary>
        /// 清空当前节点的全部子节点，并将当前节点标记为叶子节点。
        /// EN: Removes all child nodes and marks the current node as a leaf.
        /// </summary>
        public void ClearChildren()
        {
            _zNodeChildren?.Clear();
            UpdataVersion();
            IsLeafNode = true;
        }

        /// <summary>
        /// 从当前节点开始按完整路径查找节点。
        /// EN: Finds a node by full path, starting at the current node.
        /// </summary>
        /// <param name="path">要查找的节点路径。EN: The node path to find.</param>
        /// <returns>匹配的节点；未找到时返回 <see langword="null"/>。EN: The matching node, or <see langword="null"/> when no node is found.</returns>
        /// <exception cref="ArgumentException">路径为 <see langword="null"/>。EN: The path is <see langword="null"/>.</exception>
        public ZNode GetZNodeByPath(string path)
        {
            if (path == null)
            {
                throw new ArgumentException("znode is null");
            }
            string[] pathAr = path.Split('/'); // /byrobot-schedule/node
            if (pathAr[0] == "")
            {
                pathAr[0] = "/";
            }
            if (Path != pathAr[0])
            {
                return null;
            }
            ZNode resultNode = this;
            for (int i = 1; i < pathAr.Length; i++)
            {
                resultNode = resultNode.ZNodeChildren.Find(nd => nd.Path == pathAr[i]);
                if (resultNode == null)
                {
                    break;
                }
            }
            return resultNode;
        }

        /// <summary>
        /// 为当前节点添加一个子节点。
        /// EN: Adds a child to the current node.
        /// </summary>
        /// <param name="znode">要添加的子节点。EN: The child node to add.</param>
        /// <exception cref="ArgumentException">子节点为 <see langword="null"/>。EN: The child node is <see langword="null"/>.</exception>
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
            if (IsLeafNode) IsLeafNode = false;
        }

        /// <summary>
        /// 从当前节点的直接子节点中删除指定节点。
        /// EN: Removes the specified node from the current node's direct children.
        /// </summary>
        /// <param name="znode">要删除的子节点。EN: The child node to remove.</param>
        /// <returns>删除成功时为 <see langword="true"/>；否则为 <see langword="false"/>。EN: <see langword="true"/> when the node was removed; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentException">子节点为 <see langword="null"/>。EN: The child node is <see langword="null"/>.</exception>
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
                    if (ZNodeChildren.Count == 0) IsLeafNode = true;
                    return true;
                }
            }
            return false;
        }





        /// <summary>
        /// 删除树中的指定节点；根节点不能删除。
        /// EN: Removes the specified node from the tree; the root node cannot be removed.
        /// </summary>
        /// <param name="znode">要删除的节点。EN: The node to remove.</param>
        /// <param name="isCheckTreeList">是否先确认节点属于当前树；频繁调用且归属已知时可设为 <see langword="false"/>。EN: Whether to verify that the node belongs to this tree; set to <see langword="false"/> for repeated calls when ownership is already known.</param>
        /// <param name="isPromoteChildren">删除后是否将该节点的子节点提升到其父节点。EN: Whether to promote the removed node's children to its parent.</param>
        /// <returns>完成删除时为 <see langword="true"/>；否则为 <see langword="false"/>。EN: <see langword="true"/> when removal succeeds; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentException">节点为 <see langword="null"/>。EN: The node is <see langword="null"/>.</exception>
        public bool RemoveAny([System.Diagnostics.CodeAnalysis.NotNull] ZNode znode, bool isCheckTreeList = true, bool isPromoteChildren = false)
        {
            if (znode == null)
            {
                throw new ArgumentException("znode is null");
            }
            if (!isCheckTreeList || (ToList()?.Contains(znode) ?? false))
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
        /// 删除树中所有符合条件的节点。
        /// EN: Removes every node in the tree that matches the predicate.
        /// </summary>
        /// <param name="removeNodeFilterFunc">用于选择待删除节点的函数。EN: The predicate used to select nodes for removal.</param>
        /// <param name="isPromoteChildren">删除后是否将被删除节点的子节点提升到其父节点。EN: Whether to promote each removed node's children to its parent.</param>
        /// <returns>至少删除一个节点时为 <see langword="true"/>；否则为 <see langword="false"/>。EN: <see langword="true"/> when at least one node was removed; otherwise, <see langword="false"/>.</returns>
        public bool RemoveAny([System.Diagnostics.CodeAnalysis.NotNull] Func<ZNode, bool> removeNodeFilterFunc, bool isPromoteChildren = false)
        {
            List<ZNode> willRemoveNodeList = new List<ZNode>();
            foreach (ZNode tempNode in this)
            {
                if (removeNodeFilterFunc(tempNode))
                {
                    willRemoveNodeList.Add(tempNode);
                }
            }
            foreach (ZNode tempNode in willRemoveNodeList)
            {
                RemoveAny(tempNode, false, isPromoteChildren);
            }
            return willRemoveNodeList.Count > 0;
        }


        /// <summary>
        /// 按树的迭代顺序将当前树转换为列表。
        /// EN: Converts the current tree to a list in tree enumeration order.
        /// </summary>
        /// <returns>包含当前节点及全部后代节点的列表。EN: A list containing the current node and all descendant nodes.</returns>
        public List<ZNode> ToList()
        {
            List<ZNode> zNodes = new List<ZNode>();
            foreach (ZNode node in this)
            {
                zNodes.Add(node);
            }
            return zNodes;
        }

        /// <summary>
        /// 获取树中的叶子节点；如果树中只有根节点，则根节点也视为叶子节点。
        /// EN: Gets the leaf nodes; when the tree contains only its root, the root is treated as a leaf.
        /// </summary>
        /// <returns>叶子节点列表。EN: The list of leaf nodes.</returns>
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
        /// 使用指定函数筛选叶子节点，仅保留匹配叶子及其父节点链；如果没有叶子匹配，则只保留根节点。
        /// EN: Filters leaf nodes, retaining only matching leaves and their ancestor chains; when no leaf matches, only the root is retained.
        /// </summary>
        /// <param name="leafNodeFilterFunc">用于选择要保留叶子节点的函数；调用方负责处理函数中的异常。EN: The predicate used to select leaves to retain; the caller is responsible for exceptions raised by the predicate.</param>
        /// <param name="filterNodeDataType">可选的匹配节点数据类型标识，仅设置到匹配叶子，不设置到其父节点。EN: An optional data type identifier applied only to matching leaves, not to retained ancestors.</param>
        /// <returns>新的筛选结果树；当前树不会被修改。EN: A new filtered tree; the current tree is not modified.</returns>
        public ZNode FilterLeafNode([System.Diagnostics.CodeAnalysis.NotNull] Func<ZNode, bool> leafNodeFilterFunc, string filterNodeDataType = null)
        {
            ZNode filterResultNode = DeepClone();
            List<ZNode> leafs = filterResultNode.GetLeafNodeList();
            List<ZNode> skipLeafs = new List<ZNode>();
            List<ZNode> willDelNodeList = filterResultNode.ToList();
            foreach (var tempNode in leafs)
            {
                if (skipLeafs.Contains(tempNode))
                {
                    continue;
                }
                if (leafNodeFilterFunc(tempNode))
                {
                    willDelNodeList.Remove(tempNode);
                    if (!string.IsNullOrEmpty(filterNodeDataType))
                    {
                        tempNode.NodeDataType = filterNodeDataType;
                    }
                    if (tempNode.ParentZNode != null)
                    {
                        //如果该叶子需要保留，则统一先处理他的兄弟节点，提前删除掉，避免反复遍历(因为如果后面发现兄弟节点也符合条件会反复保留其父节点)
                        foreach (ZNode brotherNode in tempNode.ParentZNode.ZNodeChildren)
                        {
                            if (brotherNode == tempNode)
                            {
                                continue;
                            }
                            if (brotherNode.HasChildren)
                            {
                                continue;
                            }
                            if (leafNodeFilterFunc(brotherNode))
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
                        while (tempParent != null)
                        {
                            willDelNodeList.Remove(tempParent);
                            tempParent = tempParent.ParentZNode;
                        }
                    }
                }
            }

            foreach (ZNode delNode in willDelNodeList)
            {
                filterResultNode.RemoveAny(delNode, false);
            }
            return filterResultNode;
        }

        /// <summary>
        /// 创建当前节点树的深度克隆。
        /// EN: Creates a deep clone of the current node tree.
        /// </summary>
        /// <returns>克隆后的节点树。EN: The cloned node tree.</returns>
        public object Clone()
        {
            return DeepClone();
        }

        /// <summary>
        /// 深度克隆当前节点树，并尽量避免保留对源树成员的引用。
        /// EN: Deep-clones the current node tree while avoiding references to members of the source tree where possible.
        /// </summary>
        /// <returns>克隆后的根节点，其结构版本重置为零。EN: The cloned root node with its structural version reset to zero.</returns>
        public ZNode DeepClone()
        {
            ZNode cloneNode = new ZNode(null, Path, Value, Type);
            cloneNode.NodeDataType = NodeDataType;
            cloneNode.ParentZNode = null;//被克隆出来的node不保有父级关系
            if (cloneNode.Tag is ICloneable)
            {
                cloneNode.Tag = ((ICloneable)Tag).Clone();
            }
            else
            {
                cloneNode.Tag = Tag;
            }
            cloneNode._version = 0;//克隆Znode版本全部重置为0
            if (ZNodeChildren?.Count > 0)
            {
                cloneNode.ZNodeChildren = new List<ZNode>();
                foreach (ZNode childNode in ZNodeChildren)
                {
                    cloneNode.AddChildren(childNode.DeepClone());
                }
            }
            return cloneNode;
        }

        #region 迭代器实现

        /// <summary>
        /// 返回按层级遍历当前节点树的枚举器。
        /// EN: Returns an enumerator that traverses the current node tree level by level.
        /// </summary>
        /// <returns>树节点枚举器。EN: An enumerator over the tree nodes.</returns>
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
        /// <summary>
        /// 获取当前节点是否已经释放。
        /// EN: Gets whether the current node has been disposed.
        /// </summary>
        public bool IsDisposed
        {
            get;
            private set;
        } = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (HasChildren)
                {
                    foreach (var child in ZNodeChildren)
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

        /// <summary>
        /// 释放当前节点及其后代节点持有的资源。
        /// EN: Releases resources held by the current node and its descendants.
        /// </summary>
        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

    }
}
