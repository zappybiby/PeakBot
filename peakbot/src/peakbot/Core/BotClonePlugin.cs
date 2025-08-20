// /Core/BotClonePlugin.cs
using BepInEx;
using Photon.Pun;
using System.Collections;
using UnityEngine;

namespace Peak.BotClone
{
    [BepInPlugin("pharmacomaniac.botclone.graph", "Bot Clone (NavMesh+Graph)", "4.7.0")]
    public class BotClonePlugin : BaseUnityPlugin
    {
        [SerializeField] Peak.BotClone.Config.BotCloneSettings? settings;  // nullable

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
            StartCoroutine(Bootstrap());
        }
        void OnDestroy() => PhotonNetwork.RemoveCallbackTarget(this);

        IEnumerator Bootstrap()
        {
            Logger.LogInfo("[BotClone] Waiting for level load...");
            while (!PhotonNetwork.InRoom || Photon.Pun.SceneManagerHelper.ActiveSceneName == "Airport" || Character.localCharacter == null || LoadingScreenHandler.loading)
                yield return null;

            Logger.LogInfo("[BotClone] Level loaded. Starting NavMesh bake...");
            var runtimeNavMesh = gameObject.AddComponent<RuntimeNavMesh>();
            yield return StartCoroutine(runtimeNavMesh.BakeNavMesh());

            Logger.LogInfo("[BotClone] NavMesh baked. Generating NavPoints asynchronously...");
            yield return StartCoroutine(RuntimeNavPointGenerator.GenerateAsync());

            PrefabCache.Cache(settings?.botPrefabName ?? "Character_Bot");
            Logger.LogInfo("[BotClone] Initialised — press F10 to spawn clone.");
            yield break;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F10) || (UnityEngine.InputSystem.Keyboard.current?.f10Key.wasPressedThisFrame ?? false))
                CloneSpawner.Spawn(settings, this.Logger); // accepts null now
        }
    }
}