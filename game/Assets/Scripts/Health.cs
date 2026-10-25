using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Attach to every player prefab AND any shootable target (the target also needs a NetworkObject).
/// Server owns the health value; clients read it via NetworkVariable.
/// Same public API as your old script (MaxHealth, CurrentHealth, IsDead, TakeDamage),
/// plus inspector debug fields and events for the UI.
/// </summary>
public class Health : NetworkBehaviour
{       // Set by the weapon on the server, read by MatchManager to credit kills.
    [System.NonSerialized] public ulong LastAttackerId = ulong.MaxValue;

    public void TakeDamage(float amount, ulong attackerId)
    {
        LastAttackerId = attackerId;
        TakeDamage(amount);
    }
    
    [Header("Health Settings")]
    public float MaxHealth = 100f;

    [Header("Debug (display only - editing these does nothing)")]
    [SerializeField] private float debugCurrentHealth;
    [SerializeField] private bool debugIsDead;

    public NetworkVariable<float> CurrentHealth = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public bool IsDead => CurrentHealth.Value <= 0f;

    /// <summary>(current, max) - fires on every client and the server.</summary>
    public event Action<float, float> OnHealthChanged;
    public event Action OnDied;

    public override void OnNetworkSpawn()
    {
        CurrentHealth.OnValueChanged += HandleChanged;

        if (IsServer)
            CurrentHealth.Value = MaxHealth;

        RefreshDebug(CurrentHealth.Value);
    }

    public override void OnNetworkDespawn()
    {
        CurrentHealth.OnValueChanged -= HandleChanged;
    }

    /// <summary>Only call on the server (e.g. from WeaponController's ServerRpc).</summary>
    public void TakeDamage(float amount)
    {
        if (!IsServer) return;
        if (IsDead) return;

        CurrentHealth.Value = Mathf.Clamp(CurrentHealth.Value - amount, 0f, MaxHealth);

        if (CurrentHealth.Value <= 0f)
            HandleDeath();
    }

    public void ResetHealth()
    {
        if (!IsServer) return;
        CurrentHealth.Value = MaxHealth;
    }

    private void HandleDeath()
    {
        // Hook your respawn / ragdoll / score logic here (server side).
        Debug.Log($"{gameObject.name} died.");
    }

    private void HandleChanged(float previous, float current)
    {
        RefreshDebug(current);
        OnHealthChanged?.Invoke(current, MaxHealth);
        if (current <= 0f && previous > 0f)
            OnDied?.Invoke();
    }

    private void RefreshDebug(float current)
    {
        debugCurrentHealth = current;
        debugIsDead = current <= 0f;
    }
}