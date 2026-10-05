using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UniGLTF;
using UniGLTF.Utils;
using UniVRM10;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 让 VRM 的 SpringBone（头发 / 裙摆 / 尾巴等动骨）真正动起来。
    ///
    /// DCM 用 <c>Object.Instantiate</c> 把我们加载好的 VRM prefab 克隆到角色身上。
    /// Unity 的克隆走原生序列化，只会复制 <c>[SerializeField]</c>/public 字段；
    /// 而 UniVRM 在导入阶段构建的「运行时数据」存在非序列化字段里，克隆体上全部丢失：
    ///
    /// 1) <b>弹簧数据</b>：源 prefab 上 <c>Vrm10Instance.SpringBone</c> 里存着 springs/joints/colliders
    ///    的组件引用列表。克隆体上这些列表可能是空的 —— 表现为日志里
    ///    <c>springs=0 joints=0 colliderGroups=0</c>，动骨自然一动不动。
    ///    （VRM10SpringBoneJoint / VRM10SpringBoneColliderGroup 组件本身是 MonoBehaviour，
    ///    会被正常克隆过来，缺的只是「把它们组织起来」的列表。）
    ///
    /// 2) <b>静止姿态（rest pose）</b>：<c>RuntimeGltfInstance._initialTransformStates</c>
    ///    是 private readonly 非序列化字段，克隆体上是空字典。而
    ///    <c>Vrm10Instance.DefaultTransformStates</c> 只要有 RuntimeGltfInstance 就直接用这份空表
    ///    （不再走「现场构建」的回退路径），于是每个关节的 DefaultLocalRotation 退化成 identity，
    ///    弹簧的静止基准全错。
    ///
    /// 修复方式：从源 prefab 把这两份数据按「层级路径」重映射到克隆体上
    /// （克隆体的层级和源完全一致，路径可以一一对应），
    /// 然后 DisposeRuntime 让 UniVRM 用修好的数据重建弹簧缓冲区。
    ///
    /// 驱动方式：把 <c>Vrm10Instance.UpdateType</c> 设为 None，由一个执行顺序极靠后的
    /// 驱动器统一调用 <c>Runtime.Process()</c>，保证动骨在所有动画 / IK 之后求值。
    /// </summary>
    public static class VrmSpringBoneFixer
    {
        private static readonly FieldInfo? InitialStatesField =
            typeof(RuntimeGltfInstance).GetField("_initialTransformStates",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo? DefaultStatesField =
            typeof(Vrm10Instance).GetField("m_defaultTransformStates",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Fix(GameObject? modelRoot)
        {
            if (modelRoot == null) return;

            var vrm = modelRoot.GetComponentInChildren<Vrm10Instance>(true);
            if (vrm == null) return;

            // 找源 prefab：克隆体上的 VrmModelMarker 记着 ModelId。
            GameObject? original = null;
            var marker = modelRoot.GetComponentInChildren<VrmModelMarker>(true);
            if (!string.IsNullOrEmpty(marker?.ModelId) &&
                !VrmModelRegistry.TryGetLoadedPrefab(marker.ModelId, out original))
                original = null;

            var remapped = RestoreSpringBoneData(vrm, original);
            var poseSource = RestoreInitialPose(vrm, original);

            // 动作捕捉式的"限位"：胸部过度变形 / 穿模在这里被夹住。
            // 必须在 DisposeRuntime 之前改，否则改的是已经构建好的物理缓冲区。
            VrmSpringBoneTuner.Apply(modelRoot, vrm);

            // 让 Runtime / SpringBone 用修好的数据重建。
            ResetDefaultStatesCache(vrm);
            vrm.DisposeRuntime();

            // 关闭自调度，交给下面的驱动器统一驱动。
            vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
            vrm.enabled = true;

            var (springs, joints, colliders) = CountSpringBones(vrm);
            if (springs == 0)
            {
                // 诊断线索：如果关节组件还在、只是列表为空，说明是克隆丢数据（而不是模型没带弹簧）。
                var jointComponents = modelRoot.GetComponentsInChildren<VRM10SpringBoneJoint>(true).Length;
                VrmLog.Warn(jointComponents > 0
                    ? $"模型上有 {jointComponents} 个 VRM10SpringBoneJoint 组件，但弹簧列表为空且未能从源模型恢复，动骨不会动。"
                    : "该模型没有 SpringBone 数据（导出 VRM 时未包含骨架物理），动骨不会动。");
            }
            else
            {
                VrmLog.Info($"动骨就绪: springs={springs} joints={joints} colliderGroups={colliders}" +
                            (remapped ? "（弹簧数据已从源模型重映射）" : "") +
                            $"，静止姿态 {poseSource}");
            }

            var driver = modelRoot.GetComponent<VrmSpringBoneDriver>();
            if (driver == null) driver = modelRoot.AddComponent<VrmSpringBoneDriver>();
            driver.Bind(vrm);
        }

        /// <summary>
        /// 把源 prefab 上的 springs / colliders 列表按层级路径重映射到克隆体上。
        /// 返回是否执行了重映射。
        /// </summary>
        private static bool RestoreSpringBoneData(Vrm10Instance cloneVrm, GameObject? originalRoot)
        {
            var origVrm = originalRoot == null ? null : originalRoot.GetComponentInChildren<Vrm10Instance>(true);
            if (origVrm == null) return false;

            var origSpringBone = origVrm.SpringBone;
            if (origSpringBone?.Springs == null || origSpringBone.Springs.Count == 0) return false;

            var cloneSpringBone = cloneVrm.SpringBone;
            if (cloneSpringBone == null)
            {
                cloneSpringBone = new Vrm10InstanceSpringBone();
                cloneVrm.SpringBone = cloneSpringBone;
            }

            // 序列化完好的话克隆体上本来就有数据，无需修复。
            if (cloneSpringBone.Springs != null && cloneSpringBone.Springs.Count > 0) return false;

            var origMap = BuildPathMap(origVrm.transform);
            var cloneMap = BuildPathMap(cloneVrm.transform);

            Transform? Map(Component? c)
            {
                if (c == null) return null;
                var path = PathOf(c.transform, origVrm.transform);
                return path != null && cloneMap.TryGetValue(path, out var cloneTf) ? cloneTf : null;
            }

            // 顶层碰撞体组
            cloneSpringBone.ColliderGroups.Clear();
            if (origSpringBone.ColliderGroups != null)
                foreach (var group in origSpringBone.ColliderGroups)
                {
                    var cloneTf = Map(group);
                    if (cloneTf == null) continue;
                    var cloneGroup = cloneTf.GetComponent<VRM10SpringBoneColliderGroup>();
                    if (cloneGroup != null) cloneSpringBone.ColliderGroups.Add(cloneGroup);
                }

            // 弹簧（关节顺序必须保持原样，物理链依赖它）
            var springs = new List<Vrm10InstanceSpringBone.Spring>(origSpringBone.Springs.Count);
            foreach (var spring in origSpringBone.Springs)
            {
                var cloneSpring = new Vrm10InstanceSpringBone.Spring(spring.Name);
                cloneSpring.Center = Map(spring.Center);

                if (spring.ColliderGroups != null)
                    foreach (var group in spring.ColliderGroups)
                    {
                        var cloneTf = Map(group);
                        if (cloneTf == null) continue;
                        var cloneGroup = cloneTf.GetComponent<VRM10SpringBoneColliderGroup>();
                        if (cloneGroup != null) cloneSpring.ColliderGroups.Add(cloneGroup);
                    }

                if (spring.Joints != null)
                    foreach (var joint in spring.Joints)
                    {
                        var cloneTf = Map(joint);
                        if (cloneTf == null) continue;
                        var cloneJoint = cloneTf.GetComponent<VRM10SpringBoneJoint>();
                        if (cloneJoint != null) cloneSpring.Joints.Add(cloneJoint);
                    }

                springs.Add(cloneSpring);
            }

            cloneSpringBone.Springs!.Clear();
            cloneSpringBone.Springs.AddRange(springs);
            return true;
        }

        /// <summary>
        /// 重建克隆体的静止姿态表。优先从源 prefab 的初姿态重映射（最准确），
        /// 找不到源时退化为「克隆体当前姿态」（刚 Instantiate 完通常仍是静止姿态）。
        /// 返回给日志用的一句话。
        /// </summary>
        private static string RestoreInitialPose(Vrm10Instance cloneVrm, GameObject? originalRoot)
        {
            var cloneRgi = cloneVrm.GetComponent<RuntimeGltfInstance>();
            if (cloneRgi == null || InitialStatesField == null) return "读取失败（跳过）";
            if (!(InitialStatesField.GetValue(cloneRgi) is IDictionary<Transform, TransformState> dict))
                return "读取失败（跳过）";

            if (dict.Count > 0) return $"{dict.Count} 项（克隆自带）";
            dict.Clear();

            var origRgi = originalRoot == null ? null : originalRoot.GetComponentInChildren<RuntimeGltfInstance>(true);
            if (origRgi != null && origRgi.InitialTransformStates.Count > 0)
            {
                var origRoot = origRgi.transform;
                var cloneMap = BuildPathMap(cloneVrm.transform);

                foreach (var kv in origRgi.InitialTransformStates)
                {
                    if (kv.Key == null) continue;
                    var path = PathOf(kv.Key, origRoot);
                    if (path == null || !cloneMap.TryGetValue(path, out var cloneTf)) continue;
                    dict[cloneTf] = kv.Value;
                }

                if (dict.Count > 0) return $"{dict.Count} 项（从源模型重映射）";
            }

            // 兜底：拿克隆体当前姿态当静止姿态。刚 Instantiate 完通常还处于静止姿态，
            // 但如果模型已经被摆过姿势，这里就会偏 —— 所以上面那条路径永远是首选。
            foreach (var tf in cloneVrm.GetComponentsInChildren<Transform>(true))
                dict[tf] = new TransformState(tf);
            return $"{dict.Count} 项（退化：占用克隆体当前姿态）";
        }

        /// <summary>清掉 <c>Vrm10Instance</c> 对默认姿态的缓存，否则修复不会生效。</summary>
        private static void ResetDefaultStatesCache(Vrm10Instance vrm)
        {
            DefaultStatesField?.SetValue(vrm, null);
        }

        private static (int springs, int joints, int colliderGroups) CountSpringBones(Vrm10Instance vrm)
        {
            var sb = vrm.SpringBone;
            if (sb?.Springs == null) return (0, 0, 0);

            var joints = 0;
            var colliders = sb.ColliderGroups?.Count ?? 0;
            foreach (var spring in sb.Springs)
            {
                joints += spring.Joints?.Count ?? 0;
                colliders += spring.ColliderGroups?.Count ?? 0;
            }

            return (sb.Springs.Count, joints, colliders);
        }

        private static Dictionary<string, Transform> BuildPathMap(Transform root)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var path = PathOf(t, root);
                if (path != null) map[path] = t;
            }

            return map;
        }

        /// <summary>计算相对 root 的层级路径；不属于该 root 时返回 null。</summary>
        private static string? PathOf(Transform? t, Transform root)
        {
            if (t == null) return null;

            var sb = new StringBuilder();
            var cur = t;
            while (cur != null && cur != root)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }

            return cur == root ? sb.ToString() : null;
        }
    }

    /// <summary>
    /// 统一驱动点：执行顺序放到非常靠后，保证在所有 Animator / IK / 约束之后再跑弹簧模拟。
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class VrmSpringBoneDriver : MonoBehaviour
    {
        private Vrm10Instance? _vrm;
        private bool _broken;
        private bool _loggedFirstProcess;

        public void Bind(Vrm10Instance vrm)
        {
            _vrm = vrm;
            _broken = false;
            _loggedFirstProcess = false;
        }

        private void LateUpdate()
        {
            if (_broken || _vrm == null) return;

            try
            {
                var runtime = _vrm.Runtime;

                if (!_loggedFirstProcess)
                {
                    _loggedFirstProcess = true;
                    VrmLog.Detail($"动骨驱动已启动 (initPose={runtime.InitPose?.Count ?? 0}, " +
                                  $"springbone={(runtime.SpringBone == null ? "null" : "ok")})");
                }

                runtime.Process();
            }
            catch (Exception e)
            {
                _broken = true;
                VrmLog.Error($"动骨驱动抛异常，已停止: {e}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                _vrm?.DisposeRuntime();
            }
            catch (Exception e)
            {
                VrmLog.Warn($"释放动骨 Runtime 失败: {e.Message}");
            }

            _vrm = null;
        }
    }
}
