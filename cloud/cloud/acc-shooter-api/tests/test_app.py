import json
import sys
import threading
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace

import pytest
from boto3.dynamodb.types import TypeDeserializer
from botocore.exceptions import ClientError

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "hello_world"))

import app


class FakeClient:
    def __init__(self):
        self.tables = {}
        self.lock = threading.Lock()
        self.transactions = 0
        self.transaction_requests = []

    def transact_write_items(self, TransactItems):
        with self.lock:
            self.transactions += 1
            self.transaction_requests.append(TransactItems)
            pending_puts = []
            pending_updates = []
            deserializer = TypeDeserializer()
            for action in TransactItems:
                if "Put" in action:
                    put = action["Put"]
                    item = {
                        key: deserializer.deserialize(value)
                        for key, value in put["Item"].items()
                    }
                    table = self.tables[put["TableName"]]
                    primary_key = item[table.key_name]
                    if primary_key in table.items:
                        raise self.cancelled()
                    pending_puts.append((table, item))
                    continue

                update = action["Update"]
                table = self.tables[update["TableName"]]
                key = {
                    name: deserializer.deserialize(value)
                    for name, value in update["Key"].items()
                }
                existing = table.items.get(next(iter(key.values())))
                values = {
                    name: deserializer.deserialize(value)
                    for name, value in update["ExpressionAttributeValues"].items()
                }
                if table.name == "Servers":
                    expression = update["UpdateExpression"]
                    valid = existing is not None
                    reserved_slots = existing.get("reservedSlots") if existing else None
                    numeric_slots = isinstance(reserved_slots, (int, Decimal)) and not isinstance(
                        reserved_slots, bool
                    )
                    if "+" in expression or (
                        "-" in expression and ":new_slots" not in values
                    ):
                        if existing is not None and not numeric_slots:
                            raise self.operand_type_error("+" if "+" in expression else "-")
                        condition = update["ConditionExpression"]
                        valid = valid and numeric_slots and (
                            "attribute_type(reservedSlots, :number_type)" in condition
                            and values.get(":number_type") == "N"
                            and existing.get("status") == "READY"
                            and existing.get("heartbeatExpiresAt", 0) > values[":now"]
                            and reserved_slots < existing.get("capacity", 0)
                            and existing.get("serverGeneration") == values.get(":generation")
                            and existing.get("matchId") == values.get(":match")
                            and existing.get("publicAddress")
                            and existing.get("gamePort")
                        )
                    elif ":new_slots" in values:
                        condition = update["ConditionExpression"]
                        valid = valid and numeric_slots and (
                            "attribute_type(reservedSlots, :number_type)" in condition
                            and values.get(":number_type") == "N"
                            and reserved_slots == values.get(":current_slots")
                            and existing.get("serverGeneration") == values.get(":generation")
                        )
                        if ":ready" in values:
                            valid = valid and (
                                existing.get("status") == "READY"
                                and existing.get("heartbeatExpiresAt", 0) > values[":now"]
                                and reserved_slots < existing.get("capacity", 0)
                                and existing.get("matchId") == values.get(":match")
                                and existing.get("publicAddress")
                                and existing.get("gamePort")
                            )
                        else:
                            valid = valid and reserved_slots >= values.get(":one", 0)
                    else:
                        valid = valid and (
                            existing.get("serverGeneration") == values.get(":generation")
                            and existing.get("matchId") == values.get(":match")
                            and existing.get("status") in {"READY", "DRAINING"}
                        )
                    if not valid:
                        raise self.cancelled()
                    pending_updates.append(
                        (table, existing, update)
                    )
                elif table.name == "Rooms":
                    status = existing.get("status") if existing else None
                    allocation = "ADD playerCount" in update["UpdateExpression"]
                    if allocation:
                        valid = (
                            existing is not None
                            and status in {"open", "waiting_for_server"}
                            and existing.get("expiresAt", 0) > values[":now"]
                            and existing.get("playerCount", 0) < existing.get("maxPlayers", 0)
                            and not (
                                status == "waiting_for_server"
                                and existing.get("serverId") is not None
                            )
                            and not (
                                status == "open"
                                and (
                                    existing.get("serverId") != values.get(":server")
                                    or existing.get("matchId") != values.get(":match")
                                    or existing.get("serverGeneration") != values.get(":generation")
                                )
                            )
                        )
                    else:
                        valid = (
                            existing is not None
                            and existing.get("status") == "open"
                            and existing.get("serverId") == values.get(":server")
                            and existing.get("matchId") == values.get(":match")
                            and existing.get("serverGeneration") == values.get(":generation")
                            and (
                                existing.get("playerCount") == values.get(":one")
                                or existing.get("playerCount", 0) > values.get(":one", 0)
                            )
                        )
                    if not valid:
                        raise self.cancelled()
                    pending_updates.append(
                        (table, existing, update)
                    )
                elif table.name == "Reservations":
                    target_status = values.get(":active") and ":released" not in values
                    if (
                        existing is None
                        or existing.get("status") not in (
                            {"RESERVED"} if target_status else {"RESERVED", "ACTIVE"}
                        )
                        or existing.get("serverId") != values.get(":server")
                        or existing.get("serverGeneration") != values.get(":generation")
                    ):
                        raise self.cancelled()
                    pending_updates.append((table, existing, update))
                elif table.name == "JoinTickets":
                    if (
                        existing is None
                        or existing.get("status") != values.get(":issued")
                        or existing.get("serverId") != values.get(":server")
                        or existing.get("serverGeneration") != values.get(":generation")
                        or existing.get("expiresAt", 0) <= values.get(":now", 0)
                    ):
                        raise self.cancelled()
                    pending_updates.append((table, existing, update))
            for table, item, update in pending_updates:
                values = {
                    name: deserializer.deserialize(value)
                    for name, value in update["ExpressionAttributeValues"].items()
                }
                if table.name == "Servers":
                    expression = update["UpdateExpression"]
                    if ":new_slots" in values:
                        new_slots = values[":new_slots"]
                        if isinstance(new_slots, Decimal) and new_slots == new_slots.to_integral_value():
                            new_slots = int(new_slots)
                        item["reservedSlots"] = new_slots
                    elif "+" in expression:
                        item["reservedSlots"] = item.get("reservedSlots", 0) + values[":one"]
                    elif "-" in expression:
                        item["reservedSlots"] = item.get("reservedSlots", 0) - values[":one"]
                    else:
                        item["status"] = values[":draining"]
                elif table.name == "Rooms":
                    if "ADD playerCount" in update["UpdateExpression"]:
                        item["playerCount"] = item.get("playerCount", 0) + values[":one"]
                        item["status"] = values[":open"]
                        item["serverId"] = values[":server"]
                        item["matchId"] = values[":match"]
                        item["serverGeneration"] = values[":generation"]
                    elif ":zero" in values:
                        item["playerCount"] = values[":zero"]
                        item["status"] = values[":waiting"]
                        item.pop("serverId", None)
                        item.pop("matchId", None)
                        item.pop("serverGeneration", None)
                    else:
                        item["playerCount"] = item.get("playerCount", 0) - values[":one"]
                elif table.name == "Reservations":
                    if ":released" in values:
                        item["status"] = values[":released"]
                        item["releasedAt"] = values[":now"]
                    else:
                        item["status"] = values[":active"]
                elif table.name == "JoinTickets":
                    item["status"] = values[":consumed"]
                    item["consumedAt"] = values[":now"]
            for table, item in pending_puts:
                table.items[item[table.key_name]] = item

    @staticmethod
    def cancelled():
        return ClientError(
            {"Error": {"Code": "TransactionCanceledException", "Message": "Condition failed"}},
            "TransactWriteItems",
        )

    @staticmethod
    def operand_type_error(operator):
        return ClientError(
            {
                "Error": {
                    "Code": "ValidationException",
                    "Message": (
                        "Invalid UpdateExpression: Incorrect operand type for operator "
                        f"or function; operator or function: {operator}, operand type: M"
                    ),
                }
            },
            "TransactWriteItems",
        )


class FakeTable:
    def __init__(self, name, client, key_name):
        self.name = name
        self.meta = SimpleNamespace(client=client)
        self.key_name = key_name
        self.items = {}
        self.lock = threading.Lock()
        self.scan_page_size = None
        self.last_update_request = None
        client.tables[name] = self

    def get_item(self, Key, **kwargs):
        item = self.items.get(Key[self.key_name])
        return {"Item": self._as_dynamodb_values(item)} if item is not None else {}

    def put_item(self, Item, **kwargs):
        with self.lock:
            item = dict(Item)
            key = item[self.key_name]
            existing = self.items.get(key)
            if existing is not None:
                values = kwargs.get("ExpressionAttributeValues", {})
                if (
                    existing.get("status") != values.get(":stopped")
                    and existing.get("heartbeatExpiresAt", float("inf")) > values.get(":now", 0)
                ):
                    raise ClientError(
                        {"Error": {"Code": "ConditionalCheckFailedException", "Message": "Condition failed"}},
                        "PutItem",
                    )
            self.items[key] = item
            return {}

    def query(self, **kwargs):
        condition = kwargs.get("KeyConditionExpression")

        def values_for(node):
            expression = node.get_expression()
            if expression["operator"] == "AND":
                return values_for(expression["values"][0]) | values_for(expression["values"][1])
            if expression["operator"] == "=":
                return {expression["values"][0].name: expression["values"][1]}
            return {}

        expected = values_for(condition) if condition is not None else {}
        records = [
            item for item in self.items.values()
            if all(item.get(key) == value for key, value in expected.items())
        ]
        if kwargs.get("IndexName") == "leaderboard-v2-index":
            records.sort(key=lambda item: item.get("leaderboardSort", ""), reverse=not kwargs.get("ScanIndexForward", True))
        limit = kwargs.get("Limit")
        if limit is not None:
            records = records[:limit]
        return {"Items": [self._as_dynamodb_values(item) for item in records]}

    @classmethod
    def _as_dynamodb_values(cls, value):
        if type(value) is int:
            return Decimal(value)
        if isinstance(value, dict):
            return {key: cls._as_dynamodb_values(item) for key, item in value.items()}
        if isinstance(value, list):
            return [cls._as_dynamodb_values(item) for item in value]
        return value

    def scan(self, **kwargs):
        projected = kwargs.get("ProjectionExpression", "")
        fields = [value.strip().lstrip("#") for value in projected.split(",") if value.strip()]
        aliases = kwargs.get("ExpressionAttributeNames", {})
        fields = [aliases.get("#" + field, field) for field in fields]
        records = list(self.items.values())
        start_key = kwargs.get("ExclusiveStartKey")
        if start_key:
            start = next(
                (index + 1 for index, item in enumerate(records) if item.get(self.key_name) == start_key[self.key_name]),
                len(records),
            )
            records = records[start:]
        page_size = self.scan_page_size or len(records)
        page = records[:page_size]
        result = {
            "Items": [
                self._as_dynamodb_values(
                    {field: item[field] for field in fields if field in item}
                )
                for item in page
            ]
        }
        if len(records) > page_size:
            result["LastEvaluatedKey"] = {
                self.key_name: page[-1][self.key_name]
            }
        return result

    def update_item(self, Key, ExpressionAttributeValues, **kwargs):
        with self.lock:
            self.last_update_request = kwargs
            token_hash = Key[self.key_name]
            item = self.items.get(token_hash)
            if self.name == "Servers":
                aliases = kwargs.get("ExpressionAttributeNames", {})
                status_name = aliases.get("#status", "status")
                old_states = {
                    value
                    for key, value in ExpressionAttributeValues.items()
                    if key.startswith(":old")
                }
                new_status = ExpressionAttributeValues.get(":new")
                valid = (
                    item is not None
                    and item.get("serverGeneration") == ExpressionAttributeValues.get(":generation")
                    and item.get(status_name) in old_states
                )
                if new_status == "READY":
                    valid = valid and item.get("reservedSlots", 0) < item.get("capacity", 0)
                if not valid:
                    raise ClientError(
                        {"Error": {"Code": "ConditionalCheckFailedException", "Message": "Condition failed"}},
                        "UpdateItem",
                    )
                item["status"] = new_status
                item["heartbeatExpiresAt"] = ExpressionAttributeValues[":heartbeat"]
                item["ttl"] = ExpressionAttributeValues[":ttl"]
                item["lastHeartbeatAt"] = ExpressionAttributeValues[":now"]
                return {"Attributes": dict(item)}
            if (
                item is None
                or item.get("status") != ExpressionAttributeValues[":issued"]
                or item.get("serverId") != ExpressionAttributeValues[":server"]
                or item.get("serverGeneration") != ExpressionAttributeValues[":generation"]
                or item.get("expiresAt", 0) <= ExpressionAttributeValues[":now"]
            ):
                raise ClientError(
                    {"Error": {"Code": "ConditionalCheckFailedException", "Message": "Condition failed"}},
                    "UpdateItem",
                )
            item["status"] = ExpressionAttributeValues[":consumed"]
            item["consumedAt"] = ExpressionAttributeValues[":now"]
            return {"Attributes": dict(item)}


@pytest.fixture
def tables(monkeypatch):
    client = FakeClient()
    room_table = FakeTable("Rooms", client, "roomCode")
    player_table = FakeTable("Players", client, "userId")
    ticket_table = FakeTable("JoinTickets", client, "ticketId")
    server_table = FakeTable("Servers", client, "serverId")
    reservation_table = FakeTable("Reservations", client, "reservationId")
    match_table = FakeTable("Matches", client, "matchId")
    lookup = {
        "rooms": room_table,
        "players": player_table,
        "tickets": ticket_table,
        "servers": server_table,
        "reservations": reservation_table,
        "matches": match_table,
    }
    monkeypatch.setattr(app, "_table", lookup.__getitem__)
    return SimpleNamespace(
        client=client,
        rooms=room_table,
        players=player_table,
        tickets=ticket_table,
        servers=server_table,
        reservations=reservation_table,
        matches=match_table,
    )


def event(method, path, body=None, headers=None, query=None, path_parameters=None):
    return {
        "requestContext": {"http": {"method": method}},
        "rawPath": path,
        "headers": headers or {},
        "body": None if body is None else json.dumps(body),
        "queryStringParameters": query,
        "pathParameters": path_parameters or {},
    }


def server_event(method, path, body, server_id="i-0123456789abcdef0", path_parameters=None):
    result = event(
        method,
        path,
        body,
        headers={"Idempotency-Key": "server-registration-key-0001"},
        path_parameters=path_parameters,
    )
    result["requestContext"]["authorizer"] = {
        "iam": {
            "userArn": (
                "arn:aws:sts::123456789012:assumed-role/"
                f"GameServerAgentRole/{server_id}"
            )
        }
    }
    return result


def payload(result):
    return json.loads(result["body"])


def error_code(result):
    return payload(result)["error"]["code"]


def test_config_disables_unavailable_matchmaking():
    result = app.lambda_handler(event("GET", "/config"), None)
    features = payload(result)["features"]
    assert features["privateRooms"] is False
    assert features["quickplay"] is False
    assert features["ticketStatus"] is True


def test_config_and_leaderboard_do_not_claim_unprepared_features(tables):
    config = payload(app.lambda_handler(event("GET", "/config"), None))
    assert config["features"]["privateRooms"] is False
    assert config["features"]["quickplay"] is False
    assert config["features"]["leaderboard"] is False

    result = app.lambda_handler(event("GET", "/leaderboard"), None)
    assert result["statusCode"] == 503
    assert error_code(result) == "leaderboard_unavailable"


@pytest.mark.parametrize(
    ("body", "code"),
    [
        ({"maxPlayers": True}, "invalid_max_players"),
        ({"maxPlayers": 11}, "invalid_max_players"),
        ({"killLimit": 0}, "invalid_kill_limit"),
        ({"extra": 1}, "unknown_fields"),
    ],
)
def test_create_room_rejects_invalid_input(body, code, tables):
    result = app.lambda_handler(
        event("POST", "/guest/rooms", body, {"Idempotency-Key": "a" * 32}),
        None,
    )
    assert result["statusCode"] == 400
    assert error_code(result) == code
    assert tables.client.transactions == 0


def test_create_room_requires_idempotency_key(tables):
    result = app.lambda_handler(event("POST", "/guest/rooms", {}), None)
    assert result["statusCode"] == 400
    assert error_code(result) == "invalid_idempotency_key"
    assert tables.client.transactions == 0


def test_create_room_is_idempotent_and_records_no_assignment(monkeypatch, tables):
    monkeypatch.setattr(app, "make_room_code", lambda: "AB12CD")
    headers = {"Idempotency-Key": "retry-key-0123456789"}
    create = event("POST", "/guest/rooms", {"maxPlayers": 4, "killLimit": 20}, headers)

    first = app.lambda_handler(create, None)
    second = app.lambda_handler(create, None)

    assert first["statusCode"] == second["statusCode"] == 201
    assert payload(first) == payload(second)
    assert payload(first)["status"] == "waiting_for_server"
    assert payload(first)["assignment"] == {
        "status": "pending",
        "reason": "player_join_required",
    }
    assert len(tables.rooms.items) == 2
    assert tables.client.transactions == 1
    assert tables.rooms.items["AB12CD"]["playerCount"] == 0


def test_create_room_rejects_same_key_with_different_body(monkeypatch, tables):
    monkeypatch.setattr(app, "make_room_code", lambda: "AB12CD")
    headers = {"Idempotency-Key": "retry-key-0123456789"}
    first = app.lambda_handler(
        event("POST", "/guest/rooms", {"maxPlayers": 4}, headers),
        None,
    )
    conflict = app.lambda_handler(
        event("POST", "/guest/rooms", {"maxPlayers": 5}, headers),
        None,
    )
    assert first["statusCode"] == 201
    assert conflict["statusCode"] == 409
    assert error_code(conflict) == "idempotency_conflict"


def test_expired_idempotency_key_requires_a_new_key(monkeypatch, tables):
    monkeypatch.setattr(app, "make_room_code", lambda: "AB12CD")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    request = event(
        "POST",
        "/guest/rooms",
        {},
        {"Idempotency-Key": "expired-key-0123456789"},
    )
    assert app.lambda_handler(request, None)["statusCode"] == 201

    monkeypatch.setattr(app.time, "time", lambda: 1000 + app.IDEMPOTENCY_TTL_SECONDS)
    retry = app.lambda_handler(request, None)
    assert retry["statusCode"] == 409
    assert error_code(retry) == "idempotency_key_expired"


def test_concurrent_create_retries_return_one_room(monkeypatch, tables):
    monkeypatch.setattr(app, "make_room_code", lambda: "AB12CD")
    request = event(
        "POST",
        "/guest/rooms",
        {"maxPlayers": 4},
        {"Idempotency-Key": "concurrent-key-012345"},
    )
    results = []
    threads = [
        threading.Thread(target=lambda: results.append(app.lambda_handler(request, None)))
        for _ in range(8)
    ]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()

    assert len(results) == 8
    assert all(result["statusCode"] == 201 for result in results)
    assert len({payload(result)["roomCode"] for result in results}) == 1
    assert sum(1 for item in tables.rooms.items.values() if item.get("guestRoom")) == 1


def test_join_checks_room_expiry_and_capacity_before_server_assignment(tables):
    tables.rooms.items["EXP123"] = {
        "roomCode": "EXP123",
        "status": "open",
        "maxPlayers": 4,
        "playerCount": 1,
        "expiresAt": 100,
    }
    expired = app.lambda_handler(
        event("POST", "/guest/rooms/EXP123/join", {}, {"Idempotency-Key": "expired-join-key-0001"}),
        None,
    )
    assert expired["statusCode"] == 410
    assert error_code(expired) == "room_expired"

    tables.rooms.items["FULL12"] = {
        "roomCode": "FULL12",
        "status": "open",
        "maxPlayers": 4,
        "playerCount": 4,
        "expiresAt": 9999999999,
    }
    full = app.lambda_handler(
        event("POST", "/guest/rooms/FULL12/join", {}, {"Idempotency-Key": "full-join-key-0000001"}),
        None,
    )
    assert full["statusCode"] == 409
    assert error_code(full) == "room_full"
    assert tables.client.transactions == 0


def test_join_and_quickplay_without_server_do_not_write_reservations(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    tables.rooms.items["ROOM12"] = {
        "roomCode": "ROOM12",
        "status": "open",
        "maxPlayers": 10,
        "playerCount": 1,
        "expiresAt": 9999999999,
    }
    before = dict(tables.rooms.items["ROOM12"])
    joined = app.lambda_handler(
        event("POST", "/guest/rooms/ROOM12/join", {}, {"Idempotency-Key": "join-no-server-00001"}),
        None,
    )
    quickplay = app.lambda_handler(
        event("POST", "/guest/quickplay", {}, {"Idempotency-Key": "quick-no-server-00001"}),
        None,
    )

    assert joined["statusCode"] == quickplay["statusCode"] == 503
    assert error_code(joined) == error_code(quickplay) == "no_servers_available"
    assert tables.rooms.items["ROOM12"] == before
    assert tables.client.transactions == 0
    assert tables.tickets.items == {}


def test_join_of_created_waiting_room_reports_no_server(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    monkeypatch.setattr(app, "make_room_code", lambda: "AB12CD")
    created = app.lambda_handler(
        event(
            "POST",
            "/guest/rooms",
            {},
            {"Idempotency-Key": "waiting-key-0123456789"},
        ),
        None,
    )
    result = app.lambda_handler(
        event(
            "POST",
            "/guest/rooms/AB12CD/join",
            {},
            {"Idempotency-Key": "waiting-join-key-00001"},
        ),
        None,
    )

    assert created["statusCode"] == 201
    assert result["statusCode"] == 503
    assert error_code(result) == "no_servers_available"
    assert tables.rooms.items["AB12CD"]["playerCount"] == 0
    assert tables.tickets.items == {}


def test_join_rejects_invalid_room_code(tables):
    result = app.lambda_handler(event("POST", "/guest/rooms/bad-code/join", {}), None)
    assert result["statusCode"] == 400
    assert error_code(result) == "invalid_room_code"


def test_ticket_status_is_minimal_and_does_not_expose_assignment(tables):
    import hashlib

    token = "t" * 43
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id] = {
        "ticketId": ticket_id,
        "status": "ISSUED",
        "expiresAt": 9999999999,
        "serverId": "secret-server",
        "endpoint": "secret.example:7777",
        "subjectId": "private-user",
    }
    result = app.lambda_handler(
        event("POST", "/guest/tickets/status", {"ticket": token}),
        None,
    )
    assert result["statusCode"] == 200
    assert payload(result) == {"ticketStatus": "issued", "expiresAt": 9999999999}
    assert "secret" not in result["body"]


def test_ticket_status_reports_expired_and_unknown_tokens(tables):
    import hashlib

    token = "e" * 43
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id] = {
        "ticketId": ticket_id,
        "status": "ISSUED",
        "expiresAt": 100,
    }
    expired = app.lambda_handler(
        event("POST", "/guest/tickets/status", {"ticket": token}),
        None,
    )
    unknown = app.lambda_handler(
        event("POST", "/guest/tickets/status", {"ticket": "u" * 43}),
        None,
    )
    assert payload(expired) == {"ticketStatus": "expired", "expiresAt": 100}
    assert unknown["statusCode"] == 404
    assert error_code(unknown) == "ticket_not_found"


def test_leaderboard_only_returns_public_fields_from_sharded_queries(monkeypatch, tables):
    monkeypatch.setenv("LEADERBOARD_ENABLED", "true")
    tables.players.items.update(
        {
            "private-1": {
                "userId": "private-1",
                "displayName": "Aria",
                "kills": 8,
                "deaths": 2,
                "email": "private@example.invalid",
                "authToken": "never-return-this",
                **app.leaderboard_index_fields("private-1", 8, 2),
            },
            "private-2": {
                "userId": "private-2",
                "displayName": "Bea",
                "kills": 12,
                "deaths": 5,
                "email": "other@example.invalid",
                **app.leaderboard_index_fields("private-2", 12, 5),
            },
        }
    )
    result = app.lambda_handler(
        event("GET", "/leaderboard", query={"top": "2"}),
        None,
    )
    assert result["statusCode"] == 200
    data = payload(result)
    assert data == {
        "players": [
            {"rank": 1, "displayName": "Bea", "kills": 12, "deaths": 5},
            {"rank": 2, "displayName": "Aria", "kills": 8, "deaths": 2},
        ]
    }
    assert "email" not in result["body"]
    assert "userId" not in result["body"]
    assert "authToken" not in result["body"]


@pytest.mark.parametrize("top", ["0", "101", "-1", "1.5", "abc"])
def test_leaderboard_rejects_invalid_top(monkeypatch, top, tables):
    monkeypatch.setenv("LEADERBOARD_ENABLED", "true")
    result = app.lambda_handler(event("GET", "/leaderboard", query={"top": top}), None)
    assert result["statusCode"] == 400
    assert error_code(result) == "invalid_top"


def _server_record():
    return {
        "serverId": "i-0123456789abcdef0",
        "serverGeneration": "11111111-1111-4111-8111-111111111111",
        "matchId": "22222222-2222-4222-8222-222222222222",
        "region": "ap-south-1",
        "publicAddress": "8.8.8.8",
        "gamePort": 7777,
        "protocol": "udp",
        "capacity": 10,
        "reservedSlots": 0,
        "status": "READY",
        "heartbeatExpiresAt": 5000,
        "ttl": 50000,
    }


def test_internal_server_operations_reject_unauthenticated_and_wrong_role(tables, monkeypatch):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    body = {"publicAddress": "8.8.8.8", "gamePort": 7777, "capacity": 10}
    unauthenticated = app.lambda_handler(
        event("POST", "/internal/servers/register", body),
        None,
    )
    wrong_role = server_event("POST", "/internal/servers/register", body)
    wrong_role["requestContext"]["authorizer"]["iam"]["userArn"] = (
        "arn:aws:sts::123456789012:assumed-role/OtherRole/i-0123456789abcdef0"
    )
    wrong_role_result = app.lambda_handler(wrong_role, None)
    assert unauthenticated["statusCode"] == 403
    assert wrong_role_result["statusCode"] == 403
    assert error_code(unauthenticated) == error_code(wrong_role_result) == "server_auth_required"
    assert tables.servers.items == {}


def test_registration_and_heartbeat_require_the_authenticated_instance(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    registration = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/register",
            {"publicAddress": "8.8.8.8", "gamePort": 7777, "capacity": 10},
        ),
        None,
    )
    assert registration["statusCode"] == 201
    registration_data = payload(registration)
    assert registration_data["status"] == "STARTING"
    assert registration_data["serverId"] == "i-0123456789abcdef0"
    assert registration_data["matchId"]
    assert tables.servers.items[registration_data["serverId"]]["reservedSlots"] == 0

    heartbeat = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/heartbeat",
            {"generation": registration_data["serverGeneration"], "status": "READY"},
        ),
        None,
    )
    assert heartbeat["statusCode"] == 200
    assert payload(heartbeat)["status"] == "READY"
    assert tables.servers.items[registration_data["serverId"]]["heartbeatExpiresAt"] == 1090

    duplicate = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/register",
            {"publicAddress": "8.8.8.8", "gamePort": 7777, "capacity": 10},
        ),
        None,
    )
    assert duplicate["statusCode"] == 200
    assert payload(duplicate)["duplicate"] is True
    assert payload(duplicate)["matchId"] == registration_data["matchId"]
    conflict = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/register",
            {"publicAddress": "8.8.8.8", "gamePort": 7778, "capacity": 10},
        ),
        None,
    )
    assert conflict["statusCode"] == 409
    assert error_code(conflict) == "idempotency_conflict"


def test_restart_registration_replaces_stopped_instance_with_new_address(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    first = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/register",
            {"publicAddress": "52.66.53.59", "gamePort": 7777, "capacity": 10},
        ),
        None,
    )
    old_registration = payload(first)
    stopped = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/heartbeat",
            {"generation": old_registration["serverGeneration"], "status": "STOPPED"},
        ),
        None,
    )
    assert stopped["statusCode"] == 200

    restarted_request = server_event(
        "POST",
        "/internal/servers/register",
        {"publicAddress": "13.127.204.73", "gamePort": 7777, "capacity": 10},
    )
    restarted_request["headers"]["Idempotency-Key"] = "server-restart-key-0002"
    restarted = app.lambda_handler(restarted_request, None)

    assert restarted["statusCode"] == 201
    new_registration = payload(restarted)
    assert new_registration["serverGeneration"] != old_registration["serverGeneration"]
    assert new_registration["matchId"] != old_registration["matchId"]
    record = tables.servers.items[new_registration["serverId"]]
    assert record["publicAddress"] == "13.127.204.73"
    assert record["status"] == "STARTING"
    assert record["reservedSlots"] == 0


def test_restart_registration_recovers_after_heartbeat_lease_expires(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    first = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/register",
            {"publicAddress": "52.66.53.59", "gamePort": 7777, "capacity": 10},
        ),
        None,
    )
    old_registration = payload(first)
    monkeypatch.setattr(app.time, "time", lambda: 1000 + app.SERVER_HEARTBEAT_SECONDS)

    restarted_request = server_event(
        "POST",
        "/internal/servers/register",
        {"publicAddress": "13.127.204.73", "gamePort": 7777, "capacity": 10},
    )
    restarted_request["headers"]["Idempotency-Key"] = "server-restart-key-0003"
    restarted = app.lambda_handler(restarted_request, None)

    assert restarted["statusCode"] == 201
    assert payload(restarted)["serverGeneration"] != old_registration["serverGeneration"]
    assert tables.servers.items[old_registration["serverId"]]["publicAddress"] == "13.127.204.73"


def test_fresh_registration_key_does_not_replace_a_live_instance(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    request = server_event(
        "POST",
        "/internal/servers/register",
        {"publicAddress": "8.8.8.8", "gamePort": 7777, "capacity": 10},
    )
    first = app.lambda_handler(request, None)
    original = payload(first)
    retry = dict(request)
    retry["headers"] = {"Idempotency-Key": "different-startup-key-0002"}

    result = app.lambda_handler(retry, None)

    assert result["statusCode"] == 409
    assert error_code(result) == "server_already_registered"
    assert tables.servers.items[original["serverId"]]["serverGeneration"] == original["serverGeneration"]


def test_server_registration_validates_network_and_capacity(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    for body, code in (
        ({"publicAddress": "127.0.0.1", "gamePort": 7777}, "invalid_public_address"),
        ({"publicAddress": "8.8.8.8", "gamePort": True}, "invalid_game_port"),
        ({"publicAddress": "8.8.8.8", "gamePort": 7777, "capacity": 11}, "invalid_capacity"),
    ):
        result = app.lambda_handler(
            server_event("POST", "/internal/servers/register", body),
            None,
        )
        assert result["statusCode"] == 400
        assert error_code(result) == code
    assert tables.servers.items == {}


def test_quickplay_assigns_one_slot_and_replays_same_ticket(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    tables.servers.items[_server_record()["serverId"]] = _server_record()
    request = event(
        "POST",
        "/guest/quickplay",
        {},
        {"Idempotency-Key": "quickplay-retry-key-00001"},
    )
    first = app.lambda_handler(request, None)
    second = app.lambda_handler(request, None)

    assert first["statusCode"] == second["statusCode"] == 200
    assert payload(first) == payload(second)
    assignment = payload(first)
    assert assignment["status"] == "assigned"
    assert assignment["endpoint"] == {
        "host": "8.8.8.8",
        "port": 7777,
        "protocol": "udp",
    }
    assert assignment["matchId"] == _server_record()["matchId"]
    assert len(assignment["ticket"]) >= 40
    assert tables.servers.items[_server_record()["serverId"]]["reservedSlots"] == 1
    assert len(tables.tickets.items) == len(tables.reservations.items) == 1
    assert tables.client.transactions == 1


def test_quickplay_slot_type_change_before_transaction_is_unavailable(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    server = _server_record()
    tables.servers.items[server["serverId"]] = dict(server)

    def available(region, now):
        tables.servers.items[server["serverId"]] = {**server, "reservedSlots": {"corrupt": 0}}
        return [dict(server)]

    monkeypatch.setattr(app, "_available_servers", available)
    result = app.lambda_handler(
        event(
            "POST",
            "/guest/quickplay",
            {},
            {"Idempotency-Key": "quickplay-slot-type-race-01"},
        ),
        None,
    )

    assert result["statusCode"] == 503
    assert error_code(result) == "no_servers_available"
    assert tables.servers.items[server["serverId"]]["reservedSlots"] == {"corrupt": 0}
    assert tables.tickets.items == {}
    assert tables.reservations.items == {}


def test_quickplay_skips_server_with_non_numeric_slot_counter(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    server = _server_record()
    server["reservedSlots"] = {"corrupt": 0}
    tables.servers.items[server["serverId"]] = server

    result = app.lambda_handler(
        event(
            "POST",
            "/guest/quickplay",
            {},
            {"Idempotency-Key": "quickplay-corrupt-slots-01"},
        ),
        None,
    )

    assert result["statusCode"] == 503
    assert error_code(result) == "no_servers_available"
    assert tables.client.transactions == 0
    assert tables.servers.items[server["serverId"]]["reservedSlots"] == {"corrupt": 0}


def test_private_room_join_atomically_pins_server_and_capacity(monkeypatch, tables):
    monkeypatch.setenv("MATCHMAKING_ENABLED", "true")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    tables.servers.items[_server_record()["serverId"]] = _server_record()
    tables.rooms.items["ROOM12"] = {
        "roomCode": "ROOM12",
        "status": "waiting_for_server",
        "maxPlayers": 10,
        "playerCount": 0,
        "expiresAt": 5000,
    }
    request = event(
        "POST",
        "/guest/rooms/ROOM12/join",
        {},
        {"Idempotency-Key": "private-join-key-000001"},
    )
    assigned = app.lambda_handler(request, None)
    assert assigned["statusCode"] == 200
    assert payload(assigned)["roomCode"] == "ROOM12"
    assert tables.rooms.items["ROOM12"]["status"] == "open"
    assert tables.rooms.items["ROOM12"]["serverId"] == _server_record()["serverId"]
    assert tables.rooms.items["ROOM12"]["playerCount"] == 1
    assert tables.servers.items[_server_record()["serverId"]]["reservedSlots"] == 1


def test_ticket_consumption_is_iam_protected_bound_and_single_use(monkeypatch, tables):
    import hashlib

    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    monkeypatch.setattr(app.time, "time", lambda: 1000)
    server = _server_record()
    server["reservedSlots"] = 1
    tables.servers.items[server["serverId"]] = server
    token = "T" * 43
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    tables.tickets.items[ticket_id] = {
        "ticketId": ticket_id,
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": server["serverId"],
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "status": "ISSUED",
        "expiresAt": 2000,
    }
    tables.reservations.items["abcdefghijklmnopqrstuvwx"] = {
        "reservationId": "abcdefghijklmnopqrstuvwx",
        "serverId": server["serverId"],
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "status": "RESERVED",
        "ticketExpiresAt": 2000,
    }
    unauthenticated = app.lambda_handler(
        event(
            "POST",
            "/internal/tickets/consume",
            {"ticket": token, "generation": server["serverGeneration"]},
        ),
        None,
    )
    assert unauthenticated["statusCode"] == 403

    request = server_event(
        "POST",
        "/internal/tickets/consume",
        {"ticket": token, "generation": server["serverGeneration"]},
    )
    consumed = app.lambda_handler(request, None)
    replay = app.lambda_handler(request, None)
    assert consumed["statusCode"] == 200
    assert payload(consumed)["reservationId"] == "abcdefghijklmnopqrstuvwx"
    assert replay["statusCode"] == 409
    assert error_code(replay) == "ticket_invalid_or_consumed"
    assert tables.reservations.items["abcdefghijklmnopqrstuvwx"]["status"] == "ACTIVE"
    released = app.lambda_handler(
        server_event(
            "POST",
            "/internal/servers/reservations/abcdefghijklmnopqrstuvwx/release",
            {"generation": server["serverGeneration"]},
            path_parameters={"reservationId": "abcdefghijklmnopqrstuvwx"},
        ),
        None,
    )
    assert released["statusCode"] == 200
    assert tables.servers.items[server["serverId"]]["reservedSlots"] == 0


def test_release_reservation_is_idempotent_and_decrements_capacity(tables, monkeypatch):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    server = _server_record()
    server["reservedSlots"] = 1
    tables.servers.items[server["serverId"]] = server
    reservation_id = "abcdefghijklmnopqrstuvwx"
    tables.reservations.items[reservation_id] = {
        "reservationId": reservation_id,
        "serverId": server["serverId"],
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "status": "RESERVED",
        "createdAt": 900,
        "ticketExpiresAt": 1100,
    }
    request = server_event(
        "POST",
        f"/internal/servers/reservations/{reservation_id}/release",
        {"generation": server["serverGeneration"]},
        path_parameters={"reservationId": reservation_id},
    )
    first = app.lambda_handler(request, None)
    replay = app.lambda_handler(request, None)
    assert first["statusCode"] == replay["statusCode"] == 200
    assert payload(first)["released"] is True
    assert payload(replay)["released"] is False
    assert tables.servers.items[server["serverId"]]["reservedSlots"] == 0
    assert tables.reservations.items[reservation_id]["status"] == "RELEASED"


def test_result_submission_is_authenticated_validated_and_idempotent(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    server = _server_record()
    tables.servers.items[server["serverId"]] = server
    body = {
        "generation": server["serverGeneration"],
        "winnerTeam": 1,
        "teamScores": [18, 20],
        "durationSeconds": 600,
        "players": [
            {"playerId": "ngo-1", "team": 0, "kills": 7, "deaths": 4},
            {"playerId": "ngo-2", "team": 1, "kills": 8, "deaths": 2},
        ],
    }
    path = f"/internal/matches/{server['matchId']}/results"
    unauthenticated = app.lambda_handler(event("POST", path, body), None)
    assert unauthenticated["statusCode"] == 403

    request = server_event(
        "POST",
        path,
        body,
        path_parameters={"matchId": server["matchId"]},
    )
    result = app.lambda_handler(request, None)
    replay = app.lambda_handler(request, None)
    assert result["statusCode"] == 201
    assert payload(result)["verificationStatus"] == "server_reported"
    assert replay["statusCode"] == 200
    assert payload(replay)["duplicate"] is True
    assert tables.servers.items[server["serverId"]]["status"] == "DRAINING"
    assert tables.matches.items[server["matchId"]]["players"] == body["players"]


def test_match_result_validation_rejects_duplicates_and_unknown_fields(monkeypatch, tables):
    monkeypatch.setenv("SERVER_ROLE_NAME", "GameServerAgentRole")
    monkeypatch.setenv("AWS_ACCOUNT_ID", "123456789012")
    server = _server_record()
    tables.servers.items[server["serverId"]] = server
    body = {
        "generation": server["serverGeneration"],
        "winnerTeam": 0,
        "teamScores": [1, 0],
        "durationSeconds": 60,
        "players": [
            {"playerId": "same", "team": 0, "kills": 1, "deaths": 0},
            {"playerId": "same", "team": 1, "kills": 0, "deaths": 1},
        ],
    }
    result = app.lambda_handler(
        server_event(
            "POST",
            f"/internal/matches/{server['matchId']}/results",
            body,
            path_parameters={"matchId": server["matchId"]},
        ),
        None,
    )
    assert result["statusCode"] == 400
    assert error_code(result) == "duplicate_player_id"
    assert tables.matches.items == {}
