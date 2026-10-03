using System;
using System.Collections;
using System.Collections.Generic;
using DuckovCustomModel.Managers;
using DuckovCustomModel.MonoBehaviours;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 游戏内实时微调定位锚点的小面板（默认 F9 开 / 关）。
    ///
    /// 打开面板时解锁鼠标并暂停角色输入（照抄 DCM 配置窗口的做法），
    /// 游戏照常运行，可以边看模型边调。
    ///
    /// gizmo 手感对齐 Unity 场景视图：
    ///   * 每个锚点是一个球，点球选中（判定范围足够大）；
    ///   * 选中的锚点画出三根箭头（红/绿/蓝 = 锚点自身的右/上/前）和三个圆弧：
    ///       拖箭头 = 沿该轴移动；
    ///       拖圆弧 = 绕该轴旋转；
    ///       悬停会加亮变粗；
    ///   * 直接拖球 = 在屏幕平面里自由移动。
    ///
    /// 滑条的 0 = 代码里的出厂默认值；调完点唯一的「保存校正」落到 Overrides/&lt;模型&gt;.json。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VrmLocatorTuner : MonoBehaviour
    {
        private const int PanelWidth = 420;
        private const float PanelX = 12f;
        private const float PanelY = 12f;
        private const float ArrowLengthPx = 76f;   // 箭头长度（屏幕像素）
        private const float ArcRadiusPx = 54f;     // 圆弧半径（屏幕像素）

        private static VrmLocatorTuner? _instance;

        private ModelHandler? _handler;
        private bool _visible;
        private int _selected;
        private bool _inputAvailable = true;
        private KeyCode _hotkey = KeyCode.F9;
        private Vector2 _scroll;
        private string _status = string.Empty;
        private float _statusUntil;
        private static GUIStyle? _markerLabelStyle;

        // ---- 鼠标解锁 / 输入屏蔽 ----
        // 恢复策略（重要，别改回"快照恢复"）：
        //   * PlayerInput 走 DCM 的 InputBlocker.IsExternalBlocking —— 那是中心化、每帧强制
        //     执行的开关，DCM 配置窗口也用它；直接 DeactivateInput 会被 InputBlocker 每帧
        //     打回原形（Enable），两边打架。
        //   * CharacterInputControl 用"所有权制"：谁发现它 enabled 才去 disable 并认领，
        //     关面板时认领者无条件恢复。无条件恢复是安全的：其它需要屏蔽的一方
        //     （DCM 配置窗口）每帧会自己再屏蔽回去。
        //   之前两个面板互相踩"快照"（我们开着时 DCM 快照到 false、关掉时不恢复），
        //     结果就是面板全关了输入还是死的。
        private bool _cursorWasVisible;
        private CursorLockMode _savedCursorLock;
        private Coroutine? _cursorRoutine;
        private CharacterInputControl? _charInput;
        private bool _charInputOwned;
        private PlayerInput? _playerInput;
        private bool _blockerBorrowed;   // 成功借用了 InputBlocker.IsExternalBlocking
        private bool _directBlocked;     // 没有 InputBlocker 时的直接 DeactivateInput 兜底
        private static System.Reflection.FieldInfo? _externalBlockingField;

        // ---- gizmo ----
        private enum GizmoPart
        {
            None,
            Ball,
            Arrow,
            Arc,
        }

        private GizmoPart _dragPart;
        private GizmoPart _hover;
        private int _hoverAxis;
        private int _activeAxis;
        private VrmAnchorState? _dragState;
        private Vector3 _dragStartWorld;      // 拖拽起始时锚点的世界位置
        private Vector3 _dragCenterWorld;     // 轴向：锚点起始世界位置
        private Vector3 _dragAxisWorld;       // 轴向：轴（拖拽开始时固定）
        private Vector3 _dragStartClosest;    // Arrow：起始最近点
        private Vector3 _lastArcV;            // Arc：上一事件鼠标在弧平面上的单位向量

        private static readonly Color[] AxisColors =
        {
            new Color(0.95f, 0.25f, 0.25f),   // X 右 红
            new Color(0.40f, 0.85f, 0.30f),   // Y 上 绿
            new Color(0.35f, 0.55f, 0.95f),   // Z 前 蓝
        };

        private static Texture2D? _ballTexture;
        private static Texture2D? _whiteTexture;
        private static Texture2D? _arrowTexture;

        private static readonly HumanBodyBones[] BoneChoices =
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
            HumanBodyBones.RightUpperLeg,
        };

        public static void Ensure(GameObject modelRoot, ModelHandler? handler)
        {
            if (modelRoot == null) return;
            var tuner = modelRoot.GetComponent<VrmLocatorTuner>();
            if (tuner == null) tuner = modelRoot.AddComponent<VrmLocatorTuner>();
            tuner.Bind(handler);
        }

        public void Bind(ModelHandler? handler)
        {
            _handler = handler;
            _instance = this;

            var hotkeyName = VrmConfig.Current.Locators.DebugPanelHotkey;
            if (!Enum.TryParse<KeyCode>(hotkeyName, true, out var parsed)) parsed = KeyCode.F9;
            _hotkey = parsed;
        }

        private void Update()
        {
            // 面板开着时每帧重申屏蔽 —— 与 DCM 配置窗口同一策略，防止其它系统
            // 中途改了输入状态后没人管（这也是之前"关了面板按键还是死的"的成因之一）。
            if (_visible) ReassertBlocking();

            if (!_inputAvailable) return;

            try
            {
                if (Input.GetKeyDown(_hotkey))
                {
                    if (_visible) ClosePanel();
                    else OpenPanel();
                }
            }
            catch (Exception)
            {
                // 项目可能只启用了新输入系统 —— 面板不再响应热键，但不影响其它功能。
                _inputAvailable = false;
                VrmLog.Detail("旧版 Input 不可用，锚点微调面板的热键已禁用。");
            }
        }

        private void ReassertBlocking()
        {
            if (_charInputOwned && _charInput != null && _charInput.enabled) _charInput.enabled = false;

            // InputBlocker 自己每帧强制执行，不用重申；直接屏蔽的兜底路径需要重申。
            if (!_blockerBorrowed && _directBlocked && _playerInput != null && _playerInput.inputIsActive)
            {
                try
                {
                    _playerInput.DeactivateInput();
                }
                catch (Exception)
                {
                    // 忽略：下一帧再试。
                }
            }
        }

        // ---- 面板开 / 关 ----

        private void OpenPanel()
        {
            _visible = true;
            ShowStatus("面板已打开：点球选中 · 拖箭头/圆弧/球调整");

            _cursorWasVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            _cursorRoutine = StartCoroutine(ForceCursorFree());

            // 角色输入：所有权制 —— 只有它当前是启用状态时才去禁用并认领。
            _charInput = CharacterInputControl.Instance;
            _charInputOwned = false;
            if (_charInput != null && _charInput.enabled)
            {
                _charInput.enabled = false;
                _charInputOwned = true;
            }

            // 玩家动作：优先借 DCM 的 InputBlocker（中心化、每帧强制执行）。
            _playerInput = GameManager.MainPlayerInput;
            _blockerBorrowed = SetExternalBlocking(true);
            if (!_blockerBorrowed && _playerInput != null && _playerInput.inputIsActive)
            {
                _directBlocked = true;
                try
                {
                    _playerInput.DeactivateInput();
                }
                catch (Exception e)
                {
                    VrmLog.Detail($"DeactivateInput 失败（不影响使用）: {e.Message}");
                }
            }
        }

        private void ClosePanel()
        {
            _visible = false;
            EndDrag();
            ReleaseGameInput();
            ShowStatus("面板已关闭");
        }

        private void ReleaseGameInput()
        {
            if (_cursorRoutine != null)
            {
                StopCoroutine(_cursorRoutine);
                _cursorRoutine = null;
            }

            // 归还 InputBlocker；它下一帧会自动把动作重新启用。
            if (_blockerBorrowed) SetExternalBlocking(false);

            if (_directBlocked && _playerInput != null && !_playerInput.inputIsActive)
            {
                try
                {
                    _playerInput.ActivateInput();
                }
                catch (Exception e)
                {
                    VrmLog.Detail($"ActivateInput 失败（不影响使用）: {e.Message}");
                }
            }

            // 角色输入：认领者无条件恢复（其它需要屏蔽的一方每帧会自己再屏蔽回去，
            // 所以即使现在 DCM 配置窗口正开着，这里恢复也不会造成冲突）。
            if (_charInput != null && !_charInput.enabled)
            {
                try
                {
                    _charInput.enabled = true;
                }
                catch (Exception e)
                {
                    VrmLog.Detail($"恢复角色输入失败（组件可能已销毁）: {e.Message}");
                }
            }

            _charInput = null;
            _charInputOwned = false;
            _playerInput = null;
            _blockerBorrowed = false;
            _directBlocked = false;

            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _savedCursorLock;
        }

        /// <summary>借/还 DCM 的 InputBlocker.IsExternalBlocking（internal 字段，反射访问）。</summary>
        private static bool SetExternalBlocking(bool value)
        {
            try
            {
                var blocker = InputBlocker.Instance;
                if (blocker == null) return false;

                _externalBlockingField ??= AccessTools.Field(typeof(InputBlocker), "IsExternalBlocking");
                if (_externalBlockingField == null) return false;

                _externalBlockingField.SetValue(blocker, value);
                return true;
            }
            catch (Exception e)
            {
                VrmLog.Detail($"设置 InputBlocker 失败（不影响使用）: {e.Message}");
                return false;
            }
        }

        private IEnumerator ForceCursorFree()
        {
            while (_visible)
            {
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
                yield return null;
            }
        }

        private void OnDisable()
        {
            if (_visible) ClosePanel();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        // ---- IMGUI ----

        private void OnGUI()
        {
            if (!_visible) return;

            var states = VrmLocatorBuilder.States;

            var height = Mathf.Min(Screen.height - PanelY * 2f - 20f, 620f);
            var panelRect = new Rect(PanelX, PanelY, PanelWidth, height);
            GUILayout.BeginArea(panelRect, GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("VRM 定位锚点微调");
            var modelName = VrmLocatorBuilder.ActiveModelId ?? "(未绑定模型)";
            GUILayout.Label($"模型: {modelName}");
            if (VrmLocatorBuilder.Frame == null)
                GUILayout.Label("⚠ 骨架测量不可用");
            else
                GUILayout.Label($"已生成 {VrmLocatorBuilder.GeneratedCount}/{states.Count} 个锚点（状态值 = 骨骼本地 Transform，所见即所得）");

            GUILayout.Space(4);
            GUILayout.Label("点球=选中 · 拖箭头=沿轴移 · 拖圆弧=绕轴转 · 拖球=自由移", _markerLabelStyle ??
                (_markerLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 }));
            GUILayout.Label("⚠ 调完记得点下方「保存校正」，否则退出游戏就丢了", _markerLabelStyle ??
                (_markerLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold }));

            GUILayout.Space(6);

            if (states.Count == 0)
            {
                GUILayout.Label("当前没有锚点。");
            }
            else
            {
                _selected = Mathf.Clamp(_selected, 0, states.Count - 1);
                DrawAnchorEditor(states[_selected]);
            }

            GUILayout.Space(10);
            GUILayout.Label("身高（米）");
            DrawHeightControls();

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存校正"))
            {
                var sub = VrmConfig.Current.Locators.OverrideDirectory;
                _status = VrmLocatorBuilder.SaveOverrides(VrmLocatorBuilder.ActiveModelId, sub)
                    ? "已保存。文件在 " + VrmLocatorBuilder.OverridePath(VrmLocatorBuilder.ActiveModelId, sub)
                    : "保存失败（见日志）";
                _statusUntil = Time.unscaledTime + 8f;
            }

            if (GUILayout.Button("重新生成"))
            {
                // 重新读校正文件并重建锚点，然后让 DCM 重新登记 socket、重挂装备
                // （不这样做的话 DCM 缓存里还是已销毁的旧锚点，装备不会跟随）。
                VrmSocketRefresher.Refresh(_handler, rebuildRecord: true);
                _status = "已重新生成并重新挂接装备";
                _statusUntil = Time.unscaledTime + 3f;
            }

            if (GUILayout.Button("关闭")) ClosePanel();
            GUILayout.EndHorizontal();

            if (Time.unscaledTime < _statusUntil && _status.Length > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label(_status);
            }

            GUILayout.Space(4);
            GUILayout.Label($"热键 {_hotkey} 开关面板 · 面板打开时游戏照常运行");

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            HandleGizmoEvents(states, panelRect);
            DrawScreenMarkers(states, _selected);
        }

        private void DrawAnchorEditor(VrmAnchorState state)
        {
            VrmLocatorBuilder.TryGetDefault(state.Name, out var basis);

            // ---- 锚点选择 ----
            var allStates = VrmLocatorBuilder.States;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(32))) _selected = (_selected - 1 + allStates.Count) % allStates.Count;
            GUILayout.Label(state.Name, GUILayout.Width(240));
            if (GUILayout.Button("▶", GUILayout.Width(32))) _selected = (_selected + 1) % allStates.Count;
            GUILayout.EndHorizontal();

            // ---- 父骨骼 ----
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("挂载骨骼", GUILayout.Width(64));
            if (GUILayout.Button("◀", GUILayout.Width(28))) ShiftBone(state, -1);
            GUILayout.Label(state.Bone.ToString(), GUILayout.Width(180));
            if (GUILayout.Button("▶", GUILayout.Width(28))) ShiftBone(state, 1);
            GUILayout.EndHorizontal();

            // ---- 位置 ----
            GUILayout.Space(6);
            GUILayout.Label("位置（骨骼本地，米：右 / 上 / 前，0 = 代码默认值）");
            var offsetDelta = state.Offset - basis.Offset;
            var newOffsetDelta = Slider3("右", "上", "前", offsetDelta, -0.5f, 0.5f);
            if (newOffsetDelta != offsetDelta)
            {
                state.Offset = basis.Offset + newOffsetDelta;
                VrmLocatorBuilder.Apply(state);
            }

            // ---- 旋转 ----
            GUILayout.Space(6);
            GUILayout.Label("旋转（度，相对挂载骨骼：绕右 / 绕上 / 绕前，0 = 代码默认值）");
            var eulerDelta = new Vector3(
                NormAngle(state.Euler.x - basis.Euler.x),
                NormAngle(state.Euler.y - basis.Euler.y),
                NormAngle(state.Euler.z - basis.Euler.z));
            var newEulerDelta = Slider3("绕右", "绕上", "绕前", eulerDelta, -180f, 180f);
            if (newEulerDelta != eulerDelta)
            {
                state.Euler = new Vector3(
                    NormAngle(basis.Euler.x + newEulerDelta.x),
                    NormAngle(basis.Euler.y + newEulerDelta.y),
                    NormAngle(basis.Euler.z + newEulerDelta.z));
                VrmLocatorBuilder.Apply(state);
            }

            GUILayout.Label($"当前绝对值  位置({state.Offset.x:0.###}, {state.Offset.y:0.###}, {state.Offset.z:0.###})" +
                            $"  旋转({state.Euler.x:0.#}, {state.Euler.y:0.#}, {state.Euler.z:0.#})",
                _markerLabelStyle ?? (_markerLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 }));

            // ---- 缩放 ----
            GUILayout.Space(6);
            var scaleDelta = state.Scale - basis.Scale;
            var newScaleDelta = LabeledSlider("缩放Δ", scaleDelta, -2f, 2f);
            if (!Mathf.Approximately(newScaleDelta, scaleDelta))
            {
                state.Scale = Mathf.Max(0f, basis.Scale + newScaleDelta);
                VrmLocatorBuilder.Apply(state);
            }

            GUILayout.Space(8);
            if (GUILayout.Button("重置本项（恢复代码默认值）"))
            {
                state.Offset = basis.Offset;
                state.Euler = basis.Euler;
                state.Scale = basis.Scale;
                VrmLocatorBuilder.Apply(state);
                VrmSocketRefresher.Refresh(_handler, rebuildRecord: false);
                ShowStatus($"已重置 {state.Name}，装备已重新挂接");
            }
        }

        // ---- 屏幕 gizmo ----

        private static Texture2D WhiteTexture => _whiteTexture ??= CreateWhiteTexture();

        private static Texture2D BallTexture
        {
            get
            {
                if (_ballTexture != null) return _ballTexture;
                const int size = 48;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                var half = size * 0.5f - 1f;
                var pixels = new Color[size * size];
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - half) / half;
                    var dy = (y - half) / half;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = d <= 0.72f ? 1f : d >= 1f ? 0f : (1f - d) / 0.28f;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }

                tex.SetPixels(pixels);
                tex.Apply();
                _ballTexture = tex;
                return tex;
            }
        }

        /// <summary>箭头三角（尖端朝本地 +X，即贴图右缘中点）。</summary>
        private static Texture2D ArrowTexture
        {
            get
            {
                if (_arrowTexture != null) return _arrowTexture;
                const int size = 32;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                var half = (size - 1) * 0.5f;
                var pixels = new Color[size * size];
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var nx = x / (float)(size - 1);            // 0 左底 → 1 右尖
                    var dy = Mathf.Abs(y - half) / half;        // 0 中线 → 1 上下边
                    var inside = nx > 0.04f && dy <= 1f - nx;
                    pixels[y * size + x] = inside ? Color.white : Color.clear;
                }

                tex.SetPixels(pixels);
                tex.Apply();
                _arrowTexture = tex;
                return tex;
            }
        }

        private static Texture2D CreateWhiteTexture()
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
            tex.Apply();
            return tex;
        }

        private static float WorldPerPixel(Camera cam)
        {
            return 2f * 10f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, Screen.height);
        }

        private static Vector2 WorldToGuiPoint(Camera cam, Vector3 world)
        {
            var sp = cam.WorldToScreenPoint(world);
            return new Vector2(sp.x, Screen.height - sp.y);
        }

        private static Vector3 AxisDir(Transform tf, int axis)
        {
            return axis switch
            {
                0 => tf.right,
                1 => tf.up,
                _ => tf.forward,
            };
        }

        private void HandleGizmoEvents(IReadOnlyList<VrmAnchorState> states, Rect panelRect)
        {
            var e = Event.current;
            if (e == null) return;

            if (panelRect.Contains(e.mousePosition))
            {
                // 拖拽中鼠标移回面板也要能松手。
                if (e.type == EventType.MouseUp && _dragPart != GizmoPart.None)
                {
                    EndDrag();
                    e.Use();
                }

                return;
            }

            var cam = Camera.main;
            if (cam == null) return;

            if (e.type == EventType.MouseMove)
            {
                if (_dragPart == GizmoPart.None)
                    _hover = HitTest(states, cam, e.mousePosition, out _hoverAxis, out _);
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                var part = HitTest(states, cam, e.mousePosition, out var axis, out var index);
                if (part == GizmoPart.None) return;

                if (part == GizmoPart.Ball && index != _selected)
                {
                    // 点别的锚点：只切换选中，避免误拖。
                    _selected = index;
                    _hover = GizmoPart.Ball;
                    _hoverAxis = 0;
                    e.Use();
                    return;
                }

                var state = states[index];
                if (state.Instance == null) return;

                var tf = state.Instance.transform;
                if (VrmLocatorBuilder.Frame == null) return;

                var mouse = new Vector3(e.mousePosition.x, Screen.height - e.mousePosition.y, 0f);
                var ray = cam.ScreenPointToRay(mouse);

                _dragState = state;
                _dragPart = part;
                _activeAxis = axis;
                _dragCenterWorld = tf.position;
                _dragStartWorld = tf.position;
                _dragAxisWorld = part == GizmoPart.Ball ? Vector3.zero : AxisDir(tf, axis).normalized;

                switch (part)
                {
                    case GizmoPart.Arrow:
                        if (!ClosestPointOnLineToRay(ray, _dragCenterWorld, _dragAxisWorld, out _dragStartClosest))
                        {
                            EndDrag();
                            return;
                        }

                        break;

                    case GizmoPart.Arc:
                    {
                        var plane = new Plane(_dragAxisWorld, _dragCenterWorld);
                        if (!plane.Raycast(ray, out var enter))
                        {
                            EndDrag();
                            return;
                        }

                        var v = ray.GetPoint(enter) - _dragCenterWorld;
                        if (v.sqrMagnitude < 1e-10f)
                        {
                            EndDrag();
                            return;
                        }

                        _lastArcV = v.normalized;
                        break;
                    }
                }

                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _dragPart != GizmoPart.None)
            {
                DragGizmo(cam, e);
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _dragPart != GizmoPart.None)
            {
                EndDrag();
                e.Use();
            }
        }

        /// <summary>命中测试：优先箭头 / 圆弧（选中锚点），其次任何锚点的球。</summary>
        private GizmoPart HitTest(IReadOnlyList<VrmAnchorState> states, Camera cam, Vector2 mouse,
            out int axis, out int index)
        {
            axis = 0;
            index = -1;
            var result = GizmoPart.None;

            if (_selected >= 0 && _selected < states.Count &&
                states[_selected].HasInstance && states[_selected].Instance != null)
            {
                var tf = states[_selected].Instance!.transform;
                var sp = cam.WorldToScreenPoint(tf.position);
                if (sp.z > 0f)
                {
                    var center = tf.position;
                    var cp = new Vector2(sp.x, Screen.height - sp.y);
                    var wpp = WorldPerPixel(cam);
                    var axisLen = wpp * ArrowLengthPx;
                    var arcR = wpp * ArcRadiusPx;

                    // 箭头（线轴）
                    var bestArrow = 14f;
                    for (var i = 0; i < 3; i++)
                    {
                        var tipWorld = center + AxisDir(tf, i) * axisLen;
                        var tipSp = cam.WorldToScreenPoint(tipWorld);
                        if (tipSp.z <= 0f) continue;

                        var tp = new Vector2(tipSp.x, Screen.height - tipSp.y);
                        var d = Mathf.Min(DistToSegment(mouse, cp, tp), Vector2.Distance(mouse, tp) + 5f);
                        if (d < bestArrow)
                        {
                            bestArrow = d;
                            axis = i;
                            result = GizmoPart.Arrow;
                            index = _selected;
                        }
                    }

                    // 圆弧（弧轴）—— 箭头优先：圆弧和箭头在屏幕上常常交叉，靠得太近时不抢箭头。
                    var bestArc = 11f;
                    for (var i = 0; i < 3; i++)
                    {
                        var d = DistToArc(cam, center, AxisDir(tf, i), arcR, mouse);
                        if (result == GizmoPart.Arrow && d > bestArrow - 5f) continue;
                        if (d < bestArc)
                        {
                            bestArc = d;
                            axis = i;
                            result = GizmoPart.Arc;
                            index = _selected;
                        }
                    }
                }
            }

            // 球：箭头 / 圆弧已经命中时不参与（否则箭头靠根部的部分会被误判成自由拖动）
            if (result != GizmoPart.None) return result;

            var bestBall = float.MaxValue;
            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (!s.HasInstance || s.Instance == null) continue;

                var sp = cam.WorldToScreenPoint(s.Instance.transform.position);
                if (sp.z <= 0f) continue;

                var p = new Vector2(sp.x, Screen.height - sp.y);
                var d = Vector2.Distance(mouse, p);
                var limit = i == _selected ? 26f : 30f;
                if (d <= limit && d < bestBall)
                {
                    bestBall = d;
                    index = i;
                    result = GizmoPart.Ball;
                    axis = 0;
                }
            }

            return result;
        }

        private void DragGizmo(Camera cam, Event e)
        {
            var state = _dragState;
            if (state == null || state.Instance == null)
            {
                EndDrag();
                return;
            }

            if (VrmLocatorBuilder.Frame == null) return;

            var mouse = new Vector3(e.mousePosition.x, Screen.height - e.mousePosition.y, 0f);
            var ray = cam.ScreenPointToRay(mouse);
            var tf = state.Instance.transform;

            switch (_dragPart)
            {
                case GizmoPart.Ball:
                {
                    var plane = new Plane(cam.transform.forward, _dragStartWorld);
                    if (!plane.Raycast(ray, out var t)) return;

                    var world = ray.GetPoint(t);
                    tf.position = world;
                    state.Offset = tf.localPosition;
                    break;
                }

                case GizmoPart.Arrow:
                {
                    if (!ClosestPointOnLineToRay(ray, _dragCenterWorld, _dragAxisWorld, out var p)) return;

                    var worldDelta = p - _dragStartClosest;
                    tf.position = _dragStartWorld + worldDelta;
                    state.Offset = tf.localPosition;
                    break;
                }

                case GizmoPart.Arc:
                {
                    var plane = new Plane(_dragAxisWorld, _dragCenterWorld);
                    if (!plane.Raycast(ray, out var enter)) return;

                    var m = ray.GetPoint(enter) - _dragCenterWorld;
                    if (m.sqrMagnitude < 1e-10f) return;

                    var v = m.normalized;
                    var angle = Mathf.Atan2(
                        Vector3.Dot(Vector3.Cross(_lastArcV, v), _dragAxisWorld),
                        Vector3.Dot(_lastArcV, v)) * Mathf.Rad2Deg;
                    if (Mathf.Abs(angle) < 0.005f) return;

                    tf.rotation = Quaternion.AngleAxis(angle, _dragAxisWorld) * tf.rotation;
                    _lastArcV = v;

                    var eu = tf.localRotation.eulerAngles;
                    state.Euler = new Vector3(NormAngle(eu.x), NormAngle(eu.y), NormAngle(eu.z));
                    break;
                }
            }

            VrmLocatorBuilder.Apply(state);
        }

        private void EndDrag()
        {
            _dragPart = GizmoPart.None;
            _dragState = null;
        }

        private void DrawScreenMarkers(IReadOnlyList<VrmAnchorState> states, int selected)
        {
            var cam = Camera.main;
            if (cam == null) return;

            if (_markerLabelStyle == null)
                _markerLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (!state.HasInstance || state.Instance == null) continue;

                var sp = cam.WorldToScreenPoint(state.Instance.transform.position);
                if (sp.z <= 0f) continue; // 在相机背后

                if (i == selected)
                {
                    DrawSelectedGizmo(cam, state);
                }
                else
                {
                    DrawBall(cam, state, false);
                }
            }

            // 选中锚点的球画在最上层
            if (selected >= 0 && selected < states.Count &&
                states[selected].HasInstance && states[selected].Instance != null &&
                cam.WorldToScreenPoint(states[selected].Instance!.transform.position).z > 0f)
            {
                DrawBall(cam, states[selected], true);
            }
        }

        private static void DrawBall(Camera cam, VrmAnchorState state, bool selected)
        {
            var p = WorldToGuiPoint(cam, state.Instance!.transform.position);
            var size = selected ? 20f : 12f;
            var prev = GUI.color;
            GUI.color = selected ? Color.yellow : new Color(0.3f, 0.9f, 1f, 0.95f);
            GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), BallTexture,
                ScaleMode.ScaleToFit, true);
            GUI.color = prev;

            _markerLabelStyle!.normal.textColor = selected ? Color.yellow : Color.white;
            GUI.Label(new Rect(p.x + 12f, p.y - 9f, 200f, 18f), state.Name, _markerLabelStyle);
        }

        /// <summary>画选中锚点的三根箭头 + 三个圆弧（悬停/拖拽中的那根会加亮变粗）。</summary>
        private void DrawSelectedGizmo(Camera cam, VrmAnchorState state)
        {
            var tf = state.Instance!.transform;
            var center = tf.position;
            var sp = cam.WorldToScreenPoint(center);
            if (sp.z <= 0f) return;

            var cp = new Vector2(sp.x, Screen.height - sp.y);
            var wpp = WorldPerPixel(cam);
            var axisLen = wpp * ArrowLengthPx;
            var arcRadius = wpp * ArcRadiusPx;

            var dragging = _dragPart != GizmoPart.None;

            // 圆弧（画在箭头下层）
            for (var i = 0; i < 3; i++)
            {
                var hot = dragging
                    ? _dragPart == GizmoPart.Arc && _activeAxis == i
                    : _hover == GizmoPart.Arc && _hoverAxis == i;
                var col = AxisColors[i];
                col.a = hot ? 1f : 0.8f;
                DrawArc(cam, center, AxisDir(tf, i), arcRadius, col, hot ? 3.5f : 2f);
            }

            // 箭头
            for (var i = 0; i < 3; i++)
            {
                var hot = dragging
                    ? _dragPart == GizmoPart.Arrow && _activeAxis == i
                    : _hover == GizmoPart.Arrow && _hoverAxis == i;

                var dir = AxisDir(tf, i);
                var tipWorld = center + dir * axisLen;
                var tipSp = cam.WorldToScreenPoint(tipWorld);
                if (tipSp.z <= 0f) continue;

                var tp = new Vector2(tipSp.x, Screen.height - tipSp.y);
                var col = AxisColors[i];
                DrawScreenLine(cp, tp, col, hot ? 5f : 3f);
                DrawArrowTip(tp, tp - cp, col);
            }
        }

        private static void DrawArrowTip(Vector2 tip, Vector2 dirGui, Color color)
        {
            if (dirGui.sqrMagnitude < 0.5f) return;

            var angle = Mathf.Atan2(dirGui.y, dirGui.x) * Mathf.Rad2Deg;
            const float len = 16f, halfW = 7f;
            var savedColor = GUI.color;
            var savedMatrix = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, tip);
            GUI.DrawTexture(new Rect(tip.x - len, tip.y - halfW, len, halfW * 2f), ArrowTexture,
                ScaleMode.StretchToFill, true);
            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }

        private static void DrawArc(Camera cam, Vector3 center, Vector3 axisDir, float worldRadius,
            Color color, float width)
        {
            var d = axisDir.normalized;
            var helper = Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right;
            var u = Vector3.Cross(d, helper).normalized;
            var v = Vector3.Cross(d, u);

            const int segments = 40;
            var hasPrev = false;
            var prev = Vector2.zero;
            for (var i = 0; i <= segments; i++)
            {
                var ang = i * (Mathf.PI * 2f / segments);
                var world = center + worldRadius * (Mathf.Cos(ang) * u + Mathf.Sin(ang) * v);
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f)
                {
                    hasPrev = false;
                    continue;
                }

                var p = new Vector2(sp.x, Screen.height - sp.y);
                if (hasPrev) DrawScreenLine(prev, p, color, width);
                prev = p;
                hasPrev = true;
            }
        }

        private static float DistToArc(Camera cam, Vector3 center, Vector3 axisDir, float worldRadius, Vector2 mouse)
        {
            var d = axisDir.normalized;
            var helper = Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right;
            var u = Vector3.Cross(d, helper).normalized;
            var v = Vector3.Cross(d, u);

            const int segments = 40;
            var best = float.MaxValue;
            var hasPrev = false;
            var prev = Vector2.zero;
            for (var i = 0; i <= segments; i++)
            {
                var ang = i * (Mathf.PI * 2f / segments);
                var world = center + worldRadius * (Mathf.Cos(ang) * u + Mathf.Sin(ang) * v);
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f)
                {
                    hasPrev = false;
                    continue;
                }

                var p = new Vector2(sp.x, Screen.height - sp.y);
                if (hasPrev) best = Mathf.Min(best, DistToSegment(mouse, prev, p));
                prev = p;
                hasPrev = true;
            }

            return best;
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);

            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>屏幕空间画线（用旋转的 1px 白贴图凑）。注意 GUI 的 Y 轴朝下。</summary>
        private static void DrawScreenLine(Vector2 a, Vector2 b, Color color, float width)
        {
            var dir = b - a;
            var len = dir.magnitude;
            if (len < 0.5f) return;

            var angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            var savedColor = GUI.color;
            var savedMatrix = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), WhiteTexture, ScaleMode.StretchToFill, true);
            GUI.matrix = savedMatrix;
            GUI.color = savedColor;
        }

        /// <summary>鼠标射线与一条直线的最近点（直线由 linePoint / lineDir 定义）。</summary>
        private static bool ClosestPointOnLineToRay(Ray ray, Vector3 linePoint, Vector3 lineDir, out Vector3 point)
        {
            point = default;
            var a = lineDir.normalized;
            var d = ray.direction;
            var b = Vector3.Dot(d, a);
            var denom = 1f - b * b;
            if (denom < 1e-6f) return false; // 近乎平行，投影不稳定

            var w0 = ray.origin - linePoint;
            var t = (Vector3.Dot(w0, a) * b - Vector3.Dot(w0, d)) / denom;
            var s = Vector3.Dot(w0, a) + t * b;
            point = linePoint + a * s;
            return true;
        }

        // ---- 其余控件 ----

        private void ShowStatus(string message)
        {
            _status = message;
            _statusUntil = Time.unscaledTime + 4f;
        }

        private static float NormAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }

        private void DrawHeightControls()
        {
            var targetId = _handler?.TargetTypeId;
            var modelId = _handler?.CurrentModelInfo?.ModelID;

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(modelId))
            {
                GUILayout.Label("（还没绑定角色 / 模型）");
                return;
            }

            var current = ModelHeightManager.GetHeight(targetId, modelId);
            if (current <= 0f) current = 1.35f;

            var value = LabeledSlider("身高", current, 0.5f, 3f);
            if (!Mathf.Approximately(value, current)) ModelHeightManager.SetHeight(targetId, modelId, value);

            if (GUILayout.Button("重置身高")) ModelHeightManager.ResetHeight(targetId, modelId);
        }

        private static Vector3 Slider3(string labelX, string labelY, string labelZ, Vector3 value,
            float min, float max)
        {
            var result = value;
            result.x = LabeledSlider(labelX, result.x, min, max);
            result.y = LabeledSlider(labelY, result.y, min, max);
            result.z = LabeledSlider(labelZ, result.z, min, max);
            return result;
        }

        private static float LabeledSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(48));
            var v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(230));
            GUILayout.Label(v.ToString("0.###"), GUILayout.Width(64));
            GUILayout.EndHorizontal();
            return v;
        }

        private static void ShiftBone(VrmAnchorState state, int delta)
        {
            var current = Array.IndexOf(BoneChoices, state.Bone);
            if (current < 0) current = 0;
            current = (current + delta + BoneChoices.Length) % BoneChoices.Length;
            state.Bone = BoneChoices[current];
            VrmLocatorBuilder.Apply(state);
        }
    }

}
