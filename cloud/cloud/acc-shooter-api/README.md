# ACC Shooter backend

Python 3.13 Lambda handler and AWS SAM template for the ACC Shooter HTTP API.
The reported production endpoint is
`https://q9u78vshof.execute-api.ap-south-1.amazonaws.com`. This change is
source-only: it has not been deployed and no live API or AWS resource was used.

## Runtime gates

`EnableMatchmaking` and `EnableLeaderboard` are both `false` by default in the
SAM template. With matchmaking disabled, private-room joining and Quick Play return
`503 matchmaking_disabled`; they do not allocate server capacity or issue
tickets. Set matchmaking true only after the deployment prerequisites below
are met. `/config` reflects those gates. Guest room creation currently creates
an expiring room-code shell, not a playable room, and `privateRooms` remains
false until room ownership/guest identity is implemented.

## Common API behavior

Successful JSON responses include `Content-Type: application/json` and
`Cache-Control: no-store`. API errors use:

```json
{
  "error": {
    "code": "no_servers_available",
    "message": "No healthy game server has available capacity.",
    "retryable": true
  }
}
```

Some errors include an `error.details` object. Clients should branch on
`error.code`, not message text. Malformed JSON returns `400 invalid_json`,
non-object JSON returns `400 invalid_request`, unknown fields return
`400 unknown_fields`, unexpected errors return retryable `500 internal_error`,
and unknown routes return `404 route_not_found`. API Gateway may reject an
unsigned internal request before Lambda and return its own `403` response.

Guest operations that allocate capacity require an `Idempotency-Key` header
with 16–128 URL-safe characters. A retry using the same key and same operation
payload returns the original assignment and ticket without reserving another
slot while that ticket remains issued and unexpired. If that ticket is consumed
or expired, a retry returns `409 idempotency_conflict`; start a new user
operation with a fresh key. Reusing a key for a different payload also returns
`409 idempotency_conflict`. Idempotency records expire after 24 hours
(DynamoDB TTL deletion is asynchronous). This key is not authentication or a
guest identity.

## Public API contracts

### `GET /config`

```json
{
  "gameName": "ACC Shooter",
  "region": "ap-south-1",
  "protocol": "udp",
  "maxPlayersPerMatch": 10,
  "defaultKillLimit": 30,
  "features": {
    "guestRoomCreation": true,
    "privateRooms": false,
    "quickplay": false,
    "ticketStatus": true,
    "leaderboard": false,
    "googleSignIn": false
  }
}
```

`quickplay` reflects `EnableMatchmaking`; `leaderboard` reflects
`EnableLeaderboard`. `privateRooms` stays false because no authenticated
room owner exists. Ticket status is a read-only lookup; it does not mean a game
server is available.

### `POST /guest/rooms` — create a private-room code

Headers: `Idempotency-Key: <16–128 URL-safe characters>`

Request (both fields optional):

```json
{"maxPlayers": 10, "killLimit": 30}
```

`maxPlayers` is an integer from 2–10; `killLimit` is an integer from 1–200.
Success is `201 Created`:

```json
{
  "roomCode": "AB12CD",
  "status": "waiting_for_server",
  "maxPlayers": 10,
  "killLimit": 30,
  "expiresAt": 1900000000,
  "assignment": {
    "status": "pending",
    "reason": "player_join_required"
  }
}
```

The room shell expires in six hours and begins with `playerCount: 0`. This call
does not reserve a server slot, issue a ticket, authenticate an owner, or
promise playability; the creator must use the join route to reserve a real
server slot and receive a ticket. Room-code creation is currently
unauthenticated and needs abuse controls.

Errors include `400 invalid_max_players`, `400 invalid_kill_limit`,
`400 invalid_idempotency_key`, `409 idempotency_conflict`,
`409 idempotency_key_expired`, and retryable `503 room_code_unavailable`.

### `POST /guest/rooms/{code}/join` — join and reserve a private-room slot

Headers: `Idempotency-Key: <16–128 URL-safe characters>`. The request body may
be omitted or `{}`; extra fields are rejected. The six-character room code is
case-insensitive.

Successful assignment (`200 OK`):

```json
{
  "status": "assigned",
  "roomCode": "AB12CD",
  "endpoint": {"host": "game.example.net", "port": 7777, "protocol": "udp"},
  "matchId": "22222222-2222-4222-8222-222222222222",
  "ticket": "<opaque-short-lived-bearer-token>",
  "ticketExpiresAt": 1900000120
}
```

The first successful join atomically pins the room to a healthy server and
increments its player count. Later joins must reserve capacity on that same
server. The room’s player count and server slot count are updated in the same
DynamoDB transaction as the ticket and reservation writes. A room cannot move
to another server while it has active reservations.

Errors include `400 invalid_room_code`, `400 invalid_idempotency_key`,
`404 room_not_found`, `409 room_full`, `409 room_not_joinable`,
`409 idempotency_conflict`, `410 room_expired`, retryable
`503 matchmaking_disabled`, and retryable `503 no_servers_available`.

### `POST /guest/quickplay` — find an available server slot

Headers: `Idempotency-Key: <16–128 URL-safe characters>`. Body may be omitted,
`{}`, or `{"region":"ap-south-1"}`. Only `region` is accepted; when omitted,
the Lambda’s AWS region is used.

Success is the same assignment object as room joining, without `roomCode`.
Each successful assignment reserves one slot in the server, stores a hashed
ticket, and creates a reservation in one DynamoDB transaction.

Errors include `400 invalid_region`, `400 invalid_idempotency_key`,
`409 idempotency_conflict`, retryable `503 matchmaking_disabled`, and retryable
`503 no_servers_available`. A GSI query is only candidate discovery; its result
does not constitute a reservation.

### `POST /guest/tickets/status` — read ticket state

Request:

```json
{"ticket": "<opaque-ticket-token>"}
```

Success:

```json
{"ticketStatus": "issued", "expiresAt": 1900000120}
```

Only status and expiry are returned. Possible status values are `issued`,
`consumed`, and `expired`. Errors are `400 invalid_ticket` and
`404 ticket_not_found`. The token is a bearer secret; do not log it. No
unauthenticated route can issue or consume a ticket.

### `GET /leaderboard?top=20`

Success (when `LEADERBOARD_ENABLED=true` and the new index is backfilled):

```json
{
  "players": [
    {"rank": 1, "displayName": "Player", "kills": 12, "deaths": 3}
  ]
}
```

`top` must be 1–100. The response contains only `rank`, `displayName`,
`kills`, and `deaths`; player IDs and raw table records are not returned.
Invalid limits return `400 invalid_top`; while the index is unavailable, the
route returns retryable `503 leaderboard_unavailable` instead of a partial
global ranking.

## Trusted dedicated-server API contracts

All `/internal/*` routes require API Gateway `AWS_IAM` authorization. The
caller must use SigV4 with temporary credentials from the EC2 instance profile.
The Lambda independently checks the AWS IAM authorizer’s `userArn`, requires
the configured account and stack-specific `*-GameServerAgentRole`, and derives `serverId` from
the EC2 instance-ID session name. No route accepts a caller-supplied server ID.
The IAM role has invoke permission only for these internal routes and has no
DynamoDB permissions. Do not put these credentials or the role on Unity clients.

The template creates a role and instance profile but no EC2 instances. The
operator must also constrain which launch templates/instances may receive this
profile (`iam:PassRole`) and protect the account’s IAM/EC2 administration.

### `POST /internal/servers/register`

AWS_IAM and `Idempotency-Key: <16–128 URL-safe characters>` are required.
Retrying the same key/body returns the same server generation and match ID;
reusing the key with a different body returns `409 idempotency_conflict`.
Request:

```json
{"publicAddress":"game.example.net","gamePort":7777,"capacity":10,"protocol":"udp"}
```

`publicAddress` must be a public IP address or DNS name; port is 1–65535;
capacity is 2–10; protocol is `udp`. `serverId` is derived from the caller,
`matchId` and `generation` are generated by the backend. Registration begins
in `STARTING` and is conditional: it cannot overwrite a healthy instance
registration.

`201 Created`:

```json
{
  "serverId":"i-0123456789abcdef0",
  "serverGeneration":"11111111-1111-4111-8111-111111111111",
  "matchId":"22222222-2222-4222-8222-222222222222",
  "status":"STARTING",
  "heartbeatExpiresAt":1900000090,
  "duplicate":false
}
```

The trusted agent must determine `publicAddress` from instance metadata or a
trusted provisioning source. IAM authenticates the instance role, not the
correctness of a compromised agent’s supplied address.
An idempotent retry returns `200 OK` with the original identifiers and
`duplicate:true`.

### `POST /internal/servers/heartbeat`

AWS_IAM required. Request:

```json
{"generation":"11111111-1111-4111-8111-111111111111","status":"READY"}
```

Status must be `READY`, `DRAINING`, or `STOPPED`. `READY` is allowed only from
`STARTING`/`READY` and while `reservedSlots < capacity`; `DRAINING` prevents new
allocations; `STOPPED` permits later registration. A heartbeat extends
`heartbeatExpiresAt` by 90 seconds. The server must heartbeat more frequently
than that. An expired heartbeat excludes the instance from allocation even
before DynamoDB TTL cleanup.

Response:

```json
{"serverId":"i-0123456789abcdef0","status":"READY","heartbeatExpiresAt":1900000090}
```

### Reserving and releasing server slots

There is no public “reserve arbitrary server” route. Quick Play and room join
discover candidates using `region-status-index`, then transactionally condition
on `READY`, live heartbeat, matching generation/match, and
`reservedSlots < capacity`. Reservation increments the server’s counter and
creates one `Reservations` item plus one `JoinTickets` item. A room reservation
also conditionally pins/increments the room in that same transaction.

An unconsumed ticket’s reservation expires with the 120-second ticket. The
trusted server reaper, run on heartbeat, finds this server’s expired
reservations through `server-status-index` and releases each using a
conditional transaction. After ticket consumption, the server must call the
release route when that player disconnects or the slot otherwise becomes free.

### `POST /internal/tickets/consume`

AWS_IAM required. Request:

```json
{"ticket":"<opaque-ticket-token>","generation":"11111111-1111-4111-8111-111111111111"}
```

The token is SHA-256 hashed for lookup. One conditional DynamoDB update requires
`ISSUED`, matching `serverId` and server generation, and `expiresAt > now`, then
changes it to `CONSUMED`. Only one concurrent request can consume the ticket.

Response:

```json
{
  "reservationId":"<opaque-reservation-id>",
  "matchId":"22222222-2222-4222-8222-222222222222",
  "roomCode":"AB12CD",
  "consumed":true
}
```

`roomCode` is `null` for Quick Play. Invalid, expired, replayed, or
wrong-server tickets return `409 ticket_invalid_or_consumed`.

### `POST /internal/servers/reservations/{reservationId}/release`

AWS_IAM required. Request:

```json
{"generation":"11111111-1111-4111-8111-111111111111"}
```

Response:

```json
{"reservationId":"<opaque-reservation-id>","released":true}
```

Releasing an already released reservation returns `200` with `released:false`.
Reservation state and slot decrement are atomic. For room reservations, room
occupancy is decremented in that transaction; releasing the final player
unpins the room for a later assignment.

### `POST /internal/matches/{matchId}/results`

AWS_IAM required. The caller must own the registered match and current
generation. Request:

```json
{
  "generation":"11111111-1111-4111-8111-111111111111",
  "winnerTeam":1,
  "teamScores":[18,20],
  "durationSeconds":600,
  "players":[
    {"playerId":"ngo-client-id-1","team":0,"kills":7,"deaths":4},
    {"playerId":"ngo-client-id-2","team":1,"kills":8,"deaths":2}
  ]
}
```

`winnerTeam` and each `team` are 0 or 1; `teamScores` has two integers
0–200; duration is 1–7200 seconds; there may be at most 10 players with unique
1–64 character IDs; per-player kills/deaths are 0–200. The first valid result
creates the immutable `Matches` row and drains that server in one conditional
transaction (`201`). Repeating the same payload returns `200` with
`duplicate:true`; a different result returns `409`.

The response labels the data `verificationStatus:"server_reported"`. Results
are not currently linked to authenticated player identities and do not update
the leaderboard. The current Unity gameplay has client-authoritative movement
and firing, so server-reported results are not cheat-resistant competitive
evidence.

First submission:

```json
{
  "matchId":"22222222-2222-4222-8222-222222222222",
  "accepted":true,
  "duplicate":false,
  "verificationStatus":"server_reported"
}
```

An identical retry returns `200 OK` with `duplicate:true`.

Internal error codes include:

| Route | Status and codes |
|---|---|
| Register | `400 invalid_idempotency_key`, `400 invalid_public_address`, `400 invalid_game_port`, `400 invalid_capacity`, `400 invalid_protocol`, `409 idempotency_conflict`, `409 server_already_registered` |
| Heartbeat | `400 invalid_generation`, `400 invalid_server_status`, `409 server_state_conflict` |
| Ticket consume | `400 invalid_ticket`, `400 invalid_generation`, `409 server_generation_mismatch`, `409 server_not_active`, `409 ticket_invalid_or_consumed` |
| Slot release | `400 invalid_reservation_id`, `400 invalid_generation`, `404 reservation_not_found`, `409 reservation_state_conflict` |
| Match result | `400 invalid_match_id`, field-specific `400 invalid_*`, `403 match_server_mismatch`, `409 match_already_submitted`, `409 match_result_conflict` |

All Lambda-handled errors use the common JSON error shape. AWS_IAM may reject an
unauthorized request at API Gateway before the handler runs.

## DynamoDB access patterns and migration

- **Servers** retains primary key `serverId` and old `status-index`; adds
  `region-status-index` (`region` partition, `status` sort) for candidate lookup.
  The GSI is eventually consistent. It finds candidates only; the base-table
  conditional transaction is the capacity authority.
- **Reservations** is a new table keyed by `reservationId`, with TTL and
  `server-status-index` (`serverId` partition, `status` sort) for server-owned
  expiry cleanup. Reservation state and capacity changes use
  `TransactWriteItems`.
- **JoinTickets** remains keyed by `ticketId`, which is the SHA-256 digest of
  the random ticket. Raw bearer tokens are not stored in this table. The
  idempotency response contains the original assignment token so retries can
  receive the same ticket; DynamoDB encrypts it at rest, and the Lambda role is
  the only reader.
- **Rooms** remains keyed by `roomCode`. Existing `status-index` is retained.
  Room creation idempotency records share this table using `REQUEST#...` keys.
- **Matches** remains keyed by `matchId` and enables TTL on `ttl`.
- **Players** retains the old `leaderboard-index` for compatibility and adds
  `leaderboard-v2-index` with 16 deterministic shards:
  `leaderboardShard` is `SHARD#00`…`SHARD#15` using SHA-256(userId) modulo 16.
  `leaderboardSort` is a fixed-width kills score, inverted fixed-width deaths,
  and user ID. The route queries the top N of every shard and merges at most
  `16 * N` records; this avoids a table scan and provides deterministic global
  top-N ordering (kills descending, deaths ascending, userId descending for
  ties). The GSI projects only display name and public stats.

Existing Players rows do not have the new index attributes. A controlled
backfill and updates to every profile/stat writer are required before enabling
the leaderboard. There is currently no authenticated player identity or
trusted result-to-profile update path, so the flag remains false.

Existing Servers rows also need a controlled migration before allocation: they
must receive `region`, `serverGeneration`, `matchId`, `heartbeatExpiresAt`,
`reservedSlots`, `capacity`, `publicAddress`, and `gamePort`, or be left
unavailable until a fresh IAM-authenticated registration replaces an expired
or stopped row. The current source does not backfill any live records.

The template’s existing table-name defaults preserve names used by the current
stack. Parameters allow an isolated staging stack to use distinct names; do
not deploy a second stack with the default names.

## Security assumptions and deployment prerequisites

1. Create/deploy and verify an isolated staging stack using unique table-name
   parameter values. The template creates a named IAM role, so acknowledge
   `CAPABILITY_NAMED_IAM`. Review a CloudFormation change set before executing it.
2. Add a dedicated-server agent that obtains instance-profile credentials
   through IMDSv2, signs requests with SigV4, derives its address from trusted
   metadata, registers, heartbeats, consumes tickets, and releases reservations.
   Verify `AWS_IAM` route authorization and the EC2 assumed-role ARN/session
   format in staging.
3. Restrict role attachment and `iam:PassRole` to the approved server launch
   process. The template role can invoke only the internal routes; it cannot
   directly read/write DynamoDB.
4. Keep `EnableMatchmaking=false` until guest allocation abuse protection
   (secure guest identity or an explicit rate-limit design) and the server agent
   are ready. A random idempotency key is not identity. Do not give the role or
   AWS credentials to the Unity client.
5. Keep `EnableLeaderboard=false` until the sharded Players index is
   backfilled and all trusted profile writers maintain its keys.
6. Do not treat server-reported results as competitive until server-side
   movement, firing, and result authority replace the current client-authority
   concerns.

No EC2 capacity is created by this template. It defines only an IAM role and
instance profile. No SAM build/deploy or AWS change has been run for this task.
The precise next deployment step is to prepare and review a change set for a
separate staging stack, overriding every table-name parameter with a unique
staging name and keeping both feature parameters false. Do not execute that
change set until the schema migration, IAM session identity, and agent
prerequisites above have been reviewed.

## Unity integration status

The inspected `AWSBackendClient` currently calls `POST /guest/rooms` and
deserializes `roomCode`, `status`, `maxPlayers`, `killLimit`, and `message`.
It does not yet call room join, Quick Play, ticket status, or consume assignment
responses. For joining/Quick Play, Unity must send a fresh stable
`Idempotency-Key` per user operation and deserialize `status`, `endpoint.host`,
`endpoint.port`, `endpoint.protocol`, `matchId`, `ticket`, and
`ticketExpiresAt`. The ticket must travel to the game server via NGO connection
data; the game server, not the client, consumes it using its IAM identity.
Never send AWS credentials or the server role to Unity.

No Unity project files were changed.

## Local tests

Run the isolated in-memory test suite from the workspace root:

```powershell
py -3.13 -m pytest cloud\acc-shooter-api\tests -q
```

The fake DynamoDB client models conditional transactions and concurrency for
local behavior tests. It is not an AWS service emulator. `sam validate --lint`
checks the template without deploying or contacting live resources.
