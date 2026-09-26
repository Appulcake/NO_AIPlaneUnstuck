using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

// ReSharper disable InconsistentNaming

namespace NO_APU;

[HarmonyPatch]
internal static class HarmonyPatches
{
    private static ConditionalWeakTable<AIPilotTaxiState, TaxiingPlanesTracker> TrackedPlanes = new();
    
    private static readonly List<Airbase> SpawnableAirbases = [];
    private static readonly Dictionary<Airbase, int> PendingAirbaseSpawns = new();
    
    private static bool _loggedBoteDeploymentIntercept;
    
    // Stricter enforcement of stuck planes, if they haven't moved X distance in Y time, force (safe) disembark to
    // make space, as they otherwise often can get indefinitely stuck in a deadlock
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.IsStuck))]
    [HarmonyPostfix]
    private static void IsStuckPostfix(AIPilotTaxiState __instance, Aircraft? aircraft, ref bool __result)
    {
        if (__result || aircraft == null || aircraft.rb == null)
            return;
        
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
        if (FastMath.SquareDistance(tracker.AnchorPosition, currentPosition) >= Plugin.CachedProgressDistanceSqr)
        {
            tracker.AnchorPosition = currentPosition;
            tracker.LastProgressTime = now;
            tracker.Logged = false;
            return;
        }
        
        if (now - tracker.LastProgressTime < Plugin.CachedProgressTimeout)
            return;
        
        // This'll cause FixedUpdateState to initiate braking then disembarking, which should remove this stuck plane
        __result = true;
        
        if (tracker.Logged) return;
        
        if (Plugin.CachedDebugLogs)
            Plugin.Logger.LogInfo(
                $"Taxiing aircraft \"{aircraft.name}\" made less than {Plugin.CachedProgressDistance:0.#}m of progress " +
                $"for {Plugin.CachedProgressTimeout:0} seconds. Sending IsStuck state, forcing disembark.");
        
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
    
    // AI planes by default choose to exclusively try to spawn from the nearest airbase to the perceived threat/objective
    // as long as there's any space to spawn, leading to a potentially huge chunk of AI units just stuck there waiting
    // in queue to take off
    // This shifts a configurable % of to-be-spawned AI planes to spawn from a less congested alternate airport
    // (if available/has space) instead, to spread out some of the backlog
    // This chance is configurable for a couple rank brackets, as you don't want crickets to slow boat from an alternate
    // base in narnia, but high rank planes are fast and tend to have long range weapons that are far more suitable for it
    internal static bool DeployAIAircraftPrefix(FactionHQ __instance)
    {
        DeployAIAircraftReplacement(__instance);
        return false;
    }
    
    private static void DeployAIAircraftReplacement(FactionHQ hq)
    {
        var friendlyPlayers = hq.factionPlayers.Count;
        var enemyPlayers = 0;
        
        foreach (var hqEntry in FactionRegistry.GetAllHQs())
            if (hqEntry != hq)
                enemyPlayers += hqEntry.GetPlayers(false).Count;
        
        var aiLimit = hq.AIAircraftLimit + enemyPlayers * hq.addAIPerEnemyPlayer -
                      friendlyPlayers * hq.reduceAIPerFriendlyPlayer;
        
        if (hq.activeAIAircraft.Count >= aiLimit)
            return;
        
        var aircraft = Encyclopedia.i.aircraft;
        
        for (var i = 0; i < aircraft.Count; i++)
        {
            var index = Random.Range(i, aircraft.Count);
            (aircraft[i], aircraft[index]) = (aircraft[index], aircraft[i]);
        }
        
        var reserveAmount = hq.reserveAirframes + friendlyPlayers * hq.extraReservesPerPlayer;
        
        foreach (var definition in aircraft)
        {
            if (BoteCompatibility.IsShip(definition) || !hq.AircraftSupply.TryGetValue(definition, out var supply) ||
                supply.Count <= reserveAmount)
                continue;
            
            if (TryDeployAircraft(hq, definition))
                return;
        }
    }
    
    internal static bool BoteDeployAIAircraftPrefix(FactionHQ __0, ref bool __result)
    {
        if (!BoteCompatibility.EnsureReady())
            return true;
        
        if (!_loggedBoteDeploymentIntercept)
        {
            Plugin.Logger.LogInfo("Intercepted BOTE DeployAIAircraft prefix successfully.");
            _loggedBoteDeploymentIntercept = true;
        }
        
        DeployAIAircraftReplacement(__0);
        __result = false;
        return false;
    }
    
    private static bool TryDeployAircraft(FactionHQ hq, AircraftDefinition definition)
    {
        SpawnableAirbases.Clear();
        
        // Preserve threat + distance ordering from airbasesSorted
        foreach (var entry in hq.airbasesSorted)
        {
            var airbase = entry.airbase;
            
            if (airbase != null && !airbase.disabled && airbase.CanSpawnAircraft(definition))
                SpawnableAirbases.Add(airbase);
        }
        
        if (SpawnableAirbases.Count == 0)
            return false;
        
        // This index 0 is what vanilla normally prefers (closest to perceived threat/objective)
        var primary = SpawnableAirbases[0];
        var diversionChance = GetDiversionChance(definition.aircraftParameters.rankRequired);
        var divert = diversionChance > 0f && SpawnableAirbases.Count > 1 && Random.value < diversionChance;
        
        if (!divert)
        {
            foreach (var airbase in SpawnableAirbases)
                if (TrySpawnAircraft(hq, airbase, definition))
                    return true;
            
            return false;
        }
        
        // With multiple airbases available, % chance based diversion of plane backlog to least congested alternate airbase
        // Can still fall back to use the primary vanilla choice if this fails
        while (SpawnableAirbases.Count > 1)
        {
            var bestIndex = 1;
            var bestCongestion = GetAirbaseCongestion(SpawnableAirbases[1]);
            
            for (var i = 2; i < SpawnableAirbases.Count; i++)
            {
                var congestion = GetAirbaseCongestion(SpawnableAirbases[i]);
                
                if (congestion < bestCongestion)
                {
                    bestCongestion = congestion;
                    bestIndex = i;
                }
            }
            
            var alternate = SpawnableAirbases[bestIndex];
            SpawnableAirbases.RemoveAt(bestIndex);
            
            if (TrySpawnAircraft(hq, alternate, definition))
                return true;
        }
        
        return TrySpawnAircraft(hq, primary, definition);
    }
    
    private static float GetDiversionChance(int rank)
    {
        if (!Plugin.AllowAlternateBaseSpawn.Value)
            return 0f;
        
        return rank switch
        {
            <= 0 => Plugin.Rank0DiversionChance.Value,
            <= 2 => Plugin.Rank12DiversionChance.Value,
            3 => Plugin.Rank3DiversionChance.Value,
            _ => Plugin.Rank46DiversionChance.Value
        };
    }
    
    private static int GetAirbaseCongestion(Airbase airbase)
    {
        var congestion = airbase.ControlledAircraft?.Count ?? 0;
        
        if (PendingAirbaseSpawns.TryGetValue(airbase, out var pending))
            congestion += pending;
        
        return congestion;
    }
    
    private static bool TrySpawnAircraft(FactionHQ hq, Airbase airbase, AircraftDefinition definition)
    {
        Loadout? loadout = null;
        var fuelLevel = definition.aircraftParameters.DefaultFuelLevel;
        
        var standardLoadout = definition.aircraftParameters.GetRandomStandardLoadout(definition, hq);
        
        if (standardLoadout != null)
        {
            loadout = standardLoadout.loadout;
            fuelLevel = standardLoadout.FuelRatio;
        }
        
        var livery = definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        
        var spawned = airbase.TrySpawnAircraft(null, definition, new LiveryKey(livery), loadout, fuelLevel).Allowed;
        
        if (spawned)
        {
            PendingAirbaseSpawns.TryGetValue(airbase, out var pending);
            PendingAirbaseSpawns[airbase] = pending + 1;
        }
        
        return spawned;
    }
    
    // AI by default very conservatively wait for a blocked runway, a plane quite far away from landing already
    // marks it as blocked, this helps restrict that range only to closer + aligned on final planes to help squeeze
    // out more planes to take off
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.IsAvailableForTakeoff))]
    [HarmonyPrefix]
    private static bool IsAvailableForTakeoffPrefix(Airbase.Runway __instance, Aircraft querier, ref bool __result)
    {
        if (HasBlockingLanding(__instance, querier))
        {
            __result = false;
            return false;
        }
        
        if (__instance.takeoffQueue.TryPeek(out var queued))
        {
            if (queued == null || queued.disabled)
            {
                __instance.takeoffQueue.Dequeue();
            }
            else
            {
                __result = queued == querier;
                return false;
            }
        }
        
        if (__instance.OtherAircraftUsingRunway(querier))
        {
            __result = false;
            return false;
        }
        
        foreach (var crossing in __instance.crossingRunways)
            if (crossing.OtherAircraftUsingRunway(querier))
            {
                __result = false;
                return false;
            }
        
        __result = true;
        return false;
    }
    
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.OtherAircraftUsingRunway))]
    [HarmonyPrefix]
    private static bool OtherAircraftUsingRunwayPrefix(Airbase.Runway __instance, Aircraft checker, ref bool __result)
    {
        if (HasBlockingLanding(__instance, checker) ||
            (__instance.takeoffQueue.TryPeek(out var queued) && queued != null && queued != checker))
        {
            __result = true;
            return false;
        }
        
        __result = false;
        return false;
    }
    
    private static bool HasBlockingLanding(Airbase.Runway runway, Aircraft checker)
    {
        var range = Mathf.Max(Plugin.LandingBlockRange.Value, 0f);
        
        foreach (var landing in runway.GetLandingList())
        {
            if (landing == null || landing == checker || landing.disabled)
                continue;
            
            if (runway.AircraftOnRunway(landing))
                return true;
            
            if (range > 0f && runway.AircraftOnApproach(landing, range, false))
                return true;
        }
        
        return false;
    }
    
    // When an AI plane becomes the next up in slot to take-off, if its brake check is triggered it'll dequeue
    // This can happen easily in forks where multiple taxiways terminate into a runway, so now the plane about to
    // take-off suddenly stops because another taxiing plane approaches from another angle, and it brakes for it
    // which then cancels its take-off, and now both are waiting
    // This prevents that from giving up its take-off slot in such an event
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.WaitingForTakeoffClearance))]
    [HarmonyPrefix]
    private static void WaitingForTakeoffClearancePrefix(AIPilotTaxiState __instance, out bool __state)
    {
        __state = __instance.takeoffQueued;
    }
    
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.WaitingForTakeoffClearance))]
    [HarmonyPostfix]
    private static void WaitingForTakeoffClearancePostfix(AIPilotTaxiState __instance, bool __state)
    {
        if (!__state || __instance.takeoffQueued)
            return;
        
        var runway = __instance.takeoffRunway;
        var aircraft = __instance.aircraft;
        
        if (runway == null || aircraft == null)
            return;
        
        if (!runway.takeoffQueue.Contains(aircraft))
            runway.QueueTakeoff(aircraft);
        
        __instance.takeoffQueued = true;
    }
    
    private static bool HasTakeoffPriority(AIPilotTaxiState state)
    {
        if (!state.takeoffQueued || state.takeoffRunway == null || state.aircraft == null)
            return false;
        
        if (!state.takeoffRunway.takeoffQueue.TryPeek(out var queued))
            return false;
        
        return queued == state.aircraft;
    }
    
    // Allow increasing how many AI plane spawn attempts happen per tick, optionally scaling up when there's a larger
    // backlog
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.DeployUnits))]
    [HarmonyPrefix]
    private static void DeployUnitsPrefix()
    {
        PendingAirbaseSpawns.Clear();
    }
    
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.DeployUnits))]
    [HarmonyPostfix]
    private static void DeployUnitsPostfix(FactionHQ __instance)
    {
        var deficit = GetAIAircraftDeficit(__instance);
        
        if (deficit <= 0)
            return;
        
        var maxAttempts = Mathf.Max(1, Plugin.MaxDeployAttemptsPerTick.Value);
        var deficitPerExtra = Mathf.Max(1, Plugin.AIDeficitPerExtraAttempt.Value);
        
        // DeployUnits() already did an attempt, don't count that
        var desiredTotalAttempts = Mathf.Clamp(1 + deficit / deficitPerExtra, 1, maxAttempts);
        var extraAttempts = desiredTotalAttempts - 1;
        
        for (var i = 0; i < extraAttempts; i++)
        {
            if (!HasLowCongestionAirbase(__instance) || GetAIAircraftDeficit(__instance) <= 0)
                break;
            
            __instance.DeployAIAircraft();
        }
    }
    
    private static bool HasLowCongestionAirbase(FactionHQ hq)
    {
        var limit = Mathf.Max(Plugin.ExtraSpawnCongestionLimit.Value, 0);
        
        foreach (var entry in hq.airbasesSorted)
        {
            var airbase = entry.airbase;
            
            if (airbase == null || airbase.disabled || !airbase.AnyHangarsAvailable())
                continue;
            
            if (GetAirbaseCongestion(airbase) <= limit)
                return true;
        }
        
        return false;
    }
    
    private static int GetAIAircraftDeficit(FactionHQ hq)
    {
        var friendlyPlayers = hq.factionPlayers.Count;
        var enemyPlayers = 0;
        
        foreach (var other in FactionRegistry.GetAllHQs())
            if (other != hq)
                enemyPlayers += other.GetPlayers(false).Count;
        
        var target = hq.AIAircraftLimit + enemyPlayers * hq.addAIPerEnemyPlayer -
                     friendlyPlayers * hq.reduceAIPerFriendlyPlayer;
        
        return Mathf.Max(0, Mathf.CeilToInt(target - hq.activeAIAircraft.Count));
    }
    
    // RefreshObstacles in vanilla has a typo, this fixes that
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.RefreshObstacles))]
    [HarmonyPrefix]
    private static bool RefreshObstaclesPrefix(AIPilotTaxiState __instance)
    {
        if (Time.timeSinceLevelLoad - __instance.lastObstacleRefresh < 4f)
            return false;
        
        __instance.lastObstacleRefresh = Time.timeSinceLevelLoad;
        __instance.obstacles.Clear();
        var airbase = __instance.airbase;
        var aircraft = __instance.aircraft;
        
        if (airbase == null || aircraft == null)
            return false;
        
        foreach (var item in airbase.ControlledAircraft)
        {
            if (item == null || item == aircraft)
                continue;
            
            // Vanilla here compares range between item.transform.position and item.transform.position, making this
            // whole thing redundant
            if (!FastMath.InRange(item.transform.position, aircraft.transform.position, 300f))
                continue;
            
            __instance.obstacles.Add(new Obstacle(item.transform, item.maxRadius, item.obstacleTop));
        }
        
        return false;
    }
    
    // Vanilla reacts to any plane in the entire forward 180 degree vision arc, narrowing this can reduce excessive
    // mutual braking on converging taxiways
    // Also handle AggressiveTakeoffPriority here where the #1 plane in take off position commits to taking off instead
    // of braking for other planes (those should be the ones braking for it)
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.CheckObstacles))]
    [HarmonyPrefix]
    private static bool CheckObstaclesPrefix(AIPilotTaxiState __instance)
    {
        if (Plugin.CachedAggressiveTakeoffPriority && HasTakeoffPriority(__instance))
        {
            __instance.brakeUrgency = 0f;
            __instance.obstacleAvoidVector = Vector3.zero;
            return false;
        }
        
        if (Time.timeSinceLevelLoad - __instance.lastObstacleCheck < 1f)
            return false;
        
        __instance.brakeUrgency = 0f;
        __instance.lastObstacleCheck = Time.timeSinceLevelLoad;
        __instance.obstacleAvoidVector = Vector3.zero;
        
        foreach (var obstacle in __instance.obstacles)
        {
            if (obstacle.Transform == null)
                continue;
            
            var vector = obstacle.Transform.position - __instance.aircraft.transform.position;
            var normalized = vector.normalized;
            var forwardDot = Vector3.Dot(normalized, __instance.aircraft.transform.forward);
            var opposingDot = Vector3.Dot(-normalized, obstacle.Transform.forward);
            
            // Don't apply cone changes to planes that are taxiing after landing, only those that are about to take off
            var avoidanceDot = __instance.aircraft.pilots[0].flightInfo.HasTakenOff
                ? 0f
                : Plugin.CachedObstacleAvoidanceDot;
            if (forwardDot < avoidanceDot)
                continue;
            
            var magnitude = vector.magnitude;
            magnitude -= obstacle.Radius + __instance.aircraft.maxRadius;
            if (magnitude < 50f && opposingDot > 0f)
            {
                if (!__instance.yielding && __instance.aircraft.pilots[0].flightInfo.HasTakenOff)
                {
                    __instance.yielding = true;
                    var yieldDirection = Vector3.RotateTowards(__instance.aircraft.transform.forward, -normalized,
                        Mathf.PI / 2f, 0f);
                    __instance.yieldPosition = __instance.aircraft.GlobalPosition() + yieldDirection * 50f;
                    break;
                }
                
                if (!(forwardDot > opposingDot))
                    break;
            }
            
            var brakingSpeed = Mathf.Sqrt(Mathf.Max(magnitude - 10f, 0f));
            
            __instance.brakeUrgency += __instance.aircraft.speed > brakingSpeed ? 1 : 0;
        }
        
        return false;
    }
    
    [HarmonyPatch(typeof(AIPilotTaxiState), nameof(AIPilotTaxiState.WaitingAtRunwayCrossing))]
    [HarmonyPrefix]
    private static bool WaitingAtRunwayCrossingPrefix(AIPilotTaxiState __instance, ref bool __result)
    {
        if (!Plugin.CachedAggressiveTakeoffPriority || !HasTakeoffPriority(__instance))
            return true;
        
        __instance.waitingAtRunwayCrossing = false;
        __result = false;
        return false;
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