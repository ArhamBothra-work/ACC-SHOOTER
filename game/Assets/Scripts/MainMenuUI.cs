using System.Text;
using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Shooter-style main menu, built entirely in code.
/// Left: huge angled title + buttons. Right: your 3D heroes (MenuStage) lit by MenuCameraRig.
/// Also owns the in-game room-code pill and the Esc pause menu.
/// Put on the NetworkManager object.
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    [Header("Scene objects")]
    public Camera menuCamera;
    public GameObject menuStage;      // parent of the 2 hero characters, hidden during the match

    [Header("Look")]
    public string gameTitle = "TEAM DEATHMATCH";
    public string subtitle = "5 VS 5  //  ONLINE";
    public Font uiFont;               // optional: a bold condensed font (Bebas Neue, Oswald, Teko...)
    public Sprite logoSprite;         // optional: replaces the title text
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [SerializeField] bool allowUnauthenticatedAwsRoomCreation;
#endif

    const float Sk = 0.2f;
    static readonly Vector2 L = new Vector2(0f, 0.5f);   // left-middle anchor
    static readonly Vector2 C = new Vector2(0.5f, 0.5f);

    NetworkBootstrap boot;
    AWSBackendClient awsBackend;
    GameObject menuRoot, mainPanel, modalDim, createPanel, joinPanel, busyPanel, hudRoot, pausePanel;
    Text mainMsg, createMsg, joinMsg, busyText, playersValue, hudCode, pauseCode;
    InputField nameField, codeField;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    GameObject directPanel;
    Text directMsg;
    InputField directIpField, directPortField;
#endif
    int players = 4;
    int killsToWin = 30;
    const int KillsStep = 5, KillsMin = 5, KillsMax = 100;   // kills-to-win selector: multiples of 5
    Text killsValue;
    bool inGame;

    // ------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (NetworkBootstrap.IsDedicatedServerMode() && !Application.isEditor)
        {
            if (menuStage != null) menuStage.SetActive(false);
            enabled = false;
            return;
        }

        if (uiFont != null) UIKit.Font = uiFont;
        EnsureEventSystem();
        BuildUI();
    }

    void Start()
    {
        boot = NetworkBootstrap.Instance;
        if (boot == null) { Debug.LogError("MainMenuUI: NetworkBootstrap not found on the NetworkManager object."); return; }
        PlayerProfile.LoadGuestId();
        awsBackend = GetComponent<AWSBackendClient>();
        if (awsBackend == null) awsBackend = gameObject.AddComponent<AWSBackendClient>();
        boot.OnSessionStarted += EnterGame;
        boot.OnSessionEnded += ExitGame;
        ShowMain("");
        SetStage(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void OnDestroy()
    {
        if (boot == null) return;
        boot.OnSessionStarted -= EnterGame;
        boot.OnSessionEnded -= ExitGame;
    }

    void Update()
    {
        if (!inGame) return;
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetPause(!pausePanel.activeSelf);
    }

    // ------------------------------------------------------------ flow

    void ShowMain(string msg)
    {
        menuRoot.SetActive(true);
        mainPanel.SetActive(true);
        modalDim.SetActive(false);
        createPanel.SetActive(false);
        joinPanel.SetActive(false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        directPanel.SetActive(false);
#endif
        busyPanel.SetActive(false);
        hudRoot.SetActive(false);
        pausePanel.SetActive(false);
        mainMsg.text = msg;
        nameField.text = PlayerProfile.LoadName();
    }

    void ShowCreate()
    {
        modalDim.SetActive(true);
        createPanel.SetActive(true);
        joinPanel.SetActive(false);
        createMsg.text = "";
        SetPlayers(players);
        SetKills(killsToWin);
    }

    void ShowJoin()
    {
        modalDim.SetActive(true);
        joinPanel.SetActive(true);
        createPanel.SetActive(false);
        joinMsg.text = "";
        codeField.text = "";
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Development-only path for testing a desktop client against a direct-IP server.
    void ShowDirect()
    {
        modalDim.SetActive(true);
        createPanel.SetActive(false);
        joinPanel.SetActive(false);
        directPanel.SetActive(true);
        directMsg.text = "";
        directIpField.text = "";
        directPortField.text = "7777";
    }

    void ConfirmDirectConnect()
    {
        if (boot == null) { directMsg.text = "Network bootstrap is unavailable."; return; }
        if (boot.Busy) { directMsg.text = "Another network operation is in progress."; return; }

        string ip = (directIpField.text ?? "").Trim();
        if (!System.Net.IPAddress.TryParse(ip, out var parsed) ||
            System.Net.IPAddress.Any.Equals(parsed) || System.Net.IPAddress.IPv6Any.Equals(parsed))
        {
            directMsg.text = "Enter a valid server IP address.";
            return;
        }

        if (!ushort.TryParse((directPortField.text ?? "").Trim(), out ushort port) || port == 0)
        {
            directMsg.text = "Enter a port from 1 to 65535.";
            return;
        }

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.IsListening || nm.ShutdownInProgress)
        {
            directMsg.text = "A network session is already running or shutting down.";
            return;
        }

        busyText.text = "CONNECTING DIRECT...";
        busyPanel.SetActive(true);
        // Send a minimal local-test ticket so the dedicated server's connection-approval
        // callback receives a non-empty payload. The local-test agent accepts any ticket
        // string and returns { valid: true }. Real production calls go through
        // JoinDirectWithApprovalPayload with a backend-issued ticket.
        byte[] devTicket = System.Text.Encoding.UTF8.GetBytes("dev-direct-local");
        if (!boot.JoinDirectWithApprovalPayload(ip, port, devTicket, "DEV"))
        {
            busyPanel.SetActive(false);
            directMsg.text = boot.Status;
        }
    }
#endif

    void CloseModals()
    {
        modalDim.SetActive(false);
        createPanel.SetActive(false);
        joinPanel.SetActive(false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        directPanel.SetActive(false);
#endif
    }

    void SetPlayers(int n)
    {
        players = Mathf.Clamp(n, 2, 10);
        playersValue.text = players.ToString();
    }

    void SetKills(int n)
    {
        // snap to a multiple of KillsStep, then clamp
        int snapped = Mathf.RoundToInt(n / (float)KillsStep) * KillsStep;
        killsToWin = Mathf.Clamp(snapped, KillsMin, KillsMax);
        killsValue.text = killsToWin.ToString();
    }

    async void ConfirmQuickPlay()
    {
        if (boot == null || boot.Busy) return;

        if (awsBackend == null)
        {
            mainMsg.text = "AWS backend client is unavailable.";
            return;
        }

        busyText.text = "FINDING MATCH (QUICK PLAY)...";
        busyPanel.SetActive(true);
        mainMsg.text = "";

        string key = AWSBackendClient.CreateIdempotencyKey();
        var result = await awsBackend.QuickPlayAsync(key);

        if (!result.Success)
        {
            busyPanel.SetActive(false);
            mainMsg.text = FriendlyErrorMessage(result.Error, result.StatusCode);
            return;
        }

        var assignment = result.Data;
        busyText.text = $"CONNECTING TO {assignment.endpoint.host}:{assignment.endpoint.port}...";

        byte[] ticketBytes = Encoding.UTF8.GetBytes(assignment.ticket);
        string code = !string.IsNullOrEmpty(assignment.roomCode) ? assignment.roomCode : "QUICKPLAY";

        bool started = boot.JoinDirectWithApprovalPayload(assignment.endpoint.host, (ushort)assignment.endpoint.port, ticketBytes, code);
        if (!started)
        {
            busyPanel.SetActive(false);
            mainMsg.text = boot.Status;
        }
    }

    async void ConfirmCreateAwsRoom()
    {
        if (boot == null || boot.Busy) return;

        if (awsBackend == null)
        {
            createMsg.text = "AWS backend client is unavailable.";
            return;
        }

        busyText.text = "CREATING AWS ROOM...";
        busyPanel.SetActive(true);
        createMsg.text = "";

        string createKey = AWSBackendClient.CreateIdempotencyKey();
        var createResult = await awsBackend.CreateGuestRoomAsync(players, killsToWin, createKey);

        if (!createResult.Success)
        {
            busyPanel.SetActive(false);
            createMsg.text = FriendlyErrorMessage(createResult.Error, createResult.StatusCode);
            return;
        }

        string roomCode = createResult.Data.roomCode;
        busyText.text = $"RESERVING SERVER (ROOM {roomCode})...";

        string joinKey = AWSBackendClient.CreateIdempotencyKey();
        var joinResult = await awsBackend.JoinGuestRoomAsync(roomCode, joinKey);

        if (!joinResult.Success)
        {
            busyPanel.SetActive(false);
            createMsg.text = $"Room {roomCode} created, but server assignment failed: {FriendlyErrorMessage(joinResult.Error, joinResult.StatusCode)}";
            return;
        }

        var assignment = joinResult.Data;
        busyText.text = $"CONNECTING TO {assignment.endpoint.host}:{assignment.endpoint.port}...";

        byte[] ticketBytes = Encoding.UTF8.GetBytes(assignment.ticket);
        bool started = boot.JoinDirectWithApprovalPayload(assignment.endpoint.host, (ushort)assignment.endpoint.port, ticketBytes, roomCode);
        if (!started)
        {
            busyPanel.SetActive(false);
            createMsg.text = boot.Status;
        }
    }

    async void ConfirmCreateRelay()
    {
        if (boot == null || boot.Busy) return;
        busyText.text = "CREATING RELAY ROOM...";
        busyPanel.SetActive(true);

        var mm = FindFirstObjectByType<MatchManager>();
        if (mm != null) mm.scoreLimit = killsToWin;
        else Debug.LogWarning("MainMenuUI: no MatchManager in the scene, kills-to-win will not apply.");

        bool ok = await boot.CreateRoomAsync(players);
        busyPanel.SetActive(false);
        if (!ok) createMsg.text = boot.Status;
    }

    async void ConfirmJoinAwsRoom()
    {
        if (boot == null || boot.Busy) return;

        string code = (codeField.text ?? "").Trim().ToUpperInvariant();
        if (code.Length != 6)
        {
            joinMsg.text = "Enter a valid 6-character room code.";
            return;
        }

        if (awsBackend == null)
        {
            joinMsg.text = "AWS backend client is unavailable.";
            return;
        }

        busyText.text = $"JOINING AWS ROOM {code}...";
        busyPanel.SetActive(true);
        joinMsg.text = "";

        string joinKey = AWSBackendClient.CreateIdempotencyKey();
        var joinResult = await awsBackend.JoinGuestRoomAsync(code, joinKey);

        if (!joinResult.Success)
        {
            busyPanel.SetActive(false);
            joinMsg.text = FriendlyErrorMessage(joinResult.Error, joinResult.StatusCode);
            return;
        }

        var assignment = joinResult.Data;
        busyText.text = $"CONNECTING TO {assignment.endpoint.host}:{assignment.endpoint.port}...";

        byte[] ticketBytes = Encoding.UTF8.GetBytes(assignment.ticket);
        bool started = boot.JoinDirectWithApprovalPayload(assignment.endpoint.host, (ushort)assignment.endpoint.port, ticketBytes, code);
        if (!started)
        {
            busyPanel.SetActive(false);
            joinMsg.text = boot.Status;
        }
    }

    async void ConfirmJoinRelay()
    {
        if (boot == null || boot.Busy) return;
        string code = (codeField.text ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { joinMsg.text = "Enter a room code."; return; }

        busyText.text = "JOINING RELAY ROOM...";
        busyPanel.SetActive(true);
        bool ok = await boot.JoinRoomAsync(code);
        busyPanel.SetActive(false);
        if (!ok) joinMsg.text = boot.Status;
    }

    static string FriendlyErrorMessage(string error, long statusCode)
    {
        if (string.IsNullOrWhiteSpace(error))
            return statusCode != 0 ? $"Request failed (HTTP {statusCode})." : "Could not reach AWS backend.";

        if (error.Contains("matchmaking_disabled"))
            return "Matchmaking is currently disabled in backend config.";
        if (error.Contains("no_servers_available"))
            return "No dedicated game servers are currently available.";
        if (error.Contains("room_not_found"))
            return "Room not found. Check the room code.";
        if (error.Contains("room_full"))
            return "Room is full.";
        if (error.Contains("room_expired"))
            return "Room has expired.";

        return error;
    }

    void EnterGame()
    {
        inGame = true;
        menuRoot.SetActive(false);
        busyPanel.SetActive(false);
        pausePanel.SetActive(false);
        hudRoot.SetActive(true);
        hudCode.text = "ROOM  " + boot.RoomCode + "     ESC: MENU";
        pauseCode.text = boot.RoomCode;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        Debug.Log($"[InputDiag][Menu] Gameplay entered; sessionActive={boot.SessionActive}, connected={NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}.");
        StartCoroutine(HideStageWhenPlayerExists());
    }

    void ExitGame(string reason)
    {
        inGame = false;
        StopAllCoroutines();
        SetStage(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Debug.Log($"[InputDiag][Menu] Gameplay exited; reason={reason}, sessionActive={boot != null && boot.SessionActive}, listening={NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}.");
        ShowMain(reason);
    }

    IEnumerator HideStageWhenPlayerExists()
    {
        var nm = NetworkManager.Singleton;
        float t = 0f;
        while (t < 10f && (nm.LocalClient == null || nm.LocalClient.PlayerObject == null))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        yield return null;
        SetStage(false);
    }

    void SetPause(bool on)
    {
        pausePanel.SetActive(on);
        Cursor.lockState = on ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = on;
        Debug.Log($"[InputDiag][Menu] Pause state changed; paused={on}, inGame={inGame}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}, focused={Application.isFocused}, connected={NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient}.");
    }

    void SetStage(bool on)
    {
        if (menuCamera != null)
        {
            menuCamera.enabled = on;
            var al = menuCamera.GetComponent<AudioListener>();
            if (al != null) al.enabled = on;
        }
        if (menuStage != null) menuStage.SetActive(on);
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ------------------------------------------------------------ UI construction

    void BuildUI()
    {
        var cgo = new GameObject("MenuCanvas");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();
        Transform root = cgo.transform;

        // ---------------- menu root
        var menuRt = UIKit.Rect("MenuRoot", root); UIKit.Fill(menuRt);
        menuRoot = menuRt.gameObject;

        var vig = UIKit.Img(menuRt, "Vignette", Color.black, UIKit.Vignette);
        UIKit.Fill(vig.rectTransform);

        var shade = UIKit.Img(menuRt, "LeftShade", new Color(0f, 0f, 0f, 0.9f), UIKit.GradientRight);
        shade.rectTransform.anchorMin = new Vector2(0f, 0f);
        shade.rectTransform.anchorMax = new Vector2(0f, 1f);
        shade.rectTransform.pivot = new Vector2(0f, 0.5f);
        shade.rectTransform.anchoredPosition = Vector2.zero;
        shade.rectTransform.sizeDelta = new Vector2(1150f, 0f);

        // ---------------- main panel
        var main = UIKit.Rect("Main", menuRt); UIKit.Fill(main);
        mainPanel = main.gameObject;

        var stripe = UIKit.Img(main, "Stripe", UIKit.Amber);
        UIKit.Place(stripe.rectTransform, L, new Vector2(70f, 250f), new Vector2(12f, 150f));
        UIKit.Skew(stripe, Sk);

        if (logoSprite != null)
        {
            var logo = UIKit.Img(main, "Logo", Color.white, logoSprite);
            logo.preserveAspect = true;
            UIKit.Place(logo.rectTransform, L, new Vector2(110f, 250f), new Vector2(900f, 260f));
        }
        else
        {
            var title = UIKit.Label(main, gameTitle, 112, TextAnchor.MiddleLeft, L, new Vector2(110f, 250f),
                                    new Vector2(1200f, 170f), FontStyle.Bold, Sk);
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            title.verticalOverflow = VerticalWrapMode.Overflow;
        }

        UIKit.Label(main, subtitle, 30, TextAnchor.MiddleLeft, L, new Vector2(116f, 150f),
                    new Vector2(900f, 44f), FontStyle.Bold, Sk, UIKit.Amber);

        UIKit.MakeButton(main, "QUICK PLAY", L, new Vector2(110f, 95f), new Vector2(470f, 78f), true, ConfirmQuickPlay, 34);
        UIKit.MakeButton(main, "CREATE ROOM", L, new Vector2(110f, 10f), new Vector2(470f, 78f), false, ShowCreate, 34);
        UIKit.MakeButton(main, "JOIN ROOM", L, new Vector2(110f, -75f), new Vector2(470f, 78f), false, ShowJoin, 34);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        UIKit.MakeButton(main, "DEV DIRECT CONNECT", L, new Vector2(110f, -160f), new Vector2(470f, 78f), false, ShowDirect, 30);
        UIKit.MakeButton(main, "QUIT", L, new Vector2(110f, -245f), new Vector2(470f, 78f), false, Quit, 34);
#else
        UIKit.MakeButton(main, "QUIT", L, new Vector2(110f, -160f), new Vector2(470f, 78f), false, Quit, 34);
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        mainMsg = UIKit.Label(main, "", 26, TextAnchor.MiddleLeft, L, new Vector2(112f, -330f),
#else
        mainMsg = UIKit.Label(main, "", 26, TextAnchor.MiddleLeft, L, new Vector2(112f, -225f),
#endif
                              new Vector2(900f, 40f), FontStyle.Bold, Sk, UIKit.Warn);

        UIKit.Label(main, "QUICK PLAY WITH MATCHMAKING, OR CREATE AND JOIN ROOMS", 20, TextAnchor.MiddleLeft,
                    new Vector2(0f, 0f), new Vector2(112f, 60f), new Vector2(1000f, 30f), FontStyle.Normal, Sk, UIKit.Soft);

        var top = new Vector2(1f, 1f);
        UIKit.Label(main, "USERNAME", 20, TextAnchor.MiddleRight, top, new Vector2(-40f, -36f),
                    new Vector2(340f, 28f), FontStyle.Bold, Sk, UIKit.Amber);
        nameField = UIKit.MakeInput(main, "YOUR NAME", top, new Vector2(-40f, -70f), new Vector2(340f, 58f),
                                    28, TextAnchor.MiddleLeft, 16);
        nameField.onEndEdit.AddListener(s =>
        {
            string clean = PlayerProfile.Clean(s);
            nameField.text = clean;
            PlayerProfile.SaveName(clean);
        });

        // ---------------- modal dim (blocks clicks on the main panel)
        var dim = UIKit.Img(menuRt, "ModalDim", new Color(0f, 0f, 0f, 0.7f), null, true);
        UIKit.Fill(dim.rectTransform);
        modalDim = dim.gameObject;

        // ---------------- create room
        var cr = Modal(menuRt, "Create", new Vector2(760f, 690f), "CREATE ROOM");
        createPanel = cr.gameObject;

        // --- number of players
        UIKit.Label(cr, "NUMBER OF PLAYERS", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 180f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(cr, "-", C, new Vector2(-150f, 110f), new Vector2(84f, 84f), false, () => SetPlayers(players - 1), 48);
        playersValue = UIKit.Label(cr, "4", 72, TextAnchor.MiddleCenter, C, new Vector2(0f, 110f),
                                   new Vector2(160f, 90f), FontStyle.Bold, Sk);
        UIKit.MakeButton(cr, "+", C, new Vector2(150f, 110f), new Vector2(84f, 84f), false, () => SetPlayers(players + 1), 48);

        // --- kills required to win (multiples of 5)
        UIKit.Label(cr, "KILLS TO WIN", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 20f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(cr, "-", C, new Vector2(-150f, -50f), new Vector2(84f, 84f), false, () => SetKills(killsToWin - KillsStep), 48);
        killsValue = UIKit.Label(cr, "30", 72, TextAnchor.MiddleCenter, C, new Vector2(0f, -50f),
                                 new Vector2(160f, 90f), FontStyle.Bold, Sk);
        UIKit.MakeButton(cr, "+", C, new Vector2(150f, -50f), new Vector2(84f, 84f), false, () => SetKills(killsToWin + KillsStep), 48);

        UIKit.Label(cr, "FIRST TEAM TO REACH IT WINS  /  TEAMS ARE BALANCED AUTOMATICALLY", 18, TextAnchor.MiddleCenter, C,
                    new Vector2(0f, -120f), new Vector2(700f, 30f), FontStyle.Normal, Sk, UIKit.Soft);

        UIKit.MakeButton(cr, "CREATE AWS ROOM", C, new Vector2(-160f, -185f), new Vector2(280f, 74f), true, ConfirmCreateAwsRoom, 27);
        UIKit.MakeButton(cr, "CREATE RELAY", C, new Vector2(160f, -185f), new Vector2(280f, 74f), false, ConfirmCreateRelay, 27);
        UIKit.MakeButton(cr, "EXIT", C, new Vector2(0f, -265f), new Vector2(300f, 56f), false, CloseModals, 28);
        createMsg = UIKit.Label(cr, "", 20, TextAnchor.MiddleCenter, C, new Vector2(0f, -320f),
                                new Vector2(700f, 48f), FontStyle.Bold, Sk, UIKit.Warn);

        // ---------------- join room
        var jr = Modal(menuRt, "Join", new Vector2(760f, 560f), "JOIN ROOM");
        joinPanel = jr.gameObject;
        UIKit.Label(jr, "ENTER ROOM CODE", 24, TextAnchor.MiddleCenter, C, new Vector2(0f, 90f),
                    new Vector2(660f, 36f), FontStyle.Bold, Sk, UIKit.Amber);
        codeField = UIKit.MakeInput(jr, "ABC123", C, new Vector2(0f, 18f), new Vector2(460f, 88f),
                                    48, TextAnchor.MiddleCenter, 6);
        codeField.onValidateInput = (text, index, ch) => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '\0';
        UIKit.MakeButton(jr, "JOIN AWS ROOM", C, new Vector2(-160f, -105f), new Vector2(280f, 74f), true, ConfirmJoinAwsRoom, 27);
        UIKit.MakeButton(jr, "JOIN RELAY", C, new Vector2(160f, -105f), new Vector2(280f, 74f), false, ConfirmJoinRelay, 27);
        UIKit.MakeButton(jr, "BACK", C, new Vector2(0f, -185f), new Vector2(300f, 56f), false, CloseModals, 28);
        joinMsg = UIKit.Label(jr, "", 20, TextAnchor.MiddleCenter, C, new Vector2(0f, -245f),
                              new Vector2(700f, 40f), FontStyle.Bold, Sk, UIKit.Warn);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ---------------- direct connect (editor / development builds only)
        var dp = Modal(menuRt, "DirectConnect", new Vector2(760f, 560f), "DEV DIRECT CONNECT");
        directPanel = dp.gameObject;
        UIKit.Label(dp, "CONNECT TO A DIRECT-IP SERVER", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, 130f),
                    new Vector2(660f, 32f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.Label(dp, "SERVER IP", 20, TextAnchor.MiddleLeft, C, new Vector2(-230f, 65f),
                    new Vector2(180f, 30f), FontStyle.Bold, Sk, UIKit.Soft);
        directIpField = UIKit.MakeInput(dp, "127.0.0.1", C, new Vector2(55f, 65f), new Vector2(380f, 58f),
                                        26, TextAnchor.MiddleLeft, 45);
        UIKit.Label(dp, "PORT", 20, TextAnchor.MiddleLeft, C, new Vector2(-230f, -15f),
                    new Vector2(180f, 30f), FontStyle.Bold, Sk, UIKit.Soft);
        directPortField = UIKit.MakeInput(dp, "7777", C, new Vector2(55f, -15f), new Vector2(380f, 58f),
                                          26, TextAnchor.MiddleLeft, 5);
        directPortField.onValidateInput = (text, index, ch) => char.IsDigit(ch) ? ch : '\0';
        UIKit.Label(dp, "DEVELOPMENT BUILDS ONLY — DOES NOT USE RELAY", 17, TextAnchor.MiddleCenter, C,
                    new Vector2(0f, -92f), new Vector2(680f, 30f), FontStyle.Normal, Sk, UIKit.Warn);
        UIKit.MakeButton(dp, "CONNECT", C, new Vector2(-160f, -170f), new Vector2(280f, 74f), true, ConfirmDirectConnect, 30);
        UIKit.MakeButton(dp, "BACK", C, new Vector2(160f, -170f), new Vector2(280f, 74f), false, CloseModals, 30);
        directMsg = UIKit.Label(dp, "", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, -245f),
                                 new Vector2(700f, 40f), FontStyle.Bold, Sk, UIKit.Warn);
#endif

        // ---------------- busy overlay
        var busy = UIKit.Img(root, "Busy", new Color(0f, 0f, 0f, 0.75f), null, true);
        UIKit.Fill(busy.rectTransform);
        busyText = UIKit.Label(busy.transform, "CONNECTING...", 56, TextAnchor.MiddleCenter, C, Vector2.zero,
                               new Vector2(1000f, 100f), FontStyle.Bold, Sk);
        busyPanel = busy.gameObject;

        // ---------------- in-game room code pill (bottom centre)
        var pill = UIKit.Img(root, "RoomPill", new Color(0f, 0f, 0f, 0.55f));
        UIKit.Place(pill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(520f, 44f));
        UIKit.Skew(pill, Sk);
        var pillEdge = UIKit.Img(pill.transform, "Edge", UIKit.Amber);
        UIKit.Place(pillEdge.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(6f, 44f));
        UIKit.Skew(pillEdge, Sk);
        hudCode = UIKit.Label(pill.transform, "ROOM", 22, TextAnchor.MiddleCenter, C, Vector2.zero,
                              new Vector2(500f, 44f), FontStyle.Bold, Sk);
        hudRoot = pill.gameObject;

        // ---------------- pause panel
        var pa = Modal(root, "Pause", new Vector2(700f, 470f), "PAUSED");
        pausePanel = pa.gameObject;
        UIKit.Label(pa, "ROOM CODE", 22, TextAnchor.MiddleCenter, C, new Vector2(0f, 80f),
                    new Vector2(560f, 30f), FontStyle.Bold, Sk, UIKit.Soft);
        pauseCode = UIKit.Label(pa, "------", 64, TextAnchor.MiddleCenter, C, new Vector2(0f, 25f),
                                new Vector2(560f, 80f), FontStyle.Bold, Sk, UIKit.Amber);
        UIKit.MakeButton(pa, "COPY CODE", C, new Vector2(0f, -55f), new Vector2(320f, 60f), false,
                         () => GUIUtility.systemCopyBuffer = boot.RoomCode, 24);
        UIKit.MakeButton(pa, "RESUME", C, new Vector2(-160f, -145f), new Vector2(290f, 70f), true, () => SetPause(false), 28);
        UIKit.MakeButton(pa, "LEAVE ROOM", C, new Vector2(160f, -145f), new Vector2(290f, 70f), false, () => boot.Leave(), 28);
    }

    /// <summary>Dark panel with an amber top bar and a big angled title.</summary>
    RectTransform Modal(Transform parent, string name, Vector2 size, string title)
    {
        var img = UIKit.Img(parent, name, UIKit.Ink, null, true);
        UIKit.Place(img.rectTransform, C, Vector2.zero, size);

        var bar = UIKit.Img(img.transform, "Bar", UIKit.Amber);
        UIKit.Place(bar.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(size.x, 6f));

        UIKit.Label(img.transform, title, 56, TextAnchor.MiddleCenter, C, new Vector2(0f, size.y / 2f - 75f),
                    new Vector2(size.x - 40f, 80f), FontStyle.Bold, Sk);
        return img.rectTransform;
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        var module = go.AddComponent<InputSystemUIInputModule>();
        module.AssignDefaultActions();
    }
}
