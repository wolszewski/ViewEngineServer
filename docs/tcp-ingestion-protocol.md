# TCP ingestion protocol

## Purpose

This protocol provides a lower-overhead ingestion path than HTTP by keeping a TCP
connection open, sending newline-framed commands, and reusing collection schema so
row mutations can be sent with numeric field indexes instead of field names.

The current design is intentionally small:

- one persistent TCP connection per producer process
- per-command request ids
- per-collection ordering guarantees
- asynchronous `ACK` / `ERR` support for `UPSERT`/`DELETE` (client does not block on replies)
- `SCHEMA` responses for bootstrap and cache refresh

By default the server listens on TCP port `6000`. In Aspire, the app host should
be treated as the source of truth for the TCP port and should pass that value to
both the web host and the data provider via configuration.

## Transport

- UTF-8 text
- one message per line
- token separator: `|`
- reserved characters in string tokens are escaped with URI percent-encoding
- `#` represents `null`
- `$` represents the empty string

Example raw frame:

```text
UPSERT|42|trades|trade-1|3|1|T000001|4|101.25|8|Working
```

## Connection lifecycle

When a client connects, the server immediately sends:

```text
CONNECTED|1
```

`1` is the protocol version. The client should validate it before sending work.

The client keeps the socket open and can queue requests independently of the
background send/receive loops. Reconnect is client-driven.

## Ordering and processing

- Commands for a single collection are processed in-order.
- On the server, each collection has a bounded queue (`CollectionQueueCapacity`,
  default `100000`) with a single consumer, so mutation ordering is preserved
  per collection while TCP readers do not wait for mutation execution.
- `UPSERT` and `DELETE` are non-blocking from the client perspective: requests are
  queued immediately, and `ACK`/`ERR` can arrive later on the same TCP stream.
- Request/response commands still return:
  - `SCHEMA|requestId|collection|...` for create/get-schema success
  - `ERR|requestId|message` for failures
  - `PONG|requestId` for ping
- `EnableAsyncAcks` controls whether the server emits async `ACK`/`ERR` for
  `UPSERT`/`DELETE`. It is enabled by default.

## Commands

### Create collection

```text
CREATE|requestId|collection|fieldCount|fieldName1|fieldType1|fieldName2|fieldType2|...
```

Notes:

- The primary key is implicit and is always field index `0` with the name `key`.
- The `CREATE` request contains only user-defined fields.
- On success, the server returns a full `SCHEMA` response that includes the
  implicit key field.
- Supported `fieldType` values include: `string` (alias: `enum`), `boolean` (alias: `bool`), `int`, `long`, `double`,
  `decimal`, `dateonly`, `datetime`, and `datetimeoffset`.

Example:

```text
CREATE|1|trades|3|tradeId|string|price|decimal|status|string
```

### Get schema

```text
GET_SCHEMA|requestId|collection
```

Example:

```text
GET_SCHEMA|2|trades
```

### Upsert row

```text
UPSERT|requestId|collection|rowKey|pairCount|fieldIndex1|value1|fieldIndex2|value2|...
```

Rules:

- `rowKey` is always sent separately and is not part of the indexed field pairs.
- field index `0` is reserved for `key` and cannot be updated through `UPSERT`.
- clients should translate field names to indexes locally by using cached schema.

Example:

```text
UPSERT|3|trades|trade-1|2|2|101.25|3|Working
```

### Delete row

```text
DELETE|requestId|collection|rowKey
```

Example:

```text
DELETE|4|trades|trade-1
```

### Ping

```text
PING|requestId
```

Example:

```text
PING|5
```

## Responses

### Ack

```text
ACK|requestId|operation
```

Example:

```text
ACK|3|UPSERT
```

### Schema

```text
SCHEMA|requestId|collection|fieldCount|fieldIndex1|fieldName1|fieldType1|fieldIndex2|fieldName2|fieldType2|...
```

Example:

```text
SCHEMA|2|trades|4|0|key|string|1|tradeId|string|2|price|decimal|3|status|string
```

### Error

```text
ERR|requestId|message
```

### Pong

```text
PONG|requestId
```

## Client schema caching

Clients should cache the `SCHEMA` response by collection name and translate:

```text
{ "tradeId": "T000001", "price": "101.25", "status": "Working" }
```

into:

```text
UPSERT|3|trades|trade-1|3|1|T000001|2|101.25|3|Working
```

That removes repeated field-name payload from the hot ingestion path while still
letting application code work with field-name dictionaries.
