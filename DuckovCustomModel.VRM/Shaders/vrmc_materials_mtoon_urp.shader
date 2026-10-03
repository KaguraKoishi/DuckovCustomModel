Shader "VRM10/Universal Render Pipeline/MToon10"
{
    Properties
    {
        // Rendering
        _AlphaMode ("alphaMode", Int) = 0
        _TransparentWithZWrite ("mtoon.transparentWithZWrite", Int) = 0
        _Cutoff ("alphaCutoff", Range(0, 1)) = 0.5 // Unity specified name
        _RenderQueueOffset ("mtoon.renderQueueOffsetNumber", Int) = 0
        _DoubleSided ("doubleSided", Int) = 0

        // Lighting
        _Color ("pbrMetallicRoughness.baseColorFactor", Color) = (1, 1, 1, 1) // Unity specified name
        _MainTex ("pbrMetallicRoughness.baseColorTexture", 2D) = "white" {} // Unity specified name
        _ShadeColor ("mtoon.shadeColorFactor", Color) = (1, 1, 1, 1)
        _ShadeTex ("mtoon.shadeMultiplyTexture", 2D) = "white" {}
        [Normal] _BumpMap ("normalTexture", 2D) = "bump" {} // Unity specified name
        _BumpScale ("normalTexture.scale", Float) = 1.0 // Unity specified name
        _ShadingShiftFactor ("mtoon.shadingShiftFactor", Range(-1, 1)) = -0.05
        _ShadingShiftTex ("mtoon.shadingShiftTexture", 2D) = "black" {} // channel R
        _ShadingShiftTexScale ("mtoon.shadingShiftTexture.scale", Float) = 1
        _ShadingToonyFactor ("mtoon.shadingToonyFactor", Range(0, 1)) = 0.95

        // GI
        _GiEqualization ("mtoon.giEqualizationFactor", Range(0, 1)) = 0.9

        // Emission
        [HDR] _EmissionColor ("emissiveFactor", Color) = (0, 0, 0, 1) // Unity specified name
        _EmissionMap ("emissiveTexture", 2D) = "white" {} // Unity specified name

        // Rim Lighting
        _MatcapColor ("mtoon.matcapFactor", Color) = (0, 0, 0, 1) // 仕様のデフォルト値は白だが、過去の仕様違反 UniVRM 実装アプリケーションのために黒とする。 https://github.com/vrm-c/UniVRM/pull/2594
        _MatcapTex ("mtoon.matcapTexture", 2D) = "black" {}
        _RimColor ("mtoon.parametricRimColorFactor", Color) = (0, 0, 0, 1)
        _RimFresnelPower ("mtoon.parametricRimFresnelPowerFactor", Range(0, 100)) = 5.0
        _RimLift ("mtoon.parametricRimLiftFactor", Range(0, 1)) = 0
        _RimTex ("mtoon.rimMultiplyTexture", 2D) = "white" {}
        _RimLightingMix ("mtoon.rimLightingMixFactor", Range(0, 1)) = 1

        // Outline
        _OutlineWidthMode ("mtoon.outlineWidthMode", Int) = 0
        [PowerSlider(2.2)] _OutlineWidth ("mtoon.outlineWidthFactor", Range(0, 0.05)) = 0
        _OutlineWidthTex ("mtoon.outlineWidthMultiplyTexture", 2D) = "white" {} // channel G
        _OutlineColor ("mtoon.outlineColorFactor", Color) = (0, 0, 0, 1)
        _OutlineLightingMix ("mtoon.outlineLightingMixFactor", Range(0, 1)) = 1

        // UV Animation
        _UvAnimMaskTex ("mtoon.uvAnimationMaskTexture", 2D) = "white" {} // channel B
        _UvAnimScrollXSpeed ("mtoon.uvAnimationScrollXSpeedFactor", Float) = 0
        _UvAnimScrollYSpeed ("mtoon.uvAnimationScrollYSpeedFactor", Float) = 0
        _UvAnimRotationSpeed ("mtoon.uvAnimationRotationSpeedFactor", Float) = 0

        // Unity ShaderPass Mode
        _M_CullMode ("_CullMode", Float) = 2.0
        _M_SrcBlend ("_SrcBlend", Float) = 1.0
        _M_DstBlend ("_DstBlend", Float) = 0.0
        _M_ZWrite ("_ZWrite", Float) = 1.0
        _M_AlphaToMask ("_AlphaToMask", Float) = 0.0

        // etc
        _M_DebugMode ("_DebugMode", Float) = 0.0

        // for Editor
        _M_EditMode ("_EditMode", Float) = 0.0
    }

    // Shader Model 3.0
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        // Universal Forward Only Pass
        //
        // ★ 本模组对官方 shader 的唯一改动就在下面这两行 ★
        //
        // Escape from Duckov 的 URP 渲染器运行在 Deferred 模式。Deferred 下，
        // renderQueue <= 2500（不透明区间）的物体只会被这两类 Pass 绘制：
        //   * G-Buffer Pass      —— LightMode = "UniversalGBuffer"
        //   * Forward-Only Pass  —— LightMode = "UniversalForwardOnly" / "SRPDefaultUnlit" / "LightweightForward"
        // 注意 Forward-Only 的 ShaderTagId 列表里【不包含】"UniversalForward"
        // （见 UniversalRenderer 构造函数：m_RenderOpaqueForwardOnlyPass 的 shaderTagIds）。
        // 只有 Forward 模式的 DrawObjectsPass 默认列表才是
        // [SRPDefaultUnlit, UniversalForward, UniversalForwardOnly]。
        //
        // 官方 MToon10 URP shader 原本只有 "UniversalForward" 一个 Pass，
        // 因此在不透明队列下【完全不会被绘制】——表现为灰模 / 直接看到背景；
        // 一旦把 renderQueue 推到 2501 以上，就会被透明 Pass（默认列表含 UniversalForward）绘制，
        // 这就是「手动把 2000 改成 2600 就恢复正常」的真实原因。
        //
        // 修法：把本 Pass 的 Name / LightMode 就地改成 "UniversalForwardOnly"。
        //   * Deferred 下 → 命中 Forward-Only 列表，拿到完整前向光照（SetupLights 无条件调
        //     ForwardLights.Setup，所以主光 + 附加光齐备）。
        //   * Forward 模式下 → 命中默认列表索引 2，Unity 对每个物体只取优先级最高的一个匹配 Pass，
        //     因此仍然只绘制一次。
        // 刻意【不新增一个 Pass】，因为那会让该 shader 的 multi_compile 变体矩阵翻倍，
        // 在包含 1.2 GB VRM 资产的工程里会导致 AssetBundle 构建时间爆炸。
        Pass
        {
            PackageRequirements
            {
                "unity": "2021.3"
                "com.unity.render-pipelines.universal": "12.0.0"
            }

            Name "UniversalForwardOnly"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull [_M_CullMode]
            Blend [_M_SrcBlend] [_M_DstBlend]
            ZWrite [_M_ZWrite]
            ZTest LEqual
            BlendOp Add, Max
            AlphaToMask [_M_AlphaToMask]

            HLSLPROGRAM
            #pragma target 3.0

            // Unity defined keywords
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #pragma multi_compile __ _ALPHATEST_ON _ALPHABLEND_ON
            #pragma multi_compile __ _NORMALMAP
            #pragma multi_compile __ _MTOON_EMISSIVEMAP
            #pragma multi_compile __ _MTOON_RIMMAP
            #pragma multi_compile __ _MTOON_PARAMETERMAP

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            
            #pragma vertex MToonVertex
            #pragma fragment MToonFragment

            #define MTOON_URP

            #include "./vrmc_materials_mtoon_forward_vertex.hlsl"
            #include "./vrmc_materials_mtoon_forward_fragment.hlsl"
            ENDHLSL
        }

        // MToon Outline Pass
        Pass
        {
            PackageRequirements
            {
                "unity": "2021.3"
                "com.unity.render-pipelines.universal": "12.0.0"
            }

            Name "MToonOutline"
            Tags { "LightMode" = "MToonOutline" }

            Cull Front
            Blend [_M_SrcBlend] [_M_DstBlend]
            ZWrite [_M_ZWrite]
            ZTest LEqual
            Offset 1, 1
            BlendOp Add, Max
            AlphaToMask [_M_AlphaToMask]

            HLSLPROGRAM
            #pragma target 3.0

            // Unity defined keywords
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #pragma multi_compile __ _ALPHATEST_ON _ALPHABLEND_ON
            #pragma multi_compile __ _NORMALMAP
            #pragma multi_compile __ _MTOON_EMISSIVEMAP
            #pragma multi_compile __ _MTOON_RIMMAP
            #pragma multi_compile __ _MTOON_PARAMETERMAP
            #pragma multi_compile __ _MTOON_OUTLINE_WORLD _MTOON_OUTLINE_SCREEN

            #pragma vertex MToonVertex
            #pragma fragment MToonFragment

            #define MTOON_URP
            #define MTOON_PASS_OUTLINE

            #include "./vrmc_materials_mtoon_forward_vertex.hlsl"
            #include "./vrmc_materials_mtoon_forward_fragment.hlsl"
            ENDHLSL
        }

        //  Depth Only Pass
        Pass
        {
            PackageRequirements
            {
                "unity": "2021.3"
                "com.unity.render-pipelines.universal": "12.0.0"
            }

            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull [_M_CullMode]
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0

            // Unity defined keywords
            #pragma multi_compile_instancing

            #pragma multi_compile __ _ALPHATEST_ON _ALPHABLEND_ON

            #pragma vertex MToonDepthOnlyVertex
            #pragma fragment MToonDepthOnlyFragment

            #define MTOON_URP
            
            #include "./vrmc_materials_mtoon_depthonly_vertex.hlsl"
            #include "./vrmc_materials_mtoon_depthonly_fragment.hlsl"
            ENDHLSL
        }

        //  Depth Normals Pass
        Pass
        {
            PackageRequirements
            {
                "unity": "2021.3"
                "com.unity.render-pipelines.universal": "12.0.0"
            }

            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull [_M_CullMode]
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0

            // Unity defined keywords
            #pragma multi_compile_instancing

            #pragma multi_compile __ _ALPHATEST_ON _ALPHABLEND_ON
            #pragma multi_compile __ _NORMALMAP

            #pragma vertex MToonDepthNormalsVertex
            #pragma fragment MToonDepthNormalsFragment

            #define MTOON_URP
            
            #include "./vrmc_materials_mtoon_depthnormals_vertex.hlsl"
            #include "./vrmc_materials_mtoon_depthnormals_fragment.hlsl"
            ENDHLSL
        }

        //  Shadow Caster Pass
        Pass
        {
            PackageRequirements
            {
                "unity": "2021.3"
                "com.unity.render-pipelines.universal": "12.0.0"
            }

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull [_M_CullMode]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0

            // Unity defined keywords
            #pragma multi_compile_instancing

            #pragma multi_compile __ _ALPHATEST_ON _ALPHABLEND_ON

            #pragma vertex MToonShadowCasterVertex
            #pragma fragment MToonShadowCasterFragment

            #define MTOON_URP
            
            #include "./vrmc_materials_mtoon_shadowcaster_vertex.hlsl"
            #include "./vrmc_materials_mtoon_shadowcaster_fragment.hlsl"
            ENDHLSL
        }

        Pass
        {
            PackageRequirements
            {
                "unity": "6000.0"
                "com.unity.render-pipelines.universal": "17.0.0"
            }

            Name "XRMotionVectors"
            Tags { "LightMode" = "XRMotionVectors" }
            ColorMask RGBA

            // Stencil write for obj motion pixels
            Stencil
            {
                WriteMask 1
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local_vertex _ADD_PRECOMPUTED_VELOCITY
            #define APPLICATION_SPACE_WARP_MOTION 1

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "VRM10.MToon10.Editor.MToonInspector"
}
