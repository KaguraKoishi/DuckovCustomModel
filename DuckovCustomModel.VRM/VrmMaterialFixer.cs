using System.Collections.Generic;
using System.Linq;
using VRM10.MToon10;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// VRM 材质后期处理。
    ///
    /// 背景（本项目最重要的一处修复）：
    /// 游戏使用 URP，且渲染器处于 <b>Deferred</b> 模式。在 Deferred 下，renderQueue 落在不透明区间
    /// （0..2500）的物体，只会被下面两类 Pass 绘制：
    ///   * G-Buffer Pass           —— 只接受 LightMode = <c>UniversalGBuffer</c>
    ///   * Forward-Only Pass       —— 只接受 LightMode = <c>UniversalForwardOnly</c> / <c>SRPDefaultUnlit</c> / <c>LightweightForward</c>
    /// （见 UniversalRenderer 构造函数中 m_RenderOpaqueForwardOnlyPass 的 ShaderTagId 列表。
    ///   注意它<b>不包含</b> <c>UniversalForward</c>；只有 Forward 模式的 DrawObjectsPass 才包含。）
    ///
    /// 而 UniVRM 官方的 <c>vrmc_materials_mtoon_urp.shader</c> 只有 <c>UniversalForward</c> 一个 Pass，
    /// 因此在 Deferred 下：renderQueue = 2000 的材质<b>完全不会被绘制</b>（表现为灰模 / 直接透视背景）。
    /// 正确修法是让 Shader 具备 <c>UniversalForwardOnly</c> Pass（vrmshaders 已经是打过补丁的版本）。
    ///
    /// 本类负责：
    ///   1. 用 MToonValidator 把 <c>_M_*</c> / renderQueue / 关键字规范化成官方取值；
    ///   2. 首次使用时自检 Shader 是否具备 Deferred 兼容通道，缺失时降级并显式告警。
    /// </summary>
    public static class VrmMaterialFixer
    {
        /// <summary>URP 不透明区间上界（含）。</summary>
        private const int OpaqueRangeMax = 2500;

        private const int MToonCutoutQueue = 2450;

        /// <summary>降级用的队列：Opaque → 2600 / Cutout → 2650，保证落在半透明区间。</summary>
        private const int FallbackOpaqueQueue = 2600;
        private const int FallbackCutoutQueue = 2650;

        private static readonly HashSet<int> LoggedMaterialIds = new HashSet<int>();
        private static bool _deferredCheckLogged;

        public static void Normalize(GameObject? root)
        {
            if (root == null) return;

            var mtoonShader = VrmShaderLoader.MToonUrpShader;
            if (mtoonShader == null) return;

            // Deferred 通道自检放在这里（首次真正用到材质时），而不是模组 Awake ——
            // AB 刚加载时 Shader 的 Pass 元数据可能还没就绪。
            var deferCompatible = VrmShaderLoader.MToonSupportsDeferred;
            if (!_deferredCheckLogged)
            {
                _deferredCheckLogged = true;
                if (deferCompatible)
                {
                    VrmLog.Info("MToon10 材质就绪（Deferred 渲染下正常绘制并接受光照）");
                    VrmLog.Detail(VrmShaderLoader.DescribePasses(mtoonShader));
                }
                else
                {
                    VrmLog.Error(
                        "MToon10 缺少 UniversalForwardOnly / UniversalGBuffer 通道，已降级到半透明队列" +
                        "（能显示但光照会偏暗）。请用新版 vrmshaders。");
                    VrmLog.Error(VrmShaderLoader.DescribePasses(mtoonShader));
                }
            }

            int materialCount = 0, fixedCount = 0, pushedCount = 0;
            var queues = new SortedDictionary<int, int>();

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;

                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                foreach (var material in materials)
                {
                    if (material == null) continue;
                    if (material.shader != mtoonShader) continue;

                    materialCount++;
                    if (NormalizeOne(material, deferCompatible, ref pushedCount)) fixedCount++;

                    queues.TryGetValue(material.renderQueue, out var n);
                    queues[material.renderQueue] = n + 1;
                }
            }

            if (materialCount == 0) return;

            var queueSummary = string.Join(", ", queues.Select(kv => $"{kv.Key}x{kv.Value}"));
            VrmLog.Info($"材质: {materialCount} 个 MToon（队列分布 {queueSummary}）");
            VrmLog.Detail($"材质规范化 {fixedCount} 处，队列降级 {pushedCount} 处");
        }

        private static bool NormalizeOne(Material material, bool deferCompatible, ref int pushedCount)
        {
            var changed = false;

            // 1) 补全被 UniVRM/AssetBundle 版本差异吞掉的关键属性，避免 MToonValidator 读到 0 造成误判。
            EnsureDefaults(material, ref changed);

            // 2) 官方校验器：写入 _M_SrcBlend/_M_DstBlend/_M_ZWrite/_M_CullMode/_M_AlphaToMask、
            //    RenderType 标签、renderQueue 以及 _ALPHATEST_ON/_ALPHABLEND_ON 等关键字。
            var queueBefore = material.renderQueue;
            try
            {
                new MToonValidator(material).Validate();
            }
            catch (System.Exception e)
            {
                VrmLog.Warn($"MToonValidator 校验 {material.name} 失败: {e.Message}");
                return changed;
            }

            if (material.renderQueue != queueBefore) changed = true;

            // 3) Deferred 通道缺失时的安全降级。
            if (!deferCompatible && material.renderQueue <= OpaqueRangeMax)
            {
                var target = material.renderQueue == MToonCutoutQueue ? FallbackCutoutQueue : FallbackOpaqueQueue;
                material.renderQueue = target;
                material.SetInt(MToon10Prop.UnityZWrite.ToUnityShaderLabName(), 1);
                pushedCount++;
                changed = true;
            }

            LogOnce(material);
            return changed;
        }

        /// <summary>
        /// 部分导入路径下，材质里可能根本没有这些属性（例如 Shader 换了但材质是旧的），
        /// 此时 <c>GetInt</c> 会返回 0，被误判成 Opaque。这里只在属性缺失时补一个合理默认值。
        /// </summary>
        private static void EnsureDefaults(Material material, ref bool changed)
        {
            if (!material.HasProperty(MToon10Prop.AlphaMode.ToUnityShaderLabName()))
            {
                material.SetInt(MToon10Prop.AlphaMode.ToUnityShaderLabName(), (int)MToon10AlphaMode.Opaque);
                changed = true;
            }

            if (!material.HasProperty(MToon10Prop.TransparentWithZWrite.ToUnityShaderLabName()))
            {
                material.SetInt(MToon10Prop.TransparentWithZWrite.ToUnityShaderLabName(),
                    (int)MToon10TransparentWithZWriteMode.Off);
                changed = true;
            }

            if (!material.HasProperty(MToon10Prop.DoubleSided.ToUnityShaderLabName()))
            {
                material.SetInt(MToon10Prop.DoubleSided.ToUnityShaderLabName(),
                    (int)MToon10DoubleSidedMode.Off);
                changed = true;
            }

            if (!material.HasProperty(MToon10Prop.RenderQueueOffsetNumber.ToUnityShaderLabName()))
            {
                material.SetInt(MToon10Prop.RenderQueueOffsetNumber.ToUnityShaderLabName(), 0);
                changed = true;
            }
        }

        /// <summary>每个材质实例只打印一次，且只在 verbose 模式下输出。</summary>
        private static void LogOnce(Material material)
        {
            if (!VrmLog.Verbose) return;

            var id = material.GetInstanceID();
            if (!LoggedMaterialIds.Add(id)) return;

            var mainTex = material.HasProperty(MToon10Prop.BaseColorTexture.ToUnityShaderLabName())
                ? material.GetTexture(MToon10Prop.BaseColorTexture.ToUnityShaderLabName())
                : null;
            var color = material.HasProperty(MToon10Prop.BaseColorFactor.ToUnityShaderLabName())
                ? material.GetColor(MToon10Prop.BaseColorFactor.ToUnityShaderLabName())
                : Color.magenta;
            var alphaMode = material.GetInt(MToon10Prop.AlphaMode.ToUnityShaderLabName());
            var cull = material.GetInt(MToon10Prop.UnityCullMode.ToUnityShaderLabName());
            var zwrite = material.GetInt(MToon10Prop.UnityZWrite.ToUnityShaderLabName());
            var gi = material.HasProperty(MToon10Prop.GiEqualizationFactor.ToUnityShaderLabName())
                ? material.GetFloat(MToon10Prop.GiEqualizationFactor.ToUnityShaderLabName())
                : -1f;

            VrmLog.Detail(
                $"材质 {material.name}: alphaMode={alphaMode} queue={material.renderQueue} " +
                $"cull={cull} zwrite={zwrite} giEq={gi:F2} " +
                $"mainTex={(mainTex == null ? "null" : mainTex.name)} color={color} " +
                $"renderType={material.GetTag("RenderType", false)}");
        }

        /// <summary>模型销毁 / 卸载时清理缓存，避免 InstanceID 复用后不再打印日志。</summary>
        public static void Forget(Material material)
        {
            if (material != null) LoggedMaterialIds.Remove(material.GetInstanceID());
        }
    }
}
