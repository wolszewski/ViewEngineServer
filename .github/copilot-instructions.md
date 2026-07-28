# Copilot Instructions

## Style guide

1. If an interface has exactly one implementation in the solution, keep the interface in the same file as that implementation.
2. Keep the interface above the implementing class in that file.
3. If an interface has multiple implementations (including test doubles), keep it in a dedicated interface file.
4. Keep comments minimal.
5. Remove XML doc comments unless they are required for externally consumed APIs.
6. Keep only comments that explain non-obvious behavior, invariants, concurrency constraints, or performance optimizations.
7. Do not add section-divider or decorative comments.

## Current application in this repository

- `IViewEngine` and `ViewEngine` are colocated in `Core/Engine/ViewEngine.cs`.
- `ICollectionStore` and `CollectionStore` are colocated in `Core/Storage/CollectionStore.cs`.
- `IOutboundPublisher` stays in `Core/Publishing/IOutboundPublisher.cs` because it has multiple implementations (`WebSocketOutboundPublisher`, `CapturingPublisher`).
