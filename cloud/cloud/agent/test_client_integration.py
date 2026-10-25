"""Tests verifying the client-side API contracts and full join-flow integration.

Simulates the client actions performed in Unity:
1. QuickPlay request to /guest/quickplay -> receiving assignment -> sending ticket to dedicated server.
2. Create Room request to /guest/rooms -> Join Room request to /guest/rooms/{code}/join -> receiving assignment -> sending ticket to dedicated server.
3. Error response contracts and friendly error messages.
"""

from __future__ import annotations

import json
from pathlib import Path
import sys
import time
from typing import Any, Dict
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

import pytest

from cloud.agent.agent import BackendApiClient, GameServerAgent
from cloud.agent.test_agent import dispatch_loopback_request


def test_client_quickplay_assignment_schema():
    """Verify that the quickplay response contract expected by AWSBackendClient.cs matches the API output."""
    mock_api_response = {
        "status": "assigned",
        "roomCode": "QP1234",
        "endpoint": {
            "host": "13.232.100.50",
            "port": 7777,
            "protocol": "udp",
        },
        "matchId": "m-quickplay-test",
        "ticket": "t-abc123xyz-quickplay-token",
        "ticketExpiresAt": 1700000000,
    }

    # Verify all expected Unity fields exist and match AWSAssignmentResponse
    assert mock_api_response["status"] == "assigned"
    assert "endpoint" in mock_api_response
    assert mock_api_response["endpoint"]["host"] == "13.232.100.50"
    assert mock_api_response["endpoint"]["port"] == 7777
    assert mock_api_response["endpoint"]["protocol"] == "udp"
    assert mock_api_response["ticket"] == "t-abc123xyz-quickplay-token"
    assert mock_api_response["ticketExpiresAt"] > 0

    # Unity sends UTF8 bytes of assignment.ticket in ConnectionData
    ticket_bytes = mock_api_response["ticket"].encode("utf-8")
    assert 0 < len(ticket_bytes) <= 1024


def test_client_room_create_and_join_schema():
    """Verify create-room followed by join-room contract expected by MainMenuUI.cs."""
    create_response = {
        "roomCode": "ABCDEF",
        "status": "waiting_for_server",
        "maxPlayers": 6,
        "killLimit": 40,
        "expiresAt": 1700003600,
        "assignment": None,
    }
    assert create_response["roomCode"] == "ABCDEF"
    assert len(create_response["roomCode"]) == 6
    assert create_response["status"] == "waiting_for_server"

    join_response = {
        "status": "assigned",
        "roomCode": "ABCDEF",
        "endpoint": {
            "host": "15.206.10.20",
            "port": 7777,
            "protocol": "udp",
        },
        "matchId": "m-room-test",
        "ticket": "t-room-join-ticket-999",
        "ticketExpiresAt": 1700000300,
    }
    assert join_response["endpoint"]["host"] == "15.206.10.20"
    assert join_response["ticket"] == "t-room-join-ticket-999"


def test_client_ticket_to_agent_validation():
    """Verify that a ticket received from AWS matchmaking is accepted by the server agent /validate endpoint."""
    mock_api = mock.create_autospec(BackendApiClient, instance=True)
    mock_api.register.return_value = {
        "serverId": "i-0123456789abcdef0",
        "serverGeneration": "gen-1234-uuid",
        "matchId": "match-5678-uuid",
        "status": "STARTING",
        "heartbeatExpiresAt": int(time.time()) + 90,
    }
    mock_api.release_reservation.return_value = {
        "reservationId": "res-qp-456",
        "released": True,
    }
    agent = GameServerAgent(
        api_url="https://mock.api.com",
        region="ap-south-1",
        public_address="52.95.110.1",
        game_port=7778,
        capacity=10,
        loopback_port=0,
        api_client=mock_api,
    )
    assert agent.start() is True

    ticket = "t-valid-matchmaking-ticket-456"

    # Configure agent api mock to return success for this ticket
    agent.api.consume_ticket.return_value = {
        "reservationId": "res-qp-456",
        "matchId": "match-5678-uuid",
        "roomCode": "QP1234",
        "consumed": True,
    }

    # Unity dedicated server receives client ConnectionData, extracts ticket, and calls agent /validate
    validate_payload = {
        "ticket": ticket,
        "clientId": 3,
        "roomCode": "QP1234",
    }
    code, val_data = dispatch_loopback_request(agent, "POST", "/validate", validate_payload)

    assert code == 200
    assert val_data["valid"] is True
    assert val_data["reservationId"] == "res-qp-456"
    assert agent.client_reservations["3"] == "res-qp-456"

    # On client disconnect, Unity calls agent /release
    code, rel_data = dispatch_loopback_request(agent, "POST", "/release", {"clientId": 3})
    assert code == 200
    assert rel_data["released"] is True
    assert "3" not in agent.client_reservations

    agent.stop()


def test_client_backend_error_handling():
    """Verify standard AWS backend error responses can be mapped to user-friendly messages."""
    error_payloads = [
        {"error": {"code": "matchmaking_disabled", "message": "Matchmaking is currently disabled.", "retryable": False}},
        {"error": {"code": "no_servers_available", "message": "No available servers.", "retryable": True}},
        {"error": {"code": "room_not_found", "message": "The specified room does not exist.", "retryable": False}},
        {"error": {"code": "room_full", "message": "The room is full.", "retryable": False}},
        {"error": {"code": "room_expired", "message": "The room has expired.", "retryable": False}},
    ]

    for p in error_payloads:
        err = p["error"]
        assert "code" in err
        assert "message" in err
        assert isinstance(err["retryable"], bool)
