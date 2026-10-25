# ACC Shooter — AWS Cloud Handover README

**Audience:** the teammate building the AWS side. **Game owner** maintains the Unity project.
**Last updated:** Oct 2026. **Companion file:** `TDM_PROJECT_HANDOFF.md` (everything about the game itself).
**This README replaces section 12 ("Cloud plan") of the handoff file** where they differ (the old plan was WebGL-first; this one is desktop-first, see section 8).

> **This is an AWS project, not a game project.** The game adapts to AWS services, not the other way round. Anything the game currently hard-codes or keeps in memory/local storage should move to an AWS service where it makes sense, **but nothing in the live match path may add latency.**

---

## 0. Read this first (60-second summary)

**Game:** 5v5 first-person team deathmatch (Unity 6, editor **6000.0.84f1**, Netcode for GameObjects). Fully playable in the editor with rooms, join codes, kills, respawn, scoreboard, kill feed. **It has never been built.** Rooms today use Unity's Relay (the creating player is the host). That is what we are replacing.

**Your job:** build the AWS backend that provides dedicated game servers, rooms/matchmaking, quick play without login, Google login, stored player stats, and a stats dashboard, all on **one AWS account with a hard budget (Free plan, up to $200 credits)**.

**Decisions already made**

| # | Decision | Why |
|---|---|---|
| 1 | **Dedicated game servers on EC2** (Linux, UDP). Nobody's laptop is the host. | Lowest latency, fair for everyone, shows EC2 + networking. |
| 2 | **Primary client = Windows + macOS desktop builds.** WebGL is an optional extra, last. | Browsers cannot use UDP and cannot match desktop smoothness (section 8). |
| 3 | **All room / matchmaking / stats / config data in DynamoDB**, reached through **API Gateway + Lambda**. | Required service, instant scale, pay-per-use. |
| 4 | **Quick Play without login (guest)** and **optional Google sign-in (Cognito)** to save stats. | Requirement 7. |
| 5 | **Dashboard = static website on S3 + CloudFront** calling the API. **No Athena, no QuickSight.** | Athena is not free-tier; stats are pre-aggregated in DynamoDB instead. |
| 6 | **API Gateway / Lambda are only in the lobby path** (find room, join, stats). **Match traffic is pure UDP between client and EC2.** | Keeps in-match latency at the network minimum. |
| 7 | **Join tickets** (one-time, 2-minute DynamoDB items) authorise players on the game server. | Players cannot bypass room codes or capacity by guessing the server IP. |

**Required AWS services (all used):** EC2, IAM, DynamoDB, API Gateway, S3, Lambda, VPC/Security Groups/Elastic IP (networking), CloudFront, Cognito (marked optional by the owner but included for Google login), CloudWatch, Systems Manager, Budgets.

**Deliverables checklist**
- [ ] D1 Account guardrails (MFA, budget alerts, IAM users)
- [ ] D2 DynamoDB tables + Lambdas + HTTP API (guest routes first)
- [ ] D3 EC2 dedicated server running the Linux build, reachable over UDP
- [ ] D4 Slot agent + room registry (code → server endpoint)
- [ ] D5 Match results written to DynamoDB + leaderboard
- [ ] D6 Cognito + Google login (desktop flow) and guest mode working side by side
- [ ] D7 S3 + CloudFront website: downloads page + stats dashboard
- [ ] D8 Scaling + cost guard (auto-stop, alarms, optional Auto Scaling group)
- [ ] D9 (optional) WebGL

---

## 1. Ground rules

1. **Budget cap:** stay inside the Free plan credits. Target **about $20/month or less** with one small server (section 11). Set alarms **before** creating anything.
2. **Never put AWS keys, secrets or tokens in Unity or Git.** Clients only hold Cognito tokens and join tickets. Servers use an **IAM instance role**.
3. **Clients never write stats.** Only the game server (through the agent) records matches.
4. **No NAT Gateway, no load balancer, no Global Accelerator, no WAF, no Secrets Manager** unless the owner approves. Each costs a recurring fee (section 11.4).
5. **Infrastructure as code** (AWS SAM or CDK) in `cloud/`, so everything can be deleted and rebuilt. Tag every resource `project=acc-shooter`.
6. **One region** for everything (pick the one nearest the players, section 8.4).
7. Keep an **API contract** (section 7) and change it only by agreement with the owner, because the Unity code depends on it.

---

## 2. Quick glossary
- **Slot:** one game-server process on one UDP port (hosts exactly one room at a time).
- **Slot agent:** a small script on the EC2 box that registers slots, starts/stops the Unity server processes, and talks to DynamoDB with the instance's IAM role. Keeps AWS SDK code out of Unity.
- **Join ticket:** random one-time string stored in DynamoDB (with TTL) that proves a player was allowed into a specific room.
- **Guest:** a player without login. Can play, stats are not saved.

---

## 3. Current game networking → AWS mapping

What the game does today, and which AWS service replaces or supports it. Each row is a small, self-contained task.

| # | Game feature today | How it works now | AWS service(s) | Basic task |
|---|---|---|---|---|
| 1 | **Create room** (players 2–10, kills to win 5–100) | Unity Sessions creates a relay allocation; creator becomes host | **API Gateway** `POST /rooms` → **Lambda** → **DynamoDB** `Rooms` + claim a free slot in `Servers` | 2 tables, 1 Lambda, 1 route |
| 2 | **Room code** (6 chars) | Generated by Unity's service | Lambda generates the code; conditional `PutItem` (`attribute_not_exists`) avoids collisions; **TTL** expires old rooms | add code generator + TTL |
| 3 | **Join by code** | `JoinSessionByCodeAsync` | `POST /rooms/{code}/join` → Lambda reads `Rooms`, writes a `JoinTickets` item, returns `{ip, port, ticket}` | 1 Lambda, 1 route |
| 4 | **Quick Play** (new) | – | `POST /guest/quickplay` → Lambda queries `Rooms` GSI for an open public room, else creates one | 1 Lambda, 1 GSI |
| 5 | **Where the server runs** | On a player's PC | **EC2** (Linux Dedicated Server build), **Elastic IP**, **Security Group**, **systemd**, build stored in **S3** | launch 1 instance |
| 6 | **Max players / "room full"** | `NetworkBootstrap` connection approval | Server approval + ticket check; `Rooms.playerCount` updated by the agent; DynamoDB **conditional write** stops over-filling | approval + agent heartbeat |
| 7 | **Dedicated server mode** | `-server -port N`, listens on `0.0.0.0` | Same build; add flags `-maxPlayers -killLimit -roomCode -slot` | small Unity change |
| 8 | **Player identity** | Unity anonymous auth | **Guest** (no login) or **Cognito** + Google | user pool + Google IdP |
| 9 | **Username** | `PlayerPrefs` (local, lost on clear) | **DynamoDB** `Players`, routes `GET/PUT /me`; guests keep a local-only name | 1 table, 2 routes |
| 10 | **Kills, deaths, winner, team score** | `MatchManager` in server memory, shown in the HUD | Server → agent → **Lambda** `recordMatch` → **DynamoDB** `Matches` + atomic counters on `Players` | 1 Lambda, 1 table |
| 11 | In-match scoreboard / kill feed / top killers | NetworkVariables + HUD | **Unchanged.** No AWS in the live match path | none |
| 12 | **Global leaderboard** (new) | – | `Players` **GSI** + `GET /leaderboard` | 1 GSI, 1 route |
| 13 | **Player stats dashboard** (new) | – | **S3 + CloudFront** static site, **Cognito** Hosted UI login, API calls | static HTML/JS |
| 14 | **Game downloads** (new) | – | **S3 + CloudFront** downloads page | upload zips |
| 15 | **Hard-coded settings** (kill-limit choices, max players, kill phrases, version, maintenance flag) | Inspector fields / C# arrays | **DynamoDB** `Config` item + `GET /config` (cached on the client) | 1 item, 1 route |
| 16 | **Ping / FPS** | on-screen label | **CloudWatch** custom metrics from the agent (players, tick time) | agent publishes metrics |
| 17 | **Server logs** | none | **CloudWatch Logs** (7-day retention) or local rotated files | optional |
| 18 | **Server build distribution** | – | **S3** private artifacts bucket; EC2 pulls the build on boot | upload + `aws s3 sync` |
| 19 | **SSH access** | – | **Systems Manager Session Manager** (no open port 22) | attach managed policy |
| 20 | **Cost safety** | – | **Budgets**, **EventBridge Scheduler** + Lambda to stop idle servers | alarms + 1 Lambda |

---

## 4. Target architecture

```
                         +-------------------------------+
 Player laptop           |  S3 + CloudFront              |   static site: downloads, dashboard
 (Windows / Mac)  -----> |  (HTTPS, free tier)           |
      |                  +-------------------------------+
      |  HTTPS (lobby only)
      v
 +----------------+    +--------+    +-----------------------------------------+
 | API Gateway    | -> | Lambda | -> | DynamoDB: Players, Matches, Rooms,      |
 | (HTTP API)     |    |        |    | Servers, JoinTickets, Config            |
 | JWT authorizer |    +--------+    +-----------------------------------------+
 +----------------+         ^                         ^
      ^                     |                         | IAM role (no keys)
      | tokens              |               +---------+-----------------+
 +----------+               |               | EC2 (public subnet)       |
 | Cognito  |               |               |  slot agent (Python)      |
 | + Google |               |               |  Unity Linux servers      |
 +----------+               |               |  slots: UDP 7777..7786    |
                            |               +---------------------------+
                            |                        ^
 Player ------------- UDP (match traffic, direct, no proxy) -----+

 CloudWatch (logs, metrics, alarms) | Budgets | SSM Session Manager | S3 artifacts (server builds)
```

### 4.1 Request flows

**Quick Play (guest)**
1. Client `POST /guest/quickplay`.
2. Lambda queries `Rooms` (GSI `status-index`, status `OPEN`, public). If none has space, it claims a `FREE` slot (conditional update `FREE → RESERVED`) and creates a public room (10 players, default kill limit from `Config`).
3. Lambda writes a `JoinTickets` item (TTL 120 s) and returns `{endpoint, ticket}`.
4. Client connects by UDP to `ip:port` and sends the ticket in the connection payload.
5. Server (via the agent's local HTTP endpoint) consumes the ticket (conditional delete) and approves or rejects.

**Create room / join by code:** same as above, with the room config chosen by the player; join checks `playerCount < maxPlayers`.

**Match end:** Unity server → `http://127.0.0.1:8081/result` (agent) → agent invokes Lambda `recordMatch` (IAM, no API Gateway) → `TransactWriteItems`: put `Matches` rows, `ADD` to `Players` counters (logged-in players only) → agent sets the slot back to `FREE` after the process restarts.

**Login (desktop):** client opens the system browser on the Cognito Hosted UI (Google) with PKCE, a loopback listener on `http://localhost:<port>/callback` catches the code, client exchanges it for tokens, then calls JWT-protected routes.

**Dashboard:** the website does the same login in the browser (PKCE) and calls `GET /me`, `GET /me/matches`, `GET /leaderboard`.

---

## 5. Service-by-service design

### 5.1 EC2 (game servers)
- **Instance:** start with **t3.small** (2 vCPU, 2 GB). For demo days use **c7i-flex.large** (Free-plan eligible, no burst limits). Linux, **x86_64** (assume the Unity Linux server build is x86_64; check ARM support in the editor before using `t4g`).
- **Network:** default VPC, **public subnet**, auto-assign public IPv4, **Elastic IP** on the first instance. Security group: **UDP 7777–7786** from `0.0.0.0/0`. **No SSH rule**; use **Session Manager**.
- **Storage:** small gp3 EBS (8–20 GB) for OS + server build + logs only. **Nothing important lives on the instance**; persistent data goes to DynamoDB/S3.
- **Processes:** a **slot agent** (systemd service) manages N Unity server processes (`-batchmode -nographics -server -port 77xx -slot K ...`). Start with **2 slots**, measure memory per process, then raise.
- **T3 note:** t3 is burstable. For steady load watch `CPUCreditBalance`. Set credit mode to **standard** to avoid surprise charges, and move to c7i-flex.large if CPU credits run out.
- **Bootstrap:** user-data installs Python/boto3, pulls the server build and agent from the **S3 artifacts bucket**, enables the systemd unit.

### 5.2 IAM
| Principal | Purpose | Permissions (least privilege) |
|---|---|---|
| Root | Emergency only | MFA, no access keys |
| Developers (IAM Identity Center or IAM users in a group) | Build the stack | Admin on this sandbox account is acceptable for a student project; tighten later |
| `acc-game-server-role` (EC2 instance profile) | Agent talks to AWS | DynamoDB Get/Update/Put/Delete/Query on the project tables only, `lambda:InvokeFunction` on `recordMatch`, `cloudwatch:PutMetricData`, `ssm:GetParameter` on `/acc/*`, S3 read on the artifacts bucket, managed policy `AmazonSSMManagedInstanceCore` |
| One role per Lambda | Business logic | Only the tables/actions that function needs |
| Cognito | Identity | No IAM roles needed (JWT authorizer on the API) |

See Appendix A for a sample policy. **No long-lived access keys anywhere.**

### 5.3 DynamoDB
Single region, **on-demand** tables (zero capacity planning, instant scale). Details in section 6. Use **TTL** on `Rooms`, `Servers`, `JoinTickets`. Use **conditional writes** for slot claiming, room codes and one-time tickets. Use **atomic counters** (`ADD`) for stats, so no analytics engine is needed.

### 5.4 API Gateway + Lambda
- **HTTP API** (cheaper than REST, has a built-in Cognito JWT authorizer).
- Routes in section 7. Stage throttling (for example 20 req/s, burst 50) protects the public guest routes.
- Lambda runtime **Node.js or Python**, 256–512 MB (more memory = faster CPU = lower cold-start time). Lobby only, so a cold start of a few hundred ms is acceptable.
- CORS allowed only for the CloudFront site origin.

### 5.5 S3 + CloudFront
- Bucket `acc-site-<id>`: static website (landing page, **downloads**, **dashboard**). Private bucket + **CloudFront Origin Access Control**. HTTPS from the free `*.cloudfront.net` domain (no custom domain needed).
- Bucket `acc-artifacts-<id>`: private, holds Linux server builds and the agent. Versioning on.
- Game client zips are served from CloudFront (keeps download traffic off EC2).

### 5.6 Cognito
- **User pool**, tier **Essentials or Lite** (free for the first 10,000 monthly active users; Google counts as a social provider). **Do not choose Plus** (no free tier).
- **Google** as identity provider (OAuth client created in Google Cloud Console; authorised redirect URI is `https://<cognito-domain>/oauth2/idpresponse`).
- **App client:** public (no secret), authorisation-code grant + PKCE, scopes `openid email profile`, callbacks: the CloudFront dashboard URL and `http://localhost:<port>/callback` (desktop login).
- Guests never touch Cognito.

### 5.7 Networking services
| Service | Use | Cost note |
|---|---|---|
| VPC (default), subnet, internet gateway | EC2 connectivity | Free |
| Security groups | UDP game ports only | Free |
| Elastic IP / public IPv4 | Stable server address | **Charged per hour per public IPv4** |
| **DynamoDB gateway endpoint** | Private path EC2 → DynamoDB | Free |
| CloudFront | Website + downloads | Free tier (check) |
| Route 53 + ACM | Custom domain (optional) | Hosted zone ~$0.50/month + domain fee; skip for the MVP |
| *Avoid:* NAT Gateway, NLB, Global Accelerator | – | Recurring monthly fees |

### 5.8 Systems Manager, CloudWatch, Budgets, EventBridge
- **Session Manager** replaces SSH. **Parameter Store (standard)** holds non-secret config (`/acc/region`, `/acc/api-url`).
- **CloudWatch:** custom metrics `PlayersOnline`, `FreeSlots`, `TickTimeMs`; alarms for budget, CPU credits and no-heartbeat; log retention 7 days.
- **Budgets:** actual and forecast alerts at $5 / $10 / $20.
- **EventBridge Scheduler + Lambda:** stop the instance when all slots have been free for 30 minutes; start it when quick play finds no capacity.

---

## 6. Data model (DynamoDB)

All tables on-demand. `ttl` is a Number attribute (epoch seconds) with TTL enabled where noted.

| Table | Keys | Main attributes | GSIs | TTL |
|---|---|---|---|---|
| **Players** | PK `userId` (Cognito `sub`) | `username`, `createdAt`, `lastLogin`, `totalKills`, `totalDeaths`, `totalWins`, `totalMatches`, `gsi1pk`=`"LB"`, `gsi1sk`=`totalKills` (N) | `leaderboard-index`: PK `gsi1pk`, SK `gsi1sk` | no |
| **Matches** | PK `userId`, SK `endedAt#matchId` | `matchId`, `roomCode`, `team`, `kills`, `deaths`, `won`, `durationSec`, `killLimit` | – | optional (e.g. 180 days) |
| **Rooms** | PK `roomCode` | `status` (OPEN/FULL/PLAYING/ENDED), `isPublic`, `maxPlayers`, `killLimit`, `playerCount`, `slotId`, `endpoint{ip,port}`, `createdBy`, `createdAt` | `status-index`: PK `status`, SK `createdAt` | yes (e.g. 2 h) |
| **Servers** | PK `slotId` (`<instanceId>:<port>`) | `instanceId`, `publicIp`, `port`, `status` (FREE/RESERVED/IN_USE/DRAINING), `roomCode`, `lastHeartbeat` | `status-index`: PK `status` | yes (heartbeat + 60 s) |
| **JoinTickets** | PK `ticket` | `roomCode`, `userId` (`"guest"` or sub), `displayName` | – | yes (now + 120 s) |
| **Config** | PK `key` (`"game"`) | `killLimitOptions`, `maxPlayersLimit`, `killPhrases[]`, `suicidePhrases[]`, `minClientVersion`, `maintenance` | – | no |

**Access patterns (no table scans anywhere)**
| Need | Operation |
|---|---|
| Profile / stats | `GetItem` Players |
| Last 20 matches | `Query` Matches, `ScanIndexForward=false`, `Limit=20` |
| Top killers | `Query` `leaderboard-index`, PK `"LB"`, descending, `Limit=20` |
| Find open quick-play room | `Query` Rooms `status-index`, status `OPEN` |
| Claim a free server slot | `Query` Servers `status-index` (FREE), then `UpdateItem` with `ConditionExpression status = FREE` (retry on conflict) |
| Unique room code | `PutItem` with `attribute_not_exists(roomCode)` (retry with a new code) |
| One-time ticket | `DeleteItem` with `attribute_exists(ticket)` and `ReturnValues=ALL_OLD` |
| Record a match | `TransactWriteItems` (Matches puts + Players `ADD`) |

Room codes: 6 characters from an unambiguous alphabet (no `0 O 1 I`).

---

## 7. API contract (v0)

Base URL = the HTTP API invoke URL. JSON in/out. "JWT" = Cognito ID token in `Authorization: Bearer ...`.
Every JWT route has a no-login twin under `/guest/` that points to the **same Lambda**; the Lambda reads `requestContext.authorizer.jwt.claims.sub` if present.

| Method + path | Auth | Purpose |
|---|---|---|
| `GET /config` | none | Remote config (limits, phrases, min version, maintenance flag) |
| `POST /guest/quickplay` | none (throttled) | Join or create a public room → endpoint + ticket |
| `POST /guest/rooms` | none (throttled) | Body `{maxPlayers, killLimit, isPublic, displayName}` → `{roomCode, endpoint, ticket}` |
| `POST /guest/rooms/{code}/join` | none (throttled) | Body `{displayName}` → `{endpoint, ticket, room}` or `404/409` |
| `POST /quickplay`, `POST /rooms`, `POST /rooms/{code}/join` | JWT | Same, but the ticket carries `userId` so stats are saved |
| `GET /me` | JWT | Profile + totals (creates the profile on first call) |
| `PUT /me` | JWT | `{username}` (max 16 chars, no `<` `>`) |
| `GET /me/matches?limit=20` | JWT | Match history |
| `GET /leaderboard?top=20` | none | Top killers |

**Example join response**
```json
{
  "room": { "roomCode": "K7M2QD", "maxPlayers": 10, "killLimit": 30, "playerCount": 3 },
  "endpoint": { "ip": "203.0.113.10", "port": 7778, "protocol": "udp" },
  "ticket": "b1c7...e9"
}
```

**Server-only operations are NOT exposed on API Gateway.** The agent uses IAM directly: `Servers` heartbeat, ticket consume, `Rooms` updates, `lambda:Invoke recordMatch`.

---

## 8. Build and delivery: which mode to build

### 8.1 The honest answer to "same smoothness for everyone"
Gameplay smoothness here depends on **network latency and client frame rate**, not on graphics quality. A **native desktop build talking UDP to a nearby EC2 server** is the best achievable. A browser (WebGL) cannot match it: no UDP (WebSockets over TCP/TLS add delay on packet loss), single-threaded, memory-limited, throttled in background tabs, and it needs a domain + TLS certificate for the game server.

| Option | Latency / smoothness | Reach | AWS showcase | Recommendation |
|---|---|---|---|---|
| **Windows + macOS standalone** (UDP) | Best | Any Windows/Mac laptop after a download | Downloads from S3 + CloudFront | **Primary** |
| **WebGL** (WebSockets, TLS) | Noticeably worse under packet loss; heavier on low-end laptops | Zero install | Static hosting | **Optional last step**, labelled "instant play (lower quality network)" |
| Linux Dedicated Server | n/a (server) | EC2 | EC2 | **Required** |

A single server process uses either UDP or WebSockets, not both. Supporting WebGL means a **separate server fleet/port range** plus TLS. Do this only after everything else works.

### 8.2 Build targets (File > Build Profiles)
| Target | Settings | Notes |
|---|---|---|
| **Linux Dedicated Server** | Platform Linux, subtarget **Server**, Mono (simplest), scene list = game scene | Runs headless: `-batchmode -nographics -server -port N` |
| **Windows 64-bit** | Windows, Mono (IL2CPP needs a Windows machine) | Built from the Mac with *Windows Build Support (Mono)* |
| **macOS** | Apple silicon or Universal, IL2CPP | Unsigned apps show a Gatekeeper warning (right-click > Open). Apple signing/notarisation costs a developer account |
| First tests | tick **Development Build** | Shows the ping/FPS label; turn off for release |

Zip each client build and upload it to the website bucket.

### 8.3 Low-latency checklist
1. **Region next to the players.** Compare with `ping ec2.<region>.amazonaws.com` or cloudping.info. Physics sets the floor: same-country players usually see roughly 10–60 ms, intercontinental 150+ ms.
2. **Direct UDP to the instance** (public IP). No load balancer or proxy in the path.
3. **Keep AWS calls out of the match.** API/DynamoDB only for lobby, join, results.
4. **Tick rate 60** (already recommended); server `targetFrameRate=60`; client interpolation on.
5. **Avoid CPU throttling** on t3 (credits) or use c7i-flex.large.
6. **Headless server build** (`-nographics`), no vsync.
7. **Lambda 256–512 MB** to shorten cold starts; lobby calls can tolerate it.
8. Use the **existing in-game ping label** (Development build) to compare regions and instance types.

### 8.4 Region choice
Ask the owner where the players are and choose the closest region (for example `ap-south-1` Mumbai for India, a `eu-*` region for Europe, `us-east-1` for the US east coast). Prices differ by region. Record the choice here: **Region: ________**.

---

## 9. What changes in the Unity game (game adapts to AWS)

Owner implements these (or asks the assistant to generate the scripts). Contract = section 7.

| # | Change | Where |
|---|---|---|
| U1 | `CloudConfig` ScriptableObject: API URL, region, Cognito domain, client ID, redirect port (non-secret) | new |
| U2 | `CloudApi`: `UnityWebRequest` JSON client for section 7 routes | new |
| U3 | `NetworkBootstrap`: new "AWS mode". **Create / Join / Quick Play** call the API, then connect as a **client** with `SetConnectionData(ip, port)` and the **ticket in `ConnectionData`**. Keep the current Unity Sessions flow behind a flag for offline demos | edit |
| U4 | `NetworkBootstrap` server mode: read `-maxPlayers -killLimit -roomCode -slot`; connection approval reads the ticket and asks the agent (`http://127.0.0.1:8081/validate`, use `Pending=true` while waiting); reject if invalid or room full | edit |
| U5 | `MatchManager`: take `scoreLimit` from server args; at match end `POST` results to the agent (`/result`: players with userId, team, kills, deaths, winner) | edit |
| U6 | `PlayerProfile`: username from `GET /me` when logged in; guests use the local name | edit |
| U7 | `MainMenuUI`: add **QUICK PLAY**, **SIGN IN WITH GOOGLE**, **DASHBOARD** (opens the website) buttons; "Create Room" now means "ask AWS for a server" | edit |
| U8 | `AuthService`: desktop PKCE login (system browser + local `HttpListener` on `localhost`), token storage, refresh | new |
| U9 | Remote config: load phrases/limits from `GET /config` with a built-in fallback when offline | edit |
| U10 | Server: after a match, reset state (or let the agent restart the process) so the slot returns to `FREE` | edit |

Rule: **the creator of a room is just a player**; the server is on EC2.

---

## 10. Step-by-step plan

Tags: **[AWS]** friend, **[UNITY]** owner. "Done when" = acceptance test.

### Phase 0 — Account and guardrails (day 1)
1. **[AWS]** Create the AWS account, choose the **Free plan**. Do **not** join an AWS Organization or Control Tower (Free-plan credits would expire immediately on upgrade).
2. **[AWS]** Root user: MFA on, no access keys. Create an admin IAM user or Identity Center user for yourself, and one for the owner.
3. **[AWS]** Budgets: create alerts at $5, $10, $20 (actual + forecast). Turn on Free Tier usage alerts.
4. **[AWS]** Choose the region (section 8.4). Create `cloud/` in the repo; choose SAM or CDK.
5. **Done when:** a test budget alert email arrives and you can sign in with MFA.

### Phase 1 — DynamoDB + API Gateway + Lambda (guest data plane)
1. **[AWS]** Create tables from section 6 (on-demand, TTL on `Rooms`, `Servers`, `JoinTickets`). Seed `Config`.
2. **[AWS]** Lambdas: `getConfig`, `createRoom`, `joinRoom`, `quickplay`, `getLeaderboard` (Players first with a few test items).
3. **[AWS]** HTTP API with `/guest/*` routes, `GET /config`, `GET /leaderboard`; throttle the stage; CORS.
4. **[AWS]** Test with `curl`. To test slot logic before servers exist, insert fake `Servers` items.
5. **Done when:** `POST /guest/quickplay` returns an endpoint and ticket, and the ticket disappears after 2 minutes (TTL).

### Phase 2 — EC2 game server
1. **[UNITY]** Produce the **Linux Dedicated Server** build (section 8.2) and a Windows/Mac development client.
2. **[AWS]** S3 artifacts bucket; upload the server build.
3. **[AWS]** Launch the EC2 instance with the instance role (Appendix A), security group UDP 7777–7786, Elastic IP, SSM. Pull the build from S3, `chmod +x`, run once by hand: `./Game.x86_64 -batchmode -nographics -server -port 7777 -logFile server.log`.
4. **[UNITY]** Test with a client using the existing direct-IP method (`JoinDirect(ip, port)`).
5. **Done when:** two clients on different networks play on the EC2 server and the ping label looks acceptable.

### Phase 3 — Slot agent + room registry
1. **[AWS]** Write the agent (Python + boto3): register slots in `Servers`, heartbeat every 10–15 s, poll for `RESERVED`, start the Unity process with room args, mark `IN_USE`, watch for exit, mark `FREE`. Add the local HTTP endpoints (`/validate`, `/result`, `/status`). Run it under systemd (Appendix B).
2. **[UNITY]** Implement U3, U4, U5, U10.
3. **[AWS]** Wire Lambda `createRoom` / `joinRoom` / `quickplay` to the real slots.
4. **Done when:** pressing Quick Play in the game lands you in a match on EC2; a second client joins with the code; a third with a wrong code gets a clear error; an 11th player is refused.

### Phase 4 — Match results + leaderboard
1. **[AWS]** Lambda `recordMatch` (invoked by the agent): `TransactWriteItems`.
2. **[UNITY]** Implement U5 result payload.
3. **[AWS]** Fill `Players` totals and the `leaderboard-index`.
4. **Done when:** after a match, `GET /leaderboard` shows the winners' updated totals (logged-in players only).

### Phase 5 — Cognito + Google + guest side by side
1. **[AWS]** Google Cloud: OAuth consent screen + OAuth client. Cognito user pool (Essentials), Google IdP, app client (public, PKCE), Cognito domain prefix.
2. **[AWS]** Add the JWT-protected route set (`/me`, `/rooms`, `/quickplay`, `/rooms/{code}/join`, `/me/matches`) pointing at the same Lambdas.
3. **[UNITY]** Implement U6, U7, U8.
4. **Done when:** a Google user plays, closes the game, logs in again on another PC and sees the same stats; a guest plays with no login and no stats saved.

### Phase 6 — Website: downloads + dashboard
1. **[AWS]** S3 site bucket + CloudFront (OAC), upload client zips.
2. **[AWS]** Downloads page: Windows and macOS zips plus short instructions for the security warnings (Mac: System Settings > Privacy & Security > Open Anyway; Windows: More info > Run anyway).
3. **[AWS]** Static dashboard (plain HTML/JS, charts with a bundled JS library): login (PKCE), totals, K/D, last 20 matches, kills-per-match chart, leaderboard, username edit.
4. **Done when:** logging in on the site shows the same stats as in the game. **No Athena or QuickSight is used.**

### Phase 7 — Scaling and cost guard
1. **[AWS]** CloudWatch metrics + alarms (no heartbeat, CPU credits, free slots).
2. **[AWS]** EventBridge Scheduler + Lambda: stop the instance when idle 30 minutes; `quickplay` starts it and returns `503 {"retryAfter":60}` while warming up.
3. **[AWS]** Optional: launch template + **Auto Scaling group** (min 0–1, max 2) that registers new slots at boot; scale on `FreeSlots`. Keep max low to protect the budget.
4. **Done when:** an idle server stops by itself and the first quick play after that succeeds within about a minute.

### Phase 8 — Optional WebGL
Only after Phases 0–7. Needs a WebSocket server fleet with TLS (domain + certificate), Unity WebGL compatibility fixes (Esc is captured by the browser, clipboard, cursor lock), and a clear "lower network quality" label. Discuss with the owner first.

---

## 11. Pricing and Free-plan availability

### 11.1 What the Free plan gives (verified from AWS pages, Oct 2026)
- **$100 credit at sign-up + up to $100 more** for completing activities = **up to $200**.
- Free plan lasts **up to 6 months or until the credits run out**, whichever is first. Before that you can **upgrade to the Paid plan** in the console; remaining credits still apply until they expire. If the account is not upgraded, it is closed.
- **Joining an AWS Organization / Control Tower makes the credits expire immediately.** Don't.
- Free-plan **EC2 instance types** (AWS docs): `t3.micro, t3.small, t4g.micro, t4g.small, c7i-flex.large, m7i-flex.large`. Larger and nano types are not available on the Free plan.
- All services in this project can be paid from credits. Some are also **always free** within limits.

### 11.2 Service pricing and availability (verify each in the AWS Pricing Calculator for your region; prices are approximate)

| Service | Use here | Price (approx.) | Free plan / free tier | Source quality |
|---|---|---|---|---|
| **EC2 t3.small** | Game server | ~$0.021/h (us-east-1) ≈ **$15/month** always on; ~$0.027/h in Tokyo | Eligible, paid from credits | AWS docs for eligibility; price from third party and memory, verify |
| EC2 t3.micro | Tiny tests | ≈ $0.0104–0.0136/h ≈ **$8–10/month** | Eligible | same |
| EC2 c7i-flex.large | Demo days | ≈ $0.107/h (Tokyo) ≈ $78/month always on | Eligible | third party, verify |
| **Public IPv4 address** | Server address | ~$0.005/h ≈ **$3.65/month each** | Paid from credits | AWS pricing, verify |
| EBS gp3 | Root disk 8–20 GB | ~$0.08/GB-month ≈ **$1–2** | Paid from credits | verify |
| Data transfer out | Game traffic | first ~100 GB/month free account-wide, then ~$0.09/GB | Always-free allowance (verify) | verify |
| **DynamoDB** | All data | On-demand ≈ **$0.625–1.25 per million writes**, **$0.125–0.25 per million reads** (AWS cut on-demand prices in Nov 2024; sources disagree), storage $0.25/GB-month after **25 GB free** | 25 GB always free. The always-free 25 WCU/RCU applies to **provisioned** mode; on-demand costs cents at this scale | third party, verify |
| **API Gateway HTTP API** | Lobby API | **$1.00 per million requests** (REST is $3.50) | Paid from credits (older accounts had 1M free calls) | third party, verify |
| **Lambda** | Backend logic | $0.20 per million requests + GB-seconds | **1M requests + 400,000 GB-seconds per month always free** | third party, verify |
| **Cognito** | Login | Essentials **$0.015 per MAU** above the free tier; Plus has no free tier | **10,000 MAU free** (Lite/Essentials, includes Google); OIDC/SAML federation only 50 MAU free | **AWS pricing page** |
| **S3** | Site, downloads, builds | ~$0.023/GB-month; $0.005 per 1,000 PUT; $0.0004 per 1,000 GET | Paid from credits | verify |
| **CloudFront** | Website, downloads | ~$0.085/GB after free allowance | Always-free ~1 TB data out + 10M requests per month (verify) | verify |
| **CloudWatch** | Logs, metrics, alarms | Logs ~$0.50/GB ingested; custom metrics ~$0.30 each after the first 10 | 5 GB logs, 10 alarms, 10 metrics free (verify) | verify |
| **Budgets** | Alerts | First 2 budgets free, then small daily fee | Free for 2 | verify |
| **Systems Manager** (Session Manager, Parameter Store standard) | Access, config | $0 | Free | verify |
| **IAM, VPC, Security Groups, DynamoDB gateway endpoint, ACM** | Security + network | $0 | Free | known |
| **EventBridge Scheduler** | Idle shutdown | pennies | Free allowance (verify) | verify |
| Route 53 (optional) | Domain | $0.50/month per hosted zone + domain registration | Not free | skip for MVP |

### 11.3 Example monthly cost (us-east-1 prices, estimates only)
| Scenario | EC2 | IPv4 + EBS | Everything else | **Total** | Credits last |
|---|---|---|---|---|---|
| A. t3.small always on | ~$15 | ~$5 | ~$0–2 | **~$20** | ~9 months (but the Free plan itself ends at 6) |
| B. t3.small 8 h/day (scheduler) | ~$5 | ~$5 | ~$0–2 | **~$10–12** | ~16+ months |
| C. c7i-flex.large only on demo days (~20 h/month) | ~$2 | ~$5 | ~$0–2 | **~$8–9** | long |
| D. Idle (instance stopped) | $0 | ~$2–5 | ~$0 | **~$2–5** | very long |

Traffic estimate: about 30 KB/s per player downstream at 60 Hz, roughly 1 GB per full-room hour. Measure it with CloudWatch before trusting this.

### 11.4 Services to avoid (recurring fees)
**NAT Gateway** (~$32+/month), **Network Load Balancer** (~$16+/month), **Global Accelerator** (~$18/month + data), **WAF** (~$5+/month), **Secrets Manager** ($0.40/secret/month, use Parameter Store), **Athena** (per TB scanned, not in the free tier, so not used), **Cognito Plus**, **RDS/Aurora** (not needed).

### 11.5 Cost guardrails
- Budget alerts first. Check **Billing > Free Tier** weekly.
- Stop the instance when idle (Phase 7). Delete unused Elastic IPs and EBS volumes.
- Tag everything, delete the stack when the showcase ends.
- Decide before month 6: **upgrade to Paid** (keeps remaining credits) or let the Free-plan account close.

---

## 12. Security checklist
- [ ] Root MFA; no root access keys; no Organization.
- [ ] No keys or secrets in Unity builds, Git, or the website.
- [ ] EC2 uses an instance role; Session Manager instead of SSH; only UDP game ports open.
- [ ] S3 buckets private; CloudFront uses Origin Access Control; artifacts bucket never public.
- [ ] API: stage throttling, request validation (lengths, ranges), CORS limited to the site origin.
- [ ] Username sanitising: max 16 chars, strip `<` and `>` (same rule as `PlayerProfile.Clean`). Reject empty/offensive names if desired.
- [ ] Join tickets are random 128-bit, single use, 120 s TTL.
- [ ] Server validates room capacity itself (never trusts the client).
- [ ] Cognito app client public with PKCE; JWTs verified by the API Gateway authorizer.
- [ ] DynamoDB least-privilege per Lambda; conditional writes for claims.
- [ ] Alarms on spend and on server heartbeat.

---

## 13. Testing and acceptance

| # | Test | Expected |
|---|---|---|
| 1 | Quick Play as guest | Lands in a match on EC2, no login |
| 2 | Create room (4 players, 10 kills) | Code shown, room config applied on the server |
| 3 | Join by code from another network | Joins; wrong code gives a clear error |
| 4 | 5th player in a 4-player room | Refused ("room full") |
| 5 | Reuse an old ticket / connect without one | Refused |
| 6 | Win a match (10 kills) | Winner shown; stats recorded for logged-in players only |
| 7 | Log in with Google on a second PC | Same stats, same username |
| 8 | Dashboard | Totals, last 20 matches, leaderboard match the game |
| 9 | Kill the server process | Slot returns to FREE; clients get a clean error |
| 10 | Leave the system idle | Instance stops itself; next Quick Play restarts it within ~1 minute |
| 11 | Latency | Record ping per region/instance type; target a steady value with no spikes at 60 Hz |
| 12 | Budget | After a week, spend matches the table in 11.3 |

Showcase script (5 minutes): open the site → download/launch the game → Quick Play as guest → create a room and join with a second laptop → play to the kill limit → sign in with Google → show the dashboard → show CloudWatch (players, tick time) and the DynamoDB tables → show the Budgets page.

---

## 14. Handover checklist
1. **Repo** (private GitHub, Git LFS for large assets, `.gitignore` for `Library/ Temp/ Obj/ Logs/ UserSettings/ Builds/`). Layout: `Assets/ Packages/ ProjectSettings/ cloud/ docs/`. Commit all `.meta` files.
2. **Unity access:** invite the friend to the Unity organization / project **ACC_Shooter**; same editor **6000.0.84f1**.
3. **AWS access:** dedicated AWS account, owner holds billing and root (MFA); friend gets an IAM Identity Center or IAM user.
4. **Google Cloud:** one account owns the OAuth client (decide who).
5. **Branches:** `main` (stable), `cloud` (friend), `game` (owner); merge by pull request.
6. **Ownership split:** friend owns `cloud/` and the API contract; owner owns `Assets/` and implements U1–U10 against the contract.
7. **Config:** non-secret values only in `CloudConfig` (API URL, region, Cognito IDs). Secrets/params live in Parameter Store.
8. **Weekly sync:** review Billing, Free Tier usage, and the acceptance table.

---

## 15. Open decisions (record answers here)
| Question | Answer |
|---|---|
| Region | |
| Slots per instance (measure memory per Unity server) | |
| Instance type for demos | |
| IaC tool (SAM or CDK) | |
| Do we ship macOS signed/notarised? | **No.** Unsigned. The downloads page explains "Open Anyway" (Mac) and "More info > Run anyway" (Windows). |
| Is WebGL in scope? | **No. Decided: native desktop (Windows + macOS) only.** Phase 8 is dropped unless the owner reopens it. |
| Owner of Google OAuth client | |
| Domain name (optional) | |

---

## Appendix A — Sample IAM policy for `acc-game-server-role` (replace the placeholders)
```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["dynamodb:GetItem","dynamodb:PutItem","dynamodb:UpdateItem","dynamodb:DeleteItem","dynamodb:Query"],
      "Resource": [
        "arn:aws:dynamodb:REGION:ACCOUNT_ID:table/Servers",
        "arn:aws:dynamodb:REGION:ACCOUNT_ID:table/Servers/index/*",
        "arn:aws:dynamodb:REGION:ACCOUNT_ID:table/Rooms",
        "arn:aws:dynamodb:REGION:ACCOUNT_ID:table/Rooms/index/*",
        "arn:aws:dynamodb:REGION:ACCOUNT_ID:table/JoinTickets"
      ]
    },
    {
      "Effect": "Allow",
      "Action": "lambda:InvokeFunction",
      "Resource": "arn:aws:lambda:REGION:ACCOUNT_ID:function:recordMatch"
    },
    {
      "Effect": "Allow",
      "Action": "cloudwatch:PutMetricData",
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": ["ssm:GetParameter","ssm:GetParameters"],
      "Resource": "arn:aws:ssm:REGION:ACCOUNT_ID:parameter/acc/*"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:GetObject","s3:ListBucket"],
      "Resource": [
        "arn:aws:s3:::ACC_ARTIFACTS_BUCKET",
        "arn:aws:s3:::ACC_ARTIFACTS_BUCKET/*"
      ]
    }
  ]
}
```
Also attach the AWS managed policy `AmazonSSMManagedInstanceCore` so Session Manager works. `recordMatch` has its own role with write access to `Matches` and `Players`.

## Appendix B — Example systemd unit for the slot agent
```ini
[Unit]
Description=ACC slot agent
After=network-online.target

[Service]
User=ubuntu
WorkingDirectory=/opt/acc
Environment=AWS_REGION=YOUR_REGION
ExecStart=/usr/bin/python3 /opt/acc/agent/agent.py
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

## Appendix C — Server command line (target contract)
```
./Game.x86_64 -batchmode -nographics -server -port 7778 -slot 1 \
              -roomCode K7M2QD -maxPlayers 10 -killLimit 30 -logFile /var/log/acc/slot1.log
```
`-server` and `-port` already work in the game today. The other flags (`-slot -roomCode -maxPlayers -killLimit`) are Unity change U4/U5.

## Appendix D — Example items
```json
// Rooms
{ "roomCode": "K7M2QD", "status": "OPEN", "isPublic": true, "maxPlayers": 10, "killLimit": 30,
  "playerCount": 3, "slotId": "i-0abc123:7778", "endpoint": {"ip": "203.0.113.10", "port": 7778},
  "createdBy": "guest", "createdAt": "2026-10-20T10:15:00Z", "ttl": 1792500000 }

// Servers
{ "slotId": "i-0abc123:7778", "instanceId": "i-0abc123", "publicIp": "203.0.113.10", "port": 7778,
  "status": "IN_USE", "roomCode": "K7M2QD", "lastHeartbeat": 1792492800, "ttl": 1792492860 }

// Players
{ "userId": "9f1c...-sub", "username": "Sush", "totalKills": 128, "totalDeaths": 97, "totalWins": 9,
  "totalMatches": 21, "gsi1pk": "LB", "gsi1sk": 128 }
```

## Appendix E — Handy checks
- List Free-plan-eligible EC2 types: `aws ec2 describe-instance-types --filters Name=free-tier-eligible,Values=true --query "InstanceTypes[*].[InstanceType]" --output text | sort`
- Test an endpoint: `curl -X POST "$API/guest/quickplay" -H "content-type: application/json" -d '{"displayName":"tester"}'`
- UDP reachability from your laptop: `nc -u -v <ip> 7777` (no reply is normal; check the server log instead).
