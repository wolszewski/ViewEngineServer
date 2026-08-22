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

- `setviewport`
  - requires an existing `subscriptionId`.
  - updates `startIndex`/`pageSize` only; `sendSnapshot` defaults to `false` (no snapshot), but can be set to `true` to force one.
- `unsubscribe`
  - requires an existing `subscriptionId`.
  - removes route/viewport state for that subscription.

## Reconnect behavior

On reconnect, clients subscribe again and receive a new server-assigned `subscriptionId`. A previous id is not portable across connections.
