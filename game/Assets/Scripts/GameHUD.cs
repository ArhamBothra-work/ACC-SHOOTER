using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// In-game HUD, built in code:
///  - top centre: BLUE vs RED team score
///  - top left: kill feed (funny phrases)
///  - top right: top 3 killers (all teams)
///  - hold Tab: scoreboard (both teams + most kills overall)
/// Put on the NetworkManager object.
/// </summary>
public class GameHUD : MonoBehaviour
{
    [Header("Controls")]
    public Key scoreboardKey = Key.Tab;

    [Header("Kill feed")]
    public float feedSeconds = 6f;
    public int maxFeedEntries = 6;

    const float Sk = 0.2f;
    static readonly Vector2 C = new Vector2(0.5f, 0.5f);

    class Entry { public string name; public int team, k, d; public bool me; }
    class Row { public GameObject go; public Image bg; public Text rank, name, k, d; }
    class FeedItem { public GameObject go; public CanvasGroup cg; public float born; }

    NetworkBootstrap boot;
    GameObject root, board;
    bool inGame;
    float nextRefresh;

    Text scoreA, scoreB, limitText, boardTitle, boardScoreA, boardScoreB;
    GameObject youA, youB;

    RectTransform feed;
    readonly List<FeedItem> items = new List<FeedItem>();

    readonly Text[] topName = new Text[3];
    readonly Text[] topKills = new Text[3];

    readonly List<Row> rowsA = new List<Row>();
    readonly List<Row> rowsB = new List<Row>();
    readonly List<Row> rowsAll = new List<Row>();

    // ------------------------------------------------------------ lifecycle

    void Start()
    {
        if (NetworkBootstrap.IsDedicatedServerMode()) { enabled = false; return; }

        boot = NetworkBootstrap.Instance;
        if (boot != null)
        {
            boot.OnSessionStarted += OnStarted;
            boot.OnSessionEnded += OnEnded;
        }
        MatchManager.OnKillFeed += AddFeed;

        Build();
        root.SetActive(false);
    }

    void OnDestroy()
    {
        MatchManager.OnKillFeed -= AddFeed;
        if (boot != null)
        {
            boot.OnSessionStarted -= OnStarted;
            boot.OnSessionEnded -= OnEnded;
        }
    }

    void OnStarted()
    {
        inGame = true;
        ClearFeed();
        root.SetActive(true);
        board.SetActive(false);
        nextRefresh = 0f;
    }

    void OnEnded(string reason)
    {
        inGame = false;
        ClearFeed();
        root.SetActive(false);
    }

    void Update()
    {
        if (!inGame) return;

        // fade + remove old kill feed entries
        for (int i = items.Count - 1; i >= 0; i--)
        {
            float age = Time.unscaledTime - items[i].born;
            if (age >= feedSeconds)
            {
                Destroy(items[i].go);
                items.RemoveAt(i);
            }
            else if (age > feedSeconds - 1f)
            {
                items[i].cg.alpha = feedSeconds - age;
            }
        }

        // scoreboard: hold the key, or auto-show when the match is won
        bool show = Keyboard.current != null && Keyboard.current[scoreboardKey].isPressed;
        var mm = MatchManager.Instance;
        if (mm != null && mm.Winner.Value >= 0) show = true;
        if (board.activeSelf != show) board.SetActive(show);

        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + (show ? 0.2f : 0.5f);
            Refresh(show);
        }
    }

    // ------------------------------------------------------------ data

    List<Entry> Gather()
    {
        var list = new List<Entry>();
        foreach (var p in FindObjectsByType<PlayerProfile>(FindObjectsSortMode.None))
        {
            var t = p.GetComponent<PlayerTeam>();
            list.Add(new Entry
            {
                name = p.DisplayName,
                team = t != null ? t.Team.Value : -1,
                k = p.Kills.Value,
                d = p.Deaths.Value,
                me = p.IsOwner
            });
        }
        list.Sort((a, b) => a.k != b.k ? b.k.CompareTo(a.k) : a.d.CompareTo(b.d));
        return list;
    }

    void Refresh(bool showBoard)
    {
        var mm = MatchManager.Instance;
        int a = mm != null ? mm.ScoreA.Value : 0;
        int b = mm != null ? mm.ScoreB.Value : 0;
        int limit = mm != null ? mm.ScoreLimit.Value : 0;
        int winner = mm != null ? mm.Winner.Value : -1;

        scoreA.text = a.ToString();
        scoreB.text = b.ToString();
        limitText.text = limit > 0 ? "FIRST TO " + limit : "VS";

        var list = Gather();

        int myTeam = -1;
        foreach (var e in list) if (e.me) myTeam = e.team;
        youA.SetActive(myTeam == 0);
        youB.SetActive(myTeam == 1);

        // top 3 killers (all teams)
        for (int i = 0; i < 3; i++)
        {
            bool has = i < list.Count && list[i].k > 0;
            topName[i].gameObject.SetActive(has);
            topKills[i].gameObject.SetActive(has);
            if (!has) continue;
            topName[i].text = (i + 1) + ".  " + list[i].name;
            topName[i].color = UIKit.TeamColor(list[i].team);
            topKills[i].text = list[i].k.ToString();
        }

        if (!showBoard) return;

        if (winner >= 0)
        {
            boardTitle.text = UIKit.TeamName(winner) + " TEAM WINS";
            boardTitle.color = UIKit.TeamColor(winner);
        }
        else
        {
            boardTitle.text = "SCOREBOARD";
            boardTitle.color = Color.white;
        }
        boardScoreA.text = a.ToString();
        boardScoreB.text = b.ToString();

        var teamA = list.FindAll(x => x.team == 0);
        var teamB = list.FindAll(x => x.team == 1);
        FillRows(rowsA, teamA, false);
        FillRows(rowsB, teamB, false);
        FillRows(rowsAll, list, true);
    }

    void FillRows(List<Row> rows, List<Entry> data, bool useTeamColorName)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i >= data.Count) { r.go.SetActive(false); continue; }
            var e = data[i];
            r.go.SetActive(true);
            r.rank.text = (i + 1).ToString();
            r.name.text = e.name;
            r.name.color = useTeamColorName ? UIKit.TeamColor(e.team) : Color.white;
            r.k.text = e.k.ToString();
            r.d.text = e.d.ToString();
            r.bg.color = e.me ? new Color(1f, 0.62f, 0.08f, 0.3f) : new Color(1f, 1f, 1f, 0.06f);
        }
    }

    // ------------------------------------------------------------ kill feed

    void ClearFeed()
    {
        foreach (var it in items) if (it.go != null) Destroy(it.go);
        items.Clear();
    }

    void AddFeed(KillInfo k)
    {
        if (!inGame || feed == null) return;

        string[] list = k.suicide ? MatchManager.SuicidePhrases : MatchManager.KillPhrases;
        string phrase = list[Mathf.Clamp(k.phrase, 0, list.Length - 1)];
        string kName = "<color=#" + UIKit.TeamHex(k.killerTeam) + ">" + k.killer + "</color>";
        string vName = "<color=#" + UIKit.TeamHex(k.victimTeam) + ">" + k.victim + "</color>";
        string msg = phrase.Replace("{k}", kName).Replace("{v}", vName);

        ulong me = NetworkManager.Singleton.LocalClientId;
        bool mine = k.victimId == me || (!k.suicide && k.killerId == me);

        var bg = UIKit.Img(feed, "Kill", mine ? new Color(0.33f, 0.2f, 0.02f, 0.85f) : new Color(0f, 0f, 0f, 0.72f), UIKit.GradientRight);
        bg.rectTransform.sizeDelta = new Vector2(560f, 40f);
        UIKit.Skew(bg, Sk);

        var stripe = UIKit.Img(bg.transform, "Stripe",
                               mine ? UIKit.Amber : UIKit.TeamColor(k.suicide ? k.victimTeam : k.killerTeam));
        UIKit.Place(stripe.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(6f, 40f));
        UIKit.Skew(stripe, Sk);

        var t = UIKit.Txt(bg.transform, msg, 22, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold, Sk);
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        UIKit.Fill(t.rectTransform);
        t.rectTransform.offsetMin = new Vector2(18f, 0f);
        t.rectTransform.offsetMax = new Vector2(-8f, 0f);

        var cg = bg.gameObject.AddComponent<CanvasGroup>();
        items.Add(new FeedItem { go = bg.gameObject, cg = cg, born = Time.unscaledTime });

        while (items.Count > maxFeedEntries)
        {
            Destroy(items[0].go);
            items.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------ construction

    void Build()
    {
        var cgo = new GameObject("HudCanvas");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        root = cgo;
        Transform rt = cgo.transform;

        BuildScoreBar(rt);
        BuildFeed(rt);
        BuildTopKillers(rt);
        BuildBoard(rt);
    }

    void BuildScoreBar(Transform parent)
    {
        var bar = UIKit.Rect("ScoreBar", parent);
        UIKit.Place(bar, new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(1000f, 80f));
        var top = new Vector2(0.5f, 1f);

        // centre box
        var mid = UIKit.Img(bar, "Mid", new Color(0f, 0f, 0f, 0.8f));
        UIKit.Place(mid.rectTransform, top, Vector2.zero, new Vector2(150f, 66f));
        UIKit.Skew(mid, Sk);
        limitText = UIKit.Label(mid.transform, "VS", 20, TextAnchor.MiddleCenter, C, Vector2.zero,
                                new Vector2(150f, 66f), FontStyle.Bold, Sk, UIKit.Amber);

        // blue box (left)
        var ba = UIKit.Img(bar, "Blue", new Color(UIKit.Blue.r, UIKit.Blue.g, UIKit.Blue.b, 0.92f));
        UIKit.Place(ba.rectTransform, top, new Vector2(-241f, 0f), new Vector2(328f, 66f));
        UIKit.Skew(ba, Sk);
        UIKit.Label(ba.transform, "BLUE", 28, TextAnchor.MiddleCenter, C, new Vector2(-95f, 0f),
                    new Vector2(150f, 60f), FontStyle.Bold, Sk);
        scoreA = UIKit.Label(ba.transform, "0", 56, TextAnchor.MiddleCenter, C, new Vector2(85f, 0f),
                             new Vector2(120f, 66f), FontStyle.Bold, Sk);

        // red box (right)
        var rb = UIKit.Img(bar, "Red", new Color(UIKit.Red.r, UIKit.Red.g, UIKit.Red.b, 0.92f));
        UIKit.Place(rb.rectTransform, top, new Vector2(241f, 0f), new Vector2(328f, 66f));
        UIKit.Skew(rb, Sk);
        scoreB = UIKit.Label(rb.transform, "0", 56, TextAnchor.MiddleCenter, C, new Vector2(-85f, 0f),
                             new Vector2(120f, 66f), FontStyle.Bold, Sk);
        UIKit.Label(rb.transform, "RED", 28, TextAnchor.MiddleCenter, C, new Vector2(95f, 0f),
                    new Vector2(150f, 60f), FontStyle.Bold, Sk);

        // "your team" markers
        youA = UIKit.Label(bar, "YOUR TEAM", 18, TextAnchor.MiddleCenter, top, new Vector2(-241f, -72f),
                           new Vector2(328f, 24f), FontStyle.Bold, Sk, UIKit.Amber).gameObject;
        youB = UIKit.Label(bar, "YOUR TEAM", 18, TextAnchor.MiddleCenter, top, new Vector2(241f, -72f),
                           new Vector2(328f, 24f), FontStyle.Bold, Sk, UIKit.Amber).gameObject;
    }

    void BuildFeed(Transform parent)
    {
        feed = UIKit.Rect("KillFeed", parent);
        UIKit.Place(feed, new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(560f, 400f));
        var vlg = feed.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.spacing = 6f;
        vlg.childControlWidth = false;
        vlg.childControlHeight = false;
        vlg.childForceExpandWidth = false;
        vlg.childForceExpandHeight = false;
    }

    void BuildTopKillers(Transform parent)
    {
        var top = new Vector2(1f, 1f);
        var bg = UIKit.Img(parent, "TopKillers", new Color(0f, 0f, 0f, 0.7f), UIKit.GradientLeft);
        UIKit.Place(bg.rectTransform, top, new Vector2(-24f, -24f), new Vector2(340f, 150f));

        UIKit.Label(bg.transform, "TOP KILLERS", 20, TextAnchor.MiddleRight, C, new Vector2(-10f, 52f),
                    new Vector2(300f, 28f), FontStyle.Bold, Sk, UIKit.Amber);

        for (int i = 0; i < 3; i++)
        {
            float y = 16f - i * 36f;
            topName[i] = UIKit.Label(bg.transform, "", 24, TextAnchor.MiddleLeft, C, new Vector2(-42f, y),
                                     new Vector2(240f, 32f), FontStyle.Bold, Sk);
            topKills[i] = UIKit.Label(bg.transform, "", 26, TextAnchor.MiddleRight, C, new Vector2(125f, y),
                                      new Vector2(60f, 32f), FontStyle.Bold, Sk, UIKit.Amber);
        }
    }

    void BuildBoard(Transform parent)
    {
        var dim = UIKit.Img(parent, "Board", new Color(0f, 0f, 0f, 0.7f));
        UIKit.Fill(dim.rectTransform);
        board = dim.gameObject;

        var panel = UIKit.Img(dim.transform, "Panel", UIKit.Ink);
        UIKit.Place(panel.rectTransform, C, Vector2.zero, new Vector2(1180f, 740f));
        var bar = UIKit.Img(panel.transform, "Bar", UIKit.Amber);
        UIKit.Place(bar.rectTransform, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(1180f, 6f));

        boardTitle = UIKit.Label(panel.transform, "SCOREBOARD", 50, TextAnchor.MiddleCenter, C,
                                 new Vector2(0f, 320f), new Vector2(1000f, 70f), FontStyle.Bold, Sk);

        BuildTeamColumn(panel.transform, -300f, UIKit.Blue, "TEAM BLUE", rowsA, true);
        BuildTeamColumn(panel.transform, 300f, UIKit.Red, "TEAM RED", rowsB, false);

        // overall leaderboard
        var strip = UIKit.Img(panel.transform, "AllStrip", new Color(UIKit.Amber.r, UIKit.Amber.g, UIKit.Amber.b, 0.9f));
        UIKit.Place(strip.rectTransform, C, new Vector2(0f, -120f), new Vector2(1100f, 40f));
        UIKit.Skew(strip, Sk);
        UIKit.Label(strip.transform, "MOST KILLS  -  ALL PLAYERS", 24, TextAnchor.MiddleCenter, C, Vector2.zero,
                    new Vector2(1000f, 40f), FontStyle.Bold, Sk, Color.black);
        for (int i = 0; i < 5; i++)
            rowsAll.Add(MakeRow(panel.transform, 0f, -165f - i * 44f, 1100f));
    }

    void BuildTeamColumn(Transform panel, float cx, Color teamColor, string title, List<Row> rows, bool isBlue)
    {
        var head = UIKit.Img(panel, "Head", new Color(teamColor.r, teamColor.g, teamColor.b, 0.92f));
        UIKit.Place(head.rectTransform, C, new Vector2(cx, 235f), new Vector2(540f, 50f));
        UIKit.Skew(head, Sk);
        UIKit.Label(head.transform, title, 28, TextAnchor.MiddleLeft, C, new Vector2(20f, 0f),
                    new Vector2(400f, 50f), FontStyle.Bold, Sk);
        var score = UIKit.Label(head.transform, "0", 36, TextAnchor.MiddleRight, C, new Vector2(-20f, 0f),
                                new Vector2(200f, 50f), FontStyle.Bold, Sk);
        if (isBlue) boardScoreA = score; else boardScoreB = score;

        // column captions
        float w = 540f;
        var cap = UIKit.Rect("Caption", panel);
        UIKit.Place(cap, C, new Vector2(cx, 195f), new Vector2(w, 26f));
        Caption(cap, "#", -w / 2f + 30f, 50f);
        Caption(cap, "PLAYER", -55f, w - 230f, TextAnchor.MiddleLeft);
        Caption(cap, "K", w / 2f - 95f, 80f);
        Caption(cap, "D", w / 2f - 30f, 60f);

        for (int i = 0; i < 5; i++)
            rows.Add(MakeRow(panel, cx, 150f - i * 44f, w));
    }

    void Caption(Transform parent, string s, float x, float width, TextAnchor a = TextAnchor.MiddleCenter)
    {
        UIKit.Label(parent, s, 18, a, C, new Vector2(x, 0f), new Vector2(width, 26f), FontStyle.Bold, Sk, UIKit.Soft);
    }

    Row MakeRow(Transform parent, float cx, float y, float w)
    {
        var bg = UIKit.Img(parent, "Row", new Color(1f, 1f, 1f, 0.06f));
        UIKit.Place(bg.rectTransform, C, new Vector2(cx, y), new Vector2(w, 38f));
        UIKit.Skew(bg, 0.18f);

        var r = new Row { go = bg.gameObject, bg = bg };
        r.rank = Cell(bg.transform, -w / 2f + 30f, 50f, TextAnchor.MiddleCenter);
        r.name = Cell(bg.transform, -55f, w - 230f, TextAnchor.MiddleLeft);
        r.k = Cell(bg.transform, w / 2f - 95f, 80f, TextAnchor.MiddleCenter);
        r.d = Cell(bg.transform, w / 2f - 30f, 60f, TextAnchor.MiddleCenter);
        r.go.SetActive(false);
        return r;
    }

    Text Cell(Transform parent, float x, float width, TextAnchor a)
    {
        return UIKit.Label(parent, "", 24, a, C, new Vector2(x, 0f), new Vector2(width, 38f), FontStyle.Bold, 0.18f);
    }
}
