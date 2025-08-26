// /Core/CosmeticReplicator.cs
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using System.Collections.Generic;
using UnityEngine;
using Zorro.Core;

namespace Peak.BotClone
{
    internal class CosmeticReplicator : MonoBehaviour, IOnEventCallback
    {
        const byte EVT_SKIN = 199;

        public void BroadcastFromLocal()
        {
            var save = GameHandler.GetService<PersistentPlayerDataService>()
                                  .GetPlayerData(PhotonNetwork.LocalPlayer).customizationData;
            object[] payload =
            {
                GetComponent<PhotonView>().ViewID,
                save.currentSkin, save.currentOutfit, save.currentHat,
                save.currentEyes, save.currentMouth, save.currentAccessory
            };
            PhotonNetwork.RaiseEvent(EVT_SKIN, payload,
                new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            PhotonNetwork.AddCallbackTarget(this);
        }
        void OnDestroy() => PhotonNetwork.RemoveCallbackTarget(this);

        public void OnEvent(EventData e)
        {
            if (e.Code != EVT_SKIN) return;
            var d = (object[])e.CustomData;
            var view = PhotonView.Find((int)d[0]);
            if (!view) return;
            var refs = view.GetComponentInChildren<CustomizationRefs>();
            if (!refs) return;
            ApplyAppearance(refs, (int)d[1], (int)d[2], (int)d[3], (int)d[4], (int)d[5], (int)d[6]);
        }

        static void ApplyAppearance(CustomizationRefs r, int skin, int fit, int hat, int eyes, int mouth, int acc)
        {
            var shop = Singleton<Customization>.Instance;
            var tint = shop.skins[skin].color;

            foreach (var rd in r.PlayerRenderers) rd.material.SetColor("_SkinColor", tint);
            foreach (var rd in r.EyeRenderers)    rd.material.SetColor("_SkinColor", tint);

            r.mainRenderer.sharedMesh = shop.fits[fit].fitMesh;
            r.mainRenderer.SetSharedMaterials(new List<Material>
            {
                r.mainRenderer.materials[0],
                shop.fits[fit].fitMaterial,
                shop.fits[fit].fitMaterialShoes
            });

            bool skirt = shop.fits[fit].isSkirt;
            r.skirt.gameObject.SetActive(skirt);
            r.shorts.gameObject.SetActive(!skirt);
            var pantsMat = shop.fits[fit].fitPantsMaterial;
            if (skirt) r.skirt.sharedMaterial = pantsMat; else r.shorts.sharedMaterial = pantsMat;

            for (int i = 0; i < r.playerHats.Length; i++) r.playerHats[i].gameObject.SetActive(i == hat);
            if (hat < r.playerHats.Length) r.playerHats[hat].material = shop.fits[fit].fitHatMaterial;
            foreach (var rd in r.EyeRenderers) rd.material.mainTexture   = shop.eyes[eyes].texture;
            r.mouthRenderer.material.mainTexture      = shop.mouths[mouth].texture;
            r.accessoryRenderer.material.mainTexture  = shop.accessories[acc].texture;
        }
    }
}
