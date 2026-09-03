# Subscription contract

`subscriptionId` is a stable identity scoped to one WebSocket connection and one collection.

## Rules

1. `subscribe` creates a new subscription id on the server for that connection.
2. A subscription id is bound to exactly one `collectionId` for its lifetime.
3. Reusing the same `(connectionId, subscriptionId)` for another collection is rejected.
4. `updateview`/`setviewport` can change viewport and view settings, but not the collection binding.
5. To switch collection or preset context, create a new subscription.
6. `subscribe` for a `collectionId` that does not exist yet is rejected immediately with a
   `subscriptionRejected` message; the client must create a new subscription (e.g. after user retry
   or reconnect) once the collection exists.

## Inbound command contract

- `subscribe`
  - `collectionId` is required.
  - `fields` behavior:
    - omitted (`null`) => all fields
    - empty array (`[]`) => primary-key-only projection
    - non-empty array => projection to listed fields; primary key is always included in snapshots
  - `sortColumn`, `sortAscending`, `filters`, `fieldPresetId`, `startIndex`, `pageSize`, `sendSnapshot`, and `messageFormat` are optional.

- `updateview`
  - requires an existing `subscriptionId`.
  - may update `startIndex`, `pageSize`, `sortColumn`, `sortAscending`, `filters`, and `fields`.
  - `fields: []` clears projection back to all fields.
- `snapshotMode` is supported with values:
  - `no`: do not force a snapshot
  - `delta`: send only the minimal snapshot rows needed to reconcile the requested viewport
  - `full`: send a full snapshot for the requested view
- `snapshotMode` defaults to `delta`.
- legacy `sendSnapshot: true|false` still maps to `full|no`.
- if `snapshotMode` is `delta` and the effective view definition is unchanged, viewport expansion sends only the uncovered range.
  - example: existing `0-200` updated to `0-400` sends rows `200-399` only.
- if `snapshotMode` is `full`, the server sends a fresh snapshot for the requested view.

- `setviewport`
- requires an existing `subscriptionId`.
- updates `startIndex`/`pageSize` only.
- `snapshotMode` defaults to `delta`.
- `unsubscribe`
  - requires an existing `subscriptionId`.
  - removes route/viewport state for that subscription.

## Reconnect behavior

On reconnect, clients subscribe again and receive a new server-assigned `subscriptionId`. A previous id is not portable across connections.

## Outbound snapshot contract

- snapshot delivery is streamed as:
  1. `snapshotStart`
  2. zero or more `snapshotRow` events
  3. `eos`
- both full and partial snapshots use the same shape.
- every `snapshotRow` includes an explicit row number so clients can place rows correctly during partial viewport expansion.
- compact snapshot rows are `S|subscriptionId|rowNumber|key|...`
- JSON snapshot rows include `rowNumber` next to `row`

## Subscription rejection contract

- if `subscribe` targets a `collectionId` that does not exist, the server sends a rejection instead of
  `subscriptionAccepted` and does not process the subscribe further (no snapshot, no viewport state).
- compact: `ERR|subscriptionId|reason|message`
- JSON: `{"type":"subscriptionRejected","subscriptionId":...,"reason":"collection_not_found","message":"..."}`
- `reason` is a stable machine-readable code (currently `collection_not_found`); `message` is a human-readable detail.
- clients must not send `updateview`/`setviewport` for a rejected subscription id; treat rejection as terminal
  for that subscription attempt and surface a clear failure state (e.g. "Subscription failed") with a way to retry/reconnect.
- collection existence is checked exactly once, atomically, at the point the engine registers/dispatches the subscribe
  (`ViewEngine._collectionRuntimes` lookup). There is no separate pre-check against the collection store, so a
  subscribe racing a concurrent `createcollection` can never be silently accepted while actually missing its runtime -
  it is either fully accepted against a registered runtime or rejected. Rejection is signalled as data (a
  `SubscriptionRejectedDelta` returned from the same lookup), not an exception, since a missing collection is an
  expected, externally-triggerable outcome (bad client input or a benign create/subscribe race), not a programming error.

