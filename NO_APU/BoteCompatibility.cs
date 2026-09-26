using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace NO_APU;

internal static class BoteCompatibility
{
    internal const string BoteGuid = "com.minec.bote";
    private const string ModAssetsTypeName = "NOComponentWIP.ModAssets";
    private static MethodInfo? _modAssetsGetter;
    private static FieldInfo? _serializedShipDefinitionsField;
    private static HashSet<AircraftDefinition>? _shipDefinitions;
    
    internal static bool Installed { get; private set; }
    
    internal static void Initialise()
    {
        Installed = Chainloader.PluginInfos.ContainsKey(BoteGuid);
        
        if (!Installed)
            return;
        
        var modAssetsType = AccessTools.TypeByName(ModAssetsTypeName);
        if (modAssetsType == null)
        {
            Plugin.Logger.LogWarning("BOTE detected, but NOComponentWIP.ModAssets could not be found. " +
                                     "BOTE deployment compatibility will fall back to BOTE's own handling.");
            return;
        }
        
        _modAssetsGetter = AccessTools.PropertyGetter(modAssetsType, "i");
        _serializedShipDefinitionsField = AccessTools.Field(modAssetsType, "shipDefinitions");
        if (_modAssetsGetter == null || _serializedShipDefinitionsField == null)
        {
            Plugin.Logger.LogWarning("BOTE detected, but its ship definition data could not be resolved. " +
                                     "BOTE deployment compatibility will fall back to BOTE's own handling.");
            return;
        }
        
        Plugin.Logger.LogInfo("BOTE detected. Deployment compatibility available.");
    }
    
    internal static bool EnsureReady()
    {
        if (!Installed || _shipDefinitions != null)
            return true;
        
        if (_modAssetsGetter == null || _serializedShipDefinitionsField == null)
            return false;
        
        object? modAssets;
        try
        {
            modAssets = _modAssetsGetter.Invoke(null, null);
        }
        catch
        {
            return false;
        }
        
        if (modAssets == null)
            return false;
        
        if (_serializedShipDefinitionsField.GetValue(modAssets) is not AircraftDefinition[] definitions ||
            definitions.Length == 0)
            return false;
        
        var ships = new HashSet<AircraftDefinition>();
        foreach (var definition in definitions)
            if (definition != null)
                ships.Add(definition);
        
        if (ships.Count == 0)
            return false;
        
        _shipDefinitions = ships;
        Plugin.Logger.LogInfo(
            $"BOTE compatibility initialized with {_shipDefinitions.Count} excluded ship definitions.");
        return true;
    }
    
    internal static bool IsShip(AircraftDefinition definition) =>
        Installed && _shipDefinitions != null && _shipDefinitions.Contains(definition);
}