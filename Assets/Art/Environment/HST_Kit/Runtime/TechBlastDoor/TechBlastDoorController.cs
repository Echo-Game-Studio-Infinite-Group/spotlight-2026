using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Environment
{
    /// <summary>
    /// 科技感重型气密防爆门 —— 四层门扇开合控制器(纯 Transform 插值, 不用协程/动画剪辑/Timeline)。
    ///
    /// 结构(从正面往里看, 一路穿过去):
    ///   ① 外层装饰门(近侧)  ② 横向真门  ③ 竖向真门  ④ 外层装饰门(远侧)
    ///   两边各一道同款装饰门, 中间一横一竖两道真门 —— 所以从任意一侧进来都先看到一道装饰门。
    ///
    /// 开门时序(冻结规格 scripts/door/door_spec.py 的 TIMING, 与 GIF 出图逐帧一致):
    ///   **近侧装饰门先开, 走到 bcdStartFraction(默认 50%)时, 横向真门 + 竖向真门 + 远侧装饰门
    ///   同时起步** —— 两组在时间轴上重叠, 不是"等外层走完再开内层"。
    ///   关门是这条时间线的**严格镜像**: 先一起关中间三组, 再关近侧装饰门。
    ///   每层时长 = 该层位移长度 / speedMetersPerSecond ⇒ 所有门扇线速度一致(重门走得慢而稳)。
    ///
    /// 挂载方式:
    ///   1. 把 SM_TechBlastDoor_v1.fbx 拖进场景, 在门根节点(或其父空物体)上挂本组件;
    ///   2. 十六个门扇引用留空即可 —— Awake 会按门扇对象名(GEO-OL_in_l 等)在子孙节点自动查找;
    ///   3. 其他脚本调 Open()/Close()/Toggle(), 或读 IsPassable 决定玩家能否通过。
    ///   ★ 挂载时门扇必须处于关闭位(FBX 实例化出来的默认位姿就是关闭位), 关闭位在 Awake 捕获一次。
    ///
    /// 坐标合同(默认位移为什么是这组数 —— 改导出设置时必须重算):
    ///   Blender 冻结规格 scripts/door/door_spec.py(v5.6)的 TRAVEL 是 Blender 世界位移 (bx, by, bz)。
    ///   FBX 按 scripts/door/export_unity_door.py 的口径导出(axis_forward='-Z', axis_up='Y',
    ///   apply_unit_scale=True)后, 实测映射 unity = (-x, z, -y): Blender 门正面朝 -Y → Unity 门正面朝 +Z。
    ///   本组件的位移 = 【门根节点局部坐标系下的 Unity 位移】= spec.TRAVEL 按 (-bx, bz, -by) 换算。
    ///   v5.6 共 16 扇(每层 4 扇 = 内/外 × 左/右(或上/下); 内扇携中缝齿, 外扇与内扇异深度搭接):
    ///       ★ v5.5 起两层装饰门**都是纯左右平移**(原带的 1.10 抬升已归零), 且近侧装饰门行程加长为
    ///         外扇 ±1.96 / 内扇 ±3.90(原来 ±1.66 / ±3.28 —— 内扇半宽加厚到 80mm 后要退得更远):
    ///       GEO-OL_out_l/r    近侧装饰门外扇: Blender(∓1.960, 0, 0) → Unity(±1.96, 0, 0)
    ///       GEO-OL_in_l/r     近侧装饰门内扇: Blender(∓3.900, 0, 0) → Unity(±3.90, 0, 0)
    ///       GEO-ID_horiz_out_l/r 横向真门外扇: Blender(∓1.655, 0, 0) → Unity(±1.655, 0, 0)
    ///       GEO-ID_horiz_in_l/r  横向真门内扇: Blender(∓3.280, 0, 0) → Unity(±3.280, 0, 0)
    ///       竖向门(上/下各内外两扇; 上扇上移入框顶腔, 下扇下沉入基础槽):
    ///       GEO-ID_vert_up_out 上外扇: Blender(0, 0, +1.5425) → Unity(0, +1.5425, 0)
    ///       GEO-ID_vert_up_in  上内扇: Blender(0, 0, +3.0300) → Unity(0, +3.0300, 0)
    ///       GEO-ID_vert_dn_out 下外扇: Blender(0, 0, -1.5025) → Unity(0, -1.5025, 0)
    ///       GEO-ID_vert_dn_in  下内扇: Blender(0, 0, -2.9900) → Unity(0, -2.9900, 0)
    ///       GEO-OD_out_l/r / in_l/r   远侧装饰门四扇(近侧的镜像; 行程仍 ±1.655 / ±3.280)。
    ///   Blender 侧已用逐 t 扫掠审计验证(v5.6 装配收据 HAMMER_assemble.json: sweep.clean=true):
    ///   16 扇沿各自位移走完全程**零干涉**(门扇×框体 / 门扇×门扇, 设计接触白名单除外)。
    ///   位移施加在"世界关闭位"上、再经门根变换门根局部向量 ⇒ 门根在关卡里任意旋转缩放都成立。
    ///
    /// 位移方向的左右没翻(自查): Unity 里门正面朝 +Z, 观众站 +Z 侧朝 -Z 看门, 此时 Unity +X 在观众左手边,
    ///   所以"左扇向 +X 滑"= 向观众左边滑开, 与 Blender 侧"左扇向 -X 滑"是同一个物理方向。
    ///
    /// 失配排查:
    ///   · 门扇不动: 控制台会报"找不到门扇"红字 —— FBX 对象名与 doorObjectName 不一致, 手动拖 Transform 进槽位。
    ///   · 方向反了/左右互换: FBX 导入的轴向设置被改过(合同 unity=(-x,z,-y) 不再成立), 按 IMPORT.md 重算默认值。
    ///   · 门扇一开局就飞出框外: 挂组件时门扇不在关闭位 —— 重新实例化 FBX; 关闭位只在 Awake 捕获一次。
    ///   · 只开了装饰门, 里面不动: 看 bcdStartFraction 是不是被调得过大(它控制"外层走到几成时内层起步")。
    /// </summary>
    public class TechBlastDoorController : MonoBehaviour
    {
        /// <summary>四层的层号(与 door_spec.LAYER_GROUPS 的 A/B/C/D 一一对应)。</summary>
        public enum DoorLayer
        {
            OuterDecorNear = 0,   // ① 外层装饰门(近侧) —— 最先开
            Horizontal = 1,       // ② 横向真门
            Vertical = 2,         // ③ 竖向真门
            OuterDecorFar = 3,    // ④ 外层装饰门(远侧)
        }

        /// <summary>一扇门 = 一个 Transform 引用 + 一个开门位移向量 + 运行时捕获的关闭位。</summary>
        [Serializable]
        public class DoorLeafBinding
        {
            [Tooltip("门扇 Transform; 留空则运行时按 doorObjectName 在本组件的子孙节点里自动查找")]
            public Transform leaf;

            [Tooltip("门扇在 FBX 里的对象名(=Unity GameObject 名), 来自 Blender 冻结规格 TRAVEL 的键")]
            public string doorObjectName;

            [Tooltip("关门→开门的位移向量, 单位米; 语义 = 门根节点局部坐标(Unity: +X右 +Y上 +Z前), 默认值已按坐标合同换算")]
            public Vector3 openOffset;

            /// <summary>关闭位的世界坐标, Awake 时捕获一次; 不序列化(场景里存的是 FBX 自带的关闭位)。</summary>
            [NonSerialized] public Vector3 closedWorldPosition;
        }

        [Header("① 外层装饰门(近侧 · 最先开; 内/外两半扇×左右, 纯左右平移退入框柱收纳腔)")]
        [SerializeField] private DoorLeafBinding[] outerDecorNearLeaves =
        {
            new DoorLeafBinding { doorObjectName = "GEO-OL_out_l", openOffset = new Vector3(+1.96f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OL_out_r", openOffset = new Vector3(-1.96f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OL_in_l", openOffset = new Vector3(+3.90f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OL_in_r", openOffset = new Vector3(-3.90f, 0f, 0f) },
        };

        [Header("② 横向真门(与 ③④ 一起开; 内/外两半扇×左右, 纯水平退入框柱收纳腔)")]
        [SerializeField] private DoorLeafBinding[] horizontalDoorLeaves =
        {
            new DoorLeafBinding { doorObjectName = "GEO-ID_horiz_out_l", openOffset = new Vector3(+1.655f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_horiz_out_r", openOffset = new Vector3(-1.655f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_horiz_in_l", openOffset = new Vector3(+3.28f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_horiz_in_r", openOffset = new Vector3(-3.28f, 0f, 0f) },
        };

        [Header("③ 竖向真门(与 ②④ 一起开; 上/下各内外两扇: 上扇上移入框顶腔, 下扇下沉入基础槽)")]
        [SerializeField] private DoorLeafBinding[] verticalDoorLeaves =
        {
            new DoorLeafBinding { doorObjectName = "GEO-ID_vert_up_out", openOffset = new Vector3(0f, +1.5425f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_vert_up_in", openOffset = new Vector3(0f, +3.03f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_vert_dn_out", openOffset = new Vector3(0f, -1.5025f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-ID_vert_dn_in", openOffset = new Vector3(0f, -2.99f, 0f) },
        };

        [Header("④ 外层装饰门(远侧 · 与 ②③ 一起开; 是近侧的镜像)")]
        [SerializeField] private DoorLeafBinding[] outerDecorFarLeaves =
        {
            new DoorLeafBinding { doorObjectName = "GEO-OD_out_l", openOffset = new Vector3(+1.655f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OD_out_r", openOffset = new Vector3(-1.655f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OD_in_l", openOffset = new Vector3(+3.28f, 0f, 0f) },
            new DoorLeafBinding { doorObjectName = "GEO-OD_in_r", openOffset = new Vector3(-3.28f, 0f, 0f) },
        };

        [Header("时序 · 门扇滑动速度(米/秒; 四层同速, 每层时长 = 该层位移长度 / 本值)")]
        [Tooltip("冻结规格是 1.0 m/s(GIF 的口径)。试验场按用户令调成 1.5 —— 比 GIF 快 50%。")]
        [SerializeField, Min(0.05f)] private float speedMetersPerSecond = 1.5f;

        [Header("时序 · 近侧装饰门走到几成时, 中间三组同时起步(0.5 = 外层开一半内层就动)")]
        [Tooltip("冻结规格 TIMING.bcd_start_frac = 0.50: 外层开一半, 内层两道 + 远侧门就起步")]
        [SerializeField, Range(0f, 1f)] private float bcdStartFraction = 0.5f;

        [Header("缓动 · 每层自身进度的位移曲线(横轴=该层时间进度, 纵轴=位移比例)")]
        [SerializeField] private AnimationCurve easingCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("初始状态 · 场景加载时门是否已开(通常保持 false)")]
        [SerializeField] private bool startOpen = false;

        // 运行时: 四层视图 + 时间线(由位移长度推出, 换参数即重算)
        private DoorLeafBinding[][] _layers;
        private readonly float[] _layerDuration = new float[4];   // 该层自身时长(秒)
        private readonly float[] _layerStartOpen = new float[4];  // 开门方向上该层的启动时刻(秒)
        private readonly float[] _layerStartClose = new float[4]; // 关门方向 = 开门时间线的镜像
        private float _totalTime;                                  // 开门全程时长(秒)
        private float _masterTime;                                 // 主时钟: 0=全关, _totalTime=全开
        private float _targetTime;                                 // 目标时钟
        private bool _missingLeafLogged;                           // 缺门扇的红字只报一次, 防刷屏

        /// <summary>门是否可以通行 —— 四层全部开完才为真; 门未开完不许通行。</summary>
        public bool IsPassable => _totalTime > 0f && _masterTime >= _totalTime - 1e-3f;

        /// <summary>当前整体开合进度, 0=全关, 1=全开。</summary>
        public float Progress => _totalTime > 0f ? Mathf.Clamp01(_masterTime / _totalTime) : 0f;

        /// <summary>开门全程时长(秒) —— 供触发区算"跑到门口门刚好开完"的纵深。</summary>
        public float OpenDuration => _totalTime;

        /// <summary>取某一层当前的开合进度(0=该层全关, 1=该层全开) —— 用于分镜/音效/提示。</summary>
        public float GetLayerProgress(DoorLayer layer)
        {
            int i = (int)layer;
            if (_layers == null || i < 0 || i >= _layers.Length)
            {
                return 0f;
            }
            float start = (_targetTime >= _masterTime) ? _layerStartOpen[i] : _layerStartClose[i];
            return Mathf.Clamp01((_masterTime - start) / Mathf.Max(1e-5f, _layerDuration[i]));
        }

        /// <summary>开门: 近侧装饰门先走, 到 bcdStartFraction 时横向真门 + 竖向真门 + 远侧装饰门同时起步。</summary>
        public void Open()
        {
            _targetTime = _totalTime;
        }

        /// <summary>关门: 开门时间线的严格镜像 —— 先一起关中间三组, 再关近侧装饰门。</summary>
        public void Close()
        {
            _targetTime = 0f;
        }

        /// <summary>开着就关, 关着就开(以当前目标状态取反, 连按不会抖)。</summary>
        public void Toggle()
        {
            _targetTime = _targetTime > _totalTime * 0.5f ? 0f : _totalTime;
        }

        /// <summary>
        /// 直接把门设到指定整体进度(0=全关, 1=全开), 用于过场/脚本对齐。
        /// 只改当前时钟, 不改目标状态 —— 之后门会从这一进度继续朝原目标开/关。
        /// </summary>
        public void SetProgress(float normalizedProgress)
        {
            _masterTime = Mathf.Clamp01(normalizedProgress) * _totalTime;
            ApplyPose();
        }

        private void Awake()
        {
            _layers = new[] { outerDecorNearLeaves, horizontalDoorLeaves, verticalDoorLeaves, outerDecorFarLeaves };
            BindMissingLeaves();
            CaptureClosedPose();
            ComputeTimeline();
            _masterTime = startOpen ? _totalTime : 0f;
            _targetTime = _masterTime;
            ApplyPose();
        }

        private void Update()
        {
            if (Mathf.Approximately(_masterTime, _targetTime))
            {
                return;
            }

            _masterTime = Mathf.MoveTowards(_masterTime, _targetTime, Time.deltaTime);
            ApplyPose();
        }

        /// <summary>按对象名自动补齐留空的门扇引用; 名字来自 Blender 冻结规格, 与 FBX 里的 GameObject 一一对应。</summary>
        private void BindMissingLeaves()
        {
            var childrenByName = new Dictionary<string, Transform>();
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
            {
                if (!childrenByName.ContainsKey(child.name))
                {
                    childrenByName.Add(child.name, child);
                }
            }

            var missing = new List<string>();
            foreach (DoorLeafBinding[] layer in _layers)
            {
                foreach (DoorLeafBinding binding in layer)
                {
                    if (binding.leaf != null || string.IsNullOrEmpty(binding.doorObjectName))
                    {
                        continue;
                    }
                    if (childrenByName.TryGetValue(binding.doorObjectName, out Transform found))
                    {
                        binding.leaf = found;
                    }
                    else
                    {
                        missing.Add(binding.doorObjectName);
                    }
                }
            }

            if (missing.Count > 0 && !_missingLeafLogged)
            {
                _missingLeafLogged = true;
                Debug.LogError($"[TechBlastDoor] 在 {name} 的子孙节点里找不到 {missing.Count} 扇门: {string.Join(", ", missing)}" +
                               "—— 请核对 FBX 对象名, 或手动把 Transform 拖进 Inspector 对应槽位。", this);
            }
        }

        /// <summary>捕获十六扇门的关闭位(世界坐标)。只在此处捕获一次, 之后所有插值都基于它。</summary>
        private void CaptureClosedPose()
        {
            foreach (DoorLeafBinding[] layer in _layers)
            {
                foreach (DoorLeafBinding binding in layer)
                {
                    binding.closedWorldPosition = binding.leaf != null ? binding.leaf.position : Vector3.zero;
                }
            }
        }

        /// <summary>
        /// 排出四层的时间线(单位: 秒):
        ///   每层时长 = 该层位移长度 / speed(所以各扇线速度一致);
        ///   开门: 近侧装饰门从 0 开始, 走到 bcdStartFraction 时其余三组**同时**起步;
        ///   关门: 取开门时间线的镜像(start_i' = total - start_i - dur_i), 于是先一起关中间三组, 最后关近侧装饰门。
        /// </summary>
        private void ComputeTimeline()
        {
            float speed = Mathf.Max(0.05f, speedMetersPerSecond);
            for (int i = 0; i < _layers.Length; i++)
            {
                float longest = 0f;
                foreach (DoorLeafBinding binding in _layers[i])
                {
                    longest = Mathf.Max(longest, binding.openOffset.magnitude);
                }
                _layerDuration[i] = Mathf.Max(longest, 1e-5f) / speed;
            }

            float others = _layerDuration[0] * Mathf.Clamp01(bcdStartFraction);
            _layerStartOpen[0] = 0f;
            for (int i = 1; i < _layers.Length; i++)
            {
                _layerStartOpen[i] = others;
            }

            _totalTime = 0f;
            for (int i = 0; i < _layers.Length; i++)
            {
                _totalTime = Mathf.Max(_totalTime, _layerStartOpen[i] + _layerDuration[i]);
            }
            for (int i = 0; i < _layers.Length; i++)
            {
                _layerStartClose[i] = _totalTime - _layerStartOpen[i] - _layerDuration[i];
            }
        }

        /// <summary>把主时钟换算成每层自身进度, 过缓动曲线后按位移向量摆门扇(世界关闭位 + 门根局部向量)。</summary>
        private void ApplyPose()
        {
            if (_layers == null)
            {
                return;
            }

            bool opening = _targetTime >= _masterTime;
            for (int i = 0; i < _layers.Length; i++)
            {
                float start = opening ? _layerStartOpen[i] : _layerStartClose[i];
                float local = Mathf.Clamp01((_masterTime - start) / Mathf.Max(1e-5f, _layerDuration[i]));
                float eased = easingCurve != null ? easingCurve.Evaluate(local) : local;
                foreach (DoorLeafBinding binding in _layers[i])
                {
                    if (binding.leaf == null)
                    {
                        continue;
                    }
                    binding.leaf.position = binding.closedWorldPosition + transform.TransformVector(binding.openOffset * eased);
                }
            }
        }

        /// <summary>Inspector 里改完参数(速度/起步比例/缓动)后即时重排时间线, 方便实时调手感的节奏。</summary>
        private void OnValidate()
        {
            if (!Application.isPlaying || _layers == null)
            {
                return;
            }
            ComputeTimeline();
        }
    }
}
