using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NO_APU;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency(BoteCompatibility.BoteGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger { get; private set; } = null!;
    
    private static ConfigEntry<bool> Enabled { get; set; } = null!;
    private static ConfigEntry<float> ProgressDistance { get; set; } = null!;
    private static ConfigEntry<float> ProgressTimeout { get; set; } = null!;
    internal static ConfigEntry<float> LandingBlockRange { get; private set; } = null!;
    private static ConfigEntry<float> ObstacleAvoidanceCone { get; set; } = null!;
    private static ConfigEntry<bool> AggressiveTakeoffPriority { get; set; } = null!;
    
    internal static ConfigEntry<bool> AllowAlternateBaseSpawn { get; private set; } = null!;
    internal static ConfigEntry<float> Rank0DiversionChance { get; private set; } = null!;
    internal static ConfigEntry<float> Rank12DiversionChance { get; private set; } = null!;
    internal static ConfigEntry<float> Rank3DiversionChance { get; private set; } = null!;
    internal static ConfigEntry<float> Rank46DiversionChance { get; private set; } = null!;
    
    internal static ConfigEntry<int> MaxDeployAttemptsPerTick { get; private set; } = null!;
    internal static ConfigEntry<int> AIDeficitPerExtraAttempt { get; private set; } = null!;
    internal static ConfigEntry<int> ExtraSpawnCongestionLimit { get; private set; } = null!;
    
    private static ConfigEntry<bool> DebugLogs { get; set; } = null!;
    
    private Harmony? Harmony { get; set; }
    
    internal static float CachedProgressDistance { get; private set; }
    internal static float CachedProgressDistanceSqr { get; private set; }
    internal static float CachedProgressTimeout { get; private set; }
    internal static float CachedObstacleAvoidanceDot { get; private set; }
    internal static bool CachedAggressiveTakeoffPriority { get; private set; }
    internal static bool CachedDebugLogs { get; private set; }
    
    private void Awake()
    {
        Logger = base.Logger;
        
        BoteCompatibility.Initialise();
        
        Enabled = Config.Bind("General", "Enable plugin", true);
        ProgressDistance = Config.Bind("Taxi", "Minimum Progress Distance", 5f,
            new ConfigDescription(
                "Minimum progress a plane has to make within No Progress Timeout to not get flagged as stuck.",
                new AcceptableValueRange<float>(0.1f, 1000f)));
        
        ProgressTimeout = Config.Bind("Taxi", "No Progress Timeout", 30f,
            new ConfigDescription(
                "Time window within which a plane has to make at least Minimum Progress Distance to not get flagged as stuck.",
                new AcceptableValueRange<float>(0.1f, 600f)));
        LandingBlockRange = Config.Bind("Taxi", "Landing Block Range", 500f,
            "A landing plane has to be within this distance to cause that runway to be marked as blocked " +
            "so AI planes don't try to use it. Vanilla default is ~2000/2500m (short/long takeoff).");
        ObstacleAvoidanceCone = Config.Bind("Taxi", "Obstacle Avoidance Cone", 120f,
            new ConfigDescription(
                "Width of obstacle avoidance angle in degrees of the forward cone in which taxiing AI planes react to other " +
                "planes. Vanilla is 180 degrees, lower values make AI less cautious about aircraft approaching from the sides.",
                new AcceptableValueRange<float>(0f, 360f)));
        AggressiveTakeoffPriority = Config.Bind("Taxi", "Aggressive Takeoff Priority", true,
            "When an AI plane is first in the takeoff queue, ignore normal taxi obstacle braking " +
            "and runway crossing waits, so it commits to entering the runway and taking off.");
        
        AllowAlternateBaseSpawn = Config.Bind("Deployment", "0. Enable Alternate Airbase Spawn", true,
            "Enable allowing planes to spawn at least congested alternative airports, with configurable " +
            "chance for rank brackets.");
        Rank0DiversionChance = Config.Bind("Deployment", "1. Rank 0 Diversion Chance", 0.05f,
            new ConfigDescription(
                "Chance for a rank 0 AI plane to prefer a less congested alternate airbase.",
                new AcceptableValueRange<float>(0f, 1f)));
        Rank12DiversionChance = Config.Bind("Deployment", "2. Rank 1-2 Diversion Chance", 0.25f,
            new ConfigDescription(
                "Chance for a rank 1-2 AI plane to prefer a less congested alternate airbase.",
                new AcceptableValueRange<float>(0f, 1f)));
        Rank3DiversionChance = Config.Bind("Deployment", "3. Rank 3 Diversion Chance", 0.60f,
            new ConfigDescription(
                "Chance for a rank 3 AI plane to prefer a less congested alternate airbase.",
                new AcceptableValueRange<float>(0f, 1f)));
        Rank46DiversionChance = Config.Bind("Deployment", "4. Rank 4-6 Diversion Chance", 0.90f,
            new ConfigDescription(
                "Chance for a rank 4-6 AI plane to prefer a less congested alternate airbase.",
                new AcceptableValueRange<float>(0f, 1f)));
        MaxDeployAttemptsPerTick = Config.Bind("Deployment", "5. Max Deploy Attempts Per Tick", 3,
            "Maximum AI plane deployment attempts during each normal deployment tick. Vanilla is 1.");
        AIDeficitPerExtraAttempt = Config.Bind("Deployment", "6. AI Deficit Per Extra Attempt", 5,
            "Adds another deployment attempt for roughly every this many missing AI planes.");
        ExtraSpawnCongestionLimit = Config.Bind("Deployment", "7. Extra Spawn Congestion Limit", 4,
            "Extra deployment attempts are only made while at least one airbase has this many or fewer AI planes.");
        
        DebugLogs = Config.Bind("Debug", "Enable Debug Logs", false);
        
        Enabled.SettingChanged += (_, _) =>
        {
            if (Harmony == null)
                return;
            Repatch();
        };
        
        ProgressDistance.SettingChanged += (_, _) => CacheSettings();
        ProgressTimeout.SettingChanged += (_, _) => CacheSettings();
        ObstacleAvoidanceCone.SettingChanged += (_, _) => CacheSettings();
        AggressiveTakeoffPriority.SettingChanged += (_, _) => CacheSettings();
        DebugLogs.SettingChanged += (_, _) => CacheSettings();
        CacheSettings();
        
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Repatch();
        
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} loaded!");
    }
    
    private void OnDestroy()
    {
        Harmony?.UnpatchSelf();
    }
    
    private static void CacheSettings()
    {
        CachedProgressDistance = Mathf.Max(ProgressDistance.Value, 0.1f);
        CachedProgressDistanceSqr = CachedProgressDistance * CachedProgressDistance;
        CachedProgressTimeout = Mathf.Max(ProgressTimeout.Value, 0.1f);
        CachedAggressiveTakeoffPriority = AggressiveTakeoffPriority.Value;
        
        var cone = Mathf.Clamp(ObstacleAvoidanceCone.Value, 0f, 360f);
        CachedObstacleAvoidanceDot = Mathf.Cos(cone * 0.5f * Mathf.Deg2Rad);
        
        CachedDebugLogs = DebugLogs.Value;
    }
    
    private void Repatch()
    {
        Harmony?.UnpatchSelf();
        HarmonyPatches.ResetTrackers();
        
        if (!Enabled.Value)
            return;
        
        Harmony?.PatchAll();
        DeploymentCompatibility.Apply(Harmony!);
        Logger.LogInfo("Patching done!");
    }
}