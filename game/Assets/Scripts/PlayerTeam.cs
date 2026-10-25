using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Player root. Handles:
///  - automatic team balancing (always fills the smaller team: 3 players = 2v1, 5 players = 3v2, never 4v1)
///  - spawning at the team's spawn point
///  - respawn after death
///  - team colour tint on the body so you can tell teams apart
/// Team 0 = Team A (blue), Team 1 = Team B (red).
/// </summary>
public class PlayerTeam : NetworkBehaviour
{
    public static readonly List<PlayerTeam> All = new List<PlayerTeam>();

    [Header("Respawn")]
    public float respawnDelay = 3f;
    [Tooltip("Player root sits at capsule centre, so lift it above the spawn point's ground position.")]
    public float spawnHeightOffset = 1f;

    [Header("Team look")]
    public Color teamAColor = new Color(0.25f, 0.5f, 1f);
    public Color teamBColor = new Color(1f, 0.3f, 0.25f);
    [Range(0f, 1f)] public float tintStrength = 0.35f;

    [Header("Debug (display only)")]
    [SerializeField] private int debugTeam = -1;
    [SerializeField] private int debugPlayersOnTeamA;
    [SerializeField] private int debugPlayersOnTeamB;

    /// <summary>-1 = not assigned yet, 0 = Team A, 1 = Team B.</summary>
    public NetworkVariable<int> Team = new NetworkVariable<int>(-1,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public string TeamName => Team.Value == 0 ? "Team A" : Team.Value == 1 ? "Team B" : "No team";

    private Health _health;
    private WeaponController _weapon;
    private CharacterController _cc;
    private CapsuleCollider _hitbox;
    private NetworkTransform _netTransform;
    private SkinnedMeshRenderer[] _bodyRenderers;
    private MaterialPropertyBlock _mpb;
    private bool _spawnRequested;
    private bool _spawnedOnce;       // server
    private bool _respawning;        // server

    public override void OnNetworkSpawn()
    {
        All.Add(this);

        _health = GetComponent<Health>();
        _weapon = GetComponent<WeaponController>();
        _cc = GetComponent<CharacterController>();
        _hitbox = GetComponent<CapsuleCollider>();
        _netTransform = GetComponent<NetworkTransform>();
        _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        _mpb = new MaterialPropertyBlock();

        Team.OnValueChanged += HandleTeamChanged;
        if (_health != null) _health.OnHealthChanged += HandleHealthChanged;

        if (IsServer)
        {
            Team.Value = PickTeam();
            if (_health != null) _health.OnDied += HandleDiedServer;
        }

        ApplyTint(Team.Value);
        SetAliveVisuals(_health == null || !_health.IsDead);
        TryRequestInitialSpawn();
    }

    public override void OnNetworkDespawn()
    {
        All.Remove(this);
        Team.OnValueChanged -= HandleTeamChanged;
        if (_health != null)
        {
            _health.OnHealthChanged -= HandleHealthChanged;
            _health.OnDied -= HandleDiedServer;
        }
    }

    private void Update()
    {
        debugTeam = Team.Value;
        CountTeams(null, out debugPlayersOnTeamA, out debugPlayersOnTeamB);
    }

    // ------------------------------------------------------------ TEAMS
    private int PickTeam()
    {
        CountTeams(this, out int a, out int b);
        return a <= b ? 0 : 1;   // fill the smaller team; tie -> Team A
    }

    private static void CountTeams(PlayerTeam ignore, out int a, out int b)
    {
        a = 0; b = 0;
        foreach (var p in All)
        {
            if (p == ignore) continue;
            if (p.Team.Value == 0) a++;
            else if (p.Team.Value == 1) b++;
        }
    }

    private void HandleTeamChanged(int previous, int current)
    {
        ApplyTint(current);
        TryRequestInitialSpawn();
    }

    private void ApplyTint(int team)
    {
        if (_bodyRenderers == null || team < 0) return;

        Color tint = Color.Lerp(Color.white, team == 0 ? teamAColor : teamBColor, tintStrength);
        _mpb.SetColor("_BaseColor", tint);   // URP / HDRP
        _mpb.SetColor("_Color", tint);       // Built-in
        foreach (var r in _bodyRenderers) if (r != null) r.SetPropertyBlock(_mpb);
    }

    // ------------------------------------------------------------ SPAWN
    // The owner asks the server for its first spawn once the team is known.
    private void TryRequestInitialSpawn()
    {
        if (!IsOwner || _spawnRequested || Team.Value < 0) return;
        _spawnRequested = true;
        RequestSpawnServerRpc();
    }

    [ServerRpc]
    private void RequestSpawnServerRpc()
    {
        if (_spawnedOnce) return;   // can't be abused to teleport repeatedly
        _spawnedOnce = true;
        SendTeleport();
    }

    private void SendTeleport()
    {
        Transform sp = TeamSpawnPoint.GetRandom(Team.Value);
        if (sp == null)
        {
            Debug.LogWarning($"[Team] No TeamSpawnPoint found for team {Team.Value}. Add spawn points to the scene.");
            return;
        }

        Vector3 pos = sp.position + Vector3.up * spawnHeightOffset;
        var target = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        TeleportClientRpc(pos, sp.rotation, target);
    }

    [ClientRpc]
    private void TeleportClientRpc(Vector3 pos, Quaternion rot, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;

        // The CharacterController overrides direct position changes unless it is switched off first.
        if (_cc != null) _cc.enabled = false;

        if (_netTransform != null) _netTransform.Teleport(pos, rot, transform.localScale);
        else transform.SetPositionAndRotation(pos, rot);

        if (_cc != null) _cc.enabled = true;
    }

    // ---------------------------------------------------------- RESPAWN
    private void HandleDiedServer()
    {
        if (!_respawning) StartCoroutine(RespawnRoutine());
    }

    private IEnumerator RespawnRoutine()
    {
        _respawning = true;
        yield return new WaitForSeconds(respawnDelay);

        if (_health != null) _health.ResetHealth();
        if (_weapon != null) _weapon.RefillAmmo();
        SendTeleport();
        _respawning = false;
    }

    // ------------------------------------------------------ DEAD VISUALS
    private void HandleHealthChanged(float current, float max) => SetAliveVisuals(current > 0f);

    // Dead bodies stop blocking bullets (hitbox off) and other players' dead bodies disappear.
    private void SetAliveVisuals(bool alive)
    {
        if (_hitbox != null) _hitbox.enabled = alive;
        if (IsOwner) return;   // your own body is handled by PlayerController
        if (_bodyRenderers != null)
            foreach (var r in _bodyRenderers) if (r != null) r.enabled = alive;
    }
}