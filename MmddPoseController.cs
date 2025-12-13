using Il2CppInterop.Runtime.Attributes;
using System.Text;
using UnityEngine;

namespace CharaAnime
{
    public class MmddPoseController : MonoBehaviour
    {
        public MmddPoseController(IntPtr ptr) : base(ptr)
        {
        }

        public float VmdFrameRate = 30.0f;

        private VirtualBone boneNeck => dummyDict.ContainsKey("首") ? dummyDict["首"] : null;
        private VirtualBone boneHead => dummyDict.ContainsKey("頭") ? dummyDict["頭"] : null;
        private VirtualBone boneEyeL => dummyDict.ContainsKey("左目") ? dummyDict["左目"] : null;
        private VirtualBone boneEyeR => dummyDict.ContainsKey("右目") ? dummyDict["右目"] : null;

        public float LoopStart = 0f;
        public float LoopEnd = 0f;
        public bool EnableLoop = true;

        // ... [BoneAdjustment class and dictionary - NO CHANGE] ...
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

        static MmddPoseController()
        {
            InitializeDefaultBoneSettings();
        }

        public static void UpdateBoneRotationOffset(string boneName, Vector3 offset)
        { GetOrCreateAdjustment(boneName).RotOffsetEuler = offset; }

        public static void UpdateBoneAxisCorrection(string boneName, Vector3 axisEuler)
        { GetOrCreateAdjustment(boneName).SetAxisCorrection(axisEuler); }

        [Flags] private enum BoneFlag { None = 0, IsIK = 1 << 0, IsCenter = 1 << 1, RightSide = 1 << 2, Finger = 1 << 3 }

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
            { "センター", "cf_j_hips" }, { "グルーブ", "cf_j_hips" }, { "下半身", "cf_j_waist01" },
            { "上半身", "cf_j_spine01" }, { "上半身2", "cf_j_spine02" }, { "首", "cf_j_neck" }, { "頭", "cf_j_head" },
            { "左肩", "cf_j_shoulder_L" }, { "左腕", "cf_j_arm00_L" }, { "左ひじ", "cf_j_forearm01_L" }, { "左手首", "cf_j_hand_L" },
            { "右肩", "cf_j_shoulder_R" }, { "右腕", "cf_j_arm00_R" }, { "右ひじ", "cf_j_forearm01_R" }, { "右手首", "cf_j_hand_R" },
            { "左足", "cf_j_thigh00_L" }, { "左ひざ", "cf_j_leg01_L" }, { "左足首", "cf_j_foot_L" }, { "左つま先", "cf_j_toes_L" },
            { "右足", "cf_j_thigh00_R" }, { "右ひざ", "cf_j_leg01_R" }, { "右足首", "cf_j_foot_R" }, { "右つま先", "cf_j_toes_R" },
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
            { "センター", "全ての親" }, { "グルーブ", "センター" }, { "下半身", "センター" }, { "上半身", "センター" },
            { "上半身2", "上半身" }, { "首", "上半身2" }, { "頭", "首" },
            { "左肩", "上半身2" }, { "左腕", "左肩" }, { "左ひじ", "左腕" }, { "左手首", "左ひじ" },
            { "右肩", "上半身2" }, { "右腕", "右肩" }, { "右ひじ", "右腕" }, { "右手首", "右ひじ" },
            { "左足", "下半身" }, { "左ひざ", "左足" }, { "左足首", "左ひざ" },
            { "右足", "下半身" }, { "右ひざ", "右足" }, { "右足首", "右ひざ" },
            { "左足ＩＫ", "全ての親" }, { "右足ＩＫ", "全ての親" }, { "左つま先ＩＫ", "左足ＩＫ" }, { "右つま先ＩＫ", "右足ＩＫ" }
        };

        private static readonly Dictionary<string, string[]> TwistBoneMap = new Dictionary<string, string[]>
        {
            { "左腕捩", new[] { "左腕", "左手首" } }, { "右腕捩", new[] { "右腕", "右手首" } },
            { "左手捩", new[] { "左手首" } }, { "右手捩", new[] { "右手首" } },
            { "左捩", new[] { "左腕", "左手首" } }, { "右捩", new[] { "右腕", "右手首" } },
            { "左腕捻", new[] { "左腕", "左手首" } }, { "右腕捻", new[] { "右腕", "右手首" } }
        };

        private class MorphCache
        {
            public SkinnedMeshRenderer renderer;
            public int index;
            public List<VmdReader.VmdMorphFrame> frames;
            public int currentIndex;
            public float tweakScale = 1.0f;
            public string mmdName;
            public float calculatedWeight;
        }

        private class IKLink
        { public VirtualBone bone; public bool isKnee; public Vector3 minAngle; public Vector3 maxAngle; }

        private class IKChain
        { public string name; public VirtualBone target; public VirtualBone endEffector; public List<IKLink> links = new List<IKLink>(); public int iteration = 40; public float limitAngle = 2.0f; public bool active = true; }

        private class VirtualBone
        { public GameObject gameObject; public Transform transform; public Transform realTransform; public Quaternion bindOffset; public Vector3 bindPos; public List<VmdReader.VmdBoneFrame> frames; public int currentIndex; public BoneFlag flags; public string name; }

        private GameObject dummyRoot;
        private List<VirtualBone> activeBones = new List<VirtualBone>();
        private List<MorphCache> activeMorphs = new List<MorphCache>();
        private Dictionary<string, VirtualBone> dummyDict = new Dictionary<string, VirtualBone>();
        private List<IKChain> ikChains = new List<IKChain>();
        private Dictionary<string, List<VmdReader.VmdBoneFrame>> twistBoneFrames = new Dictionary<string, List<VmdReader.VmdBoneFrame>>();

        private GameObject targetObject;
        private bool isPlaying = false;
        private float currentTime = 0f;
        private float maxTime = 0f;
        private bool loop = true;
        private bool useExternalTime = false;

        public float positionScale = 0.085f;
        public float MorphScale = 0.7f;
        public bool CalibrateMode = false;

        public static string DebugBoneName = "右腕";
        public static string DebugText = "";

        public static MmddPoseController Install(GameObject container, GameObject target)
        {
            var controller = container.AddComponent<MmddPoseController>();
            controller.targetObject = target;
            return controller;
        }

        public void OnDestroy()
        { if (dummyRoot != null) Destroy(dummyRoot); }

        // ⚠️ REMOVED OnCameraPreCull registration. We will use LateUpdate instead.
        // private void OnEnable() { Camera.onPreCull += (Camera.CameraCallback)OnCameraPreCull; }
        // private void OnDisable() { Camera.onPreCull -= (Camera.CameraCallback)OnCameraPreCull; }
        // private void OnCameraPreCull(Camera cam) { if (isPlaying && activeMorphs.Count > 0) ApplyMorphAnimation(currentTime); }

        public void LateUpdate()
        {
            if (CalibrateMode) { UpdateVirtualSkeleton(-1f); return; }
            if (!isPlaying && !useExternalTime) return;

            if (!useExternalTime)
            {
                currentTime += Time.deltaTime * VmdFrameRate;
                ProcessLoopLogic();
            }

            UpdateVirtualSkeleton(currentTime);
            if (twistBoneFrames.Count > 0) UpdateTwistBones(currentTime);
            foreach (var chain in ikChains) if (chain.active) SolveCCD(chain);
            ApplyGazeAdjustment();
            ApplyToRealBones();

            // 🟢 [CHANGED] Force expression update in LateUpdate. This is more reliable if OnPreCull fails.
            if (activeMorphs.Count > 0) ApplyMorphAnimation(currentTime);

            useExternalTime = false;
        }

        public void SetTime(float frameTime)
        {
            useExternalTime = true;
            currentTime = frameTime;
            ProcessLoopLogic();
            UpdateVirtualSkeleton(currentTime);
            if (twistBoneFrames.Count > 0) UpdateTwistBones(currentTime);
            if (activeMorphs.Count > 0) ApplyMorphAnimation(currentTime);
        }

        public void Stop()
        {
            isPlaying = false;
            ResetMorphs();
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
            Console.WriteLine($"[Mmdd] Play VMD: {vmdData.ModelName}");

            PreprocessTwistBones(vmdData);
            BuildVirtualSkeleton(targetObject, vmdData);
            BuildIKChains();

            // 🟢 [Robust Fallback]
            VmdReader.VmdData dataForMorph = vmdData; // Default to main VMD
            if (morphData != null && morphData.MorphFrames != null && morphData.MorphFrames.Count > 0)
            {
                dataForMorph = morphData;
                Console.WriteLine($"[Mmdd] Using separate Morph VMD. Count: {morphData.MorphFrames.Count}");
            }
            else
            {
                Console.WriteLine($"[Mmdd] Using Motion VMD for Morphs. Count: {vmdData.MorphFrames.Count}");
            }

            float maxFrame = 0;
            if (vmdData.BoneFrames.Count > 0) maxFrame = vmdData.BoneFrames.Max(f => f.FrameNo);
            if (dataForMorph.MorphFrames.Count > 0) maxFrame = Math.Max(maxFrame, dataForMorph.MorphFrames.Max(f => f.FrameNo));
            this.maxTime = maxFrame;

            BuildMorphCache(targetObject, dataForMorph);

            this.currentTime = 0;
            this.useExternalTime = false;
            this.isPlaying = true;
        }

        // ... [PlayVpd, ApplyGazeAdjustment - NO CHANGE] ...
        [HideFromIl2Cpp]
        public void PlayVpd(VpdReader.VpdData vpdData)
        {
            if (targetObject == null || vpdData == null) return;
            Stop();
            Console.WriteLine($"[Mmdd] Apply VPD: {vpdData.FileName}");

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
            foreach (var chain in ikChains) if (chain.active) SolveCCD(chain);
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

            Console.WriteLine($"[Mmdd] Scanning Morphs for: {root.name}");

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

                                // 🟢 [Retained] Your fix for teeth
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

                                activeMorphs.Add(cache);

                                // 🔴 [Removed] break;
                                // This allows the tooth shape to be added multiple times
                                // (once for A, once for E, once for O, etc.)
                            }
                        }
                    }
                }
            }
            Console.WriteLine($"[Mmdd] Active Morphs Found: {activeMorphs.Count}");
        }

        private void ApplyMorphAnimation(float time)
        {
            if (activeMorphs.Count == 0) return;

            float currentSmileStrength = 0f;

            // 1. Calculate raw weights for all entries
            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;

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

                // Smile detection logic
                if (cache.calculatedWeight > 0.1f)
                {
                    string shapeName = cache.renderer.sharedMesh.GetBlendShapeName(cache.index).ToLower();
                    if ((shapeName.Contains("egao") || shapeName.Contains("smile")) &&
                        (shapeName.Contains("eye") || shapeName.Contains("eyelid")))
                    {
                        if (cache.calculatedWeight > currentSmileStrength)
                            currentSmileStrength = cache.calculatedWeight;
                    }
                    if (cache.mmdName == "笑い" || cache.mmdName == "smile" || cache.mmdName == "笑顔")
                    {
                        if (cache.calculatedWeight > currentSmileStrength)
                            currentSmileStrength = cache.calculatedWeight;
                    }
                }
            }

            float globalScale = (MorphScale > 0) ? MorphScale : 1.0f;

            // 2. Accumulate weights (MAX logic)
            // Key: Unique ID combining Renderer ID and BlendShape Index
            Dictionary<long, float> mergedWeights = new Dictionary<long, float>();

            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;

                float finalWeight = cache.calculatedWeight;
                string shapeName = cache.renderer.sharedMesh.GetBlendShapeName(cache.index).ToLower();

                bool isBlink = shapeName.Contains("def_cl") || shapeName.Contains("blink") ||
                             cache.mmdName == "まばたき" || cache.mmdName == "blink";

                if (isBlink)
                {
                    if (currentSmileStrength > 5f)
                    {
                        float suppression = Mathf.Clamp01((currentSmileStrength - 5f) / 20f);
                        finalWeight *= (1.0f - suppression);
                    }
                }

                finalWeight *= globalScale * cache.tweakScale;
                finalWeight = Mathf.Clamp(finalWeight, 0f, 100f);

                // Create a unique key for (Renderer + Index)
                // InstanceID is int (32), Index is int (32), pack into long (64)
                long key = ((long)cache.renderer.GetInstanceID() << 32) | (uint)cache.index;

                if (mergedWeights.ContainsKey(key))
                {
                    // If multiple VMD keys drive this shape (e.g. A and E driving Tooth), use the larger value.
                    if (finalWeight > mergedWeights[key]) mergedWeights[key] = finalWeight;
                }
                else
                {
                    mergedWeights[key] = finalWeight;
                }
            }

            // 3. Apply the final merged weights
            foreach (var cache in activeMorphs)
            {
                if (cache.renderer == null) continue;
                long key = ((long)cache.renderer.GetInstanceID() << 32) | (uint)cache.index;

                // Only apply if it exists in the dictionary
                // (Optimization: remove from dict after applying so we don't set the same value twice per frame)
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
        private void PreprocessTwistBones(VmdReader.VmdData vmdData)
        {
            twistBoneFrames.Clear();
            var boneFrameGroups = vmdData.BoneFrames.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var twistBoneName in TwistBoneMap.Keys)
            {
                if (boneFrameGroups.TryGetValue(twistBoneName, out var frames))
                {
                    twistBoneFrames[twistBoneName] = frames;
                    vmdData.BoneFrames.RemoveAll(f => f.Name == twistBoneName);
                }
            }
        }

        private void UpdateTwistBones(float time)
        {
            foreach (var kvp in twistBoneFrames)
            {
                string twistBoneName = kvp.Key;
                var frames = kvp.Value;
                if (frames.Count == 0) continue;
                int i = 0;
                while (i < frames.Count - 1 && time >= frames[i + 1].FrameNo) i++;
                var prev = frames[i];
                var next = (i < frames.Count - 1) ? frames[i + 1] : prev;
                float t = 0f;
                float duration = next.FrameNo - prev.FrameNo;
                if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;
                t = t * t * (3f - 2f * t);
                Quaternion rotA = new Quaternion(-prev.Rotation.x, prev.Rotation.y, -prev.Rotation.z, prev.Rotation.w).normalized;
                Quaternion rotB = new Quaternion(-next.Rotation.x, next.Rotation.y, -next.Rotation.z, next.Rotation.w).normalized;
                if (Quaternion.Dot(rotA, rotB) < 0) rotB = new Quaternion(-rotB.x, -rotB.y, -rotB.z, -rotB.w);
                Quaternion finalTwistRot = Quaternion.Slerp(rotA, rotB, t);
                ApplyTwistBoneRotation(twistBoneName, finalTwistRot, time);
            }
        }

        private void ApplyTwistBoneRotation(string twistBoneName, Quaternion twistRotation, float time)
        {
            if (!TwistBoneMap.TryGetValue(twistBoneName, out var targetBones)) return;
            foreach (var targetBoneName in targetBones)
            {
                if (dummyDict.TryGetValue(targetBoneName, out var targetBone))
                {
                    float weight = (twistBoneName.Contains("腕捩") && targetBoneName.Contains("腕")) ? 0.3f : 0.7f;
                    if (twistBoneName.Contains("手捩")) weight = 1.0f;
                    if (twistRotation.eulerAngles.magnitude < 0.1f) weight = 0f;
                    Quaternion appliedRotation = Quaternion.Slerp(Quaternion.identity, twistRotation, weight);
                    targetBone.transform.localRotation = appliedRotation * targetBone.transform.localRotation;
                }
            }
        }

        private void BuildVirtualSkeleton(GameObject target, VmdReader.VmdData vmdData)
        {
            if (dummyRoot != null) Destroy(dummyRoot);
            activeBones.Clear();
            dummyDict.Clear();
            dummyRoot = new GameObject("Mmdd_Dummy_Root");
            dummyRoot.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);

            var realBoneMap = new Dictionary<string, Transform>();
            MapBonesRecursive(target.transform, realBoneMap);

            var vmdGrouped = vmdData.BoneFrames.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.ToList());

            string[] essentialIK = { "左足ＩＫ", "右足ＩＫ", "左つま先ＩＫ", "右つま先ＩＫ" };
            foreach (var ikName in essentialIK) if (!vmdGrouped.ContainsKey(ikName)) vmdGrouped[ikName] = new List<VmdReader.VmdBoneFrame>();

            foreach (var kvp in vmdGrouped)
            {
                string mmdName = kvp.Key;
                var frames = kvp.Value;
                frames.Sort((a, b) => a.FrameNo.CompareTo(b.FrameNo));

                GameObject go = new GameObject(mmdName);
                VirtualBone vBone = new VirtualBone { gameObject = go, transform = go.transform, frames = frames, currentIndex = 0, name = mmdName, flags = BoneFlag.None };

                if (mmdName.Contains("ＩＫ")) vBone.flags |= BoneFlag.IsIK;
                if (mmdName == "センター" || mmdName == "Hips" || mmdName == "全ての親") vBone.flags |= BoneFlag.IsCenter;
                if (mmdName.Contains("右")) vBone.flags |= BoneFlag.RightSide;
                if (mmdName.Contains("指")) vBone.flags |= BoneFlag.Finger;

                string unityName = null;
                bool isGroove = (mmdName == "グルーブ" || mmdName == "ｸﾞﾙｰﾌﾞ" || mmdName == "Groove");
                bool isCenter = (mmdName == "センター" || mmdName == "Center" || mmdName == "Hips");

                if (MmddGui.Cfg_EnableGrooveFix)
                {
                    if (isGroove) { vBone.flags |= BoneFlag.IsCenter; unityName = "cf_j_hips"; }
                    else if (isCenter) { vBone.flags |= BoneFlag.IsCenter; unityName = null; }
                    else { if (MmdToUnityMap.ContainsKey(mmdName)) unityName = MmdToUnityMap[mmdName]; }
                }
                else
                {
                    if (MmdToUnityMap.ContainsKey(mmdName)) unityName = MmdToUnityMap[mmdName];
                }

                if (!string.IsNullOrEmpty(unityName) && realBoneMap.TryGetValue(unityName, out Transform realT))
                {
                    vBone.realTransform = realT;
                    vBone.bindOffset = realT.localRotation;
                    vBone.bindPos = realT.localPosition;
                }

                dummyDict[mmdName] = vBone;
                activeBones.Add(vBone);
            }

            foreach (var kvp in dummyDict)
            {
                var child = kvp.Value;
                if (MmdHierarchy.TryGetValue(child.name, out string parentName) && dummyDict.TryGetValue(parentName, out VirtualBone parent))
                    child.transform.SetParent(parent.transform);
                else
                    child.transform.SetParent(dummyRoot.transform);

                child.transform.localRotation = Quaternion.identity;
                child.transform.localPosition = Vector3.zero;
            }
        }

        private void BuildIKChains()
        {
            ikChains.Clear();
            AddIKChain("左足ＩＫ", "左足首", new[] { "左ひざ", "左足" }, true);
            AddIKChain("右足ＩＫ", "右足首", new[] { "右ひざ", "右足" }, true);
            AddIKChain("左手ＩＫ", "左手首", new[] { "左ひじ", "左腕" }, false);
            AddIKChain("右手ＩＫ", "右手首", new[] { "右ひじ", "右腕" }, false);
        }

        private void AddIKChain(string targetName, string effectorName, string[] linkNames, bool isLeg)
        {
            if (!dummyDict.ContainsKey(targetName) || !dummyDict.ContainsKey(effectorName)) return;
            IKChain chain = new IKChain { name = targetName, target = dummyDict[targetName], endEffector = dummyDict[effectorName], iteration = isLeg ? 40 : 20 };
            foreach (var name in linkNames)
            {
                if (dummyDict.ContainsKey(name))
                {
                    IKLink link = new IKLink { bone = dummyDict[name] };
                    if (isLeg && name.Contains("ひざ")) { link.isKnee = true; link.minAngle = new Vector3(-180, 0, 0); link.maxAngle = new Vector3(-0.5f, 0, 0); }
                    chain.links.Add(link);
                }
            }
            ikChains.Add(chain);
        }

        private void UpdateVirtualSkeleton(float time)
        {
            foreach (var bone in activeBones)
            {
                if (time < 0) { bone.transform.localRotation = Quaternion.identity; continue; }
                if (bone.frames.Count == 0) continue;

                int i = bone.currentIndex;
                var frames = bone.frames;
                if (i >= frames.Count - 1 || frames[i].FrameNo > time) i = 0;
                while (i < frames.Count - 1 && time >= frames[i + 1].FrameNo) i++;
                bone.currentIndex = i;

                var prev = frames[i];
                var next = (i < frames.Count - 1) ? frames[i + 1] : prev;

                float t = 0f;
                float duration = next.FrameNo - prev.FrameNo;
                if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;
                t = t * t * (3f - 2f * t);

                float ax = prev.Rotation.x, ay = prev.Rotation.y, az = prev.Rotation.z, aw = prev.Rotation.w;
                float bx = next.Rotation.x, by = next.Rotation.y, bz = next.Rotation.z, bw = next.Rotation.w;

                bool isRightFinger = (bone.flags & BoneFlag.RightSide) != 0 && (bone.flags & BoneFlag.Finger) != 0;
                Quaternion rotA = new Quaternion(-ax, isRightFinger ? -ay : ay, isRightFinger ? az : -az, aw);
                Quaternion rotB = new Quaternion(-bx, isRightFinger ? -by : by, isRightFinger ? bz : -bz, bw);
                Quaternion mmdRot = Quaternion.Slerp(rotA, rotB, t);

                if (BoneSettings.TryGetValue(bone.name, out BoneAdjustment adj))
                {
                    if (adj.AxisCorrectionEuler != Vector3.zero) mmdRot = adj.AxisCorrection * mmdRot * Quaternion.Inverse(adj.AxisCorrection);
                    if (adj.RotOffsetEuler != Vector3.zero) mmdRot = mmdRot * Quaternion.Euler(adj.RotOffsetEuler);
                }

                bone.transform.localRotation = mmdRot;

                if ((bone.flags & (BoneFlag.IsCenter | BoneFlag.IsIK)) != 0)
                {
                    Vector3 posA = new Vector3(-prev.Position.x, prev.Position.y, -prev.Position.z);
                    Vector3 posB = new Vector3(-next.Position.x, next.Position.y, -next.Position.z);
                    Vector3 finalPos = Vector3.Lerp(posA, posB, t);
                    //if ((bone.flags & BoneFlag.IsIK) != 0) finalPos.y -= 0.08f;
                    bone.transform.localPosition = finalPos * positionScale;
                }
            }
        }

        private void SolveCCD(IKChain chain)
        {
            Vector3 targetPos = chain.target.transform.position;
            for (int i = 0; i < chain.iteration; i++)
            {
                foreach (var link in chain.links)
                {
                    Vector3 endPos = chain.endEffector.transform.position;
                    Vector3 toTarget = targetPos - link.bone.transform.position;
                    Vector3 toEnd = endPos - link.bone.transform.position;
                    Vector3 toTargetLocal = link.bone.transform.InverseTransformDirection(toTarget);
                    Vector3 toEndLocal = link.bone.transform.InverseTransformDirection(toEnd);
                    Quaternion rotation = Quaternion.FromToRotation(toEndLocal, toTargetLocal);
                    if (link.isKnee)
                    {
                        Vector3 euler = rotation.eulerAngles;
                        if (euler.x > 180) euler.x -= 360;
                        float clampedX = Mathf.Clamp(euler.x, -chain.limitAngle * Mathf.Rad2Deg, 0);
                        rotation = Quaternion.Euler(clampedX, 0, 0);
                    }
                    link.bone.transform.localRotation = rotation * link.bone.transform.localRotation;
                }
                if ((chain.endEffector.transform.position - targetPos).sqrMagnitude < 0.0001f) break;
            }
        }

        private void ApplyToRealBones()
        {
            VirtualBone vCenter = null;
            if (MmddGui.Cfg_EnableGrooveFix)
            {
                if (dummyDict.ContainsKey("センター")) vCenter = dummyDict["センター"];
                else if (dummyDict.ContainsKey("Center")) vCenter = dummyDict["Center"];
            }

            foreach (var bone in activeBones)
            {
                if (bone.realTransform != null)
                {
                    bool isMerging = MmddGui.Cfg_EnableGrooveFix && vCenter != null && (bone.name == "グルーブ" || bone.name == "Groove");

                    if (isMerging)
                    {
                        Quaternion finalRot = vCenter.transform.localRotation * bone.transform.localRotation;
                        bone.realTransform.localRotation = bone.bindOffset * finalRot;
                        Vector3 centerPos = vCenter.transform.localPosition;
                        Vector3 groovePos = bone.transform.localPosition;
                        Vector3 finalPos = bone.bindPos + centerPos + groovePos + GlobalPositionOffset;
                        bone.realTransform.localPosition = finalPos;
                    }
                    else
                    {
                        bone.realTransform.localRotation = bone.bindOffset * bone.transform.localRotation;
                        if ((bone.flags & (BoneFlag.IsCenter | BoneFlag.IsIK)) != 0)
                        {
                            Vector3 finalPos = bone.bindPos + bone.transform.localPosition + GlobalPositionOffset;
                            bone.realTransform.localPosition = finalPos;
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
    }
}