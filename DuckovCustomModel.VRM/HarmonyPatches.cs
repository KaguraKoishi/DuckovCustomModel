using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DuckovCustomModel.Core.Data;
using DuckovCustomModel.Managers;
using DuckovCustomModel.MonoBehaviours;
using HarmonyLib;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 接管 DCM 对「VRM 虚拟模型包」的资源读取。
    ///
    /// VRM 包里的 BundlePath 只是一个占位文件，里面没有任何资源；模型 prefab
    /// 在内存里（UniVRM 加载产物）。所有「真需要资源」的入口都在这里重定向，
    /// 其余入口返回 null —— 让 DCM 静默跳过，而不是走 AB 路径刷屏报错。
    /// </summary>
    [HarmonyPatch(typeof(AssetBundleManager))]
    public static class AssetBundleManager_Patches
    {
        private static bool IsVrm(ModelInfo? modelInfo)
        {
            return VrmModelRegistry.IsVrmModel(modelInfo);
        }

        private static bool IsVrmBundle(ModelBundleInfo? bundleInfo)
        {
            if (bundleInfo?.Models == null || bundleInfo.Models.Length == 0) return false;
            foreach (var model in bundleInfo.Models)
                if (IsVrm(model))
                    return true;

            return false;
        }

        /// <summary>VRM 包没有可加载的 AB；返回 null 让未接管的调用方静默失败。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.GetOrLoadAssetBundle))]
        public static bool GetOrLoadAssetBundle_Prefix(ModelBundleInfo bundleInfo, ref AssetBundle? __result)
        {
            if (!IsVrmBundle(bundleInfo)) return true;

            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.GetOrLoadAssetBundleAsync))]
        public static bool GetOrLoadAssetBundleAsync_Prefix(
            ModelBundleInfo bundleInfo,
            CancellationToken cancellationToken,
            ref UniTask<AssetBundle?> __result)
        {
            if (!IsVrmBundle(bundleInfo)) return true;

            __result = UniTask.FromResult<AssetBundle?>(null);
            return false;
        }

        /// <summary>
        /// 真正取 prefab 的地方。此时模型一定已经加载好了（由
        /// <see cref="ModelHandler_InitializeCustomModel_Patch"/> 保证），这里只取缓存。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.LoadModelPrefab))]
        public static bool LoadModelPrefab_Prefix(ModelInfo modelInfo, ref GameObject? __result)
        {
            if (!IsVrm(modelInfo)) return true;

            if (VrmModelRegistry.TryGetLoadedPrefab(modelInfo.ModelID, out var prefab))
            {
                __result = prefab;
                return false;
            }

            VrmLog.Warn($"VRM 模型尚未加载完成: {modelInfo.ModelID}");
            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.LoadModelPrefabAsync))]
        public static bool LoadModelPrefabAsync_Prefix(ModelInfo modelInfo,
            ref UniTask<GameObject?> __result)
        {
            if (!IsVrm(modelInfo)) return true;

            if (VrmModelRegistry.TryGetLoadedPrefab(modelInfo.ModelID, out var prefab))
            {
                __result = UniTask.FromResult<GameObject?>(prefab);
                return false;
            }

            // 尚未加载：挂一个后台加载，加载完把结果交回去（不再阻塞主线程）。
            var completion = new UniTaskCompletionSource<GameObject?>();
            VrmModelRegistry.RequestLoad(modelInfo.ModelID, go => completion.TrySetResult(go));
            __result = completion.Task;
            return false;
        }

        /// <summary>
        /// 只看「源文件还在不在」——惰性加载的意义就在于这里不该触发模型加载。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.CheckPrefabExists))]
        public static bool CheckPrefabExists_Prefix(ModelInfo modelInfo, ref bool __result)
        {
            if (!IsVrm(modelInfo)) return true;

            __result = VrmCatalog.IsAvailable(modelInfo);
            return false;
        }

        /// <summary>UI / 刷新会走这里，不劫持会被当成无效模型。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.CheckBundleStatus))]
        public static bool CheckBundleStatus_Prefix(ModelInfo modelInfo,
            ref (bool isValid, string? errorMessage) __result)
        {
            if (!IsVrm(modelInfo)) return true;

            __result = VrmCatalog.IsAvailable(modelInfo)
                ? (true, null)
                : (false, "VRM 源文件不存在");
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AssetBundleManager.CheckBundleStatusAsync))]
        public static bool CheckBundleStatusAsync_Prefix(
            ModelInfo modelInfo,
            ref UniTask<(bool isValid, string? errorMessage)> __result)
        {
            if (!IsVrm(modelInfo)) return true;

            __result = UniTask.FromResult(VrmCatalog.IsAvailable(modelInfo)
                ? (true, (string?)null)
                : (false, (string?)"VRM 源文件不存在"));
            return false;
        }

        /// <summary>统计一次 DCM 到底问了多少次「prefab 存在吗」，只在 verbose 下输出。</summary>
        private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);

        internal static void ReportMissing(string modelId)
        {
            if (!VrmLog.Verbose) return;
            if (!ReportedMissing.Add(modelId)) return;
            VrmLog.Detail($"DCM 查询了未注册的 VRM 模型: {modelId}");
        }
    }

    /// <summary>
    /// 惰性加载的关键一环：DCM 决定「用这个模型」时会同步调用
    /// <c>ModelHandler.InitializeCustomModel</c>，而 VRM 的加载是秒级的。
    /// 直接同步加载会把主线程卡住十几秒 —— 所以这里先拦下来，
    /// 转后台加载，加载完再重新走一遍正常流程。
    /// </summary>
    [HarmonyPatch(typeof(ModelHandler), nameof(ModelHandler.InitializeCustomModel))]
    public static class ModelHandler_InitializeCustomModel_Patch
    {
        private static readonly HashSet<string> Deferred = new(StringComparer.Ordinal);

        /// <summary>返回 false 表示这次调用被我们接管了，不执行原方法。</summary>
        [HarmonyPrefix]
        public static bool Prefix(ModelHandler __instance, ModelBundleInfo modelBundleInfo, ModelInfo modelInfo)
        {
            if (!VrmModelRegistry.IsVrmModel(modelInfo)) return true;

            // 已经在内存里 → 正常流程。
            if (VrmModelRegistry.TryGetLoadedPrefab(modelInfo.ModelID, out _)) return true;

            // 加载失败过 → 交给原流程走 null 分支（只会报一次错，不会死循环）。
            if (VrmModelRegistry.GetState(modelInfo.ModelID) == VrmLoadState.Failed) return true;

            // 同一个模型只排一次队。
            if (!Deferred.Add(modelInfo.ModelID)) return false;

            VrmLog.Info($"模型尚未加载，转后台加载后再应用: {modelInfo.Name}");

            var handler = __instance;
            VrmModelRegistry.RequestLoad(modelInfo.ModelID, _ =>
            {
                Deferred.Remove(modelInfo.ModelID);
                if (handler == null) return;

                try
                {
                    handler.InitializeCustomModel(modelBundleInfo, modelInfo);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"加载完成后应用模型失败:\n{e}");
                }
            });

            return false;
        }

        /// <summary>
        /// 锚点必须在 DCM 记录 socket <b>之前</b>就存在。
        /// DCM 的流程是：Instantiate → 设置 Animator → <c>RecordCustomModelSockets()</c>
        /// （按名字搜索锚点并登记）→ <c>ChangeToCustomModelSockets()</c>（把装备挂上去）。
        /// 之前把锚点生成放在 <c>InitializeCustomModel</c> 的 Postfix 里，那时 socket 已经
        /// 记录完毕（结果是空字典），装备全部留在被隐藏的原模型上 —— 枪、护甲、血条全都看不见。
        /// 所以这里挂在 <c>RecordCustomModelSockets</c> 的 Prefix 上：此时模型实例已就绪、
        /// 动画器还没拿到 Controller（仍是静止 T-pose，正是量骨架 / 修动骨的时机）。
        /// </summary>
        [HarmonyPatch(typeof(ModelHandler), "RecordCustomModelSockets")]
        public static class ModelHandler_RecordCustomModelSockets_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(ModelHandler __instance)
            {
                var model = __instance.CustomModelInstance;
                var modelInfo = __instance.CurrentModelInfo;
                if (model == null || modelInfo == null || !VrmModelRegistry.IsVrmModel(modelInfo)) return;

                UrpDiagnostics.LogOnce();

                try
                {
                    VrmMaterialFixer.Normalize(model);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"材质规范化失败:\n{e}");
                }

                // 骨架测量（VrmAnchorFrame）只在模型首次加载（静止 T-pose）时做一次，
                // 之后复用组件缓存 —— 测量必须在 T-pose 下进行，带姿势的测量值不可信。
                VrmAnchorFrame? frame = model.GetComponent<VrmAnchorFrame>();
                if (frame == null)
                {
                    try
                    {
                        frame = VrmAnchorFrame.Capture(model, modelInfo.ModelID);
                    }
                    catch (Exception e)
                    {
                        VrmLog.Error($"骨架测量失败（锚点将退回模型根）:\n{e}");
                    }
                }

                try
                {
                    VrmSpringBoneFixer.Fix(model);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"SpringBone 修复失败:\n{e}");
                }

                try
                {
                    VrmLocatorBuilder.Build(__instance, model, frame);
                    VrmCatalog.RecordHelmetHeight(modelInfo.ModelID, VrmLocatorBuilder.LocalHelmetHeight());
                }
                catch (Exception e)
                {
                    VrmLog.Error($"定位锚点生成失败:\n{e}");
                }

                try
                {
                    BindAnimator(__instance);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"动画器绑定失败:\n{e}");
                }
            }

            /// <summary>
            /// 最小动画器绑定：VRM 实例的 Animator 没有 controller，而 DCM 的动画驱动
            /// （Running/Moving/Dashing/MoveSpeed 等参数）只对挂了 controller 的 Animator
            /// 生效 —— 把 vrmmotion 包里的 controller 补上即可，其余全部交给 DCM 本体。
            ///
            /// 时机说明：DCM 在更早的 InitializeCustomModelInternal 里已经用「无 controller」
            /// 的 Animator 初始化过 CustomAnimatorControl（参数缓存按空控制器建了），
            /// 所以赋完 controller 后要重新 SetCustomAnimator 让缓存按新控制器重建。
            /// vrmmotion AB 不存在时安静跳过 —— 动画是可选功能。
            /// </summary>
            private static void BindAnimator(ModelHandler handler)
            {
                var animator = handler.CustomAnimator;
                if (animator == null)
                {
                    VrmLog.Info("模型实例上没有 Animator 组件，动画器绑定跳过。");
                    return;
                }

                if (animator.runtimeAnimatorController != null) return; // 模型自带控制器，不干预

                var controller = VrmMotionBundle.Controller;
                if (controller == null) return;

                animator.runtimeAnimatorController = controller;
                handler.CustomAnimatorControl?.SetCustomAnimator(animator);
                VrmLog.Info($"已挂载动画器: {controller.name}");
            }
        }
    }

    /// <summary>
    /// 让 DCM 重新执行「socket 登记 + 装备挂接」。
    ///
    /// 背景：DCM 在模型初始化时把锚点 Transform 的**直接引用**缓存进
    /// <c>_customModelLocators</c>，装备全部作为锚点的子物体挂接。微调面板的
    /// 「重新生成」会销毁重建锚点 GameObject，DCM 缓存随之失效（Unity 假 null），
    /// 装备就会留在原处不动 —— 必须重新走一遍 DCM 的登记/挂接流程才恢复。
    ///
    /// 注意：rebuildRecord = true 会触发 <see cref="ModelHandler_RecordCustomModelSockets_Patch"/>
    /// 的 Prefix（重建锚点、重新读校正文件），适合「重新生成」；
    /// 只改了已有锚点的位姿时用 rebuildRecord = false（只重挂装备，不重建锚点）。
    /// </summary>
    internal static class VrmSocketRefresher
    {
        private static System.Reflection.MethodInfo? _record;
        private static System.Reflection.MethodInfo? _change;

        public static void Refresh(ModelHandler? handler, bool rebuildRecord)
        {
            if (handler == null) return;

            var modelInfo = handler.CurrentModelInfo;
            if (modelInfo == null || !VrmModelRegistry.IsVrmModel(modelInfo)) return;

            try
            {
                if (rebuildRecord)
                {
                    _record ??= AccessTools.Method(typeof(ModelHandler), "RecordCustomModelSockets");
                    _record?.Invoke(handler, null);
                }

                _change ??= AccessTools.Method(typeof(ModelHandler), "ChangeToCustomModelSockets");
                _change?.Invoke(handler, null);
                VrmLog.Info(rebuildRecord ? "锚点已重建，装备已重新挂接。" : "装备已重新挂接到锚点。");
            }
            catch (Exception e)
            {
                VrmLog.Error($"重新挂接装备失败: {e.Message}");
            }
        }
    }

    /// <summary>
    /// DCM 的头盔高度 / 根缩放是从 prefab 上的定位器算的。
    /// VRM 的 prefab 可能是「按需加载、用完淘汰」的，所以这里：
    ///   1. prefab 在内存里 → 直接用；
    ///   2. 不在内存里但有历史测量值（写在目录缓存里）→ 用缓存值；
    ///   3. 都没有 → 返回 0，绝不为了量个高度把模型加载起来。
    /// </summary>
    [HarmonyPatch]
    public static class ModelHeightManager_Patches
    {
        private static bool TryGetVrmEntry(string? modelID, out VrmCatalogEntry entry)
        {
            entry = null!;
            if (string.IsNullOrEmpty(modelID) ||
                !modelID.StartsWith(VrmModelRegistry.VrmModelIdPrefix, StringComparison.Ordinal))
                return false;

            var found = VrmCatalog.Find(modelID);
            if (found == null) return false;
            entry = found;
            return true;
        }

        private static Transform? FindLocator(GameObject prefab, string locatorName)
        {
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                if (t.name == locatorName)
                    return t;
            return null;
        }

        /// <summary>
        /// 兜底身高：连静态估算都拿不到时用这个（米）。它只是身高滑条的**基准**，
        /// DCM 的缩放是「目标 / 基准」的比值，所以基准偏一点不会把模型缩坏，
        /// 只影响滑条上显示的数字。真正实测到以后会被覆盖。
        /// </summary>
        private const float FallbackHelmetHeight = 1.35f;

        private static readonly HashSet<string> DefaultedHeightModels = new(StringComparer.Ordinal);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModelHeightManager), "GetHelmetHeightFromPrefab")]
        public static bool GetHelmetHeightFromPrefab_Prefix(string modelID, ref float __result)
        {
            if (!TryGetVrmEntry(modelID, out var entry)) return true;

            var measured = 0f;

            // 1) prefab 还在内存里 → 直接量（VRM 的 prefab 上没有头盔锚点，一般量不到）；
            if (VrmModelRegistry.TryGetLoadedPrefab(modelID, out var prefab))
            {
                var locator = FindLocator(prefab, SocketNames.Helmet);
                // 锚点是运行时挂在**实例**骨骼下的，prefab 上通常没有 → 拿骨骼估一个。
                measured = locator != null
                    ? locator.position.y - prefab.transform.position.y
                    : VrmCatalog.MeasureHelmetHeight(prefab);
            }

            // 2) 目录缓存里由「锚点管线实测」或「GLB 静态估算」写下的值；
            if (measured <= 0f && entry.HelmetHeight > 0f) measured = entry.HelmetHeight;

            // 3) 都没有 → 用兜底值。返回 0 会让 DCM 直接隐藏身高滑条，那是更糟的结果。
            if (measured <= 0f)
            {
                measured = FallbackHelmetHeight;
                if (DefaultedHeightModels.Add(modelID))
                    VrmLog.Info($"模型 {modelID} 还没有实测到身高，先用默认 {FallbackHelmetHeight:0.##} 作为身高基准" +
                                "（选中模型 / 重载一次后会被实测值覆盖）。");
            }

            __result = measured;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModelHeightManager), "GetInitialRootScaleFromPrefab")]
        public static bool GetInitialRootScaleFromPrefab_Prefix(string modelID, ref Vector3 __result)
        {
            if (!TryGetVrmEntry(modelID, out var entry)) return true;

            if (VrmModelRegistry.TryGetLoadedPrefab(modelID, out var prefab))
            {
                __result = prefab.transform.localScale;
                return false;
            }

            __result = entry.RootScale is { Length: 3 }
                ? new Vector3(entry.RootScale[0], entry.RootScale[1], entry.RootScale[2])
                : Vector3.one;
            return false;
        }
    }
}
