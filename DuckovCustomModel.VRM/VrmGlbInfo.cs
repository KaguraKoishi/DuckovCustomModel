using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 直接从 .vrm（GLB 容器）里读元数据与内嵌缩略图，不经过 UniVRM 的完整加载。
    ///
    /// 用途：
    ///   1. 扫描阶段同步拿到显示名 / 作者 / 缩略图（不用等几百 MB 的模型加载完）；
    ///   2. 顺带统计弹簧与碰撞体数量，用于日志与「胸部保护」的预判。
    ///
    /// 实现上只读 JSON chunk 与缩略图对应的 bufferView 区段，不会把整个 GLB 读进内存，
    /// 因此对于几百 MB 的模型也是毫秒级。
    /// </summary>
    public sealed class VrmGlbInfo
    {
        private const int ChunkTypeJson = 0x4E4F534A; // 'JSON'
        private const int ChunkTypeBin = 0x004E4942;  // 'BIN\0'

        /// <summary>模型显示名（VRM1 meta.name / VRM0 meta.title）。</summary>
        public string? Name;

        /// <summary>作者（VRM1 meta.authors[0] / VRM0 meta.author）。</summary>
        public string? Author;

        /// <summary>内嵌缩略图的原始字节（PNG / JPEG，<c>Texture2D.LoadImage</c> 可直接吃）。</summary>
        public byte[]? Thumbnail;

        /// <summary>VRM 版本：0 或 1；-1 表示不是 VRM。</summary>
        public int VrmVersion = -1;

        /// <summary>弹簧条数（VRM1 springs / VRM0 boneGroups）。</summary>
        public int SpringCount;

        /// <summary>弹簧关节总数（VRM1 springs[].joints / VRM0 boneGroups[].bones）。</summary>
        public int JointCount;

        /// <summary>弹簧碰撞体总数（VRM1 colliders / VRM0 colliderGroups[].colliders）。</summary>
        public int ColliderCount;

        /// <summary>是否使用了 VRMC_springBone_limit 角限位扩展。</summary>
        public bool HasAngleLimit;

        /// <summary>是否使用了 VRMC_springBone_extended_collider（inside* 碰撞体）。</summary>
        public bool HasExtendedCollider;

        /// <summary>
        /// 静态估算的「头顶高度」（模型本地单位，从根节点量起）。
        /// 只用来在模型还没被加载时先给 DCM 一个身高基准值；模型真正加载后会被骨骼实测值覆盖。
        /// </summary>
        public float HeadHeight;

        public static bool TryRead(string path, out VrmGlbInfo info)
        {
            info = new VrmGlbInfo();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = new byte[12];
                if (!ReadExactly(fs, header, header.Length)) return false;
                if (header[0] != (byte)'g' || header[1] != (byte)'l' || header[2] != (byte)'T' || header[3] != (byte)'F')
                    return false;

                var json = ReadChunks(fs, out var binChunkOffset);
                if (json == null) return false;

                var root = JObject.Parse(json);
                ReadSpringBoneStats(root, info);
                ReadHeadHeight(root, info);

                if (!TryGetMeta(root, out var name, out var author, out var thumbnailIndex)) return true;

                info.Name = name;
                info.Author = author;
                info.Thumbnail = ReadThumbnail(fs, root, binChunkOffset, thumbnailIndex);
                return true;
            }
            catch (Exception e)
            {
                VrmLog.Warn($"解析 {Path.GetFileName(path)} 元数据失败: {e.Message}");
                return false;
            }
        }

        /// <summary>遍历 GLB chunk，返回 JSON 文本，并记下 BIN chunk 的起始偏移。</summary>
        private static string? ReadChunks(FileStream fs, out long binChunkOffset)
        {
            binChunkOffset = -1;
            string? json = null;

            while (fs.Position < fs.Length)
            {
                var header = new byte[8];
                if (!ReadExactly(fs, header, header.Length)) break;

                var length = BitConverter.ToInt32(header, 0);
                var type = BitConverter.ToInt32(header, 4);

                if (type == ChunkTypeJson)
                {
                    var bytes = new byte[length];
                    if (!ReadExactly(fs, bytes, length)) break;
                    json = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    if (type == ChunkTypeBin && binChunkOffset < 0) binChunkOffset = fs.Position;
                    if (length > 0) fs.Seek(length, SeekOrigin.Current);
                }
            }

            return json;
        }

        private static void ReadSpringBoneStats(JObject root, VrmGlbInfo info)
        {
            if (!(root["extensions"] is JObject exts)) return;

            if (exts["VRMC_vrm"] != null) info.VrmVersion = 1;
            else if (exts["VRM"] != null) info.VrmVersion = 0;

            info.HasAngleLimit = exts["VRMC_springBone_limit"] != null;
            info.HasExtendedCollider = exts["VRMC_springBone_extended_collider"] != null;

            // VRM 1.0：extensions.VRMC_springBone { springs[], colliders[], colliderGroups[] }
            if (exts["VRMC_springBone"] is JObject sb1)
            {
                if (sb1["springs"] is JArray springs)
                {
                    info.SpringCount = springs.Count;
                    foreach (var spring in springs)
                        if (spring["joints"] is JArray joints)
                            info.JointCount += joints.Count;
                }

                if (sb1["colliders"] is JArray colliders) info.ColliderCount = colliders.Count;
                return;
            }

            // VRM 0.x：extensions.VRM.secondaryAnimation { boneGroups[], colliderGroups[] }
            if (exts["VRM"]?["secondaryAnimation"] is JObject sa)
            {
                if (sa["boneGroups"] is JArray groups)
                {
                    info.SpringCount = groups.Count;
                    foreach (var group in groups)
                        if (group["bones"] is JArray bones)
                            info.JointCount += bones.Count;
                }

                if (sa["colliderGroups"] is JArray cg)
                    foreach (var group in cg)
                        if (group["colliders"] is JArray cols)
                            info.ColliderCount += cols.Count;
            }
        }

        /// <summary>
        /// 从 VRM 的人形骨骼定义里静态估算身高：沿节点链把平移累加起来求出
        /// <c>hips</c> 与 <c>head</c> 的世界 Y，再按「头顶 ≈ 头骨 + 0.55 倍髋→头距离」补上颅顶。
        ///
        /// 只沿着链累加平移与缩放、忽略旋转 —— 对标准的 VRM 骨架足够准
        /// （人形骨骼链在导入前几乎都是轴对齐的），而这个值本来也只是「加载前的占位」。
        /// </summary>
        private static void ReadHeadHeight(JObject root, VrmGlbInfo info)
        {
            try
            {
                if (!(root["extensions"] is JObject exts)) return;

                var humanBones = exts["VRMC_vrm"]?["humanoid"]?["humanBones"] as JObject
                                 ?? exts["VRM"]?["humanoid"]?["humanBones"] as JObject;
                if (humanBones == null) return;

                var headNode = humanBones["head"]?["node"]?.Value<int?>() ?? -1;
                var hipsNode = humanBones["hips"]?["node"]?.Value<int?>() ?? -1;
                if (headNode < 0 || hipsNode < 0) return;

                if (!(root["nodes"] is JArray nodes) || nodes.Count == 0) return;

                var worldY = ComputeNodeWorldY(root, nodes);
                if (worldY.Count == 0) return;
                if (!worldY.TryGetValue(headNode, out var headY)) return;
                if (!worldY.TryGetValue(hipsNode, out var hipsY)) return;

                var unit = headY - hipsY;
                if (unit <= 0.0001f) return;

                info.HeadHeight = headY + unit * 0.55f;
            }
            catch (Exception e)
            {
                VrmLog.Detail($"静态估算头高失败（忽略）: {e.Message}");
            }
        }

        /// <summary>沿节点树累加平移 / 缩放，求出若干节点的世界 Y（忽略旋转，估算够用）。</summary>
        private static Dictionary<int, float> ComputeNodeWorldY(JObject root, JArray nodes)
        {
            var parent = new int[nodes.Count];
            var children = new List<int>[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                parent[i] = -1;
                children[i] = new List<int>();
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                if (!(nodes[i]?["children"] is JArray kids)) continue;
                foreach (var kid in kids)
                {
                    var index = kid.Value<int>();
                    if (index < 0 || index >= nodes.Count) continue;
                    parent[index] = i;
                    children[i].Add(index);
                }
            }

            var roots = new List<int>();
            if (root["scenes"] is JArray scenes && scenes.Count > 0 &&
                scenes[0]?["nodes"] is JArray sceneNodes)
            {
                foreach (var n in sceneNodes)
                {
                    var index = n.Value<int>();
                    if (index >= 0 && index < nodes.Count) roots.Add(index);
                }
            }

            if (roots.Count == 0)
                for (var i = 0; i < nodes.Count; i++)
                    if (parent[i] < 0)
                        roots.Add(i);

            var result = new Dictionary<int, float>(Math.Max(2, nodes.Count));
            var stack = new Stack<(int Index, float ParentY, float ParentScaleY)>();
            foreach (var r in roots) stack.Push((r, 0f, 1f));

            while (stack.Count > 0)
            {
                var (index, parentY, parentScaleY) = stack.Pop();

                var localY = 0f;
                var localScaleY = 1f;

                if (nodes[index] is JObject node)
                {
                    if (node["matrix"] is JArray matrix && matrix.Count >= 16)
                    {
                        // glTF 的 matrix 是列主序：平移在 12/13/14，Y 轴缩放是第 2 列的模长。
                        localY = matrix[13].Value<float>();
                        float sx = matrix[4].Value<float>(), sy = matrix[5].Value<float>(),
                            sz = matrix[6].Value<float>();
                        localScaleY = (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
                    }
                    else
                    {
                        if (node["translation"] is JArray t && t.Count >= 3) localY = t[1].Value<float>();
                        if (node["scale"] is JArray s && s.Count >= 3) localScaleY = s[1].Value<float>();
                    }
                }

                var scaleY = parentScaleY * localScaleY;
                result[index] = parentY + parentScaleY * localY;

                foreach (var child in children[index]) stack.Push((child, result[index], scaleY));
            }

            return result;
        }

        private static bool TryGetMeta(JObject root, out string? name, out string? author, out int thumbnailIndex)
        {
            name = null;
            author = null;
            thumbnailIndex = -1;

            if (!(root["extensions"] is JObject exts)) return false;

            // VRM 1.0：extensions.VRMC_vrm.meta { name, authors[], thumbnailImage }
            var meta1 = exts["VRMC_vrm"]?["meta"];
            if (meta1 != null)
            {
                name = meta1.Value<string>("name");
                author = meta1["authors"]?.First?.Value<string>();
                thumbnailIndex = meta1.Value<int?>("thumbnailImage") ?? -1;
                return true;
            }

            // VRM 0.x：extensions.VRM.meta { title, author, texture }
            var meta0 = exts["VRM"]?["meta"];
            if (meta0 != null)
            {
                name = meta0.Value<string>("title");
                author = meta0.Value<string>("author");
                thumbnailIndex = meta0.Value<int?>("texture") ?? -1;
                return true;
            }

            return false;
        }

        private static byte[]? ReadThumbnail(FileStream fs, JObject root, long binChunkOffset, int imageIndex)
        {
            if (imageIndex < 0 || binChunkOffset < 0) return null;
            if (!(root["images"] is JArray images) || imageIndex >= images.Count) return null;
            var image = images[imageIndex];

            // 常规路径：bufferView 指向 BIN chunk 里的一段
            var bufferViewIndex = image.Value<int?>("bufferView") ?? -1;
            if (bufferViewIndex >= 0)
            {
                var bufferView = root["bufferViews"]?[bufferViewIndex];
                if (bufferView == null) return null;

                var offset = bufferView.Value<long?>("byteOffset") ?? 0;
                var length = bufferView.Value<long?>("byteLength") ?? 0;
                if (length <= 0) return null;

                fs.Seek(binChunkOffset + offset, SeekOrigin.Begin);
                var bytes = new byte[length];
                if (!ReadExactly(fs, bytes, (int)length)) return null;
                return IsSupportedImage(bytes) ? bytes : null;
            }

            // 兜底：data URI
            var uri = image.Value<string>("uri");
            if (!string.IsNullOrEmpty(uri) && uri.StartsWith("data:", StringComparison.Ordinal))
            {
                var comma = uri.IndexOf(',');
                if (comma < 0) return null;
                try
                {
                    var bytes = Convert.FromBase64String(uri.Substring(comma + 1));
                    return IsSupportedImage(bytes) ? bytes : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>只接受 PNG / JPEG —— DCM 用 <c>Texture2D.LoadImage</c> 读图，不支持 WebP 等格式。</summary>
        private static bool IsSupportedImage(byte[] bytes)
        {
            if (bytes.Length < 4) return false;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true; // PNG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true;                                        // JPEG
            return false;
        }

        private static bool ReadExactly(FileStream fs, byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = fs.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }

            return true;
        }
    }
}
