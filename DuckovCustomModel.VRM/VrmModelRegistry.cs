using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cysharp.Threading.Tasks;
using DuckovCustomModel.Core.Data;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DuckovCustomModel.VRM
{
    public enum VrmLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed,
    }

    /// <summary>
    /// VRM 模型的「按需加载」缓存。
    ///
    /// 启动时<b>不</b>加载任何模型 —— 只由 <see cref="VrmCatalog"/> 读 GLB 头部写注册信息，
    /// DCM 列表里就能立刻看到名字与预览图。真正的 UniVRM 加载只在下面两个时机发生：
    ///   * 用户选中了某个模型（<c>ModelHandler.InitializeCustomModel</c>）；
    ///   * 后台预加载当前存档已选中的模型（<see cref="PreloadSelected"/>）。
    ///
    /// 加载好的 prefab 会被缓存（DCM 每换一次模型都会 Instantiate 一次，重复加载代价极高），
    /// 并按 LRU 淘汰；<b>正在被使用的模型不会被卸载</b>
    /// （克隆体上的 <see cref="VrmModelMarker"/> 是判据）。
    /// </summary>
    public static class VrmModelRegistry
    {
        public const string VrmModelIdPrefix = VrmCatalog.VrmModelIdPrefix;

        private static readonly Dictionary<string, GameObject> Prefabs = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Action<GameObject?>>> Pending = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);
        private static readonly LinkedList<string> Lru = new();
        private static readonly HashSet<string> ThumbnailLogged = new(StringComparer.Ordinal);

        public static bool IsVrmModel(ModelInfo? modelInfo) => VrmCatalog.IsVrmModel(modelInfo);

        public static bool TryGetLoadedPrefab(string modelId, out GameObject prefab)
        {
            if (Prefabs.TryGetValue(modelId, out var found) && found != null)
            {
                Touch(modelId);
                prefab = found;
                return true;
            }

            prefab = null!;
            return false;
        }

        public static VrmLoadState GetState(string modelId)
        {
            if (Prefabs.TryGetValue(modelId, out var go) && go != null) return VrmLoadState.Loaded;
            if (Pending.ContainsKey(modelId)) return VrmLoadState.Loading;
            return Failed.Contains(modelId) ? VrmLoadState.Failed : VrmLoadState.NotLoaded;
        }

        /// <summary>请求加载（幂等）。已加载会同步回调；正在加载会排队；已失败会立刻回调 null。</summary>
        public static void RequestLoad(string modelId, Action<GameObject?>? onCompleted = null)
        {
            if (string.IsNullOrEmpty(modelId)) return;

            if (Prefabs.TryGetValue(modelId, out var existing) && existing != null)
            {
                Touch(modelId);
                onCompleted?.Invoke(existing);
                return;
            }

            if (Pending.TryGetValue(modelId, out var waiters))
            {
                if (onCompleted != null) waiters.Add(onCompleted);
                return;
            }

            if (Failed.Contains(modelId))
            {
                // 文件可能已经被修好了（热重载会清掉 Failed），这里再确认一次。
                if (!VrmCatalog.TryGetSourcePath(modelId, out var retryPath))
                {
                    onCompleted?.Invoke(null);
                    return;
                }

                Failed.Remove(modelId);
                VrmLog.Detail($"重新尝试加载 {modelId}: {retryPath}");
            }

            waiters = new List<Action<GameObject?>>();
            if (onCompleted != null) waiters.Add(onCompleted);
            Pending[modelId] = waiters;

            LoadAsync(modelId).Forget();
        }

        private static async UniTaskVoid LoadAsync(string modelId)
        {
            GameObject? result = null;
            try
            {
                if (!VrmCatalog.TryGetSourcePath(modelId, out var path))
                {
                    VrmLog.Warn($"找不到 VRM 源文件: {modelId}");
                }
                else if (!HasEnoughMemoryFor(path))
                {
                    // 内存不够就明确跳过 —— 强行加载会 OOM，把整个游戏进程搞崩
                    //（实测：托管 OOM 之后 D3D11 连 RenderTexture 都分配不出来）。
                }
                else
                {
                    VrmLog.Info($"开始加载模型: {System.IO.Path.GetFileName(path)}");
                    // ⚠ 必须显式关掉 Control Rig：
                    //   默认（Generate）时 UniVRM 会另建一套「Runtime Control Rig」影子骨架，
                    //   并把模型根上 Animator 的 avatar 换成那套影子骨架的 Avatar；真实骨骼
                    //   要靠 Vrm10Runtime.Process() → ControlRig.Process() 回写。
                    //   而 m_useControlRig 是非序列化字段，DCM 用 Object.Instantiate 出来的
                    //   克隆体上恒为 false → 影子骨架不回写 → 任何 AnimatorController 都无效果。
                    //   传 None 后模型根上的 Animator 保留「真实骨骼的 humanoid Avatar」，
                    //   humanoid 动画可以直接驱动真实骨骼，克隆体也一样。
                    var instance = await Vrm10.LoadPathAsync(
                        path: path,
                        canLoadVrm0X: true,
                        controlRigGenerationOption: ControlRigGenerationOption.None,
                        showMeshes: true,
                        awaitCaller: new RuntimeOnlyAwaitCaller(),
                        materialGenerator: new CustomVrmMaterialGenerator());

                    if (instance == null)
                    {
                        VrmLog.Error($"加载失败: {path}");
                    }
                    else
                    {
                        var go = instance.gameObject;
                        go.name = VrmCatalog.Find(modelId)?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
                        // 先固定住，等 DCM Instantiate 到角色身上。保持未激活可以避免它在场景里“飘”一帧。
                        go.SetActive(false);
                        Object.DontDestroyOnLoad(go);

                        // 标记：克隆体据此找回源 prefab（SpringBone 修复要用）。
                        go.AddComponent<VrmModelMarker>().ModelId = modelId;

                        // 趁 prefab 还没被身高系统缩放，量一次头盔高度 / 根缩放写进目录缓存。
                        VrmCatalog.RecordPrefabMetrics(modelId, go);

                        Prefabs[modelId] = go;
                        Touch(modelId);
                        result = go;

                        var entry = VrmCatalog.Find(modelId);
                        VrmLog.Info($"模型已就绪: {go.name}（{go.GetComponentsInChildren<Transform>(true).Length} 个节点" +
                                    (entry == null
                                        ? "）"
                                        : $"，弹簧 {entry.SpringCount} 条 / 关节 {entry.JointCount} 个 / 碰撞体 {entry.ColliderCount} 个）"));
                    }
                }
            }
            catch (Exception e)
            {
                VrmLog.Error($"加载 {modelId} 抛异常:\n{e}");

                if (e is OutOfMemoryException)
                {
                    // 半成品加载会残留大量未托管缓冲，主动回收一次，给游戏本体留条活路。
                    GC.Collect();
                    VrmLog.Warn("内存不足导致加载失败，已触发垃圾回收。请关闭其它占内存的程序，" +
                                "或在 DCM 里改用文件更小的模型。");
                }
            }
            finally
            {
                if (result == null) Failed.Add(modelId);

                if (Pending.TryGetValue(modelId, out var waiters))
                {
                    Pending.Remove(modelId);
                    foreach (var waiter in waiters)
                        try
                        {
                            waiter(result);
                        }
                        catch (Exception e)
                        {
                            VrmLog.Error($"加载回调异常: {e}");
                        }
                }

                TrimCache();
            }
        }

        /// <summary>后台预热：把当前存档里已经选中的 VRM 模型先加载好，切换时不至于卡一下。</summary>
        public static void PreloadSelected()
        {
            try
            {
                var usingModel = ModEntry.UsingModel;
                if (usingModel?.TargetTypeModelIDs == null) return;

                foreach (var modelId in usingModel.TargetTypeModelIDs.Values.Distinct())
                {
                    if (string.IsNullOrEmpty(modelId)) continue;
                    if (!IsVrmModel(new ModelInfo { ModelID = modelId })) continue;
                    if (!VrmCatalog.TryGetSourcePath(modelId, out _)) continue;

                    VrmLog.Detail($"后台预加载已选模型: {modelId}");
                    RequestLoad(modelId);
                }
            }
            catch (Exception e)
            {
                VrmLog.Detail($"预加载已选模型失败（忽略）: {e.Message}");
            }
        }

        /// <summary>模型文件变了 / 被删了：丢弃缓存。正在使用的会先留着，等它被换下去再释放。</summary>
        public static void Invalidate(string modelId)
        {
            Failed.Remove(modelId);
            if (Prefabs.TryGetValue(modelId, out var go))
            {
                if (IsInUse(modelId))
                {
                    VrmLog.Info($"模型文件已变化，但它正在使用中，稍后再释放: {modelId}");
                    return;
                }

                Prefabs.Remove(modelId);
                Lru.Remove(modelId);
                if (go != null) Object.Destroy(go);
                VrmLog.Info($"已丢弃模型缓存（文件已变化）: {modelId}");
            }

            TrimCache();
        }

        public static void InvalidateAll()
        {
            Failed.Clear();
            foreach (var modelId in Prefabs.Keys.ToArray()) Invalidate(modelId);
        }

        /// <summary>按 LRU 把超出上限的缓存卸载掉；正在使用的模型一律保留。</summary>
        public static void TrimCache()
        {
            var max = Math.Max(1, VrmConfig.Current.MaxCachedModels);
            if (Prefabs.Count <= max) return;

            var node = Lru.First;
            while (node != null && Prefabs.Count > max)
            {
                var next = node.Next;
                var modelId = node.Value;
                if (!IsInUse(modelId) && Prefabs.TryGetValue(modelId, out var go))
                {
                    Prefabs.Remove(modelId);
                    Lru.Remove(node);
                    if (go != null) Object.Destroy(go);
                    VrmLog.Detail($"按 LRU 释放模型缓存: {modelId}");
                }

                node = next;
            }
        }

        /// <summary>克隆体上带着 VrmModelMarker → 说明这个模型正被某个角色使用。</summary>
        private static bool IsInUse(string modelId)
        {
            foreach (var marker in Object.FindObjectsOfType<VrmModelMarker>())
                if (marker != null && string.Equals(marker.ModelId, modelId, StringComparison.Ordinal))
                    return true;

            return false;
        }

        private static void Touch(string modelId)
        {
            Lru.Remove(modelId);
            Lru.AddLast(modelId);
        }

        /// <summary>记录一次「列表里展示了这个模型」的事件（用于一次性输出预览图诊断）。</summary>
        public static void NoteThumbnail(string modelId, bool ok)
        {
            if (!VrmLog.Verbose) return;
            if (!ThumbnailLogged.Add(modelId)) return;
            VrmLog.Detail($"预览图 {(ok ? "已加载" : "缺失")}: {modelId}");
        }

        // ---- 内存余量守卫 ----
        // VRM 解析的峰值内存远大于文件本身（文件全量进内存 + 网格/BlendShape/纹理再展开一遍）。
        // 注意：物理内存空闲不代表能分配 —— Windows 按「提交内存 = 物理内存 + 页面文件」限流，
        // commit 耗尽时物理内存再多也会 OOM（实测：MallocTracked 原生崩溃 / D3D11 建不出
        // RenderTexture，全是 commit 分配失败）。所以这里取「物理可用」与「commit 可用」的
        // 较小值，要求至少「文件大小 × 10」且不低于 2GB；不够就明确跳过，绝不把游戏拖崩。

        private const long MinLoadHeadroomBytes = 2L * 1024 * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUS
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUS stat);

        private static ulong GetUsableMemory()
        {
            try
            {
                var stat = new MEMORYSTATUS { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUS)) };
                if (!GlobalMemoryStatusEx(ref stat)) return ulong.MaxValue;
                return Math.Min(stat.AvailPhys, stat.AvailPageFile);
            }
            catch
            {
                return ulong.MaxValue; // 查不到就不拦，让流程照走
            }
        }

        private static bool HasEnoughMemoryFor(string path)
        {
            try
            {
                var fileSize = new FileInfo(path).Length;
                var need = Math.Max(fileSize * 10, MinLoadHeadroomBytes);
                var avail = GetUsableMemory();
                if (avail >= (ulong)need) return true;

                VrmLog.Error(
                    $"系统可用内存不足，跳过加载 {Path.GetFileName(path)}" +
                    $"（文件 {fileSize / 1024 / 1024}MB，解析峰值需要约 {need / 1024.0 / 1024 / 1024:0.#}GB，" +
                    $"当前可用 {avail / 1024.0 / 1024 / 1024:0.##}GB）。" +
                    "请关闭其它占内存的程序、增大 Windows 虚拟内存（页面文件）后重试，" +
                    "或在 DCM 里改用文件更小的模型。");
                return false;
            }
            catch
            {
                return true; // 检查本身出错就不拦
            }
        }
    }
}
