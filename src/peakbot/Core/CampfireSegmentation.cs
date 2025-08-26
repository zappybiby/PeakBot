// /Core/CampfireSegmentation.cs
// Builds a world-space “biome slice” AABB using campfires as vertical cut lines.
// The slice spans the FULL world on the cross-axis, and is clamped on the
// primary axis (X or Z) to the region between the two campfires that bracket
// the player (or to the midpoints to neighbors, if BandUnion mode is used).

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Peak.BotClone
{
    internal static class CampfireSegmentation
    {
        public enum SliceMode
        {
            // Clamp strictly between the two bracketing campfires (±axisPad).
            StrictBetween = 0,
            // Use the Voronoi band: halfway to the neighbor on each side (±axisPad).
            BandUnion = 1,
        }

        internal struct CfInfo
        {
            public string  Segment;
            public Vector3 WorldPos;
        }

        /// <summary>
        /// Compute a “biome slice” AABB that covers the whole world in the cross-axis,
        /// and only the region between the two campfires bracketing the player on the
        /// primary (X or Z) axis. The primary axis is chosen by larger variance across
        /// campfire positions. The result is clamped to world bounds and padded in Y.
        /// </summary>
        /// <returns>true on success; false if world/campfires not available.</returns>
        public static bool TryBuildBiomeSlice(
            Vector3 playerPos,
            out Bounds slice,
            out string segLower, out string segUpper,
            SliceMode mode = SliceMode.BandUnion,
            float axisPad = 20f,
            float yDownPad = 10f,
            float yUpPad = 15f)
        {
            slice = default;
            segLower = segUpper = "?";

            if (!TryGetWorldBounds(out var world)) return false;

            var cfs = GetActiveCampfires();
            if (cfs.Count < 2) return false;

            // Choose primary horizontal axis by variance
            float meanX = cfs.Average(c => c.WorldPos.x);
            float meanZ = cfs.Average(c => c.WorldPos.z);
            float varX  = cfs.Sum(c => (c.WorldPos.x - meanX) * (c.WorldPos.x - meanX));
            float varZ  = cfs.Sum(c => (c.WorldPos.z - meanZ) * (c.WorldPos.z - meanZ));
            bool useZ   = varZ >= varX;

            // Project onto axis and sort
            var ordered = cfs
                .Select(cf => new { cf, s = useZ ? cf.WorldPos.z : cf.WorldPos.x })
                .OrderBy(p => p.s)
                .ToArray();

            float sPlayer = useZ ? playerPos.z : playerPos.x;

            // Find bracketing indices k, k+1 (clamp to ends)
            int k = 0;
            while (k + 1 < ordered.Length && !(ordered[k].s <= sPlayer && sPlayer <= ordered[k + 1].s)) k++;
            if (k >= ordered.Length - 1) k = ordered.Length - 2;

            var lo = ordered[k];
            var hi = ordered[k + 1];
            segLower = lo.cf.Segment;
            segUpper = hi.cf.Segment;

            // Determine slice limits along the primary axis
            float sMin, sMax;
            if (mode == SliceMode.StrictBetween)
            {
                sMin = lo.s - axisPad;
                sMax = hi.s + axisPad;
            }
            else // BandUnion
            {
                float leftMid  = (k > 0)                  ? 0.5f * (ordered[k - 1].s + lo.s)
                                                           : (useZ ? world.min.z : world.min.x);
                float rightMid = (k + 2 < ordered.Length) ? 0.5f * (ordered[k + 1].s + ordered[k + 2].s)
                                                           : (useZ ? world.max.z : world.max.x);
                sMin = leftMid  - axisPad;
                sMax = rightMid + axisPad;
            }

            // Build AABB: full world on cross-axis, clamp on primary axis; pad Y
            Vector3 min = world.min, max = world.max;
            if (useZ) { min.z = Mathf.Max(min.z, sMin); max.z = Mathf.Min(max.z, sMax); }
            else      { min.x = Mathf.Max(min.x, sMin); max.x = Mathf.Min(max.x, sMax); }

            min.y -= yDownPad; max.y += yUpPad;
            if (min.x > max.x || min.y > max.y || min.z > max.z) return false;

            slice = new Bounds((min + max) * 0.5f, max - min);

            // Ensure practical minimum thickness for tiny gaps
            var size = slice.size;
            if (useZ) size.z = Mathf.Max(size.z, 40f);
            else      size.x = Mathf.Max(size.x, 40f);
            size.y = Mathf.Max(size.y, 20f);
            slice.size = size;

            return true;
        }

        // ---------- Internals: world bounds + campfire discovery ----------

        private static bool TryGetWorldBounds(out Bounds bounds)
        {
            int geometryMask = LayerMask.GetMask("Terrain", "Map", "Default");
            var cols = Object.FindObjectsOfType<Collider>()
                             .Where(c => c && ((geometryMask & (1 << c.gameObject.layer)) != 0))
                             .ToList();
            if (cols.Count == 0)
            {
                bounds = default;
                return false;
            }
            bounds = cols[0].bounds;
            for (int i = 1; i < cols.Count; i++) bounds.Encapsulate(cols[i].bounds);
            return true;
        }

        private static List<CfInfo> GetActiveCampfires()
        {
            var list = new List<CfInfo>();
            try
            {
                var mapGO = GameObject.Find("Map");
                if (!mapGO) return list;

                var root = mapGO.transform;
                var nested = root.Find("Map");      // handle Map/Map nesting
                var searchRoot = nested ? nested : root;

                var campfires = searchRoot.GetComponentsInChildren<Campfire>(true);
                foreach (var cf in campfires)
                {
                    if (!HasTrue(cf, "didStart") || !HasTrue(cf, "didAwake")) continue;
                    list.Add(new CfInfo
                    {
                        Segment  = cf.advanceToSegment.ToString(),
                        WorldPos = cf.transform.position
                    });
                }
            }
            catch { }
            return list;
        }

        private static bool HasTrue(object obj, string name)
        {
            try
            {
                var t = obj.GetType();
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (p != null && p.CanRead && p.PropertyType == typeof(bool))
                    return (bool)p.GetValue(obj, null);

                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (f != null && f.FieldType == typeof(bool))
                    return (bool)f.GetValue(obj);
            }
            catch { }
            return false;
        }
    }
}
