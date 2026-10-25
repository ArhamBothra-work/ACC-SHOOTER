"""DynamoDB operations for match allocation and one-use join tickets."""

import hashlib
import secrets
from decimal import Decimal

from boto3.dynamodb.conditions import Key
from boto3.dynamodb.types import TypeSerializer
from botocore.exceptions import ClientError


class IdempotencyConflict(Exception):
    pass


class ReservationUnavailable(Exception):
    pass


class ReservationNotFound(Exception):
    pass


def _serialize_item(item):
    serializer = TypeSerializer()
    return {key: serializer.serialize(value) for key, value in item.items()}


def _request_key(subject_id, idempotency_key):
    digest = hashlib.sha256(
        f"{subject_id}\0{idempotency_key}".encode("utf-8")
    ).hexdigest()
    return "REQUEST#" + digest


def _cached_response(rooms_table, request_key, request_hash, now, tickets_table=None):
    item = rooms_table.get_item(
        Key={"roomCode": request_key},
        ConsistentRead=True,
    ).get("Item")
    if item is None:
        return None
    expires_at = item.get("expiresAt")
    if isinstance(expires_at, Decimal) and expires_at == expires_at.to_integral_value():
        expires_at = int(expires_at)
    if type(expires_at) is int and expires_at <= now:
        raise IdempotencyConflict("Idempotency key has expired; use a new key.")
    if item.get("requestHash") != request_hash:
        raise IdempotencyConflict("Idempotency key was reused for a different request.")
    cached = item.get("response")
    if not isinstance(cached, dict):
        raise RuntimeError("Stored idempotency record has no valid response.")
    token = cached.get("ticket")
    if token is not None:
        if tickets_table is None:
            raise RuntimeError("Ticket table is required to replay a matchmaking assignment.")
        ticket = ticket_status(tickets_table, token, now)
        if ticket is None or ticket.get("status") != "ISSUED":
            raise IdempotencyConflict(
                "The previous assignment ticket is no longer usable; start a new request with a new key."
            )
    return cached


def cached_reservation_response(
    rooms_table,
    tickets_table,
    idempotency_key,
    request_hash,
    now,
):
    """Return an earlier allocation before candidate discovery or room checks."""
    return _cached_response(
        rooms_table,
        _request_key("public-guest", idempotency_key),
        request_hash,
        now,
        tickets_table,
    )


def reserve_server_and_issue_ticket(
    ddb_client,
    rooms_table,
    servers_table,
    tickets_table,
    reservations_table,
    server,
    *,
    idempotency_key,
    request_hash,
    now,
    room_code=None,
    ticket_ttl_seconds=120,
):
    """Atomically reserve capacity and create the ticket and reservation records."""
    if not isinstance(idempotency_key, str) or not idempotency_key:
        raise ValueError("An idempotency key is required.")
    if type(now) is not int or type(ticket_ttl_seconds) is not int or ticket_ttl_seconds < 1:
        raise ValueError("Invalid ticket time configuration.")

    request_key = _request_key("public-guest", idempotency_key)
    cached = _cached_response(rooms_table, request_key, request_hash, now, tickets_table)
    if cached is not None:
        return cached

    token = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token.encode("ascii")).hexdigest()
    reservation_id = secrets.token_urlsafe(18)
    expires_at = now + ticket_ttl_seconds
    response = {
        "status": "assigned",
        "endpoint": {
            "host": server["publicAddress"],
            "port": server["gamePort"],
            "protocol": "udp",
        },
        "matchId": server["matchId"],
        "ticket": token,
        "ticketExpiresAt": expires_at,
    }
    request_record = {
        "roomCode": request_key,
        "status": "IDEMPOTENCY",
        "createdAt": str(now),
        "requestHash": request_hash,
        "response": response,
        "expiresAt": now + 24 * 60 * 60,
        "ttl": now + 24 * 60 * 60,
    }
    ticket = {
        "ticketId": token_hash,
        "reservationId": reservation_id,
        "serverId": server["serverId"],
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "roomCode": room_code,
        "status": "ISSUED",
        "issuedAt": now,
        "expiresAt": expires_at,
        "ttl": expires_at,
    }
    reservation = {
        "reservationId": reservation_id,
        "serverId": server["serverId"],
        "serverGeneration": server["serverGeneration"],
        "matchId": server["matchId"],
        "ticketId": token_hash,
        "roomCode": room_code,
        "status": "RESERVED",
        "createdAt": now,
        "ticketExpiresAt": expires_at,
        "ttl": now + 7 * 24 * 60 * 60,
    }

    transaction = [
        {
            "Update": {
                "TableName": servers_table.name,
                "Key": _serialize_item({"serverId": server["serverId"]}),
                "UpdateExpression": "SET reservedSlots = reservedSlots + :one",
                "ConditionExpression": (
                    "#status = :ready AND heartbeatExpiresAt > :now "
                    "AND serverGeneration = :generation "
                    "AND matchId = :match "
                    "AND reservedSlots < capacity "
                    "AND attribute_exists(publicAddress) AND attribute_exists(gamePort)"
                ),
                "ExpressionAttributeNames": {"#status": "status"},
                "ExpressionAttributeValues": _serialize_item(
                    {
                        ":one": 1,
                        ":ready": "READY",
                        ":now": now,
                        ":generation": server["serverGeneration"],
                        ":match": server["matchId"],
                    }
                ),
            }
        },
        {
            "Put": {
                "TableName": tickets_table.name,
                "Item": _serialize_item(ticket),
                "ConditionExpression": "attribute_not_exists(ticketId)",
            }
        },
        {
            "Put": {
                "TableName": reservations_table.name,
                "Item": _serialize_item(reservation),
                "ConditionExpression": "attribute_not_exists(reservationId)",
            }
        },
    ]
    if room_code is not None:
        transaction.append(
            {
                "Update": {
                    "TableName": rooms_table.name,
                    "Key": _serialize_item({"roomCode": room_code}),
                    "UpdateExpression": (
                        "SET #status = :open, serverId = :server, matchId = :match, "
                        "serverGeneration = :generation ADD playerCount :one"
                    ),
                    "ConditionExpression": (
                        "expiresAt > :now AND playerCount < maxPlayers AND "
                        "((#status = :waiting AND attribute_not_exists(serverId)) OR "
                        "(#status = :open AND serverId = :server AND matchId = :match "
                        "AND serverGeneration = :generation))"
                    ),
                    "ExpressionAttributeNames": {"#status": "status"},
                    "ExpressionAttributeValues": _serialize_item(
                        {
                            ":one": 1,
                            ":now": now,
                            ":waiting": "waiting_for_server",
                            ":open": "open",
                            ":server": server["serverId"],
                            ":match": server["matchId"],
                            ":generation": server["serverGeneration"],
                        }
                    ),
                }
            }
        )
    transaction.append(
        {
            "Put": {
                "TableName": rooms_table.name,
                "Item": _serialize_item(request_record),
                "ConditionExpression": "attribute_not_exists(roomCode)",
            }
        }
    )

    try:
        ddb_client.transact_write_items(TransactItems=transaction)
        return response
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") != "TransactionCanceledException":
            raise
        cached = _cached_response(rooms_table, request_key, request_hash, now, tickets_table)
        if cached is not None:
            return cached
        raise ReservationUnavailable(
            "Server capacity, heartbeat, room capacity, or uniqueness condition failed."
        ) from exc


def consume_join_ticket(
    ddb_client,
    tickets_table,
    reservations_table,
    token,
    server_id,
    server_generation,
    now,
):
    """Consume a ticket and activate its slot in one transaction."""
    if not isinstance(token, str) or not token:
        return None
    try:
        token_bytes = token.encode("ascii")
    except UnicodeEncodeError:
        return None
    ticket_id = hashlib.sha256(token_bytes).hexdigest()
    ticket = tickets_table.get_item(
        Key={"ticketId": ticket_id},
        ConsistentRead=True,
    ).get("Item")
    if (
        not ticket
        or ticket.get("serverId") != server_id
        or ticket.get("serverGeneration") != server_generation
        or ticket.get("status") != "ISSUED"
        or ticket.get("expiresAt", 0) <= now
    ):
        return None
    serializer = TypeSerializer()
    try:
        ddb_client.transact_write_items(
            TransactItems=[
                {
                    "Update": {
                        "TableName": tickets_table.name,
                        "Key": {"ticketId": serializer.serialize(ticket_id)},
                        "UpdateExpression": "SET #status = :consumed, consumedAt = :now",
                        "ConditionExpression": (
                            "#status = :issued AND serverId = :server "
                            "AND serverGeneration = :generation AND expiresAt > :now"
                        ),
                        "ExpressionAttributeNames": {"#status": "status"},
                        "ExpressionAttributeValues": {
                            name: serializer.serialize(value)
                            for name, value in {
                                ":consumed": "CONSUMED",
                                ":issued": "ISSUED",
                                ":server": server_id,
                                ":generation": server_generation,
                                ":now": now,
                            }.items()
                        },
                    }
                },
                {
                    "Update": {
                        "TableName": reservations_table.name,
                        "Key": {
                            "reservationId": serializer.serialize(ticket["reservationId"])
                        },
                        "UpdateExpression": "SET #status = :active, consumedAt = :now",
                        "ConditionExpression": (
                            "#status = :reserved AND serverId = :server "
                            "AND serverGeneration = :generation AND ticketExpiresAt > :now"
                        ),
                        "ExpressionAttributeNames": {"#status": "status"},
                        "ExpressionAttributeValues": {
                            name: serializer.serialize(value)
                            for name, value in {
                                ":active": "ACTIVE",
                                ":reserved": "RESERVED",
                                ":server": server_id,
                                ":generation": server_generation,
                                ":now": now,
                            }.items()
                        },
                    }
                },
            ]
        )
        ticket["status"] = "CONSUMED"
        ticket["consumedAt"] = now
        return ticket
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") == "TransactionCanceledException":
            return None
        raise


def release_reservation(
    ddb_client,
    reservations_table,
    servers_table,
    rooms_table,
    reservation_id,
    server_id,
    server_generation,
    now,
):
    """Release one reservation once; the slot decrement is in the same transaction."""
    last_error = None
    for _ in range(5):
        reservation = reservations_table.get_item(
            Key={"reservationId": reservation_id},
            ConsistentRead=True,
        ).get("Item")
        if reservation is None or reservation.get("serverId") != server_id:
            raise ReservationNotFound()
        if reservation.get("status") == "RELEASED":
            return False
        if reservation.get("status") not in {"RESERVED", "ACTIVE"}:
            raise ReservationNotFound()

        current_server = servers_table.get_item(
            Key={"serverId": server_id},
            ConsistentRead=True,
        ).get("Item")
        transaction = [
            {
                "Update": {
                    "TableName": reservations_table.name,
                    "Key": _serialize_item({"reservationId": reservation_id}),
                    "UpdateExpression": "SET #status = :released, releasedAt = :now",
                    "ConditionExpression": (
                        "#status IN (:reserved, :active) AND serverId = :server "
                        "AND serverGeneration = :generation"
                    ),
                    "ExpressionAttributeNames": {"#status": "status"},
                    "ExpressionAttributeValues": _serialize_item(
                        {
                            ":released": "RELEASED",
                            ":reserved": "RESERVED",
                            ":active": "ACTIVE",
                            ":server": server_id,
                            ":generation": reservation["serverGeneration"],
                            ":now": now,
                        }
                    ),
                }
            }
        ]
        if (
            current_server
            and current_server.get("serverGeneration") == reservation.get("serverGeneration")
        ):
            transaction.append(
                {
                    "Update": {
                        "TableName": servers_table.name,
                        "Key": _serialize_item({"serverId": server_id}),
                        "UpdateExpression": "SET reservedSlots = reservedSlots - :one",
                        "ConditionExpression": (
                            "serverGeneration = :generation AND reservedSlots >= :one"
                        ),
                        "ExpressionAttributeValues": _serialize_item(
                            {
                                ":one": 1,
                                ":generation": reservation["serverGeneration"],
                            }
                        ),
                    }
                }
            )

        room_code = reservation.get("roomCode")
        room = (
            rooms_table.get_item(Key={"roomCode": room_code}, ConsistentRead=True).get("Item")
            if room_code
            else None
        )
        if (
            room
            and room.get("status") == "open"
            and room.get("serverId") == server_id
            and room.get("matchId") == reservation.get("matchId")
            and room.get("serverGeneration") == reservation.get("serverGeneration")
        ):
            player_count = room.get("playerCount")
            if isinstance(player_count, Decimal) and player_count == player_count.to_integral_value():
                player_count = int(player_count)
            if type(player_count) is int and player_count > 0:
                if player_count == 1:
                    update_expression = (
                        "SET playerCount = :zero, #status = :waiting REMOVE serverId"
                    )
                    condition = (
                        "playerCount = :one AND serverId = :server "
                        "AND matchId = :match AND serverGeneration = :generation"
                    )
                    values = {
                        ":zero": 0,
                        ":waiting": "waiting_for_server",
                        ":one": 1,
                        ":server": server_id,
                        ":match": reservation["matchId"],
                        ":generation": reservation["serverGeneration"],
                    }
                    names = {"#status": "status"}
                else:
                    update_expression = "SET playerCount = playerCount - :one"
                    condition = (
                        "playerCount > :one AND serverId = :server "
                        "AND matchId = :match AND serverGeneration = :generation"
                    )
                    values = {
                        ":one": 1,
                        ":server": server_id,
                        ":match": reservation["matchId"],
                        ":generation": reservation["serverGeneration"],
                    }
                    names = None
                room_update = {
                    "TableName": rooms_table.name,
                    "Key": _serialize_item({"roomCode": room_code}),
                    "UpdateExpression": update_expression,
                    "ConditionExpression": condition,
                    "ExpressionAttributeValues": _serialize_item(values),
                }
                if names:
                    room_update["ExpressionAttributeNames"] = names
                transaction.append({"Update": room_update})

        try:
            ddb_client.transact_write_items(TransactItems=transaction)
            return True
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") != "TransactionCanceledException":
                raise
            last_error = exc
            latest = reservations_table.get_item(
                Key={"reservationId": reservation_id},
                ConsistentRead=True,
            ).get("Item")
            if latest and latest.get("status") == "RELEASED":
                return False
    raise ReservationUnavailable("Reservation release conditions changed.") from last_error


def reap_expired_reservations(
    reservations_table,
    ddb_client,
    servers_table,
    rooms_table,
    server_id,
    server_generation,
    now,
):
    """Release expired unconsumed reservations for one authenticated server."""
    query = {
        "IndexName": "server-status-index",
        "KeyConditionExpression": Key("serverId").eq(server_id) & Key("status").eq("RESERVED"),
    }
    expired = []
    while True:
        result = reservations_table.query(**query)
        expired.extend(
            item
            for item in result.get("Items", [])
            if item.get("serverGeneration") == server_generation
            and item.get("ticketExpiresAt", now + 1) <= now
        )
        last_key = result.get("LastEvaluatedKey")
        if not last_key:
            break
        query["ExclusiveStartKey"] = last_key
    released = 0
    for item in expired:
        if release_reservation(
            ddb_client,
            reservations_table,
            servers_table,
            rooms_table,
            item["reservationId"],
            server_id,
            server_generation,
            now,
        ):
            released += 1
    return released


def ticket_status(tickets_table, token, now):
    """Read a ticket's minimal public status using its opaque bearer token."""
    ticket_id = hashlib.sha256(token.encode("ascii")).hexdigest()
    item = tickets_table.get_item(
        Key={"ticketId": ticket_id},
        ConsistentRead=True,
        ProjectionExpression="#status, expiresAt",
        ExpressionAttributeNames={"#status": "status"},
    ).get("Item")
    if item is None:
        return None
    expires_at = item.get("expiresAt")
    if isinstance(expires_at, Decimal) and expires_at == expires_at.to_integral_value():
        expires_at = int(expires_at)
    if type(expires_at) is not int:
        raise RuntimeError("Ticket has no valid numeric expiry.")
    if expires_at <= now:
        return {"status": "EXPIRED", "expiresAt": expires_at}
    item["expiresAt"] = expires_at
    return item
