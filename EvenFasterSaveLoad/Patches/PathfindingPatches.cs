using System;
using System.Collections.Generic;
using HarmonyLib;
using NPC;
using Pathfinding;
using UnityEngine;
using Connection = NPC.WaypointGraph.WaypointConnection;

namespace EvenFasterSaveLoad.Patches
{
    [HarmonyPatch]
    internal static class PathfindingPatches
    {
        // Scenes ship with a single path thread, which makes the waypoint batch below sequential anyway.
        [HarmonyPrefix, HarmonyPatch(typeof(AstarPath), nameof(AstarPath.InitializePathProcessor))]
        private static void MorePathThreads(AstarPath __instance)
        {
            int wanted = Plugin.PathfindingThreads.Value;
            if (!Plugin.ModEnabled || wanted == 1 || __instance.threadCount != ThreadCount.One)
                return;
            // ThreadCount only names values up to Eight, but the path processor takes any count.
            __instance.threadCount = (ThreadCount)(wanted > 0 ? wanted : Mathf.Clamp(SystemInfo.processorCount - 1, 1, 16));
        }

        /// <summary>
        /// WaypointGraph.MapWaypoints runs on every scene load and computes a path for every pair of connected
        /// waypoint clusters, blocking on each one before requesting the next. This version builds the same
        /// graph but requests all paths first and collects the results afterwards.
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(typeof(WaypointGraph), nameof(WaypointGraph.MapWaypoints))]
        private static bool MapWaypoints(WaypointGraph __instance)
        {
            if (!Plugin.On(Plugin.BatchedWaypoints) || !Application.isPlaying)
                return true;
            if (AstarPath.active == null)
            {
                Debug.LogWarning("NO NAVMESH GENERATED!");
                return false;
            }

            Debug.Log("Mapping waypoints...");
            WaypointGraph.instance = __instance;
            lock (__instance._graphLock)
            {
                __instance._waypointMap = new List<Connection>();
                BuildClusters();
                Debug.Log("Map contains " + __instance._waypointMap.Count + " waypoint clusters");

                var requested = new List<Connection.Interconnection>();
                foreach (Connection cluster in __instance._waypointMap)
                    cluster.connectsTo = Interconnect(__instance, cluster, requested).ToArray();

                foreach (Connection.Interconnection link in requested)
                {
                    ABPath path = link.cachedPath;
                    path.BlockUntilCalculated();
                    link.distance = path.error ? float.PositiveInfinity : WaypointGraph.PathDistance(path.vectorPath, path.path);
                }
            }
            return false;
        }

        // The WaypointConnection constructors register themselves in the graph's map.
        private static void BuildClusters()
        {
            // FindObjectsOfType (what vanilla calls) returns objects in instance-id order; region ids depend on it.
            foreach (Waypoint waypoint in UnityEngine.Object.FindObjectsByType<Waypoint>(FindObjectsSortMode.InstanceID))
            {
                if (waypoint.previousWaypoint == null && waypoint.nextWaypoint != null)
                    new Connection(waypoint, waypoint.GetEndOfChain(Waypoint.Direction.forward));
                else if (waypoint is Waypoint_LevelChange door && door.LinkedDoor != null)
                    new Connection(door, door.LinkedDoor.OtherLevel, door.LinkedDoor.OtherEntrypoint);
            }
        }

        private static List<Connection.Interconnection> Interconnect(WaypointGraph graph, Connection cluster, List<Connection.Interconnection> requested)
        {
            var links = new List<Connection.Interconnection>();
            var regionsDone = new HashSet<int>();
            foreach (Connection.ConnectionNode from in cluster.waypointNodes)
            {
                int region = from.regionId;
                if (!regionsDone.Add(region))
                    continue;
                foreach (Connection other in graph.FindConnectionsByRegion(region, cluster))
                {
                    Connection.ConnectionNode to = graph.GetNodeByRegion(region, other);
                    ABPath path = ABPath.Construct(from.waypoint.transform.position, to.waypoint.transform.position);
                    AstarPath.StartPath(path);

                    var link = new Connection.Interconnection
                    {
                        toRegion = Array.Find(other.waypointNodes, n => n.regionId == region).regionId,
                        waypointConnection = other,
                        cachedPath = path,
                        startNode = from,
                        startNodePos = from.waypoint ? from.waypoint.transform.position : default,
                        endNode = to,
                        endNodePos = to.waypoint ? to.waypoint.transform.position : default,
                    };
                    links.Add(link);
                    requested.Add(link);
                }
            }
            return links;
        }
    }
}
