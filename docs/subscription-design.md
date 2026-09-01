# Subscription contract

`subscriptionId` is a stable identity scoped to one WebSocket connection and one collection.

## Rules

1. `subscribe` creates a new subscription id on the server for that connection.
2. A subscription id is bound to exactly one `collectionId` for its lifetime.
3. Reusing the same `(connectionId, subscriptionId)` for another collection is rejected.
4. `updateview`/`setviewport` can change viewport and view settings, but not the collection binding.
5. To switch collection or preset context, create a new subscription.

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

## Subscribing before the collection exists

`subscribe` is always accepted at the WebSocket layer (an `accepted` frame is sent with the assigned `subscriptionId`), even if the target `collectionId` does not exist yet.

- The `accepted` frame's `snapshotFollows` field is tri-state, not a boolean: `"none"` (no snapshot is coming), `"immediate"` (a snapshot is being sent right now), or `"pending"` (the collection doesn't exist yet, so the snapshot is deferred).
- If the collection does not exist yet, `accepted` reports `snapshotFollows: "pending"` and `totalCount: -1`, and no snapshot frames follow immediately.
- The server remembers the pending subscribe request (per connection/subscription id).
- When the collection is later created (first `createCollection`/ingest for that `collectionId`), the server automatically resumes any pending subscriptions for it and pushes the real snapshot (`snapshotStart`/`snapshotRow`/`eos`) to the client without requiring any further client action.
- The client does not need to poll or resend `updateview` to detect collection creation; it should treat `snapshotFollows: "pending"` as "waiting for data" and simply wait for the pushed snapshot.
- Compact frame shape: `A|subscriptionId|snapshotFollows|startIndex|totalCount|field1|field2|...`, where `snapshotFollows` is encoded as a single digit: `0` = none, `1` = immediate, `2` = pending.

## Outbound snapshot contract

- snapshot delivery is streamed as:
  1. `snapshotStart`
  2. zero or more `snapshotRow` events
  3. `eos`
- both full and partial snapshots use the same shape.
- every `snapshotRow` includes an explicit row number so clients can place rows correctly during partial viewport expansion.
- compact snapshot rows are `S|subscriptionId|rowNumber|key|...`
- JSON snapshot rows include `rowNumber` next to `row`
