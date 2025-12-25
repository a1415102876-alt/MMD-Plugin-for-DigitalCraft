using BepInEx;
using UnityEngine;

namespace CharaAnime
{
    public class CharaAnimeMgr : MonoBehaviour
    {
        public CharaAnimeMgr(IntPtr ptr) : base(ptr)
        {
        }

        public static CharaAnimeMgr Instance;
        public Dictionary<GameObject, MmddPoseController> ociPoseCtrlDic;
        private MmddAudioPlayer audioPlayer;
        private MmddCameraController camCtrl;

        private VmdReader.VmdData cachedCameraVmdData = null;

        private float maxTime = 0f;
        private float masterTime = 0f;
        public float BgmOffset = 0f;

        private bool isPaused = false;
        private float pauseTime = 0f;
        private bool wasPlayingBeforePause = false;

        public float MaxFrame { get; private set; } = 0f;
        public bool IsPaused => isPaused;

        public float CurrentFrame => isPaused ? pauseTime :
            (audioPlayer != null && audioPlayer.IsPlaying ?
                (audioPlayer.CurrentTime + BgmOffset) * MmddGui.Cfg_VmdFrameRate :
                (masterTime + BgmOffset) * MmddGui.Cfg_VmdFrameRate);

        public Dictionary<string, string> CharacterVmdAssignments = new Dictionary<string, string>();
        public Dictionary<string, string> CharacterMorphAssignments = new Dictionary<string, string>();

        public List<GameObject> DetectedCharacters = new List<GameObject>();

        public void ScanCharactersInScene()
        {
            DetectedCharacters.Clear();
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in allTransforms)
            {
                GameObject go = t.gameObject;
                if (!go.activeInHierarchy) continue;
                if (go.name.Contains("chaF") || go.name.Contains("chaM") || go.name == "00")
                {
                    if (go.GetComponentsInChildren<Transform>(true).Length < 50) continue;
                    DetectedCharacters.Add(go);
                }
            }
            DetectedCharacters.Sort((a, b) => a.name.CompareTo(b.name));
        }

        public void Awake()
        {
            if (Instance == null) Instance = this;
            this.ociPoseCtrlDic = new Dictionary<GameObject, MmddPoseController>();
            if (audioPlayer == null) audioPlayer = MmddAudioPlayer.Install(this.gameObject);
        }

        public void Start()
        { Console.WriteLine("[CharaAnimeMgr] Ready! Use F8 to open GUI."); }

        public void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    if (ociPoseCtrlDic.Count > 0 || (camCtrl != null && camCtrl.IsPlaying)) StopAnimation();
                    ApplyMotionToScene();
                }

                if (ociPoseCtrlDic.Count == 0 && (camCtrl == null || !camCtrl.IsPlaying)) return;

                if (Input.GetKeyDown(KeyCode.F10)) StopAnimation();

                if (isPaused)
                {
                    SyncFrame(pauseTime);
                    return;
                }

                float loopStartFrame = 0f;
                float loopEndFrame = 0f;
                bool enableLoop = false;

                var mainCtrl = ociPoseCtrlDic.Values.FirstOrDefault();
                if (mainCtrl != null)
                {
                    loopStartFrame = mainCtrl.LoopStart;
                    loopEndFrame = mainCtrl.LoopEnd;
                    enableLoop = mainCtrl.EnableLoop;
                }

                float currentVmdFrame = 0f;
                float fps = MmddGui.Cfg_VmdFrameRate;

                if (audioPlayer != null && audioPlayer.IsPlaying)
                {
                    float adjustedTime = audioPlayer.CurrentTime + BgmOffset;
                    currentVmdFrame = adjustedTime * fps;
                }
                else
                {
                    masterTime += Time.deltaTime;
                    currentVmdFrame = (masterTime + BgmOffset) * fps;
                }

                if (currentVmdFrame < 0) currentVmdFrame = 0;

                float checkEndFrame = (loopEndFrame > 0.1f) ? loopEndFrame : MaxFrame;
                if (checkEndFrame <= loopStartFrame) checkEndFrame = MaxFrame;

                if (currentVmdFrame >= checkEndFrame)
                {
                    if (enableLoop)
                    {
                        currentVmdFrame = loopStartFrame;
                        float targetTimeSec = loopStartFrame / fps;
                        if (audioPlayer != null && audioPlayer.IsPlaying)
                        {
                            float audioTime = targetTimeSec - BgmOffset;
                            if (audioTime < 0) audioTime = 0;
                            audioPlayer.Time = audioTime;
                        }
                        masterTime = targetTimeSec - BgmOffset;
                        ResetAllFrameIndexes();
                    }
                    else
                    {
                        StopAnimation();
                        return;
                    }
                }
                SyncFrame(currentVmdFrame);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CharaAnimeMgr Update Error]: {e}");
            }
        }

        private void SyncFrame(float frame)
        {
            foreach (var kvp in ociPoseCtrlDic) if (kvp.Value != null) kvp.Value.SetTime(frame);
            if (camCtrl != null && camCtrl.IsPlaying) camCtrl.SetTime(frame);
        }

        private void ResetAllFrameIndexes()
        {
            foreach (var kvp in ociPoseCtrlDic) if (kvp.Value != null) kvp.Value.ResetFrameIndexes();
        }

        public void TogglePause()
        {
            if (ociPoseCtrlDic.Count == 0) return;
            isPaused = !isPaused;

            if (isPaused)
            {
                if (audioPlayer != null && audioPlayer.IsPlaying)
                {
                    pauseTime = (audioPlayer.CurrentTime + BgmOffset) * MmddGui.Cfg_VmdFrameRate;
                    wasPlayingBeforePause = true;
                    audioPlayer.Pause();
                }
                else
                {
                    pauseTime = (masterTime + BgmOffset) * MmddGui.Cfg_VmdFrameRate;
                    wasPlayingBeforePause = false;
                }
                SyncFrame(pauseTime);
            }
            else
            {
                float currentVmdTimeSec = pauseTime / MmddGui.Cfg_VmdFrameRate;
                masterTime = currentVmdTimeSec - BgmOffset;

                if (audioPlayer != null && wasPlayingBeforePause)
                {
                    float resumeTime = masterTime;
                    if (resumeTime < 0) resumeTime = 0;
                    audioPlayer.Time = resumeTime;
                    audioPlayer.Resume();
                }
            }
        }

        public void StepFrame(float amount)
        {
            if (ociPoseCtrlDic.Count == 0) return;
            if (!isPaused) TogglePause();
            pauseTime += amount;
            if (pauseTime < 0) pauseTime = 0;
            if (MaxFrame > 0 && pauseTime > MaxFrame) pauseTime = MaxFrame;
            SyncFrame(pauseTime);
        }

        public void SeekToFrame(float frame)
        {
            if (ociPoseCtrlDic.Count == 0) return;
            if (!isPaused) TogglePause();
            pauseTime = frame;
            if (pauseTime < 0) pauseTime = 0;
            if (MaxFrame > 0 && pauseTime > MaxFrame) pauseTime = MaxFrame;
            SyncFrame(pauseTime);
        }

        public void ApplyMotionToScene()
        {
            StopAnimation();
            cachedCameraVmdData = null;

            string VMD_DIR = Path.Combine(Paths.ConfigPath, "CharaAnime_VMD");
            string globalCameraPath = Path.Combine(VMD_DIR, MmddGui.SelectedCameraFile);
            string globalMorphFileName = MmddGui.SelectedMorphFile;

            ScanCharactersInScene();

            int count = 0;
            GameObject mainCharacter = null;
            float maxDuration = 0f;

            Dictionary<string, VmdReader.VmdData> vmdDataCache = new Dictionary<string, VmdReader.VmdData>();

            foreach (var go in DetectedCharacters)
            {
                if (go == null) continue;

                string motionFileName = CharacterVmdAssignments.ContainsKey(go.name) ? CharacterVmdAssignments[go.name] : MmddGui.SelectedMotionFile;

                string morphFileName = "";
                if (CharacterMorphAssignments.ContainsKey(go.name))
                    morphFileName = CharacterMorphAssignments[go.name];
                else
                    morphFileName = globalMorphFileName;

                VmdReader.VmdData motionVmdData = GetOrLoadVmd(motionFileName, VMD_DIR, vmdDataCache);
                if (motionVmdData == null) continue;

                // 🟢 [修复核心] 增加 None 的判断，确保不传错文件
                VmdReader.VmdData morphVmdData = null;
                if (!string.IsNullOrEmpty(morphFileName) && morphFileName != "None")
                {
                    morphVmdData = GetOrLoadVmd(morphFileName, VMD_DIR, vmdDataCache);
                    // 如果加载的文件里根本没表情，也视为 null
                    if (morphVmdData != null && morphVmdData.MorphFrames.Count == 0) morphVmdData = null;
                }

                DisableInterference(go);

                MmddPoseController ctrl = GetPoseController(go);
                if (ctrl != null)
                {
                    ctrl.VmdFrameRate = MmddGui.Cfg_VmdFrameRate;
                    ctrl.Play(motionVmdData, morphVmdData);
                    count++;

                    float localMax = 0f;
                    if (motionVmdData.BoneFrames.Count > 0) localMax = motionVmdData.BoneFrames.Max(f => f.FrameNo);
                    if (localMax > maxDuration) maxDuration = localMax;

                    if (morphVmdData != null && morphVmdData.MorphFrames.Count > 0)
                        maxDuration = Mathf.Max(maxDuration, morphVmdData.MorphFrames.Max(f => f.FrameNo));

                    if (mainCharacter == null) mainCharacter = go;
                }
            }

            Console.WriteLine($"[CharaAnimeMgr] Playing! Applied unique motions to {count} characters.");

            VmdReader.VmdData cameraVmdData = null;
            if (MmddGui.SelectedCameraFile != MmddGui.SelectedMotionFile)
            {
                cameraVmdData = GetOrLoadVmd(MmddGui.SelectedCameraFile, VMD_DIR, vmdDataCache);
                cachedCameraVmdData = cameraVmdData;
            }

            if (cameraVmdData == null)
            {
                cameraVmdData = GetOrLoadVmd(MmddGui.SelectedMotionFile, VMD_DIR, vmdDataCache);
                cachedCameraVmdData = cameraVmdData;
            }

            if (cameraVmdData != null && cameraVmdData.CameraFrames.Count > 0)
                maxDuration = Mathf.Max(maxDuration, cameraVmdData.CameraFrames.Max(f => f.FrameNo));

            this.MaxFrame = maxDuration;

            if (camCtrl == null) camCtrl = MmddCameraController.Install(this.gameObject);
            if (camCtrl != null && cameraVmdData != null && cameraVmdData.CameraFrames.Count > 0)
            {
                camCtrl.UseExternalTime = true;
                camCtrl.VmdFrameRate = MmddGui.Cfg_VmdFrameRate;
                camCtrl.FollowTarget = (mainCharacter != null) ? mainCharacter.transform : null;
                camCtrl.Play(cameraVmdData);
                Console.WriteLine($"[Mmdd] Camera Applied. Target: {(mainCharacter != null ? mainCharacter.name : "None")}");
            }

            string globalWavPath = Path.Combine(VMD_DIR, MmddGui.SelectedAudioFile);
            if (File.Exists(globalWavPath))
            {
                if (audioPlayer == null) audioPlayer = MmddAudioPlayer.Install(gameObject);
                audioPlayer.Play(globalWavPath);
            }

            isPaused = false;
            pauseTime = 0f;
            masterTime = 0.001f;
        }

        private VmdReader.VmdData GetOrLoadVmd(string fileName, string vmdDir, Dictionary<string, VmdReader.VmdData> cache)
        {
            if (string.IsNullOrEmpty(fileName) || fileName == "No VMDs Found") return null;
            if (cache.TryGetValue(fileName, out var data)) return data;

            string path = Path.Combine(vmdDir, fileName);
            if (File.Exists(path))
            {
                var vmdData = VmdReader.LoadVmd(path);
                if (vmdData != null)
                {
                    cache[fileName] = vmdData;
                    return vmdData;
                }
            }
            return null;
        }

        public void StopAnimation()
        {
            if (audioPlayer != null) audioPlayer.Stop();
            foreach (var kvp in ociPoseCtrlDic)
            {
                GameObject go = kvp.Key;
                MmddPoseController ctrl = kvp.Value;
                if (ctrl != null) { ctrl.Stop(); Destroy(ctrl); }
                if (go != null) EnableInterference(go);
            }
            ociPoseCtrlDic.Clear();
            isPaused = false;
            pauseTime = 0f;
            masterTime = 0f;
            MaxFrame = 0f;
            Console.WriteLine("[CharaAnimeMgr] Stopped and Reset.");
            if (camCtrl != null) camCtrl.RestoreControl();
        }

        private void DisableInterference(GameObject go)
        {
            foreach (var anim in go.GetComponentsInChildren<Animator>(true)) if (anim.enabled) anim.enabled = false;
            foreach (var comp in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string name = comp.GetIl2CppType().Name;
                if (name.Contains("BoneCollider") || name.Contains("BoneSpring") || name.Contains("BoneCloth")) continue;
                if (name.Contains("MmddPoseController") || name.Contains("MmddAudioPlayer")) continue;
                if (name.Contains("RenderQueue") || name.Contains("ChaPreparation")) continue;

                bool isEnemy = name.Contains("uLipSync") || name == "FaceBlendShape" || name.Contains("FaceController") ||
                    name.Contains("Look") || name.Contains("EyeLook") || name.Contains("Hsi") || name.Contains("FrameCorrect") || name.Contains("IKCorrect");

                if (isEnemy && comp.enabled) comp.enabled = false;
            }
        }

        private void EnableInterference(GameObject go)
        {
            foreach (var anim in go.GetComponentsInChildren<Animator>(true)) anim.enabled = true;
            foreach (var comp in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string name = comp.GetIl2CppType().Name;
                if (name.Contains("IK") || name.Contains("LookAt")) comp.enabled = true;
            }
        }

        public MmddPoseController GetPoseController(GameObject target)
        {
            if (target == null) return null;
            if (this.ociPoseCtrlDic.TryGetValue(target, out var ctrl)) if (ctrl != null) return ctrl;
            var newCtrl = MmddPoseController.Install(target, target);
            RegistPoseController(target, newCtrl);
            return newCtrl;
        }

        public void RegistPoseController(GameObject target, MmddPoseController poseCtrl)
        {
            if (target != null) this.ociPoseCtrlDic[target] = poseCtrl;
        }

        public void ApplyVpdToScene()
        {
            string vpdPath = Path.Combine(Paths.GameRootPath, "test.vpd");
            if (!File.Exists(vpdPath)) return;
            var vpdData = VpdReader.LoadVpd(vpdPath);
            if (vpdData == null) return;
            StopAnimation();

            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in allTransforms)
            {
                GameObject go = t.gameObject;
                if (!go.activeInHierarchy) continue;
                if (go.name.Contains("chaF") || go.name.Contains("chaM") || go.name == "00")
                {
                    if (go.GetComponentsInChildren<Transform>(true).Length < 50) continue;
                    DisableInterference(go);
                    MmddPoseController ctrl = GetPoseController(go);
                    if (ctrl != null) ctrl.PlayVpd(vpdData);
                }
            }
        }

        public void DumpAllBlendShapes()
        {
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            GameObject targetGO = null;
            foreach (var t in allTransforms)
            {
                if ((t.gameObject.name.Contains("chaF") || t.gameObject.name.Contains("chaM")) && t.gameObject.activeInHierarchy)
                {
                    targetGO = t.gameObject;
                    break;
                }
            }
            if (targetGO == null) return;
            List<string> lines = new List<string> { $"BlendShapes Dump for {targetGO.name}" };
            var renderers = targetGO.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in renderers)
            {
                if (r.sharedMesh == null) continue;
                lines.Add($"Renderer: {r.name}");
                for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) lines.Add($"  [{i}] {r.sharedMesh.GetBlendShapeName(i)}");
            }
            try { File.WriteAllLines(Path.Combine(Paths.ConfigPath, "BlendShape_Dump.txt"), lines); } catch { }
        }
    }
}