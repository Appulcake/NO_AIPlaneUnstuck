using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

// ReSharper disable InconsistentNaming

namespace NO_AIPlaneUnstuck;

[HarmonyPatch]
internal static class HarmonyPatches
{
    // Weak table tracking taxiing planes to handle destroyed objects
    private static ConditionalWeakTable<AIPilotTaxiState, TaxiingPlanesTracker> TrackedPlanes = new();
    
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.IsStuck))]
    [HarmonyPostfix]
    private static void IsStuckPostfix(AIPilotTaxiState __instance, Aircraft? aircraft, ref bool __result)
    {
        if (__result || aircraft == null || aircraft.rb == null)
            return;
        
        var progressDistance = Mathf.Max(Plugin.ProgressDistance.Value, 0.1f);
        var progressDistanceSqr = progressDistance * progressDistance;
        var progressTimeout = Mathf.Max(Plugin.ProgressTimeout.Value, 0.1f);
        
        var tracker = TrackedPlanes.GetValue(__instance, _ => new TaxiingPlanesTracker());
        var currentPosition = aircraft.GlobalPosition();
        var now = Time.timeSinceLevelLoad;
        
        // Initialise tracking new plane
        if (!tracker.Initialized || !ReferenceEquals(tracker.Aircraft, aircraft))
        {
            tracker.Aircraft = aircraft;
            tracker.AnchorPosition = currentPosition;
            tracker.LastProgressTime = now;
            tracker.Initialized = true;
            tracker.Logged = false;
            return;
        }
        
        // Track movement in large enough steps (from AnchorPosition instead of previous frame's position)
        // to prevent any tiny jitter from resetting the "haven't moved long enough" timer
        if (FastMath.SquareDistance(tracker.AnchorPosition, currentPosition) >= progressDistanceSqr)
        {
            tracker.AnchorPosition = currentPosition;
            tracker.LastProgressTime = now;
            tracker.Logged = false;
            return;
        }
        
        if (now - tracker.LastProgressTime < progressTimeout)
            return;
        
        // This'll cause FixedUpdateState to initiate braking then disembarking, which should remove this stuck plane
        __result = true;
        
        if (tracker.Logged) return;
        
        Plugin.Logger.LogInfo(
            $"Taxiing aircraft \"{aircraft.name}\" made less than {progressDistance:0.#}m of progress for " +
            $"{progressTimeout:0} seconds. Sending IsStuck state, forcing disembark..");
        
        tracker.Logged = true;
    }
    
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.LeaveState))]
    [HarmonyPostfix]
    private static void LeaveStatePostfix(AIPilotTaxiState __instance)
    {
        TrackedPlanes.Remove(__instance);
    }
    
    internal static void ResetTrackers()
    {
        TrackedPlanes = new ConditionalWeakTable<AIPilotTaxiState, TaxiingPlanesTracker>();
    }
    
    private sealed class TaxiingPlanesTracker
    {
        internal Aircraft Aircraft = null!;
        internal GlobalPosition AnchorPosition;
        internal bool Initialized;
        internal float LastProgressTime;
        internal bool Logged;
    }
}