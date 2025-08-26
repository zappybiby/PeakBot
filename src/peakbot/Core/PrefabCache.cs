// /Core/PrefabCache.cs
using Photon.Pun;
using System.Collections.Generic;
using UnityEngine;

namespace Peak.BotClone
{
    internal static class PrefabCache
    {
        static GameObject? cached;  // nullable
        public static GameObject? Prefab => cached;

        public static void Cache(string id)
        {
            if (cached) return;

#pragma warning disable CS0618
            var spawner = Object.FindObjectOfType<BotSpawner>();
            if (spawner && spawner.botPrefab) cached = spawner.botPrefab;
            if (!cached)
            {
                var anyBot = Object.FindObjectOfType<Bot>();
                if (anyBot) cached = anyBot.gameObject;
            }
#pragma warning restore CS0618

            if (!cached) return;

            if (PhotonNetwork.PrefabPool is DefaultPool def)
            {
                def.ResourceCache[id] = cached;
                return;
            }
            if (!(PhotonNetwork.PrefabPool is RuntimePool))
                PhotonNetwork.PrefabPool = new RuntimePool(id, cached, PhotonNetwork.PrefabPool);
        }

        sealed class RuntimePool : IPunPrefabPool
        {
            readonly Dictionary<string, GameObject> map = new();
            readonly IPunPrefabPool? fallback;
            public RuntimePool(string id, GameObject prefab, IPunPrefabPool oldPool){ map[id] = prefab; fallback = oldPool; }
            public GameObject Instantiate(string id, Vector3 pos, Quaternion rot)
            {
                if (map.TryGetValue(id, out var p)) return Object.Instantiate(p, pos, rot);
                var obj = fallback?.Instantiate(id, pos, rot);
                if (obj) return obj;
                var res = Resources.Load<GameObject>(id);
                return res ? Object.Instantiate(res, pos, rot) : null;
            }
            public void Destroy(GameObject go)
            {
                if (fallback != null) fallback.Destroy(go);
                else Object.Destroy(go);
            }
        }
    }
}
