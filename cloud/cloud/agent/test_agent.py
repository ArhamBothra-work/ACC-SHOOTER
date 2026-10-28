"""Automated tests for the ACC Dedicated Server Agent.

Uses isolated mocks; makes no live AWS or network calls.
"""

from __future__ import annotations

import io
import json
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request
from typing import Any, Dict, Optional, Tuple
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

import pytest
from botocore.credentials import Credentials

from cloud.agent.agent import (
    BackendApiClient,
    GameServerAgent,
    LoopbackRequestHandler,
    SigV4HttpError,
    UnityServerSupervisor,
    get_imds_public_ip,
    main,
)


def make_mock_credentials():
    return Credentials("MOCKACCESSKEY123", "MOCKSECRETKEY123456789", "MOCKSESSIONTOKEN")


class DummyHttpResponse:
    def __init__(self, status_code: int, data: Dict[str, Any]):
        self.status = status_code
        self.code = status_code
        self._body = json.dumps(data).encode("utf-8")

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


class DummySocket:
    """In-memory socket stream for testing HTTP request handlers without OS networking."""

    def __init__(self, input_bytes: bytes = b""):
        self.input_bytes = input_bytes
        self.output_bytes = bytearray()

    def makefile(self, mode: str, *args: Any, **kwargs: Any) -> Any:
        if "r" in mode:
            return io.BytesIO(self.input_bytes)
        return self

    def write(self, b: bytes) -> None:
        self.output_bytes.extend(b)

    def sendall(self, b: bytes) -> None:
        self.output_bytes.extend(b)


def dispatch_loopback_request(
    agent: GameServerAgent,
    method: str,
    path: str,
    body: Optional[Dict[str, Any]] = None,
    client_ip: str = "127.0.0.1",
) -> Tuple[int, Dict[str, Any]]:
    """Simulates an in-memory HTTP request to LoopbackRequestHandler."""
    payload = json.dumps(body).encode("utf-8") if body is not None else b""
    raw_req = (
        f"{method} {path} HTTP/1.1\r\n"
        f"Host: 127.0.0.1\r\n"
        f"Content-Length: {len(payload)}\r\n"
        f"Content-Type: application/json\r\n\r\n"
    ).encode("utf-8") + payload

    sock = DummySocket(raw_req)
    server = mock.MagicMock()
    server.agent = agent
    LoopbackRequestHandler(sock, (client_ip, 12345), server)

    output = bytes(sock.output_bytes).decode("utf-8")
    lines = output.split("\r\n")
    status_code = int(lines[0].split()[1])
    body_part = output.split("\r\n\r\n", 1)[1] if "\r\n\r\n" in output else "{}"
    data = json.loads(body_part) if body_part else {}
    return status_code, data


# -----------------------------------------------------------------------------
# 1. ApiClient Tests
# -----------------------------------------------------------------------------

def test_api_client_register():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    mock_resp = DummyHttpResponse(
        201,
        {
            "serverId": "i-0123456789abcdef0",
            "serverGeneration": "11111111-1111-4111-8111-111111111111",
            "matchId": "22222222-2222-4222-8222-222222222222",
            "status": "STARTING",
            "heartbeatExpiresAt": 1900000090,
            "duplicate": False,
        },
    )

    with mock.patch("urllib.request.urlopen", return_value=mock_resp) as mock_urlopen:
        res = client.register(
            public_address="52.95.110.1",
            game_port=7777,
            capacity=10,
            protocol="udp",
            idempotency_key="test-key-12345678",
        )

        assert res["serverId"] == "i-0123456789abcdef0"
        assert res["status"] == "STARTING"
        assert mock_urlopen.call_count == 1
        req = mock_urlopen.call_args[0][0]
        assert req.get_method() == "POST"
        assert req.full_url == "https://mock.api.com/internal/servers/register"
        assert req.headers["Idempotency-key"] == "test-key-12345678"
        assert "Authorization" in req.headers
        body = json.loads(req.data.decode("utf-8"))
        assert body == {
            "publicAddress": "52.95.110.1",
            "gamePort": 7777,
            "capacity": 10,
            "protocol": "udp",
        }


def test_api_client_heartbeat():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    mock_resp = DummyHttpResponse(
        200,
        {
            "serverId": "i-0123456789abcdef0",
            "status": "READY",
            "heartbeatExpiresAt": 1900000090,
        },
    )

    with mock.patch("urllib.request.urlopen", return_value=mock_resp) as mock_urlopen:
        res = client.heartbeat(generation="11111111-1111-4111-8111-111111111111", status="READY")
        assert res["status"] == "READY"
        req = mock_urlopen.call_args[0][0]
        body = json.loads(req.data.decode("utf-8"))
        assert body == {"generation": "11111111-1111-4111-8111-111111111111", "status": "READY"}


def test_api_client_consume_ticket():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    mock_resp = DummyHttpResponse(
        200,
        {
            "reservationId": "res-123",
            "matchId": "match-456",
            "roomCode": "ABC123",
            "consumed": True,
        },
    )

    with mock.patch("urllib.request.urlopen", return_value=mock_resp):
        res = client.consume_ticket(ticket="t-token-abc", generation="gen-uuid")
        assert res["reservationId"] == "res-123"
        assert res["consumed"] is True


def test_api_client_release_reservation():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    mock_resp = DummyHttpResponse(200, {"reservationId": "res-123", "released": True})

    with mock.patch("urllib.request.urlopen", return_value=mock_resp) as mock_urlopen:
        res = client.release_reservation("res-123", generation="gen-uuid")
        assert res["released"] is True
        req = mock_urlopen.call_args[0][0]
        assert req.full_url == "https://mock.api.com/internal/servers/reservations/res-123/release"


def test_api_client_submit_results():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    mock_resp = DummyHttpResponse(
        201,
        {
            "matchId": "match-456",
            "accepted": True,
            "duplicate": False,
            "verificationStatus": "server_reported",
        },
    )

    with mock.patch("urllib.request.urlopen", return_value=mock_resp) as mock_urlopen:
        res = client.submit_results(
            match_id="match-456",
            generation="gen-uuid",
            winner_team=0,
            team_scores=[30, 20],
            duration_seconds=500,
            players=[{"playerId": "p1", "team": 0, "kills": 15, "deaths": 5}],
        )
        assert res["accepted"] is True
        req = mock_urlopen.call_args[0][0]
        assert req.full_url == "https://mock.api.com/internal/matches/match-456/results"


def test_api_client_error_handling():
    creds = make_mock_credentials()
    client = BackendApiClient(api_url="https://mock.api.com", region="ap-south-1", credentials=creds)

    error_payload = json.dumps({
        "error": {
            "code": "ticket_invalid_or_consumed",
            "message": "Ticket is expired or already consumed.",
            "retryable": False,
        }
    }).encode("utf-8")

    http_error = urllib.error.HTTPError(
        url="https://mock.api.com/internal/tickets/consume",
        code=409,
        msg="Conflict",
        hdrs={},
        fp=io.BytesIO(error_payload),
    )

    with mock.patch("urllib.request.urlopen", side_effect=http_error):
        with pytest.raises(SigV4HttpError) as exc_info:
            client.consume_ticket("bad-ticket", "gen-uuid")
        assert exc_info.value.status_code == 409
        assert exc_info.value.error_code == "ticket_invalid_or_consumed"
        assert "Ticket is expired" in exc_info.value.message


# -----------------------------------------------------------------------------
# 2. Supervisor Tests
# -----------------------------------------------------------------------------

def test_supervisor_command_building():
    sup = UnityServerSupervisor(
        binary_path="/opt/acc/ACCServer.x86_64",
        game_port=7777,
        capacity=10,
        match_id="match-abc",
        room_code="ROOM12",
        kill_limit=45,
        log_file="/var/log/acc.log",
    )
    cmd = sup.build_command()
    assert cmd == [
        "/opt/acc/ACCServer.x86_64",
        "-batchmode",
        "-nographics",
        "-server",
        "-port",
        "7777",
        "-maxPlayers",
        "10",
        "-matchId",
        "match-abc",
        "-roomCode",
        "ROOM12",
        "-killLimit",
        "45",
        "-logFile",
        "/var/log/acc.log",
    ]


def test_supervisor_start_and_stop():
    sup = UnityServerSupervisor(
        binary_path=sys.executable,
        game_port=7777,
    )
    sup.build_command = lambda: [sys.executable, "-c", "import time; time.sleep(5)"]

    assert sup.start() is True
    assert sup.is_alive() is True
    sup.stop(timeout=1.0)
    assert sup.is_alive() is False


# -----------------------------------------------------------------------------
# 3. IMDSv2 Discovery Test
# -----------------------------------------------------------------------------

def test_imds_public_ip_discovery():
    token_resp = DummyHttpResponse(200, {})
    token_resp._body = b"mock-token-xyz"

    ip_resp = DummyHttpResponse(200, {})
    ip_resp._body = b"54.120.30.40"

    with mock.patch("urllib.request.urlopen", side_effect=[token_resp, ip_resp]):
        ip = get_imds_public_ip()
        assert ip == "54.120.30.40"


# -----------------------------------------------------------------------------
# 4. Loopback HTTP Endpoint & Integration Tests
# -----------------------------------------------------------------------------

@pytest.fixture
def test_agent():
    mock_api = mock.create_autospec(BackendApiClient, instance=True)
    mock_api.register.return_value = {
        "serverId": "i-0123456789abcdef0",
        "serverGeneration": "gen-1234-uuid",
        "matchId": "match-5678-uuid",
        "status": "STARTING",
        "heartbeatExpiresAt": int(time.time()) + 90,
    }
    mock_api.heartbeat.return_value = {
        "serverId": "i-0123456789abcdef0",
        "status": "READY",
        "heartbeatExpiresAt": int(time.time()) + 90,
    }
    mock_api.consume_ticket.return_value = {
        "reservationId": "res-999",
        "matchId": "match-5678-uuid",
        "roomCode": "K7M2QD",
        "consumed": True,
    }
    mock_api.release_reservation.return_value = {
        "reservationId": "res-999",
        "released": True,
    }
    mock_api.submit_results.return_value = {
        "matchId": "match-5678-uuid",
        "accepted": True,
        "duplicate": False,
        "verificationStatus": "server_reported",
    }

    agent = GameServerAgent(
        api_url="https://mock.api.com",
        region="ap-south-1",
        public_address="52.95.110.1",
        game_port=7778,
        capacity=10,
        loopback_port=0,  # 0 indicates in-memory dispatch test
        api_client=mock_api,
    )
    yield agent
    agent.stop()


def test_agent_full_lifecycle(test_agent):
    # Agent start
    assert test_agent.start() is True
    assert test_agent.server_id == "i-0123456789abcdef0"
    assert test_agent.generation == "gen-1234-uuid"
    assert test_agent.match_id == "match-5678-uuid"
    assert test_agent.status == "READY"

    # 1. Test GET /status
    code, status_data = dispatch_loopback_request(test_agent, "GET", "/status")
    assert code == 200
    assert status_data["status"] == "READY"
    assert status_data["serverId"] == "i-0123456789abcdef0"
    assert status_data["matchId"] == "match-5678-uuid"

    # 2. Test POST /validate (Success)
    code, val_data = dispatch_loopback_request(
        test_agent, "POST", "/validate", {"ticket": "valid-ticket-token", "clientId": 42}
    )
    assert code == 200
    assert val_data["valid"] is True
    assert val_data["reservationId"] == "res-999"
    assert val_data["roomCode"] == "K7M2QD"
    assert test_agent.client_reservations["42"] == "res-999"

    # 3. Test POST /validate (Rejection)
    test_agent.api.consume_ticket.side_effect = SigV4HttpError(
        409, "ticket_invalid_or_consumed", "Ticket is expired"
    )
    code, bad_val_data = dispatch_loopback_request(
        test_agent, "POST", "/validate", {"ticket": "expired-ticket"}
    )
    assert code == 200
    assert bad_val_data["valid"] is False
    assert bad_val_data["error"] == "ticket_invalid_or_consumed"

    # 4. Test POST /release
    code, rel_data = dispatch_loopback_request(test_agent, "POST", "/release", {"clientId": 42})
    assert code == 200
    assert rel_data["released"] is True
    assert "42" not in test_agent.client_reservations

    # 5. Test POST /result
    code, res_data = dispatch_loopback_request(
        test_agent,
        "POST",
        "/result",
        {
            "winnerTeam": 0,
            "teamScores": [30, 24],
            "durationSeconds": 450,
            "players": [
                {"playerId": "p1", "team": 0, "kills": 15, "deaths": 8},
                {"playerId": "p2", "team": 1, "kills": 8, "deaths": 15},
            ],
        },
    )
    assert code == 200
    assert res_data["accepted"] is True
    assert res_data["matchId"] == "match-5678-uuid"
    assert test_agent.status == "DRAINING"

    # 6. Test Heartbeat cycle
    assert test_agent.heartbeat_cycle() is True

    # 7. Test Non-loopback client rejection (403)
    code, non_loopback = dispatch_loopback_request(
        test_agent, "GET", "/status", client_ip="192.168.1.100"
    )
    assert code == 403
    assert "Loopback access only" in non_loopback["error"]


def test_heartbeat_loop_stops_after_unity_exit_without_further_heartbeats(test_agent):
    assert test_agent.start() is True
    test_agent.heartbeat_interval = 0.05
    test_agent.api.heartbeat.side_effect = lambda generation, status: {
        "serverId": test_agent.server_id,
        "status": status,
        "heartbeatExpiresAt": int(time.time()) + 90,
    }
    test_agent.supervisor = mock.Mock()
    test_agent.supervisor.poll.return_value = 1
    test_agent.api.heartbeat.reset_mock()

    test_agent.run_heartbeat_loop()

    assert test_agent.process_exit_code == 1
    assert test_agent._stop_event.is_set()
    assert [call.kwargs["status"] for call in test_agent.api.heartbeat.call_args_list] == ["DRAINING"]
    assert test_agent.status == "DRAINING"


def test_exited_game_process_stops_heartbeats_and_marks_registration_stopped(test_agent):
    assert test_agent.start() is True
    test_agent.api.heartbeat.side_effect = lambda generation, status: {
        "serverId": test_agent.server_id,
        "status": status,
        "heartbeatExpiresAt": int(time.time()) + 90,
    }
    test_agent.supervisor = mock.Mock()
    test_agent.supervisor.poll.return_value = 1

    assert test_agent.heartbeat_cycle() is False
    assert test_agent._stop_event.is_set()
    assert test_agent.status == "DRAINING"
    test_agent.stop()

    assert [call.kwargs["status"] for call in test_agent.api.heartbeat.call_args_list] == [
        "READY",
        "DRAINING",
        "STOPPED",
    ]
    assert test_agent.status == "STOPPED"


def test_failed_ready_transition_marks_startup_registration_stopped():
    api = mock.create_autospec(BackendApiClient, instance=True)
    api.register.return_value = {
        "serverId": "i-0123456789abcdef0",
        "serverGeneration": "11111111-1111-4111-8111-111111111111",
        "matchId": "22222222-2222-4222-8222-222222222222",
        "status": "STARTING",
    }
    api.heartbeat.side_effect = [
        RuntimeError("READY transition failed"),
        {"status": "STOPPED"},
    ]
    supervisor = mock.Mock()
    supervisor.start.return_value = True

    with mock.patch("cloud.agent.agent.UnityServerSupervisor", return_value=supervisor):
        agent = GameServerAgent(
            api_url="https://mock.api.com",
            region="ap-south-1",
            public_address="52.95.110.1",
            game_port=7777,
            server_binary="/opt/acc/server",
            loopback_port=0,
            api_client=api,
        )
        assert agent.start() is False

    supervisor.stop.assert_called_once()
    assert [call.kwargs["status"] for call in api.heartbeat.call_args_list] == [
        "READY",
        "STOPPED",
    ]
    assert agent.status == "STOPPED"


def test_unity_process_exit_returns_failure_for_systemd_restart():
    agent = mock.Mock()
    agent.local_test = False
    agent.process_exit_code = None
    agent.start.return_value = True
    agent.run_heartbeat_loop.side_effect = lambda: setattr(agent, "process_exit_code", 1)

    with mock.patch("cloud.agent.agent.parse_args", return_value=mock.Mock()), mock.patch(
        "cloud.agent.agent.GameServerAgent",
        return_value=agent,
    ), mock.patch("cloud.agent.agent.signal.signal"):
        assert main() == 1

    agent.stop.assert_called_once()


def test_unity_dedicated_server_validate_and_release(test_agent):
    """Simulates Unity NetworkBootstrap AgentValidateRequestPayload and AgentReleaseRequestPayload."""
    assert test_agent.start() is True

    # Unity sends AgentValidateRequestPayload
    unity_payload = {"ticket": "test-unity-ticket-token", "clientId": 10}
    code, resp = dispatch_loopback_request(test_agent, "POST", "/validate", unity_payload)
    assert code == 200
    assert resp["valid"] is True
    assert resp["reservationId"] == "res-999"
    assert test_agent.client_reservations["10"] == "res-999"

    # Unity sends AgentReleaseRequestPayload on client disconnect
    unity_release = {"clientId": 10}
    code, rel_resp = dispatch_loopback_request(test_agent, "POST", "/release", unity_release)
    assert code == 200
    assert rel_resp["released"] is True
    assert "10" not in test_agent.client_reservations


def test_unity_dedicated_server_result_reporting(test_agent):
    """Simulates Unity MatchManager MatchResultPayload at match end."""
    assert test_agent.start() is True

    unity_result_payload = {
        "winnerTeam": 1,
        "teamScores": [18, 30],
        "durationSeconds": 320,
        "players": [
            {"playerId": "client-0", "team": 0, "kills": 10, "deaths": 12},
            {"playerId": "client-1", "team": 1, "kills": 16, "deaths": 7},
            {"playerId": "client-2", "team": 1, "kills": 14, "deaths": 11},
        ],
    }
    code, resp = dispatch_loopback_request(test_agent, "POST", "/result", unity_result_payload)
    assert code == 200
    assert resp["accepted"] is True
    assert resp["matchId"] == test_agent.match_id
    assert test_agent.status == "DRAINING"

    # Test malformed payload rejection
    bad_payload = {"winnerTeam": 5, "teamScores": [10]}
    code, bad_resp = dispatch_loopback_request(test_agent, "POST", "/result", bad_payload)
    assert code == 400
    assert bad_resp["accepted"] is False
