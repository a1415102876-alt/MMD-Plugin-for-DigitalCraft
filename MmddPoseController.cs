using Il2CppInterop.Runtime.Attributes;
using System.Text;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CharaAnime
{
    public class MmddPoseController : MonoBehaviour
    {
        public MmddPoseController(IntPtr ptr) : base(ptr)
        {
        }
        public enum UpperBodyMode
        {
            FollowHips = 0, 
            Stabilize = 1  
        }
        public UpperBodyMode upperBodyMode = UpperBodyMode.FollowHips;

        public float VmdFrameRate = 30.0f;
        private VirtualBone boneNeck => dummyDict.ContainsKey("首") ? dummyDict["首"] : null;
        private VirtualBone boneHead => dummyDict.ContainsKey("頭") ? dummyDict["頭"] : null;
        private VirtualBone boneEyeL => dummyDict.ContainsKey("左目") ? dummyDict["左目"] : null;
        private VirtualBone boneEyeR => dummyDict.ContainsKey("右目") ? dummyDict["右目"] : null;

        public float LoopStart = 0f;
        public float LoopEnd = 0f;
        public bool EnableLoop = true;
        public bool ForceDisableIK = false;
        private bool isDirtyIKData = false;
        private Dictionary<string, float> ankleHeightOffsets = new Dictionary<string, float>();
        private Transform cachedRealHips = null;
        /// <summary>
        /// 保存"应该启用IK"的状态，用于在取消强制关闭时恢复
        /// </summary>
        private bool shouldIKBeEnabled = false;
        [Serializable]
        public class BoneAdjustment
        {
            public string BoneName;
            public Vector3 RotOffsetEuler;
            public Quaternion AxisCorrection;
            public Vector3 AxisCorrectionEuler;

            public BoneAdjustment(string name)
            {
                BoneName = name;
                RotOffsetEuler = Vector3.zero;
                AxisCorrection = Quaternion.identity;
                AxisCorrectionEuler = Vector3.zero;
            }

            public void SetAxisCorrection(Vector3 euler)
            {
                AxisCorrectionEuler = euler;
                AxisCorrection = Quaternion.Euler(euler);
            }
        }

        public struct BezierCurve
        {
            public Vector2 P1; // 控制点1 (x, y)
            public Vector2 P2; // 控制点2 (x, y)
        }

        public static Dictionary<string, BoneAdjustment> BoneSettings = new Dictionary<string, BoneAdjustment>();
        public static Vector3 GlobalPositionOffset = Vector3.zero;

        public static void InitializeDefaultBoneSettings()
        {
            if (BoneSettings.Count > 0) return;
            string[] allBones = {
                "左腕", "右腕", "左ひじ", "右ひじ", "左肩", "右肩",
                "左親指０", "左親指１", "左親指２", "右親指０", "右親指１", "右親指２",
                "左人指１", "左中指１", "左薬指１", "左小指１",
                "右人指１", "右中指１", "右薬指１", "右小指１",
                "上半身2", "首", "左足", "右足", "左ひざ", "右ひざ", "センター"
            };
            foreach (var name in allBones) GetOrCreateAdjustment(name);
            GetOrCreateAdjustment("左ひじ").SetAxisCorrection(new Vector3(0, 0, 0));
            GetOrCreateAdjustment("右ひじ").SetAxisCorrection(new Vector3(0, 0, 0));
        }

        public static BoneAdjustment GetOrCreateAdjustment(string name)
        {
            if (!BoneSettings.ContainsKey(name))
                BoneSettings[name] = new BoneAdjustment(name);
            return BoneSettings[name];
        }

        public static string ExportPreset()
        {
            StringBuilder sb = new StringBuilder();
            if (GlobalPositionOffset != Vector3.zero)
                sb.AppendLine($"GlobalPos={GlobalPositionOffset.x},{GlobalPositionOffset.y},{GlobalPositionOffset.z}");
            foreach (var kvp in BoneSettings)
            {
                var adj = kvp.Value;
                if (adj.RotOffsetEuler != Vector3.zero || adj.AxisCorrectionEuler != Vector3.zero)
                {
                    sb.Append($"{adj.BoneName}=");
                    if (adj.RotOffsetEuler != Vector3.zero) sb.Append($"r,{adj.RotOffsetEuler.x},{adj.RotOffsetEuler.y},{adj.RotOffsetEuler.z},");
                    if (adj.AxisCorrectionEuler != Vector3.zero) sb.Append($"a,{adj.AxisCorrectionEuler.x},{adj.AxisCorrectionEuler.y},{adj.AxisCorrectionEuler.z},");
                    if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public static void ImportPreset(string presetData)
        {
            if (string.IsNullOrEmpty(presetData)) return;
            BoneSettings.Clear();
            InitializeDefaultBoneSettings();
            GlobalPositionOffset = Vector3.zero;
            string[] lines = presetData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                try
                {
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    string key = parts[0].Trim();
                    string data = parts[1].Trim();
                    if (key == "GlobalPos")
                    {
                        string[] xyz = data.Split(',');
                        if (xyz.Length == 3) GlobalPositionOffset = new Vector3(float.Parse(xyz[0]), float.Parse(xyz[1]), float.Parse(xyz[2]));
                        continue;
                    }
                    var adj = GetOrCreateAdjustment(key);
                    string[] values = data.Split(',');
                    for (int i = 0; i < values.Length; i++)
                    {
                        string type = values[i].Trim().ToLower();
                        if (type == "r" && i + 3 < values.Length) { adj.RotOffsetEuler = new Vector3(float.Parse(values[i + 1]), float.Parse(values[i + 2]), float.Parse(values[i + 3])); i += 3; }
                        else if (type == "a" && i + 3 < values.Length) { adj.SetAxisCorrection(new Vector3(float.Parse(values[i + 1]), float.Parse(values[i + 2]), float.Parse(values[i + 3]))); i += 3; }
                    }
                }
                catch { }
            }
        }
        /// <summary>
        /// 智能脏数据检测：检测 IK 数据是否为无效数据（全为0或活动范围极小）
        /// </summary>
        private bool DetectDirtyIKData(VmdReader.VmdData vmdData)
        {
            if (vmdData == null || vmdData.IkFrames == null || vmdData.IkFrames.Count == 0)
                return false;

            // 定义 IK 骨骼名字（涵盖全角/半角/英文）
            var ikBoneNames = new[] {
                "左足IK", "右足IK", "左足ＩＫ", "右足ＩＫ",
                "Left Leg IK", "Right Leg IK", "左足Ik", "右足Ik"
            };

            // 从 BoneFrames 中提取 IK 骨骼位置信息
            var ikBoneFrames = vmdData.BoneFrames
                .Where(f => ikBoneNames.Contains(f.Name))
                .ToList();

            // 如果找不到位置帧，说明是脏数据
            if (ikBoneFrames.Count == 0)
                return true;

            // 计算统计数据
            int sampleCount = 0;
            int zeroPositionCount = 0;
            Vector3 minPos = new Vector3(9999, 9999, 9999);
            Vector3 maxPos = new Vector3(-9999, -9999, -9999);

            // 每隔 5 帧采样一次
            for (int i = 0; i < ikBoneFrames.Count; i += 5)
            {
                var pos = ikBoneFrames[i].Position;
                if (pos.sqrMagnitude < 0.0001f) zeroPositionCount++;
                minPos = Vector3.Min(minPos, pos);
                maxPos = Vector3.Max(maxPos, pos);
                sampleCount++;
            }

            float range = Vector3.Distance(minPos, maxPos);
            float zeroRatio = (sampleCount > 0) ? (float)zeroPositionCount / sampleCount : 0f;

            // 判据 A: 90% 以上时间在原点
            if (zeroRatio > 0.9f)
                return true;

            // 判据 B: 活动范围极小（小于 0.5 单位）
            if (sampleCount > 20 && range < 0.5f)
                return true;

            return false;
        }
        static MmddPoseController()
        {
            InitializeDefaultBoneSettings();
        }

        public static void UpdateBoneRotationOffset(string boneName, Vector3 offset)
        { GetOrCreateAdjustment(boneName).RotOffsetEuler = offset; }

        public static void UpdateBoneAxisCorrection(string boneName, Vector3 axisEuler)
        { GetOrCreateAdjustment(boneName).SetAxisCorrection(axisEuler); }

        [Flags]
        public enum BoneFlag
        {
            None = 0,
            IsIK = 1 << 0,
            IsCenter = 1 << 1,
            RightSide = 1 << 2,
            Finger = 1 << 3
        }

        // ... [Dictionaries MorphToUnityMap, MmdToUnityMap, etc. - NO CHANGE] ...
        private static readonly Dictionary<string, string[]> MorphToUnityMap = new Dictionary<string, string[]>
        {
            { "あ", new[] { "kuti_f00_vo_a", "tooth.f00_def_op", "f00_def_op" } },
            { "a",  new[] { "kuti_f00_vo_a", "tooth.f00_def_op", "f00_def_op" } },
            { "い", new[] { "kuti_f00_vo_i", "tooth.f00_def_cl", "f00_def_cl" } },
            { "i",  new[] { "kuti_f00_vo_i", "tooth.f00_def_cl", "f00_def_cl" } },
            { "う", new[] { "kuti_f00_vo_u" } },
            { "u",  new[] { "kuti_f00_vo_u" } },
            { "え", new[] { "kuti_f00_vo_e", "tooth.f00_def_op", "f00_def_op" } },
            { "e",  new[] { "kuti_f00_vo_e", "tooth.f00_def_op", "f00_def_op" } },
            { "お", new[] { "kuti_f00_vo_o", "tooth.f00_def_op", "f00_def_op" } },
            { "o",  new[] { "kuti_f00_vo_o", "tooth.f00_def_op", "f00_def_op" } },
            { "まばたき", new[] { "face.eye_f00_def_cl", "eyelash.eye_f00_def_cl", "eyelid.eye_f00_def_cl" } },
            { "blink",    new[] { "face.eye_f00_def_cl", "eyelash.eye_f00_def_cl", "eyelid.eye_f00_def_cl" } },
            { "笑い", new[] { "face.kuti_f00_egao_cl", "face.eye_f00_egao_cl", "eyelash.eye_f00_egao_cl", "eyelid.eye_f00_egao_cl", "tooth.f00_def_cl" } },
            { "smile", new[] { "face.kuti_f00_egao_cl", "face.eye_f00_egao_cl", "eyelash.eye_f00_egao_cl", "eyelid.eye_f00_egao_cl", "tooth.f00_def_cl" } },
            { "困る", new[] { "mayuge.f00_komari", "f00_komari" } },
            { "怒り", new[] { "mayuge.f00_ikari", "f00_ikari" } },
            { "真剣", new[] { "kuti_f00_sinken" } }
        };

        private static readonly Dictionary<string, string> MmdToUnityMap = new Dictionary<string, string>
        {
            { "センター", "cf_j_hips" }, { "グルーブ", "cf_j_hips" }, { "下半身", "cf_j_waist01" },{ "腰", "cf_j_waist01" },
            { "腰キャンセル左", "cf_j_thigh00_L" }, { "腰キャンセル右", "cf_j_thigh00_R" },
            { "上半身", "cf_j_spine01" }, { "上半身2", "cf_j_spine02" }, { "上半身3", "cf_j_spine03" },{ "首", "cf_j_neck" }, { "頭", "cf_j_head" },
            { "左肩", "cf_j_shoulder_L" }, { "左腕", "cf_j_arm00_L" }, { "左ひじ", "cf_j_forearm01_L" }, { "左手首", "cf_j_hand_L" },
            { "右肩", "cf_j_shoulder_R" }, { "右腕", "cf_j_arm00_R" }, { "右ひじ", "cf_j_forearm01_R" }, { "右手首", "cf_j_hand_R" },
            { "左足", "cf_j_thigh00_L" }, { "左ひざ", "cf_j_leg01_L" }, { "左足首", "cf_j_leg03_L" }, { "左つま先", "cf_j_toes_L" },
            { "右足", "cf_j_thigh00_R" }, { "右ひざ", "cf_j_leg01_R" }, { "右足首", "cf_j_leg03_R" }, { "右つま先", "cf_j_toes_R" },
            // ini 里用的脚踝骨名（用于 IK 几何计算）
            // 如果游戏里实际是 cf_j_leg03_L，这里会优先用 leg03；如果不存在，会回退到 cf_j_foot_L
            { "左足首_leg03", "cf_j_leg03_L" }, { "右足首_leg03", "cf_j_leg03_R" },
            { "左親指１", "cf_j_thumb02_L" }, { "左親指２", "cf_j_thumb03_L" },{ "左親指０", "cf_j_thumb01_L" },
            { "右親指１", "cf_j_thumb02_R" }, { "右親指２", "cf_j_thumb03_R" },{ "右親指０", "cf_j_thumb01_R" },
            { "左人指１", "cf_j_index01_L" }, { "左人指２", "cf_j_index02_L" }, { "左人指３", "cf_j_index03_L" },
            { "左中指１", "cf_j_middle01_L" }, { "左中指２", "cf_j_middle02_L" }, { "左中指３", "cf_j_middle03_L" },
            { "左薬指１", "cf_j_ring01_L" }, { "左薬指２", "cf_j_ring02_L" }, { "左薬指３", "cf_j_ring03_L" },
            { "左小指１", "cf_j_little01_L" }, { "左小指２", "cf_j_little02_L" }, { "左小指３", "cf_j_little03_L" },
            { "右人指１", "cf_j_index01_R" }, { "右人指２", "cf_j_index02_R" }, { "右人指３", "cf_j_index03_R" },
            { "右中指１", "cf_j_middle01_R" }, { "右中指２", "cf_j_middle02_R" }, { "右中指３", "cf_j_middle03_R" },
            { "右薬指１", "cf_j_ring01_R" }, { "右薬指２", "cf_j_ring02_R" }, { "右薬指３", "cf_j_ring03_R" },
            { "右小指１", "cf_j_little01_R" }, { "右小指２", "cf_j_little02_R" }, { "右小指３", "cf_j_little03_R" },
            { "Center", "cf_j_hips" }, { "Hips", "cf_j_hips" }, { "全ての親", "cf_j_root" }
        };

        private static readonly Dictionary<string, string> MmdHierarchy = new Dictionary<string, string>
        {
            // --- 核心躯干层级 (修正版) ---
            // 结构：AllParents -> Center -> Groove -> 腰 -> 体の重心(Hips) -> 下半身(Waist) & 上半身(Spine)
            
            { "センター", "全ての親" },
            { "グルーブ", "センター" },
            
            // 1. 插入"腰" (Back Hips)
            { "腰", "グルーブ" }, 
            
            // 2. "体の重心" (Hips) 挂在 "腰" 下面
            { "体の重心", "腰" }, 
            
            // 3. "下半身" (LowerBody) 挂在 "体の重心" 下面 (这是 TDA 标准)
            { "下半身", "体の重心" }, 

            // 4. "上半身" (Spine1) 也挂在 "体の重心" 下面
            { "上半身", "体の重心" },
            
            // --- 脊柱层级 (修正版) ---
            { "上半身2", "上半身" },
            { "上半身3", "上半身2" },
            { "首", "上半身3" },      
            { "頭", "首" },

            // --- 手臂 (保持不变) ---
            { "左肩", "上半身2" }, // 注意：肩通常挂在 上半身2 下，而不是 3 (视具体模型而定，Illusion模型通常肩在Spine2/3之间，挂2没问题)
            { "左腕", "左肩" }, { "左ひじ", "左腕" }, { "左手首", "左ひじ" },
            { "右肩", "上半身2" },
            { "右腕", "右肩" }, { "右ひじ", "右腕" }, { "右手首", "右ひじ" },

            // --- 腿部 (保持不变，注意父节点是 腰キャンセル) ---
            { "腰キャンセル左", "下半身" }, { "腰キャンセル右", "下半身" },
            { "左足", "腰キャンセル左" }, { "左ひざ", "左足" }, { "左足首", "左ひざ" },
            { "右足", "腰キャンセル右" }, { "右ひざ", "右足" }, { "右足首", "右ひざ" },

            // --- IK (保持不变) ---
            { "左足IK親", "全ての親" }, { "右足IK親", "全ての親" },
            { "左足ＩＫ", "左足IK親" }, { "右足ＩＫ", "右足IK親" },
            { "左つま先ＩＫ", "左足ＩＫ" }, { "右つま先ＩＫ", "右足ＩＫ" }
        };

        /// <summary>
        /// VMD 捩骨名称到虚拟捩骨名称的映射（用于创建虚拟捩骨）
        /// </summary>
        private static readonly Dictionary<string, string> VmdToTwistBoneMap = new Dictionary<string, string>
        {
            { "左腕捩", "左腕捩" }, { "右腕捩", "右腕捩" },
            { "左手捩", "左手捩" }, { "右手捩", "右手捩" },
            { "左捩", "左腕捩" }, { "右捩", "右腕捩" },
            { "左腕捻", "左腕捩" }, { "右腕捻", "右腕捩" }
        };

        /// <summary>
        /// 初始化 Twist Disperse 配置（参考 vmdlib 的 BoneTwistDisperse）
        /// </summary>
        private void InitializeTwistDisperse()
        {
            twistDisperseList.Clear();

            // ARMTWIST: 腕捩 -> 大臂，基础分散率 0.6（60% 分散到大臂，40% 保留在捩骨）
            twistDisperseList.Add(new TwistDisperseInfo
            {
                name = "ARMTWIST_L",
                twistBoneName = "左腕捩",
                disperseBoneName = "左腕",
                baseDisperseRate = 0.6f,
                enable = true
            });
            twistDisperseList.Add(new TwistDisperseInfo
            {
                name = "ARMTWIST_R",
                twistBoneName = "右腕捩",
                disperseBoneName = "右腕",
                baseDisperseRate = 0.6f,
                enable = true
            });

            // HANDTWIST: 手捩 -> 前臂，基础分散率 0.4（40% 分散到前臂，60% 保留在捩骨）
            twistDisperseList.Add(new TwistDisperseInfo
            {
                name = "HANDTWIST_L",
                twistBoneName = "左手捩",
                disperseBoneName = "左ひじ",
                baseDisperseRate = 0.6f,
                enable = true
            });
            twistDisperseList.Add(new TwistDisperseInfo
            {
                name = "HANDTWIST_R",
                twistBoneName = "右手捩",
                disperseBoneName = "右ひじ",
                baseDisperseRate = 0.6f,
                enable = true
            });
        }

        private class MorphCache
        {
            public SkinnedMeshRenderer renderer;
            public int index;
            public List<VmdReader.VmdMorphFrame> frames;
            public int currentIndex;
            public float tweakScale = 1.0f;
            public string mmdName;
            public float calculatedWeight;
            public bool isSmileRelated; 
            public bool isBlinkRelated; 
        }

        public class IKLink
        { public VirtualBone bone; public bool isKnee; public Vector3 minAngle; public Vector3 maxAngle; }

        public class IKChain
        {
            public string name;
            public VirtualBone target;
            public VirtualBone endEffector;
            public List<IKLink> links = new List<IKLink>();
            public int iteration = 15;
            public float limitAngle = 2.0f;
            public bool active = true;
            public CCDIKSolver solver;
            public bool isIkEnabledInVmd = true;
        }
        public class VirtualBone
        {
            public GameObject gameObject;
            public Transform transform;
            public Transform realTransform;
            public BezierCurve[] cachedCurves;

            // 绑定姿势下真实骨骼的局部位姿（用于回写）
            public Quaternion bindOffset;
            public Vector3 bindPos;

            // IK 解算用的工作副本（solver space），参考 FinalIK 的 solverPosition/solverRotation
            public Vector3 solverLocalPosition;
            public Quaternion solverLocalRotation;

            public List<VmdReader.VmdBoneFrame> frames;
            public int currentIndex;
            public BoneFlag flags;
            public string name;
        }

        private GameObject dummyRoot;
        private List<VirtualBone> activeBones = new List<VirtualBone>();
        private List<MorphCache> activeMorphs = new List<MorphCache>();
        private Dictionary<string, VirtualBone> dummyDict = new Dictionary<string, VirtualBone>();
        private List<IKChain> ikChains = new List<IKChain>();
        private Dictionary<string, List<VmdReader.VmdBoneFrame>> twistBoneFrames = new Dictionary<string, List<VmdReader.VmdBoneFrame>>();
        private Dictionary<string, float> centerInitialHeights = new Dictionary<string, float>();

        /// <summary>
        /// Twist Disperse 配置：定义捩骨旋转如何分散到目标骨骼
        /// </summary>
        private class TwistDisperseInfo
        {
            public string name;
            public string twistBoneName;      // 捩骨名称（虚拟骨骼，如"左腕捩"）
            public string disperseBoneName;  // 分散目标骨骼名称（如"左腕"）
            public float baseDisperseRate;    // 基础分散率（0-1），0.6 表示 60% 分散，40% 保留
            public bool enable;
            public VirtualBone twistBone;     // 虚拟捩骨引用
            public VirtualBone disperseBone; // 分散目标骨骼引用
            
            /// <summary>
            /// 获取当前有效的分散率（基础分散率 × UI 权重）
            /// UI 权重映射：0.0 = 禁用，1.0 = 基础分散率，2.0 = 最大分散率(1.0)
            /// </summary>
            public float GetEffectiveDisperseRate()
            {
                float uiWeight = 1.0f;
                
                // 根据捩骨类型获取 UI 权重
                if (twistBoneName.Contains("腕捩"))
                    uiWeight = MmddGui.Cfg_TwistWeight_Arm;
                else if (twistBoneName.Contains("手捩"))
                    uiWeight = MmddGui.Cfg_TwistWeight_Hand;
                else
                    uiWeight = MmddGui.Cfg_TwistWeight_Default;
                
                // UI 权重为 0 时禁用
                if (uiWeight <= 0f)
                    return 0f;
                
                // 将 UI 权重（0-2）映射到分散率
                // 权重 1.0 = 100% 基础分散率
                // 权重 2.0 = 最大分散率 1.0
                // 权重 0.5 = 50% 基础分散率
                if (uiWeight >= 2.0f)
                    return 1.0f; // 最大分散率
                else if (uiWeight <= 1.0f)
                    return baseDisperseRate * uiWeight; // 线性映射到基础分散率
                else
                    // 1.0 < uiWeight < 2.0：从基础分散率线性插值到 1.0
                    return Mathf.Lerp(baseDisperseRate, 1.0f, (uiWeight - 1.0f) / 1.0f);
            }
        }

        private List<TwistDisperseInfo> twistDisperseList = new List<TwistDisperseInfo>();
        private GameObject targetObject;
        private bool isPlaying = false;
        private float currentTime = 0f;
        private float maxTime = 0f;
        private bool loop = true;
        private bool useExternalTime = false;

        /// <summary>
        /// 调试用途 / 外部访问：当前驱动的目标角色对象。
        /// </summary>
        public GameObject TargetObject => targetObject;

        /// <summary>
        /// 调试用途：公开 IK 链列表给可视化/调试组件使用。
        /// </summary>
        public List<IKChain> DebugIkChains => ikChains;

        public Vector3 positionScale = new Vector3(0.085f, 0.085f, 0.085f);
        public float MorphScale = 0.7f;
        public bool CalibrateMode = false;
        public float LegWidthFix = 0.0f;
        public static string DebugBoneName = "右腕";
        public static string DebugText = "";
        public enum RootMotionMode
        {
            Standard = 0, 
            Groove = 1,   
            Off = 2       
        }

        public RootMotionMode rootMotionMode = RootMotionMode.Standard;

        private VmdReader.VmdData currentVmd; 
        private int lastProcessedIkFrameIndex = 0; 

        private Vector3 lastCenterRootXZ = Vector3.zero;
        private bool hasCenterRootXZ = false;


        private void UpdateIKKeyframeState(float currentTime)
        {
            if (ForceDisableIK || this.isDirtyIKData)
            {
                foreach (var chain in ikChains) chain.isIkEnabledInVmd = false;
                return;
            }

            if (currentVmd == null) return;


            if (currentVmd.IkFrames == null || currentVmd.IkFrames.Count == 0)
            {
                // 恢复 Play 方法中设定的 IK 状态
                foreach (var chain in ikChains)
                {
                    chain.isIkEnabledInVmd = shouldIKBeEnabled;
                }
                return;
            }

            if (currentTime < 0.1f) lastProcessedIkFrameIndex = 0;
            while (lastProcessedIkFrameIndex < currentVmd.IkFrames.Count - 1)
            {
                if (currentVmd.IkFrames[lastProcessedIkFrameIndex + 1].FrameNo <= currentTime)
                    lastProcessedIkFrameIndex++;
                else
                    break;
            }
            var currentFrame = currentVmd.IkFrames[lastProcessedIkFrameIndex];
            if (currentFrame.FrameNo <= currentTime && currentFrame.IkInfos != null)
            {
                foreach (var info in currentFrame.IkInfos)
                {
                    var chain = ikChains.FirstOrDefault(c => c.name == info.Name);
                    if (chain != null) chain.isIkEnabledInVmd = info.Enable;
                }
            }
        }
        public static MmddPoseController Install(GameObject container, GameObject target)
        {
            var controller = container.AddComponent<MmddPoseController>();
            controller.targetObject = target;
            return controller;
        }

        public void OnDestroy()
        { 
            if (dummyRoot != null) Destroy(dummyRoot);
        }

        public void LateUpdate()
        {
            // 1. 校准模式检查
            if (CalibrateMode) { UpdateVirtualSkeleton(-1f); return; }

            // 2. 播放状态检查
            if (!isPlaying && !useExternalTime) return;

            // 3. 时间推进
            if (!useExternalTime)
            {
                currentTime += Time.deltaTime * VmdFrameRate;
                ProcessLoopLogic();
            }

            // 4. 更新基础骨骼 (FK)
            UpdateVirtualSkeleton(currentTime);

            // 5. 根运动：让 dummyRoot 按 Center/ROOT 的 XZ 位移在舞台上平移（接近 MMD 的整体移动）
            UpdateRootMotionFromCenter();

            // 6. 对足 IK 目标进行距离裁剪，避免超出腿长太多导致的极端拉伸
            ClampLegIKTargetsToReachableRange();

            // 7. 更新捩骨 (Twist Bones)
            if (twistBoneFrames.Count > 0) UpdateTwistBones(currentTime);

            // 8. 处理 Twist Disperse（将捩骨旋转分散到目标骨骼）
            ProcessTwistDisperse();

            // 9. 更新腰キャンセル虚拟骨的旋转，用于抵消一部分下半身旋转
            UpdateWaistCancelRotation();

            // 更新 IK 开关状态
            // 根据当前时间去 VMD 里查，这一帧 IK 应该是开还是关
            UpdateIKKeyframeState(currentTime);

            // 执行 IK 解算
            foreach (var chain in ikChains)
            {
                // 只有当链条激活且求解器已初始化时才运行
                if (chain.active && chain.solver != null)
                {
                    if (chain.isIkEnabledInVmd)
                    {
                        chain.solver.Solve();
                        if (chain.name.Contains("足") || chain.name.Contains("Leg") || chain.name.Contains("Foot"))
                        {
                            if (chain.endEffector != null && chain.target != null)
                            {
                                chain.endEffector.transform.rotation = chain.target.transform.rotation;
                            }
                        }
                    }
                }
            }

            // 10. 视线修正
            ApplyGazeAdjustment();

            // 11. 将虚拟骨骼映射回真实骨骼
            ApplyToRealBones();

            foreach (var chain in ikChains)
            {
                // 只针对腿部 IK
                if (chain.active && chain.isIkEnabledInVmd && (chain.name.Contains("足") || chain.name.Contains("Leg")))
                {
                    // 只有当 IK 目标和真实脚踝都存在时
                    if (chain.target != null && chain.endEffector != null && chain.endEffector.realTransform != null)
                    {

                        chain.endEffector.realTransform.rotation = chain.target.transform.rotation;
                    }
                }
            }
            // 12. 表情更新 (Morphs)
            if (activeMorphs.Count > 0) ApplyMorphAnimation(currentTime);

            // F12 一键导出当前帧骨骼与 VMD 数据快照
            if (Input.GetKeyDown(KeyCode.F12))
            {
                DumpCurrentFrameSnapshot();
            }

            // 13. 重置外部时间标志
            useExternalTime = false;
        }
        public void SetTime(float frameTime)
        {
            useExternalTime = true;
            currentTime = frameTime;

            // 拖动进度条时，清除惯性，让动作瞬间到位
            foreach (var chain in ikChains)
            {
                if (chain.solver != null) chain.solver.ResetHistory();
            }

            ProcessLoopLogic();
            UpdateVirtualSkeleton(currentTime);
            if (twistBoneFrames.Count > 0) UpdateTwistBones(currentTime);
            ProcessTwistDisperse();
            if (activeMorphs.Count > 0) ApplyMorphAnimation(currentTime);
        }

        public void Stop()
        {
            isPlaying = false;
            ResetMorphs();

            foreach (var chain in ikChains)
            {
                if (chain.solver != null) chain.solver.ResetHistory();
            }
        }

        public void ResetFrameIndexes()
        {
            foreach (var bone in activeBones) bone.currentIndex = 0;
            foreach (var cache in activeMorphs) cache.currentIndex = 0;
        }

        [HideFromIl2Cpp]
        public void Play(VmdReader.VmdData vmdData, VmdReader.VmdData morphData = null)
        {
            if (targetObject == null || vmdData == null) return;
            Stop();

            // 重置状态
            this.currentVmd = vmdData;
            this.isDirtyIKData = false; 

            // 智能 IK 模式判定

            bool hasIkSwitchFrames = (this.currentVmd.IkFrames != null && this.currentVmd.IkFrames.Count > 0);


            bool hasIkBoneFrames = false;
            if (this.currentVmd.BoneFrames != null)
            {
                hasIkBoneFrames = this.currentVmd.BoneFrames.Any(f => f.Name.Contains("足IK") || f.Name.Contains("足ＩＫ"));
            }

            bool shouldEnableIK = false;

            if (hasIkSwitchFrames)
            {

                bool isDirty = DetectDirtyIKData(vmdData);
                this.isDirtyIKData = isDirty;
                shouldEnableIK = !isDirty;
            }
            else if (hasIkBoneFrames)
            {

                shouldEnableIK = true;
            }
            else
            {

                shouldEnableIK = false;
            }


            shouldIKBeEnabled = shouldEnableIK;

            // 2. 设置链条初始状态
            foreach (var chain in ikChains)
            {
                chain.isIkEnabledInVmd = shouldEnableIK;
                if (chain.solver != null) chain.solver.ResetHistory();
            }

            // 3. 对 VMD 数据做后续处理
            if (hasIkSwitchFrames)
            {
                this.currentVmd.IkFrames.Sort((a, b) => a.FrameNo.CompareTo(b.FrameNo));
            }

            this.lastProcessedIkFrameIndex = 0;


            VmdReader.VmdData dataForMorph = vmdData;
            if (morphData != null && morphData.MorphFrames != null && morphData.MorphFrames.Count > 0)
            {
                dataForMorph = morphData;
            }

            float maxFrame = 0;
            if (vmdData.BoneFrames.Count > 0) maxFrame = vmdData.BoneFrames.Max(f => f.FrameNo);
            if (dataForMorph.MorphFrames.Count > 0) maxFrame = Math.Max(maxFrame, dataForMorph.MorphFrames.Max(f => f.FrameNo));
            this.maxTime = maxFrame;

            PreprocessTwistBones(vmdData);
            BuildVirtualSkeleton(targetObject, vmdData);
            UpdateVirtualSkeleton(0);
            BuildIKChains(); 

            foreach (var tdi in twistDisperseList)
            {
                if (dummyDict.TryGetValue(tdi.twistBoneName, out var twistBone))
                    tdi.twistBone = twistBone;
                if (dummyDict.TryGetValue(tdi.disperseBoneName, out var disperseBone))
                    tdi.disperseBone = disperseBone;
            }

            foreach (var chain in ikChains)
            {
                chain.isIkEnabledInVmd = shouldEnableIK;

                // 初始化 Solver
                if (chain.solver == null)
                {
                }
            }

            BuildMorphCache(targetObject, dataForMorph);

            this.currentTime = 0;
            this.useExternalTime = false;
            this.isPlaying = true;

            // 根运动状态重置：以当前 Center 的局部 XZ 作为基准
            hasCenterRootXZ = false;
        }

        [HideFromIl2Cpp]
        public void PlayVpd(VpdReader.VpdData vpdData)
        {
            if (targetObject == null || vpdData == null) return;
            Stop();

            VmdReader.VmdData tempVmd = new VmdReader.VmdData { ModelName = "VPD_Dummy" };
            foreach (var vb in vpdData.Bones)
            {
                tempVmd.BoneFrames.Add(new VmdReader.VmdBoneFrame
                {
                    Name = vb.Name,
                    FrameNo = 0,
                    Position = vb.Position,
                    Rotation = vb.Rotation
                });
            }

            BuildVirtualSkeleton(targetObject, tempVmd);
            BuildIKChains();

            this.maxTime = 0;
            this.currentTime = 0;
            this.useExternalTime = true;
            this.isPlaying = true;

            UpdateVirtualSkeleton(0);

            foreach (var chain in ikChains)
            {
                // 检查链条是否激活，且求解器是否已初始化
                if (chain.active && chain.solver != null)
                {
                    chain.solver.Solve();
                }
            }

            ApplyToRealBones();
            activeMorphs.Clear();
        }

        private void ApplyGazeAdjustment()
        {
            if (!MmddGui.Cfg_EnableGaze || Camera.main == null) return;
            if (boneNeck == null || boneHead == null) return;

            Vector3 targetPos = Camera.main.transform.position;
            float weight = MmddGui.Cfg_GazeWeight;

            Vector3 headPos = boneHead.transform.position;
            Vector3 lookDirHead = targetPos - headPos;
            Quaternion targetRotHead = Quaternion.LookRotation(lookDirHead);

            if (Vector3.Angle(boneNeck.transform.parent.forward, lookDirHead) < 150f)
            {
                boneNeck.transform.rotation = Quaternion.Slerp(boneNeck.transform.rotation, targetRotHead, weight * 0.4f);
                boneHead.transform.rotation = Quaternion.Slerp(boneHead.transform.rotation, targetRotHead, weight * 0.6f);
            }

            if (boneEyeL != null && boneEyeR != null)
            {
                Vector3 eyeLPos = boneEyeL.transform.position;
                Vector3 lookDirEye = targetPos - eyeLPos;
                Quaternion targetRotEye = Quaternion.LookRotation(lookDirEye);

                Vector3 headForward = boneHead.transform.forward;
                float angle = Vector3.Angle(headForward, lookDirEye);

                if (angle < 80f)
                {
                    Quaternion eyeRot = Quaternion.Slerp(boneEyeL.transform.rotation, targetRotEye, weight);
                    boneEyeL.transform.rotation = eyeRot;
                    boneEyeR.transform.rotation = eyeRot;
                }
                else
                {
                    Quaternion resetRot = Quaternion.LookRotation(headForward);
                    boneEyeL.transform.rotation = Quaternion.Slerp(boneEyeL.transform.rotation, resetRot, 0.1f);
                    boneEyeR.transform.rotation = Quaternion.Slerp(boneEyeR.transform.rotation, resetRot, 0.1f);
                }
            }
        }

        private void BuildMorphCache(GameObject root, VmdReader.VmdData vmdData)
        {
            activeMorphs.Clear();
            if (vmdData.MorphFrames.Count == 0) return;

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var vmdMorphs = vmdData.MorphFrames.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var renderer in renderers)
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                renderer.updateWhenOffscreen = true;

                string rName = renderer.name.ToLower();
                bool isFaceMesh = rName.Contains("face") || rName.Contains("head");

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string shapeName = mesh.GetBlendShapeName(i);

                    foreach (var mapEntry in MorphToUnityMap)
                    {
                        bool matched = mapEntry.Value.Any(k => shapeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                        if (matched)
                        {
                            if (isFaceMesh)
                            {
                                bool isGenericOpen = shapeName.Contains("kuwae") || shapeName.Contains("def_op");

                                // 牙齿修复逻辑
                                if (shapeName.Contains("tooth")) isGenericOpen = false;

                                bool isVowelKey = mapEntry.Key == "あ" || mapEntry.Key == "え" || mapEntry.Key == "お";
                                if (isGenericOpen && isVowelKey) continue;
                            }

                            if (vmdMorphs.ContainsKey(mapEntry.Key))
                            {
                                var frames = vmdMorphs[mapEntry.Key];
                                frames.Sort((a, b) => a.FrameNo.CompareTo(b.FrameNo));

                                var cache = new MorphCache
                                {
                                    renderer = renderer,
                                    index = i,
                                    frames = frames,
                                    currentIndex = 0,
                                    tweakScale = 1.0f,
                                    mmdName = mapEntry.Key,
                                    calculatedWeight = 0f
                                };
                                string shapeNameLower = renderer.sharedMesh.GetBlendShapeName(i).ToLower();
                                cache.isSmileRelated = (shapeNameLower.Contains("egao") || shapeNameLower.Contains("smile")) &&
                                                       (shapeNameLower.Contains("eye") || shapeNameLower.Contains("eyelid"));
                                // 还要合并 mmdName 的判断
                                if (cache.mmdName == "笑い" || cache.mmdName == "smile" || cache.mmdName == "笑顔") cache.isSmileRelated = true;

                                cache.isBlinkRelated = shapeNameLower.Contains("def_cl") || shapeNameLower.Contains("blink") ||
                                                       cache.mmdName == "まばたき" || cache.mmdName == "blink";

                                activeMorphs.Add(cache);
                            }
                        }
                    }
                }
            }
        }

        private void ApplyMorphAnimation(float time)
        {
            if (activeMorphs.Count == 0) return;

            float currentSmileStrength = 0f;

            // 1. 第一步：计算原始权重 & 检测笑容强度
            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;

                // --- 时间轴插值计算 (保持原样) ---
                var frames = cache.frames;
                int i = cache.currentIndex;
                if (i >= frames.Count - 1 || frames[i].FrameNo > time) i = 0;
                while (i < frames.Count - 1 && time >= frames[i + 1].FrameNo) i++;
                cache.currentIndex = i;

                var prev = frames[i];
                var next = (i < frames.Count - 1) ? frames[i + 1] : prev;

                float t = 0f;
                float duration = next.FrameNo - prev.FrameNo;
                if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;

                float rawWeight = Mathf.Lerp(prev.Weight, next.Weight, t);
                cache.calculatedWeight = rawWeight * 100.0f;

                if (cache.calculatedWeight > 0.1f)
                {
                    if (cache.isSmileRelated)
                    {
                        if (cache.calculatedWeight > currentSmileStrength)
                            currentSmileStrength = cache.calculatedWeight;
                    }
                }
            }

            float globalScale = (MorphScale > 0) ? MorphScale : 1.0f;

            // 2. 第二步：合并权重 & 应用眨眼抑制
            Dictionary<long, float> mergedWeights = new Dictionary<long, float>();

            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;

                float finalWeight = cache.calculatedWeight;

                if (cache.isBlinkRelated)
                {
                    // 如果笑容很强，且当前是眨眼动作，则减弱眨眼幅度（防止眼皮穿模）
                    if (currentSmileStrength > 5f)
                    {
                        float suppression = Mathf.Clamp01((currentSmileStrength - 5f) / 20f);
                        finalWeight *= (1.0f - suppression);
                    }
                }

                finalWeight *= globalScale * cache.tweakScale;
                finalWeight = Mathf.Clamp(finalWeight, 0f, 100f);

                // 创建唯一 Key (InstanceID + Index)
                long key = ((long)cache.renderer.GetInstanceID() << 32) | (uint)cache.index;

                if (mergedWeights.ContainsKey(key))
                {
                    // 取最大值逻辑
                    if (finalWeight > mergedWeights[key]) mergedWeights[key] = finalWeight;
                }
                else
                {
                    mergedWeights[key] = finalWeight;
                }
            }

            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;
                long key = ((long)cache.renderer.GetInstanceID() << 32) | (uint)cache.index;

                if (mergedWeights.TryGetValue(key, out float weightToSet))
                {
                    cache.renderer.SetBlendShapeWeight(cache.index, weightToSet);
                    mergedWeights.Remove(key);
                }
            }
        }

        public void ResetMorphs()
        {
            if (activeMorphs == null) return;
            foreach (var cache in activeMorphs)
            {
                if (cache.renderer != null)
                {
                    cache.renderer.SetBlendShapeWeight(cache.index, 0f);
                }
            }
        }

        // ... [Rest of the file: PreprocessTwistBones, UpdateTwistBones, etc. - NO CHANGE] ...
        /// <summary>
        /// 预处理捩骨：从 VMD 数据中提取捩骨帧，并创建虚拟捩骨
        /// </summary>
        private void PreprocessTwistBones(VmdReader.VmdData vmdData)
        {
            twistBoneFrames.Clear();
            var boneFrameGroups = vmdData.BoneFrames.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());
            
            // 提取所有可能的捩骨帧
            foreach (var kvp in VmdToTwistBoneMap)
            {
                string vmdName = kvp.Key;
                string twistBoneName = kvp.Value;
                
                if (boneFrameGroups.TryGetValue(vmdName, out var frames))
                {
                    // 如果已经存在该捩骨的帧，合并；否则创建新的
                    if (twistBoneFrames.ContainsKey(twistBoneName))
                    {
                        twistBoneFrames[twistBoneName].AddRange(frames);
                    }
                    else
                    {
                        twistBoneFrames[twistBoneName] = new List<VmdReader.VmdBoneFrame>(frames);
                    }
                    vmdData.BoneFrames.RemoveAll(f => f.Name == vmdName);
                }
            }

            // 对每个捩骨的帧进行排序
            foreach (var kvp in twistBoneFrames)
            {
                kvp.Value.Sort((a, b) => a.FrameNo.CompareTo(b.FrameNo));
            }
        }

        /// <summary>
        /// 更新捩骨：从 VMD 数据中插值计算捩骨旋转，并应用到虚拟捩骨
        /// </summary>
        private void UpdateTwistBones(float time)
        {
            foreach (var kvp in twistBoneFrames)
            {
                string twistBoneName = kvp.Key;
                var frames = kvp.Value;
                if (frames.Count == 0) continue;

                // 查找当前时间对应的关键帧
                int i = 0;
                while (i < frames.Count - 1 && time >= frames[i + 1].FrameNo) i++;
                var prev = frames[i];
                var next = (i < frames.Count - 1) ? frames[i + 1] : prev;

                // 计算插值参数
                float t = 0f;
                float duration = next.FrameNo - prev.FrameNo;
                if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;
                t = Mathf.Clamp01(t);
                t = t * t * (3f - 2f * t); // 平滑插值

                // 转换 MMD 坐标系到 Unity 坐标系
                Quaternion rotA = new Quaternion(-prev.Rotation.x, prev.Rotation.y, -prev.Rotation.z, prev.Rotation.w).normalized;
                Quaternion rotB = new Quaternion(-next.Rotation.x, next.Rotation.y, -next.Rotation.z, next.Rotation.w).normalized;
                if (Quaternion.Dot(rotA, rotB) < 0) rotB = new Quaternion(-rotB.x, -rotB.y, -rotB.z, -rotB.w);
                Quaternion finalTwistRot = Quaternion.Slerp(rotA, rotB, t);

                // 应用到虚拟捩骨
                if (dummyDict.TryGetValue(twistBoneName, out var twistBone))
                {
                    twistBone.transform.localRotation = finalTwistRot;
                }
            }
        }

        /// <summary>
        /// 处理 Twist Disperse：将虚拟捩骨的旋转分散到目标骨骼（参考 vmdlib 的 processTwistDisperse）
        /// </summary>
        private void ProcessTwistDisperse()
        {
            foreach (var tdi in twistDisperseList)
            {
                if (!tdi.enable) continue;
                if (tdi.twistBone == null || tdi.disperseBone == null) continue;

                float disperseRate = tdi.GetEffectiveDisperseRate();
                if (disperseRate <= 0f) continue;

                // 1. 获取捩骨原始旋转 (包含脏数据)
                Quaternion orgQ = tdi.twistBone.transform.localRotation;


                // 提取 Twist (纯扭转)
                Quaternion twistQ = GetTwistRotation(orgQ, Vector3.right);

                // 如果提取失败（比如四元数异常），回退到 Identity
                if (twistQ == new Quaternion(0, 0, 0, 0)) twistQ = Quaternion.identity;


                // 2. 计算保留部分 (使用 Slerp 更平滑)
                float remainRate = 1.0f - disperseRate;
                Quaternion remainQ = Quaternion.Slerp(Quaternion.identity, twistQ, remainRate);

                // 3. 计算分散部分
                // disperseQ = twistQ - remainQ
                Quaternion disperseQ = twistQ * Quaternion.Inverse(remainQ);

                // 4. 应用旋转
                // 捩骨只保留纯净的 Twist (丢弃了 orgQ 里的 Swing 弯曲分量)
                tdi.twistBone.transform.localRotation = remainQ;

                // 父骨骼叠加分散出去的 Twist
                tdi.disperseBone.transform.localRotation = tdi.disperseBone.transform.localRotation * disperseQ;
            }
        }

        /// <summary>
        /// [数学工具] 提取四元数在指定轴上的扭转分量 (Twist)
        /// 这种方法完全避免了欧拉角转换和万向锁问题
        /// </summary>
        private Quaternion GetTwistRotation(Quaternion q, Vector3 axis)
        {
            // 将四元数的虚部 (x,y,z) 投影到目标轴 (axis) 上
            Vector3 ra = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Project(ra, axis);

            // 重组四元数 (投影后的虚部 + 原始实部 w)
            Quaternion twist = new Quaternion(p.x, p.y, p.z, q.w).normalized;

            // 防止奇异点 (如果旋转轴和目标轴垂直，投影为0，需要处理)
            if (twist.w == 0 && twist.x == 0 && twist.y == 0 && twist.z == 0)
                return Quaternion.identity;

            return twist;
        }

        private void BuildVirtualSkeleton(GameObject target, VmdReader.VmdData vmdData)
        {
            // 1. 清理旧数据
            if (dummyRoot != null) Destroy(dummyRoot);
            activeBones.Clear();
            dummyDict.Clear();

            // 创建虚拟根节点，并将其"挂载"到角色身上
            // 这样角色移动时，整个虚拟舞台和 IK 目标都会跟着移动
            dummyRoot = new GameObject("Mmdd_Dummy_Root");
            dummyRoot.transform.SetParent(target.transform, false);

            // 归零局部坐标，确保虚拟舞台的原点完全重合于角色的原点
            dummyRoot.transform.localPosition = Vector3.zero;
            dummyRoot.transform.localRotation = Quaternion.identity;
            dummyRoot.transform.localScale = Vector3.one;
            float groundHeight = target.transform.position.y;

            // 2. 构建真实骨骼映射
            var realBoneMap = new Dictionary<string, Transform>();
            MapBonesRecursive(target.transform, realBoneMap);

            // 计算并记录真实脚踝相对于地面的高度
            // 我们直接认定 target (角色根) 的 Y 就是地面基准
            ankleHeightOffsets.Clear();
            float baseGroundY = target.transform.position.y;

            if (realBoneMap.TryGetValue("cf_j_leg03_L", out var lAnk))
                ankleHeightOffsets["左足首"] = lAnk.position.y - baseGroundY;

            if (realBoneMap.TryGetValue("cf_j_leg03_R", out var rAnk))
                ankleHeightOffsets["右足首"] = rAnk.position.y - baseGroundY;
            // 4. VMD 数据分组
            var vmdGrouped = vmdData.BoneFrames.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());

            // 5. 确保核心 IK 骨骼存在
            string[] essentialIK = { "左足ＩＫ", "右足ＩＫ", "左つま先ＩＫ", "右つま先ＩＫ" };
            foreach (var ikName in essentialIK)
                if (!vmdGrouped.ContainsKey(ikName))
                    vmdGrouped[ikName] = new List<VmdReader.VmdBoneFrame>();
            // 强制确保 FK 结构骨骼（膝盖）存在
            // 即使 VMD 里没有膝盖的旋转数据，IK 解算也必须要有膝盖骨骼作为关节。
            string[] essentialStructure = {    "左ひざ", "右ひざ",
    "左足", "右足",
    "左足首", "右足首"
};


            foreach (var name in essentialStructure)
            {
                if (!vmdGrouped.ContainsKey(name))
                {
                    // 如果 VMD 里没这个骨头，手动创建一个空的帧列表
                    vmdGrouped[name] = new List<VmdReader.VmdBoneFrame>();
                }
            }
            // 6. 创建虚拟骨骼对象
            foreach (var kvp in vmdGrouped)
            {
                string mmdName = kvp.Key;
                var frames = kvp.Value;
                frames.Sort((a, b) => a.FrameNo.CompareTo(b.FrameNo));

                GameObject go = new GameObject(mmdName);
                VirtualBone vBone = new VirtualBone
                {
                    gameObject = go,
                    transform = go.transform,
                    frames = frames,
                    currentIndex = 0,
                    name = mmdName,
                    flags = BoneFlag.None
                };
                if (frames != null && frames.Count > 0)
                {
                    vBone.cachedCurves = new BezierCurve[frames.Count * 4];
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var f = frames[i];
                        if (f.Curve != null && f.Curve.Length >= 64)
                        {
                            // 解析 X, Y, Z, Rot 四个通道
                            // 通道偏移量：0->X, 16->Y, 32->Z, 48->Rot
                            vBone.cachedCurves[i * 4 + 0] = ParseCurve(f.Curve, 0);  // X
                            vBone.cachedCurves[i * 4 + 1] = ParseCurve(f.Curve, 16); // Y
                            vBone.cachedCurves[i * 4 + 2] = ParseCurve(f.Curve, 32); // Z
                            vBone.cachedCurves[i * 4 + 3] = ParseCurve(f.Curve, 48); // Rot
                        }
                        else
                        {
                            // 默认线性插值 (0,0) -> (1,1)
                            // 实际上线性是 P1(20,20)/127, P2(107,107)/127，这里简化处理或设为默认
                            var linear = new BezierCurve { P1 = new Vector2(0.15f, 0.15f), P2 = new Vector2(0.85f, 0.85f) };
                            vBone.cachedCurves[i * 4 + 0] = linear;
                            vBone.cachedCurves[i * 4 + 1] = linear;
                            vBone.cachedCurves[i * 4 + 2] = linear;
                            vBone.cachedCurves[i * 4 + 3] = linear;
                        }
                    }
                }
                if (mmdName.Contains("ＩＫ") || mmdName.Contains("IK")) vBone.flags |= BoneFlag.IsIK;
                if (mmdName == "センター" || mmdName == "Hips" || mmdName == "全ての親" || mmdName == "グルーブ")
                    vBone.flags |= BoneFlag.IsCenter;
                if (mmdName.Contains("右")) vBone.flags |= BoneFlag.RightSide;
                if (mmdName.Contains("指")) vBone.flags |= BoneFlag.Finger;

                string unityName = null;
                if (MmdToUnityMap.ContainsKey(mmdName)) unityName = MmdToUnityMap[mmdName];

                if (!string.IsNullOrEmpty(unityName) && realBoneMap.TryGetValue(unityName, out Transform realT))
                {
                    vBone.realTransform = realT;
                    vBone.bindOffset = realT.localRotation;
                    vBone.bindPos = realT.localPosition;
                }

                dummyDict[mmdName] = vBone;
                activeBones.Add(vBone);
            }

            // 7. 创建"腰キャンセル"
            var waistCancelNames = new[] { "腰キャンセル左", "腰キャンセル右" };
            foreach (var cancelName in waistCancelNames)
            {
                if (!dummyDict.ContainsKey(cancelName) && MmdToUnityMap.ContainsKey(cancelName))
                {
                    string unityName = MmdToUnityMap[cancelName];
                    if (realBoneMap.TryGetValue(unityName, out Transform realT))
                    {
                        GameObject go = new GameObject(cancelName);
                        VirtualBone vBone = new VirtualBone
                        {
                            gameObject = go,
                            transform = go.transform,
                            frames = new List<VmdReader.VmdBoneFrame>(),
                            currentIndex = 0,
                            name = cancelName,
                            flags = BoneFlag.None,
                            realTransform = null
                        };
                        dummyDict[cancelName] = vBone;
                        activeBones.Add(vBone);
                    }
                }
            }

            //设置层级关系
            VirtualBone allParentVb = null;
            dummyDict.TryGetValue("全ての親", out allParentVb);

            foreach (var kvp in dummyDict)
            {
                var child = kvp.Value;
                VirtualBone parent = null;
                VirtualBone groove = null;

                // 跳过虚拟捩骨（它们的层级关系会在 CreateTwistBones 中设置）
                if (child.name.Contains("捩") || child.name.Contains("捻"))
                    continue;

                // 提前定义 isFootIKRelated，防止 goto 跳过导致未赋值
                bool isFootIKRelated = child.name.Contains("足IK親") || child.name.Contains("足ＩＫ親") ||
                                       child.name.Contains("足ＩＫ") || child.name.Contains("足IK") ||
                                       child.name.Contains("つま先ＩＫ");

                // --- [A] 优先处理 Groove 和 身体 ---
                if (child.name == "グルーブ" || child.name == "Groove")
                {
                    if (dummyDict.TryGetValue("センター", out var center) || dummyDict.TryGetValue("Center", out center))
                    {
                        parent = center;
                        child.transform.SetParent(parent.transform, false);
                        goto FinishParenting;
                    }
                }

                if (child.name == "下半身" || child.name == "lower_body")
                {
                    if (dummyDict.TryGetValue("グルーブ", out groove)) parent = groove;
                    else if (dummyDict.TryGetValue("Groove", out groove)) parent = groove;
                    else if (dummyDict.TryGetValue("センター", out var center)) parent = center;
                    else if (dummyDict.TryGetValue("Center", out center)) parent = center;

                    if (parent != null)
                    {
                        child.transform.SetParent(parent.transform, false);
                        goto FinishParenting;
                    }
                }

                if (child.name == "上半身" || child.name == "upper_body")
                {
                    if (!MmdHierarchy.ContainsKey(child.name))
                    {
                        if (dummyDict.TryGetValue("下半身", out var lower)) parent = lower;
                        else if (dummyDict.TryGetValue("グルーブ", out groove)) parent = groove;
                        else if (dummyDict.TryGetValue("Groove", out groove)) parent = groove;
                        else if (dummyDict.TryGetValue("センター", out var center)) parent = center;

                        if (parent != null)
                        {
                            child.transform.SetParent(parent.transform, false);
                            goto FinishParenting;
                        }
                    }
                }

                // --- [B] 处理 IK 骨骼 ---
                if (isFootIKRelated)
                {
                    if ((child.name.Contains("足IK親") || child.name.Contains("足ＩＫ親")) && allParentVb != null)
                    {
                        parent = allParentVb;
                        child.transform.SetParent(allParentVb.transform, false);
                    }
                    else if (MmdHierarchy.TryGetValue(child.name, out string ikParentName) &&
                             dummyDict.TryGetValue(ikParentName, out parent))
                    {
                        child.transform.SetParent(parent.transform, false);
                    }
                    else if (allParentVb != null)
                    {
                        parent = allParentVb;
                        child.transform.SetParent(allParentVb.transform, false);
                    }
                    else
                    {
                        child.transform.SetParent(dummyRoot.transform, false);
                    }
                }
                // --- [C] 通用 FK 骨骼 ---
                else if (MmdHierarchy.TryGetValue(child.name, out string parentName2) &&
                         dummyDict.TryGetValue(parentName2, out parent))
                {
                    child.transform.SetParent(parent.transform, false);
                }
                else
                {
                    child.transform.SetParent(dummyRoot.transform, false);
                }

            FinishParenting:
                child.transform.localRotation = Quaternion.identity;

                // 初始化几何位置
                bool positionSet = false;

                bool isLegBone = (child.name.Contains("足") || child.name.Contains("ひざ") || child.name.Contains("つま先"))
                                 && !child.name.Contains("IK") && !child.name.Contains("ＩＫ");

                if (isLegBone && child.realTransform != null)
                {
                    if (parent != null && parent.realTransform != null)
                        child.transform.localPosition = parent.realTransform.InverseTransformPoint(child.realTransform.position);
                    else if (parent != null)
                        child.transform.localPosition = parent.transform.InverseTransformPoint(child.realTransform.position);
                    else
                        child.transform.position = child.realTransform.position;
                    positionSet = true;
                }

                else if (isFootIKRelated)
                {
                    Transform ankleBone = null;
                    Transform toeBone = null;
                    bool isLeft = child.name.Contains("左");

                    string leg03 = isLeft ? "cf_j_leg03_L" : "cf_j_leg03_R";
                    string foot = isLeft ? "cf_j_foot_L" : "cf_j_foot_R";
                    if (!realBoneMap.TryGetValue(leg03, out ankleBone)) realBoneMap.TryGetValue(foot, out ankleBone);

                    string toes = isLeft ? "cf_j_toes_L" : "cf_j_toes_R";
                    realBoneMap.TryGetValue(toes, out toeBone);

                    if (child.name.Contains("足IK親") || child.name.Contains("足ＩＫ親"))
                    {
                        if (ankleBone != null)
                        {
                            Vector3 worldPos = ankleBone.position;
                            worldPos.y = groundHeight;
                            child.transform.position = worldPos;
                            positionSet = true;
                        }
                    }
                    else if ((child.name.Contains("足ＩＫ") || child.name.Contains("足IK")) && !child.name.Contains("つま先"))
                    {
                        if (ankleBone != null)
                        {
                            child.transform.position = ankleBone.position;
                            positionSet = true;
                        }
                    }
                    else if (child.name.Contains("つま先ＩＫ") || child.name.Contains("つま先IK"))
                    {
                        if (ankleBone != null && toeBone != null)
                        {
                            Vector3 dir = (toeBone.position - ankleBone.position);
                            child.transform.position = ankleBone.position + dir * 1.4f;
                            positionSet = true;
                        }
                    }
                }

                if (!positionSet)
                {
                    if (child.realTransform != null && parent != null && parent.realTransform != null)
                        child.transform.localPosition = parent.realTransform.InverseTransformPoint(child.realTransform.position);
                    else if (child.realTransform != null)
                        child.transform.localPosition = dummyRoot.transform.InverseTransformPoint(child.realTransform.position);
                    else
                        child.transform.localPosition = Vector3.zero;
                }

                child.solverLocalPosition = child.transform.localPosition;
                child.solverLocalRotation = child.transform.localRotation;
            }

            // 10. 插入腰キャンセル骨骼（必须在设置层级关系之后，但在插入其他辅助骨骼之前）
            InsertWaistCancelBones();
            
            // 11. 创建虚拟捩骨（必须在设置层级关系之后）
            CreateTwistBones();
            
            // 12. 插入辅助骨骼
            InsertFootEndBones();

            // 12. 排序
            activeBones.Sort((a, b) => {
                int depthA = GetTransformDepth(a.transform);
                int depthB = GetTransformDepth(b.transform);
                return depthA.CompareTo(depthB);
            });

            // 12. 保存 Center 初始高度
            centerInitialHeights.Clear();
            foreach (var kvp in dummyDict)
            {
                if ((kvp.Value.flags & BoneFlag.IsCenter) != 0)
                {
                    centerInitialHeights[kvp.Key] = kvp.Value.transform.localPosition.y;
                }
            }
        }
        private static BezierCurve ParseCurve(byte[] data, int offset)
        {
            float p1x = data[offset + 0] / 127f;
            float p1y = data[offset + 4] / 127f;
            float p2x = data[offset + 8] / 127f;
            float p2y = data[offset + 12] / 127f;
            return new BezierCurve { P1 = new Vector2(p1x, p1y), P2 = new Vector2(p2x, p2y) };
        }
        private int GetTransformDepth(Transform t)
        {
            int depth = 0;
            while (t.parent != null)
            {
                depth++;
                t = t.parent;
            }
            return depth;
        }

        /// <summary>
        /// 创建虚拟捩骨：为每个需要分散的捩骨创建虚拟骨骼
        /// </summary>
        private void CreateTwistBones()
        {
            // 初始化 Twist Disperse 配置
            InitializeTwistDisperse();

            foreach (var tdi in twistDisperseList)
            {
                // 检查捩骨是否已存在（可能 VMD 中有数据）
                if (dummyDict.ContainsKey(tdi.twistBoneName))
                {
                    tdi.twistBone = dummyDict[tdi.twistBoneName];
                }
                else
                {
                    // 创建虚拟捩骨
                    // 找到父骨骼（分散目标骨骼）
                    if (!dummyDict.TryGetValue(tdi.disperseBoneName, out var parentBone))
                        continue;

                    GameObject go = new GameObject(tdi.twistBoneName);
                    VirtualBone twistBone = new VirtualBone
                    {
                        gameObject = go,
                        transform = go.transform,
                        frames = twistBoneFrames.ContainsKey(tdi.twistBoneName) 
                            ? twistBoneFrames[tdi.twistBoneName] 
                            : new List<VmdReader.VmdBoneFrame>(),
                        currentIndex = 0,
                        name = tdi.twistBoneName,
                        flags = BoneFlag.None,
                        realTransform = null, // 虚拟骨骼不直接映射到真实骨骼
                        bindOffset = Quaternion.identity,
                        bindPos = Vector3.zero,
                        solverLocalPosition = Vector3.zero,
                        solverLocalRotation = Quaternion.identity
                    };

                    // 设置父节点为分散目标骨骼
                    twistBone.transform.SetParent(parentBone.transform, false);
                    twistBone.transform.localPosition = Vector3.zero;
                    twistBone.transform.localRotation = Quaternion.identity;

                    dummyDict[tdi.twistBoneName] = twistBone;
                    activeBones.Add(twistBone);
                    tdi.twistBone = twistBone;
                }

                // 设置分散目标骨骼引用
                if (dummyDict.TryGetValue(tdi.disperseBoneName, out var disperseBone))
                {
                    tdi.disperseBone = disperseBone;
                }

                // 这里使用基础分散率，因为 UI 权重是运行时调整的
                tdi.enable = tdi.baseDisperseRate > 0f;
            }
        }

        /// <summary>
        /// 在虚拟骨架中插入"腰キャンセル"中间骨：下半身 -> 腰キャンセル(L/R) -> 大腿
        /// 作用：缓冲腰部大幅旋转/平移对腿 IK 的直接影响，靠近 MMDD 原始结构。
        /// </summary>
        private void InsertWaistCancelBones()
        {
            // 腰キャンセル挂在"下半身"下
            if (!dummyDict.TryGetValue("下半身", out var waist)) return;

            var pairs = new (string cancelName, string thighName)[]
            {
                ("腰キャンセル左", "左足"),
                ("腰キャンセル右", "右足")
            };

            foreach (var (cancelName, thighName) in pairs)
            {
                if (!dummyDict.TryGetValue(thighName, out var thigh)) continue;
                
                VirtualBone cancel = null;
                Transform positionBone = null; // 用于位置绑定的真实骨骼（cf_j_thigh00_L）
                
                if (dummyDict.TryGetValue(cancelName, out var existingCancel))
                {
                    // 腰キャンセル已经存在，只需要确保层级关系正确
                    if (existingCancel.transform.parent != waist.transform)
                    {
                        existingCancel.transform.SetParent(waist.transform, false);
                    }
                    cancel = existingCancel;
                }
                else
                {
                    var go = new GameObject(cancelName);
                    cancel = new VirtualBone
                    {
                        gameObject = go,
                        transform = go.transform,
                        name = cancelName,
                        flags = BoneFlag.None,
                        realTransform = null, // 不直接同步到真实骨骼
                        bindOffset = Quaternion.identity,
                        bindPos = Vector3.zero,
                        // 腰Cancel 本身不受 VMD 帧驱动，但为了兼容 UpdateVirtualSkeleton，需要提供空列表
                        frames = new List<VmdReader.VmdBoneFrame>(),
                        currentIndex = 0,
                        solverLocalRotation = Quaternion.identity
                    };
                    cancel.transform.SetParent(waist.transform, false);
                    dummyDict[cancelName] = cancel;
                    activeBones.Add(cancel);
                }
                
                // vmdlib ini [BonePosition]: BACK_HIPS_C_L=b:cf_j_thigh00_L
                // 位置绑定到 cf_j_thigh00_L（用于初始位置）
                if (MmdToUnityMap.TryGetValue(cancelName, out string unityName))
                {
                    // 从真实骨骼映射中获取 cf_j_thigh00_L（用于位置绑定）
                    var realBoneMap = new Dictionary<string, Transform>();
                    MapBonesRecursive(waist.realTransform?.root ?? waist.transform.root, realBoneMap);
                    realBoneMap.TryGetValue(unityName, out positionBone);
                }
                
                // 设置腰キャンセル的位置（如果还没有设置，或者需要更新）
                if (positionBone != null)
                {
                    // 使用 cf_j_thigh00_L 的位置（在真实父骨骼 cf_j_waist01 的局部空间中）
                    Vector3 realChildWorldPos = positionBone.position;
                    Vector3 realRelativePos = waist.realTransform != null 
                        ? waist.realTransform.InverseTransformPoint(realChildWorldPos)
                        : waist.transform.InverseTransformPoint(realChildWorldPos);
                    cancel.transform.localPosition = realRelativePos;
                }
                else if (cancel.transform.localPosition == Vector3.zero)
                {
                    // 如果没有真实骨骼映射，使用大腿的位置（回退方案）
                    Vector3 thighWorldPos = thigh.transform.position;
                    cancel.transform.position = thighWorldPos;
                }
                cancel.transform.localRotation = Quaternion.identity;
                cancel.solverLocalPosition = cancel.transform.localPosition;

                // 🟢 [关键修复] 重新挂接大腿：父改为腰Cancel，并归零局部位移，保持世界位置不变
                // 即使腰キャンセル已经存在，也需要重新挂接大腿，确保层级关系和localPosition正确
                Vector3 thighWorldPosBefore = thigh.transform.position; // 保存世界位置
                thigh.transform.SetParent(cancel.transform, false);
                thigh.transform.localPosition = Vector3.zero; // 相对于腰キャンセル归零
                
                // 🟢 [修复子节点层级] 确保大腿的子节点（膝盖、脚踝）的层级关系正确
                // 因为Unity的SetParent不会自动更新子节点的父节点，需要手动检查并强制修正
                string kneeName = thighName == "左足" ? "左ひざ" : "右ひざ";
                string ankleName = thighName == "左足" ? "左足首" : "右足首";
                
                // 1. 修复膝盖的父节点：必须是大腿
                if (dummyDict.TryGetValue(kneeName, out var knee))
                {
                    if (knee.transform.parent != thigh.transform)
                    {
                        knee.transform.SetParent(thigh.transform, false);
                    }
                }
                
                // 2. 修复脚踝的父节点：优先是膝盖，如果膝盖不存在则挂到大腿
                if (dummyDict.TryGetValue(ankleName, out var ankle))
                {
                    Transform correctParent = null;
                    
                    // 优先：如果膝盖存在，脚踝应该挂到膝盖下
                    if (dummyDict.TryGetValue(kneeName, out var knee2))
                    {
                        correctParent = knee2.transform;
                    }
                    else
                    {
                        // 回退：如果膝盖不存在，脚踝应该挂到大腿下
                        correctParent = thigh.transform;
                    }
                    
                    // 强制修正：无论当前挂在哪里，都要挂到正确的位置
                    if (ankle.transform.parent != correctParent)
                    {
                        ankle.transform.SetParent(correctParent, false);
                    }
                }
            }
        }

        /// <summary>
        /// 在虚拟骨架中为左右脚添加“脚底 End / 脚尖 End”辅助虚拟骨，
        /// 用于构建更贴近 MMDD ini 结构的 IK_FOOT / IK_TOE 末端点。
        ///
        /// 这些 End 点本身不受 VMD 驱动，只参与 IK endEffector 计算：
        /// - 脚底 End：大致位于脚掌中心偏下，用于脚 IK 末端（贴地更稳）
        /// - 脚尖 End：位于脚尖方向，用于脚尖 IK 末端（抬脚尖/点地）
        /// </summary>
        private void InsertFootEndBones()
                    {
            InsertSingleFootEndBones(
                footName: "左足首",
                toeName: "左つま先",
                soleEndName: "左足底End",
                toeEndName: "左つま先End");

            InsertSingleFootEndBones(
                footName: "右足首",
                toeName: "右つま先",
                soleEndName: "右足底End",
                toeEndName: "右つま先End");
        }

        /// <summary>
        /// 为单脚创建脚底 End / 脚尖 End 虚拟骨。
        /// </summary>
        private void InsertSingleFootEndBones(string footName, string toeName, string soleEndName, string toeEndName)
        {
            if (!dummyDict.TryGetValue(footName, out var footBone))
                return;

            // --- 脚底 End：挂在脚首下方，略向下并略向前，近似脚掌中心 ---
            if (!dummyDict.ContainsKey(soleEndName))
                {
                var goSole = new GameObject(soleEndName);
                var sole = new VirtualBone
                {
                    gameObject = goSole,
                    transform = goSole.transform,
                    name = soleEndName,
                    flags = BoneFlag.IsIK,         // 仅用于 IK，真实骨骼为空
                    realTransform = null,
                    bindOffset = Quaternion.identity,
                    bindPos = Vector3.zero,
                    frames = new List<VmdReader.VmdBoneFrame>(),
                    currentIndex = 0,
                    solverLocalRotation = Quaternion.identity
                };

                // 默认放在脚首本地坐标系下，稍微向下/向前一点：
                // -Y：贴近地面；+Z：脚尖方向（参考一般 Unity 脚模型朝向）
                Vector3 localOffset = new Vector3(0f, -0.02f, 0.05f);

                // 如果有脚尖虚拟骨，可以用脚首->脚尖方向估一个更靠谱的前向
                if (dummyDict.TryGetValue(toeName, out var toeBone) && footBone.realTransform != null && toeBone.realTransform != null)
                {
                    Vector3 footWorld = footBone.realTransform.position;
                    Vector3 toeWorld = toeBone.realTransform.position;
                    Vector3 dir = (toeWorld - footWorld);
                    float len = dir.magnitude;
                    if (len > 1e-4f)
                    {
                        dir /= len;
                        // 在水平方向上放到脚趾三分之一处，并稍微下移一点
                        Vector3 worldOffset = dir * (len * 0.33f);
                        worldOffset.y -= 0.02f;
                        Vector3 local = footBone.transform.InverseTransformPoint(footBone.transform.position + worldOffset);
                        localOffset = local;
                    }
                }

                sole.transform.SetParent(footBone.transform, false);
                sole.transform.localRotation = Quaternion.identity;
                sole.transform.localPosition = localOffset;
                sole.solverLocalPosition = sole.transform.localPosition;
                sole.solverLocalRotation = sole.transform.localRotation;

                dummyDict[soleEndName] = sole;
                activeBones.Add(sole);
            }

            // --- 脚尖 End：挂在脚尖骨下方，沿脚尖前向再延伸一点 ---
            if (dummyDict.TryGetValue(toeName, out var toeBoneRef) && !dummyDict.ContainsKey(toeEndName))
            {
                var goToe = new GameObject(toeEndName);
                var toeEnd = new VirtualBone
                {
                    gameObject = goToe,
                    transform = goToe.transform,
                    name = toeEndName,
                    flags = BoneFlag.IsIK,        // 仅用于 IK 末端
                    realTransform = null,
                    bindOffset = Quaternion.identity,
                    bindPos = Vector3.zero,
                    frames = new List<VmdReader.VmdBoneFrame>(),
                    currentIndex = 0,
                    solverLocalRotation = Quaternion.identity
                };

                toeEnd.transform.SetParent(toeBoneRef.transform, false);

                // 默认沿本地 Z 轴延伸一点，贴近脚尖方向
                Vector3 toeLocalOffset = new Vector3(0f, 0f, 0.05f);
                toeEnd.transform.localRotation = Quaternion.identity;
                toeEnd.transform.localPosition = toeLocalOffset;
                toeEnd.solverLocalPosition = toeEnd.transform.localPosition;
                toeEnd.solverLocalRotation = toeEnd.transform.localRotation;

                dummyDict[toeEndName] = toeEnd;
                activeBones.Add(toeEnd);
            }
        }

        /// <summary>
        /// 根运动：使用 Center / ROOT 骨骼在局部空间的 XZ 位移来驱动 dummyRoot 在世界中的平移，
        /// 让整个人尽量按 VMD 的 Center 轨迹在舞台上移动，而不是只靠腿去“够”远处的 IK 目标。
        /// 仅使用 XZ 分量，Y 仍由对地 / GlobalPositionOffset 控制。
        /// </summary>
        private void UpdateRootMotionFromCenter()
        {
            if (dummyRoot == null) return;

            // 模式 2: 关闭根运动，直接返回
            if (rootMotionMode == RootMotionMode.Off) return;

            VirtualBone centerBone = null;

            // 根据模式选择驱动源（老大）
            if (rootMotionMode == RootMotionMode.Groove)
            {
                // ---【Groove 模式】---
                // 适用：像《Cynical Night Plan》这种用 Groove 做主位移的动作
                // 优先级：所有的亲 -> Groove -> Center

                if (dummyDict.TryGetValue("全ての親", out var c1)) centerBone = c1;
                else if (dummyDict.TryGetValue("AllParents", out var c2)) centerBone = c2;

                // 关键点：在这里优先查找 Groove
                else if (dummyDict.TryGetValue("グルーブ", out var g1)) centerBone = g1;
                else if (dummyDict.TryGetValue("Groove", out var g2)) centerBone = g2;

                else if (dummyDict.TryGetValue("センター", out var c3)) centerBone = c3;
                else if (dummyDict.TryGetValue("Center", out var c4)) centerBone = c4;
            }
            else
            {
                // ---【标准模式 (默认)】---
                // 适用：普通动作，防止 Groove 的胯部摇摆导致滑步
                // 优先级：所有的亲 -> Center (强制忽略 Groove)

                if (dummyDict.TryGetValue("全ての親", out var c1)) centerBone = c1;
                else if (dummyDict.TryGetValue("AllParents", out var c2)) centerBone = c2;
                else if (dummyDict.TryGetValue("センター", out var c3)) centerBone = c3;
                else if (dummyDict.TryGetValue("Center", out var c4)) centerBone = c4;
            }

            // 兜底查找 (IsCenter 标记)
            if (centerBone == null)
            {
                foreach (var kvp in dummyDict)
                {
                    var vb = kvp.Value;
                    if ((vb.flags & BoneFlag.IsCenter) != 0)
                    {
                        // 在标准模式下，如果找到的是 Groove，跳过它
                        if (rootMotionMode == RootMotionMode.Standard &&
                           (vb.name == "グルーブ" || vb.name == "Groove"))
                            continue;

                        centerBone = vb;
                        break;
                    }
                }
            }

            if (centerBone == null) return;

            // --- 下面计算位移的逻辑保持不变 ---
            Vector3 localPos = centerBone.transform.localPosition;
            Vector3 curXZ = new Vector3(localPos.x, 0f, localPos.z);

            if (!hasCenterRootXZ)
            {
                lastCenterRootXZ = curXZ;
                hasCenterRootXZ = true;
                return;
            }

            Vector3 deltaXZ = curXZ - lastCenterRootXZ;

            if (deltaXZ.sqrMagnitude > 1e-6f)
            {
                Vector3 worldDelta = dummyRoot.transform.TransformDirection(deltaXZ);
                dummyRoot.transform.position += worldDelta;

                centerBone.transform.localPosition = new Vector3(
                    centerBone.transform.localPosition.x - deltaXZ.x,
                    centerBone.transform.localPosition.y,
                    centerBone.transform.localPosition.z - deltaXZ.z
                );

                lastCenterRootXZ = curXZ;
            }
        }

        /// <summary>
        /// 根据下半身的局部旋转，给“腰キャンセル左/右”施加反向旋转，
        /// 以抵消一部分腰部旋转对大腿的影响（类似 MMDD 的腰キャンセル行为）。
        /// </summary>
        private void UpdateWaistCancelRotation()
        {
            if (!dummyDict.TryGetValue("下半身", out var waist)) return;

            Quaternion waistLocalRot = waist.transform.localRotation;
            // 完全抵消：factor = 1；如需部分抵消可以调成 0.5 等
            const float factor = 0.0f;
            Quaternion invWaist = Quaternion.Inverse(waistLocalRot);
            Quaternion cancelRot = Quaternion.Slerp(Quaternion.identity, invWaist, factor);

            if (dummyDict.TryGetValue("腰キャンセル左", out var waistCancelL))
            {
                waistCancelL.transform.localRotation = cancelRot;
            }

            if (dummyDict.TryGetValue("腰キャンセル右", out var waistCancelR))
            {
                waistCancelR.transform.localRotation = cancelRot;
            }
        }

        /// <summary>
        /// 对足 IK 目标进行简单的几何裁剪：当目标距离大于 (大腿+小腿) 的最大可达长度时，
        /// 将目标沿髋->目标方向投影回可达球壳内，避免极端拉伸导致的腿“够不到还一直伸直”。
        /// 仅作用于腿部 IK 链（名字包含“足ＩＫ”或“Leg”且 useLeg=true）。
        /// </summary>
        private void ClampLegIKTargetsToReachableRange()
        {
            foreach (var chain in ikChains)
            {
                if (chain == null || chain.solver == null || !chain.solver.useLeg) continue;
                if (chain.solver.chains == null || chain.solver.chains.Length < 2) continue;

                // 只对脚 IK 链生效，避免影响手IK等
                if (!(chain.name.Contains("足ＩＫ") || chain.name.Contains("Foot") || chain.name.Contains("Leg"))) continue;

                Transform thigh = chain.solver.chains[chain.solver.chains.Length - 1]; // 最后一节是大腿
                Transform knee = chain.solver.chains[0];
                Transform foot = chain.solver.endEffector;
                Transform target = chain.solver.target;
                if (thigh == null || knee == null || foot == null || target == null) continue;

                float lenThigh = Vector3.Distance(thigh.position, knee.position);
                float lenShin = Vector3.Distance(knee.position, foot.position);
                float maxReach = lenThigh + lenShin;

                Vector3 hipPos = thigh.position;
                Vector3 targetPos = target.position;
                Vector3 toTarget = targetPos - hipPos;
                float dist = toTarget.magnitude;
                if (dist <= 1e-6f) continue;

                // 允许略微超出一点（5%），超过则进行裁剪
                // 🟢 [修复] 只裁剪 XZ 方向的距离，保持 Y 坐标不变（Y 由 VMD 数据驱动）
                if (dist > maxReach * 1.05f)
                {
                    float clampDist = maxReach * 0.98f;
                    // 保持原始 Y 坐标不变，只裁剪水平距离
                    float targetY = targetPos.y;
                    float deltaY = targetY - hipPos.y;
                    
                    // 计算可用的水平距离：sqrt(clampDist^2 - deltaY^2)
                    float availableDistXZ = Mathf.Sqrt(Mathf.Max(0f, clampDist * clampDist - deltaY * deltaY));
                    
                    if (availableDistXZ > 1e-6f)
                    {
                        // 计算水平方向向量
                        Vector3 toTargetXZ = new Vector3(toTarget.x, 0f, toTarget.z);
                        float distXZ = toTargetXZ.magnitude;
                        
                        if (distXZ > 1e-6f)
                        {
                            // 裁剪水平距离，保持 Y 不变
                            Vector3 normalizedXZ = toTargetXZ / distXZ;
                            Vector3 newTargetPosXZ = new Vector3(hipPos.x, 0f, hipPos.z) + normalizedXZ * availableDistXZ;
                            Vector3 newTargetPos = new Vector3(newTargetPosXZ.x, targetY, newTargetPosXZ.z);
                            target.position = newTargetPos;
                        }
                    }
                }

                // （已移除 Grounder Y 限制逻辑，改由 IK 求解器内部距离/几何检查负责防止脚飞起）
            }
        }

        private void BuildIKChains()
        {
            ikChains.Clear();
            // 腿 IK：使用脚首作为末端，但 Solver 内部会考虑脚底 End 作为几何末端（InsertFootEndBones 提供）
            AddIKChain("左足ＩＫ", "左足首", new[] { "左ひざ", "左足" }, true);
            AddIKChain("右足ＩＫ", "右足首", new[] { "右ひざ", "右足" }, true);

            // ⚠️ 脚尖 IK（IK_TOE）：目前旋转不稳定，暂时禁用，先保证主腿 IK 行为正确
            // 如后续需要，可只针对脚尖单骨做一维旋转 IK，再重新启用
            //AddIKChain("左つま先ＩＫ", "左つま先End", new[] { "左つま先", "左足首" }, false);
            //AddIKChain("右つま先ＩＫ", "右つま先End", new[] { "右つま先", "右足首" }, false);

            AddIKChain("左手ＩＫ", "左手首", new[] { "左ひじ", "左腕" }, false);
            AddIKChain("右手ＩＫ", "右手首", new[] { "右ひじ", "右腕" }, false);
        }

        private void AddIKChain(string targetName, string effectorName, string[] linkNames, bool isLeg)
        {
            if (!dummyDict.ContainsKey(targetName) || !dummyDict.ContainsKey(effectorName)) return;

            IKChain chain = new IKChain
            {
                name = targetName,
                target = dummyDict[targetName],
                endEffector = dummyDict[effectorName],
                iteration = isLeg ? 20 : 10 // 手臂不需要太多迭代
            };

            List<Transform> solverChains = new List<Transform>();

            foreach (var name in linkNames)
            {
                if (dummyDict.ContainsKey(name))
                {
                    var bone = dummyDict[name];
                    IKLink link = new IKLink { bone = bone };
                    // ... (你原来的 link 设置代码) ...
                    chain.links.Add(link);

                    // 收集 Transform 给 Solver 用
                    solverChains.Add(bone.transform);
                }
            }
            ikChains.Add(chain);

            // --- [新增] 初始化 CCDIKSolver ---
            chain.solver = new CCDIKSolver();
            chain.solver.target = chain.target.transform;       // IK Target (VMD驱动的点)
            chain.solver.endEffector = chain.endEffector.transform; // 脚踝 (需要对齐的点)
            chain.solver.chains = solverChains.ToArray();       // [0]=Knee, [1]=Thigh
            chain.solver.useLeg = isLeg;
            chain.solver.iterations = chain.iteration;

            // IK Bone 指向 Target 本身
            chain.solver.ikBone = chain.target.transform;

            // --- [改进] 计算腿 IK 的默认弯曲平面和几何参数（参考 vmdlib.py） ---
            if (isLeg && solverChains.Count >= 2)
            {
                Transform knee = solverChains[0];
                Transform thigh = solverChains[solverChains.Count - 1];
                Transform foot = chain.endEffector.transform;

                // 大腿指向膝盖的方向
                Vector3 thighToKnee = (knee.position - thigh.position).normalized;

                // 使用 dummyRoot 的前方作为"角色前方"参考
                Vector3 characterForward = dummyRoot != null ? dummyRoot.transform.forward : Vector3.forward;

                // 计算一个稳定的法线：大腿方向 × 角色前方
                Vector3 bendNormal = Vector3.Cross(thighToKnee, characterForward);
                if (bendNormal.sqrMagnitude < 1e-4f)
                {
                    // 如果和前方几乎平行，就退回用世界右轴（偏向 X 轴弯曲）
                    bendNormal = Vector3.right;
                }

                // 左右腿需要一致的弯曲方向：默认让膝盖朝前弯
                chain.solver.bendNormal = bendNormal.normalized;
                
                // 🟢 [改进] 初始化腿部几何参数（计算 baseInvQ 等）
                chain.solver.InitializeLegGeometry();
            }
        }

        private void UpdateVirtualSkeleton(float time)
        {
            // 调试标记
            bool shouldDebug = (time < 5f && Time.frameCount % 30 == 0);
            if (dummyRoot != null)
            {
                // 定义所有可能的 IK 亲骨骼名称
                string[] ikParentNames = { "左足IK親", "右足IK親", "左足ＩＫ親", "右足ＩＫ親" };

                foreach (var name in ikParentNames)
                {
                    if (dummyDict.TryGetValue(name, out var ikParentBone))
                    {
                        // 如果它的父级不是 dummyRoot (比如是 Center 或 Groove)，则强制断开
                        // 这样身体移动时，IK 亲骨骼会留在原地，保证 IK 坐标是绝对世界坐标
                        if (ikParentBone.transform.parent != dummyRoot.transform)
                        {
                            ikParentBone.transform.SetParent(dummyRoot.transform, false);
                        }
                    }
                }
            }
            foreach (var bone in activeBones)
            {
                if (time < 0) { bone.transform.localRotation = Quaternion.identity; continue; }
                if (bone.frames.Count == 0) continue;

                // 1. 查找关键帧索引
                int i = bone.currentIndex;
                var frames = bone.frames;
                if (i >= frames.Count - 1 || frames[i].FrameNo > time) i = 0;
                while (i < frames.Count - 1 && time >= frames[i + 1].FrameNo) i++;
                bone.currentIndex = i;

                var prev = frames[i];
                var next = (i < frames.Count - 1) ? frames[i + 1] : prev;

                // 2. 计算插值
                float duration = next.FrameNo - prev.FrameNo;
                float tLinear = 0f;
                if (duration > 0.0001f) tLinear = (time - prev.FrameNo) / duration;
                tLinear = Mathf.Clamp01(tLinear);

                float tx = tLinear, ty = tLinear, tz = tLinear, tr = tLinear;
                if (bone.cachedCurves != null)
                {
                    // 直接从数组取值，i 是当前帧索引
                    tx = EvaluateVmdBezier(bone.cachedCurves[i * 4 + 0], tLinear);
                    ty = EvaluateVmdBezier(bone.cachedCurves[i * 4 + 1], tLinear);
                    tz = EvaluateVmdBezier(bone.cachedCurves[i * 4 + 2], tLinear);
                    tr = EvaluateVmdBezier(bone.cachedCurves[i * 4 + 3], tLinear);
                }
                else
                {
                    // 回退逻辑
                    float tSmooth = tLinear * tLinear * (3f - 2f * tLinear);
                    tx = ty = tz = tr = tSmooth;
                }
                // 3. 处理旋转 (Rotation)
                float ax = prev.Rotation.x, ay = prev.Rotation.y, az = prev.Rotation.z, aw = prev.Rotation.w;
                float bx = next.Rotation.x, by = next.Rotation.y, bz = next.Rotation.z, bw = next.Rotation.w;
                bool isRightFinger = (bone.flags & BoneFlag.RightSide) != 0 && (bone.flags & BoneFlag.Finger) != 0;

                float finalAY = ay;
                float finalBY = by;
                if (isRightFinger) { finalAY = -ay; finalBY = -by; }

                Quaternion rotA = new Quaternion(-ax, finalAY, isRightFinger ? az : -az, aw);
                Quaternion rotB = new Quaternion(-bx, finalBY, isRightFinger ? bz : -bz, bw);
                Quaternion mmdRot = Quaternion.Slerp(rotA, rotB, tr);

                if (BoneSettings.TryGetValue(bone.name, out BoneAdjustment adj))
                {
                    if (adj.AxisCorrectionEuler != Vector3.zero)
                        mmdRot = adj.AxisCorrection * mmdRot * Quaternion.Inverse(adj.AxisCorrection);
                    if (adj.RotOffsetEuler != Vector3.zero)
                        mmdRot = mmdRot * Quaternion.Euler(adj.RotOffsetEuler);
                }

                bone.transform.localRotation = mmdRot;

                // 4. 处理位置 (Position)
                bool isIKOrCenter = (bone.flags & (BoneFlag.IsIK | BoneFlag.IsCenter)) != 0;
                if (!isIKOrCenter)
                {
                    string name = bone.name;
                    isIKOrCenter = name == "全ての親" || name == "センター" || name == "グルーブ" || name == "体の重心" ||
                                   name.EndsWith("足ＩＫ") || name.EndsWith("足IK") ||
                                   name.EndsWith("つま先ＩＫ") ||
                                   name.Contains("足IK親") || name.Contains("足ＩＫ親");
                }

                bool hasPosData = isIKOrCenter && bone.frames.Count > 0;

                if (hasPosData)
                {
                    Vector3 vmdPosA = prev.Position;
                    Vector3 vmdPosB = next.Position;

                    float ix = Mathf.Lerp(vmdPosA.x, vmdPosB.x, tx);
                    float iy = Mathf.Lerp(vmdPosA.y, vmdPosB.y, ty);
                    float iz = Mathf.Lerp(vmdPosA.z, vmdPosB.z, tz);

                    // 基础 VMD 位移（MMD 坐标系 -> Unity 坐标系）
                    Vector3 interpolated = new Vector3(-ix, iy, -iz);
                    Vector3 finalPos = Vector3.Scale(interpolated, positionScale);

                    // ================= [腿部间距补偿] =================
                    if (Mathf.Abs(LegWidthFix) > 0.0001f)
                    {
                        // 针对左脚 IK：向左推 (通常是 -X 方向，根据你的坐标系调整)
                        if (bone.name == "左足ＩＫ" || bone.name == "左足IK")
                        {
                            // 假设 Unity 中左边是 -X (视模型朝向而定，如果是反的就把 -= 改 +=)
                            finalPos.x -= LegWidthFix;
                        }
                        // 针对右脚 IK：向右推 (通常是 +X 方向)
                        else if (bone.name == "右足ＩＫ" || bone.name == "右足IK")
                        {
                            finalPos.x += LegWidthFix;
                        }
                    }

                    // A. 处理 Center 骨骼的初始高度
                    if ((bone.flags & BoneFlag.IsCenter) != 0)
                    {
                        if (centerInitialHeights.TryGetValue(bone.name, out float initialHeight))
                            finalPos.y += initialHeight;
                        finalPos += GlobalPositionOffset;
                    }

                    // 🟢 [核心修复] 处理 IK 亲骨骼的初始偏移 (Rest Pose Offset)
                    // MMD 的 IK Parent 骨骼通常不在原点，而是在脚踝的初始位置
                    // VMD 数据是相对于这个初始位置的增量，所以必须把 solverLocalPosition (初始位姿) 加回去
                    if (bone.name.Contains("足IK親") || bone.name.Contains("足ＩＫ親"))
                    {
                        finalPos += bone.solverLocalPosition;
                    }

                    // B. 防滑步逻辑 (Force World Coordinate)
                    // 仅针对真正的 "足IK" (Target)，且排除有父级带动的情况
                    bool isFootIKForOffset = bone.name.Contains("左足ＩＫ") || bone.name.Contains("右足ＩＫ") ||
                                             bone.name.Contains("左足IK") || bone.name.Contains("右足IK");

                    // 再次确认不包含 "亲"
                    if (bone.name.Contains("親")) isFootIKForOffset = false;

                    bool hasIKParent = false;
                    if (bone.transform.parent != null)
                    {
                        string pName = bone.transform.parent.name;
                        hasIKParent = pName.Contains("足IK親") || pName.Contains("足ＩＫ親") || pName.Contains("Parent");
                    }

                    // 标准局部坐标逻辑
                    bone.transform.localPosition = finalPos;
                }
            }
        }

        /// <summary>
        /// 根据骨骼帧中的 VMD 曲线数据，计算给定线性时间 t (0~1) 的 Bézier 插值结果。
        /// curve: 64 字节，X/Y/Z/R 各 16 字节，每 4 字节一个控制点，仅低 8 位有效 (0~127)。
        /// baseOffset: 0,16,32,48 分别对应 X/Y/Z/R。
        /// 返回值为 0~1 的插值率。
        /// </summary>
        private static float EvaluateVmdBezier(BezierCurve curve, float t)
        {
            // 直接使用预计算好的值，不需要判空和除法
            float p1x = curve.P1.x;
            float p1y = curve.P1.y;
            float p2x = curve.P2.x;
            float p2y = curve.P2.y;

            if ((p1x == p1y) && (p2x == p2y)) return t; // 线性优化

            float tt = Mathf.Clamp01(t);
            for (int i = 0; i < 8; i++) // 这里的迭代次数可以尝试降到 4 或 5 以进一步优化
            {
                float xEst = SampleBezier(p1x, p2x, tt);
                float slope = SampleBezierDerivative(p1x, p2x, tt);
                if (Mathf.Abs(slope) < 1e-5f) break;
                tt -= (xEst - t) / slope;
                tt = Mathf.Clamp01(tt);
            }
            return SampleBezier(p1y, p2y, tt);
        }

        private static float SampleBezier(float p1, float p2, float t)
        {
            float oneMinusT = 1f - t;
            // 标准三次 Bézier：B(t) = 3(1-t)^2 t p1 + 3(1-t)t^2 p2 + t^3
            return 3f * oneMinusT * oneMinusT * t * p1
                 + 3f * oneMinusT * t * t * p2
                 + t * t * t;
        }

        private static float SampleBezierDerivative(float p1, float p2, float t)
        {
            float oneMinusT = 1f - t;
            return 3f * oneMinusT * oneMinusT * p1
                 + 6f * oneMinusT * t * (p2 - p1)
                 + 3f * t * t;
        }

        private void ApplyToRealBones()
        {
            foreach (var bone in activeBones)
            {
                if (bone.realTransform != null)
                {
                    bool isLegBone = bone.name.Contains("左足") || bone.name.Contains("右足") ||
                                     bone.name.Contains("左ひざ") || bone.name.Contains("右ひざ") ||
                                     bone.name.Contains("左足首") || bone.name.Contains("右足首");

                    if (bone.realTransform.name == "cf_j_hips")
                    {
                        Quaternion centerRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("センター", out var cBone)) centerRot = cBone.transform.localRotation;
                        else if (dummyDict.TryGetValue("Center", out var cBone2)) centerRot = cBone2.transform.localRotation;

                        Quaternion grooveRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("グルーブ", out var gBone)) grooveRot = gBone.transform.localRotation;
                        else if (dummyDict.TryGetValue("Groove", out var gBone2)) grooveRot = gBone2.transform.localRotation;

                        Quaternion waistRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("腰", out var wBone)) waistRot = wBone.transform.localRotation;

                        // [重要] 显式查找 Hips，防止重复叠加
                        Quaternion hipsRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("体の重心", out var h)) hipsRot = h.transform.localRotation;
                        else if (dummyDict.TryGetValue("Hips", out var h2)) hipsRot = h2.transform.localRotation;

                        Quaternion lowerBodyRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("下半身", out var lbBone)) lowerBodyRot = lbBone.transform.localRotation;

                        // 累积所有父级旋转
                        Quaternion finalCombinedRot = centerRot * grooveRot * waistRot * hipsRot * lowerBodyRot;
                        bone.realTransform.localRotation = finalCombinedRot * bone.bindOffset;
                    }
                    // =================================================================================
                    // 2. [Waist] 下半身：强制归零 (防止双重旋转)  <--- 之前漏掉的就是这个！
                    // =================================================================================
                    else if (bone.realTransform.name == "cf_j_waist01")
                    {
                        // 因为父级(Hips)已经替它转了，所以这里必须保持静止
                        bone.realTransform.localRotation = bone.bindOffset;
                    }
                    // =================================================================================
                    // 3. [Spine] 上半身：UI 模式切换 (兼容不同动作)
                    // =================================================================================
                    else if (bone.realTransform.name == "cf_j_spine01")
                    {
                        Quaternion grooveRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("グルーブ", out var g)) grooveRot = g.transform.localRotation;
                        else if (dummyDict.TryGetValue("Groove", out var g2)) grooveRot = g2.transform.localRotation;

                        Quaternion waistRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("腰", out var w)) waistRot = w.transform.localRotation;

                        Quaternion lowerBodyRot = Quaternion.identity;
                        if (dummyDict.TryGetValue("下半身", out var lbBone)) lowerBodyRot = lbBone.transform.localRotation;

                        Quaternion cancellation = Quaternion.identity;
                        if (upperBodyMode == UpperBodyMode.Stabilize)
                        {
                            // 稳定模式：抵消所有胯部动作 (Groove + Waist + LowerBody)
                            cancellation = Quaternion.Inverse(grooveRot * waistRot * lowerBodyRot);
                        }
                        else
                        {
                            // 跟随模式：只抵消下半身，保留 Groove/Waist 的整体带动
                            cancellation = Quaternion.Inverse(lowerBodyRot);
                        }

                        bone.realTransform.localRotation = cancellation * bone.bindOffset * bone.transform.localRotation;
                    }
                    // =================================================================================
                    // 4. [Others] 其他骨骼：标准处理
                    // =================================================================================
                    else
                    {
                        bone.realTransform.localRotation = bone.bindOffset * bone.transform.localRotation;
                    }

                    // --- 位置处理逻辑 (保持不变) ---
                    Vector3 virtualWorldPos = bone.transform.position;
                    if ((bone.flags & (BoneFlag.IsCenter | BoneFlag.IsIK)) != 0)
                    {
                        Vector3 targetWorldPos = virtualWorldPos;
                        if ((bone.flags & BoneFlag.IsCenter) != 0 && (bone.name == "センター" || bone.name == "Center"))
                        {
                            VirtualBone vGroove = null;
                            if (dummyDict.TryGetValue("グルーブ", out vGroove) || dummyDict.TryGetValue("Groove", out vGroove))
                            {
                                if (vGroove.realTransform == bone.realTransform) targetWorldPos = vGroove.transform.position;
                            }
                        }
                        if (bone.name == "下半身" && bone.realTransform != null)
                        {
                            float weight = 0.5f;
                            Quaternion reducedRot = Quaternion.Slerp(Quaternion.identity, bone.transform.localRotation, weight);
                            bone.realTransform.localRotation = bone.bindOffset * reducedRot;
                        }
                        if (bone.realTransform.parent != null)
                        {
                            Vector3 finalPos = bone.realTransform.parent.InverseTransformPoint(targetWorldPos);
                            bone.realTransform.localPosition = finalPos;
                        }
                        else bone.realTransform.position = targetWorldPos;
                    }
                    else if (isLegBone)
                    {
                        bool isAnkle = bone.name.Contains("足首");
                        if (isAnkle && bone.realTransform != null)
                        {
                            float heightOffset = 0f;
                            if (ankleHeightOffsets.TryGetValue(bone.name, out float offset)) heightOffset = offset;
                            bone.realTransform.position = virtualWorldPos + Vector3.up * heightOffset;
                            bone.realTransform.rotation = bone.transform.rotation;
                        }
                        else if (bone.realTransform.parent != null)
                        {
                            Vector3 realLocalPos = bone.realTransform.parent.InverseTransformPoint(virtualWorldPos);
                            bone.realTransform.localPosition = realLocalPos;
                            Quaternion virtualWorldRot = bone.transform.rotation;
                            Quaternion realLocalRot = Quaternion.Inverse(bone.realTransform.parent.rotation) * virtualWorldRot;
                            bone.realTransform.localRotation = bone.bindOffset * realLocalRot;
                        }
                        else
                        {
                            bone.realTransform.position = virtualWorldPos;
                            bone.realTransform.rotation = bone.transform.rotation;
                        }
                    }
                }
            }
        }

        private void ProcessLoopLogic()
        {
            float actualEnd = (LoopEnd > 0.1f && LoopEnd < maxTime) ? LoopEnd : maxTime;
            if (actualEnd <= LoopStart) actualEnd = maxTime;
            if (currentTime >= actualEnd)
            {
                if (EnableLoop && actualEnd > 0.1f)
                {
                    currentTime = LoopStart;
                    ResetFrameIndexes();
                }
                else
                {
                    currentTime = actualEnd;
                    Stop();
                }
            }
        }
        private void MapBonesRecursive(Transform t, Dictionary<string, Transform> map)
        {
            if (!map.ContainsKey(t.name)) map.Add(t.name, t);
            for (int i = 0; i < t.childCount; i++) MapBonesRecursive(t.GetChild(i), map);
        }

        // === F12 导出当前帧的虚拟骨骼 / 真实骨骼 / VMD 源数据到 TXT ===
        [HideFromIl2Cpp]
        private void DumpCurrentFrameSnapshot()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "MmddFrameDump");
                Directory.CreateDirectory(dir);

                string fileName = $"Frame_{Time.frameCount}_t{currentTime:F3}.txt";
                string path = Path.Combine(dir, fileName);

                var sb = new StringBuilder(64 * 1024);

                sb.AppendLine("[Mmdd] Frame Snapshot");
                sb.AppendLine($"Time      : {currentTime:F3} (frames)");
                sb.AppendLine($"UnityTime : {Time.time:F3} (seconds)");
                sb.AppendLine($"Frame     : {Time.frameCount}");
                sb.AppendLine($"TargetObj : {targetObject?.name ?? "<null>"}");
                sb.AppendLine($"DummyRoot : {(dummyRoot != null ? dummyRoot.transform.position.ToString("F4") : "<null>")}");
                sb.AppendLine();
                
                // Groove Y轴数据和关键骨骼的父节点信息
                VirtualBone vGroove = null;
                if (dummyDict.TryGetValue("グルーブ", out vGroove) || dummyDict.TryGetValue("Groove", out vGroove))
                {
                    bool grooveHasPosData = vGroove.frames.Count > 0;
                    float grooveY = vGroove.transform.localPosition.y;
                    sb.AppendLine("[Groove Debug]");
                    sb.AppendLine($"  Groove Name      : {vGroove.name}");
                    sb.AppendLine($"  Has Position Data: {grooveHasPosData}");
                    sb.AppendLine($"  LocalPosition.Y  : {grooveY:F4}");
                    sb.AppendLine($"  WorldPosition    : {vGroove.transform.position.ToString("F4")}");
                    if (vGroove.transform.parent != null)
                    {
                        sb.AppendLine($"  Virtual Parent   : {vGroove.transform.parent.name}, World={vGroove.transform.parent.position.ToString("F4")}");
                    }
                    sb.AppendLine();
                }

                foreach (var bone in activeBones)
                {
                    sb.AppendLine($"[Bone] {bone.name}");
                    sb.AppendLine($"  Flags        : {bone.flags}");

                    // 虚拟骨骼（Dummy）
                    if (bone.transform != null)
                    {
                        Vector3 vLocalPos = bone.transform.localPosition;
                        Vector3 vWorldPos = bone.transform.position;
                        Vector3 vLocalEuler = bone.transform.localRotation.eulerAngles;
                        Vector3 vWorldEuler = bone.transform.rotation.eulerAngles;

                        sb.AppendLine($"  Virtual.LocalPos   : ({vLocalPos.x:F4}, {vLocalPos.y:F4}, {vLocalPos.z:F4})");
                        sb.AppendLine($"  Virtual.WorldPos   : ({vWorldPos.x:F4}, {vWorldPos.y:F4}, {vWorldPos.z:F4})");
                        sb.AppendLine($"  Virtual.LocalEuler : ({vLocalEuler.x:F2}, {vLocalEuler.y:F2}, {vLocalEuler.z:F2})");
                        sb.AppendLine($"  Virtual.WorldEuler : ({vWorldEuler.x:F2}, {vWorldEuler.y:F2}, {vWorldEuler.z:F2})");
                        
                        // 虚拟父节点信息
                        if (bone.transform.parent != null)
                        {
                            sb.AppendLine($"  Virtual.Parent     : {bone.transform.parent.name}, World={bone.transform.parent.position.ToString("F4")}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("  Virtual : <no transform>");
                    }

                    // 真实骨骼（Real）
                    if (bone.realTransform != null)
                    {
                        Vector3 rLocalPos = bone.realTransform.localPosition;
                        Vector3 rWorldPos = bone.realTransform.position;
                        Vector3 rLocalEuler = bone.realTransform.localRotation.eulerAngles;
                        Vector3 rWorldEuler = bone.realTransform.rotation.eulerAngles;

                        sb.AppendLine($"  Real.Path      : {GetTransformPath(bone.realTransform)}");
                        sb.AppendLine($"  Real.LocalPos  : ({rLocalPos.x:F4}, {rLocalPos.y:F4}, {rLocalPos.z:F4})");
                        sb.AppendLine($"  Real.WorldPos  : ({rWorldPos.x:F4}, {rWorldPos.y:F4}, {rWorldPos.z:F4})");
                        sb.AppendLine($"  Real.LocalEuler: ({rLocalEuler.x:F2}, {rLocalEuler.y:F2}, {rLocalEuler.z:F2})");
                        sb.AppendLine($"  Real.WorldEuler: ({rWorldEuler.x:F2}, {rWorldEuler.y:F2}, {rWorldEuler.z:F2})");
                        
                        // 真实父节点信息
                        if (bone.realTransform.parent != null)
                        {
                            sb.AppendLine($"  Real.Parent       : {bone.realTransform.parent.name}, World={bone.realTransform.parent.position.ToString("F4")}");
                        }
                        
                        // 对于关键骨骼，添加Y轴差值分析
                        if (bone.transform != null && (bone.name == "センター" || bone.name == "下半身" || 
                            (bone.name.Contains("左足") && !bone.name.Contains("ひざ") && !bone.name.Contains("足首"))))
                        {
                            float yDiff = rWorldPos.y - bone.transform.position.y;
                            sb.AppendLine($"  Y-Diff (Real-Virtual): {yDiff:F4}");
                            if (vGroove != null && vGroove.frames.Count > 0)
                            {
                                float grooveY = vGroove.transform.localPosition.y;
                                sb.AppendLine($"  Groove Y Value      : {grooveY:F4}");
                                sb.AppendLine($"  Y-Diff / Groove Y   : {(Mathf.Abs(grooveY) > 0.0001f ? (yDiff / grooveY).ToString("F2") : "N/A")}");
                            }
                        }
                    }
                    else
                    {
                        sb.AppendLine("  Real  : <no realTransform>");
                    }

                    // VMD 源数据（当前时间点邻近帧）
                    if (bone.frames != null && bone.frames.Count > 0)
                    {
                        var frames = bone.frames;

                        int i = bone.currentIndex;
                        if (i >= frames.Count - 1 || frames[i].FrameNo > currentTime) i = 0;
                        while (i < frames.Count - 1 && currentTime >= frames[i + 1].FrameNo) i++;

                        var prev = frames[i];
                        var next = (i < frames.Count - 1) ? frames[i + 1] : prev;

                        float duration = next.FrameNo - prev.FrameNo;
                        float tLinear = 0f;
                        if (duration > 0.0001f) tLinear = (currentTime - prev.FrameNo) / duration;
                        tLinear = Mathf.Clamp01(tLinear);

                        sb.AppendLine($"  VMD.PrevFrame  : {prev.FrameNo}");
                        sb.AppendLine($"  VMD.NextFrame  : {next.FrameNo}");
                        sb.AppendLine($"  VMD.tLinear    : {tLinear:F4}");

                        // 原始 VMD 位置/旋转（MMD 坐标）
                        var pPos = prev.Position;
                        var nPos = next.Position;
                        var pRot = prev.Rotation;
                        var nRot = next.Rotation;

                        sb.AppendLine($"  VMD.Prev.Pos   : ({pPos.x:F4}, {pPos.y:F4}, {pPos.z:F4})");
                        sb.AppendLine($"  VMD.Next.Pos   : ({nPos.x:F4}, {nPos.y:F4}, {nPos.z:F4})");
                        sb.AppendLine($"  VMD.Prev.RotQ  : ({pRot.x:F5}, {pRot.y:F5}, {pRot.z:F5}, {pRot.w:F5})");
                        sb.AppendLine($"  VMD.Next.RotQ  : ({nRot.x:F5}, {nRot.y:F5}, {nRot.z:F5}, {nRot.w:F5})");
                    }
                    else
                    {
                        sb.AppendLine("  VMD  : <no frames>");
                    }

                    sb.AppendLine();
                }

                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mmdd] DumpCurrentFrameSnapshot ERROR: {ex}");
            }
        }

        // 获取 Transform 在层级中的路径，便于在 TXT 中识别真实骨骼
        private string GetTransformPath(Transform t)
        {
            if (t == null) return "<null>";
            var names = new List<string>();
            while (t != null)
            {
                names.Add(t.name);
                t = t.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
    }