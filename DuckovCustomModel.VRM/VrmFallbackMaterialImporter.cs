using System;
using System.Collections.Generic;
using UniGLTF;
using UnityEngine;
using VRM10.MToon10;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 兜底材质生成器：任何「MToon importer 没收下的材质」都交给 URP 版 MToon10 渲染。
    ///
    /// 为什么必须有它 —— 这类材质有两种来源，都会让模型**加载失败**（不是变紫，是整只模型加载不出来）：
    ///
    ///   1) VRM 0.x 里 <c>materialProperties[].shader == "VRM_USE_GLTFSHADER"</c> 的材质
    ///      （常见于饰品 / 小物件），迁移到 VRM 1.0 后**连 glTF 扩展都没有**，就是一只普通 PBR 材质。
    ///      实例：Fioka.vrm 的 <c>ACC</c>。
    ///   2) 非 VRM0 来源的模型（例如 Blender 的 VRM 插件直接导出的 VRM 1.0），
    ///      材质全是原生 glTF PBR，一个 MToon 都没有。实例：测试.vrm。
    ///
    /// 它们会被 MToon importer 拒收 → 落到 UniVRM 默认的 <c>UrpVrm10MaterialDescriptorGenerator</c>，
    /// 那儿对 PBR 用的是 <c>Shader.Find("Universal Render Pipeline/Lit")</c> ——
    /// **本游戏的 build 把 URP/Lit 与 Simple Lit 都剥掉了**（实测 <c>Duckov_Data/globalgamemanagers</c>
    /// 里只有 Unlit / Particles.Lit 等，没有 Lit），所以 <c>Shader.Find</c> 返回 null →
    /// <c>MaterialDescriptor.Shader</c> 为 null → <c>MaterialFactory.LoadAsync</c> 抛
    /// <c>ArgumentNullException: Shader</c> → 整个模型加载失败。
    ///
    /// （曾经的另一条错路：把 <c>KHR_materials_unlit</c> 材质塞给 UniVRM 的
    /// <c>BuiltInGltfUnlitMaterialImporter</c> + <c>UniGLTF/UniUnlit</c>。也不行：UniUnlit 是 Built-in
    /// 管线的 CGPROGRAM shader，Pass 没有 LightMode 标签，URP Deferred 下根本不会被绘制。）
    ///
    /// 所以这里统一生成 MToon10（URP 版）的 <see cref="MaterialDescriptor"/>：
    /// baseColor / 贴图从 glTF 材质读，alphaMode / doubleSided 交给 <see cref="MToonValidator"/>
    /// 换算成 blend / cull / renderQueue —— 与模型上其它 MToon 材质走完全同一条渲染管线。
    ///
    /// 视觉上是**近似**：glTF PBR 的 metallic / roughness 在 MToon10 里没有对应属性，会被忽略；
    /// 阴影侧用 baseColor 的一半来近似。本游戏里能保证的是「能画出来、颜色对、透明度对」。
    /// </summary>
    internal sealed class VrmFallbackMaterialImporter
    {
        // MToon10 的属性名，对应 vrmshaders 里的 vrmc_materials_mtoon_urp.shader
        private const string PropBaseColor = "_Color";
        private const string PropBaseColorTexture = "_MainTex";
        private const string PropShadeColor = "_ShadeColor";
        private const string PropNormalMap = "_BumpMap";
        private const string PropNormalScale = "_BumpScale";
        private const string PropEmissionColor = "_EmissionColor";
        private const string PropEmissionMap = "_EmissionMap";
        private const string PropAlphaMode = "_AlphaMode";
        private const string PropAlphaCutoff = "_Cutoff";
        private const string PropDoubleSided = "_DoubleSided";

        // MToon10AlphaMode
        private const float AlphaModeOpaque = 0f;
        private const float AlphaModeCutout = 1f;
        private const float AlphaModeTransparent = 2f;

        /// <summary>阴影色相对基础色的比例。MToon10 的 shading 项是 baseColor 与 shadeColor 的混合，
        /// 取一半亮度即得到接近普通朗伯着色的明暗关系（纯 MToon 模型的 shadeColor 由作者指定，不走这里）。</summary>
        private const float ShadeDarken = 0.5f;

        private readonly Shader? mtoonShader;
        private readonly string? modelName;

        public VrmFallbackMaterialImporter(Shader? mtoonShader, string? modelName = null)
        {
            this.mtoonShader = mtoonShader;
            this.modelName = modelName;
        }

        public bool TryCreateParam(GltfData data, int i, out MaterialDescriptor matDesc)
        {
            matDesc = null!;

            // 没有 MToon10 就没法兜底（此时宁可让上层走原逻辑，也不要凭空造一个 null Shader 的材质）
            if (mtoonShader == null) return false;
            if (i < 0 || i >= data.GLTF.materials.Count) return false;

            var src = data.GLTF.materials[i];
            var name = GltfMaterialImportUtils.ImportMaterialName(i, src);
            var unlit = glTF_KHR_materials_unlit.IsEnable(src);

            var colors = new Dictionary<string, Color>();
            var floats = new Dictionary<string, float>();
            var textureSlots = new Dictionary<string, TextureDescriptor>();

            // baseColor：glTF 是 linear，MToon10 的 _Color / _ShadeColor 是 sRGB 空间
            var baseColorFactor = GltfMaterialImportUtils.ImportLinearBaseColorFactor(data, src);
            var baseColor = baseColorFactor?.gamma ?? Color.white;
            colors[PropBaseColor] = baseColor;
            colors[PropShadeColor] = unlit
                ? new Color(baseColor.r, baseColor.g, baseColor.b, 1f) // unlit 材质要平铺直出，阴影色=基础色
                : new Color(baseColor.r * ShadeDarken, baseColor.g * ShadeDarken, baseColor.b * ShadeDarken, 1f);

            var baseColorTexture = src.pbrMetallicRoughness?.baseColorTexture;
            if (baseColorTexture != null)
            {
                var (offset, scale) = GltfTextureImporter.GetTextureOffsetAndScale(baseColorTexture);
                if (GltfTextureImporter.TryCreateSrgb(data, baseColorTexture.index, offset, scale,
                        out _, out var desc))
                    textureSlots[PropBaseColorTexture] = desc;
            }

            var normalTexture = src.normalTexture;
            if (normalTexture != null)
            {
                var (offset, scale) = GltfTextureImporter.GetTextureOffsetAndScale(normalTexture);
                if (GltfTextureImporter.TryCreateNormal(data, normalTexture.index, offset, scale,
                        out _, out var desc))
                {
                    textureSlots[PropNormalMap] = desc;
                    floats[PropNormalScale] = normalTexture.scale;
                }
            }

            var emissiveFactor = GltfMaterialImportUtils.ImportLinearEmissiveFactor(data, src);
            if (emissiveFactor.HasValue)
                colors[PropEmissionColor] = emissiveFactor.Value;

            var emissiveTexture = src.emissiveTexture;
            if (emissiveTexture != null)
            {
                var (offset, scale) = GltfTextureImporter.GetTextureOffsetAndScale(emissiveTexture);
                if (GltfTextureImporter.TryCreateSrgb(data, emissiveTexture.index, offset, scale,
                        out _, out var desc))
                    textureSlots[PropEmissionMap] = desc;
            }

            // alpha / 双面 —— 交给 MToonValidator 换算成 _M_SrcBlend / _M_DstBlend / _M_ZWrite /
            // _M_CullMode / renderQueue 与相关 keyword
            floats[PropAlphaMode] = src.alphaMode switch
            {
                "MASK" => AlphaModeCutout,
                "BLEND" => AlphaModeTransparent,
                _ => AlphaModeOpaque,
            };
            floats[PropAlphaCutoff] = src.alphaCutoff;
            floats[PropDoubleSided] = src.doubleSided ? 1f : 0f;

            matDesc = new MaterialDescriptor(
                name,
                mtoonShader,
                null,
                textureSlots,
                floats,
                colors,
                new Dictionary<string, Vector4>(),
                new Action<Material>[]
                {
                    material => new MToonValidator(material).Validate(),
                });

            VrmLog.Info($"材质 '{name}' 不是 MToon（glTF {(unlit ? "unlit" : "PBR")}），" +
                        $"已用 MToon10(URP) 兜底渲染{(modelName == null ? "" : $"（{modelName}）")}");

            return true;
        }
    }
}
