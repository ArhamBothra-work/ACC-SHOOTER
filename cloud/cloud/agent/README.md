# ACC Shooter — EC2 Dedicated Server Agent

A lightweight daemon that runs on the AWS EC2 dedicated game server host. It acts as the bridge between the headless Unity Linux server (`ACCServer.x86_64`) and the AWS backend HTTP API.

## Features

1. **Backend Registration**: Uses the instance profile IAM role and AWS SigV4 to register the server slot (`POST /internal/servers/register`).
2. **Periodic Heartbeat**: Keeps the server slot alive in DynamoDB (`POST /internal/servers/heartbeat`) and triggers server-side TTL cleanup for expired reservations.
3. **Process Supervision**: Spawns and monitors `./ACCServer.x86_64` with the appropriate dedicated-server arguments (`-batchmode -nographics -server -port <port> -maxPlayers <capacity> -matchId <matchId>`).
4. **Loopback Service (`127.0.0.1:8081`)**:
   - `POST /validate`: Called by Unity during connection approval. Consumes the player's join ticket via `POST /internal/tickets/consume`.
   - `POST /result`: Called by Unity when the match concludes. Submits final match scores and stats via `POST /internal/matches/{matchId}/results`.
   - `POST /release`: Called by Unity when a player disconnects to free up capacity.
   - `GET /status`: Local diagnostic endpoint reporting agent and server status.
5. **Graceful Shutdown**: Intercepts `SIGINT`/`SIGTERM` to mark the server `STOPPED` in DynamoDB and terminate the server process safely.

## Configuration

| Argument | Env Var | Default | Description |
|---|---|---|---|
| `--api-url` | `API_URL` | Deployed HTTP API endpoint | Base URL of the API Gateway HTTP API |
| `--region` | `AWS_REGION` | `ap-south-1` | AWS Region of the deployment |
| `--public-address`| `PUBLIC_ADDRESS` | Auto (IMDSv2) | Public IPv4 or DNS name of the EC2 instance |
| `--game-port` | `GAME_PORT` | `7777` | UDP game port for Unity Transport |
| `--capacity` | `CAPACITY` | `10` | Maximum players supported by the match |
| `--server-binary` | `SERVER_BINARY` | None / path | Path to `ACCServer.x86_64` executable |
| `--loopback-port` | `AGENT_PORT` | `8081` | Local port for Unity server IPC |
| `--heartbeat-interval`| `HEARTBEAT_INTERVAL`| `20.0` | Heartbeat frequency in seconds (expiry is 90s) |
| `--log-file` | `SERVER_LOG` | `server.log` | Path for Unity headless log file |

## Local Loopback Contract (for Unity Dedicated Server)

### 1. Connection Approval: `POST /validate`
Called from `NetworkBootstrap.Approve` when a player joins with `ConnectionData`:
```json
{
  "ticket": "<join-ticket-bearer-token>",
  "clientId": 1
}
```
Response:
```json
{
  "valid": true,
  "reservationId": "res-0123456789abcdef",
  "matchId": "22222222-2222-4222-8222-222222222222",
  "roomCode": "AB12CD"
}
```
If rejected or expired:
```json
{
  "valid": false,
  "error": "ticket_invalid_or_consumed"
}
```

### 2. Match Outcome: `POST /result`
Called from `MatchManager.EndMatch`:
```json
{
  "winnerTeam": 0,
  "teamScores": [30, 24],
  "durationSeconds": 480,
  "players": [
    {"playerId": "p1", "team": 0, "kills": 15, "deaths": 8},
    {"playerId": "p2", "team": 1, "kills": 8, "deaths": 15}
  ]
}
```
Response:
```json
{
  "accepted": true,
  "matchId": "22222222-2222-4222-8222-222222222222"
}
```

## Example Systemd Service (`/etc/systemd/system/acc-agent.service`)

```ini
[Unit]
Description=ACC Dedicated Game Server Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=ubuntu
WorkingDirectory=/opt/acc
Environment=AWS_REGION=ap-south-1
Environment=API_URL=https://<api-id>.execute-api.ap-south-1.amazonaws.com
Environment=SERVER_BINARY=/opt/acc/server/ACCServer.x86_64
Environment=GAME_PORT=7777
Environment=CAPACITY=10
ExecStart=/usr/bin/python3 /opt/acc/agent/agent.py
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```
