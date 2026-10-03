using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 锚点用的骨架测量：缓存 Humanoid 骨骼引用，量出「髋→头距离」与「头顶高度」，
    /// 供头盔对齐和身高基准使用。
    ///
    /// 注意：本类<b>只做测量</b>，绝不驱动骨骼姿态 —— 动画一律由模型
    /// AssetBundle 自带的 Animator / AnimatorController 负责（DCM 官方 add-animator 机制）。
    /// 锚点本身不再需要任何坐标系换算（状态值 = 骨骼本地 Transform，所见即所得）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VrmAnchorFrame : MonoBehaviour
    {
        /// <summary>需要定位的骨骼，按「父先于子」排列。</summary>
        public static readonly HumanBodyBones[] BoneOrder =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes,
        };

        private readonly Dictionary<HumanBodyBones, Transform> _bones = new(BoneOrder.Length);

        public bool Valid { get; private set; }

        /// <summary>抓取时髋到头的距离（米）。头盔对齐与身高推算用。</summary>
        public float Unit { get; private set; } = 1f;

        /// <summary>抓取时的头顶世界 Y。</summary>
        public float HeadTopWorldY { get; private set; }

        public Transform? Bone(HumanBodyBones bone)
        {
            return _bones.TryGetValue(bone, out var tf) ? tf : null;
        }

        public bool Has(HumanBodyBones bone)
        {
            return _bones.TryGetValue(bone, out var tf) && tf != null;
        }

        /// <summary>
        /// 抓取骨架测量。必须在任何东西摆过姿势之前调用（模型刚实例化、静止 T-pose 时）。
        /// 返回 null 表示模型不是可识别的人形（缺关键骨骼）。
        /// </summary>
        public static VrmAnchorFrame? Capture(GameObject modelRoot, string? modelId = null)
        {
            if (modelRoot == null) return null;

            var vrm = modelRoot.GetComponentInChildren<Vrm10Instance>(true);
            var humanoid = vrm != null
                ? vrm.Humanoid
                : modelRoot.GetComponentInChildren<UniHumanoid.Humanoid>(true);
            if (humanoid == null)
            {
                VrmLog.Warn("找不到 Humanoid 组件，无法测量骨架。");
                return null;
            }

            var frame = modelRoot.GetComponent<VrmAnchorFrame>();
            if (frame == null) frame = modelRoot.AddComponent<VrmAnchorFrame>();

            frame._bones.Clear();
            foreach (var bone in BoneOrder)
            {
                var tf = GetBone(humanoid, bone);
                if (tf == null) continue;
                frame._bones[bone] = tf;
            }

            if (!frame.Has(HumanBodyBones.Hips) || !frame.Has(HumanBodyBones.Head))
            {
                VrmLog.Warn("骨架缺少必需骨骼（髋 / 头），头盔对齐与身高推算不可用。");
                frame.Valid = false;
                return frame;
            }

            var hips = frame._bones[HumanBodyBones.Hips];
            var head = frame._bones[HumanBodyBones.Head];

            var up = head.position - hips.position;
            frame.Unit = Mathf.Max(0.01f, up.magnitude);
            // 头骨通常在颈椎顶端，真实头顶还要往上一段（约 0.55 倍髋→头距离）。
            frame.HeadTopWorldY = head.position.y + frame.Unit * 0.55f;

            VrmLog.Info($"骨架测量完成（Unit={frame.Unit:0.###}m，{modelId}）");

            frame.Valid = true;
            return frame;
        }

        private static Transform? GetBone(UniHumanoid.Humanoid h, HumanBodyBones bone)
        {
            return bone switch
            {
                HumanBodyBones.Hips => h.Hips,
                HumanBodyBones.Spine => h.Spine,
                HumanBodyBones.Chest => h.Chest,
                HumanBodyBones.UpperChest => h.UpperChest != null ? h.UpperChest : h.Chest,
                HumanBodyBones.Neck => h.Neck,
                HumanBodyBones.Head => h.Head,
                HumanBodyBones.LeftShoulder => h.LeftShoulder,
                HumanBodyBones.LeftUpperArm => h.LeftUpperArm,
                HumanBodyBones.LeftLowerArm => h.LeftLowerArm,
                HumanBodyBones.LeftHand => h.LeftHand,
                HumanBodyBones.RightShoulder => h.RightShoulder,
                HumanBodyBones.RightUpperArm => h.RightUpperArm,
                HumanBodyBones.RightLowerArm => h.RightLowerArm,
                HumanBodyBones.RightHand => h.RightHand,
                HumanBodyBones.LeftUpperLeg => h.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg => h.LeftLowerLeg,
                HumanBodyBones.LeftFoot => h.LeftFoot,
                HumanBodyBones.LeftToes => h.LeftToes,
                HumanBodyBones.RightUpperLeg => h.RightUpperLeg,
                HumanBodyBones.RightLowerLeg => h.RightLowerLeg,
                HumanBodyBones.RightFoot => h.RightFoot,
                HumanBodyBones.RightToes => h.RightToes,
                _ => null,
            };
        }
    }
}
