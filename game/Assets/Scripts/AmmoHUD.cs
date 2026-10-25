using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Displays the locally owned player's replicated weapon ammo.</summary>
public class AmmoHUD : NetworkBehaviour
{
    private WeaponController _weapon;
    private GameObject _canvasRoot;
    private Text _ammoText;
    private Text _statusText;

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            BindLocalWeapon();
    }

    public override void OnNetworkDespawn()
    {
        UnbindWeapon();
        DestroyHud();
    }

    public override void OnGainedOwnership()
    {
        BindLocalWeapon();
    }

    public override void OnLostOwnership()
    {
        UnbindWeapon();
        DestroyHud();
    }

    public override void OnDestroy()
    {
        UnbindWeapon();
        DestroyHud();
        base.OnDestroy();
    }

    private void BindLocalWeapon()
    {
        if (!IsOwner || _weapon != null)
            return;

        _weapon = GetComponent<WeaponController>();
        if (_weapon == null)
        {
            Debug.LogError($"[AmmoHUD] Locally owned player '{name}' has no WeaponController.");
            return;
        }

        BuildHud();
        _weapon.AmmoInMag.OnValueChanged += HandleAmmoChanged;
        _weapon.ReserveAmmo.OnValueChanged += HandleAmmoChanged;
        _weapon.IsReloading.OnValueChanged += HandleReloadChanged;
        Refresh();
    }

    private void UnbindWeapon()
    {
        if (_weapon == null)
            return;

        _weapon.AmmoInMag.OnValueChanged -= HandleAmmoChanged;
        _weapon.ReserveAmmo.OnValueChanged -= HandleAmmoChanged;
        _weapon.IsReloading.OnValueChanged -= HandleReloadChanged;
        _weapon = null;
    }

    private void BuildHud()
    {
        if (_canvasRoot != null)
            return;

        _canvasRoot = new GameObject("AmmoHudCanvas");
        Canvas canvas = _canvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 95;

        CanvasScaler scaler = _canvasRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        Image panel = UIKit.Img(_canvasRoot.transform, "AmmoPanel",
            new Color(UIKit.Ink.r, UIKit.Ink.g, UIKit.Ink.b, 0.9f), UIKit.GradientLeft);
        UIKit.Place(panel.rectTransform, new Vector2(1f, 0f), new Vector2(-36f, 36f),
            new Vector2(330f, 112f));

        _ammoText = UIKit.Label(panel.transform, "30 / 120", 48, TextAnchor.MiddleRight,
            new Vector2(1f, 1f), new Vector2(-18f, -38f), new Vector2(292f, 58f),
            FontStyle.Bold, 0f, Color.white);

        _statusText = UIKit.Label(panel.transform, "", 19, TextAnchor.MiddleRight,
            new Vector2(1f, 0f), new Vector2(-18f, 15f), new Vector2(292f, 28f),
            FontStyle.Bold, 0f, UIKit.Amber);
        _statusText.gameObject.SetActive(false);
    }

    private void HandleAmmoChanged(int previous, int current)
    {
        Refresh();
    }

    private void HandleReloadChanged(bool previous, bool current)
    {
        Refresh();
    }

    private void Refresh()
    {
        if (_weapon == null || _ammoText == null || _statusText == null)
            return;

        int magazine = _weapon.AmmoInMag.Value;
        int reserve = _weapon.ReserveAmmo.Value;
        _ammoText.text = magazine + " / " + reserve;

        string status = "";
        Color statusColor = UIKit.Amber;
        if (_weapon.IsReloading.Value)
        {
            status = "RELOADING...";
        }
        else if (magazine <= 0 && reserve > 0)
        {
            status = "EMPTY — PRESS R";
        }
        else if (magazine <= 0 && reserve <= 0)
        {
            status = "OUT OF AMMO";
            statusColor = UIKit.Warn;
        }

        _statusText.text = status;
        _statusText.color = statusColor;
        _statusText.gameObject.SetActive(status.Length > 0);
    }

    private void DestroyHud()
    {
        if (_canvasRoot == null)
            return;

        Destroy(_canvasRoot);
        _canvasRoot = null;
        _ammoText = null;
        _statusText = null;
    }
}
