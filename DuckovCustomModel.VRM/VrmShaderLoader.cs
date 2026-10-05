using System;
using System.Collections.Generic;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using UnityEngine.Rendering;
using VRM10.MToon10;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 从模组目录下的 vrmshaders AssetBundle 里取出 VRM 需要的 Shader。
    /// 注意：这些 Shader 必须以 AssetBundle 形式提供，不能在运行时凭空构造。
    /// AB 的加载统一走 <see cref="VrmShaderBundle"/>（进程内只加载一次）。
    /// </summary>
    public static class VrmShaderLoader
    {
        /// <summary>URP 版 MToon10（游戏使用 URP，因此实际会用到这一个）。</summary>
        public const string MToonUrpShaderName = "VRM10/Universal Render Pipeline/MToon10";

        /// <summary>Built-in RP 版 MToon10，仅作为兜底名字参考。</summary>
        public const string MToonBuiltInShaderName = "VRM10/MToon10";

        /// <summary>UniVRM 的 Unlit Shader。</summary>
        public const string UniUnlitShaderName = "UniGLTF/UniUnlit";

        /// <summary>MToon10 用于“不透明物体”的通道名。游戏 URP 为 Deferred，必须有它才会被绘制。</summary>
        public const string ForwardOnlyPassName = "UniversalForwardOnly";

        /// <summary>Deferred 渲染器 G-Buffer 通道名。</summary>
        public const string GBufferPassName = "UniversalGBuffer";

        public static readonly Dictionary<string, Shader> Shaders = new Dictionary<string, Shader>();

        public static bool Initialized { get; private set; }

        public static Shader? MToonUrpShader => Get(MToonUrpShaderName);

        public static void Initialize()
        {
            if (Initialized) return;
            Initialized = true;

            var ab = VrmShaderBundle.Bundle;
            if (ab == null) return;

            foreach (var shader in ab.LoadAllAssets<Shader>())
            {
                if (shader == null || string.IsNullOrEmpty(shader.name)) continue;
                Shaders[shader.name] = shader;
            }

            if (Shaders.Count == 0)
            {
                VrmLog.Error("vrmshaders 里没有 Shader，VRM 材质会变成紫红色。");
                return;
            }

            VrmLog.Info($"已从 vrmshaders 加载 {Shaders.Count} 个 Shader: {string.Join(", ", Shaders.Keys)}");

            if (MToonUrpShader == null)
                VrmLog.Error($"未找到 {MToonUrpShaderName}，VRM 材质会变成紫红色。");
        }

        public static Shader? Get(string name)
        {
            return Shaders.TryGetValue(name, out var s) ? s : null;
        }

        /// <summary>
        /// 判断 Shader 是否含指定通道。
        ///
        /// ⚠ 不能只用 <c>Material.FindPass</c>：实测它在「AssetBundle 加载出来的 MToon10 URP shader」
        /// 上会返回 -1 —— 即便 shader 里明明有同名 Pass，Unity 自己的编译日志也写着
        /// <c>Compiling shader "…MToon10" pass "UniversalForwardOnly"</c>、并且成功压缩出 1.27MB 字节码。
        /// 所以这里走两条互相独立的途径，任一命中即算存在，且比较时忽略大小写：
        ///   1) <c>Material.GetPassName(i)</c>            —— Pass 的 <c>Name</c> 标签
        ///   2) <c>Shader.FindPassTagValue(i, "LightMode")</c> —— Pass 的 <c>LightMode</c> 标签
        ///      （Unity 内部把 LightMode 存成全大写，例如源码写 "ForwardBase"、取出来是 "FORWARDBASE"）
        /// </summary>
        public static bool HasPass(Shader shader, string passName)
        {
            if (shader == null || string.IsNullOrEmpty(passName)) return false;

            Material? probe = null;
            try
            {
                var passCount = shader.passCount;
                var lightModeTag = new ShaderTagId("LightMode");

                for (var i = 0; i < passCount; i++)
                {
                    // 途径 1：Pass 的 Name 标签
                    try
                    {
                        probe ??= new Material(shader);
                        var name = probe.GetPassName(i);
                        if (!string.IsNullOrEmpty(name) &&
                            string.Equals(name, passName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // 某些平台/版本不支持 GetPassName，忽略
                    }

                    // 途径 2：Pass 的 LightMode 标签
                    try
                    {
                        var lightMode = shader.FindPassTagValue(i, lightModeTag).name;
                        if (!string.IsNullOrEmpty(lightMode) &&
                            string.Equals(lightMode, passName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // 忽略
                    }
                }

                // 兜底：传统 FindPass（对部分 shader 有效）
                probe ??= new Material(shader);
                return probe.FindPass(passName) >= 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (probe != null) UnityEngine.Object.Destroy(probe);
            }
        }

        /// <summary>把 Shader 的全部 Pass（Name + LightMode）拼成一行，方便日志诊断。</summary>
        public static string DescribePasses(Shader shader)
        {
            if (shader == null) return "<null>";

            Material? probe = null;
            try
            {
                var passCount = shader.passCount;
                var lightModeTag = new ShaderTagId("LightMode");
                var parts = new List<string>(passCount);
                probe = new Material(shader);

                for (var i = 0; i < passCount; i++)
                {
                    string name;
                    string lightMode;
                    try { name = probe.GetPassName(i); }
                    catch { name = "?"; }
                    try { lightMode = shader.FindPassTagValue(i, lightModeTag).name; }
                    catch { lightMode = "?"; }

                    parts.Add($"[{i}] '{name}' (LightMode='{lightMode}')");
                }

                return $"{shader.name} 共 {passCount} 个 Pass: {string.Join(" | ", parts)}";
            }
            catch (Exception e)
            {
                return $"{shader.name} Pass 列表读取失败: {e.Message}";
            }
            finally
            {
                if (probe != null) UnityEngine.Object.Destroy(probe);
            }
        }

        private static bool _warnedInconclusive;

        /// <summary>
        /// MToon10 是否可用于游戏的 Deferred 渲染器。
        ///
        /// ⚠ Shader 刚从 AssetBundle 加载时（例如 ModBehaviour.Awake 阶段），
        /// Pass 元数据可能尚未就绪：实测此时 passCount=1、查不到任何 LightMode；
        /// 稍后（首次使用材质时）再查就一切正常。因此：
        ///   * 自检要放在「首次使用材质」时（VrmMaterialFixer），不要放在 Awake；
        ///   * 元数据未就绪时按「兼容」处理 —— 绝不因此把队列降到 2600 的取巧值。
        /// </summary>
        public static bool MToonSupportsDeferred
        {
            get
            {
                var shader = MToonUrpShader;
                if (shader == null) return true;

                if (shader.passCount <= 1)
                {
                    if (!_warnedInconclusive)
                    {
                        _warnedInconclusive = true;
                        VrmLog.Warn("MToon10 的 Pass 元数据尚未就绪，暂跳过 Deferred 自检（不影响渲染）");
                    }

                    return true;
                }

                return HasPass(shader, ForwardOnlyPassName) || HasPass(shader, GBufferPassName);
            }
        }
    }

        /// <summary>
        /// 让 UniVRM 使用我们从 AssetBundle 里加载出来的 MToon10 URP Shader，
        /// 而不是 Shader.Find（那样在运行时找不到 AB 里的 Shader）。
        ///
        /// 材质分流（顺序很重要）：
        ///   1) 带 <c>VRMC_materials_mtoon</c> 的材质 → 官方 <c>UrpVrm10MToonMaterialImporter</c>（原样还原作者参数）
        ///   2) 其余全部（VRM0 的 <c>VRM_USE_GLTFSHADER</c> 材质、Blender 等直出的纯 glTF PBR 材质）
        ///      → <see cref="VrmFallbackMaterialImporter"/>（MToon10 兜底）
        ///
        /// ⚠ 绝不能让材质落到 UniVRM 的 <c>UrpVrm10MaterialDescriptorGenerator</c>：它对 PBR 用
        /// <c>Shader.Find("Universal Render Pipeline/Lit")</c>，而本游戏 build 把 URP/Lit 剥掉了 →
        /// Shader 为 null → 整个模型加载失败。见 <see cref="VrmFallbackMaterialImporter"/> 的说明。
        /// </summary>
        public class CustomVrmMaterialGenerator : IMaterialDescriptorGenerator
        {
            private readonly UrpVrm10MToonMaterialImporter mtoonImporter;
            private readonly VrmFallbackMaterialImporter fallbackImporter;

            public CustomVrmMaterialGenerator(string? modelName = null)
            {
                var mtoonShader = VrmShaderLoader.MToonUrpShader;
                mtoonImporter = new UrpVrm10MToonMaterialImporter(mtoonShader);
                fallbackImporter = new VrmFallbackMaterialImporter(mtoonShader, modelName);

                if (mtoonShader == null)
                    VrmLog.Error("没有找到 MToon10 URP Shader，材质将退化为默认生成器");
                else
                    VrmLog.Detail("材质生成器已使用 vrmshaders 中的 MToon10 URP Shader（MToon 与其它材质共用）");
            }

            public MaterialDescriptor Get(GltfData data, int i)
            {
                if (mtoonImporter.TryCreateParam(data, i, out var mtoon)) return mtoon;
                if (fallbackImporter.TryCreateParam(data, i, out var fallback)) return fallback;

                // 只有「MToon10 Shader 没加载出来」才会走到这里。此时已经没有任何可用 Shader，
                // 只能退回 UniVRM 默认生成器（行为与未打补丁一致），并把原因写进日志。
                VrmLog.Error("MToon10 URP Shader 不可用，材质无法生成");
                return new UrpVrm10MaterialDescriptorGenerator().Get(data, i);
            }

            public MaterialDescriptor GetGltfDefault(string? materialName = null)
            {
                // glTF 里没写材质的兜底。同样不能用 UniVRM 默认生成器（它是 URP/Lit，本游戏没有）。
                var shader = VrmShaderLoader.MToonUrpShader;
                if (shader == null)
                    return new UrpVrm10MaterialDescriptorGenerator().GetGltfDefault(materialName);

                return new MaterialDescriptor(
                    materialName ?? "DefaultMaterial",
                    shader,
                    null,
                    new Dictionary<string, TextureDescriptor>(),
                    new Dictionary<string, float>
                    {
                        ["_AlphaMode"] = 0f,
                        ["_Cutoff"] = 0.5f,
                        ["_DoubleSided"] = 0f,
                    },
                    new Dictionary<string, Color>
                    {
                        ["_Color"] = Color.white,
                        ["_ShadeColor"] = new Color(0.5f, 0.5f, 0.5f, 1f),
                    },
                    new Dictionary<string, Vector4>(),
                    new Action<Material>[]
                    {
                        material => new MToonValidator(material).Validate(),
                    });
            }
        }
}
