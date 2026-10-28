#!/usr/bin/env python3
"""ACC Dedicated Game Server Agent.

Runs on an AWS EC2 instance hosting Linux dedicated game server instances.
Responsibilities:
1. Registers server slot with the AWS HTTP API using EC2 instance IAM credentials and SigV4.
2. Sends periodic heartbeats to maintain server readiness and update available capacity.
3. Launches and monitors the ACCServer.x86_64 Unity binary with dedicated-server arguments.
4. Exposes a loopback-only HTTP service (127.0.0.1) for the local Unity game server:
   - POST /validate -> consumes join tickets against AWS backend
   - POST /result   -> submits match outcomes and player stats
   - POST /release  -> releases reservations on client disconnect
   - GET  /status   -> diagnostic server health and status
5. Safely drains or stops registrations and manages process shutdown on termination signals.
"""

from __future__ import annotations

import argparse
import http.server
import ipaddress
import json
import logging
import os
import re
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from typing import Any, Dict, List, Optional, Tuple

import botocore.auth
import botocore.awsrequest
import botocore.session

logger = logging.getLogger("acc-agent")

DEFAULT_API_URL = "https://q9u78vshof.execute-api.ap-south-1.amazonaws.com"
DEFAULT_REGION = "ap-south-1"
DEFAULT_GAME_PORT = 7777
DEFAULT_CAPACITY = 10
DEFAULT_LOOPBACK_PORT = 8081
DEFAULT_HEARTBEAT_INTERVAL = 20.0
IMDS_TOKEN_URL = "http://169.254.169.254/latest/api/token"
IMDS_IPV4_URL = "http://169.254.169.254/latest/meta-data/public-ipv4"
DNS_NAME_PATTERN = re.compile(
    r"^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$"
)


class SigV4HttpError(Exception):
    """Raised when an AWS backend API call returns a non-2xx status code."""

    def __init__(self, status_code: int, error_code: str, message: str, raw_body: str = ""):
        super().__init__(f"HTTP {status_code} [{error_code}]: {message}")
        self.status_code = status_code
        self.error_code = error_code
        self.message = message
        self.raw_body = raw_body


def get_imds_public_ip(timeout: float = 2.0) -> Optional[str]:
    """Attempts to fetch the instance's public IPv4 address from EC2 IMDSv2."""
    try:
        token_req = urllib.request.Request(
            IMDS_TOKEN_URL,
            headers={"X-aws-ec2-metadata-token-ttl-seconds": "21600"},
            method="PUT",
        )
        with urllib.request.urlopen(token_req, timeout=timeout) as resp:
            token = resp.read().decode("utf-8").strip()

        ip_req = urllib.request.Request(
            IMDS_IPV4_URL,
            headers={"X-aws-ec2-metadata-token": token},
            method="GET",
        )
        with urllib.request.urlopen(ip_req, timeout=timeout) as resp:
            ip = resp.read().decode("utf-8").strip()
            if ip:
                return ip
    except Exception as exc:
        logger.debug("IMDSv2 discovery failed or not running on EC2: %s", exc)
    return None


class BackendApiClient:
    """Signs and sends requests to the ACC Shooter HTTP API using AWS SigV4."""

    def __init__(
        self,
        api_url: str = DEFAULT_API_URL,
        region: str = DEFAULT_REGION,
        credentials: Optional[Any] = None,
        session: Optional[botocore.session.Session] = None,
    ):
        self.api_url = api_url.rstrip("/")
        self.region = region
        self.session = session or botocore.session.get_session()
        self._custom_credentials = credentials

    def _get_credentials(self) -> Any:
        if self._custom_credentials:
            return self._custom_credentials
        creds = self.session.get_credentials()
        if creds is None:
            raise RuntimeError(
                "No AWS credentials found. Ensure an IAM role is attached to the EC2 instance "
                "or AWS credentials are configured in the environment."
            )
        return creds.get_frozen_credentials()

    def _request(
        self,
        method: str,
        path: str,
        body: Optional[Dict[str, Any]] = None,
        headers: Optional[Dict[str, str]] = None,
        timeout: float = 10.0,
    ) -> Dict[str, Any]:
        url = f"{self.api_url}{path}"
        payload_bytes = json.dumps(body).encode("utf-8") if body is not None else b""

        req_headers = {
            "Content-Type": "application/json",
            "Host": urllib.parse.urlparse(url).netloc,
        }
        if headers:
            req_headers.update(headers)

        aws_req = botocore.awsrequest.AWSRequest(
            method=method,
            url=url,
            data=payload_bytes,
            headers=req_headers,
        )
        creds = self._get_credentials()
        botocore.auth.SigV4Auth(creds, "execute-api", self.region).add_auth(aws_req)

        urllib_req = urllib.request.Request(
            aws_req.url,
            data=payload_bytes if payload_bytes else None,
            headers=dict(aws_req.headers),
            method=method,
        )

        try:
            with urllib.request.urlopen(urllib_req, timeout=timeout) as resp:
                resp_bytes = resp.read()
                return json.loads(resp_bytes.decode("utf-8")) if resp_bytes else {}
        except urllib.error.HTTPError as exc:
            err_body = exc.read().decode("utf-8")
            err_code = "http_error"
            err_msg = exc.reason
            try:
                parsed_err = json.loads(err_body)
                if isinstance(parsed_err, dict) and "error" in parsed_err:
                    err_info = parsed_err["error"]
                    if isinstance(err_info, dict):
                        err_code = err_info.get("code", err_code)
                        err_msg = err_info.get("message", err_msg)
                    elif isinstance(err_info, str):
                        err_msg = err_info
            except Exception:
                pass
            raise SigV4HttpError(exc.code, err_code, err_msg, err_body) from exc

    def register(
        self,
        public_address: str,
        game_port: int,
        capacity: int = DEFAULT_CAPACITY,
        protocol: str = "udp",
        idempotency_key: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Registers this server slot with the backend.

        Endpoint: POST /internal/servers/register
        """
        key = idempotency_key or uuid.uuid4().hex
        body = {
            "publicAddress": public_address,
            "gamePort": int(game_port),
            "capacity": int(capacity),
            "protocol": protocol,
        }
        headers = {"Idempotency-Key": key}
        return self._request("POST", "/internal/servers/register", body=body, headers=headers)

    def heartbeat(self, generation: str, status: str = "READY") -> Dict[str, Any]:
        """Sends periodic status/heartbeat for this server slot.

        Endpoint: POST /internal/servers/heartbeat
        """
        body = {
            "generation": generation,
            "status": status,
        }
        return self._request("POST", "/internal/servers/heartbeat", body=body)

    def consume_ticket(self, ticket: str, generation: str) -> Dict[str, Any]:
        """Consumes a join ticket and claims the slot.

        Endpoint: POST /internal/tickets/consume
        """
        body = {
            "ticket": ticket,
            "generation": generation,
        }
        return self._request("POST", "/internal/tickets/consume", body=body)

    def release_reservation(self, reservation_id: str, generation: str) -> Dict[str, Any]:
        """Releases a player reservation and decrements reservedSlots.

        Endpoint: POST /internal/servers/reservations/{reservationId}/release
        """
        body = {"generation": generation}
        return self._request(
            "POST",
            f"/internal/servers/reservations/{reservation_id}/release",
            body=body,
        )

    def submit_results(
        self,
        match_id: str,
        generation: str,
        winner_team: int,
        team_scores: List[int],
        duration_seconds: int,
        players: List[Dict[str, Any]],
    ) -> Dict[str, Any]:
        """Submits match results to the backend.

        Endpoint: POST /internal/matches/{matchId}/results
        """
        body = {
            "generation": generation,
            "winnerTeam": int(winner_team),
            "teamScores": [int(s) for s in team_scores],
            "durationSeconds": int(duration_seconds),
            "players": players,
        }
        return self._request("POST", f"/internal/matches/{match_id}/results", body=body)


class UnityServerSupervisor:
    """Launches, supervises, and terminates the headless ACCServer.x86_64 process."""

    def __init__(
        self,
        binary_path: str,
        game_port: int,
        capacity: int = DEFAULT_CAPACITY,
        match_id: Optional[str] = None,
        room_code: Optional[str] = None,
        kill_limit: Optional[int] = None,
        log_file: Optional[str] = None,
    ):
        self.binary_path = binary_path
        self.game_port = game_port
        self.capacity = capacity
        self.match_id = match_id
        self.room_code = room_code
        self.kill_limit = kill_limit
        self.log_file = log_file
        self.process: Optional[subprocess.Popen] = None

    def build_command(self) -> List[str]:
        cmd = [
            self.binary_path,
            "-batchmode",
            "-nographics",
            "-server",
            "-port",
            str(self.game_port),
            "-maxPlayers",
            str(self.capacity),
        ]
        if self.match_id:
            cmd.extend(["-matchId", self.match_id])
        if self.room_code:
            cmd.extend(["-roomCode", self.room_code])
        if self.kill_limit:
            cmd.extend(["-killLimit", str(self.kill_limit)])
        if self.log_file:
            cmd.extend(["-logFile", self.log_file])
        return cmd

    def start(self) -> bool:
        if self.process is not None and self.process.poll() is None:
            logger.warning("Server process is already running (PID %s).", self.process.pid)
            return True

        cmd = self.build_command()
        logger.info("Launching server: %s", " ".join(cmd))
        try:
            self.process = subprocess.Popen(
                cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            logger.info("Server started successfully with PID %s.", self.process.pid)
            return True
        except Exception as exc:
            logger.error("Failed to start server binary '%s': %s", self.binary_path, exc)
            return False

    def is_alive(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def poll(self) -> Optional[int]:
        if self.process is None:
            return None
        return self.process.poll()

    def stop(self, timeout: float = 5.0) -> None:
        if self.process is None:
            return
        if self.process.poll() is not None:
            self.process = None
            return

        logger.info("Stopping server process (PID %s)...", self.process.pid)
        try:
            self.process.terminate()
            self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            logger.warning("Server process did not exit in %s s; sending SIGKILL.", timeout)
            self.process.kill()
            self.process.wait(timeout=2.0)
        except Exception as exc:
            logger.warning("Error stopping server process: %s", exc)
        finally:
            self.process = None


class LoopbackRequestHandler(http.server.BaseHTTPRequestHandler):
    """Handles loopback HTTP requests from the local Unity server process."""

    server: LoopbackServer  # Type annotation for reference

    def log_message(self, format: str, *args: Any) -> None:
        logger.debug("[Loopback] %s - - [%s] %s", self.client_address[0], self.log_date_time_string(), format % args)

    def _is_loopback(self) -> bool:
        ip = self.client_address[0]
        try:
            addr = ipaddress.ip_address(ip)
            return addr.is_loopback
        except ValueError:
            return ip in ("127.0.0.1", "::1", "localhost")

    def _send_json(self, status: int, data: Dict[str, Any]) -> None:
        body = json.dumps(data).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self) -> Optional[Dict[str, Any]]:
        length_header = self.headers.get("Content-Length")
        if not length_header:
            return None
        try:
            length = int(length_header)
            raw = self.rfile.read(length)
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return None

    def do_GET(self) -> None:
        if not self._is_loopback():
            self._send_json(403, {"error": "Loopback access only"})
            return

        if self.path == "/status" or self.path == "/health":
            agent = self.server.agent
            self._send_json(
                200,
                {
                    "status": agent.status,
                    "serverId": agent.server_id,
                    "serverGeneration": agent.generation,
                    "matchId": agent.match_id,
                    "gamePort": agent.game_port,
                    "capacity": agent.capacity,
                    "processRunning": agent.supervisor.is_alive() if agent.supervisor else False,
                    "activeReservations": len(agent.client_reservations),
                },
            )
            return

        self._send_json(404, {"error": "route_not_found"})

    def do_POST(self) -> None:
        if not self._is_loopback():
            self._send_json(403, {"error": "Loopback access only"})
            return

        agent = self.server.agent

        if self.path == "/validate":
            body = self._read_json()
            if not body or not isinstance(body.get("ticket"), str):
                self._send_json(400, {"valid": False, "error": "invalid_ticket_payload"})
                return

            ticket = body["ticket"].strip()
            client_id = body.get("clientId")
            if agent.local_test:
                # Generate deterministic dummy response
                reservation_id = f"res-{uuid.uuid4().hex[:8]}"
                match_id = "local-test-match"
                room_code = "local-test-room"
                if client_id is not None:
                    agent.client_reservations[str(client_id)] = reservation_id
                self._send_json(
                    200,
                    {
                        "valid": True,
                        "reservationId": reservation_id,
                        "matchId": match_id,
                        "roomCode": room_code,
                    },
                )
                return
            try:
                res = agent.api.consume_ticket(ticket=ticket, generation=agent.generation)
                reservation_id = res.get("reservationId")
                if reservation_id and client_id is not None:
                    agent.client_reservations[str(client_id)] = reservation_id
                self._send_json(
                    200,
                    {
                        "valid": True,
                        "reservationId": reservation_id,
                        "matchId": res.get("matchId"),
                        "roomCode": res.get("roomCode"),
                    },
                )
            except SigV4HttpError as exc:
                logger.warning("Ticket validation failed: %s", exc)
                self._send_json(200, {"valid": False, "error": exc.error_code, "message": exc.message})
            except Exception as exc:
                logger.error("Unexpected error validating ticket: %s", exc)
                self._send_json(500, {"valid": False, "error": "internal_error", "message": str(exc)})
            return

        if self.path == "/result":
            body = self._read_json()
            if not body:
                self._send_json(400, {"accepted": False, "error": "invalid_result_payload"})
                return

            winner_team = body.get("winnerTeam")
            team_scores = body.get("teamScores")
            duration_seconds = body.get("durationSeconds", 600)
            players = body.get("players", [])

            if winner_team not in (0, 1) or not isinstance(team_scores, list) or len(team_scores) != 2:
                self._send_json(400, {"accepted": False, "error": "invalid_result_fields"})
                return

            if agent.local_test:
                # In local test mode we pretend the submission succeeded
                self._send_json(
                    200,
                    {
                        "accepted": True,
                        "duplicate": False,
                        "matchId": agent.match_id or "local-test-match",
                    },
                )
                return
            try:
                res = agent.api.submit_results(
                    match_id=agent.match_id,
                    generation=agent.generation,
                    winner_team=winner_team,
                    team_scores=team_scores,
                    duration_seconds=duration_seconds,
                    players=players,
                )
                agent.status = "DRAINING"
                self._send_json(200, {"accepted": True, "duplicate": res.get("duplicate", False), "matchId": agent.match_id})
            except SigV4HttpError as exc:
                logger.warning("Result submission rejected by backend: %s", exc)
                self._send_json(exc.status_code, {"accepted": False, "error": exc.error_code, "message": exc.message})
            except Exception as exc:
                logger.error("Unexpected error submitting match results: %s", exc)
                self._send_json(500, {"accepted": False, "error": "internal_error", "message": str(exc)})
            return

        if self.path == "/release":
            body = self._read_json() or {}
            client_id = body.get("clientId")
            reservation_id = body.get("reservationId")
            if not reservation_id and client_id is not None:
                reservation_id = agent.client_reservations.pop(str(client_id), None)

            if not reservation_id:
                self._send_json(400, {"released": False, "error": "reservation_id_required"})
                return

            if agent.local_test:
                # In local-test mode we simply acknowledge release without backend call
                self._send_json(200, {"released": True, "reservationId": reservation_id})
                return

            try:
                res = agent.api.release_reservation(reservation_id=reservation_id, generation=agent.generation)
                self._send_json(200, {"released": res.get("released", True), "reservationId": reservation_id})
            except SigV4HttpError as exc:
                logger.warning("Reservation release failed: %s", exc)
                self._send_json(exc.status_code, {"released": False, "error": exc.error_code})
            except Exception as exc:
                logger.error("Error releasing reservation: %s", exc)
                self._send_json(500, {"released": False, "error": "internal_error"})
            return

        self._send_json(404, {"error": "route_not_found"})


class LoopbackServer(http.server.HTTPServer):
    """Threaded or standard loopback HTTP server."""

    def __init__(self, server_address: Tuple[str, int], agent: GameServerAgent):
        super().__init__(server_address, LoopbackRequestHandler)
        self.agent = agent


class GameServerAgent:
    """Coordinates registration, supervisor, heartbeats, and loopback endpoints."""

    def __init__(
        self,
        api_url: str = DEFAULT_API_URL,
        region: str = DEFAULT_REGION,
        public_address: Optional[str] = None,
        game_port: int = DEFAULT_GAME_PORT,
        capacity: int = DEFAULT_CAPACITY,
        server_binary: Optional[str] = None,
        loopback_port: int = DEFAULT_LOOPBACK_PORT,
        heartbeat_interval: float = DEFAULT_HEARTBEAT_INTERVAL,
        log_file: Optional[str] = None,
        api_client: Optional[BackendApiClient] = None,
        local_test: bool = False,
    ):
        self.api_url = api_url
        self.region = region
        self.public_address = public_address
        self.game_port = game_port
        self.capacity = capacity
        self.server_binary = server_binary
        self.loopback_port = loopback_port
        self.heartbeat_interval = heartbeat_interval
        self.log_file = log_file

        self.local_test = local_test

        self.api = api_client or BackendApiClient(api_url=api_url, region=region)
        self.supervisor: Optional[UnityServerSupervisor] = None
        self.loopback_server: Optional[LoopbackServer] = None
        self.loopback_thread: Optional[threading.Thread] = None

        self.server_id: Optional[str] = None
        self.generation: Optional[str] = None
        self.match_id: Optional[str] = None
        self.status = "STARTING"
        self.running = False
        self._stop_event = threading.Event()
        self.process_exit_code: Optional[int] = None
        self.client_reservations: Dict[str, str] = {}

    def resolve_public_address(self) -> str:
        # Resolve public address (skip on local test)
        if self.local_test:
            # Use a harmless placeholder address for local testing
            return "127.0.0.1"
        # Resolve public address (skip on local test)
        if self.local_test:
            # Use loopback address for local testing
            return "127.0.0.1"
        if self.local_test:
            # Use loopback address for local testing
            return "127.0.0.1"
        if self.public_address:
            return self.public_address
        addr = get_imds_public_ip()
        if addr:
            return addr
        # Fallback for development/testing if no IP was provided
        return "52.95.110.1"



    def start(self) -> bool:
        """Starts registration, server supervisor, loopback server, and heartbeat loop.
        In local‑test mode we skip all AWS interactions and use placeholder identifiers.
        """
        if self.local_test:
            # Skip registration; use dummy IDs
            self.server_id = "local-test-server"
            self.generation = "local-test-gen"
            self.match_id = "local-test-match"
            self.status = "STARTING"
            logger.info("[local‑test] Skipping registration, using dummy server identifiers.")
        else:
            address = self.resolve_public_address()
            logger.info("Registering game server at %s:%s with capacity %s...", address, self.game_port, self.capacity)
            try:
                reg_info = self.api.register(
                    public_address=address,
                    game_port=self.game_port,
                    capacity=self.capacity,
                    protocol="udp",
                )
            except Exception as exc:
                logger.error("Registration failed: %s", exc)
                return False
            self.server_id = reg_info.get("serverId")
            self.generation = reg_info.get("serverGeneration")
            self.match_id = reg_info.get("matchId")
            self.status = reg_info.get("status", "STARTING")
            logger.info(
                "Registered successfully. serverId=%s, generation=%s, matchId=%s",
                self.server_id,
                self.generation,
                self.match_id,
            )
        # Start supervisor if binary path is provided
        if self.server_binary:
            self.supervisor = UnityServerSupervisor(
                binary_path=self.server_binary,
                game_port=self.game_port,
                capacity=self.capacity,
                match_id=self.match_id,
                log_file=self.log_file,
            )
            if not self.supervisor.start():
                logger.error("Failed to launch Unity game server.")
                self._mark_stopped()
                return False
        # Transition to READY (skip in local test)
        if not self.local_test:
            try:
                hb = self.api.heartbeat(generation=self.generation, status="READY")
                self.status = hb.get("status", "READY")
                logger.info("Server status transitioned to READY.")
            except Exception as exc:
                logger.error("Failed initial READY heartbeat: %s", exc)
                if self.supervisor:
                    self.supervisor.stop()
                self._mark_stopped()
                return False
        # Start Loopback Server (if port > 0)
        if self.loopback_port > 0:
            try:
                self.loopback_server = LoopbackServer(("127.0.0.1", self.loopback_port), self)
                self.loopback_thread = threading.Thread(
                    target=self.loopback_server.serve_forever,
                    daemon=True,
                    name="LoopbackServer",
                )
                self.loopback_thread.start()
                logger.info("Loopback endpoint listening on 127.0.0.1:%s", self.loopback_port)
            except Exception as exc:
                logger.error("Failed to bind loopback server on 127.0.0.1:%s: %s", self.loopback_port, exc)
                self.stop()
                return False
        self.running = True
        return True

    def heartbeat_cycle(self) -> bool:
        """Executes a single heartbeat cycle, checking process health."""
        if not self.running or not self.generation:
            return False

        # Check process liveness if supervisor exists
        if self.supervisor:
            poll_res = self.supervisor.poll()
            if poll_res is not None:
                logger.warning("Unity server process exited with code %s.", poll_res)
                self.status = "DRAINING"
                self.process_exit_code = poll_res
                self._stop_event.set()

        try:
            hb = self.api.heartbeat(generation=self.generation, status=self.status)
            self.status = hb.get("status", self.status)
            logger.debug("Heartbeat OK: status=%s, expiresAt=%s", self.status, hb.get("heartbeatExpiresAt"))
            return self.process_exit_code is None
        except Exception as exc:
            logger.warning("Heartbeat update failed: %s", exc)
            return False

    def run_heartbeat_loop(self) -> None:
        """Runs the periodic heartbeat loop until stopped."""
        logger.info("Starting periodic heartbeat loop (interval: %s s)...", self.heartbeat_interval)
        while not self._stop_event.is_set():
            if self._stop_event.wait(timeout=self.heartbeat_interval):
                break
            self.heartbeat_cycle()

    def stop(self) -> None:
        """Gracefully halts the agent and stops the dedicated server."""
        if not self.running and self._stop_event.is_set():
            return
        logger.info("Stopping GameServerAgent...")
        self.running = False
        self._stop_event.set()

        # Stop supervisor
        if self.supervisor:
            self.supervisor.stop()

        self._mark_stopped()

        # Shutdown loopback server
        if self.loopback_server:
            try:
                self.loopback_server.shutdown()
                self.loopback_server.server_close()
            except Exception as exc:
                logger.debug("Error shutting down loopback server: %s", exc)

    def _mark_stopped(self) -> None:
        if not self.generation or self.local_test or self.status == "STOPPED":
            return
        try:
            self.api.heartbeat(generation=self.generation, status="STOPPED")
            self.status = "STOPPED"
            logger.info("Server marked STOPPED in backend.")
        except Exception as exc:
            logger.warning("Could not mark server STOPPED: %s", exc)


def parse_args(argv: Optional[List[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="ACC Shooter EC2 Dedicated Game Server Agent")
    parser.add_argument("--api-url", default=DEFAULT_API_URL, help="Backend API URL (default: production endpoint)")
    parser.add_argument("--local-test", action="store_true", help="Run agent in local‑only test mode (skip all AWS calls).")
    parser.add_argument("--region", default=os.getenv("AWS_REGION", DEFAULT_REGION), help="AWS Region")
    parser.add_argument("--public-address", default=os.getenv("PUBLIC_ADDRESS"), help="Public IP or DNS name")
    parser.add_argument("--game-port", type=int, default=int(os.getenv("GAME_PORT", DEFAULT_GAME_PORT)), help="Game port")
    parser.add_argument("--capacity", type=int, default=int(os.getenv("CAPACITY", DEFAULT_CAPACITY)), help="Server player capacity")
    parser.add_argument("--server-binary", default=os.getenv("SERVER_BINARY"), help="Path to ACCServer.x86_64")
    parser.add_argument("--loopback-port", type=int, default=int(os.getenv("AGENT_PORT", DEFAULT_LOOPBACK_PORT)), help="Agent local port")
    parser.add_argument("--heartbeat-interval", type=float, default=float(os.getenv("HEARTBEAT_INTERVAL", DEFAULT_HEARTBEAT_INTERVAL)), help="Heartbeat interval in seconds")
    parser.add_argument("--log-file", default=os.getenv("SERVER_LOG"), help="Server log file path")
    return parser.parse_args(argv)


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] [%(name)s] %(message)s",
    )
    args = parse_args()
    agent = GameServerAgent(
        api_url=args.api_url,
        region=args.region,
        public_address=args.public_address,
        game_port=args.game_port,
        capacity=args.capacity,
        server_binary=args.server_binary,
        loopback_port=args.loopback_port,
        heartbeat_interval=args.heartbeat_interval,
        log_file=args.log_file,
        local_test=args.local_test,
    )

    def _handle_signal(signum: int, frame: Any) -> None:
        logger.info("Signal %s received, initiating shutdown...", signum)
        agent.stop()

    signal.signal(signal.SIGINT, _handle_signal)
    signal.signal(signal.SIGTERM, _handle_signal)

    if not agent.start():
        logger.error("Agent startup failed.")
        return 1

    # Run the appropriate main loop depending on mode
    try:
        if not agent.local_test:
            agent.run_heartbeat_loop()
            if agent.process_exit_code is not None:
                logger.error("Unity server exited; allowing systemd to restart the agent.")
                return 1
        else:
            # In local‑test mode wait until the stop event is signaled.
            while not agent._stop_event.is_set():
                agent._stop_event.wait(timeout=1.0)
    except KeyboardInterrupt:
        pass
    finally:
        agent.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
