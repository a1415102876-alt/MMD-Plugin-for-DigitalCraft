using BepInEx;
using UnityEngine;

namespace CharaAnime
{
    public class MmddGui : MonoBehaviour
    {
        public MmddGui(IntPtr ptr) : base(ptr)
        {
        }

        private static readonly string VMD_DIR = Path.Combine(Paths.ConfigPath, "CharaAnime_VMD");
        private static readonly string PRESET_DIR = Path.Combine(Paths.ConfigPath, "CharaAnime_Presets");

        public static List<string> AvailableVmdFiles = new List<string>();
        public static List<string> AvailableAudioFiles = new List<string>();

        public static string SelectedMotionFile = "test.vmd";
        public static string SelectedCameraFile = "camera.vmd";
        public static string SelectedAudioFile = "audio.wav";

        public static string SelectedMorphFile = "None";

        private Vector2 loadScrollPositionMotion = Vector2.zero;
        private Vector2 loadScrollPositionCamera = Vector2.zero;
        private Vector2 loadScrollPositionAudio = Vector2.zero;
        private Vector2 loadScrollPositionMorph = Vector2.zero;

        private Vector2 loadWindowScrollPos = Vector2.zero;

        private Vector2 settingsScrollPos = Vector2.zero;
        private string selectedCharInGui = "";
        private Vector2 charListScroll = Vector2.zero;
        public static bool Cfg_EnableFovMod = false;

        private bool showMain = false;
        private bool showSettings = false;
        private bool showBones = false;
        private bool showLoadWindow = false;

        private Rect mainRect = new Rect(20, 20, 400, 500);
        private Rect settingsRect = new Rect(430, 20, 400, 650);
        private Rect boneRect = new Rect(840, 20, 450, 800);
        private Rect loadRect = new Rect(20, 380, 450, 580);

        private GUI.WindowFunction drawMainDelegate;
        private GUI.WindowFunction drawSettingsDelegate;
        private GUI.WindowFunction drawBonesDelegate;
        private GUI.WindowFunction drawLoadDelegate;

        private Vector2 scrollPosition = Vector2.zero;
        private Vector2 presetScrollPos = Vector2.zero;
        private List<string> presetFileList = new List<string>();

        public static float Cfg_MorphScale = 1f;
        public static float Cfg_PosScale = 0.085f;
        public static float Cfg_VmdFrameRate = 30.0f;
        public static Vector3 Cfg_IKHeight = Vector3.zero;

        private static float s_Cfg_CameraScale = 0.085f;
        public static float Cfg_CameraScale { get => s_Cfg_CameraScale; set => s_Cfg_CameraScale = value; }
        public static float Cfg_CameraOffsetY = 0.0f;

        public static bool Cfg_EnableGrooveFix = false;
        public static bool Cfg_EnableGaze = false;
        public static float Cfg_GazeWeight = 1.0f;
        public static bool Cfg_EnableShortcuts = true;

        private string[] presetBones = new string[] {
            "左親指１", "左親指２", "左親指３", "左親指０",
            "右親指１", "右親指２", "右親指３", "右親指０",
            "左人指１", "左中指１", "左薬指１", "左小指１",
            "右人指１", "右中指１", "右薬指１", "右小指１",
            "左肩", "左腕", "左ひじ", "左手首",
            "右肩", "右腕", "右ひじ", "右手首",
            "左足", "左ひざ", "左足首",
            "右足", "右ひざ", "右足首",
            "センター", "上半身", "首", "頭",
            "左目", "右目"
        };

        private bool[] presetSelections;

        public static MmddGui Install(GameObject container)
        {
            return container.AddComponent<MmddGui>();
        }

        private void Awake()
        {
            drawMainDelegate = (GUI.WindowFunction)(Action<int>)DrawMainWindow;
            drawSettingsDelegate = (GUI.WindowFunction)(Action<int>)DrawSettingsWindow;
            drawBonesDelegate = (GUI.WindowFunction)(Action<int>)DrawBonesWindow;
            drawLoadDelegate = (GUI.WindowFunction)(Action<int>)DrawLoadWindow;

            presetSelections = new bool[presetBones.Length];
            RefreshPresetList();
            RefreshFileLists();

            TryLoadLatestPreset();
        }

        private void TryLoadLatestPreset()
        {
            try
            {
                if (!Directory.Exists(PRESET_DIR)) return;

                // 获取目录下所有 .txt 文件
                var dirInfo = new DirectoryInfo(PRESET_DIR);
                var files = dirInfo.GetFiles("*.txt");

                if (files.Length > 0)
                {
                    // 按修改时间降序排序 (最新的在第一个)，取第一个
                    var newestFile = files.OrderByDescending(f => f.LastWriteTime).First();

                    Console.WriteLine($"[MmddGui] Auto-loading latest preset: {newestFile.Name}");
                    LoadPreset(newestFile.FullName);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[MmddGui] Failed to auto-load preset: {e.Message}");
            }
        }

        private void RefreshFileLists()
        {
            AvailableVmdFiles.Clear();
            AvailableAudioFiles.Clear();

            if (!Directory.Exists(VMD_DIR)) { try { Directory.CreateDirectory(VMD_DIR); } catch { } }
            else { AvailableVmdFiles.AddRange(Directory.GetFiles(VMD_DIR, "*.vmd").Select(Path.GetFileName)); }
            if (AvailableVmdFiles.Count == 0) AvailableVmdFiles.Add("No VMDs Found");

            if (Directory.Exists(VMD_DIR))
            {
                var audios = Directory.GetFiles(VMD_DIR, "*.wav").Select(Path.GetFileName).ToList();
                audios.AddRange(Directory.GetFiles(VMD_DIR, "*.mp3").Select(Path.GetFileName));
                AvailableAudioFiles.AddRange(audios);
            }
            if (AvailableAudioFiles.Count == 0) AvailableAudioFiles.Add("No Audio Found");

            if (CharaAnimeMgr.Instance != null) CharaAnimeMgr.Instance.ScanCharactersInScene();
        }

        private void SyncSelections(MmddPoseController ctrl)
        {
            if (ctrl == null) return;
            for (int i = 0; i < presetBones.Length; i++)
                presetSelections[i] = MmddPoseController.BoneSettings.ContainsKey(presetBones[i]);
        }

        public void Update()
        {
            // 1. F8 切换窗口
            if (Input.GetKeyDown(KeyCode.F8))
            {
                showMain = !showMain;
                if (showMain)
                {
                    // 这里的 SyncSelections 只需要随便找一个控制器来同步骨骼预设即可 (因为骨骼预设是 static 的)
                    var anyCtrl = UnityEngine.Object.FindObjectOfType<MmddPoseController>();
                    SyncSelections(anyCtrl);
                    RefreshPresetList();
                    RefreshFileLists();
                }
            }

            // 2. 快捷键
            if (Cfg_EnableShortcuts && CharaAnimeMgr.Instance != null)
            {
                if (Input.GetKeyDown(KeyCode.Space)) CharaAnimeMgr.Instance.TogglePause();
                if (Input.GetKeyDown(KeyCode.L))
                {
                    // 快捷键切换 Loop，遍历所有角色
                    foreach (var ctrl in CharaAnimeMgr.Instance.ociPoseCtrlDic.Values)
                    {
                        if (ctrl != null) ctrl.EnableLoop = !ctrl.EnableLoop;
                    }
                }
            }

            // 3. 鼠标解锁逻辑
            if (showMain || showSettings || showBones || showLoadWindow)
            {
                Vector2 mousePos = Input.mousePosition;
                mousePos.y = Screen.height - mousePos.y;
                bool inMain = mainRect.Contains(mousePos);
                bool inSettings = showSettings && settingsRect.Contains(mousePos);
                bool inBones = showBones && boneRect.Contains(mousePos);
                bool inLoad = showLoadWindow && loadRect.Contains(mousePos);
                if (inMain || inSettings || inBones || inLoad) { Input.ResetInputAxes(); Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
            }

            // 4. 同步全局 IK 高度 (Static)
            if (MmddPoseController.GlobalPositionOffset != Cfg_IKHeight) MmddPoseController.GlobalPositionOffset = Cfg_IKHeight;
            if (MmddPoseController.GlobalPositionOffset != Vector3.zero && Cfg_IKHeight == Vector3.zero) Cfg_IKHeight = MmddPoseController.GlobalPositionOffset;

            if (CharaAnimeMgr.Instance != null && CharaAnimeMgr.Instance.ociPoseCtrlDic != null)
            {
                foreach (var ctrl in CharaAnimeMgr.Instance.ociPoseCtrlDic.Values)
                {
                    if (ctrl == null) continue;

                    // 同步 Morph Scale (表情强度)
                    if (Math.Abs(ctrl.MorphScale - Cfg_MorphScale) > 0.001f)
                        ctrl.MorphScale = Cfg_MorphScale;

                    // 同步 Position Scale (位移缩放)
                    if (Math.Abs(ctrl.positionScale - Cfg_PosScale) > 0.001f)
                        ctrl.positionScale = Cfg_PosScale;

                    // 同步 Frame Rate (帧率)
                    if (Math.Abs(ctrl.VmdFrameRate - Cfg_VmdFrameRate) > 0.1f)
                        ctrl.VmdFrameRate = Cfg_VmdFrameRate;
                }
            }
        }

        private void OnGUI()
        {
            if (!showMain) return;
            GUI.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
            mainRect = GUI.Window(19980, mainRect, drawMainDelegate, "MMD Player (F8)"); mainRect = ClampWindow(mainRect);
            if (showSettings) { settingsRect = GUI.Window(19981, settingsRect, drawSettingsDelegate, "Global Settings"); settingsRect = ClampWindow(settingsRect); }
            if (showBones) { boneRect = GUI.Window(19982, boneRect, drawBonesDelegate, "Bones & Presets"); boneRect = ClampWindow(boneRect); }
            if (showLoadWindow) { loadRect = GUI.Window(19983, loadRect, drawLoadDelegate, "VMD Load & Play"); loadRect = ClampWindow(loadRect); }
        }

        private Rect ClampWindow(Rect r)
        {
            r.x = Mathf.Clamp(r.x, 0, Screen.width - r.width);
            r.y = Mathf.Clamp(r.y, 0, Screen.height - r.height);
            return r;
        }

        private void DrawMainWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(FormatTitle("Playback:"));
            GUILayout.BeginVertical("box");
            if (CharaAnimeMgr.Instance != null)
            {
                GUILayout.BeginHorizontal(GUILayout.Height(30));
                GUI.backgroundColor = Color.cyan;
                if (GUILayout.Button("📂 LOAD VMD", GUILayout.Height(30))) { showLoadWindow = true; RefreshFileLists(); }
                GUI.backgroundColor = Color.white;
                //if (GUILayout.Button("📸 VPD")) CharaAnimeMgr.Instance.ApplyVpdToScene();
                GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
                if (GUILayout.Button("⏹ STOP(F10)")) CharaAnimeMgr.Instance.StopAnimation();
                GUI.backgroundColor = Color.white;
                GUILayout.EndHorizontal();

                GUILayout.Space(5);
                float currentFrame = CharaAnimeMgr.Instance.CurrentFrame;
                float maxFrame = CharaAnimeMgr.Instance.MaxFrame > 0 ? CharaAnimeMgr.Instance.MaxFrame : 100f;
                GUILayout.Label($"Frame: {currentFrame:F0} / {maxFrame:F0}");

                GUILayout.BeginHorizontal(GUILayout.Height(25));
                if (GUILayout.Button("<<", GUILayout.Width(30))) CharaAnimeMgr.Instance.StepFrame(-1.0f);
                float newFrame = GUILayout.HorizontalSlider(currentFrame, 0f, maxFrame);
                if (Math.Abs(newFrame - currentFrame) > 0.1f) CharaAnimeMgr.Instance.SeekToFrame(newFrame);
                if (GUILayout.Button(">>", GUILayout.Width(30))) CharaAnimeMgr.Instance.StepFrame(1.0f);
                GUILayout.EndHorizontal();

                GUILayout.Space(5);
                string statusText = CharaAnimeMgr.Instance.IsPaused ? "▶ RESUME" : "⏸ PAUSE";
                GUI.backgroundColor = CharaAnimeMgr.Instance.IsPaused ? Color.green : Color.yellow;
                if (GUILayout.Button(statusText, GUILayout.Height(30))) CharaAnimeMgr.Instance.TogglePause();
                GUI.backgroundColor = Color.white;

                GUILayout.Space(5);
                GUILayout.Label(FormatTitle("Loop Control (A-B):"));
                GUILayout.BeginVertical("box");
                var ctrl = UnityEngine.Object.FindObjectOfType<MmddPoseController>();
                if (ctrl != null)
                {
                    ctrl.EnableLoop = GUILayout.Toggle(ctrl.EnableLoop, " Enable Loop (Hotkey: L)");
                    GUILayout.BeginHorizontal(GUILayout.Height(25));
                    GUILayout.Label($"Start: {ctrl.LoopStart:F0}", GUILayout.Width(60));
                    float newStart = GUILayout.HorizontalSlider(ctrl.LoopStart, 0f, maxFrame);
                    if (Math.Abs(newStart - ctrl.LoopStart) > 0.1f) ctrl.LoopStart = newStart;
                    if (GUILayout.Button("Set", GUILayout.Width(35))) ctrl.LoopStart = currentFrame;
                    if (GUILayout.Button("R", GUILayout.Width(25))) ctrl.LoopStart = 0f;
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal(GUILayout.Height(25));
                    GUILayout.Label($"End: {(ctrl.LoopEnd <= 0.1f ? "Max" : ctrl.LoopEnd.ToString("F0"))}", GUILayout.Width(60));
                    float displayEnd = (ctrl.LoopEnd <= 0.1f) ? maxFrame : ctrl.LoopEnd;
                    float newEnd = GUILayout.HorizontalSlider(displayEnd, 0f, maxFrame);
                    if (newEnd >= maxFrame - 1f) ctrl.LoopEnd = 0f;
                    else if (Math.Abs(newEnd - displayEnd) > 0.1f) ctrl.LoopEnd = newEnd;
                    if (GUILayout.Button("Set", GUILayout.Width(35))) ctrl.LoopEnd = currentFrame;
                    if (GUILayout.Button("R", GUILayout.Width(25))) ctrl.LoopEnd = 0f;
                    GUILayout.EndHorizontal();
                }
                else { GUILayout.Label("<color=red>No Motion Controller</color>"); }
                GUILayout.EndVertical();

                GUILayout.Space(5);
                GUILayout.Label($"BGM Sync: {CharaAnimeMgr.Instance.BgmOffset:F3}s");
                GUILayout.BeginHorizontal(GUILayout.Height(25));
                float oldOffset = CharaAnimeMgr.Instance.BgmOffset;
                float newOffset = GUILayout.HorizontalSlider(oldOffset, -2.0f, 2.0f);
                if (Math.Abs(newOffset - oldOffset) > 0.001f) CharaAnimeMgr.Instance.BgmOffset = newOffset;
                if (GUILayout.Button("Reset", GUILayout.Width(50))) CharaAnimeMgr.Instance.BgmOffset = 0f;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.Space(10);
            GUILayout.Label(FormatTitle("Windows:"));
            GUILayout.BeginHorizontal(GUILayout.Height(40));
            GUI.backgroundColor = showSettings ? Color.green : Color.white;
            if (GUILayout.Button(showSettings ? "Close Settings" : "⚙️ Settings")) showSettings = !showSettings;
            GUI.backgroundColor = showBones ? Color.green : Color.white;
            if (GUILayout.Button(showBones ? "Close Bones" : "🦴 Bones & Presets")) showBones = !showBones;
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private void DrawLoadWindow(int id)
        {
            GUILayout.BeginVertical();

            // 1. 顶部固定区域 (关闭 & 刷新)
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(20))) showLoadWindow = false;
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            if (GUILayout.Button("🔄 Refresh Chars & Files")) { RefreshFileLists(); if (CharaAnimeMgr.Instance != null) CharaAnimeMgr.Instance.ScanCharactersInScene(); }

            // 2. 总体滚动区域 (中间所有选择项)
            loadWindowScrollPos = GUILayout.BeginScrollView(loadWindowScrollPos);

            GUILayout.BeginVertical(); // 内容包装
            GUILayout.BeginHorizontal();

            // --- 左侧：角色列表 ---
            GUILayout.BeginVertical("box", GUILayout.Width(140));
            GUILayout.Label(FormatTitle("1. Select Char"));
            if (CharaAnimeMgr.Instance != null)
            {
                charListScroll = GUILayout.BeginScrollView(charListScroll);
                if (GUILayout.Toggle(selectedCharInGui == "", "★ Global (All)", "button")) selectedCharInGui = "";
                foreach (var go in CharaAnimeMgr.Instance.DetectedCharacters)
                {
                    if (go == null) continue;
                    bool isSelected = (selectedCharInGui == go.name);
                    bool hasAssignment = CharaAnimeMgr.Instance.CharacterVmdAssignments.ContainsKey(go.name) || CharaAnimeMgr.Instance.CharacterMorphAssignments.ContainsKey(go.name);
                    GUI.backgroundColor = hasAssignment ? Color.green : (isSelected ? Color.yellow : Color.white);
                    if (GUILayout.Toggle(isSelected, go.name, "button")) selectedCharInGui = go.name;
                }
                GUI.backgroundColor = Color.white;
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            // --- 右侧：动作/表情文件分配 ---
            GUILayout.BeginVertical("box");
            GUILayout.Label(FormatTitle("2. Assign VMD"));
            GUILayout.Label($"Target: <b>{(string.IsNullOrEmpty(selectedCharInGui) ? "Global Default" : selectedCharInGui)}</b>");

            GUILayout.Label(FormatTitle("Motion VMD"));
            string currentMotion = string.IsNullOrEmpty(selectedCharInGui) ? SelectedMotionFile : (CharaAnimeMgr.Instance != null && CharaAnimeMgr.Instance.CharacterVmdAssignments.TryGetValue(selectedCharInGui, out string mv) ? mv : "(Global)");
            GUILayout.Label($"Current: {currentMotion}");
            if (!string.IsNullOrEmpty(selectedCharInGui) && CharaAnimeMgr.Instance != null) if (GUILayout.Button("Reset Motion to Global")) CharaAnimeMgr.Instance.CharacterVmdAssignments.Remove(selectedCharInGui);

            loadScrollPositionMotion = GUILayout.BeginScrollView(loadScrollPositionMotion, GUILayout.Height(100));
            for (int i = 0; i < AvailableVmdFiles.Count; i++)
            {
                string f = AvailableVmdFiles[i];
                bool h = (currentMotion == f || (string.IsNullOrEmpty(selectedCharInGui) && SelectedMotionFile == f));
                GUI.backgroundColor = h ? Color.cyan : Color.white;
                if (GUILayout.Button(f)) { if (string.IsNullOrEmpty(selectedCharInGui)) SelectedMotionFile = f; else if (CharaAnimeMgr.Instance != null) CharaAnimeMgr.Instance.CharacterVmdAssignments[selectedCharInGui] = f; }
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndScrollView();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Morph VMD"));
            string currentMorph = string.IsNullOrEmpty(selectedCharInGui) ? SelectedMorphFile : (CharaAnimeMgr.Instance != null && CharaAnimeMgr.Instance.CharacterMorphAssignments.TryGetValue(selectedCharInGui, out string mm) ? mm : "(Global)");
            GUILayout.Label($"Current: {currentMorph}");
            if (!string.IsNullOrEmpty(selectedCharInGui) && CharaAnimeMgr.Instance != null) if (GUILayout.Button("Reset Morph to Global")) CharaAnimeMgr.Instance.CharacterMorphAssignments.Remove(selectedCharInGui);

            loadScrollPositionMorph = GUILayout.BeginScrollView(loadScrollPositionMorph, GUILayout.Height(100));
            GUI.backgroundColor = (currentMorph == "None" || (string.IsNullOrEmpty(selectedCharInGui) && SelectedMorphFile == "None")) ? Color.red : Color.white;
            if (GUILayout.Button("None (Use Motion VMD)")) { if (string.IsNullOrEmpty(selectedCharInGui)) SelectedMorphFile = "None"; else if (CharaAnimeMgr.Instance != null) CharaAnimeMgr.Instance.CharacterMorphAssignments[selectedCharInGui] = "None"; }
            GUI.backgroundColor = Color.white;
            foreach (var f in AvailableVmdFiles)
            {
                bool h = (currentMorph == f || (string.IsNullOrEmpty(selectedCharInGui) && SelectedMorphFile == f));
                GUI.backgroundColor = h ? Color.cyan : Color.white;
                if (GUILayout.Button(f)) { if (string.IsNullOrEmpty(selectedCharInGui)) SelectedMorphFile = f; else if (CharaAnimeMgr.Instance != null) CharaAnimeMgr.Instance.CharacterMorphAssignments[selectedCharInGui] = f; }
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            GUILayout.Label("Global Camera & Audio:");
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical("box", GUILayout.Width(200)); GUILayout.Label($"Cam: {SelectedCameraFile}"); loadScrollPositionCamera = GUILayout.BeginScrollView(loadScrollPositionCamera, GUILayout.Height(100)); foreach (var f in AvailableVmdFiles) { if (GUILayout.Button(f)) SelectedCameraFile = f; }
            GUILayout.EndScrollView(); GUILayout.EndVertical();
            GUILayout.BeginVertical("box", GUILayout.Width(200)); GUILayout.Label($"Audio: {SelectedAudioFile}"); loadScrollPositionAudio = GUILayout.BeginScrollView(loadScrollPositionAudio, GUILayout.Height(100)); foreach (var f in AvailableAudioFiles) { if (GUILayout.Button(f)) SelectedAudioFile = f; }
            GUILayout.EndScrollView(); GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical(); 

            GUILayout.EndScrollView();

            // 3. 底部固定区域 (播放按钮)
            GUILayout.Space(5); 
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("▶ PLAY ALL", GUILayout.Height(45))) { if (CharaAnimeMgr.Instance != null) { CharaAnimeMgr.Instance.ApplyMotionToScene(); showLoadWindow = false; } }
            GUI.backgroundColor = Color.white;

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private void DrawSettingsWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace(); GUI.backgroundColor = Color.red; if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(20))) showSettings = false; GUI.backgroundColor = Color.white; GUILayout.EndHorizontal();
            settingsScrollPos = GUILayout.BeginScrollView(settingsScrollPos);

            GUILayout.Label(FormatTitle("Global Settings:"));
            GUILayout.BeginVertical("box");
            DrawSafeSlider("Pos Scale", ref Cfg_PosScale, 0.01f, 0.25f, 0.085f);
            DrawSafeSlider("Morph Scale", ref Cfg_MorphScale, 0.0f, 1.2f, 1.0f);
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Global Position Offset:"));
            GUILayout.BeginVertical("box");
            DrawPosRow("X (L/R)", ref Cfg_IKHeight.x, -2.0f, 2.0f);
            DrawPosRow("Y (Height)", ref Cfg_IKHeight.y, -1.0f, 1.0f);
            DrawPosRow("Z (Fwd/Back)", ref Cfg_IKHeight.z, -2.0f, 2.0f);
            if (GUILayout.Button("Reset All Position", GUILayout.Height(20))) Cfg_IKHeight = Vector3.zero;
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Motion Fix (Special):"));
            GUILayout.BeginVertical("box");
            bool newGroove = GUILayout.Toggle(Cfg_EnableGrooveFix, " Enable Groove(Hip) Fix");
            if (newGroove != Cfg_EnableGrooveFix) Cfg_EnableGrooveFix = newGroove;
            GUILayout.Label("<color=#AAAAAA><size=10>* Fix for motions where 'Center' is locked</size></color>");
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Gaze (LookAt):"));
            GUILayout.BeginVertical("box");
            bool newGaze = GUILayout.Toggle(Cfg_EnableGaze, " Enable Eye/Head Follow");
            if (newGaze != Cfg_EnableGaze) Cfg_EnableGaze = newGaze;
            bool oldEn = GUI.enabled; GUI.enabled = Cfg_EnableGaze;
            DrawSafeSlider("Weight", ref Cfg_GazeWeight, 0f, 1f, 1.0f);
            GUI.enabled = oldEn;
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Camera & Playback:"));
            GUILayout.BeginVertical("box");
            GUILayout.Label($"Camera Y Offset: {Cfg_CameraOffsetY:F3} m");
            GUILayout.BeginHorizontal(GUILayout.Height(30));
            GUI.backgroundColor = Color.yellow; if (GUILayout.Button("- 0.05")) Cfg_CameraOffsetY = Mathf.Max(-1f, Cfg_CameraOffsetY - 0.05f);
            GUI.backgroundColor = Color.green; if (GUILayout.Button("+ 0.05")) Cfg_CameraOffsetY = Mathf.Min(1f, Cfg_CameraOffsetY + 0.05f);
            GUI.backgroundColor = Color.white; GUILayout.EndHorizontal();
            if (GUILayout.Button("Reset Y")) Cfg_CameraOffsetY = 0f;
            GUILayout.Space(5);
            GUILayout.Label($"FPS: {Cfg_VmdFrameRate:F0}");
            GUILayout.BeginHorizontal();
            GUI.backgroundColor = Cfg_VmdFrameRate == 30f ? Color.green : Color.white; if (GUILayout.Button("30")) Cfg_VmdFrameRate = 30f;
            GUI.backgroundColor = Cfg_VmdFrameRate == 60f ? Color.green : Color.white; if (GUILayout.Button("60")) Cfg_VmdFrameRate = 60f;
            GUI.backgroundColor = Color.white; GUILayout.EndHorizontal();
            bool newFovMod = GUILayout.Toggle(Cfg_EnableFovMod, " Enable FOV Mod Support");
            if (newFovMod != Cfg_EnableFovMod) { Cfg_EnableFovMod = newFovMod; var cam = UnityEngine.Object.FindObjectOfType<MmddCameraController>(); if (cam != null) cam.isOffsetDirty = true; }
            GUILayout.Label("<color=grey><size=10>* Maps VMD Distance to FOV (Dist->0)</size></color>");
            GUILayout.EndVertical();

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private void DrawBonesWindow(int id)
        {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal(); GUILayout.FlexibleSpace(); GUI.backgroundColor = Color.red; if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(20))) showBones = false; GUI.backgroundColor = Color.white; GUILayout.EndHorizontal();
            GUILayout.Label(FormatTitle("Presets (Rot + Axis):"));
            GUILayout.BeginVertical("box");
            if (GUILayout.Button("💾 Save Current Config")) SavePreset();
            GUILayout.Space(2);
            presetScrollPos = GUILayout.BeginScrollView(presetScrollPos, GUILayout.Height(100));
            if (presetFileList.Count == 0) GUILayout.Label("No presets found.");
            foreach (var filePath in presetFileList)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(fileName)) LoadPreset(filePath);
                if (GUILayout.Button("X", GUILayout.Width(25))) DeletePreset(filePath);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("🔄 Refresh List")) RefreshPresetList();
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Select Bones:"));
            GUILayout.BeginVertical("box");
            int columns = 4;
            for (int i = 0; i < presetBones.Length; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int j = 0; j < columns; j++)
                {
                    if (i + j < presetBones.Length)
                    {
                        string bName = presetBones[i + j];
                        bool oldState = MmddPoseController.BoneSettings.ContainsKey(bName);
                        string displayName = bName.Replace("親指", "拇").Replace("人指", "食");
                        bool newState = GUILayout.Toggle(oldState, displayName);
                        if (newState != oldState)
                        {
                            if (newState) MmddPoseController.UpdateBoneRotationOffset(bName, Vector3.zero);
                            else if (MmddPoseController.BoneSettings.ContainsKey(bName)) MmddPoseController.BoneSettings.Remove(bName);
                        }
                    }
                    else { GUILayout.Label("", GUILayout.Width(10)); }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.Space(5);
            GUILayout.Label(FormatTitle("Adjustments:"));
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            var activeBones = MmddPoseController.BoneSettings.Keys.ToList();
            if (activeBones.Count == 0) GUILayout.Label("Select bones above.");
            foreach (var boneName in activeBones)
            {
                if (!MmddPoseController.BoneSettings.TryGetValue(boneName, out var adj)) continue;
                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                GUILayout.Label(FormatTitle(boneName), GUILayout.Width(100));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reset", GUILayout.Width(50))) { adj.RotOffsetEuler = Vector3.zero; adj.SetAxisCorrection(Vector3.zero); }
                GUILayout.EndHorizontal();
                GUILayout.Label("<color=cyan>Rot Offset (r)</color>");
                Vector3 rot = adj.RotOffsetEuler;
                rot.x = DrawAxisControl("X", rot.x); rot.y = DrawAxisControl("Y", rot.y); rot.z = DrawAxisControl("Z", rot.z);
                adj.RotOffsetEuler = rot;
                GUILayout.Label("<color=orange>Axis Fix (a)</color>");
                Vector3 axis = adj.AxisCorrectionEuler;
                axis.x = DrawAxisControl("X", axis.x); axis.y = DrawAxisControl("Y", axis.y); axis.z = DrawAxisControl("Z", axis.z);
                adj.SetAxisCorrection(axis);
                GUILayout.EndVertical();
                GUILayout.Space(2);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private void RefreshPresetList()
        {
            presetFileList.Clear();
            if (!Directory.Exists(PRESET_DIR)) return;
            presetFileList.AddRange(Directory.GetFiles(PRESET_DIR, "*.txt"));
        }

        private void SavePreset()
        {
            try
            {
                if (!Directory.Exists(PRESET_DIR)) Directory.CreateDirectory(PRESET_DIR);
                string fileName = $"Preset_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string path = Path.Combine(PRESET_DIR, fileName);
                string data = MmddPoseController.ExportPreset();
                File.WriteAllText(path, data);
                RefreshPresetList();
            }
            catch { }
        }

        private void LoadPreset(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string data = File.ReadAllText(path);
                MmddPoseController.ImportPreset(data);
                SyncSelections(null);
            }
            catch { }
        }

        private void DeletePreset(string path)
        { if (File.Exists(path)) File.Delete(path); RefreshPresetList(); }

        private void DrawSafeSlider(string label, ref float val, float min, float max, float def)
        {
            GUILayout.Label($"{label}: {val:F4}");
            GUILayout.BeginHorizontal(GUILayout.Height(25));
            float newVal = GUILayout.HorizontalSlider(val, min, max, GUILayout.ExpandWidth(true));
            if (Math.Abs(newVal - val) > 0.0001f) val = newVal;
            if (GUILayout.Button("R", GUILayout.Width(30))) val = def;
            GUILayout.EndHorizontal();
        }

        private float DrawAxisControl(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(15));
            float newValue = value;
            try { newValue = GUILayout.HorizontalSlider(value, -180f, 180f); } catch { }
            GUILayout.Label(newValue.ToString("F0"), GUILayout.Width(30));
            if (GUILayout.Button("<", GUILayout.Width(25))) newValue -= 1f;
            if (GUILayout.Button(">", GUILayout.Width(25))) newValue += 1f;
            if (GUILayout.Button("R", GUILayout.Width(25))) newValue = 0f;
            GUILayout.EndHorizontal();
            return newValue;
        }

        private void DrawPosRow(string label, ref float val, float min, float max)
        {
            GUILayout.BeginHorizontal(GUILayout.Height(25));
            GUILayout.Label($"{label}: {val:F3}", GUILayout.Width(90));
            float newVal = GUILayout.HorizontalSlider(val, min, max, GUILayout.ExpandWidth(true));
            if (Math.Abs(newVal - val) > 0.001f) val = newVal;
            if (GUILayout.Button("R", GUILayout.Width(25))) val = 0;
            GUILayout.EndHorizontal();
        }

        private string FormatTitle(string text) => $"<b><color=#FFFF00>{text}</color></b>";
    }
}