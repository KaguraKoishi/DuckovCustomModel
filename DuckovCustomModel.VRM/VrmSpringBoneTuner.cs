using System;
using System.Collections.Generic;
using System.Text;
using UniGLTF.SpringBoneJobs;
using UniVRM10;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 动骨参数调校。
    ///
    /// 解决「胸部动骨过度变形 / 往身体里穿模」这件事。
    /// VRM 0.x（<c>VRMSpringBone</c>）本身没有任何"限位"概念，只能靠调刚度/阻力硬扛；
    /// VRM 1.0 起多了两件武器，而 UniVRM 的 VRM10 运行时是<b>两版通吃</b>的：
    ///
    ///   1) <b>角限位</b>（<c>VRMC_springBone_limit</c>，本实现用 Cone 模式）
    ///      —— 直接限制关节方向相对静止方向的最大偏角。
    ///      物理实现见 UniGLTF.SpringBoneJobs.Anglelimit.Apply：把算出的小球位置
    ///      夹到一个以静止方向为轴、半角为 <c>anglelimit1</c> 的圆锥里。
    ///   2) <b>扩展碰撞体</b>（<c>VRMC_springBone_extended_collider</c> 的 inside* 形状）
    ///      —— VRM 0.x 也没有。这里用的是普通 Sphere：把胸骨关节挡在胸口球体之外，
    ///      避免往胸腔内部穿。
    ///
    /// 角限位给谁加由 <c>vrm.json</c> 的 <c>SpringBone.AngleLimitMode</c> 决定：
    ///   * <c>0 = None</c>   —— 不加任何限位；
    ///   * <c>1 = Breast</c> —— 只给名字命中胸部关键词的弹簧加（<see cref="VrmAngleLimitMode.Breast"/>）；
    ///   * <c>2 = All</c>    —— 给所有没配过限位的弹簧都加，胸部用 <c>MaxSwingAngleDeg</c>，
    ///     其余（头发 / 裙子 / 尾巴 / 饰品）用更松的 <c>OtherMaxSwingAngleDeg</c>。
    /// 三条模式都遵守同一条底线：<b>模型自己配过的限位一律不覆盖</b>。
    ///
    /// 胸口球体碰撞体与 <c>FreezeKeywords</c> 冻结只针对胸部关键词，与模式无关。
    /// </summary>
    internal static class VrmSpringBoneTuner
    {
        private const string ChestColliderName = "__vrm_chest_collider";
        private const string ChestColliderGroupName = "vrm:chest";

        public static void Apply(GameObject modelRoot, Vrm10Instance vrm)
        {
            var springs = vrm.SpringBone?.Springs;
            if (springs == null || springs.Count == 0) return;

            var options = VrmConfig.Current.SpringBone;

            var bust = new List<Vrm10InstanceSpringBone.Spring>();
            var other = new List<Vrm10InstanceSpringBone.Spring>();
            var frozen = new List<Vrm10InstanceSpringBone.Spring>();

            foreach (var spring in springs)
            {
                if (spring?.Joints == null || spring.Joints.Count == 0) continue;

                var label = Describe(spring);
                if (MatchesAny(label, options.FreezeKeywords)) frozen.Add(spring);
                else if (MatchesAny(label, options.BustKeywords)) bust.Add(spring);
                else other.Add(spring);
            }

            foreach (var spring in frozen) Freeze(spring);

            // ---- 角限位：按模式决定给谁加 ----
            var applied = 0;
            var already = 0;
            var skipped = 0;

            switch (options.AngleLimitMode)
            {
                case VrmAngleLimitMode.None:
                    break;

                case VrmAngleLimitMode.Breast:
                    applied += ApplyConeLimit(bust, options.MaxSwingAngleDeg, ref already);
                    skipped = CountJoints(other);
                    break;

                case VrmAngleLimitMode.All:
                    applied += ApplyConeLimit(bust, options.MaxSwingAngleDeg, ref already);
                    applied += ApplyConeLimit(other, options.OtherMaxSwingAngleDeg, ref already);
                    break;
            }

            var colliderAdded = false;
            if (bust.Count > 0 && options.ChestColliderEnabled)
                colliderAdded = AttachChestCollider(modelRoot, vrm, bust, options);

            if (frozen.Count > 0)
                VrmLog.Info($"已冻结 {frozen.Count} 条命中关键词的动骨");

            if (bust.Count > 0 || (options.AngleLimitMode == VrmAngleLimitMode.All && other.Count > 0))
                VrmLog.Info(
                    $"动骨角限位[{options.AngleLimitMode}]: 胸部 {bust.Count} 条 / 其余 {other.Count} 条弹簧，" +
                    DescribeLimit(options) +
                    (bust.Count > 0 && options.ChestColliderEnabled
                        ? colliderAdded ? "，已补胸口碰撞体" : "，无需补碰撞体"
                        : ""));

            if (VrmLog.Verbose) Dump(springs, applied, already, skipped);
        }

        private static string DescribeLimit(VrmSpringBoneOptions options)
        {
            switch (options.AngleLimitMode)
            {
                case VrmAngleLimitMode.None:
                    return "模式 None，未加任何限位";
                case VrmAngleLimitMode.Breast:
                    return $"仅胸部加限位 {options.MaxSwingAngleDeg:0.#}°";
                default:
                    return $"胸部 {options.MaxSwingAngleDeg:0.#}° / 其余 {options.OtherMaxSwingAngleDeg:0.#}°";
            }
        }

        private static int CountJoints(List<Vrm10InstanceSpringBone.Spring> springs)
        {
            var total = 0;
            foreach (var spring in springs)
                if (spring?.Joints != null)
                    total += spring.Joints.Count;

            return total;
        }

        #region 关键词

        private static string Describe(Vrm10InstanceSpringBone.Spring spring)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(spring.Name)) sb.Append(spring.Name).Append(' ');
            foreach (var joint in spring.Joints)
                if (joint != null)
                    sb.Append(joint.transform.name).Append(' ');
            return sb.ToString();
        }

        private static bool MatchesAny(string label, string[]? keywords)
        {
            if (string.IsNullOrEmpty(label) || keywords == null || keywords.Length == 0) return false;
            foreach (var keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword)) continue;
                if (label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        #endregion

        #region 角限位 / 冻结

        private static int ApplyConeLimit(List<Vrm10InstanceSpringBone.Spring> springs, float maxAngleDeg,
            ref int alreadyLimited)
        {
            var applied = 0;
            var limit = maxAngleDeg * Mathf.Deg2Rad;

            foreach (var spring in springs)
            foreach (var joint in spring.Joints)
            {
                if (joint == null) continue;

                // 模型自己配过限位就尊重原作，不覆盖。
                if (joint.m_anglelimitType != AnglelimitTypes.None)
                {
                    alreadyLimited++;
                    continue;
                }

                joint.m_anglelimitType = AnglelimitTypes.Cone;
                joint.m_pitch = limit;
                if (joint.m_limitSpaceOffset == default) joint.m_limitSpaceOffset = Quaternion.identity;
                applied++;
            }

            return applied;
        }

        /// <summary>让某条弹簧完全不动：刚度 / 重力归零，阻力拉满，且不参与任何碰撞推挤。</summary>
        private static void Freeze(Vrm10InstanceSpringBone.Spring spring)
        {
            foreach (var joint in spring.Joints)
            {
                if (joint == null) continue;
                joint.m_stiffnessForce = 0f;
                joint.m_gravityPower = 0f;
                joint.m_dragForce = 1f;
            }

            spring.ColliderGroups?.Clear();
        }

        #endregion

        #region 胸口碰撞体

        private static bool AttachChestCollider(GameObject modelRoot, Vrm10Instance vrm,
            List<Vrm10InstanceSpringBone.Spring> bust, VrmSpringBoneOptions options)
        {
            if (modelRoot.transform.Find(ChestColliderName) != null) return false;

            var chest = FindChestBone(vrm);
            if (chest == null) return false;

            var radius = EstimateRadius(chest, bust, options.ChestColliderRadiusScale);
            if (radius <= 0.001f) return false;

            var go = new GameObject(ChestColliderName);
            go.transform.SetParent(chest, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var collider = go.AddComponent<VRM10SpringBoneCollider>();
            collider.ColliderType = VRM10SpringBoneColliderTypes.Sphere;
            collider.Offset = Vector3.zero;
            collider.Tail = Vector3.zero;
            collider.Normal = Vector3.up;
            collider.Radius = radius;

            var group = go.AddComponent<VRM10SpringBoneColliderGroup>();
            group.Name = ChestColliderGroupName;
            group.Colliders = new List<VRM10SpringBoneCollider> { collider };

            vrm.SpringBone.ColliderGroups ??= new List<VRM10SpringBoneColliderGroup>();
            if (!vrm.SpringBone.ColliderGroups.Contains(group))
                vrm.SpringBone.ColliderGroups.Add(group);

            foreach (var spring in bust)
            {
                spring.ColliderGroups ??= new List<VRM10SpringBoneColliderGroup>();
                if (!spring.ColliderGroups.Contains(group))
                    spring.ColliderGroups.Add(group);
            }

            VrmLog.Detail($"胸口碰撞体: 挂在 {chest.name}，半径 {radius:F3}");
            return true;
        }

        private static Transform? FindChestBone(Vrm10Instance vrm)
        {
            if (vrm.TryGetBoneTransform(HumanBodyBones.UpperChest, out var upper) && upper != null) return upper;
            if (vrm.TryGetBoneTransform(HumanBodyBones.Chest, out var chest) && chest != null) return chest;
            if (vrm.TryGetBoneTransform(HumanBodyBones.Spine, out var spine) && spine != null) return spine;
            if (vrm.TryGetBoneTransform(HumanBodyBones.Hips, out var hips) && hips != null) return hips;
            return null;
        }

        /// <summary>半径取「胸口 → 胸部动骨根」距离的若干倍：只挡得住往胸腔里钻的那部分摆幅。</summary>
        private static float EstimateRadius(Transform chest, List<Vrm10InstanceSpringBone.Spring> bust, float scale)
        {
            var total = 0f;
            var count = 0;

            foreach (var spring in bust)
            {
                var root = spring.Joints.Count > 0 ? spring.Joints[0] : null;
                if (root == null) continue;
                total += Vector3.Distance(chest.position, root.transform.position);
                count++;
            }

            if (count == 0) return 0f;
            return total / count * scale;
        }

        #endregion

        #region 诊断

        /// <summary>
        /// 在 verbose 模式下把弹簧参数打出来。用来确认「参数是否随克隆丢失」
        /// —— 如果所有关节的 stiffness / drag / gravity 都是同一组默认值，
        /// 那就说明参数确实没带过来；反之则是模型本身的设定如此。
        /// </summary>
        private static void Dump(List<Vrm10InstanceSpringBone.Spring> springs, int limited, int already,
            int skipped)
        {
            const int maxSprings = 12;
            const int maxJoints = 2;

            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;

            foreach (var spring in springs)
            foreach (var joint in spring.Joints)
            {
                if (joint == null) continue;
                total++;
                signatures.Add(
                    $"{joint.m_stiffnessForce:F3}|{joint.m_gravityPower:F3}|{joint.m_dragForce:F3}|{joint.m_jointRadius:F4}");
            }

            VrmLog.Detail($"动骨参数概览: {springs.Count} 条弹簧 / {total} 个关节，" +
                          $"出现 {signatures.Count} 种参数组合" +
                          (signatures.Count <= 1 ? "（只有一种，疑似参数丢失）" : ""));
            VrmLog.Detail($"角限位结算: 本次新加 {limited} 个关节，模型自带 {already} 个，模式外跳过 {skipped} 个关节");

            var shown = 0;
            foreach (var spring in springs)
            {
                if (shown++ >= maxSprings) break;
                var sb = new StringBuilder();
                sb.Append($"  动骨[{shown - 1}] \"{spring.Name}\"：{spring.Joints.Count} 关节");
                if (spring.Center != null) sb.Append($"，center={spring.Center.name}");
                sb.Append($"，碰撞体组={(spring.ColliderGroups?.Count ?? 0)}");

                for (var i = 0; i < spring.Joints.Count && i < maxJoints; i++)
                {
                    var j = spring.Joints[i];
                    if (j == null) continue;
                    sb.Append($"\n    - {j.transform.name}: stiff={j.m_stiffnessForce:F2} " +
                              $"grav={j.m_gravityPower:F2} drag={j.m_dragForce:F2} r={j.m_jointRadius:F3} " +
                              $"limit={j.m_anglelimitType}({j.m_pitch * Mathf.Rad2Deg:0.#}°)");
                }

                VrmLog.Detail(sb.ToString());
            }
        }

        #endregion
    }
}
