using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 一次性打印当前渲染管线配置，便于确认游戏到底跑的是 Forward 还是 Deferred。
    /// 全部用反射，取不到就安静跳过，绝不影响主流程。仅在 verbose 模式下输出。
    /// </summary>
    internal static class UrpDiagnostics
    {
        private const BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _logged;

        public static void LogOnce()
        {
            if (_logged || !VrmLog.Verbose) return;
            _logged = true;

            try
            {
                var asset = GraphicsSettings.currentRenderPipeline;
                if (asset == null)
                {
                    VrmLog.Detail("当前不是 SRP（Built-in 渲染管线）");
                    return;
                }

                VrmLog.Detail($"pipelineAsset={asset.GetType().FullName} " +
                              $"unity={Application.unityVersion} backend={SystemInfo.graphicsDeviceType}");

                var renderer = FindRendererInstance(asset);
                if (renderer == null)
                {
                    VrmLog.Detail("未能拿到 ScriptableRenderer 实例，跳过渲染模式诊断");
                    return;
                }

                var modeField = renderer.GetType().GetField("m_RenderingMode", AllInstance);
                var mode = modeField?.GetValue(renderer)?.ToString() ?? "unknown";
                var opaqueMask = renderer.GetType().GetField("m_OpaqueLayerMask", AllInstance)?.GetValue(renderer);
                var transparentMask =
                    renderer.GetType().GetField("m_TransparentLayerMask", AllInstance)?.GetValue(renderer);

                VrmLog.Detail($"renderer={renderer.GetType().Name} renderingMode={mode} " +
                              $"opaqueMask={opaqueMask} transparentMask={transparentMask}");
            }
            catch (Exception e)
            {
                VrmLog.Detail($"渲染管线诊断失败（不影响使用）: {e.Message}");
            }
        }

        private static object? FindRendererInstance(object asset)
        {
            var type = asset.GetType();

            // URP 的 UniversalRenderPipelineAsset 通过 scriptableRenderer 属性暴露渲染器实例。
            var prop = type.GetProperty("scriptableRenderer", AllInstance);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                try
                {
                    var value = prop.GetValue(asset);
                    if (value != null) return value;
                }
                catch
                {
                    // ignored
                }

            // 兜底：找类型名里带 Renderer 的普通字段。
            foreach (var field in type.GetFields(AllInstance))
            {
                if (field.FieldType.IsArray || field.FieldType.IsGenericType) continue;
                if (field.FieldType.Name.IndexOf("Renderer", StringComparison.Ordinal) < 0) continue;

                try
                {
                    var value = field.GetValue(asset);
                    if (value != null) return value;
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }
    }
}
