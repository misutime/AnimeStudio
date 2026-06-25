using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio
{
    public sealed class LODGroup : Component
    {
        private const int MaxArrayCount = 100_000;
        private const int MaxLodCount = 256;
        private const int MaxRendererCountPerLod = 4096;

        public List<LODLevel> m_LODs { get; } = new List<LODLevel>();

        public LODGroup(ObjectReader reader) : base(reader)
        {
            var oldPosition = reader.Position;
            try
            {
                if (reader.serializedType?.m_Type?.m_Nodes != null)
                {
                    ReadRendererRefsFromTypeTree(reader);
                }

                if (!HasRendererRefs())
                {
                    ReadRendererRefsFromKnownLayout(reader);
                }
            }
            catch (Exception e) when (e is EndOfStreamException
                                      or InvalidDataException
                                      or ArgumentOutOfRangeException
                                      or IndexOutOfRangeException
                                      or OverflowException)
            {
                // LODGroup 只用于还原可见 Renderer 关系；解析失败时保留空列表，
                // 不能让一个可选组件打断模型、材质、骨骼的主导出流程。
                m_LODs.Clear();
                Logger.Debug($"Unable to parse LODGroup renderer refs for {reader.m_PathID} in {reader.assetsFile?.fileName}: {e.Message}");
            }
            finally
            {
                reader.Position = oldPosition;
            }
        }

        public IEnumerable<PPtr<Renderer>> GetRenderersAfterFirstLod()
        {
            return m_LODs
                .Skip(1)
                .SelectMany(x => x.Renderers)
                .Where(x => x != null && !x.IsNull);
        }

        private void ReadRendererRefsFromTypeTree(ObjectReader reader)
        {
            reader.Reset();
            var nodes = reader.serializedType.m_Type.m_Nodes;
            for (var i = 1; i < nodes.Count; i++)
            {
                ReadNode(nodes, reader, ref i, string.Empty);
            }
        }

        private void ReadRendererRefsFromKnownLayout(ObjectReader reader)
        {
            m_LODs.Clear();
            reader.Reset();

            if (platform == BuildTarget.NoTarget)
            {
                reader.ReadUInt32();
            }

            _ = new PPtr<GameObject>(reader);
            // Unity 2019 LODGroup 的公开字段顺序：
            // m_LocalReferencePoint(Vector3)、m_Size(float)、m_LODs(vector<LOD>)。
            // 后面的 FadeMode 等字段不影响“哪些 Renderer 属于低级 LOD”的关系。
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();
            reader.ReadSingle();

            var lodCount = reader.ReadInt32();
            if (lodCount < 0 || lodCount > MaxLodCount)
            {
                throw new InvalidDataException($"Invalid LODGroup lod count {lodCount}.");
            }

            for (var lodIndex = 0; lodIndex < lodCount; lodIndex++)
            {
                reader.ReadSingle();
                reader.ReadSingle();

                var rendererCount = reader.ReadInt32();
                if (rendererCount < 0 || rendererCount > MaxRendererCountPerLod)
                {
                    throw new InvalidDataException($"Invalid LODGroup renderer count {rendererCount} at LOD {lodIndex}.");
                }

                var lod = new LODLevel();
                for (var rendererIndex = 0; rendererIndex < rendererCount; rendererIndex++)
                {
                    var ptr = new PPtr<Renderer>(reader);
                    if (!ptr.IsNull)
                    {
                        lod.Renderers.Add(ptr);
                    }
                }
                m_LODs.Add(lod);
            }

            if (!HasRendererRefs())
            {
                m_LODs.Clear();
            }
        }

        private bool HasRendererRefs()
        {
            return m_LODs.Any(x => x.Renderers.Count > 0);
        }

        private void ReadNode(List<TypeTreeNode> nodes, ObjectReader reader, ref int index, string parentPath)
        {
            ValidateNodeIndex(nodes, index, parentPath);
            var node = nodes[index];
            var path = string.IsNullOrEmpty(parentPath) ? node.m_Name : $"{parentPath}.{node.m_Name}";
            var align = (node.m_MetaFlag & 0x4000) != 0;

            if (node.m_Type != null && node.m_Type.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                var ptr = new PPtr<AnimeStudio.Object>(reader);
                CaptureRendererRef(path, ptr);
                index = GetNodeEnd(nodes, index, path) - 1;
                if (align)
                {
                    reader.AlignStream();
                }
                return;
            }

            switch (node.m_Type)
            {
                case "SInt8":
                case "UInt8":
                case "bool":
                    reader.ReadByte();
                    break;
                case "char":
                case "short":
                case "SInt16":
                case "UInt16":
                case "unsigned short":
                    reader.ReadBytes(2);
                    break;
                case "int":
                case "SInt32":
                case "UInt32":
                case "unsigned int":
                case "Type*":
                case "float":
                    reader.ReadBytes(4);
                    break;
                case "long long":
                case "SInt64":
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                case "double":
                    reader.ReadBytes(8);
                    break;
                case "string":
                    reader.ReadAlignedString();
                    index = GetNodeEnd(nodes, index, path) - 1;
                    break;
                case "TypelessData":
                    {
                        var size = reader.ReadInt32();
                        if (size < 0 || size > reader.BytesLeft())
                        {
                            throw new InvalidDataException($"Invalid LODGroup TypelessData size {size} at {path}.");
                        }
                        reader.ReadBytes(size);
                        index = GetNodeEnd(nodes, index, path) - 1;
                        break;
                    }
                case "map":
                    ReadMap(nodes, reader, ref index, path);
                    break;
                default:
                    if (index < nodes.Count - 1 && nodes[index + 1].m_Type == "Array")
                    {
                        ReadArray(nodes, reader, ref index, path);
                    }
                    else
                    {
                        var end = GetNodeEnd(nodes, index, path);
                        for (var child = index + 1; child < end; child++)
                        {
                            ReadNode(nodes, reader, ref child, path);
                        }
                        index = end - 1;
                    }
                    break;
            }

            if (align)
            {
                reader.AlignStream();
            }
        }

        private void ReadArray(List<TypeTreeNode> nodes, ObjectReader reader, ref int index, string path)
        {
            ValidateNodeIndex(nodes, index, path);
            ValidateNodeIndex(nodes, index + 1, path);
            var arrayAlign = (nodes[index + 1].m_MetaFlag & 0x4000) != 0;
            var vector = GetNodes(nodes, index, path);
            if (vector.Count <= 3)
            {
                throw new InvalidDataException($"Invalid LODGroup array TypeTree at {path}.");
            }

            index += vector.Count - 1;
            var size = reader.ReadInt32();
            if (size < 0 || size > MaxArrayCount)
            {
                throw new InvalidDataException($"Invalid LODGroup array size {size} at {path}.");
            }

            for (var item = 0; item < size; item++)
            {
                var dataIndex = 3;
                ReadNode(vector, reader, ref dataIndex, $"{path}[{item}]");
            }

            if (arrayAlign)
            {
                reader.AlignStream();
            }
        }

        private void ReadMap(List<TypeTreeNode> nodes, ObjectReader reader, ref int index, string path)
        {
            var map = GetNodes(nodes, index, path);
            if (map.Count <= 4)
            {
                throw new InvalidDataException($"Invalid LODGroup map TypeTree at {path}.");
            }

            index += map.Count - 1;
            var first = GetNodes(map, 4, path + ".key");
            var next = 4 + first.Count;
            if (next >= map.Count)
            {
                throw new InvalidDataException($"Invalid LODGroup map TypeTree at {path}: missing value node.");
            }
            var second = GetNodes(map, next, path + ".value");
            var size = reader.ReadInt32();
            if (size < 0 || size > MaxArrayCount)
            {
                throw new InvalidDataException($"Invalid LODGroup map size {size} at {path}.");
            }

            for (var item = 0; item < size; item++)
            {
                var keyIndex = 0;
                ReadNode(first, reader, ref keyIndex, $"{path}.key[{item}]");
                var valueIndex = 0;
                ReadNode(second, reader, ref valueIndex, $"{path}.value[{item}]");
            }
        }

        private void CaptureRendererRef(string path, PPtr<AnimeStudio.Object> ptr)
        {
            if (ptr.IsNull
                || path.IndexOf("m_LODs[", StringComparison.OrdinalIgnoreCase) < 0
                || path.IndexOf("renderer", StringComparison.OrdinalIgnoreCase) < 0
                || !TryGetLodIndex(path, out var lodIndex))
            {
                return;
            }

            while (m_LODs.Count <= lodIndex)
            {
                m_LODs.Add(new LODLevel());
            }
            m_LODs[lodIndex].Renderers.Add(new PPtr<Renderer>(ptr.m_FileID, ptr.m_PathID, assetsFile));
        }

        private static bool TryGetLodIndex(string path, out int lodIndex)
        {
            lodIndex = 0;
            var start = path.IndexOf("m_LODs[", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            start += "m_LODs[".Length;
            var end = path.IndexOf(']', start);
            return end > start && int.TryParse(path.Substring(start, end - start), out lodIndex);
        }

        private static List<TypeTreeNode> GetNodes(List<TypeTreeNode> nodes, int index, string path)
        {
            var end = GetNodeEnd(nodes, index, path);
            var count = end - index;
            if (count <= 0)
            {
                throw new InvalidDataException($"Invalid LODGroup TypeTree range at {path}.");
            }
            return nodes.GetRange(index, count);
        }

        private static int GetNodeEnd(List<TypeTreeNode> nodes, int index, string path)
        {
            ValidateNodeIndex(nodes, index, path);
            var level = nodes[index].m_Level;
            var end = index + 1;
            while (end < nodes.Count && nodes[end].m_Level > level)
            {
                end++;
            }
            return end;
        }

        private static void ValidateNodeIndex(List<TypeTreeNode> nodes, int index, string path)
        {
            if (nodes == null || nodes.Count == 0)
            {
                throw new InvalidDataException($"Invalid LODGroup TypeTree at {path}: node list is empty.");
            }
            if (index < 0 || index >= nodes.Count)
            {
                throw new InvalidDataException($"Invalid LODGroup TypeTree index at {path}: index={index}, count={nodes.Count}.");
            }
        }
    }

    public sealed class LODLevel
    {
        public List<PPtr<Renderer>> Renderers { get; } = new List<PPtr<Renderer>>();
    }
}
