using BepInEx;
using BepInEx.Unity.IL2CPP;
using CharaAnime;
using Il2CppInterop.Runtime.Injection;

namespace CharaAnime_IL2CPP
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGUID = "com.yourname.charaanime";
        public const string PluginName = "CharaAnime_IL2CPP";
        public const string PluginVersion = "1.0.2";

        public static Plugin Instance;

        public override void Load()
        {
            Instance = this;

            // =========================================================
            // 1. 注册所有自定义组件
            // =========================================================
            ClassInjector.RegisterTypeInIl2Cpp<MmddPoseController>();
            ClassInjector.RegisterTypeInIl2Cpp<CharaAnimeMgr>();
            ClassInjector.RegisterTypeInIl2Cpp<MmddCameraController>();
            ClassInjector.RegisterTypeInIl2Cpp<MmddAudioPlayer>();

            // UI 组件
            ClassInjector.RegisterTypeInIl2Cpp<MmddGui>();

            // =========================================================
            // 2. 挂载组件到场景
            // =========================================================

            // 挂载总管理器
            AddComponent<CharaAnimeMgr>();

            // 挂载 UI 面板
            AddComponent<MmddGui>();

            Log.LogInfo($"Plugin {PluginGUID} {PluginVersion} loaded! All systems ready.");
        }
    }
}