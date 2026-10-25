using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

public class PlayerViewSetup : NetworkBehaviour
{
    [SerializeField] private Camera playerCamera;
    [SerializeField] private AudioListener audioListener;
    [SerializeField] private GameObject viewModel;   // assault2 (first-person gun)
    [SerializeField] private Transform bodyRoot;     // Body
    [SerializeField] private GameObject worldGun;    // gun under the RightHand bone

    public override void OnNetworkSpawn()
    {
        bool mine = IsOwner;

        if (playerCamera != null) playerCamera.enabled = mine;
        if (audioListener != null) audioListener.enabled = mine;

        if (worldGun != null) worldGun.SetActive(true);

        if (viewModel != null)
        {
            viewModel.SetActive(mine);
            foreach (var r in viewModel.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = ShadowCastingMode.Off;
        }

        if (bodyRoot != null)
        {
            var mode = mine ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            foreach (var r in bodyRoot.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = mode;
        }

        if (mine)
            Debug.Log($"[InputDiag][PlayerView] Local view setup applied; ownerClientId={OwnerClientId}, componentEnabled={enabled}, cameraEnabled={(playerCamera != null && playerCamera.enabled)}, viewModelActive={(viewModel != null && viewModel.activeInHierarchy)}, worldGunActive={(worldGun != null && worldGun.activeInHierarchy)}.");
    }
}