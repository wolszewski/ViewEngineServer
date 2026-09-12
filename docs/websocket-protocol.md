# WebSocket protocol

This is the wire reference for `GET /ws`. Lifecycle rules (subscription identity, snapshot modes, rejection
semantics) are specified in [subscription-design.md](subscription-design.md). This document covers message
shapes.

- Every WebSocket message is one UTF-8 text frame holding exactly one protocol message.
- Client-to-server messages are always JSON.
- Server-to-client messages use the format chosen per subscription with `messageFormat`: `compact`
  (the default) or `json`.

## Client to server

All messages are JSON objects with a case-insensitive `type`. Unknown types, invalid JSON, and commands for
subscription ids this connection doesn't own are ignored. Messages must fit in a single 16 KiB receive
buffer.

Some invalid requests currently close the connection instead of producing a rejection message: an unknown
name in `fields`, or `"type": null`. See
[system-design.md § Known issues](system-design.md#known-issues).

### `subscribe`

```json
{
  "type": "subscribe",
  "collectionId": "trades",
  "sortColumn": "price",
  "sortAscending": false,
  "filters": [{ "field": "symbol", "operator": "eq", "value": "AAPL" }],
  "fields": ["symbol", "price"],
  "startIndex": 0,
  "pageSize": 50,
  "sendSnapshot": true,
  "messageFormat": "json"
}
```

| Property | Default | Notes |
|---|---|---|
| `collectionId` | required | The collection must already exist, or the subscribe is rejected. |
| `sortColumn` | natural (arrival) order | An unknown column falls back to primary-key order. |
| `sortAscending` | `true` | |
| `filters` | none | All filters must match (AND). Operators: `eq`, `notEq`, `gt`, `gte`, `lt`, `lte`, `contains` (string fields only). Range operators compare typed values for typed fields. Filters on unknown fields are ignored, and an unknown operator is treated as `eq`. |
| `fieldPresetId` | none | Id of a filter preset registered through the engine API (`CreateFilterPresetCommand`). The preset's filters are combined with `filters`. No transport exposes preset registration yet. |
| `fields` | all fields | Projection. `[]` means the key only. The key is always included. An unknown field name fails the subscribe. |
| `startIndex` | `0` | Absolute position of the first row in the view. |
| `pageSize` | unbounded | Omitting it subscribes to the whole (filtered) view from `startIndex` onward. |
| `sendSnapshot` | `true` | `false` skips the initial snapshot. |
| `messageFormat` | `compact` | `compact` or `json`. |

The server assigns the `subscriptionId` and returns it in `subscriptionAccepted`. Ids are unique per
connection.

### `setviewport`

```json
{ "type": "setviewport", "subscriptionId": 1, "startIndex": 200, "pageSize": 50, "snapshotMode": "delta" }
```

Moves or resizes the viewport. `snapshotMode` is `delta` (the default: send only rows the client doesn't
have yet), `full`, or `no`. The legacy `sendSnapshot: true|false` maps to `full|no`.

### `updateview`

```json
{ "type": "updateview", "subscriptionId": 1, "sortColumn": "quantity", "sortAscending": true,
  "filters": [], "fields": null, "startIndex": 0, "pageSize": 100, "snapshotMode": "full" }
```

Any subset of `startIndex`, `pageSize`, `sortColumn`, `sortAscending`, `filters` and `fields`. Omitted or
`null` properties keep their current value. Unlike in `subscribe`, `fields: []` here resets the projection
to all fields. If sort, filters or projection change, the subscription is moved to the new view and a
snapshot is sent (unless `snapshotMode` is `no`). If only the viewport changes, it behaves like
`setviewport`.

### `unsubscribe`

```json
{ "type": "unsubscribe", "subscriptionId": 1 }
```

## Server to client

### Positions

- Snapshot rows carry an absolute `rowNumber` within the view.
- Live deltas (`rowInsert`, `rowUpdate`, `rowRemove`, `rowReplace`) carry positions **relative to the
  subscriber's viewport start**. Position `0` is the row at `startIndex`.
- A finite viewport keeps its size. When a row enters, the row pushed past the bottom edge leaves in the
  same `rowReplace`, and vice versa.
- Apply deltas in the order received. Each position is valid against the client state produced by all
  earlier messages.

### Message types

| JSON `type` | Compact tag | Sent when |
|---|---|---|
| `subscriptionAccepted` | `A` | A `subscribe` succeeded. For the initial snapshot this replaces `snapshotStart`. |
| `subscriptionRejected` | `ERR` | A `subscribe` failed. Terminal for that subscription id. |
| `updateRejected` | `UERR` | A `setviewport`/`updateview` was refused. The subscription stays active. |
| `snapshotStart` | `P` | A snapshot caused by `setviewport`/`updateview` begins. |
| `snapshotRow` | `S` | One snapshot row. |
| `eos` | `EOS` | The current snapshot is complete. |
| `rowInsert` | `I` | A row entered the viewport. |
| `rowUpdate` | `U` | Visible fields of a row in the viewport changed. |
| `rowRemove` | `D` | A row left the viewport. |
| `rowReplace` | `R` | One row left and another entered (or the same row moved) in a single step. |

Snapshot stream shapes:

```text
initial subscribe:        subscriptionAccepted(snapshotFollows=true)  snapshotRow*  eos
setviewport / updateview: snapshotStart  snapshotRow*  eos           (may repeat twice for a two-sided expansion)
```

Live deltas that the engine produces while a snapshot is being delivered are held back and sent right
after its `eos`.

## JSON format

Every JSON message has `type` and `subscriptionId`. Field values are always strings or `null`.

```json
{"type":"subscriptionAccepted","subscriptionId":1,"snapshotFollows":true,"startIndex":0,"totalCount":2,"fields":["symbol","price"]}
{"type":"subscriptionRejected","subscriptionId":1,"reason":"collection_not_found","message":"Collection 'x' does not exist."}
{"type":"updateRejected","subscriptionId":1,"reason":"sorting_not_enabled","message":"..."}
{"type":"snapshotStart","subscriptionId":1,"startIndex":200,"totalCount":1000,"isPartial":true,"fields":["symbol","price"]}
{"type":"snapshotRow","subscriptionId":1,"rowNumber":200,"row":{"key":"t-1","symbol":"AAPL","price":"150.25"}}
{"type":"eos","subscriptionId":1}
{"type":"rowInsert","subscriptionId":1,"position":3,"row":{"key":"t-9","symbol":"NVDA","price":"900"}}
{"type":"rowUpdate","subscriptionId":1,"rowId":"t-1","position":0,"changedFields":{"price":"151.00"}}
{"type":"rowRemove","subscriptionId":1,"rowId":"t-2","position":49}
{"type":"rowReplace","subscriptionId":1,"removedRowId":"t-2","removePosition":49,"insertPosition":0,"row":{"key":"t-9","symbol":"NVDA","price":"900"}}
```

`fields` in `subscriptionAccepted`/`snapshotStart` lists the projected field names without `key`. `row`
objects include `key` plus the projected fields.

## Compact format

Tokens are separated by `|`. Field values are written in the order of the `fields` list announced in
`A`/`P` (key excluded), after the explicit key token.

| Token | Meaning |
|---|---|
| `\|`, `\\`, `\^`, `\~` | Escaped literal `\|`, `\`, `^`, `~` inside a value |
| `~` | `null` |
| *(empty)* | empty string |
| `^N` | In `U` frames only: skip `N` unchanged fields |

| Frame | Layout |
|---|---|
| Accepted | `A\|subId\|snapshotFollows(1 or empty)\|startIndex\|totalCount\|field1\|field2…` |
| Subscribe rejected | `ERR\|subId\|reason\|message` |
| Update rejected | `UERR\|subId\|reason\|message` |
| Snapshot start | `P\|subId\|startIndex\|totalCount[\|1 if partial]\|field1\|field2…` |
| Snapshot row | `S\|subId\|rowNumber\|key\|v1\|v2…` |
| End of snapshot | `EOS\|subId` |
| Insert | `I\|subId\|key\|position\|v1\|v2…` |
| Update | `U\|subId\|rowId\|position\|t1\|t2…` where each `t` is a value or `^N` |
| Remove | `D\|subId\|rowId\|position` |
| Replace | `R\|subId\|removedRowId\|removePosition\|insertPosition\|key\|v1\|v2…` |

Example: a view projecting `symbol|price|quantity` where only `price` changed:

```text
U|1|t-1|0|^1|500.00|^1
```

### Known compact-format defects

These are current behaviors to be aware of when writing a compact client. They are tracked as bugs:

- When no snapshot follows (`sendSnapshot: false`), the `A` frame contains an extra empty token
  (`A|1|||0|42|…`), which shifts `startIndex`, `totalCount` and the field list by one position. Use JSON,
  or subscribe with a snapshot, until this is fixed.
- Characters outside the Basic Multilingual Plane (for example emoji) are encoded one UTF-16 code unit at a
  time and arrive as two `U+FFFD` replacement characters. The JSON format is unaffected.
- A key-only projection (`fields: []`) fails to encode rows in compact format. Rows are dropped, and the
  error is only logged on the server. The JSON format is unaffected.
- In `P`, the partial marker `1` can't be told apart from a field literally named `1`.
