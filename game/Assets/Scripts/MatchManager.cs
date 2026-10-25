using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

public struct KillInfo
{
    public string killer, victim;
    public int killerTeam, victimTeam;
    public ulong killerId, victimId;
    public int phrase;
    public bool suicide;
}

/// <summary>
/// Scene object (NOT a prefab) with a NetworkObject. The server watches every player's Health,
/// credits kills, counts team scores, picks a funny phrase and tells all clients for the kill feed.
/// </summary>
public class MatchManager : NetworkBehaviour
{
    public static MatchManager Instance { get; private set; }
    public static event Action<KillInfo> OnKillFeed;

    public const ulong NoKiller = ulong.MaxValue;

    [Header("Match (set on the server / host)")]
    public int scoreLimit = 30;         // first team to this many kills wins. 0 = never ends
    public float restartDelay = 10f;    // seconds the winner screen stays up, then scores reset

    public NetworkVariable<int> ScoreA = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> ScoreB = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> ScoreLimit = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> Winner = new NetworkVariable<int>(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // {k} = killer, {v} = victim. Add as many as you like.
    public static readonly string[] KillPhrases =
    {
        "{v} was blown away by {k}",
        "{k} smoked {v}",
        "{v} was sent to heaven by {k}",
        "{k} sent {v} to the shadow realm",
        "{k} turned {v} into Swiss cheese",
        "{v} got a free lead diet from {k}",
        "{k} told {v} nighty night",
        "{v} tripped over {k}'s bullet",
        "{k} gave {v} a permanent vacation",
        "{k} deleted {v}",
        "{v} just met {k}'s favourite rifle",
        "{k} served {v} a hot plate of lead",
        "{v} forgot to duck. {k} did not forget to shoot",
        "{k} showed {v} the light",
        "{v} has left the chat, courtesy of {k}",
        "{k} sent {v} back to the lobby",
        "{v} was turned into confetti by {k}",
        "{k} booked {v} a one-way ticket to respawn",
        "{k} schooled {v}",
        "{v} got outplayed by {k}",
        "{k} popped {v} like a balloon",
        "{v} was evicted by {k}",
        "{k} sent {v} to touch grass",
        "{k} ghosted {v}. Permanently",
        "{v} was tenderized by {k}",
        "{k} reminded {v} that bullets are faster than legs",
        "{v} took the express elevator up, thanks to {k}",
        "{k} yeeted {v} off this mortal coil",
        "{v} got the memo from {k}. It was made of lead",
        "{k} cooked {v}",
        "{v} became a cautionary tale thanks to {k}",
        "{k} put {v} down for an unplanned nap",
        "{v} lost a staring contest with {k}'s rifle",
        "{k} made {v} regret that peek",
        "{v} was bonked into oblivion by {k}",
        "{k} gave {v} a crash course in humility",
        "{v} was rearranged by {k}",
        "{k} handed {v} a participation trophy",
    };

    public static readonly string[] SuicidePhrases =
    {
        "{v} fell victim to gravity",
        "{v} found a creative way to lose",
        "{v} has left the building. Unwillingly",
        "{v} did it to themselves",
        "{v} needs a medic. And a hobby",
    };

    readonly Dictionary<ulong, bool> wasDead = new Dictionary<ulong, bool>();
    float nextPoll;
    float matchStartTime;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer)
        {
            // fresh room = fresh match (the scene object is reused between rooms)
            ScoreA.Value = 0;
            ScoreB.Value = 0;
            Winner.Value = -1;
            ScoreLimit.Value = scoreLimit;
            wasDead.Clear();
            matchStartTime = Time.time;
        }
    }

    public override void OnNetworkDespawn()
    {
        StopAllCoroutines();
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------ server: detect deaths

    void Update()
    {
        if (!IsServer || Time.time < nextPoll) return;
        nextPoll = Time.time + 0.1f;

        foreach (var p in FindObjectsByType<PlayerProfile>(FindObjectsSortMode.None))
        {
            var h = p.GetComponent<Health>();
            if (h == null) continue;

            ulong id = p.OwnerClientId;
            bool dead = h.IsDead;
            wasDead.TryGetValue(id, out bool prev);

            if (dead && !prev)
            {
                ulong attacker = h.LastAttackerId;
                h.LastAttackerId = NoKiller;
                ServerRegisterKill(attacker, id);
            }
            wasDead[id] = dead;
        }
    }

    // ------------------------------------------------------------ server: scoring

    void ServerRegisterKill(ulong killerId, ulong victimId)
    {
        if (Winner.Value != -1) return;                 // match is over, ignore until restart

        var victim = GetProfile(victimId);
        if (victim == null) return;
        victim.Deaths.Value++;

        int victimTeam = TeamOf(victim);
        bool hasKiller = killerId != NoKiller && killerId != victimId;
        var killer = hasKiller ? GetProfile(killerId) : null;

        if (killer == null)
        {
            Broadcast(new KillInfo
            {
                killer = "", victim = victim.DisplayName, killerTeam = -1, victimTeam = victimTeam,
                killerId = NoKiller, victimId = victimId,
                phrase = UnityEngine.Random.Range(0, SuicidePhrases.Length), suicide = true
            });
            return;
        }

        killer.Kills.Value++;
        int killerTeam = TeamOf(killer);
        if (killerTeam == 0) ScoreA.Value++;
        else if (killerTeam == 1) ScoreB.Value++;

        Broadcast(new KillInfo
        {
            killer = killer.DisplayName, victim = victim.DisplayName,
            killerTeam = killerTeam, victimTeam = victimTeam,
            killerId = killerId, victimId = victimId,
            phrase = UnityEngine.Random.Range(0, KillPhrases.Length), suicide = false
        });

        if (scoreLimit > 0)
        {
            if (ScoreA.Value >= scoreLimit) EndMatch(0);
            else if (ScoreB.Value >= scoreLimit) EndMatch(1);
        }
    }

    void EndMatch(int winnerTeam)
    {
        Winner.Value = winnerTeam;
        if (IsServer && NetworkBootstrap.IsDedicatedServerMode())
        {
            StartCoroutine(SubmitMatchResultAsync(winnerTeam));
        }
        StartCoroutine(RestartAfterDelay());
    }

    IEnumerator RestartAfterDelay()
    {
        yield return new WaitForSeconds(restartDelay);
        ScoreA.Value = 0;
        ScoreB.Value = 0;
        foreach (var p in FindObjectsByType<PlayerProfile>(FindObjectsSortMode.None))
            p.ServerResetStats();
        Winner.Value = -1;
        matchStartTime = Time.time;
    }

    static PlayerProfile GetProfile(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null)
            return c.PlayerObject.GetComponent<PlayerProfile>();
        return null;
    }

    static int TeamOf(PlayerProfile p)
    {
        var t = p.GetComponent<PlayerTeam>();
        return t != null ? t.Team.Value : -1;
    }

    static FixedString64Bytes Fs(string s)
    {
        var f = new FixedString64Bytes();
        f.CopyFromTruncated(s ?? "");
        return f;
    }

    void Broadcast(KillInfo k)
    {
        KillFeedClientRpc(Fs(k.killer), Fs(k.victim), k.killerTeam, k.victimTeam, k.killerId, k.victimId, k.phrase, k.suicide);
    }

    [ClientRpc]
    void KillFeedClientRpc(FixedString64Bytes killer, FixedString64Bytes victim, int killerTeam, int victimTeam,
                           ulong killerId, ulong victimId, int phrase, bool suicide)
    {
        OnKillFeed?.Invoke(new KillInfo
        {
            killer = killer.ToString(), victim = victim.ToString(),
            killerTeam = killerTeam, victimTeam = victimTeam,
            killerId = killerId, victimId = victimId,
            phrase = phrase, suicide = suicide
        });
    }

    [Serializable]
    public class MatchPlayerResult
    {
        public string playerId;
        public int team;
        public int kills;
        public int deaths;
    }

    [Serializable]
    public class MatchResultPayload
    {
        public int winnerTeam;
        public int[] teamScores;
        public int durationSeconds;
        public List<MatchPlayerResult> players;
    }

    IEnumerator SubmitMatchResultAsync(int winnerTeam)
    {
        string resultUrl = (NetworkBootstrap.Instance != null)
            ? NetworkBootstrap.Instance.AgentResultUrl
            : "http://127.0.0.1:8081/result";

        int duration = Mathf.Clamp(Mathf.RoundToInt(Time.time - matchStartTime), 1, 7200);
        int scoreA = Mathf.Clamp(ScoreA.Value, 0, 200);
        int scoreB = Mathf.Clamp(ScoreB.Value, 0, 200);

        if (winnerTeam == 0 && scoreA < scoreB) scoreA = scoreB;
        else if (winnerTeam == 1 && scoreB < scoreA) scoreB = scoreA;

        var playerResults = new List<MatchPlayerResult>();
        var seenIds = new HashSet<string>();

        foreach (var p in FindObjectsByType<PlayerProfile>(FindObjectsSortMode.None))
        {
            if (p == null) continue;
            string pid = "client-" + p.OwnerClientId;
            if (seenIds.Contains(pid)) continue;
            seenIds.Add(pid);

            int team = TeamOf(p);
            if (team != 0 && team != 1) team = 0;

            playerResults.Add(new MatchPlayerResult
            {
                playerId = pid,
                team = team,
                kills = Mathf.Clamp(p.Kills.Value, 0, 200),
                deaths = Mathf.Clamp(p.Deaths.Value, 0, 200)
            });

            if (playerResults.Count >= 10) break;
        }

        var payload = new MatchResultPayload
        {
            winnerTeam = winnerTeam,
            teamScores = new int[] { scoreA, scoreB },
            durationSeconds = duration,
            players = playerResults
        };

        string json = JsonUtility.ToJson(payload);
        byte[] raw = Encoding.UTF8.GetBytes(json);

        using (var uwr = new UnityWebRequest(resultUrl, UnityWebRequest.kHttpVerbPOST))
        {
            uwr.uploadHandler = new UploadHandlerRaw(raw);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            uwr.timeout = 5;

            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[MatchManager] Match result submitted to agent: {uwr.downloadHandler.text}");
            }
            else
            {
                Debug.LogWarning($"[MatchManager] Failed to submit match result to agent ({uwr.responseCode}): {uwr.error}");
            }
        }
    }
}