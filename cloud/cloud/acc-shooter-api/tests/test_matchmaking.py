import hashlib
import sys
import threading
from pathlib import Path
from types import SimpleNamespace

import pytest
from boto3.dynamodb.types import TypeDeserializer
from botocore.exceptions import ClientError

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "hello_world"))

import matchmaking
from tests.test_app import FakeClient, FakeTable


def fake_tables():
    client = FakeClient()
    rooms = FakeTable("Rooms", client, "roomCode")
    servers = FakeTable("Servers", client, "serverId")
    tickets = FakeTable("JoinTickets", client, "ticketId")
    reservations = FakeTable("Reservations", client, "reservationId")
    return SimpleNamespace(
        client=client,
        rooms=rooms,
        servers=servers,
        tickets=tickets,
        reservations=reservations,
    )


def ready_server():
    return {
        "serverId": "server-1",
        "status": "READY",
        "heartbeatExpiresAt": 5000,
        "reservedSlots": 0,
        "capacity": 10,
        "publicAddress": "203.0.113.10",
        "gamePort": 7777,
        "matchId": "match-1",
        "serverGeneration": "11111111-1111-4111-8111-111111111111",
    }


def reserve(tables, server=None, key="reservation-key-012345", hash_value="payload-a"):
    return matchmaking.reserve_server_and_issue_ticket(
        tables.client,
        tables.rooms,
        tables.servers,
        tables.tickets,
        tables.reservations,
        ready_server() if server is None else server,
        idempotency_key=key,
        request_hash=hash_value,
        now=1000,
    )


def test_reservation_is_atomic_and_idempotent():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()
    tables.rooms.items["ROOM12"] = {
        "roomCode": "ROOM12",
        "status": "open",
        "expiresAt": 5000,
        "playerCount": 1,
        "maxPlayers": 10,
    }

    first = reserve(tables)
    second = reserve(tables)

    assert first == second
    assert first["endpoint"] == {"host": "203.0.113.10", "port": 7777, "protocol": "udp"}
    assert tables.servers.items["server-1"]["reservedSlots"] == 1
    assert tables.rooms.items["ROOM12"]["playerCount"] == 1
    assert len(tables.tickets.items) == 1
    assert len(tables.reservations.items) == 1
    assert all(ticket["status"] == "ISSUED" for ticket in tables.tickets.items.values())
    assert tables.client.transactions == 1


def test_reservation_transaction_contains_capacity_heartbeat_and_ticket_conditions():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()

    result = reserve(tables)
    assert result["ticket"]
    assert tables.servers.items["server-1"]["reservedSlots"] == 1
    ticket = next(iter(tables.tickets.items.values()))
    assert ticket["ticketId"] == hashlib.sha256(result["ticket"].encode("ascii")).hexdigest()
    assert "publicAddress" not in ticket
    operations = tables.client.transaction_requests[0]
    server_update = operations[0]["Update"]
    ticket_put = operations[1]["Put"]
    assert "heartbeatExpiresAt > :now" in server_update["ConditionExpression"]
    assert "attribute_type(reservedSlots, :number_type)" in server_update["ConditionExpression"]
    assert "reservedSlots = :current_slots" in server_update["ConditionExpression"]
    assert "reservedSlots + " not in server_update["UpdateExpression"]
    assert server_update["UpdateExpression"] == "SET reservedSlots = :new_slots"
    assert server_update["ExpressionAttributeValues"][":number_type"] == {"S": "N"}
    assert server_update["ExpressionAttributeValues"][":current_slots"] == {"N": "0"}
    assert server_update["ExpressionAttributeValues"][":new_slots"] == {"N": "1"}
    assert "reservedSlots < capacity" in server_update["ConditionExpression"]
    assert "#status = :ready" in server_update["ConditionExpression"]
    assert ticket_put["ConditionExpression"] == "attribute_not_exists(ticketId)"


def test_non_numeric_slot_counter_is_rejected_by_reservation_transaction_guard():
    tables = fake_tables()
    candidate = ready_server()
    persisted = ready_server()
    persisted["reservedSlots"] = {"corrupt": 0}
    tables.servers.items["server-1"] = persisted

    with pytest.raises(matchmaking.ReservationUnavailable):
        reserve(tables, server=candidate)

    assert len(tables.client.transaction_requests) == 1
    update = tables.client.transaction_requests[0][0]["Update"]
    assert update["UpdateExpression"] == "SET reservedSlots = :new_slots"
    assert "reservedSlots + " not in update["UpdateExpression"]
    assert "attribute_type(reservedSlots, :number_type)" in update["ConditionExpression"]
    assert update["ExpressionAttributeValues"][":number_type"] == {"S": "N"}
    assert tables.servers.items["server-1"]["reservedSlots"] == {"corrupt": 0}
    assert tables.tickets.items == {}
    assert tables.reservations.items == {}


def test_release_does_not_decrement_non_numeric_slot_counter():
    tables = fake_tables()
    server = ready_server()
    server["reservedSlots"] = {"corrupt": 0}
    tables.servers.items["server-1"] = server
    tables.reservations.items["reservation-1"] = {
        "reservationId": "reservation-1",
        "serverId": "server-1",
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "status": "RESERVED",
        "ticketExpiresAt": 2000,
    }

    with pytest.raises(matchmaking.ReservationUnavailable):
        matchmaking.release_reservation(
            tables.client,
            tables.reservations,
            tables.servers,
            tables.rooms,
            "reservation-1",
            "server-1",
            server["serverGeneration"],
            1000,
        )

    assert tables.reservations.items["reservation-1"]["status"] == "RESERVED"
    assert tables.servers.items["server-1"]["reservedSlots"] == {"corrupt": 0}
    decrement = tables.client.transaction_requests[0][1]["Update"]
    assert decrement["UpdateExpression"] == "SET reservedSlots = :new_slots"
    assert "reservedSlots - " not in decrement["UpdateExpression"]
    assert "attribute_type(reservedSlots, :number_type)" in decrement["ConditionExpression"]


def test_arithmetic_validation_error_is_mapped_to_reservation_unavailable():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()

    def explode(TransactItems):
        raise ClientError(
            {
                "Error": {
                    "Code": "ValidationException",
                    "Message": (
                        "Invalid UpdateExpression: Incorrect operand type for operator "
                        "or function; operator or function: +, operand type: M"
                    ),
                }
            },
            "TransactWriteItems",
        )

    tables.client.transact_write_items = explode
    with pytest.raises(matchmaking.ReservationUnavailable, match="slot counter"):
        reserve(tables)

    assert tables.tickets.items == {}
    assert tables.reservations.items == {}


def test_distinct_requests_cannot_exceed_server_capacity():
    tables = fake_tables()
    server = ready_server()
    server["capacity"] = 2
    tables.servers.items["server-1"] = server
    successes = []
    failures = []

    def run(index):
        key = f"capacity-request-{index:04d}-xxxxxxxx"
        try:
            successes.append(reserve(tables, key=key, hash_value=key))
        except matchmaking.ReservationUnavailable as exc:
            failures.append(exc)

    threads = [threading.Thread(target=run, args=(index,)) for index in range(6)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert len(successes) == 2
    assert len(failures) == 4
    assert tables.servers.items["server-1"]["reservedSlots"] == 2
    assert len(tables.tickets.items) == len(tables.reservations.items) == 2


def test_no_capacity_means_no_ticket_or_idempotency_reservation():
    tables = fake_tables()
    server = ready_server()
    server["reservedSlots"] = server["capacity"]
    tables.servers.items["server-1"] = server

    with pytest.raises(matchmaking.ReservationUnavailable):
        reserve(tables, server=server)

    assert tables.servers.items["server-1"]["reservedSlots"] == 10
    assert tables.tickets.items == {}
    assert tables.rooms.items == {}


def test_room_capacity_failure_rolls_back_server_and_ticket():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()
    tables.rooms.items["ROOM12"] = {
        "roomCode": "ROOM12",
        "status": "open",
        "serverId": "server-1",
        "expiresAt": 5000,
        "playerCount": 10,
        "maxPlayers": 10,
    }

    with pytest.raises(matchmaking.ReservationUnavailable):
        matchmaking.reserve_server_and_issue_ticket(
            tables.client,
            tables.rooms,
            tables.servers,
            tables.tickets,
            tables.reservations,
            ready_server(),
            idempotency_key="reservation-key-012345",
            request_hash="payload-a",
            now=1000,
            room_code="ROOM12",
        )

    room_update = tables.client.transaction_requests[0][3]["Update"]
    assert ":open" in room_update["ConditionExpression"]
    assert "expiresAt > :now" in room_update["ConditionExpression"]
    assert "playerCount < maxPlayers" in room_update["ConditionExpression"]
    assert tables.servers.items["server-1"]["reservedSlots"] == 0
    assert tables.tickets.items == {}
    assert tables.reservations.items == {}
    assert "REQUEST#" not in tables.rooms.items


def test_same_reservation_key_with_different_payload_is_rejected():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()
    reserve(tables)

    with pytest.raises(matchmaking.IdempotencyConflict):
        reserve(tables, hash_value="different-payload")

    assert tables.servers.items["server-1"]["reservedSlots"] == 1
    assert len(tables.tickets.items) == 1


def test_consumed_or_expired_assignment_key_does_not_replay_a_dead_ticket():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()
    assignment = reserve(tables)
    ticket_id = hashlib.sha256(assignment["ticket"].encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id]["status"] = "CONSUMED"

    with pytest.raises(matchmaking.IdempotencyConflict):
        reserve(tables)
    assert tables.servers.items["server-1"]["reservedSlots"] == 1
    assert len(tables.tickets.items) == 1


def test_concurrent_reservation_retries_share_one_ticket_and_slot():
    tables = fake_tables()
    tables.servers.items["server-1"] = ready_server()
    results = []
    errors = []

    def run():
        try:
            results.append(reserve(tables))
        except Exception as exc:
            errors.append(exc)

    threads = [threading.Thread(target=run) for _ in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert errors == []
    assert len({item["ticket"] for item in results}) == 1
    assert tables.servers.items["server-1"]["reservedSlots"] == 1
    assert len(tables.tickets.items) == 1
    assert tables.client.transactions >= 1


def test_two_requests_cannot_reserve_the_last_server_slot():
    tables = fake_tables()
    server = ready_server()
    server["reservedSlots"] = 9
    tables.servers.items["server-1"] = server
    successes = []
    failures = []

    def run(key):
        try:
            successes.append(reserve(tables, server=server, key=key, hash_value=key))
        except matchmaking.ReservationUnavailable as exc:
            failures.append(exc)

    threads = [
        threading.Thread(target=run, args=("last-slot-request-0001",)),
        threading.Thread(target=run, args=("last-slot-request-0002",)),
    ]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert len(successes) == len(failures) == 1
    assert tables.servers.items["server-1"]["reservedSlots"] == 10
    assert len(tables.tickets.items) == len(tables.reservations.items) == 1


def test_expired_unconsumed_reservation_releases_its_slot():
    tables = fake_tables()
    server = ready_server()
    server["reservedSlots"] = 1
    tables.servers.items["server-1"] = server
    tables.reservations.items["expired-reservation-0001"] = {
        "reservationId": "expired-reservation-0001",
        "serverId": "server-1",
        "serverGeneration": server["serverGeneration"],
        "status": "RESERVED",
        "ticketExpiresAt": 1000,
    }

    released = matchmaking.reap_expired_reservations(
        tables.reservations,
        tables.client,
        tables.servers,
        tables.rooms,
        "server-1",
        server["serverGeneration"],
        1000,
    )

    assert released == 1
    assert tables.servers.items["server-1"]["reservedSlots"] == 0
    assert tables.reservations.items["expired-reservation-0001"]["status"] == "RELEASED"


def test_ticket_consumption_is_single_use_and_server_bound():
    tables = fake_tables()
    token = "ticket-token-value"
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id] = {
        "ticketId": ticket_id,
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": "server-1",
        "serverGeneration": ready_server()["serverGeneration"],
        "status": "ISSUED",
        "expiresAt": 2000,
    }
    tables.reservations.items["abcdefghijklmnopqrstuvwx"] = {
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": "server-1",
        "serverGeneration": ready_server()["serverGeneration"],
        "status": "RESERVED",
        "ticketExpiresAt": 2000,
    }
    results = []
    barrier = threading.Barrier(2)

    def consume():
        barrier.wait()
        results.append(
            matchmaking.consume_join_ticket(
                tables.client,
                tables.tickets,
                tables.reservations,
                token,
                "server-1",
                ready_server()["serverGeneration"],
                1000,
            )
        )

    threads = [threading.Thread(target=consume) for _ in range(2)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert sum(result is not None for result in results) == 1
    assert tables.tickets.items[ticket_id]["status"] == "CONSUMED"
    assert tables.reservations.items["abcdefghijklmnopqrstuvwx"]["status"] == "ACTIVE"
    ticket_update, reservation_update = [
        action["Update"] for action in tables.client.transaction_requests[0]
    ]
    assert "expiresAt > :now" in ticket_update["ConditionExpression"]
    assert "#status = :reserved" in reservation_update["ConditionExpression"]


def test_expired_or_wrong_server_ticket_cannot_be_consumed():
    tables = fake_tables()
    token = "ticket-token-value"
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id] = {
        "ticketId": ticket_id,
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": "server-1",
        "serverGeneration": ready_server()["serverGeneration"],
        "status": "ISSUED",
        "expiresAt": 2000,
    }
    tables.reservations.items["abcdefghijklmnopqrstuvwx"] = {
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": "server-1",
        "serverGeneration": ready_server()["serverGeneration"],
        "status": "RESERVED",
        "ticketExpiresAt": 2000,
    }

    assert matchmaking.consume_join_ticket(
        tables.client,
        tables.tickets,
        tables.reservations,
        token,
        "other-server",
        ready_server()["serverGeneration"],
        1000,
    ) is None
    assert matchmaking.consume_join_ticket(
        tables.client,
        tables.tickets,
        tables.reservations,
        token,
        "server-1",
        ready_server()["serverGeneration"],
        2000,
    ) is None
    assert tables.tickets.items[ticket_id]["status"] == "ISSUED"
