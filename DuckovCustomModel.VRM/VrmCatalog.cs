using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DuckovCustomModel.Core.Data;
using DuckovCustomModel.Managers;
using Newtonsoft.Json;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>目录里一个 .vrm 文件的缓存条目。</summary>
    internal sealed class VrmCatalogEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;

        /// <summary>变更指纹：meta 模式是 "size:ticks"，md5 模式是文件 MD5。</summary>
        public string Hash { get; set; } = string.Empty;

        public long Size { get; set; }
        public long LastWriteTicks { get; set; }

        /// <summary>预览图在模型包目录里的相对文件名；空表示该模型没有内嵌缩略图。</summary>
        public string ThumbnailFile { get; set; } = string.Empty;

        public int VrmVersion { get; set; } = -1;
        public int SpringCount { get; set; }
        public int JointCount { get; set; }
        public int ColliderCount { get; set; }

        /// <summary>从 prefab 上量到的头盔定位器高度（缓存下来，避免为了量高度去加载模型）；&lt;=0 表示没有 / 未知。</summary>
        public float HelmetHeight { get; set; }

        /// <summary>prefab 的初始根缩放；null 表示未知。</summary>
        public float[]? RootScale { get; set; }

        [JsonIgnore] public string SourcePath { get; set; } = string.Empty;
    }

    internal sealed class VrmCatalogFile
    {
        public int Version { get; set; } = 1;
        public List<VrmCatalogEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// 源目录索引：<c>ModConfigs/DuckovCustomModel.VRM/VRM/*.vrm</c> → DCM 模型包
    /// <c>ModConfigs/DuckovCustomModel/Models/VRMRuntime</c>。
    ///
    /// 关键点：<b>扫描阶段绝不加载模型本体</b>。
    ///   1. 用「文件大小 + 修改时间」（或 MD5）比对缓存，没变就直接复用上次解析结果，连 GLB 都不读；
    ///   2. 变了才只读 GLB 的 JSON chunk 头部，拿到名字 / 作者 / 内嵌缩略图 / 弹簧统计（毫秒级）；
    ///   3. 聚合写出一份 bundleinfo.json（内容没变就不写，避免 DCM 重新加载）。
    ///
    /// 真正的 UniVRM 加载 <b>只在用户选中某个模型时</b>才发生（见 <see cref="VrmModelRegistry"/>）。
    /// </summary>
    internal static class VrmCatalog
    {
        public const string VrmModelIdPrefix = "vrm:";

        /// <summary>所有 VRM 共用的 DCM 模型包目录名。</summary>
        private const string BundleFolderName = "VRMRuntime";
        private const string BundleDisplayName = "VRM Runtime";

        /// <summary>写到模型包里的 AB 文件名。内容是一份真实 AssetBundle，保证 DCM 的文件存在性 / 哈希校验能过。</summary>
        private const string PlaceholderBundleName = "vrm_runtime";

        /// <summary>旧版实现（每个模型一个包）生成的目录前缀，启动时归档掉。</summary>
        private const string LegacyFolderPrefix = "VRM_";

        private const string CatalogFileName = "vrm_catalog.json";
        private const string ThumbnailPrefix = "thumb_";

        private static readonly List<VrmCatalogEntry> EntriesInternal = new();
        private static bool _scanning;

        public static IReadOnlyList<VrmCatalogEntry> Entries => EntriesInternal;

        public static string BundleDirectory => Path.Combine(ModelManager.ModelsDirectory, BundleFolderName);

        private static string CatalogPath => Path.Combine(VrmModPath.ModConfigDirectory, CatalogFileName);

        public static bool IsVrmModel(ModelInfo? modelInfo)
        {
            return modelInfo != null
                   && !string.IsNullOrEmpty(modelInfo.ModelID)
                   && modelInfo.ModelID.StartsWith(VrmModelIdPrefix, StringComparison.Ordinal);
        }

        public static VrmCatalogEntry? Find(string modelId)
        {
            foreach (var entry in EntriesInternal)
                if (string.Equals(entry.ModelId, modelId, StringComparison.Ordinal))
                    return entry;

            return null;
        }

        public static bool Contains(string modelId) => Find(modelId) != null;

        /// <summary>模型文件此刻是否真的存在于源目录（用于 DCM 的"模型可用性"判断，不需要加载本体）。</summary>
        public static bool IsAvailable(ModelInfo? modelInfo)
        {
            if (!IsVrmModel(modelInfo)) return false;
            var entry = Find(modelInfo!.ModelID);
            return entry != null && File.Exists(entry.SourcePath);
        }

        public static bool TryGetSourcePath(string modelId, out string path)
        {
            path = string.Empty;
            var entry = Find(modelId);
            if (entry == null || !File.Exists(entry.SourcePath)) return false;
            path = entry.SourcePath;
            return true;
        }

        /// <summary>模型包目录里预览图的名字（相对路径），供 DCM 显示。</summary>
        public static string ThumbnailRelativePath(VrmCatalogEntry entry) => entry.ThumbnailFile;

        /// <summary>
        /// 扫描源目录并同步模型包。返回 bundleinfo 是否真的发生了变化
        /// （变化过才需要让 DCM 重新列模型——否则会白刷一遍列表）。
        /// </summary>
        public static bool Refresh()
        {
            if (_scanning) return false;
            _scanning = true;
            try
            {
                return RefreshInternal();
            }
            finally
            {
                _scanning = false;
            }
        }

        private static bool RefreshInternal()
        {
            string dir;
            try
            {
                dir = VrmModPath.SourceDirectory;
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                VrmLog.Error($"无法准备 VRM 目录: {e.Message}");
                return false;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.vrm", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                VrmLog.Error($"枚举 VRM 目录失败: {e.Message}");
                return false;
            }

            var cache = LoadCache();
            var newEntries = new List<VrmCatalogEntry>(files.Length);
            var parsed = 0;
            var reused = 0;

            foreach (var file in files)
            {
                try
                {
                    var entry = BuildEntry(file, cache);
                    if (entry == null) continue;
                    if (entry.ParsedNow) parsed++;
                    else reused++;
                    newEntries.Add(entry.Entry!);
                }
                catch (Exception e)
                {
                    VrmLog.Warn($"处理 {Path.GetFileName(file)} 失败: {e.Message}");
                }
            }

            ArchiveLegacyBundleFolders();

            if (newEntries.Count == 0)
            {
                EntriesInternal.Clear();
                SaveCache(newEntries);
                RemoveBundleFolder();
                VrmLog.Info("VRM 源目录为空，已移除注册目录");
                return true;
            }

            // 名称 / 模型集合变化时给个提示
            var changedSet = !SameModelSet(EntriesInternal, newEntries);
            EntriesInternal.Clear();
            EntriesInternal.AddRange(newEntries);
            SaveCache(newEntries);

            if (parsed > 0)
                VrmLog.Info($"VRM 目录扫描：{newEntries.Count} 个模型（新解析 {parsed}，缓存命中 {reused}）");
            else
                VrmLog.Detail($"VRM 目录扫描：{newEntries.Count} 个模型，全部命中缓存");

            CleanupThumbnails();

            var bundleChanged = WriteBundleInfoToDisk();
            if (changedSet && !bundleChanged) bundleChanged = true;
            return bundleChanged;
        }

        private sealed class BuildResult
        {
            public VrmCatalogEntry? Entry;
            public bool ParsedNow;
        }

        /// <summary>构造（或复用）一个条目的信息；只有指纹变了或预览图缺失才真正读 GLB。</summary>
        private static BuildResult? BuildEntry(string file, VrmCatalogFile cache)
        {
            var fileName = Path.GetFileName(file);
            var modelId = VrmModelIdPrefix + Path.GetFileNameWithoutExtension(file);

            long size;
            long ticks;
            try
            {
                var info = new FileInfo(file);
                size = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e)
            {
                VrmLog.Warn($"读取 {fileName} 文件信息失败: {e.Message}");
                return null;
            }

            var options = VrmConfig.Current;
            var cached = cache.Entries.FirstOrDefault(e =>
                string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));

            var hash = options.UseMd5 ? ComputeMd5(file) : $"{size}:{ticks}";

            var canReuse = cached != null
                           && string.Equals(cached.Hash, hash, StringComparison.Ordinal)
                           && (string.IsNullOrEmpty(cached.ThumbnailFile)
                               || File.Exists(Path.Combine(BundleDirectory, cached.ThumbnailFile)));

            if (canReuse)
            {
                cached!.SourcePath = file;
                return new BuildResult { Entry = cached, ParsedNow = false };
            }

            var entry = new VrmCatalogEntry
            {
                FileName = fileName,
                ModelId = modelId,
                Name = Path.GetFileNameWithoutExtension(file),
                Author = "Unknown",
                Hash = hash,
                Size = size,
                LastWriteTicks = ticks,
                SourcePath = file,
            };

            if (VrmGlbInfo.TryRead(file, out var meta))
            {
                if (!string.IsNullOrWhiteSpace(meta.Name)) entry.Name = meta.Name;
                if (!string.IsNullOrWhiteSpace(meta.Author)) entry.Author = meta.Author;
                entry.VrmVersion = meta.VrmVersion;
                entry.SpringCount = meta.SpringCount;
                entry.JointCount = meta.JointCount;
                entry.ColliderCount = meta.ColliderCount;
                // 静态估算的身高基准：让 DCM 的逐模型身高滑条在模型还没被加载时就出现。
                // 模型真正加载后会用骨骼实测值覆盖（见 RecordPrefabMetrics）。
                if (meta.HeadHeight > 0f && entry.HelmetHeight <= 0f) entry.HelmetHeight = meta.HeadHeight;
                entry.ThumbnailFile = SaveThumbnail(fileName, meta.Thumbnail);
            }
            else
            {
                entry.ThumbnailFile = string.Empty;
            }

            VrmLog.Detail($"解析 {fileName}: name={entry.Name} author={entry.Author} " +
                          $"vrm{entry.VrmVersion} springs={entry.SpringCount} joints={entry.JointCount} " +
                          $"colliders={entry.ColliderCount} height≈{entry.HelmetHeight:0.###} " +
                          $"thumb={(string.IsNullOrEmpty(entry.ThumbnailFile) ? "无" : entry.ThumbnailFile)}");

            return new BuildResult { Entry = entry, ParsedNow = true };
        }

        private static bool SameModelSet(IReadOnlyList<VrmCatalogEntry> a, IReadOnlyList<VrmCatalogEntry> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].ModelId, b[i].ModelId, StringComparison.Ordinal))
                    return false;

            return true;
        }

        #region 缩略图

        private static string SaveThumbnail(string sourceFileName, byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            try
            {
                var fileName = ThumbnailPrefix + SanitizeFileName(sourceFileName) + ".png";
                var path = Path.Combine(BundleDirectory, fileName);
                Directory.CreateDirectory(BundleDirectory);
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    File.WriteAllBytes(path, bytes);
                return fileName;
            }
            catch (Exception e)
            {
                VrmLog.Warn($"保存 {sourceFileName} 预览图失败: {e.Message}");
                return string.Empty;
            }
        }

        /// <summary>删掉模型包目录里已经没人引用的预览图（模型被移除 / 改名后留下的）。</summary>
        private static void CleanupThumbnails()
        {
            try
            {
                if (!Directory.Exists(BundleDirectory)) return;
                var keep = new HashSet<string>(
                    EntriesInternal.Where(e => !string.IsNullOrEmpty(e.ThumbnailFile))
                        .Select(e => e.ThumbnailFile),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.GetFiles(BundleDirectory, ThumbnailPrefix + "*.png"))
                    if (!keep.Contains(Path.GetFileName(file)))
                        File.Delete(file);
            }
            catch (Exception e)
            {
                VrmLog.Detail($"清理旧预览图失败: {e.Message}");
            }
        }

        #endregion

        #region 模型包落盘

        /// <summary>把当前条目聚合成一个 bundleinfo.json。内容没变就不写，避免触发 DCM 重载。返回是否写入了。</summary>
        private static bool WriteBundleInfoToDisk()
        {
            try
            {
                Directory.CreateDirectory(BundleDirectory);
                TryCopyPlaceholderBundle(Path.Combine(BundleDirectory, PlaceholderBundleName));

                var models = new List<ModelInfo>(EntriesInternal.Count);
                foreach (var entry in EntriesInternal)
                    models.Add(new ModelInfo
                    {
                        ModelID = entry.ModelId,
                        Name = entry.Name,
                        Author = entry.Author,
                        Description = $"VRM Runtime: {entry.FileName}",
                        Version = "1.0",
                        PrefabPath = entry.ModelId,
                        ThumbnailPath = ThumbnailRelativePath(entry),
                        TargetTypes = new[] { ModelTargetType.Character },
                        // 必须保持不自动替换 Shader：SodaCharacter 无法表达 MToon 的
                        // alphaMode / 双面渲染，替换后会破坏透明与背面剔除。
                        Features = new[] { ModelFeatures.NoAutoShaderReplace, ModelFeatures.SkipShowBackMaterial },
                    });

                var payload = new ModelBundleInfo
                {
                    BundleName = BundleDisplayName,
                    BundlePath = PlaceholderBundleName,
                    Models = models.ToArray(),
                };

                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                var path = Path.Combine(BundleDirectory, "bundleinfo.json");
                if (File.Exists(path) && File.ReadAllText(path) == json) return false;

                File.WriteAllText(path, json);
                VrmLog.Info($"已注册 {models.Count} 个 VRM 模型到 DCM 模型列表（{BundleFolderName}）");
                return true;
            }
            catch (Exception e)
            {
                VrmLog.Error($"写入 bundleinfo.json 失败: {e.Message}");
                return false;
            }
        }

        private static void TryCopyPlaceholderBundle(string destination)
        {
            try
            {
                var source = VrmShaderBundle.BundlePath;
                if (!File.Exists(source))
                {
                    VrmLog.Warn($"找不到可复制的占位 AB: {source}");
                    return;
                }

                if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length)
                    return;

                File.Copy(source, destination, true);
            }
            catch (Exception e)
            {
                VrmLog.Warn($"占位 AB 复制失败: {e.Message}");
            }
        }

        private static void RemoveBundleFolder()
        {
            try
            {
                if (!Directory.Exists(BundleDirectory)) return;
                Directory.Delete(BundleDirectory, true);
                VrmLog.Info($"没有 VRM 模型，已移除注册目录: {BundleDirectory}");
            }
            catch (Exception e)
            {
                VrmLog.Warn($"移除注册目录失败: {e.Message}");
            }
        }

        /// <summary>
        /// 旧版实现给每个模型单独开一个 <c>VRM_*</c> 目录；模型文件移走后这些目录不会消失，
        /// 于是 DCM 列表里一直挂着失效模型。这里把它们移进 <c>_vrm_legacy_backup</c> 归档
        /// （只动 bundleinfo.json 里全部是 vrm: 模型的目录，用户自己的模型包绝不触碰）。
        /// </summary>
        private static void ArchiveLegacyBundleFolders()
        {
            var root = ModelManager.ModelsDirectory;
            if (!Directory.Exists(root)) return;

            string? backup = null;
            foreach (var dir in Directory.GetDirectories(root, LegacyFolderPrefix + "*"))
            {
                try
                {
                    var infoPath = Path.Combine(dir, "bundleinfo.json");
                    if (!File.Exists(infoPath)) continue;

                    var payload = JsonConvert.DeserializeObject<ModelBundleInfo>(File.ReadAllText(infoPath));
                    if (payload?.Models == null || payload.Models.Length == 0) continue;
                    if (payload.Models.Any(m => m == null || !IsVrmModel(m))) continue;

                    backup ??= Path.Combine(root, "_vrm_legacy_backup");
                    Directory.CreateDirectory(backup);

                    var dest = Path.Combine(backup, Path.GetFileName(dir));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(dir, dest);
                    VrmLog.Info($"旧版注册目录已归档: {Path.GetFileName(dir)} -> _vrm_legacy_backup");
                }
                catch (Exception e)
                {
                    VrmLog.Warn($"归档旧目录 {Path.GetFileName(dir)} 失败: {e.Message}");
                }
            }
        }

        #endregion

        /// <summary>
        /// 刚加载完、还没被 DCM 摆弄过的 prefab 上，量一次「头盔定位高度」与「根缩放」。
        ///
        /// 为什么要在这一刻量：DCM 的 <c>ModelHeightManager</c> 会按身高滑条缩放模型，
        /// 之后再量就把缩放算进去了。prefab 刚加载完时是干净的。
        /// 量到的值缓存下来，prefab 被 LRU 卸载后 DCM 问起来也答得上来，
        /// 而且逐模型的身高滑条会自动出现（条件是 <c>HelmetHeight &gt; 0</c>）。
        /// </summary>
        public static void RecordPrefabMetrics(string modelId, GameObject prefab)
        {
            try
            {
                var entry = Find(modelId);
                if (entry == null || prefab == null) return;

                var changed = false;

                var scale = prefab.transform.localScale;
                if (entry.RootScale == null)
                {
                    entry.RootScale = new[] { scale.x, scale.y, scale.z };
                    changed = true;
                }

                // 优先用骨架：<c>Head</c> 骨骼 + 约 0.55 倍「髋→头」距离 ≈ 头顶。
                var value = MeasureHelmetHeight(prefab);
                if (value > 0f && Mathf.Abs(entry.HelmetHeight - value) > 0.001f)
                {
                    entry.HelmetHeight = value;
                    changed = true;
                }

                if (changed) SaveCache(EntriesInternal);
            }
            catch (Exception e)
            {
                VrmLog.Detail($"记录模型度量失败（忽略）: {e.Message}");
            }
        }

        /// <summary>
        /// 从 prefab 的骨架量一次「头顶高度」（相对 prefab 根，单位就是 prefab 的本地单位）。
        /// 量不到返回 0。用于身高滑条的基准值。
        /// </summary>
        public static float MeasureHelmetHeight(GameObject prefab)
        {
            if (prefab == null) return 0f;
            try
            {
                var vrm = prefab.GetComponentInChildren<UniVRM10.Vrm10Instance>(true);
                var humanoid = vrm != null
                    ? vrm.Humanoid
                    : prefab.GetComponentInChildren<UniHumanoid.Humanoid>(true);

                var head = humanoid?.Head;
                var hips = humanoid?.Hips;
                if (head == null || hips == null) return 0f;

                var unit = Mathf.Max(0.01f, head.position.y - hips.position.y);
                var worldTop = head.position.y + unit * 0.55f;
                var value = worldTop - prefab.transform.position.y;
                return value > 0f ? value : 0f;
            }
            catch (Exception e)
            {
                VrmLog.Detail($"测量头顶高度失败（忽略）: {e.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// 锚点生成后用骨骼算出的「本地头盔高度」做一次校准（比静态估算准）。
        /// 值已经除以了当前世界缩放，所以不受身高滑条影响。
        /// </summary>
        public static void RecordHelmetHeight(string modelId, float localHelmetHeight)
        {
            try
            {
                var entry = Find(modelId);
                if (entry == null || localHelmetHeight <= 0f) return;

                var scaleY = entry.RootScale is { Length: 3 } rs && rs[1] > 0f ? rs[1] : 1f;
                var value = localHelmetHeight * scaleY;
                if (Mathf.Abs(entry.HelmetHeight - value) < 0.001f) return;

                entry.HelmetHeight = value;
                SaveCache(EntriesInternal);
            }
            catch (Exception e)
            {
                VrmLog.Detail($"校准头盔高度失败（忽略）: {e.Message}");
            }
        }

        #region 缓存读写 / 工具

        private static VrmCatalogFile LoadCache()
        {
            try
            {
                var path = CatalogPath;
                if (!File.Exists(path)) return new VrmCatalogFile();
                var data = JsonConvert.DeserializeObject<VrmCatalogFile>(File.ReadAllText(path));
                return data?.Entries != null ? data : new VrmCatalogFile();
            }
            catch (Exception e)
            {
                VrmLog.Detail($"读取目录缓存失败（将重新解析）: {e.Message}");
                return new VrmCatalogFile();
            }
        }

        private static void SaveCache(List<VrmCatalogEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(VrmModPath.ModConfigDirectory);
                var data = new VrmCatalogFile { Version = 1, Entries = entries };
                File.WriteAllText(CatalogPath, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception e)
            {
                VrmLog.Detail($"写入目录缓存失败: {e.Message}");
            }
        }

        private static string ComputeMd5(string path)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                var hash = md5.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch (Exception e)
            {
                VrmLog.Warn($"计算 {Path.GetFileName(path)} MD5 失败: {e.Message}");
                return string.Empty;
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        #endregion
    }
}
