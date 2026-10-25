# -*- coding: utf-8 -*-
"""Tests for the local‑test mode of GameServerAgent.

The goal is to ensure that when ``--local-test`` is used the agent:

* does **not** invoke any BackendApiClient methods,
* creates dummy server identifiers,
* launches the UnityServerSupervisor (using the Python interpreter as a
  stand‑in binary for a quick exit command), and
* still serves the ``/status`` loopback endpoint.
"""

from __future__ import annotations

import sys
import uuid
from unittest import mock

import pytest

# Adjust import path for the repository layout
import os
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from cloud.agent.agent import GameServerAgent, BackendApiClient


def test_local_test_mode_no_api_calls(tmp_path):
    """Agent started with ``local_test=True`` makes no backend calls.

    A mock BackendApiClient is injected to verify that none of its methods are
    invoked.  The supervisor is started with the current Python executable – a
    command that immediately exits – so the test runs quickly.
    """
    mock_api = mock.create_autospec(BackendApiClient, instance=True)

    agent = GameServerAgent(
        api_url="https://example.invalid",
        region="us-east-1",
        public_address=None,
        game_port=7777,
        capacity=2,
        server_binary=sys.executable,  # trivial binary that exits instantly
        loopback_port=0,  # in‑memory dispatch, no actual socket needed
        heartbeat_interval=1.0,
        log_file=None,
        api_client=mock_api,
        local_test=True,
    )

    # Start the agent – should succeed and use dummy identifiers
    assert agent.start() is True
    assert agent.server_id == "local-test-server"
    assert agent.generation == "local-test-gen"
    assert agent.match_id == "local-test-match"

    # Verify that no backend API methods were called
    mock_api.register.assert_not_called()
    mock_api.heartbeat.assert_not_called()
    mock_api.consume_ticket.assert_not_called()
    mock_api.submit_results.assert_not_called()
    mock_api.release_reservation.assert_not_called()

    # The supervisor should have started and then stop cleanly
    assert agent.supervisor is not None
    # Wait for the supervisor process to exit (it should terminate quickly)
    if agent.supervisor and getattr(agent.supervisor, 'process', None):
        try:
            agent.supervisor.process.wait(timeout=5)
        except Exception:
            pass
    assert not agent.supervisor.is_alive()  # the binary should have exited

    # Stop the agent – should be a no‑op for the supervisor
    agent.stop()
    # Ensure stop does not raise and leaves the supervisor cleanly
    assert agent.supervisor is None or not agent.supervisor.is_alive()


def test_local_test_status_endpoint():
    """When ``local_test`` is True the ``/status`` endpoint returns placeholder data.

    The ``agent`` fixture from ``test_agent.py`` creates a mock API client; we
    construct a fresh agent with ``local_test=True`` and use the in‑memory request
    helper to query ``/status``.
    """
    from cloud.agent.test_agent import dispatch_loopback_request

    # Re‑create a minimal agent in local‑test mode
    test_agent = GameServerAgent(
        api_url="https://example.invalid",
        region="us-east-1",
        public_address=None,
        game_port=7778,
        capacity=2,
        server_binary=sys.executable,
        loopback_port=0,
        heartbeat_interval=1.0,
        log_file=None,
        api_client=mock.create_autospec(BackendApiClient, instance=True),
        local_test=True,
    )
    assert test_agent.start() is True
    # Wait for the stand‑in process to finish before querying /status
    import time
    timeout = time.time() + 5
    while test_agent.supervisor.is_alive() and time.time() < timeout:
        time.sleep(0.1)
    assert not test_agent.supervisor.is_alive()  # ensure process has exited

    code, status = dispatch_loopback_request(test_agent, "GET", "/status")
    assert code == 200
    # In local‑test mode the identifiers are the placeholders defined in start()
    assert status["serverId"] == "local-test-server"
    assert status["serverGeneration"] == "local-test-gen"
    assert status["matchId"] == "local-test-match"
    assert status["processRunning"] is False

    test_agent.stop()

def test_local_test_no_heartbeat_calls():
    """Ensure that in local‑test mode no backend API calls are made over time.
    The heartbeat loop is not started, so the mock BackendApiClient should remain untouched.
    """
    mock_api = mock.create_autospec(BackendApiClient, instance=True)
    test_agent = GameServerAgent(
        api_url="https://example.invalid",
        region="us-east-1",
        public_address=None,
        game_port=7779,
        capacity=2,
        server_binary=sys.executable,
        loopback_port=0,
        heartbeat_interval=0.1,
        log_file=None,
        api_client=mock_api,
        local_test=True,
    )
    assert test_agent.start() is True
    # Give a short window for any unexpected background work
    import time
    time.sleep(0.5)
    # No backend API methods should have been called
    mock_api.register.assert_not_called()
    mock_api.heartbeat.assert_not_called()
    mock_api.consume_ticket.assert_not_called()
    mock_api.submit_results.assert_not_called()
    mock_api.release_reservation.assert_not_called()
    test_agent.stop()

