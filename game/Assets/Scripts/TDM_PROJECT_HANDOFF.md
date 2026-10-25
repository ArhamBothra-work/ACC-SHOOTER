# 5v5 Team Deathmatch (Unity + Netcode for GameObjects) — Project Handoff

Paste or attach this file at the start of a new chat. It contains everything needed to continue, or to rebuild from scratch.
Last updated: Oct 2026. Items marked **(UNVERIFIED)** were given to the user but not confirmed as applied or working.

---

## 1. Goal and requirements

Multiplayer first-person team deathmatch, **5v5 (10 players)**.
1. Multiplayer supporting 5v5.
2. Character with rifle animations (Mixamo) that looks like it holds a gun.
3. **First person for yourself, third person for everyone else** (others see a body holding a gun).
4. Shooting that damages **opponents only** (friendly fire off).
5. Auto team assignment, balanced: 3 players = 2v1, 5 = 3v2, never 4v1.
6. Proper network system, multiplayer testing, **AWS dedicated-server compatibility**.

User rules: **always use the NEW Input System**; give steps in simple, unambiguous terms; user said they will list requirements clearly each time.

## 2. Environment (as known)

- Unity (Unity 6 style: `InputSystem_Actions` asset, URP, Terrain with trees). Exact Unity and Netcode version not confirmed.
- Packages: Netcode for GameObjects, Unity Transport, Input System, Multiplayer Play Mode.
- Active Input Handling = Input System Package (New).
- Character: Mixamo FBX `Ch15_nonPBR`, Humanoid rig. Guns: first-person `assault2`, third-person `WorldGun` (copy of the same model).
- Testing so far: Multiplayer Play Mode, **2 players only**.
- The user's laptop crashed once and lost an unsaved scene and prefab. Advice given: Auto Save, Git, Ctrl+S after each part.

## 3. Status

### Working (user-tested)
- Host + Client connection via the on-screen menu, 2 players.
- Teams auto-assigned and shown ("You are on: Team A/B"). Spawn at team spawn points.
- Shooting, damage to enemy, reloading, respawning, killing an enemy trooper: **"works perfectly"**.
- Character material fixed (was white). Hand-held gun manually adjusted to fit the hand: **"looks perfect"**.
- `Tools > TDM > Validate Setup` reported ALL GOOD on the Player prefab, 3+3 spawn points found.

### Open / unresolved
1. **Animations: only Idle played; Walk/Run/Jump did not.** Diagnostic version of `PlayerAnimatorDriver` + validator parameter check was provided **(UNVERIFIED)**. Next step: check `Debug Controller` and `Debug Speed` fields on the driver, and the Animator window at runtime. Likely causes: wrong controller (old one from deleted `PlayerAnimatorBuilder`), missing parameters, clips not Humanoid, or walk/run clips empty.
2. **Remote-player lag/choppiness** (a jump seen 0.5–1 s late by the other window). Probably editor throttling and a heavy terrain in two editors, not Netcode. Fixes given **(UNVERIFIED)**: Interaction Mode = No Throttling, Tick Rate 60, Interpolate on, lighter terrain/shadows, build a real Development exe. The updated `NetworkBootstrap` HUD (FPS + Ping) had **not yet been applied**, so no measurements exist.
3. Optional shadow fix: updated `PlayerViewSetup` (removes the extra gun in your own shadow). User undecided.
4. Confirm the gun adjustment was saved in the prefab (not lost by editing during Play mode).

### Not built yet
- Sound, UI/HUD (ammo, health, scoreboard), VFX (muzzle flash prefab, impacts, decals).
- Scoring and match end/rounds, kill feed, spectator camera while dead.
- Left-hand IK on the foregrip, proper upper-body aiming (pitch is not synced to remote bodies).
- Tests with 3–10 players, rejoin balance, 11th player refused, dedicated server on AWS with real clients.

## 4. Scripts

### Runtime (`Assets/Scripts`)
| Script | Role | Key details |
|---|---|---|
| `Health` | Server-authoritative health | `public float MaxHealth=100`; `NetworkVariable<float> CurrentHealth`; `IsDead`; `TakeDamage(float)` (server only); `ResetHealth()`; events `OnHealthChanged(cur,max)`, `OnDied`. Debug fields in Inspector. Goes on Player AND on shootable targets (target also needs a NetworkObject). |
| `WeaponController` | Hitscan rifle | New Input System; actions `Player/Attack`, optional `Player/Reload` (falls back to `reloadKey`=R). Public: `roundsPerMinute=600`, `damage=30`, `range=150`, `hitMask`, `friendlyFire=false`, `magazineSize=30`, `startingReserveAmmo=120`, `reloadTime=2.5`, `emptyReloadTime=3.0`, `autoReloadOnEmpty`, recoil (`verticalRecoil .9`, `horizontalRecoil .45`, max, snappiness, recovery), spread bloom, `automatic=true`, `requireLockedCursor`. Server validates ammo, fire rate (tolerance 0.8), origin distance (`maxOriginOffset=3`), reload state. NetworkVariables `AmmoInMag`, `ReserveAmmo`, `IsReloading`; `RefillAmmo()`; event `OnAmmoChanged`; `CurrentSpread`. Presets in component ⋮ menu: AKM / M416. Recoil rotates the **Camera child** in LateUpdate (not CameraRoot). Skips damage when shooter and target `PlayerTeam.Team` match. |
| `PlayerTeam` | Teams, spawn, respawn | `NetworkVariable<int> Team` (-1 none, 0=A blue, 1=B red); fills the smaller team (tie→A); static `All` list; `TeamName`; owner requests first spawn via ServerRpc, server sends `TeleportClientRpc` to owner (disables CharacterController, `NetworkTransform.Teleport`); `respawnDelay=3`, `spawnHeightOffset=1`; on death the server resets health + ammo and teleports; body tint via MaterialPropertyBlock (`tintStrength .35`); dead players: hitbox off, other players' renderers hidden. |
| `TeamSpawnPoint` | Spawn marker | `team` 0/1; static `GetRandom(team)`; gizmo. Scene has 3 per team. |
| `PlayerViewSetup` | First/third person switching | Owner: camera + audio + first-person gun on, body shadow-only. Others: camera off, first-person gun off, body + hand gun visible. Slots: Camera, Audio Listener, View Model(assault2), Body Root, World Gun. Redundant with part of `PlayerController` (harmless). |
| `PlayerAnimatorDriver` | Animation from real movement | Works for owner and remote with no animator syncing. Writes `Speed`, `MoveX`, `MoveZ`, `Grounded`, `Jump`. `groundCheckDistance=1.1`. Latest version validates the controller on spawn, sets root motion off, culling AlwaysAnimate, ignores teleport spikes, has Debug fields. |
| `NetworkBootstrap` | Menu + approval + dedicated server | On NetworkManager object. Host / Client / Server-only menu (IP, port), `maxPlayers=10` via connection approval, `listenAddress=0.0.0.0`, auto-starts server with `-server` (and `-port N`) or Dedicated Server build; `targetFrameRate=60` on server. Latest version: cached HUD with FPS + Ping every 0.5 s, "Connected" status fix. Replaces `NetworkButtons`. |
| `PlayerController` | Movement/look | User's original uses new Input System (Move, Look, Jump, Sprint in map `Player`, input asset cloned for owner), `controller.enabled = IsOwner`. A revised version was provided **(UNVERIFIED if applied)**: removes animation code, blocks input when dead or cursor free, Tab toggles cursor. |
| `WorldGunTuner` | TEMPORARY helper | Cycles 24 orientations of WorldGun via component ⋮ menu. Remove after use. |
| `ClientNetworkTransform` (user) | Owner-authoritative transform | Position XYZ, rotation Y only, scale off, Interpolate on. |
| `Crosshair`, `MuzzleFlashController`, `TracerFade`, `ViewModelMotion`, `HealthBarUI` (user) | Existing | `ViewModelMotion` on first-person gun (Player + CameraRoot slots). `TracerFade.Init(line, duration)` used by `WeaponController`. `HealthBarUI` compiles against new `Health`. Idea (not applied): add `SetTarget(Health)` and bind on enable so late-joiners and the HUD work. |

### Editor (`Assets/Editor`)
- `TDMAnimatorBuilder` — Tools > TDM > Build Rifle Animator Controller. Drag 4 clips (Idle/Walk/Run/Jump), it builds `Player_Rifle.controller` with the 5 parameters, a 1D Locomotion blend tree (Speed 0/5/8), and a Jump state.
- `TDMSetupValidator` — Tools > TDM > Validate Setup. Select the Player root or prefab first. Checks input handling, script versions, required components (incl. hitbox), empty slots, extra Animator on root, humanoid avatar, controller parameters, camera/listener, NetworkManager (player prefab, transport, bootstrap), spawn points, leftover scene Main Camera, leftover `TargetHealth`.

### Deleted on purpose
`NetworkButtons`, `TargetHealth`, `WorldGunVisibility`, `PlayerAnimatorBuilder` (user's own, not mine).

## 5. Player prefab layout

```
Player                      NetworkObject, ClientNetworkTransform, CharacterController (H1.8 R0.4 C0,0,0 step .3 slope 45),
│                           CapsuleCollider hitbox (NOT trigger, H1.8 R0.4 C0,0,0), PlayerController, Health(100),
│                           PlayerTeam, WeaponController, PlayerAnimatorDriver, PlayerViewSetup, Crosshair
│                           (NO Animator on the root)
├─ CameraRoot               local pos (0, 0.7, 0)
│   ├─ Camera               Camera + AudioListener, near clip 0.05
│   └─ assault2             first-person gun, ViewModelMotion, no collider/rigidbody
│       └─ MuzzlePoint      barrel tip, blue Z arrow along the barrel
└─ Body                     local pos (0, -0.9, 0)
    └─ Ch15_nonPBR          the ONLY Animator (Controller Player_Rifle, Humanoid avatar, Apply Root Motion OFF)
        └─ ...Hips...RightHand
            └─ WorldGun     third-person gun (manually positioned, user says perfect)
                └─ WorldMuzzlePoint
```
Slots: PlayerController (Input Actions, Camera, Audio Listener, CameraRoot, View Model=assault2, Body Root=Body; walk 5, run 8, jump 1.2, gravity -20, sensitivity 0.1, max pitch 85). PlayerViewSetup (Camera, Audio Listener, View Model, Body Root, World Gun). PlayerAnimatorDriver (Animator=Ch15_nonPBR). WeaponController (Input Actions, MuzzlePoint, WorldMuzzlePoint, Camera, Muzzle Flash optional, Tracer prefab = LineRenderer with Sprites-Default, width 0.02, **no TracerFade on it**; Require Locked Cursor ON).

## 6. Scene layout
- Delete the scene's Main Camera. Player prefab is **not** placed in the scene.
- `NetworkManager` object: NetworkManager (Default Player Prefab = Player; Network Prefabs List contains Player; Tick Rate 30, **recommended 60**), Unity Transport, NetworkBootstrap (Max Players 10).
- 6 spawn points (Team A x3, Team B x3) on the ground, blue arrow facing the middle.
- Target cube: NetworkObject + Health + Collider.

## 7. Input Actions asset (`InputSystem_Actions`)
Map `Player`: Move, Look, Jump, Sprint (used by PlayerController), Attack (LMB), **Reload** (added: Button, R; falls back to R key if absent).

## 8. Key design decisions and gotchas
- Movement is owner-authoritative (ClientNetworkTransform). The server never moves players; respawn teleports via owner ClientRpc.
- `PlayerController` disables the CharacterController on non-owners, so **bullets need the separate CapsuleCollider hitbox on the root** (not a child: CharacterController would collide with a child collider).
- Recoil goes on the Camera child so PlayerController's pitch on CameraRoot is untouched. Firing is processed in LateUpdate so the bullet goes where the crosshair is.
- Owner predicts ammo locally and resyncs after 0.35 s idle. Server enforces everything.
- Team balance fills the smaller team (strict `index % 2` breaks when players leave).
- **Only the Player root transform is networked.** Editing a child (e.g. WorldGun) in one editor window never appears in the other. Tune on the copy the viewer sees, then copy the component values into the prefab. Play-mode edits are lost on Stop.
- A prefab can reference only its own children and project assets, not scene objects.
- Multiplayer Play Mode: second editor is slow to load; unfocused editors are throttled (set Interaction Mode = No Throttling). Max ~4 extra windows, so 10-player tests need builds or the AWS server.
- Static lists (`PlayerTeam.All`, `TeamSpawnPoint.All`) assume domain reload stays ON. If "Reload Domain" is disabled, add static reset code.
- Don't call `NetworkManager.ConnectedClientsIds` on clients (server-only).

## 9. AWS plan (not yet tested)
Build a **Linux Dedicated Server**. EC2 Ubuntu (t3.medium is enough for 10), security group **UDP 7777** (not TCP) + SSH 22. Run: `./Game.x86_64 -batchmode -nographics -server -port 7777 -logFile server.log` (inside `screen` to survive logout). Clients enter the instance public IP and port 7777. Later options: GameLift or Unity Multiplay with the same build.

## 10. Proposed next steps
1. Finish open items: animations (Walk/Run/Jump), lag measurement, confirm saved gun transform.
2. Run the test pass: 3, 4, 5 players (2v1, 2v2, 3v2), disconnect and rejoin balance, friendly fire, reload under fire, death mid-reload, 11th player refused, Development build (2 exes).
3. Deploy the dedicated server to AWS and test with 2+ real clients.
4. Then iteration 2: sound, HUD (ammo, health, team scores), VFX, scoring and match flow, spectator camera, IK polish.

## 11. Rebuilding from scratch (checklist)
1. Install packages, set Active Input Handling = New, add the `Reload` action.
2. Put all scripts in `Assets/Scripts` (+ the 2 editor scripts in `Assets/Editor`).
3. Download Mixamo rifle Idle/Walk/Run/Jump (FBX for Unity, without skin), set Humanoid, Loop Time on idle/walk/run, bake into pose, build the controller with TDMAnimatorBuilder.
4. Build the Player hierarchy and components exactly as section 5, make it a prefab, delete it from the scene.
5. Scene: NetworkManager setup + spawn points + target (section 6).
6. Run Tools > TDM > Validate Setup (select the Player prefab). Fix all red lines.
7. Multiplayer Play Mode test (Host + Client).

## 12. AWS Unity integration boundary and manual checks

The current backend contract was checked against `cloud/acc-shooter-api/hello_world/app.py`:

- `GET /config` returns `gameName`, `region`, `protocol`, `maxPlayersPerMatch`, `defaultKillLimit`, and `features` (`quickplay`, `privateRooms`, `leaderboard`, `googleSignIn`). Unity parses these exact fields and falls back to local values with all features disabled if the request or schema fails. Advertised flags do not enable unfinished matchmaking.
- `POST /guest/rooms` accepts optional `maxPlayers` (2–10, default 10) and `killLimit` (1–200, default 30). It returns a room code and explicitly says server assignment is not configured. The development-only menu action creates a code only; it does not start gameplay.
- `POST /guest/rooms/{code}/join` currently has no successful implementation (404/503/501 only). `POST /guest/quickplay` likewise has no successful implementation (503/501 only). Unity must not present either as a playable AWS flow.
- The routes are unauthenticated, and a room-code request can write a record. The AWS create-room menu and API method are development-build/editor-only and also require the serialized `allowUnauthenticatedAwsRoomCreation` opt-in (off by default). The configured default URL is the deployed API, so enable that opt-in only after changing the API URL to a suitable test backend. No player ID, display name, cloud credential, or server secret is sent to that route.
- The guest ID is a persistent random local identifier for prototype convenience only. It is neither authenticated nor trusted by a server.
- The AWS backend does not currently return an endpoint or ticket. `JoinDirectWithApprovalPayload` only passes caller-provided bytes through NGO `NetworkConfig.ConnectionData`; it is not a ticket serializer or validator. The present server approval callback does not validate that payload. Do not use it to authorize clients until the backend contract and trusted server-side validation exist.

Manual verification checklist:

1. With the API reachable, start the menu and inspect `AWSBackendClient.Config`; verify it contains the six documented config fields. Change the API URL in the component for a controlled local stub test and return malformed JSON or time out; verify the local fallback is used and every feature flag stays false.
2. Attach `AWSBackendClient` to the NetworkManager object if you need to change its API URL; otherwise it uses the deployed default. Only against an isolated test backend, enable `allowUnauthenticatedAwsRoomCreation`, then choose **CREATE ROOM** and **DEV: AWS ROOM CODE (CLOUD WRITE)**. Verify the request contains only `maxPlayers` and `killLimit`, the returned code is shown, the “room only” warning is visible, and no NGO session starts. This writes a cloud room record; do not use the deployed default for a routine test.
3. Verify the AWS room-code button is absent from a non-development build. Existing **CREATE RELAY ROOM** / **JOIN ROOM** continue to use Unity Sessions/Relay.
4. In a development build, use the existing **DEV DIRECT CONNECT** screen with a controlled reachable server, an unreachable address, and a server that accepts then disconnects. Verify success enters gameplay; failure or the 15-second timeout returns to the menu; then verify Relay can still start. The manual IP menu remains development-only.
5. For `JoinDirectWithApprovalPayload`, use a debugger or a controlled NGO test server to verify `NetworkConfig.ConnectionData` is assigned before `StartClient` and cleared after connection/failure. This verifies transport plumbing only, not ticket authorization.
6. Launch a Linux server with `-server -port 7777 -maxPlayers 10 -killLimit 30 -roomCode ABC123 -matchId match-123`; verify the listening port, player limit, and `MatchManager.scoreLimit`. Omit each option and verify inspector/code defaults remain in effect. Invalid ranges must log a warning and retain the default.

Not implemented pending backend work: authenticated guest/session identity, successful room joining and Quick Play, endpoint/ticket response schema, ticket expiry and one-time consumption, and trusted server approval/result submission. Movement still uses owner-authoritative `ClientNetworkTransform`, and client-side weapon inputs/shot claims are not yet a competitive server-authoritative design; these remain separate public-release security blockers and are intentionally not changed here.
