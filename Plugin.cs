using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NO_AIPlaneUnstuck;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger { get; private set; } = null!;
    
    private static ConfigEntry<bool> Enabled { get; set; } = null!;
    internal static ConfigEntry<float> ProgressDistance { get; private set; } = null!;
    internal static ConfigEntry<float> ProgressTimeout { get; private set; } = null!;
    
    private Harmony? Harmony { get; set; }
    
    private void Awake()
    {
        Logger = base.Logger;
        
        Enabled = Config.Bind("General", "Enable plugin", true);
        ProgressDistance = Config.Bind("General", "Minimum Progress Distance", 5f,
            new ConfigDescription(
                "Minimum progress a plane has to make within No Progress Timeout to not get flagged as stuck.",
                new AcceptableValueRange<float>(0.1f, 1000f)));
        ProgressTimeout = Config.Bind("General", "No Progress Timeout", 30f,
            new ConfigDescription(
                "Time window within which a plane has to make at least Minimum Progress Distance to not get flagged as stuck.",
                new AcceptableValueRange<float>(0.1f, 600f)));
        
        Enabled.SettingChanged += (_, _) =>
        {
            if (Harmony == null)
                return;
            Repatch();
        };
        
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Repatch();
    }
    
    private void OnDestroy()
    {
        Harmony?.UnpatchSelf();
    }
    
    private void Repatch()
    {
        Harmony?.UnpatchSelf();
        HarmonyPatches.ResetTrackers();
        
        if (!Enabled.Value) return;
        
        Harmony?.PatchAll();
        Logger.LogInfo("Patching done!");
    }
}