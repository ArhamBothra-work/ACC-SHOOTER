using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Session layer. Lives on the NetworkManager object.
/// - Create Room / Join Room with a join code (Unity Multiplayer Services "Sessions" + Relay under the hood)
/// - Connection approval (max players, "room full")
/// - Dedicated server mode (-server / -port N / Dedicated Server build) using direct IP, for AWS
/// Replaces the old NetworkBootstrap (and the earlier standalone-Relay version).
/// </summary>
[DefaultExecutionOrder(-100)]
public class NetworkBootstrap : MonoBehaviour
{
    public static NetworkBootstrap Instance { get; private set; }

    [Header("Room")]
    public int maxPlayers = 10;

    [Header("Direct / dedicated server (AWS)")]
    public string listenAddress = "0.0.0.0";
    public ushort defaultPort = 7777;
    [SerializeField, Min(1f)] float directConnectTimeoutSeconds = 15f;

    [Header("Agent (AWS Dedicated Server)")]
    public string agentBaseUrl = "http://127.0.0.1:8081";
    public float agentTimeoutSeconds = 5f;

    public string AgentValidateUrl => $"{agentBaseUrl.TrimEnd('/')}/validate";
    public string AgentResultUrl => $"{agentBaseUrl.TrimEnd('/')}/result";
    public string AgentReleaseUrl => $"{agentBaseUrl.TrimEnd('/')}/release";

    [Header("Debug")]
    public bool showDebugHud = true;

    public string RoomCode { get; private set; } = "";
    public string ServerRoomCode { get; private set; } = "";
    public string ServerMatchId { get; private set; } = "";
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool SessionActive => sessionActive;

    public event Action<string> OnStatus;
    public event Action OnSessionStarted;          // local player is in a room
    public event Action<string> OnSessionEnded;    // reason text

    ISession session;
    bool sessionActive;
    bool directConnectPending;
    float directConnectDeadline;
    float fps = 60f;

    NetworkManager NM => NetworkManager.Singleton;
    UnityTransport Utp => NetworkManager.Singleton.GetComponent<UnityTransport>();

    // ---------------------------------------------------------------- setup

    void Awake()
    {
        Instance = this;
        var nm = GetComponent<NetworkManager>();
        if (nm != null)
        {
            nm.NetworkConfig.ConnectionApproval = true;
            nm.ConnectionApprovalCallback = Approve;
        }
    }

    void Start()
    {
        NM.OnClientConnectedCallback += HandleClientConnected;
        NM.OnClientDisconnectCallback += HandleClientDisconnect;

        if (IsDedicatedServerMode() && !Application.isEditor)
            StartDedicated(ParsePort(defaultPort));
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        }
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f) fps = Mathf.Lerp(fps, 1f / dt, 0.05f);

        if (directConnectPending && Time.unscaledTime >= directConnectDeadline)
            _ = FinishFailedDirectConnectAsync("Direct connection timed out.");
    }

    void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
    {
        if (!IsDedicatedServerMode())
        {
            bool full = NM.ConnectedClientsIds.Count >= maxPlayers;
            res.Approved = !full;
            res.CreatePlayerObject = !full;
            res.Pending = false;
            if (full) res.Reason = "Room is full.";
            return;
        }

        if (NM.ConnectedClientsIds.Count >= maxPlayers)
        {
            res.Approved = false;
            res.CreatePlayerObject = false;
            res.Reason = "Room is full.";
            res.Pending = false;
            return;
        }

        if (req.Payload == null || req.Payload.Length == 0)
        {
            res.Approved = false;
            res.CreatePlayerObject = false;
            res.Reason = "Missing join ticket.";
            res.Pending = false;
            return;
        }

        string ticket = ExtractTicketFromPayload(req.Payload);
        if (string.IsNullOrEmpty(ticket))
        {
            res.Approved = false;
            res.CreatePlayerObject = false;
            res.Reason = "Invalid join ticket format.";
            res.Pending = false;
            return;
        }

        res.Pending = true;
        StartCoroutine(ValidateTicketWithAgentCoroutine(ticket, req.ClientNetworkId, res));
    }

    // ---------------------------------------------------------------- room code API

    public async Task<bool> CreateRoomAsync(int players)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            maxPlayers = Mathf.Clamp(players, 2, 10);
            await WaitForShutdown();
            SetStatus("Creating room...");
            if (!await TryServices()) return false;

            // MaxPlayers here counts everyone in the room, host included.
            var options = new SessionOptions
            {
                Name = "Room" + UnityEngine.Random.Range(1000, 9999),
                MaxPlayers = maxPlayers
            }.WithRelayNetwork();

            session = await MultiplayerService.Instance.CreateSessionAsync(options);

            // The Sessions SDK starts the NetworkManager as host for us. Wait until it is up.
            float t = 0f;
            while (!NM.IsHost && t < 10f)
            {
                await Task.Yield();
                t += Time.unscaledDeltaTime;
            }
            if (!NM.IsHost)
            {
                SetStatus("The room was created but the host failed to start.");
                await AbandonSession(true);
                return false;
            }

            RoomCode = session.Code;
            BeginSession();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Create failed: " + e);
            SetStatus("Could not create the room. See Console.");
            await AbandonSession(NM.IsHost);
            return false;
        }
        finally { Busy = false; }
    }

    public async Task<bool> JoinRoomAsync(string code)
    {
        if (Busy) return false;
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { SetStatus("Enter a room code."); return false; }

        Busy = true;
        try
        {
            await WaitForShutdown();
            SetStatus("Joining room...");
            if (!await TryServices()) return false;

            try { session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code); }
            catch (Exception e)
            {
                Debug.LogWarning("[Room] Join rejected: " + e.Message);
                SetStatus("Could not join that room. Check the code, or the room may be full.");
                return false;
            }

            // The Sessions SDK starts the NetworkManager client for us. Wait until connected.
            float t = 0f;
            while (!NM.IsConnectedClient && t < 15f)
            {
                await Task.Yield();
                t += Time.unscaledDeltaTime;
            }

            if (!NM.IsConnectedClient)
            {
                string reason = NM.DisconnectReason;
                if (NM.IsListening) NM.Shutdown();
                await AbandonSession(false);
                SetStatus(string.IsNullOrEmpty(reason) ? "Could not connect to the room." : reason);
                return false;
            }

            RoomCode = code;
            BeginSession();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Join failed: " + e);
            SetStatus("Could not join the room. See Console.");
            await AbandonSession(false);
            return false;
        }
        finally { Busy = false; }
    }

    public void Leave()
    {
        bool was = sessionActive;
        bool wasHost = NM != null && NM.IsHost;
        directConnectPending = false;
        sessionActive = false;
        RoomCode = "";
        if (NM != null && NM.IsListening) NM.Shutdown();
        _ = AbandonSession(wasHost);
        if (was) OnSessionEnded?.Invoke("You left the room.");
    }

    // ---------------------------------------------------------------- direct IP (dev / AWS)

    /// <summary>
    /// Starts a direct-IP client connection. OnSessionStarted is raised only after NGO reports
    /// that this local client connected; a successful StartClient call only means startup began.
    /// </summary>
    public bool JoinDirect(string ip, ushort port)
    {
        return StartDirectClient(ip, port, Array.Empty<byte>());
    }

    /// <summary>
    /// Passes an already-serialized NGO approval payload. This is transport plumbing only:
    /// the backend ticket format and a trusted server-side validator are not implemented.
    /// </summary>
    public bool JoinDirectWithApprovalPayload(string ip, ushort port, byte[] approvalPayload, string roomCode = "")
    {
        if (approvalPayload == null || approvalPayload.Length == 0 || approvalPayload.Length > 1024)
        {
            SetStatus("The direct connection approval payload is missing or too large.");
            return false;
        }

        RoomCode = !string.IsNullOrEmpty(roomCode) ? roomCode : "DIRECT";
        return StartDirectClient(ip, port, approvalPayload);
    }

    bool StartDirectClient(string ip, ushort port, byte[] approvalPayload)
    {
        if (string.IsNullOrWhiteSpace(ip) || port == 0)
        {
            SetStatus("A valid direct server address and port are required.");
            return false;
        }

        if (NM == null || NM.IsListening || NM.ShutdownInProgress)
        {
            SetStatus("A network session is already running or shutting down.");
            return false;
        }

        NM.NetworkConfig.ConnectionData = approvalPayload;
        Utp.SetConnectionData(ip, port);
        // Set this first in case a local/direct transport invokes the callback during StartClient.
        directConnectPending = true;
        directConnectDeadline = Time.unscaledTime + Mathf.Max(1f, directConnectTimeoutSeconds);
        if (!NM.StartClient())
        {
            directConnectPending = false;
            NM.NetworkConfig.ConnectionData = Array.Empty<byte>();
            if (NM.IsListening) NM.Shutdown();
            SetStatus("Could not start the direct client connection.");
            _ = CleanupFailedDirectStartAsync();
            return false;
        }

        if (directConnectPending)
            SetStatus($"Connecting to {ip}:{port}...");
        return true;
    }

    async Task CleanupFailedDirectStartAsync()
    {
        await WaitForShutdown();
        if (NM != null)
            NM.NetworkConfig.ConnectionData = Array.Empty<byte>();
    }

    public void HostDirect(ushort port)
    {
        Utp.SetConnectionData("127.0.0.1", port, listenAddress);
        if (NM.StartHost()) BeginSession();
    }

    void StartDedicated(ushort port)
    {
        ApplyDedicatedServerArguments();
        Application.targetFrameRate = 60;
        Utp.SetConnectionData("127.0.0.1", port, listenAddress);
        if (NM.StartServer())
            Debug.Log($"[Server] Dedicated server listening on {listenAddress}:{port} (max {maxPlayers}, room {ServerRoomCode}, match {ServerMatchId})");
        else
            Debug.LogError("[Server] StartServer failed.");
    }

    public static bool IsDedicatedServerMode()
    {
#if UNITY_SERVER
        return true;
#else
        foreach (var a in Environment.GetCommandLineArgs())
            if (a == "-server") return true;
        return false;
#endif
    }

    static ushort ParsePort(ushort fallback)
    {
        if (!TryGetArgument("-port", out string raw))
            return fallback;
        if (ushort.TryParse(raw, out ushort port) && port > 0)
            return port;

        Debug.LogWarning($"[Server] Ignoring invalid -port value; keeping {fallback}.");
        return fallback;
    }

    void ApplyDedicatedServerArguments()
    {
        maxPlayers = ParseBoundedInt("-maxPlayers", maxPlayers, 2, 10);
        ServerRoomCode = ParseIdentifier("-roomCode");
        ServerMatchId = ParseIdentifier("-matchId");

        if (TryGetArgument("-agentUrl", out string customAgentUrl) && !string.IsNullOrWhiteSpace(customAgentUrl))
        {
            agentBaseUrl = customAgentUrl.Trim();
        }
        else if (TryGetArgument("-agentPort", out string portStr) && ushort.TryParse(portStr, out ushort aPort) && aPort > 0)
        {
            agentBaseUrl = $"http://127.0.0.1:{aPort}";
        }

        var match = FindFirstObjectByType<MatchManager>();
        if (match != null)
            match.scoreLimit = ParseBoundedInt("-killLimit", match.scoreLimit, 1, 200);
    }

    static int ParseBoundedInt(string option, int fallback, int minimum, int maximum)
    {
        if (!TryGetArgument(option, out string raw))
            return fallback;
        if (int.TryParse(raw, out int value) && value >= minimum && value <= maximum)
            return value;

        Debug.LogWarning($"[Server] Ignoring invalid {option} value; keeping {fallback}.");
        return fallback;
    }

    static string ParseIdentifier(string option)
    {
        if (!TryGetArgument(option, out string value))
            return "";

        if (value.Length > 64)
        {
            Debug.LogWarning($"[Server] Ignoring invalid {option} value.");
            return "";
        }

        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                Debug.LogWarning($"[Server] Ignoring invalid {option} value.");
                return "";
            }
        }

        return value;
    }

    static bool TryGetArgument(string option, out string value)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[i + 1];
                return true;
            }
        }

        value = null;
        return false;
    }

    // ---------------------------------------------------------------- internals

    void BeginSession()
    {
        if (sessionActive) return;
        sessionActive = true;
        OnSessionStarted?.Invoke();
    }

    void HandleClientConnected(ulong clientId)
    {
        // Relay owns its own startup path. This callback completes only direct client joins.
        if (!directConnectPending || NM.IsServer || clientId != NM.LocalClientId) return;

        directConnectPending = false;
        NM.NetworkConfig.ConnectionData = Array.Empty<byte>();
        SetStatus("Connected to direct server.");
        BeginSession();
    }

    void HandleClientDisconnect(ulong clientId)
    {
        // On dedicated server: release slot with agent when a remote client leaves
        if (NM.IsServer)
        {
            if (IsDedicatedServerMode() && clientId != NM.LocalClientId)
                StartCoroutine(ReleaseSlotAsync(clientId));
            return;
        }

        // Only react when WE (a pure client) lost the connection.
        if (clientId != NM.LocalClientId) return;

        bool failedDirectConnect = directConnectPending;
        if (!sessionActive && !failedDirectConnect) return;

        string reason = NM.DisconnectReason;
        if (string.IsNullOrEmpty(reason))
            reason = failedDirectConnect ? "Could not connect to the direct server." : "Disconnected from the room.";

        directConnectPending = false;
        sessionActive = false;
        RoomCode = "";
        if (failedDirectConnect)
        {
            _ = FinishFailedDirectConnectAsync(reason);
            return;
        }

        NM.NetworkConfig.ConnectionData = Array.Empty<byte>();
        _ = AbandonSession(false);
        SetStatus(reason);
        OnSessionEnded?.Invoke(reason);
    }

    async Task FinishFailedDirectConnectAsync(string reason)
    {
        directConnectPending = false;
        sessionActive = false;
        RoomCode = "";
        if (NM != null)
        {
            NM.NetworkConfig.ConnectionData = Array.Empty<byte>();
            if (NM.IsListening) NM.Shutdown();
        }

        await WaitForShutdown();
        await AbandonSession(false);
        SetStatus(reason);
        OnSessionEnded?.Invoke(reason);
    }

    // Leaves (or, for the host, deletes) the backend session. Never throws.
    async Task AbandonSession(bool asHost)
    {
        var s = session;
        session = null;
        if (s == null) return;
        try
        {
            if (asHost) await s.AsHost().DeleteAsync();
            else await s.LeaveAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Room] Leaving session: " + e.Message);
        }
    }

    async Task WaitForShutdown()
    {
        while (NM != null && NM.ShutdownInProgress) await Task.Yield();
    }

    async Task<bool> TryServices()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                // Random profile = a separate anonymous account per running instance,
                // so two editor windows on one PC don't share one identity.
                var options = new InitializationOptions();
                options.SetProfile("p" + UnityEngine.Random.Range(100000, 999999));
                await UnityServices.InitializeAsync(options);
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[Room] Unity Services failed: " + e);
            SetStatus("Online services unavailable. Is the Unity project linked? See Console.");
            return false;
        }
    }

    void SetStatus(string s)
    {
        Status = s;
        OnStatus?.Invoke(s);
    }

    void OnGUI()
    {
        if (!showDebugHud || !sessionActive || NM == null) return;
        string s = $"FPS {fps:0}";
        if (NM.IsClient && !NM.IsHost)
            s += $"   Ping {Utp.GetCurrentRtt(NetworkManager.ServerClientId)} ms";
        GUI.Label(new Rect(10, Screen.height - 30, 320, 24), s);
    }

    [Serializable]
    class AgentValidateRequestPayload
    {
        public string ticket;
        public ulong clientId;
    }

    [Serializable]
    class AgentValidateResponsePayload
    {
        public bool valid;
        public string reservationId;
        public string matchId;
        public string roomCode;
        public string error;
        public string message;
    }

    [Serializable]
    class AgentReleaseRequestPayload
    {
        public ulong clientId;
    }

    [Serializable]
    class TicketPayloadWrapper
    {
        public string ticket;
    }

    static string ExtractTicketFromPayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return null;
        try
        {
            string raw = Encoding.UTF8.GetString(payload).Trim();
            if (string.IsNullOrEmpty(raw)) return null;

            if (raw.StartsWith("{") && raw.EndsWith("}"))
            {
                var wrapped = JsonUtility.FromJson<TicketPayloadWrapper>(raw);
                if (wrapped != null && !string.IsNullOrWhiteSpace(wrapped.ticket))
                    return wrapped.ticket.Trim();
            }

            return raw;
        }
        catch
        {
            return null;
        }
    }

    IEnumerator ValidateTicketWithAgentCoroutine(string ticket, ulong clientId, NetworkManager.ConnectionApprovalResponse res)
    {
        string jsonBody = JsonUtility.ToJson(new AgentValidateRequestPayload { ticket = ticket, clientId = clientId });
        byte[] rawBody = Encoding.UTF8.GetBytes(jsonBody);

        using (var uwr = new UnityWebRequest(AgentValidateUrl, UnityWebRequest.kHttpVerbPOST))
        {
            uwr.uploadHandler = new UploadHandlerRaw(rawBody);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            uwr.timeout = Mathf.Max(1, Mathf.RoundToInt(agentTimeoutSeconds));

            yield return uwr.SendWebRequest();

            try
            {
                if (uwr.result == UnityWebRequest.Result.Success)
                {
                    string text = uwr.downloadHandler.text;
                    AgentValidateResponsePayload parsed = null;
                    try
                    {
                        parsed = JsonUtility.FromJson<AgentValidateResponsePayload>(text);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Server] Failed to parse agent validate response: {ex.Message}");
                    }

                    if (parsed != null && parsed.valid)
                    {
                        res.Approved = true;
                        res.CreatePlayerObject = true;
                        res.Reason = "";
                        Debug.Log($"[Server] Client {clientId} approved with ticket (match: {parsed.matchId}, room: {parsed.roomCode}).");
                    }
                    else
                    {
                        res.Approved = false;
                        res.CreatePlayerObject = false;
                        res.Reason = (parsed != null && !string.IsNullOrEmpty(parsed.error)) ? parsed.error : "Join ticket rejected.";
                        Debug.LogWarning($"[Server] Client {clientId} rejected by agent: {res.Reason}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[Server] Agent validation unavailable ({uwr.responseCode}): {uwr.error}");
                    res.Approved = false;
                    res.CreatePlayerObject = false;
                    res.Reason = "Authentication service unavailable.";
                }
            }
            finally
            {
                try
                {
                    res.Pending = false;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Server] Exception setting approval pending false: {e.Message}");
                }
            }
        }
    }

    IEnumerator ReleaseSlotAsync(ulong clientId)
    {
        string jsonBody = JsonUtility.ToJson(new AgentReleaseRequestPayload { clientId = clientId });
        byte[] rawBody = Encoding.UTF8.GetBytes(jsonBody);

        using (var uwr = new UnityWebRequest(AgentReleaseUrl, UnityWebRequest.kHttpVerbPOST))
        {
            uwr.uploadHandler = new UploadHandlerRaw(rawBody);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            uwr.timeout = Mathf.Max(1, Mathf.RoundToInt(agentTimeoutSeconds));

            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[Server] Released slot for client {clientId} with agent.");
            }
            else
            {
                Debug.LogWarning($"[Server] Failed to release slot for client {clientId} with agent: {uwr.error}");
            }
        }
    }
}
