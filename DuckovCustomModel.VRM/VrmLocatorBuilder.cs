using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DuckovCustomModel.Core.Data;
using DuckovCustomModel.MonoBehaviours;
using Newtonsoft.Json;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>一个定位锚点的运行态：定义 + 生成出来的 GameObject。</summary>
    public sealed class VrmAnchorState
    {
        /// <summary>锚点名（== <see cref="SocketNames"/> 里的常量）。</summary>
        public string Name = string.Empty;

        /// <summary>挂在哪节骨骼下（装备会跟着动画一起动）。</summary>
        public HumanBodyBones Bone = HumanBodyBones.Chest;

        /// <summary>
        /// 挂在骨骼下的本地偏移（米）。锚点是骨骼的子物体，这个值就是
        /// <c>Transform.localPosition</c> 本身 —— 与姿势无关，所见即所得。
        /// </summary>
        public Vector3 Offset;

        /// <summary>挂在骨骼下的本地朝向（度）。就是 <c>Transform.localRotation</c> 的欧拉角。</summary>
        public Vector3 Euler;

        /// <summary>
        /// 锚点的局部缩放。DCM 用它缩放挂在锚点上的装备 —— 设为 0 可以隐藏该部位的装备。
        /// </summary>
        public float Scale = 1f;

        /// <summary>把世界 Y 对齐到头顶（头盔锚点专用，它同时是身高基准）。</summary>
        public bool AlignToHeadTop;

        /// <summary>生成出来的锚点对象；未生成时为 null。</summary>
        public GameObject? Instance;

        public bool HasInstance => Instance != null;
    }

    /// <summary>单个锚点的手工覆盖（写在 <c>Overrides/&lt;模型&gt;.json</c> 里）。</summary>
    internal sealed class VrmAnchorOverride
    {
        public float[]? Position { get; set; }
        public float[]? EulerAngles { get; set; }
        public float? Scale { get; set; }
        public string? ParentBone { get; set; }
        public bool? Enabled { get; set; }
    }

    internal sealed class VrmAnchorOverrideFile
    {
        /// <summary>
        /// 2 = 锚点的骨骼本地值（localPosition / localRotation 直存）。
        /// 1 = 已废弃的「身体坐标系」格式，读取时会被忽略并要求重新校准。
        /// </summary>
        public int Version { get; set; } = 2;

        public Dictionary<string, VrmAnchorOverride> Locators { get; set; } = new();
    }

    /// <summary>
    /// 给 VRM 自动生成 DCM 需要的 11 个定位锚点（9 内部 + 2 外部）。
    ///
    /// 设计（刻意保持极简）：
    ///   * 锚点全部挂在 VRM 自己对应的 Humanoid 骨骼下（枪跟手、盔跟头、包跟胸…），
    ///     作为骨骼子物体随动画与身高一起动；
    ///   * 状态值就是锚点的 localPosition / localRotation（骨骼本地），**没有任何坐标系换算** ——
    ///     面板里看到的数字、保存进文件的数字、加载时套回去的数字，是同一组数字；
    ///   * <see cref="Defaults"/> 是代码里的出厂值，玩家在 F9 面板里拖到满意、点保存，
    ///     写成 <c>Overrides/&lt;模型文件名&gt;.json</c>，之后所有会话原样复现。
    /// </summary>
    internal static class VrmLocatorBuilder
    {
        private const string RootName = "VrmLocators";

        /// <summary>
        /// 出厂默认值 —— 锚点的骨骼本地 localPosition / localRotation（米 / 度），零换算。
        ///
        /// 手部数值来自实机校准（2026-10-04）固化的出厂基准；
        /// 其余锚点保持全零中性起点（实测校准值恰好与默认值重合）。
        /// VRM 手骨本地朝向高度一致，这套基准换模型基本直接可用，只需微调。
        /// </summary>
        private static readonly VrmAnchorState[] Defaults =
        {
            new()
            {
                Name = SocketNames.LeftHand, Bone = HumanBodyBones.LeftHand,
                Offset = new Vector3(-0.0229f, -0.0184f, 0.0019f),
                Euler = new Vector3(-11.68f, -107.72f, -70.09f), Scale = 0.8300f,
            },
            new()
            {
                Name = SocketNames.RightHand, Bone = HumanBodyBones.RightHand,
                Offset = new Vector3(0.0563f, -0.0356f, -0.0083f),
                Euler = new Vector3(-7.14f, 106.16f, 99.25f), Scale = 0.5181f,
            },
            new() { Name = SocketNames.Armor, Bone = HumanBodyBones.Chest },
            new()
            {
                Name = SocketNames.Helmet, Bone = HumanBodyBones.Head, AlignToHeadTop = true,
            },
            new() { Name = SocketNames.Face, Bone = HumanBodyBones.Head },
            new() { Name = SocketNames.Backpack, Bone = HumanBodyBones.Chest },
            new() { Name = SocketNames.MeleeWeapon, Bone = HumanBodyBones.Hips },
            new() { Name = SocketNames.PopText, Bone = HumanBodyBones.Head },
            new() { Name = SocketNames.Vehicle, Bone = HumanBodyBones.Hips },
            new() { Name = SocketNames.PaperBox, Bone = HumanBodyBones.Chest },
            new() { Name = SocketNames.Carriable, Bone = HumanBodyBones.Chest },
        };

        private static readonly List<VrmAnchorState> StatesInternal = new(Defaults.Length);
        private static readonly Dictionary<string, VrmAnchorState> ByName = new(StringComparer.Ordinal);

        private static VrmAnchorFrame? _frame;
        private static GameObject? _modelRoot;
        private static string? _activeModelId;
        private static GameObject? _root;
        private static float _headTopWorldY;

        public static IReadOnlyList<VrmAnchorState> States => StatesInternal;

        public static VrmAnchorFrame? Frame => _frame;

        public static string? ActiveModelId => _activeModelId;

        public static int GeneratedCount
        {
            get
            {
                var n = 0;
                foreach (var s in StatesInternal)
                    if (s.HasInstance)
                        n++;
                return n;
            }
        }

        public static void Build(ModelHandler handler, GameObject modelRoot, VrmAnchorFrame? frame)
        {
            StatesInternal.Clear();
            ByName.Clear();
            _frame = frame;
            _modelRoot = modelRoot;
            _activeModelId = handler.CurrentModelInfo?.ModelID;

            var options = VrmConfig.Current.Locators;
            if (!options.AutoGenerate)
            {
                VrmLog.Detail("锚点自动生成已关闭。");
                return;
            }

            if (frame == null)
            {
                VrmLog.Warn("骨架测量不可用，跳过锚点生成。");
                return;
            }

            _headTopWorldY = frame.HeadTopWorldY;

            var overrides = LoadOverrides(_activeModelId, options.OverrideDirectory);

            // 重生成时先清掉上一批（Destroy 是延迟的，但锚点是按名字找的，不影响）。
            var previous = modelRoot.transform.Find(RootName);
            if (previous != null) UnityEngine.Object.Destroy(previous.gameObject);

            _root = new GameObject(RootName);
            _root.transform.SetParent(modelRoot.transform, false);

            var applied = 0;
            foreach (var def in Defaults)
            {
                var state = new VrmAnchorState
                {
                    Name = def.Name,
                    Bone = def.Bone,
                    Offset = def.Offset,
                    Euler = def.Euler,
                    Scale = def.Scale,
                    AlignToHeadTop = def.AlignToHeadTop,
                };

                if (overrides.TryGetValue(state.Name, out var ov) && ov != null)
                {
                    if (ov.Enabled == false)
                    {
                        StatesInternal.Add(state);
                        ByName[state.Name] = state;
                        continue;
                    }

                    if (ov.Position is { Length: 3 } p) state.Offset = new Vector3(p[0], p[1], p[2]);
                    if (ov.EulerAngles is { Length: 3 } e) state.Euler = new Vector3(e[0], e[1], e[2]);
                    if (ov.Scale is { } sc) state.Scale = Mathf.Max(0f, sc);
                    if (ov.ParentBone is { Length: > 0 } parentName &&
                        Enum.TryParse<HumanBodyBones>(parentName, true, out var parsed) &&
                        frame.Has(parsed))
                        state.Bone = parsed;

                    applied++;
                }

                StatesInternal.Add(state);
                ByName[state.Name] = state;
                Apply(state);
            }

            VrmLog.Info($"锚点已生成: {GeneratedCount} 个（挂在骨骼下，随动画与身高一起动）" +
                        (applied > 0 ? $"，已应用 {applied} 处手工校正" : string.Empty));

            if (options.DebugPanelEnabled) VrmLocatorTuner.Ensure(modelRoot, handler);
        }

        /// <summary>取某个锚点的出厂默认值（微调面板的「重置本项」用）。</summary>
        public static bool TryGetDefault(string name, out VrmAnchorState defaults)
        {
            foreach (var def in Defaults)
                if (string.Equals(def.Name, name, StringComparison.Ordinal))
                {
                    defaults = def;
                    return true;
                }

            defaults = new VrmAnchorState();
            return false;
        }

        /// <summary>把某个锚点的定义套到它的 GameObject 上（微调面板实时调用）。</summary>
        public static void Apply(VrmAnchorState state)
        {
            var frame = _frame;
            if (frame == null || state.Name.Length == 0) return;

            var bone = frame.Bone(state.Bone);
            if (bone == null) return;

            var options = VrmConfig.Current.Locators;

            if (state.Instance == null) state.Instance = new GameObject(state.Name);

            var tf = state.Instance.transform;

            // 锚点直接挂在骨骼下：随动画动、随身高缩放。骨骼可能被微调面板换过。
            if (tf.parent != bone) tf.SetParent(bone, false);

            // 状态值 = 骨骼本地值，直读直写，没有任何坐标系换算。
            tf.localPosition = state.Offset;
            tf.localRotation = Quaternion.Euler(state.Euler);
            tf.localScale = Vector3.one * Mathf.Max(0f, state.Scale);

            if (state.AlignToHeadTop && _headTopWorldY > 0f)
            {
                // 头盔锚点的世界 Y 对齐到「头顶」，这样它同时就是身高基准。
                var worldPos = bone.TransformPoint(tf.localPosition);
                worldPos.y = _headTopWorldY + options.HeadTopPadding * frame.Unit;
                tf.localPosition = bone.InverseTransformPoint(worldPos);
            }
        }

        /// <summary>
        /// 头盔锚点在「模型本地空间」的高度（不受 DCM 身高缩放影响）。
        /// 由 <see cref="HarmonyPatches"/> 在锚点生成后回写进目录缓存，
        /// 供 DCM 的逐模型身高滑条在没有 prefab 可量时使用。
        /// </summary>
        public static float LocalHelmetHeight()
        {
            var frame = _frame;
            var root = _modelRoot;
            if (frame == null || root == null) return 0f;

            var scaleY = root.transform.lossyScale.y;
            if (Mathf.Abs(scaleY) < 1e-5f) scaleY = 1f;

            var worldY = _headTopWorldY + VrmConfig.Current.Locators.HeadTopPadding * frame.Unit;
            return (worldY - root.transform.position.y) / scaleY;
        }

        #region 手工校正文件

        public static string OverridePath(string? modelId, string sub)
        {
            var name = string.IsNullOrEmpty(modelId)
                ? "unknown"
                : modelId.StartsWith(VrmCatalog.VrmModelIdPrefix, StringComparison.Ordinal)
                    ? modelId.Substring(VrmCatalog.VrmModelIdPrefix.Length)
                    : modelId;

            return Path.Combine(VrmModPath.ModConfigDirectory, sub, Sanitize(name) + ".json");
        }

        private static Dictionary<string, VrmAnchorOverride> LoadOverrides(string? modelId, string sub)
        {
            var result = new Dictionary<string, VrmAnchorOverride>(StringComparer.Ordinal);
            try
            {
                var path = OverridePath(modelId, sub);
                if (!File.Exists(path)) return result;

                var data = JsonConvert.DeserializeObject<VrmAnchorOverrideFile>(File.ReadAllText(path));
                if (data?.Locators == null) return result;

                if (data.Version < 2)
                {
                    // 旧格式是「身体坐标系」值，与新语义（骨骼本地值）不兼容，直接忽略。
                    VrmLog.Warn($"忽略旧格式锚点校正文件（{Path.GetFileName(path)}），请重新校准并保存。");
                    return result;
                }

                foreach (var kv in data.Locators)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                        result[kv.Key] = kv.Value;

                VrmLog.Detail($"已读取锚点校正: {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                VrmLog.Warn($"读取锚点校正文件失败: {e.Message}");
            }

            return result;
        }

        /// <summary>把内存里锚点的现状写回校正文件（微调面板的「保存」）。</summary>
        public static bool SaveOverrides(string? modelId, string sub)
        {
            var path = OverridePath(modelId, sub);
            var file = CollectOverrides();
            if (file == null) return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
                if (File.Exists(path)) File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
                VrmLog.Info($"锚点校正已保存: {path}");
                return true;
            }
            catch (Exception e)
            {
                VrmLog.Error($"保存锚点校正失败: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从当前锚点状态收集一份校正数据（骨骼本地的位置/朝向/骨骼/缩放，直存直取）。
        /// 没有可用锚点时返回 null。
        /// </summary>
        private static VrmAnchorOverrideFile? CollectOverrides()
        {
            var frame = _frame;
            if (frame == null)
            {
                VrmLog.Warn("骨架测量不可用，无法保存锚点校正。");
                return null;
            }

            try
            {
                var file = new VrmAnchorOverrideFile();

                foreach (var state in StatesInternal)
                {
                    var go = state.Instance;
                    if (go == null) continue;

                    var tf = go.transform;
                    var bone = tf.parent;
                    if (bone == null) continue;

                    file.Locators[state.Name] = new VrmAnchorOverride
                    {
                        Position = new[] { state.Offset.x, state.Offset.y, state.Offset.z },
                        EulerAngles = new[]
                        {
                            Normalize(state.Euler.x), Normalize(state.Euler.y), Normalize(state.Euler.z),
                        },
                        ParentBone = FindBoneName(frame, bone),
                        Scale = Mathf.Max(0f, state.Scale),
                    };
                }

                if (file.Locators.Count == 0)
                {
                    VrmLog.Warn("当前没有可保存的锚点。");
                    return null;
                }

                return file;
            }
            catch (Exception e)
            {
                VrmLog.Error($"收集锚点状态失败: {e.Message}");
                return null;
            }
        }

        private static string? FindBoneName(VrmAnchorFrame frame, Transform bone)
        {
            foreach (var b in VrmAnchorFrame.BoneOrder)
                if (frame.Bone(b) == bone)
                    return b.ToString();

            return null;
        }

        private static float Normalize(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        #endregion
    }
}
