# 5v5 Team Deathmatch (Unity + Netcode for GameObjects) — Project Handoff

Paste or attach this file at the start of a new chat. It contains everything needed to continue, or to rebuild from scratch.
Last updated: Oct 2026. Items marked **(UNVERIFIED)** were given but not confirmed as applied or working.
Unity project name on the Unity Cloud dashboard: **ACC_Shooter**.

---

## 1. Goal and requirements

Multiplayer first-person team deathmatch, **5v5 (10 players)**.
1. Multiplayer supporting 5v5.
2. Character with rifle animations (Mixamo) that looks like it holds a gun.
3. **First person for yourself, third person for everyone else.**
4. Shooting damages **opponents only** (friendly fire off).
5. Auto team assignment, balanced: 3 players = 2v1, 5 = 3v2, never 4v1.
6. Proper network system, multiplayer testing, **AWS dedicated-server compatibility**.
7. **Rooms with a join code** (no typing IP/port), login-style menu, username.
8. In-game UI: team scores, scoreboard, kill feed with humorous lines, all-player top-killer leaderboard, host-chosen kills-to-win.
9. **Cloud (next phase, handled by a friend):** Google sign-in, AWS storage/database, web (WebGL) hosting. See sections 12 to 14.

User rules: **always use the NEW Input System**; give steps in simple, unambiguous terms; user lists requirements clearly each time.

## 2. Environment

- **Unity Editor 6000.0.84f1** (Unity 6.0 LTS). The project was created in 6000.0.82f1 and has been opened and fully play-tested in **6000.0.84f1**; everyone on the team must use **6000.0.84f1** (read `ProjectSettings/ProjectVersion.txt` after the first open). Unity 6 style project: `InputSystem_Actions` asset, URP, Terrain with trees. Package versions are pinned in `Packages/manifest.json` and `Packages/packages-lock.json` (commit both).
- Packages: Netcode for GameObjects, Unity Transport, Input System, Multiplayer Play Mode, **Multiplayer Services** (`com.unity.services.multiplayer`, replaces deprecated Relay/Lobby), **Authentication**.
- Active Input Handling = Input System Package (New).
- Project is linked to Unity online services (Project ID + organization present in Project Settings > Services). Relay/Sessions needed no extra dashboard enabling.
- Character: Mixamo FBX `Ch15_nonPBR`, Humanoid rig. Guns: first-person `assault2`, third-person `WorldGun`.
- Tested so far: Multiplayer Play Mode, **2 players**. The game has **not been built yet** (editor only).
- Backups: Git or equivalent is strongly advised (an unsaved scene/prefab was lost once). Ctrl+S after each step.

## 3. Status

### Working (user play-tested, full loop)
- Title screen, **Create Room** (players 2–10, kills to win in steps of 5) and **Join Room** by 6-character code, wrong-code and room-full messages, Leave Room, pause menu.
- Teams auto-assigned, spawn at team spawn points, shooting, damage to enemies only, reload, death, **respawn**.
- **Kill mechanics:** kills and deaths credited, **team score counter**, **kill limit** ends the match, winner screen, reset.
- Kill feed (funny phrases), scoreboard (Tab), top-killers list.
- Character material fixed, hand-held gun adjusted ("looks perfect").
- `Tools > TDM > Validate Setup` reported ALL GOOD earlier.

### Open / carry-over (not re-confirmed after the UI work)
1. **Animations:** earlier only Idle played (Walk/Run/Jump did not). Diagnostic `PlayerAnimatorDriver` was provided. Re-check Debug Controller / Debug Speed fields and the Animator window at runtime.
2. **Remote-player lag/choppiness** was likely editor throttling (Interaction Mode = No Throttling, Tick Rate 60). Measure with the FPS + Ping HUD in a real build.
3. Optional shadow fix (`PlayerViewSetup` removing the extra gun in your own shadow). Undecided.
4. Confirm the gun adjustment is saved in the prefab.

### Not built yet
- **VFX, sound, music, extra animations and polish** (muzzle flash prefab, impacts, decals, SFX, menu music).
- Health/ammo HUD, hit markers, spectator camera while dead, left-hand IK, upper-body aim sync.
- **Any build** (desktop Development build, Linux server build, WebGL build).
- Tests with 3–10 players, rejoin balance, 11th player refused, dedicated server on AWS.
- **All cloud work** (section 12).

## 4. Game flow and controls

Title screen → **Create Room** (players, kills to win → code appears) or **Join Room** (type code) → spawn on team → play. Hold **Tab** = scoreboard (key configurable on `GameHUD`). **Esc** = pause menu (Copy Code / Resume / Leave Room). First team to the kill limit wins; scoreboard shows "TEAM X WINS", scores reset after 10 s. Leaving returns to the title screen.

## 5. Scripts

### Runtime (`Assets/Scripts`)
| Script | Role | Key details |
|---|---|---|
| `Health` | Server-authoritative health | `MaxHealth=100`; `NetworkVariable<float> CurrentHealth`; `IsDead`; `TakeDamage(float)` (server only); **`TakeDamage(float, ulong attackerId)` overload + `LastAttackerId`** (see section 6); `ResetHealth()`; events `OnHealthChanged`, `OnDied`. On Player and on shootable targets. |
| `WeaponController` | Hitscan rifle | New Input System (`Player/Attack`, `Player/Reload`, fallback R). `roundsPerMinute=600`, `damage=30`, `range=150`, `friendlyFire=false`, `magazineSize=30`, `startingReserveAmmo=120`, reload times, recoil, spread, `requireLockedCursor`. Server validates ammo, fire rate, origin distance, reload. NetworkVariables `AmmoInMag`, `ReserveAmmo`, `IsReloading`. **Now calls `TakeDamage(damage, OwnerClientId)`.** Skips damage when teams match. |
| `PlayerTeam` | Teams, spawn, respawn | `NetworkVariable<int> Team` (-1 none, 0 = A blue, 1 = B red); fills the smaller team; static `All`; owner requests spawn via ServerRpc, server sends `TeleportClientRpc`; `respawnDelay=3`; on death resets health + ammo and teleports; body tint; dead players hidden/hitbox off. |
| `TeamSpawnPoint` | Spawn marker | `team` 0/1; `GetRandom(team)`; 3 per team in the scene. |
| `PlayerViewSetup` | First/third person | Owner: camera, audio, first-person gun, body shadow-only. Others: body + hand gun. |
| `PlayerAnimatorDriver` | Animation from movement | Writes `Speed`, `MoveX`, `MoveZ`, `Grounded`, `Jump`. No animator syncing needed. |
| `PlayerController` | Movement/look | New Input System, owner only. **Verify cursor handling:** the pause menu and GameHUD assume the cursor is locked in game; Tab may be used by an older revision (change `GameHUD.scoreboardKey` if it clashes). |
| `NetworkBootstrap` | Session layer (**rewritten**) | On the NetworkManager object. `CreateRoomAsync(players)`, `JoinRoomAsync(code)`, `Leave()` using **Multiplayer Services Sessions** (`SessionOptions{MaxPlayers}.WithRelayNetwork()`, `session.Code`, `JoinSessionByCodeAsync`); anonymous Unity Authentication with a random profile per instance; connection approval (`maxPlayers`, "Room is full"); events `OnSessionStarted`, `OnSessionEnded(reason)`, `OnStatus`; properties `RoomCode`, `Status`, `Busy`, `SessionActive`; `JoinDirect(ip, port)` / `HostDirect(port)` kept for dev; **dedicated server mode** with `-server` / `-port N` / Dedicated Server build (direct IP, `listenAddress=0.0.0.0`, 60 fps); FPS + Ping debug label. |
| `MainMenuUI` | Menu, pause, room pill (**rewritten**) | On NetworkManager object. Built entirely in code. Title + angled buttons left, 3D heroes right (`MenuStage`), username top-right (PlayerPrefs), Create Room modal (players 2–10 and **kills to win 5–100 step 5**; copies value to the scene `MatchManager.scoreLimit` before the host starts), Join modal (6-char uppercase code), busy overlay, in-game room-code pill (bottom centre), Esc pause menu. Auto-creates an EventSystem with `InputSystemUIInputModule`. Slots: Menu Camera, Menu Stage, Game Title, Subtitle, Ui Font (optional), Logo Sprite (optional). Disables itself in dedicated server mode. |
| `UIKit` / `UISkew` | Shared UI style | Palette (ink, amber, blue, red), generated gradient/vignette sprites (no art files), builders for angled buttons, labels, inputs. `UISkew` slants Image/Text. Vignette strength is in `UIKit.Vignette`; left shade in `MainMenuUI`. |
| `MenuCameraRig` | Menu camera | On MenuCamera. Frames `StageCenter` (distance, height, screen offset, FOV), slow sway, auto-creates blue/red rim lights and a key light. |
| `PlayerProfile` | Username + stats (on Player root) | `PlayerName` (FixedString32, server-written, submitted by owner via ServerRpc from PlayerPrefs), **`Kills`, `Deaths`** NetworkVariables, `DisplayName`, `ServerResetStats()`, static `LoadName/SaveName/Clean`. |
| `MatchManager` | Match state (scene NetworkObject, **not a prefab**) | Server polls every 0.1 s each player's `Health.IsDead`; on death reads `LastAttackerId`, increments `Deaths`/`Kills` and team score, picks a random phrase, sends `KillFeedClientRpc`. NetworkVariables `ScoreA`, `ScoreB`, `ScoreLimit`, `Winner`. `scoreLimit` and `restartDelay` public. **Resets all state in `OnNetworkSpawn`** (scene object is reused between rooms). Static `KillPhrases` (38) and `SuicidePhrases` (5) with `{k}`/`{v}` tokens; static event `OnKillFeed`. |
| `GameHUD` | In-game HUD (code-built, on NetworkManager object) | Team score bar (top centre, "FIRST TO N", "YOUR TEAM"), kill feed (top-left, 6 s, team-coloured names, own events highlighted), top 3 killers (top-right, all teams), Tab scoreboard (Blue table, Red table, most-kills ranking), winner title. Reads `PlayerProfile` + `PlayerTeam` via `FindObjectsByType`. |
| `WorldGunTuner` | TEMPORARY helper | Remove after use. |
| `ClientNetworkTransform`, `Crosshair`, `MuzzleFlashController`, `TracerFade`, `ViewModelMotion`, `HealthBarUI` (user) | Existing | Unchanged. |

### Editor (`Assets/Editor`)
- `TDMAnimatorBuilder` — Tools > TDM > Build Rifle Animator Controller.
- `TDMSetupValidator` — Tools > TDM > Validate Setup (select the Player prefab first).

### Deleted on purpose
`NetworkButtons`, `TargetHealth`, `WorldGunVisibility`, `PlayerAnimatorBuilder`, the standalone-Relay version of `NetworkBootstrap`.

## 6. Required edits to existing scripts (kill credit)

`Health.cs` (inside the class):
```csharp
[System.NonSerialized] public ulong LastAttackerId = ulong.MaxValue;
public void TakeDamage(float amount, ulong attackerId)
{
    LastAttackerId = attackerId;
    TakeDamage(amount);
}
```
`WeaponController.cs`: every `TakeDamage(x)` call on a target becomes `TakeDamage(x, OwnerClientId)`.

## 7. Player prefab layout

```
Player                      NetworkObject, ClientNetworkTransform, CharacterController (H1.8 R0.4 C0,0,0 step .3 slope 45),
│                           CapsuleCollider hitbox (NOT trigger, H1.8 R0.4 C0,0,0), PlayerController, Health(100),
│                           PlayerTeam, WeaponController, PlayerAnimatorDriver, PlayerViewSetup, Crosshair,
│                           PlayerProfile
│                           (NO Animator on the root)
├─ CameraRoot               local pos (0, 0.7, 0)
│   ├─ Camera               Camera + AudioListener, near clip 0.05
│   └─ assault2             first-person gun, ViewModelMotion, no collider/rigidbody
│       └─ MuzzlePoint
└─ Body                     local pos (0, -0.9, 0)
    └─ Ch15_nonPBR          the ONLY Animator (Controller Player_Rifle, Humanoid, Apply Root Motion OFF)
        └─ ...Hips...RightHand
            └─ WorldGun     third-person gun
                └─ WorldMuzzlePoint
```
Slots as in earlier versions (PlayerController, PlayerViewSetup, PlayerAnimatorDriver, WeaponController with Tracer prefab and Require Locked Cursor ON).

## 8. Scene layout

- **No scene Main Camera.** A camera named **MenuCamera** (Untagged, no Audio Listener) with `MenuCameraRig` (Target = StageCenter). It is switched off when the local player spawns.
- `MenuStage` (empty, on flat ground) → `StageCenter` (empty, blue Z arrow faces the camera) → `HeroBlue` (pos -0.7,0,0 rot 0,12,0) and `HeroRed` (pos 0.7,0,0 rot 0,-12,0): copies of `Ch15_nonPBR` including `WorldGun`, Animator controller `Player_Rifle`, **no NetworkObject**. `MenuStage` is switched off in game (assigned to `MainMenuUI.Menu Stage`).
- `NetworkManager` object: NetworkManager (Default Player Prefab = Player; Network Prefabs List contains Player; Tick Rate 60), Unity Transport, **NetworkBootstrap**, **MainMenuUI**, **GameHUD**.
- **`MatchManager`** empty object: NetworkObject + MatchManager (scene object, not in the Network Prefabs List).
- 6 spawn points (3 per team). Optional target cube (NetworkObject + Health + Collider).
- The menu and HUD canvases (`MenuCanvas`, `HudCanvas`, `EventSystem`) are created at runtime; do not add them by hand.

## 9. Input Actions asset (`InputSystem_Actions`)
Map `Player`: Move, Look, Jump, Sprint, Attack (LMB), Reload (R). Menu and HUD use direct `Keyboard.current` reads (Esc, Tab).

## 10. Key design decisions and gotchas

- Movement is owner-authoritative (`ClientNetworkTransform`). The server never moves players; respawn teleports via owner ClientRpc.
- Non-owner CharacterControllers are disabled, so bullets hit the separate CapsuleCollider hitbox on the root.
- Server is authoritative for health, ammo, kills and score. **Clients never write scores.**
- Kill detection is **polling** (`Health.IsDead`, no dependence on event signatures). Last hitter gets credit.
- `MatchManager` is a scene-placed NetworkObject and relies on NGO re-spawning it for each new room; it resets itself on spawn. If scores ever carry over or never appear after re-hosting, switch to spawning it from the server as a prefab.
- Only the Player root transform is networked; editing a child in one editor window never appears in another. Play-mode edits are lost on Stop.
- A prefab can reference only its own children and project assets.
- Multiplayer Play Mode: set Interaction Mode = No Throttling. About 4 extra windows max; 10-player tests need builds or the AWS server.
- Static lists (`PlayerTeam.All`, `TeamSpawnPoint.All`) assume domain reload stays ON.
- `NetworkManager.ConnectedClientsIds` is server-only.
- Username is stored in `PlayerPrefs` (local only). On WebGL this lives in browser storage and can be cleared, so it must move to the cloud profile.
- Do not run a live Unity project inside a cloud-sync folder (Drive/Dropbox).

## 11. Online services currently used (Unity side)

- **Unity Authentication:** anonymous sign-in only (random profile per running instance so two editor windows do not share an identity).
- **Multiplayer Services Sessions (Relay under the hood):** room creation and join code. The SDK starts the NetworkManager as host/client. MaxPlayers includes the host. Room code is deleted when the host leaves.
- Rooms are **host-based** (the creating player's game is the server). This works for desktop builds. See section 12 for what changes with WebGL and AWS.

## 11b. Networking concepts: host, client, 127.0.0.1, 0.0.0.0

**Terms.** *Host* = a player's game that is also the server (`StartHost`). *Client* = connects to someone else's server (`StartClient`). *Dedicated server* = a headless build that only serves (`StartServer`, no local player).

| Address | Meaning | Who uses it | Reachable from other PCs? |
|---|---|---|---|
| `127.0.0.1` (localhost, loopback) | "This same computer". Packets never leave the machine. | A client connecting to a server on the **same PC** (two editor windows, two exes). | No |
| `0.0.0.0` | **Listen on every network card** of this machine (loopback, LAN, public). It is a *listen* setting, **never a destination**. | The server's **listen address**. | Yes, if the firewall/router allows it |
| LAN IP (`192.168.x.x`, `10.x.x.x`) | The machine's address inside a local network. | Clients on the same Wi-Fi/router. | Only inside that network |
| Public IP / DNS name | The machine's internet address. | Clients over the internet (AWS server). | Yes, if the port is open |

Rules: a server that listens on `127.0.0.1` accepts only local clients. A server must listen on `0.0.0.0` (or its LAN/public interface) to accept remote clients. A client must be given the server's real address (public IP or domain), **never `0.0.0.0`**.

**What the game does today**
| Mode | Addressing | Notes |
|---|---|---|
| **Create / Join Room (room code)** | **No IP or port at all.** Host and client both make *outbound* connections to Unity's relay (Multiplayer Services Sessions). The SDK sets the transport data. | No router port-forwarding, no firewall rules, works across networks. The code is looked up by the service. |
| `NetworkBootstrap.HostDirect(port)` (dev only) | `SetConnectionData("127.0.0.1", port, listenAddress)` with `listenAddress = 0.0.0.0` | Host accepts remote clients. The `127.0.0.1` is only the address the host itself uses. |
| `NetworkBootstrap.JoinDirect(ip, port)` (dev only) | `SetConnectionData(ip, port)` | `ip` = server's LAN or public IP. Same PC: `127.0.0.1`. |
| **Dedicated server** (`-server -port N`) | Server listens on `0.0.0.0:port` (same call, `listenAddress=0.0.0.0`) | Clients connect to the **EC2 public IP/DNS** and port. |

**On AWS (EC2):** the instance has a *private* IP (10.x) and a *public* IP. The server still listens on `0.0.0.0` inside the instance; the **security group must allow the port** (UDP for desktop, the TLS/WebSocket port for WebGL); clients use the public IP or DNS name. Use an Elastic IP or DNS name so the address does not change on restart.

**On WebGL:** UDP does not exist in browsers. Clients use **WebSockets**, and from an HTTPS page they must use **`wss://` with a domain name and TLS certificate**, not a bare IP (see section 12.2). Room codes through Unity Relay hide all of this.

**Why this matters for the cloud phase:** the room-code feature replaces "type IP + port" with a code that maps to an endpoint. In Phase A the mapping is Unity's. In Phase B the **DynamoDB `Rooms` table** stores the endpoint (`host`, `port`, `protocol`) and `GET /rooms/{code}` returns it.

Common mistakes: typing `0.0.0.0` in a client; using the private `10.x` address from outside AWS; forgetting the security group or the UDP-vs-TCP type; testing the server from the same machine with the public IP (use `127.0.0.1` locally); running the Unity server on `127.0.0.1` only, so nobody else can connect.

---

## 12. CLOUD PLAN (AWS) — to be built by the friend

> **Superseded:** the full, current cloud plan (EC2 dedicated servers, DynamoDB, API Gateway, Cognito, S3, pricing, phases, Unity change list) is in **`README_AWS_CLOUD_HANDOVER.md`**. It is **desktop-first** (Windows + macOS clients over UDP); WebGL is now an optional last step. Where this section disagrees with the README, follow the README.



### 12.1 Requirements
- Sign-in with **Google account** and real auth (today it is only anonymous Unity Authentication).
- Use **AWS storage and database** (EC2, S3, DynamoDB) instead of local/browser cache for player data.
- Networking based on the existing **room** feature; host the game as a **web (WebGL) build** if feasible.
- Use **API Gateway** where needed. Candidate services: **Cognito** (auth), **S3** (storage + static hosting), **DynamoDB** (data), **EC2** (game server), Lambda.
- **Keep the budget as low as possible.**

### 12.2 Hard technical constraints (verified from Unity docs/forums, re-check versions)
1. **WebGL can only use WebSockets, in client mode.** Browsers have no UDP. Hosting from a WebGL client is only possible through Unity Relay (`wss`). Both ends need the WebSocket option enabled: a WebGL client can only talk to a server that also uses WebSockets.
2. An **HTTPS page cannot open `ws://`**; the browser blocks it. The server endpoint must be **`wss://` with a valid TLS certificate**, so a **domain name** and TLS termination are required for a self-hosted game server.
3. Connecting by hostname is supported on the client side from **Netcode for GameObjects 2.4+**; use the **latest Unity Transport** (a hostname fix for WSS is reported in 2.5.3).
4. `Unity Relay` with `wss` is the zero-infrastructure way to give WebGL clients rooms.
5. Never put AWS keys in the Unity build or WebGL files (anyone can read them). Clients only hold **Cognito tokens**; servers use an **IAM role**.

### 12.3 Recommended phases (cheapest first)

**Phase A — web hosting + accounts + cloud data (near-zero cost, no game server).**
- Keep Unity Sessions/Relay for rooms (use `wss` for WebGL).
- **S3 + CloudFront** host the WebGL build over HTTPS.
- **Cognito User Pool** with **Google** as federated identity provider (Hosted UI / managed login, authorization-code flow with PKCE).
- **API Gateway (HTTP API, Cognito JWT authorizer) + Lambda + DynamoDB** for profile, username, stats, leaderboard.
- Result: Google login, usernames and stats persisted in DynamoDB instead of browser cache.

**Phase B — AWS game server + own room codes (about the cost of one small EC2).**
- Build a **Linux Dedicated Server** (already supported by `-server`) with the WebSocket transport. Run on **EC2** (start with t3.small for a single room; t3.medium was the earlier estimate for 10 players with headroom).
- **TLS in front of the server** (for example Caddy or nginx with Let's Encrypt on a domain or sub-domain, proxying `wss` to the local game port).
- **DynamoDB `Rooms` table** replaces Unity Sessions as the code registry: create room → generate code → pick/start a server slot → client resolves code to `wss://host:port`.
- Server writes match results to the API using its **IAM role**.
- Removes the dependency on Unity Relay. Decision for the friend: stay on Phase A or go to B.

**Phase C (optional):** start/stop EC2 on demand via Lambda, per-room processes on a port pool, CloudWatch alarms, CI/CD.

### 12.4 Where each AWS service fits
| Service | Use |
|---|---|
| **Cognito** | User pool, Google federation, JWT tokens (ID/access). Callback URLs: the CloudFront URL and `http://localhost` for testing. |
| **S3 (+CloudFront)** | WebGL build files (correct `Content-Encoding` for compressed files, or use Unity's decompression fallback), later avatars/replays via pre-signed URLs. |
| **API Gateway (HTTP API)** | REST front door, JWT authorizer, IAM auth for server-only routes. |
| **Lambda** | Business logic behind the API (profile, rooms, results, leaderboard). |
| **DynamoDB (on-demand)** | Persistent data. |
| **EC2** | Dedicated game server (Phase B). Persistent data goes to DynamoDB/S3, **not** the instance disk (the instance can be replaced). |
| **CloudWatch / Budgets** | Logs, alarms, **budget alerts**. |

### 12.5 Data model (proposal)
- `Players`: PK `userId` (Cognito `sub`); `username`, `createdAt`, `lastLogin`, `kills`, `deaths`, `wins`, `matches`.
- `Rooms`: PK `roomCode`; `hostUserId` or `serverId`, `endpoint` {host, port, protocol}, `maxPlayers`, `killLimit`, `status` (open/playing/closed), `createdAt`, **`ttl`** (auto-expire).
- `Matches`: PK `matchId`; `roomCode`, `winnerTeam`, `endedAt`, players list {userId, team, kills, deaths}. Optional GSI by `userId`.
- Leaderboard: query `Players` with a GSI, or a small `Leaderboard` table updated by the results Lambda.

### 12.6 API contract (v0 proposal, keep in sync with Unity)
| Method + path | Auth | Purpose |
|---|---|---|
| `GET /me` | Cognito JWT | Profile and stats (creates the profile on first login) |
| `PUT /me` | Cognito JWT | Change username (sanitise, same rules as `PlayerProfile.Clean`: no `<` `>`, max 16 chars) |
| `POST /rooms` | Cognito JWT | Body {maxPlayers, killLimit} → {roomCode, endpoint?} |
| `GET /rooms/{code}` | Cognito JWT | Resolve code → endpoint, status, killLimit |
| `DELETE /rooms/{code}` | Cognito JWT (owner) or server | Close room |
| `POST /matches` | **IAM / server secret only** | Server posts final results |
| `GET /leaderboard?top=20` | Cognito JWT | Top killers |

### 12.7 Auth flow
1. Game page loads; if no valid token, redirect to the Cognito Hosted UI → Google.
2. Callback returns an authorization code; exchange (PKCE) for tokens. Easiest in WebGL: a custom WebGL HTML template (JavaScript) handles the redirect and passes tokens into Unity through a `.jslib` bridge.
3. Unity sends the **ID/access token** as `Authorization: Bearer ...` to API Gateway.
4. In-editor testing: add a `#if UNITY_EDITOR` dev-login bypass.
5. Optional: Unity Authentication supports OpenID Connect providers, so Cognito could be linked to Unity services; verify in current docs before relying on it. Otherwise keep Unity anonymous auth only for Relay transport.
6. Phase B: client passes its token in the **connection approval payload**; the server verifies the JWT (Cognito JWKS) before approving.

### 12.8 Unity-side tasks for the cloud phase
1. Login screen before the title screen ("Sign in with Google"), logged-in name shown top-right (replaces the PlayerPrefs username as source of truth; keep PlayerPrefs only as an offline fallback).
2. A `CloudApi` client (UnityWebRequest, bearer token, `CloudConfig` ScriptableObject with non-secret IDs: region, API URL, Cognito domain, client ID, redirect URI).
3. `PlayerProfile` loads username/stats from the API; server posts match results from `MatchManager` at match end.
4. Phase B only: WebSocket mode in `NetworkBootstrap`, room lookup via the API, token in the approval payload.
5. **WebGL compatibility pass (known problems):**
   - **Esc is captured by the browser to release pointer lock**, so the Esc pause menu needs a different key (or pointer-lock-change handling).
   - `GUIUtility.systemCopyBuffer` (Copy Code) does not work in WebGL without JS interop.
   - Hide the Quit button on WebGL (`Application.Quit` does nothing).
   - `Environment.GetCommandLineArgs` (dedicated-server detection) should be wrapped for WebGL.
   - Cursor lock needs a user click first.
   - Terrain with trees may be heavy for WebGL; test performance and memory early. No multithreading.
   - Test that Multiplayer Services/Authentication run correctly in a WebGL build.

### 12.9 Cost control (budget priority)
- Prefer **Phase A** first: S3, CloudFront, Cognito, HTTP API, Lambda and DynamoDB on-demand are intended to cost close to nothing at small scale.
- Free tier as found in Oct 2026 sources (**re-check on the AWS pages; sources disagree on some numbers**): accounts created after 15 July 2025 get a **Free plan with up to about $200 credits, limited to about 6 months**, after which a Free-plan account **closes unless upgraded**; Lambda, DynamoDB (25 GB) and CloudWatch basics are described as always-free within limits; Cognito has a free monthly-active-user allowance (quoted anywhere between 10K and 50K MAU depending on the tier).
- EC2 is the only clearly recurring cost (Phase B): a t3.small/t3.medium on-demand costs on the order of a few to a few tens of dollars per month, plus a **public IPv4 address charge**. Stop the instance when idle.
- Set **AWS Budgets alerts** at a low amount on day one, enable the Free Tier usage alert, tag resources, and delete unused resources (Elastic IPs, EBS volumes).
- Use **infrastructure as code** (AWS CDK, SAM or Terraform) so everything can be torn down and rebuilt.

## 13. Project handover to the friend

### 13.1 Where the scripts live
- All game scripts are in **`Assets/Scripts`** (editor tools in `Assets/Editor`) **inside the Unity project, stored in Git**. That is the single source of truth.
- Unity **compiles them into the build automatically**: every script in `Assets` goes into the player; scripts in `Assets/Editor` are **excluded** from builds. A WebGL build contains compiled WebAssembly, **not your `.cs` files**. You never upload `.cs` files to AWS.
- Development build vs release build use the same scripts (a Development build only adds debugging support).
- **Backend code (Lambda functions, API definition, infrastructure as code) goes in a separate folder `cloud/` at the repo root, next to `Assets/`, or in a separate repo.** Never inside `Assets/` (Unity would try to import it).

### 13.2 Suggested repo layout
```
ACC_Shooter/
├─ Assets/            (game: Scripts, Scenes, Prefabs, Models, ...)
├─ Packages/          (manifest.json, packages-lock.json)
├─ ProjectSettings/   (incl. ProjectVersion.txt, project ID link)
├─ cloud/             (friend's work: infra/, lambdas/, openapi.yaml, README.md)
├─ docs/              (this handoff file, API contract)
└─ .gitignore, .gitattributes
```
- Commit `Assets`, `Packages`, `ProjectSettings` and all **`.meta`** files. Do **not** commit `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`, `Builds/`, generated `*.csproj/*.sln`.
- Use **Git LFS** for large binaries (`*.fbx`, `*.png`, `*.psd`, `*.wav`, `*.mp3`, `*.exr`, large terrain `.asset` files).
- Project settings: Asset Serialization = **Force Text**, Version Control = **Visible Meta Files** (defaults).
- Use a private GitHub (or Unity Version Control) repository. Use branches, for example `main` (stable), `cloud` (friend), `vfx-audio` (you), and merge by pull request.

### 13.3 Handover checklist
1. **Repo access** for the friend (private GitHub invite or Unity Version Control).
2. **Unity access:** invite the friend to the Unity Cloud organization/project **ACC_Shooter** (Members / Delegate members on the dashboard). Same **Editor version** (from `ProjectVersion.txt`), same packages (from `manifest.json`).
3. **AWS access:** create a dedicated AWS account (or AWS Organization). Lock the **root user** with MFA and do not share it. Give the friend an **IAM Identity Center or IAM user** with the permissions needed for Cognito, S3, CloudFront, API Gateway, Lambda, DynamoDB, EC2, CloudWatch, Budgets. Decide who owns billing.
4. **Google Cloud:** create an OAuth consent screen and OAuth client (needed for Cognito Google sign-in). Decide which Google account owns it.
5. **Share:** this file, the API contract (section 12.6, later an `openapi.yaml`), the Unity config approach (`CloudConfig` ScriptableObject, non-secret values only).
6. **Rules:** no AWS keys or secrets in Unity or Git (use SSM Parameter Store / Secrets Manager); clients never write kills/scores; server is authoritative; keep the API contract versioned.
7. Agree on **one integration point** so the two of you do not collide: the friend owns `cloud/` and new scripts `CloudApi`, `CloudConfig`, `AuthService`; the owner of the game changes only the call sites (`MainMenuUI`, `PlayerProfile`, `MatchManager`).

## 14. Build and deployment status

- **Nothing has been built yet.** First builds to make (File > Build Profiles):
  1. **Windows/Mac Development Build**, run two copies, test Create/Join, read FPS and Ping (bottom-left).
  2. **Linux Dedicated Server** build; run `./Game.x86_64 -batchmode -nographics -server -port 7777 -logFile server.log` (inside `screen`); open **UDP 7777** (TCP/WSS port if using WebSockets) in the AWS security group. Note: the dedicated-server path currently uses **direct IP**, not room codes.
  3. **WebGL** build (Phase A/B), tested locally first.
- Add the game scene to the build's Scene List.

## 15. Proposed next steps (in order)

1. Put the project in **Git** (section 13) and do a first **Development Build** test with 2 copies. (Editor version is recorded in section 2.)
2. **Decide early whether WebGL is viable**: make a test WebGL build, check performance and the section 12.8 problems. This decides Phase A vs B.
3. Hand over to the friend (checklist 13.3). Friend starts with Phase A: Cognito + Google, API Gateway + Lambda + DynamoDB, S3 + CloudFront.
4. In parallel (owner): finish open items (animations, lag measurement), the **test pass** (3, 4, 5 players, rejoin, 11th player refused, friendly fire, death mid-reload), then **VFX, sound, music, polish**, health/ammo HUD, spectator camera.
5. After Phase A works, decide on Phase B (EC2 dedicated server with WebSockets, TLS and DynamoDB room registry).

## 16. Rebuilding from scratch (checklist)

1. Install packages: Netcode for GameObjects, Unity Transport, Input System, Multiplayer Play Mode, **Multiplayer Services**, **Authentication**. Set Active Input Handling = New, add the `Reload` action. Link the project in Project Settings > Services.
2. Put all scripts in `Assets/Scripts` (+ the 2 editor scripts in `Assets/Editor`). Apply the two edits in section 6.
3. Download Mixamo rifle Idle/Walk/Run/Jump (FBX for Unity, without skin), set Humanoid, Loop Time on idle/walk/run, bake into pose, build the controller with TDMAnimatorBuilder.
4. Build the Player hierarchy as in section 7 (including `PlayerProfile`), make it a prefab, delete it from the scene.
5. Scene as in section 8: NetworkManager object (NetworkBootstrap + MainMenuUI + GameHUD), MatchManager, MenuCamera + MenuCameraRig, MenuStage with the two heroes, 6 spawn points.
6. Run Tools > TDM > Validate Setup on the Player prefab, fix all red lines.
7. Test with Multiplayer Play Mode: Create Room in one window, Join by code in the other, kill an enemy, check feed, score and scoreboard.
