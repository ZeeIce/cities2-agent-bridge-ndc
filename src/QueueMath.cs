using System.Collections.Generic;

namespace CitiesIIAgentBridge
{
    // Pure queue resolution for traffic_get_queues; no game types, so it is unit-tested.
    // Approach adapted from LancerComet/cities-skylines-2-mcp (Apache-2.0), RequestHandlers.Vehicles.cs.
    internal static class QueueMath
    {
        // blocker[i] = index of the vehicle i waits behind, or -1. Each stopped vehicle is followed
        // along its blockers until a vehicle that is moving or unblocked (the queue head).
        // head[i] >= 0: the head's index; head[i] <= -2: member of deadlock loop (-2 - head[i]);
        // head[i] == -1: vehicle i is moving. Loops are vehicles blocking each other in a cycle.
        public static int[] ResolveHeads(int[] blocker, bool[] stopped, List<List<int>> loops)
        {
            int n = blocker.Length; var head = new int[n]; var state = new byte[n]; var path = new List<int>();
            for (int i = 0; i < n; i++) head[i] = -1;
            for (int i = 0; i < n; i++)
            {
                if (!stopped[i] || state[i] == 2) continue;
                path.Clear(); int j = i, result;
                while (true)
                {
                    if (state[j] == 2) { result = head[j]; break; }
                    if (state[j] == 1)
                    {
                        loops.Add(path.GetRange(path.IndexOf(j), path.Count - path.IndexOf(j)));
                        result = -2 - (loops.Count - 1); break;
                    }
                    state[j] = 1; path.Add(j);
                    int b = blocker[j];
                    if (b < 0 || b >= n || !stopped[b]) { result = j; break; }
                    j = b;
                }
                foreach (int p in path) { state[p] = 2; head[p] = result; }
            }
            return head;
        }
    }
}
