using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player root. Server-authoritative hitscan rifle (NEW INPUT SYSTEM).
///  - Fire rate in rounds-per-minute, server enforces it
///  - Magazine + reserve ammo + timed reload (R key or "Player/Reload" action)
///  - Camera recoil (kick + recovery) and spread bloom
///  - Tracer / impact VFX broadcast to everyone
/// Right-click the component header (3 dots) > "Preset: AKM / M416" to load stats.
/// </summary>
public class WeaponController : NetworkBehaviour
{
    // ---------------------------------------------------------------- INPUT
    [Header("Input (New Input System)")]
    [SerializeField] private InputActionAsset inputActions;       // same asset PlayerController uses
    public string attackActionPath = "Player/Attack";
    public string reloadActionPath = "Player/Reload";             // optional - falls back to reloadKey if the action doesn't exist
    public Key reloadKey = Key.R;
    public bool automatic = true;                                 // hold to spray
    public bool requireLockedCursor = false;

    // ----------------------------------------------------------- REFERENCES
    [Header("References")]
    [SerializeField] private Transform muzzlePoint;               // first-person barrel tip (on assault1)
    [SerializeField] private Transform worldMuzzlePoint;          // barrel tip on the hand-held gun (seen by others)
    [SerializeField] private Camera playerCamera;
    [SerializeField] private MuzzleFlashController muzzleFlash;
    [SerializeField] private LineRenderer tracerPrefab;           // do NOT put TracerFade on this prefab
    [SerializeField] private ParticleSystem impactVFXPrefab;
    [SerializeField] private GameObject scorchDecalPrefab;

    // ------------------------------------------------------------ FIRE RATE
    [Header("Fire Rate & Damage")]
    [Tooltip("Rounds per minute. AKM ~600, M416 ~700-750, SCAR ~600.")]
    public float roundsPerMinute = 600f;
    [Tooltip("Damage per bullet. 100 HP players: 30 = 4 shots to kill.")]
    public float damage = 30f;
    public float range = 150f;
    public LayerMask hitMask = ~0;
    [Tooltip("OFF = bullets do no damage to players on your own team.")]
    public bool friendlyFire = false;
    [Tooltip("Server accepts a shot if at least this fraction of the fire interval has passed (absorbs network jitter).")]
    [Range(0.5f, 1f)] public float serverFireRateTolerance = 0.8f;

    // ----------------------------------------------------------------- AMMO
    [Header("Ammo & Reload")]
    public int magazineSize = 30;
    public int startingReserveAmmo = 120;                         // 4 spare mags
    [Tooltip("Reload with bullets still in the mag (seconds).")]
    public float reloadTime = 2.5f;
    [Tooltip("Reload with an empty mag (seconds) - a bit longer.")]
    public float emptyReloadTime = 3.0f;
    public bool autoReloadOnEmpty = true;

    // --------------------------------------------------------------- RECOIL
    [Header("Recoil (degrees)")]
    [Tooltip("Camera kick UP per shot.")]
    public float verticalRecoil = 0.9f;
    [Tooltip("Random left/right kick per shot (+/-).")]
    public float horizontalRecoil = 0.45f;
    public float maxVerticalRecoil = 12f;
    public float maxHorizontalRecoil = 5f;
    [Tooltip("How fast the camera follows the kick. Higher = snappier.")]
    public float recoilSnappiness = 25f;
    [Tooltip("Seconds after the last shot before the camera starts settling back.")]
    public float recoilRecoveryDelay = 0.12f;
    [Tooltip("Degrees per second the camera settles back.")]
    public float recoilRecoverySpeed = 10f;

    [Header("Spread / Bloom (degrees)")]
    public float baseSpread = 0.1f;
    public float spreadPerShot = 0.06f;
    public float maxSpread = 1.5f;
    public float spreadRecoverySpeed = 3f;

    [Header("Debug")]
    public bool debugLogs = true;
    public float maxOriginOffset = 3f;                            // server rejects shots from further than this from the player
    [Tooltip("Display only - editing does nothing.")]
    [SerializeField] private int debugAmmoInMag;
    [SerializeField] private int debugReserveAmmo;
    [SerializeField] private bool debugIsReloading;
    [SerializeField] private float debugCurrentSpread;

    // --------------------------------------------------------------- STATE
    public NetworkVariable<int> AmmoInMag = new NetworkVariable<int>(30,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> ReserveAmmo = new NetworkVariable<int>(120,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> IsReloading = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>(ammoInMag, reserve) - hook your ammo UI here.</summary>
    public event Action<int, int> OnAmmoChanged;
    public event Action OnDryFire;
    public float FireInterval => 60f / Mathf.Max(1f, roundsPerMinute);
    public float CurrentSpread => baseSpread + _bloom;

    private InputAction _attack, _reload;
    private Health _ownHealth;
    private PlayerTeam _ownTeam;
    private readonly Dictionary<string, float> _lastDiagnosticLogTime = new Dictionary<string, float>();
    private bool _wasAttackPressed;
    private bool _reloadStuckReported;
    private float _reloadDiagnosticStartTime;

    // owner-side
    private float _nextFireTime;
    private float _lastShotTime = -999f;
    private float _reloadGuardUntil;
    private int _predictedAmmo;
    private Vector2 _recoilTarget, _recoilCurrent;   // x = pitch up, y = yaw
    private float _bloom;
    private Quaternion _camBaseRot = Quaternion.identity;

    // server-side
    private float _serverLastShotTime = -999f;
    private float _reloadEndTime;

    // ------------------------------------------------------------ LIFECYCLE
    public override void OnNetworkSpawn()
    {
        _ownHealth = GetComponent<Health>();
        _ownTeam = GetComponent<PlayerTeam>();
        AmmoInMag.OnValueChanged += HandleAmmoChanged;
        ReserveAmmo.OnValueChanged += HandleReserveChanged;
        IsReloading.OnValueChanged += HandleReloadStateChanged;

        LogDiagnostic("spawn", $"Spawned enabled={enabled}, active={gameObject.activeInHierarchy}, owner={IsOwner}, server={IsServer}, ownerClientId={OwnerClientId}, playerCamera={(playerCamera == null ? "missing" : $"enabled={playerCamera.enabled}")}, {GetNetworkDiagnosticState()}");

        if (IsServer) RefillAmmo();

        if (!IsOwner)
        {
            LogDiagnostic("remote-ownership", "Weapon input is inactive because this player object is not locally owned.");
            return;
        }

        _predictedAmmo = AmmoInMag.Value;
        if (playerCamera != null) _camBaseRot = playerCamera.transform.localRotation;
        else LogDiagnostic("missing-camera", "Weapon input will not run because the player camera reference is missing.", true);

        if (inputActions != null)
        {
            _attack = inputActions.FindAction(attackActionPath, false);
            _reload = inputActions.FindAction(reloadActionPath, false);
            _attack?.Enable();
            _reload?.Enable();
            if (_attack == null) Debug.LogWarning($"[Weapon] Action '{attackActionPath}' not found - using Mouse left button.");
            if (_reload == null) Debug.Log($"[Weapon] Action '{reloadActionPath}' not found - using key {reloadKey}.");
            LogDiagnostic("input-setup", $"Using asset '{inputActions.name}' directly; attack={DescribeAction(_attack)}, reload={DescribeAction(_reload)}.");
        }
        else Debug.LogWarning("[Weapon] No InputActionAsset assigned - using Mouse left button + reload key.");

        if (IsReloading.Value)
        {
            _reloadDiagnosticStartTime = Time.unscaledTime;
            _reloadStuckReported = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        AmmoInMag.OnValueChanged -= HandleAmmoChanged;
        ReserveAmmo.OnValueChanged -= HandleReserveChanged;
        IsReloading.OnValueChanged -= HandleReloadStateChanged;
        if (IsOwner)
            LogDiagnostic("despawn", $"Local weapon despawned; enabled={enabled}, active={gameObject.activeInHierarchy}, {GetNetworkDiagnosticState()}");
        if (IsOwner && playerCamera != null) playerCamera.transform.localRotation = _camBaseRot;
    }

    public override void OnGainedOwnership()
    {
        LogDiagnostic("ownership-gained", $"Local ownership gained; enabled={enabled}, active={gameObject.activeInHierarchy}, inputActions={(inputActions != null ? inputActions.name : "missing")}, {GetNetworkDiagnosticState()}");
    }

    public override void OnLostOwnership()
    {
        LogDiagnostic("ownership-lost", $"Local ownership lost; enabled={enabled}, active={gameObject.activeInHierarchy}, {GetNetworkDiagnosticState()}", true);
    }

    private void OnDisable()
    {
        if (IsSpawned && IsOwner)
            LogDiagnostic("component-disabled", $"WeaponController component disabled while spawned; active={gameObject.activeInHierarchy}, {GetNetworkDiagnosticState()}", true);
    }

    private void OnEnable()
    {
        if (IsSpawned && IsOwner)
            LogDiagnostic("component-enabled", $"WeaponController component enabled while spawned; active={gameObject.activeInHierarchy}, {GetNetworkDiagnosticState()}");
    }

    private void Update()
    {
        if (!IsSpawned) return;

        if (IsServer) ServerReloadTick();
        if (IsServer && IsReloading.Value && !_reloadStuckReported)
        {
            float expectedDuration = AmmoInMag.Value == 0 ? emptyReloadTime : reloadTime;
            if (Time.unscaledTime - _reloadDiagnosticStartTime > expectedDuration + 3f)
            {
                _reloadStuckReported = true;
                LogDiagnostic("reload-stuck", $"Reload has remained active beyond expected duration; elapsed={Time.unscaledTime - _reloadDiagnosticStartTime:0.0}s, expected={expectedDuration:0.0}s, ammo={AmmoInMag.Value}/{ReserveAmmo.Value}.", true);
            }
        }

        debugAmmoInMag = AmmoInMag.Value;
        debugReserveAmmo = ReserveAmmo.Value;
        debugIsReloading = IsReloading.Value;
        debugCurrentSpread = CurrentSpread;
    }

    // Camera recoil + input run in LateUpdate so the shot direction is exactly what the player is looking at.
    private void LateUpdate()
    {
        if (!IsOwner || !IsSpawned || playerCamera == null) return;

        float dt = Time.deltaTime;
        UpdateRecoil(dt);
        _bloom = Mathf.MoveTowards(_bloom, 0f, spreadRecoverySpeed * dt);

        // Once shots have settled, trust the server's ammo count again.
        if (Time.time - _lastShotTime > 0.35f) _predictedAmmo = AmmoInMag.Value;

        HandleInput();
    }

    // -------------------------------------------------------------- INPUT
    private void HandleInput()
    {
        bool reloadPressed = ReloadPressed();
        bool firePressed = FirePressed();
        bool rawMousePressed = Mouse.current != null && Mouse.current.leftButton.isPressed;
        bool rawReloadPressed = Keyboard.current != null && Keyboard.current[reloadKey].wasPressedThisFrame;

        if (_attack != null && rawMousePressed && !firePressed)
            LogDiagnostic("attack-action-missed", $"Raw left mouse button is held but action '{_attack.name}' is not pressed; actionEnabled={_attack.enabled}, asset={inputActions.name}, focused={Application.isFocused}.");
        if (_reload != null && rawReloadPressed && !reloadPressed)
            LogDiagnostic("reload-action-missed", $"Raw {reloadKey} press was seen but action '{_reload.name}' did not report WasPressedThisFrame; actionEnabled={_reload.enabled}, asset={inputActions.name}, focused={Application.isFocused}.");

        if (reloadPressed)
            LogDiagnostic("reload-input", $"Reload input received; source={(_reload != null ? _reload.name : reloadKey.ToString())}, ammo={AmmoInMag.Value}/{ReserveAmmo.Value}, reloading={IsReloading.Value}, predictedAmmo={_predictedAmmo}, cursor={Cursor.lockState}, focused={Application.isFocused}, {GetNetworkDiagnosticState()}");
        if (firePressed && !_wasAttackPressed)
            LogDiagnostic("attack-input", $"Attack input received; source={(_attack != null ? _attack.name : "Mouse.leftButton")}, automatic={automatic}, ammo={AmmoInMag.Value}/{ReserveAmmo.Value}, reloading={IsReloading.Value}, predictedAmmo={_predictedAmmo}, cursor={Cursor.lockState}, focused={Application.isFocused}, {GetNetworkDiagnosticState()}");
        _wasAttackPressed = firePressed;

        NetworkManager manager = NetworkManager.Singleton;
        if ((reloadPressed || firePressed) &&
            (manager == null || !manager.IsListening || !manager.IsConnectedClient))
            LogDiagnostic("input-network-reject", $"Input cannot be sent to the server because local NGO state is not connected; reload={reloadPressed}, attack={firePressed}, {GetNetworkDiagnosticState()}", true);

        if (requireLockedCursor && Cursor.lockState != CursorLockMode.Locked)
        {
            if (reloadPressed || firePressed)
                LogDiagnostic("cursor-reject", $"Input ignored because requireLockedCursor is enabled and cursor is {Cursor.lockState}.");
            return;
        }
        if (_ownHealth != null && _ownHealth.IsDead)
        {
            if (reloadPressed || firePressed)
                LogDiagnostic("dead-reject", $"Input ignored because local player is dead; reload={reloadPressed}, attack={firePressed}.");
            return;
        }

        if (reloadPressed) TryRequestReload();

        if (!firePressed) return;
        if (IsReloading.Value)
        {
            LogDiagnostic("client-reloading-reject", "Firing input ignored because the replicated reload state is active.");
            return;
        }
        if (Time.time < _reloadGuardUntil)
        {
            LogDiagnostic("client-reload-guard-reject", $"Firing input ignored by reload confirmation guard for {_reloadGuardUntil - Time.time:0.00}s.");
            return;
        }
        if (Time.time < _nextFireTime)
        {
            LogDiagnostic("client-fire-rate-reject", $"Firing input ignored by local fire-rate gate for {_nextFireTime - Time.time:0.00}s.");
            return;
        }

        if (_predictedAmmo <= 0)
        {
            LogDiagnostic("client-empty-reject", $"Firing input ignored because predicted ammo is empty; server ammo={AmmoInMag.Value}, reserve={ReserveAmmo.Value}, autoReload={autoReloadOnEmpty}.");
            if (autoReloadOnEmpty) TryRequestReload();
            else OnDryFire?.Invoke();
            return;
        }

        Fire();
    }

    private bool FirePressed()
    {
        if (_attack != null) return automatic ? _attack.IsPressed() : _attack.WasPressedThisFrame();
        if (Mouse.current == null) return false;
        return automatic ? Mouse.current.leftButton.isPressed : Mouse.current.leftButton.wasPressedThisFrame;
    }

    private bool ReloadPressed()
    {
        if (_reload != null) return _reload.WasPressedThisFrame();
        return Keyboard.current != null && Keyboard.current[reloadKey].wasPressedThisFrame;
    }

    // --------------------------------------------------------------- FIRING
    private void Fire()
    {
        float interval = FireInterval;
        // Keep a steady cadence even though frames don't line up with the fire interval.
        _nextFireTime = (Time.time - _nextFireTime > interval) ? Time.time + interval : _nextFireTime + interval;
        _lastShotTime = Time.time;
        _predictedAmmo--;

        if (muzzleFlash != null) muzzleFlash.Play();

        // Direction = what the crosshair points at right now (includes current recoil), plus spread bloom.
        Vector2 r = UnityEngine.Random.insideUnitCircle * CurrentSpread;
        Vector3 direction = playerCamera.transform.rotation * Quaternion.Euler(r.y, r.x, 0f) * Vector3.forward;
        Vector3 origin = playerCamera.transform.position;

        ApplyRecoilKick();
        _bloom = Mathf.Min(_bloom + spreadPerShot, maxSpread - baseSpread);

        FireServerRpc(origin, direction);
    }

    private void ApplyRecoilKick()
    {
        _recoilTarget.x = Mathf.Min(_recoilTarget.x + verticalRecoil, maxVerticalRecoil);
        _recoilTarget.y = Mathf.Clamp(
            _recoilTarget.y + UnityEngine.Random.Range(-horizontalRecoil, horizontalRecoil),
            -maxHorizontalRecoil, maxHorizontalRecoil);
    }

    private void UpdateRecoil(float dt)
    {
        if (Time.time - _lastShotTime > recoilRecoveryDelay)
            _recoilTarget = Vector2.MoveTowards(_recoilTarget, Vector2.zero, recoilRecoverySpeed * dt);

        _recoilCurrent = Vector2.Lerp(_recoilCurrent, _recoilTarget, 1f - Mathf.Exp(-recoilSnappiness * dt));

        // Applied to the Camera child (not CameraRoot), so PlayerController's look pitch is never disturbed.
        playerCamera.transform.localRotation = _camBaseRot * Quaternion.Euler(-_recoilCurrent.x, _recoilCurrent.y, 0f);
    }

    // --------------------------------------------------------------- RELOAD
    private void TryRequestReload()
    {
        if (IsReloading.Value)
        {
            LogDiagnostic("reload-already-active", "Reload request ignored because replicated reload state is already active.");
            return;
        }
        if (Time.time < _reloadGuardUntil)
        {
            LogDiagnostic("reload-guard-reject", $"Reload request ignored by reload confirmation guard for {_reloadGuardUntil - Time.time:0.00}s.");
            return;
        }
        if (_predictedAmmo >= magazineSize)
        {
            LogDiagnostic("reload-full-reject", $"Reload request ignored because predicted magazine is full ({_predictedAmmo}/{magazineSize}).");
            return;
        }
        if (ReserveAmmo.Value <= 0)
        {
            LogDiagnostic("reload-empty-reserve-reject", "Reload request ignored because reserve ammo is empty.");
            return;
        }

        _reloadGuardUntil = Time.time + 0.3f;   // blocks firing until the server confirms (covers network delay)
        ReloadServerRpc();
    }

    [ServerRpc]
    private void ReloadServerRpc()
    {
        if (IsReloading.Value)
        {
            LogDiagnostic("server-reload-active", $"Server rejected reload from client {OwnerClientId}: reload already active.");
            return;
        }
        if (AmmoInMag.Value >= magazineSize || ReserveAmmo.Value <= 0)
        {
            LogDiagnostic("server-reload-ammo", $"Server rejected reload from client {OwnerClientId}: ammo state is {AmmoInMag.Value}/{ReserveAmmo.Value}.");
            return;
        }
        if (_ownHealth != null && _ownHealth.IsDead)
        {
            LogDiagnostic("server-reload-dead", $"Server rejected reload from client {OwnerClientId}: player is dead.");
            return;
        }

        _reloadEndTime = Time.time + (AmmoInMag.Value == 0 ? emptyReloadTime : reloadTime);
        IsReloading.Value = true;
        if (debugLogs) Debug.Log($"[Weapon] Client {OwnerClientId} reloading ({AmmoInMag.Value}/{ReserveAmmo.Value})");
    }

    private void ServerReloadTick()
    {
        if (!IsReloading.Value) return;

        if (_ownHealth != null && _ownHealth.IsDead) { IsReloading.Value = false; return; }
        if (Time.time < _reloadEndTime) return;

        int take = Mathf.Min(magazineSize - AmmoInMag.Value, ReserveAmmo.Value);
        AmmoInMag.Value += take;
        ReserveAmmo.Value -= take;
        IsReloading.Value = false;
    }

    /// <summary>Server only. Call on respawn or from an ammo pickup.</summary>
    public void RefillAmmo()
    {
        if (!IsServer) return;
        AmmoInMag.Value = magazineSize;
        ReserveAmmo.Value = startingReserveAmmo;
        IsReloading.Value = false;
    }

    private void HandleAmmoChanged(int previous, int current)
    {
        if (IsOwner && current > previous) _predictedAmmo = current;   // reload finished / refill
        OnAmmoChanged?.Invoke(current, ReserveAmmo.Value);
    }

    private void HandleReserveChanged(int previous, int current)
    {
        OnAmmoChanged?.Invoke(AmmoInMag.Value, current);
    }

    private void HandleReloadStateChanged(bool previous, bool current)
    {
        if (current)
        {
            _reloadDiagnosticStartTime = Time.unscaledTime;
            _reloadStuckReported = false;
            if (IsOwner || IsServer)
                LogDiagnostic("reload-start", $"Reload state became active; ammo={AmmoInMag.Value}/{ReserveAmmo.Value}, owner={IsOwner}, server={IsServer}.");
        }
        else if (previous && (IsOwner || IsServer))
        {
            LogDiagnostic("reload-end", $"Reload state ended after {Time.unscaledTime - _reloadDiagnosticStartTime:0.0}s; ammo={AmmoInMag.Value}/{ReserveAmmo.Value}, owner={IsOwner}, server={IsServer}.");
        }
    }

    // ------------------------------------------------------- SERVER: HITSCAN
    [ServerRpc]
    private void FireServerRpc(Vector3 origin, Vector3 direction)
    {
        direction.Normalize();

        if ((origin - transform.position).sqrMagnitude > maxOriginOffset * maxOriginOffset)
        {
            Debug.LogWarning($"[Weapon] Rejected shot from client {OwnerClientId}: origin too far from player.");
            return;
        }
        if (_ownHealth != null && _ownHealth.IsDead)
        {
            LogDiagnostic("server-fire-dead", $"Server rejected shot from client {OwnerClientId}: player is dead.");
            return;
        }
        if (IsReloading.Value)
        {
            LogDiagnostic("server-fire-reloading", $"Server rejected shot from client {OwnerClientId}: reload is active.");
            return;
        }
        if (AmmoInMag.Value <= 0)
        {
            LogDiagnostic("server-fire-empty", $"Server rejected shot from client {OwnerClientId}: magazine is empty.");
            return;
        }
        if (Time.time < _serverLastShotTime + FireInterval * serverFireRateTolerance)
        {
            LogDiagnostic("server-fire-rate", $"Server rejected shot from client {OwnerClientId}: fire-rate gate has {_serverLastShotTime + FireInterval * serverFireRateTolerance - Time.time:0.00}s remaining.");
            return;
        }

        _serverLastShotTime = Time.time;
        AmmoInMag.Value--;

        Vector3 endPoint = origin + direction * range;
        Vector3 hitNormal = -direction;
        bool didHit = false;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, range, hitMask, QueryTriggerInteraction.Ignore))
        {
            endPoint = hit.point;
            hitNormal = hit.normal;
            didHit = true;

            Health health = hit.collider.GetComponentInParent<Health>();
            if (health != null && health != _ownHealth)
            {
                PlayerTeam targetTeam = health.GetComponent<PlayerTeam>();
                bool sameTeam = _ownTeam != null && targetTeam != null
                                && targetTeam.Team.Value == _ownTeam.Team.Value;

                if (sameTeam && !friendlyFire)
                {
                    if (debugLogs) Debug.Log("[Weapon] Teammate hit - no damage (friendly fire off).");
                }
                else
                {
                    health.TakeDamage(damage, OwnerClientId);
                }
            }

            if (debugLogs)
                Debug.Log($"[Weapon] Client {OwnerClientId} hit '{hit.collider.name}' (Health: {health != null}) ammo left {AmmoInMag.Value}");
        }

        Debug.DrawLine(origin, endPoint, didHit ? Color.red : Color.yellow, 1f);
        FireClientRpc(origin, endPoint, didHit, hitNormal);
    }

    private void LogDiagnostic(string key, string message, bool warning = false)
    {
        if (!debugLogs) return;

        float now = Time.unscaledTime;
        if (_lastDiagnosticLogTime.TryGetValue(key, out float lastTime) && now - lastTime < 3f)
            return;

        _lastDiagnosticLogTime[key] = now;
        string formatted = $"[InputDiag][Weapon][{key}] player={gameObject.name} ownerClientId={OwnerClientId}: {message}";
        if (warning) Debug.LogWarning(formatted);
        else Debug.Log(formatted);
    }

    private static string DescribeAction(InputAction action)
    {
        return action == null ? "missing" : $"name={action.name}, enabled={action.enabled}, bindings={action.bindings.Count}";
    }

    private static string GetNetworkDiagnosticState()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null) return "networkManager=missing";
        return $"isClient={manager.IsClient}, isServer={manager.IsServer}, connected={manager.IsConnectedClient}, listening={manager.IsListening}, shuttingDown={manager.ShutdownInProgress}";
    }

    [ClientRpc]
    private void FireClientRpc(Vector3 origin, Vector3 endPoint, bool didHit, Vector3 hitNormal)
    {
        Vector3 start = origin;
        if (IsOwner && muzzlePoint != null) start = muzzlePoint.position;
        else if (!IsOwner && worldMuzzlePoint != null) start = worldMuzzlePoint.position;

        SpawnTracer(start, endPoint);
        if (didHit) SpawnImpactVFX(endPoint, hitNormal);
    }

    private void SpawnTracer(Vector3 start, Vector3 end)
    {
        if (tracerPrefab == null) return;

        LineRenderer tracer = Instantiate(tracerPrefab, Vector3.zero, Quaternion.identity);
        tracer.positionCount = 2;
        tracer.SetPosition(0, start);
        tracer.SetPosition(1, end);

        var runner = tracer.GetComponent<TracerFade>();
        if (runner == null) runner = tracer.gameObject.AddComponent<TracerFade>();
        runner.Init(tracer, 0.05f);
    }

    private void SpawnImpactVFX(Vector3 point, Vector3 normal)
    {
        Quaternion rot = Quaternion.LookRotation(normal);

        if (impactVFXPrefab != null)
        {
            ParticleSystem vfx = Instantiate(impactVFXPrefab, point, rot);
            Destroy(vfx.gameObject, 3f);
        }

        if (scorchDecalPrefab != null)
        {
            Quaternion spin = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), normal);
            GameObject decal = Instantiate(scorchDecalPrefab, point + normal * 0.01f, spin * rot);
            Destroy(decal, 8f);
        }
    }

    // -------------------------------------------------------------- PRESETS
    [ContextMenu("Preset: AKM (600 RPM, hard-hitting, strong kick)")]
    private void PresetAKM()
    {
        roundsPerMinute = 600f; damage = 30f; magazineSize = 30; startingReserveAmmo = 120;
        reloadTime = 2.5f; emptyReloadTime = 3.0f;
        verticalRecoil = 0.9f; horizontalRecoil = 0.45f; spreadPerShot = 0.06f;
    }

    [ContextMenu("Preset: M416 (720 RPM, faster, controllable)")]
    private void PresetM416()
    {
        roundsPerMinute = 720f; damage = 26f; magazineSize = 30; startingReserveAmmo = 120;
        reloadTime = 2.2f; emptyReloadTime = 2.7f;
        verticalRecoil = 0.6f; horizontalRecoil = 0.3f; spreadPerShot = 0.04f;
    }
}