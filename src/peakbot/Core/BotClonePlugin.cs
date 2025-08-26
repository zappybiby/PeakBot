// /Core/BotClonePlugin.cs
// Bootstraps runtime NavMesh + NavPoints generation. Builds only the
// “biome slice” between the two campfires bracketing the player.

using System.Collections;
using BepInEx;
using Photon.Pun;
using UnityEngine;

namespace Peak.BotClone
{
    [BepInPlugin("pharmacomaniac.botclone.graph", "Bot Clone (NavMesh+Graph)", "4.9.0")]
    public class BotClonePlugin : BaseUnityPlugin
    {
        [SerializeField] private Peak.BotClone.Config.BotCloneSettings? settings = null;

        // Slice params (tune to taste)
        private const CampfireSegmentation.SliceMode SLICE_MODE = CampfireSegmentation.SliceMode.StrictBetween;
        private const float SLICE_AXIS_PAD  = 20f;  // small safety past the cut lines
        private const float SLICE_Y_PAD_DN  = 10f;
        private const float SLICE_Y_PAD_UP  = 15f;


        // Recenter checks
        private const float WATCH_INTERVAL = 2.0f;            // seconds
        private const float RECENTER_EDGE_FRACTION = 0.15f;   // near-edge threshold

        // State for the watcher
        private Bounds? _currentSlice;
        private string _curSegA = "?";
        private string _curSegB = "?";

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
            StartCoroutine(Bootstrap());
        }

        void OnDestroy() => PhotonNetwork.RemoveCallbackTarget(this);

        IEnumerator Bootstrap()
        {
            Logger.LogInfo("[BotClone] Waiting for level load...");
            while (!PhotonNetwork.InRoom
                   || Photon.Pun.SceneManagerHelper.ActiveSceneName == "Airport"
                   || Character.localCharacter == null
                   || LoadingScreenHandler.loading)
            {
                yield return null;
            }

            Logger.LogInfo("[BotClone] Level loaded.");
            var runtimeNavMesh = gameObject.AddComponent<RuntimeNavMesh>();

            // Build the biome slice (campfire-based band of the whole map)
            if (CampfireSegmentation.TryBuildBiomeSlice(
                    Character.localCharacter.Center,
                    out Bounds slice, out string segA, out string segB,
                    SLICE_MODE, SLICE_AXIS_PAD, SLICE_Y_PAD_DN, SLICE_Y_PAD_UP))
            {
                _currentSlice = slice; _curSegA = segA; _curSegB = segB;

                Logger.LogInfo($"[BotClone] Using biome slice {segA} ↔ {segB} | center={slice.center} size={slice.size}");

                yield return StartCoroutine(runtimeNavMesh.BakeNavMesh(slice));
                Logger.LogInfo("[BotClone] NavMesh baked (bounded). Generating NavPoints…");
                yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync(slice));
            }
            else
            {
                Logger.LogWarning("[BotClone] Could not resolve campfire slice; falling back to full bake.");
                yield return StartCoroutine(runtimeNavMesh.BakeNavMesh());
                Logger.LogInfo("[BotClone] NavMesh baked (full). Generating NavPoints…");
                yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync());
            }

            PrefabCache.Cache(settings?.botPrefabName ?? "Character_Bot");
            Logger.LogInfo("[BotClone] Initialised — press F10 to spawn clone.");

            // Keep the slice centered as the player advances
            StartCoroutine(SliceWatcher(runtimeNavMesh));
            yield break;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10) || (UnityEngine.InputSystem.Keyboard.current?.f10Key.wasPressedThisFrame ?? false))
                CloneSpawner.Spawn(settings, this.Logger);
        }

        // -- Re-center the bake when the player crosses into a new campfire band or nears edges.
        private IEnumerator SliceWatcher(RuntimeNavMesh nav)
        {
            while (true)
            {
                yield return new WaitForSeconds(WATCH_INTERVAL);
                var me = Character.localCharacter; if (!me) continue;

                if (CampfireSegmentation.TryBuildBiomeSlice(
                        me.Center,
                        out Bounds newSlice, out string segA, out string segB,
                        SLICE_MODE, SLICE_AXIS_PAD, SLICE_Y_PAD_DN, SLICE_Y_PAD_UP))
                {
                    bool pairChanged  = (segA != _curSegA) || (segB != _curSegB);
                    bool playerOutside = !_currentSlice.HasValue || !_currentSlice.Value.Contains(me.Center);
                    bool nearEdge     = _currentSlice.HasValue && IsNearEdge(_currentSlice.Value, me.Center, RECENTER_EDGE_FRACTION);
                    bool shapeShifted = !_currentSlice.HasValue || SignificantBoundsDelta(_currentSlice.Value, newSlice);

                    if (pairChanged || playerOutside || nearEdge || shapeShifted)
                    {
                        _currentSlice = newSlice; _curSegA = segA; _curSegB = segB;

                        Logger.LogInfo($"[BotClone] Re-centering slice to {segA} ↔ {segB} | center={newSlice.center} size={newSlice.size}");
                        yield return StartCoroutine(nav.BakeNavMesh(newSlice));
                        yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync(newSlice));
                    }
                }
            }
        }

        private static bool IsNearEdge(Bounds b, Vector3 p, float edgeFrac)
        {
            Vector3 min = b.min, max = b.max;
            float nx = Mathf.InverseLerp(min.x, max.x, p.x);
            float nz = Mathf.InverseLerp(min.z, max.z, p.z);
            bool nearX = nx <= edgeFrac || nx >= 1f - edgeFrac;
            bool nearZ = nz <= edgeFrac || nz >= 1f - edgeFrac;
            return nearX || nearZ;
        }

        private static bool SignificantBoundsDelta(Bounds a, Bounds b)
        {
            float centerDelta = Vector3.Distance(a.center, b.center);
            Vector3 sizeDelta = new Vector3(Mathf.Abs(a.size.x - b.size.x),
                                            Mathf.Abs(a.size.y - b.size.y),
                                            Mathf.Abs(a.size.z - b.size.z));
            return centerDelta > 25f || sizeDelta.sqrMagnitude > 25f * 25f;
        }
    }
}
