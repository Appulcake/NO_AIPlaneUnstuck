using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NO_APU;

internal static class DeploymentCompatibility
{
    internal static void Apply(Harmony harmony)
    {
        var deployMethod = AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.DeployAIAircraft));
        
        if (deployMethod == null)
        {
            Plugin.Logger.LogError("Could not find FactionHQ.DeployAIAircraft.");
            return;
        }
        
        if (!BoteCompatibility.Installed)
        {
            PatchVanillaDeployment(harmony, deployMethod);
            return;
        }
        
        var patchInfo = Harmony.GetPatchInfo(deployMethod);
        var botePrefixes = patchInfo?.Prefixes.Where(p => p.owner == BoteCompatibility.BoteGuid).ToArray();
        if (botePrefixes == null || botePrefixes.Length != 1)
        {
            Plugin.Logger.LogWarning($"BOTE is installed, but expected exactly one BOTE " +
                                     $"DeployAIAircraft prefix; found " +
                                     $"{botePrefixes?.Length ?? 0}. " +
                                     $"Leaving BOTE deployment handling intact.");
            return;
        }
        
        var botePrefix = botePrefixes[0].PatchMethod;
        var parameters = botePrefix.GetParameters();
        
        if (!botePrefix.IsStatic || botePrefix.ReturnType != typeof(bool) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(FactionHQ))
        {
            Plugin.Logger.LogWarning($"BOTE DeployAIAircraft prefix has an unexpected signature: " +
                                     $"{botePrefix.DeclaringType?.FullName}.{botePrefix.Name}. " +
                                     $"Leaving BOTE deployment handling intact.");
            return;
        }
        
        try
        {
            PatchBoteDeployment(harmony, botePrefix);
            Plugin.Logger.LogInfo($"BOTE deployment compatibility enabled via " +
                                  $"{botePrefix.DeclaringType?.FullName}.{botePrefix.Name}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Failed to install BOTE deployment compatibility. " +
                                   $"BOTE's normal deployment handling remains installed.\n{ex}");
        }
    }
    
    private static void PatchVanillaDeployment(Harmony harmony, MethodBase deployMethod)
    {
        var prefix = AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.DeployAIAircraftPrefix));
        harmony.Patch(deployMethod, new HarmonyMethod(prefix));
    }
    
    private static void PatchBoteDeployment(Harmony harmony, MethodInfo botePrefix)
    {
        var compatPrefix =
            AccessTools.Method(typeof(HarmonyPatches), nameof(HarmonyPatches.BoteDeployAIAircraftPrefix));
        harmony.Patch(botePrefix, new HarmonyMethod(compatPrefix));
    }
}