using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Put on an empty GameObject standing on the ground. Team 0 = Team A (blue), Team 1 = Team B (red).
/// The blue arrow in the Scene view shows which way the player will face.
/// </summary>
public class TeamSpawnPoint : MonoBehaviour
{
    [Tooltip("0 = Team A, 1 = Team B")]
    [Range(0, 1)] public int team;

    private static readonly List<TeamSpawnPoint> All = new List<TeamSpawnPoint>();

    private void OnEnable() => All.Add(this);
    private void OnDisable() => All.Remove(this);

    public static int Count(int team)
    {
        int n = 0;
        foreach (var p in All) if (p.team == team) n++;
        return n;
    }

    /// <summary>Random spawn point for a team, or null if none exists.</summary>
    public static Transform GetRandom(int team)
    {
        var list = new List<TeamSpawnPoint>();
        foreach (var p in All) if (p.team == team) list.Add(p);
        if (list.Count == 0) return null;
        return list[Random.Range(0, list.Count)].transform;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = team == 0 ? new Color(0.2f, 0.5f, 1f) : new Color(1f, 0.3f, 0.25f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.9f, 0.5f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
        Gizmos.DrawRay(transform.position + Vector3.up * 0.9f, transform.forward * 1.2f);
    }
}
