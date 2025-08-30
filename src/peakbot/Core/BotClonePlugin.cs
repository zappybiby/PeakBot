// /Core/BotClonePlugin.cs
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

        // Slice configuration
        private const CampfireSegmentation.SliceMode SLICE_MODE = CampfireSegmentation.SliceMode.StrictBetween;
        private const float SLICE_AXIS_PAD = 20f;
        private const float SLICE_Y_PAD_DN = 10f;
        private const float SLICE_Y_PAD_UP = 15f;

        private bool _building;
        private RuntimeNavMesh _runtimeNavMesh = null!;

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
        }

        void OnDestroy()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10) || (UnityEngine.InputSystem.Keyboard.current?.f10Key.wasPressedThisFrame ?? false))
            {
                if (!_building) StartCoroutine(BuildSliceAndSpawnNow());
            }
        }

        private IEnumerator BuildSliceAndSpawnNow()
        {
            _building = true;
            Logger.LogInfo("[BotClone] Building slice and spawning clone…");

            // Wait until scene/player are ready
            while (!PhotonNetwork.InRoom
                   || Photon.Pun.SceneManagerHelper.ActiveSceneName == "Airport"
                   || Character.localCharacter == null
                   || LoadingScreenHandler.loading)
            {
                yield return null;
            }

            // Ensure RuntimeNavMesh exists
            _runtimeNavMesh = _runtimeNavMesh ? _runtimeNavMesh : gameObject.AddComponent<RuntimeNavMesh>();

            // Determine slice at press time
            var me = Character.localCharacter;
            Bounds slice;
            bool haveSlice = CampfireSegmentation.TryBuildBiomeSlice(
                                me.Center,
                                out slice, out string segA, out string segB,
                                SLICE_MODE, SLICE_AXIS_PAD, SLICE_Y_PAD_DN, SLICE_Y_PAD_UP);

            if (haveSlice)
            {
                Logger.LogInfo($"[BotClone] Slice {segA} ↔ {segB} | center={slice.center} size={slice.size}");
                yield return StartCoroutine(_runtimeNavMesh.BakeNavMesh(slice));
                Logger.LogInfo("[BotClone] Generating NavPoints…");
                yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync(slice));
            }
            else
            {
                Logger.LogWarning("[BotClone] Slice resolve failed; full bake fallback.");
                yield return StartCoroutine(_runtimeNavMesh.BakeNavMesh());
                Logger.LogInfo("[BotClone] Generating NavPoints (full) …");
                yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync());
            }

            PrefabCache.Cache(settings?.botPrefabName ?? "Character_Bot");

            var clone = CloneSpawner.Spawn(settings, this.Logger);
            if (haveSlice && clone)
                StartCoroutine(CoSliceGuard(clone, slice, me));

            _building = false;
        }

        // Despawn if either player or bot moves outside the built slice
        private IEnumerator CoSliceGuard(GameObject clone, Bounds slice, Character player)
        {
            const float edgeGrace = 0.5f;

            while (clone && player)
            {
                var botCh = clone.GetComponent<Character>();
                if (!botCh) break;

                if (!ContainsWithGrace(slice, player.Center, edgeGrace) ||
                    !ContainsWithGrace(slice, botCh.Center, edgeGrace))
                {
                    Logger.LogInfo("[BotClone] Out of slice — despawning.");
                    if (PhotonNetwork.InRoom) PhotonNetwork.Destroy(clone);
                    else Destroy(clone);
                    yield break;
                }
                yield return null;
            }
        }

        private static bool ContainsWithGrace(Bounds b, Vector3 p, float pad)
        {
            var bb = b;
            bb.Expand(new Vector3(-2f * pad, 0f, -2f * pad));
            var test = new Vector3(p.x, Mathf.Clamp(p.y, bb.min.y, bb.max.y), p.z);
            return bb.Contains(test);
        }
    }
}