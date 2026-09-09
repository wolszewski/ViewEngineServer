import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ColDef } from 'ag-grid-community';
import type { GridApi } from 'ag-grid-community';
import {
    latencyWindowSize,
    pushModeDataAdapters,
    pushModeItemNames,
    snapshotAddGraceMs,
    subscribedFields,
    subscribedFieldSet,
    type LatencySummary,
    type PushMode,
    type RowData,
    type SnapshotStats
} from './tradesShared';

declare const LightstreamerClient: any;
declare const Subscription: any;

interface CountAnim {
    from: number;
    to: number;
    current: number;
    startedAt: number;
    durationMs: number;
}

const newCountAnim = (): CountAnim => ({ from: 0, to: 0, current: 0, startedAt: 0, durationMs: 0 });

// Bounded well under `snapshotAddGraceMs` (300ms) so a big last-jump animation always has time to
// visually finish counting up before finalizeSnapshot flips the display over to the final
// "snapshot N rows" summary line.
const countAnimMinMs = 120;
const countAnimMaxMs = 260;
const countAnimMsPerUnit = 0.03;

function stepCountAnim(anim: CountAnim, target: number): number {
    if (target !== anim.to) {
        const jump = Math.abs(target - anim.current);
        anim.from = anim.current;
        anim.to = target;
        anim.durationMs = Math.min(countAnimMaxMs, Math.max(countAnimMinMs, jump * countAnimMsPerUnit));
        anim.startedAt = performance.now();
    }

    const elapsed = performance.now() - anim.startedAt;
    const progress = anim.to === anim.from ? 1 : Math.min(1, elapsed / anim.durationMs);
    anim.current = Math.round(anim.from + (anim.to - anim.from) * progress);
    return anim.current;
}

export interface TradesSubscriptionOptions {
    lsUrl: string;
    pushMode: PushMode;
    snapshotTimeoutSeconds: number;
    gridVisible: boolean;
}

export interface TradesSubscriptionApi {
    status: string;
    isConnected: boolean;
    isLoadingSnapshot: boolean;
    snapshotStats: SnapshotStats | null;
    latencySummary: LatencySummary;
    liveSnapshotRowCount: number;
    liveSnapshotUpdateCount: number;
    trackLiveSnapshotCount: boolean;
    setTrackLiveSnapshotCount: (value: boolean) => void;
    columnDefs: ColDef<RowData>[];
    defaultColDef: ColDef<RowData>;
    gridApiRef: React.MutableRefObject<GridApi<RowData> | null>;
    connect: () => void;
    disconnect: () => void;
}

/**
 * Owns the Lightstreamer client/subscription lifecycle, the row caches used to coordinate the
 * initial COMMAND snapshot with live ADD/UPDATE/DELETE traffic, and the derived stats (snapshot
 * timing, live-loaded-count, update latency) shown by the UI.
 */
export function useTradesSubscription(options: TradesSubscriptionOptions): TradesSubscriptionApi {
    const { lsUrl, pushMode, snapshotTimeoutSeconds, gridVisible } = options;

    const [status, setStatus] = useState('Disconnected');
    const [isConnected, setIsConnected] = useState(false);
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [snapshotStats, setSnapshotStats] = useState<SnapshotStats | null>(null);
    const [latencySummary, setLatencySummary] = useState<LatencySummary>({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    const [trackLiveSnapshotCount, setTrackLiveSnapshotCount] = useState(true);
    const [liveSnapshotRowCount, setLiveSnapshotRowCount] = useState(0);
    const [liveSnapshotUpdateCount, setLiveSnapshotUpdateCount] = useState(0);
    // Raw counter of UPDATE events (field changes for a row not yet ADDed, or a merge into an
    // already-ADDed row) observed while the snapshot is still loading - `liveSnapshotUpdateCount`
    // eases toward this the same way `liveSnapshotRowCount` eases toward the ADD count.
    const snapshotUpdateCountRef = useRef(0);
    // The real COMMAND snapshot for a large item list typically arrives across only a handful of
    // network frames (each fully parsed and dispatched synchronously by the Lightstreamer client),
    // so the true received-count ref can jump by thousands between two animation frames - the
    // browser never gets a chance to paint mid-burst. This tracks a displayed value that eases
    // toward that true count over a short, fixed duration instead of jumping straight to it, so
    // the counter still reads as continuously "live" regardless of how chunky the real delivery is.
    const liveCountAnimRef = useRef(newCountAnim());
    const liveUpdateCountAnimRef = useRef(newCountAnim());

    const latencyAccRef = useRef({ maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [] as number[], recentTotalMs: 0 });
    const autoConnectHandleRef = useRef<number | null>(null);
    const gridVisibleRef = useRef(gridVisible);
    useEffect(() => {
        gridVisibleRef.current = gridVisible;
    }, [gridVisible]);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const clientRef = useRef<any>(null);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    // `snapshotBufferRef`'s key set doubles as "rows received so far" - every ADD/DELETE that
    // touches it (in both push modes) always keeps it in exact lockstep with what used to be a
    // separate `snapshotRowsReceivedRef` Set, so its `.size` is used directly instead of
    // maintaining a redundant second collection on the same per-row hot path.
    const snapshotBufferRef = useRef<Map<string, RowData>>(new Map());
    const snapshotCommandKeysRef = useRef<Set<string>>(new Set());
    const snapshotPendingKeysRef = useRef<Set<string>>(new Set());
    const pendingPreSnapshotUpdatesRef = useRef<Map<string, RowData>>(new Map());
    const pendingLiveAddKeysRef = useRef<Set<string>>(new Set());
    const pendingLiveAddUpdatesRef = useRef<Map<string, RowData>>(new Map());
    const commandSnapshotEndedRef = useRef(false);
    const snapshotCompleteRef = useRef(false);
    const subscribeTimeRef = useRef<number | null>(null);
    const firstUpdateTimeRef = useRef<number | null>(null);
    const snapshotCompletionTimeRef = useRef<number | null>(null);
    const snapshotTimeoutHandleRef = useRef<number | null>(null);
    const snapshotFinalizeGraceHandleRef = useRef<number | null>(null);
    // Latency is only meaningful once the initial snapshot is fully loaded - recording it earlier would
    // mix in stale snapshot timestamps and skew the rolling average high right after connecting.
    const hasSnapshotLoadedRef = useRef(false);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        resizable: true,
        enableCellChangeFlash: true
    }), []);
    const columnDefs = useMemo<ColDef<RowData>[]>(
        () => subscribedFields.map((field) => ({ field, headerName: field })),
        []
    );

    const recordLatency = useCallback((updatedDate: string | null | undefined) => {
        if (!hasSnapshotLoadedRef.current || !updatedDate) { return; }
        const timestamp = Date.parse(updatedDate);
        if (!Number.isFinite(timestamp)) { return; }
        const latencyMs = Date.now() - timestamp;
        const acc = latencyAccRef.current;
        const recentLatencies = [...acc.recentLatencies, latencyMs];
        let recentTotalMs = acc.recentTotalMs + latencyMs;
        if (recentLatencies.length > latencyWindowSize) {
            recentTotalMs -= recentLatencies.shift() ?? 0;
        }
        const nextCount = recentLatencies.length;
        const nextAvg = nextCount === 0 ? 0 : recentTotalMs / nextCount;
        latencyAccRef.current = {
            sampleCount: nextCount,
            maxMs: Math.max(acc.maxMs, latencyMs),
            avgMs: nextAvg,
            recentLatencies,
            recentTotalMs
        };
    }, []);

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        snapshotBufferRef.current.clear();
        snapshotCommandKeysRef.current.clear();
        snapshotPendingKeysRef.current.clear();
        pendingPreSnapshotUpdatesRef.current.clear();
        pendingLiveAddKeysRef.current.clear();
        pendingLiveAddUpdatesRef.current.clear();
        commandSnapshotEndedRef.current = false;
        snapshotCompleteRef.current = false;
        hasSnapshotLoadedRef.current = false;
        subscribeTimeRef.current = null;
        firstUpdateTimeRef.current = null;
        snapshotCompletionTimeRef.current = null;
        latencyAccRef.current = { maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [], recentTotalMs: 0 };
        setSnapshotStats(null);
        setLatencySummary({ maxMs: 0, avgMs: 0, sampleCount: 0 });
        setLiveSnapshotRowCount(0);
        setLiveSnapshotUpdateCount(0);
        snapshotUpdateCountRef.current = 0;
        liveCountAnimRef.current = newCountAnim();
        liveUpdateCountAnimRef.current = newCountAnim();
        if (snapshotTimeoutHandleRef.current !== null) {
            clearTimeout(snapshotTimeoutHandleRef.current);
            snapshotTimeoutHandleRef.current = null;
        }
        if (snapshotFinalizeGraceHandleRef.current !== null) {
            clearTimeout(snapshotFinalizeGraceHandleRef.current);
            snapshotFinalizeGraceHandleRef.current = null;
        }
        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', []);
        }
    }, []);

    const finalizeSnapshot = useCallback((forced: boolean = false) => {
        if (snapshotCompleteRef.current) { return; }
        snapshotCompleteRef.current = true;

        if (snapshotTimeoutHandleRef.current !== null) {
            clearTimeout(snapshotTimeoutHandleRef.current);
            snapshotTimeoutHandleRef.current = null;
        }

        if (snapshotFinalizeGraceHandleRef.current !== null) {
            clearTimeout(snapshotFinalizeGraceHandleRef.current);
            snapshotFinalizeGraceHandleRef.current = null;
        }

        const completionTime = snapshotCompletionTimeRef.current ?? performance.now();
        const firstUpdateTime = firstUpdateTimeRef.current ?? completionTime;
        const waitMs = subscribeTimeRef.current !== null
            ? firstUpdateTime - subscribeTimeRef.current
            : 0;
        const transferMs = Math.max(0, completionTime - firstUpdateTime);
        subscribeTimeRef.current = null;
        firstUpdateTimeRef.current = null;
        snapshotCompletionTimeRef.current = null;

        const rows = Array.from(snapshotBufferRef.current.values());
        snapshotBufferRef.current.clear();
        const rowCount = rows.length;
        for (const row of rows) {
            rowsByIdRef.current.set(row.key as string, row);
        }

        setIsLoadingSnapshot(false);
        hasSnapshotLoadedRef.current = true;

        if (gridApiRef.current && gridVisibleRef.current) {
            gridApiRef.current.setGridOption('rowData', rows);
            // Measured the same way as the WebHost example UIs: two nested rAFs bracket the
            // browser's actual paint of the just-applied rowData, so renderMs reflects real
            // render time rather than just the synchronous setGridOption call.
            const renderStart = performance.now();
            window.requestAnimationFrame(() => {
                window.requestAnimationFrame(() => {
                    setSnapshotStats({ rowCount, waitMs, transferMs, renderMs: performance.now() - renderStart, incomplete: forced });
                });
            });
        } else {
            setSnapshotStats({ rowCount, waitMs, transferMs, renderMs: 0, incomplete: forced });
        }
    }, []);

    const tryFinalizeSnapshot = useCallback(() => {
        if (snapshotCompleteRef.current || !commandSnapshotEndedRef.current) {
            return;
        }

        if (snapshotPendingKeysRef.current.size > 0) {
            snapshotCompletionTimeRef.current = null;
            if (snapshotFinalizeGraceHandleRef.current !== null) {
                clearTimeout(snapshotFinalizeGraceHandleRef.current);
                snapshotFinalizeGraceHandleRef.current = null;
            }
            return;
        }

        if (snapshotCompletionTimeRef.current === null) {
            snapshotCompletionTimeRef.current = performance.now();
        }

        if (snapshotFinalizeGraceHandleRef.current === null) {
            snapshotFinalizeGraceHandleRef.current = window.setTimeout(() => {
                snapshotFinalizeGraceHandleRef.current = null;
                finalizeSnapshot();
            }, snapshotAddGraceMs);
        }
    }, [finalizeSnapshot]);

    // Pure command mode: the single COMMAND item already carries every subscribed field on
    // each ADD/UPDATE, so there is no second-level subscription/snapshot to coordinate - the
    // row is always fully known from the update that announces it.
    const handlePureItemUpdate = useCallback((update: any) => {
        if (firstUpdateTimeRef.current === null) {
            firstUpdateTimeRef.current = performance.now();
        }

        const command = update.getValue('command');
        const rowKey: string | null = update.getValue('key');
        if (!rowKey) {
            return;
        }

        if (command === 'DELETE') {
            if (!snapshotCompleteRef.current) {
                snapshotBufferRef.current.delete(rowKey);
            } else {
                const existing = rowsByIdRef.current.get(rowKey);
                rowsByIdRef.current.delete(rowKey);
                if (existing && gridApiRef.current && gridVisibleRef.current) {
                    gridApiRef.current.applyTransaction({ remove: [existing] });
                }
            }

            tryFinalizeSnapshot();
            return;
        }

        if (!snapshotCompleteRef.current) {
            // Unlike twoLevel mode, a pure-mode row is fully known the instant its own ADD
            // arrives (no second-level snapshot to wait for), so there's no need to track a
            // separate "pending keys" set here - `tryFinalizeSnapshot`'s pending-keys check
            // simply stays trivially satisfied (always empty) for this mode.
            if (command !== 'ADD') {
                snapshotUpdateCountRef.current += 1;
            }

            const row: RowData = { key: rowKey };
            for (const field of subscribedFields) {
                row[field] = update.getValue(field);
            }

            snapshotBufferRef.current.set(rowKey, row);
            tryFinalizeSnapshot();
            return;
        }

        if (!gridVisibleRef.current) {
            return;
        }

        if (command === 'ADD') {
            const row: RowData = { key: rowKey };
            for (const field of subscribedFields) {
                row[field] = update.getValue(field);
            }

            rowsByIdRef.current.set(rowKey, row);
            if (gridApiRef.current) {
                gridApiRef.current.applyTransaction({ add: [row] });
            }

            return;
        }

        const existing = rowsByIdRef.current.get(rowKey);
        const changedFields: RowData = {};
        update.forEachChangedField((fieldName: string, _pos: number, value: string | null) => {
            if (subscribedFieldSet.has(fieldName)) {
                changedFields[fieldName] = value;
            }
        });

        let updated: RowData;
        if (existing) {
            updated = { ...existing, ...changedFields };
        } else {
            updated = { key: rowKey };
            for (const field of subscribedFields) {
                updated[field] = update.getValue(field);
            }
        }

        rowsByIdRef.current.set(rowKey, updated);
        recordLatency(updated.updatedDate);

        if (!gridApiRef.current) {
            return;
        }

        if (existing) {
            gridApiRef.current.applyTransaction({ update: [updated] });
            const rowNode = gridApiRef.current.getRowNode(rowKey);
            if (rowNode) {
                gridApiRef.current.flashCells({
                    rowNodes: [rowNode],
                    columns: Object.keys(changedFields),
                    flashDuration: 1,
                    fadeDuration: 1_000
                });
            }
        } else {
            gridApiRef.current.applyTransaction({ add: [updated] });
        }
    }, [recordLatency, tryFinalizeSnapshot]);

    const connect = useCallback(() => {
        clearState();
        setIsConnected(true);
        setIsLoadingSnapshot(true);
        subscribeTimeRef.current = performance.now();

        const timeoutMs = Math.max(1, snapshotTimeoutSeconds) * 1000;
        snapshotTimeoutHandleRef.current = window.setTimeout(() => finalizeSnapshot(true), timeoutMs);

        const listItemName = pushModeItemNames[pushMode];
        const subscription = pushMode === 'twoLevel'
            ? new Subscription('COMMAND', [listItemName], ['key', 'command'])
            : new Subscription('COMMAND', [listItemName], ['key', 'command', ...subscribedFields]);
        subscription.setDataAdapter(pushModeDataAdapters[pushMode]);
        if (pushMode === 'twoLevel') {
            subscription.setCommandSecondLevelDataAdapter('trades-merge-adapter');
            subscription.setCommandSecondLevelFields(subscribedFields);
        }
        subscription.setRequestedSnapshot('yes');

        subscription.addListener({
            onSubscription() {
                setStatus('Subscribed');
            },
            onUnsubscription() {
                setStatus('Unsubscribed');
            },
            onSubscriptionError(code: number, message: string) {
                setStatus(`Subscription error ${code}: ${message}`);
            },
            onCommandSecondLevelSubscriptionError(code: number, message: string, key: string) {
                setStatus(`Second-level subscription error for ${key}: ${code} ${message}`);
            },
            onItemUpdate: pushMode === 'pure' ? handlePureItemUpdate : (update: any) => {
                if (firstUpdateTimeRef.current === null) {
                    firstUpdateTimeRef.current = performance.now();
                }

                const itemName: string = update.getItemName();
                const isSnapshot = update.isSnapshot();
                const command = update.getValue('command');
                const commandKey = update.getValue('key');
                const rowKey = commandKey ?? itemName;
                const hasRowPayload = subscribedFields.some((field) => update.getValue(field) !== null);

                if (itemName === listItemName) {
                    if (commandKey) {
                        if (!snapshotCompleteRef.current && commandSnapshotEndedRef.current && isSnapshot) {
                            snapshotCompletionTimeRef.current = null;
                            if (snapshotFinalizeGraceHandleRef.current !== null) {
                                clearTimeout(snapshotFinalizeGraceHandleRef.current);
                                snapshotFinalizeGraceHandleRef.current = null;
                            }
                        }

                        if (!snapshotCompleteRef.current && isSnapshot) {
                            if (command === 'ADD') {
                                if (!snapshotCommandKeysRef.current.has(commandKey)) {
                                    snapshotCommandKeysRef.current.add(commandKey);
                                    snapshotPendingKeysRef.current.add(commandKey);
                                }
                            } else if (command === 'DELETE') {
                                snapshotCommandKeysRef.current.delete(commandKey);
                                snapshotPendingKeysRef.current.delete(commandKey);
                                snapshotBufferRef.current.delete(commandKey);
                                pendingPreSnapshotUpdatesRef.current.delete(commandKey);
                            }
                        } else if (command === 'DELETE') {
                            pendingLiveAddKeysRef.current.delete(commandKey);
                            pendingLiveAddUpdatesRef.current.delete(commandKey);
                            const existing = rowsByIdRef.current.get(commandKey);
                            rowsByIdRef.current.delete(commandKey);
                            if (existing && gridApiRef.current && gridVisibleRef.current) {
                                gridApiRef.current.applyTransaction({ remove: [existing] });
                            }
                        } else if (command === 'ADD') {
                            pendingLiveAddKeysRef.current.add(commandKey);
                        }
                    }
                }

                if (!hasRowPayload) {
                    tryFinalizeSnapshot();
                    return;
                }

                if (!snapshotCompleteRef.current) {
                    if (isSnapshot) {
                        const row: RowData = { key: rowKey };
                        for (const field of subscribedFields) {
                            row[field] = update.getValue(field);
                        }

                        const pendingUpdate = pendingPreSnapshotUpdatesRef.current.get(rowKey);
                        if (pendingUpdate) {
                            Object.assign(row, pendingUpdate);
                            pendingPreSnapshotUpdatesRef.current.delete(rowKey);
                        }

                        snapshotBufferRef.current.set(rowKey, row);
                        snapshotPendingKeysRef.current.delete(rowKey);
                    } else {
                        snapshotUpdateCountRef.current += 1;
                        const changedFields: RowData = {};
                        update.forEachChangedField((fieldName: string, _pos: number, value: string | null) => {
                            if (subscribedFieldSet.has(fieldName)) {
                                changedFields[fieldName] = value;
                            }
                        });

                        const existingSnapshotRow = snapshotBufferRef.current.get(rowKey);
                        if (existingSnapshotRow) {
                            snapshotBufferRef.current.set(rowKey, { ...existingSnapshotRow, ...changedFields });
                        } else {
                            const existingPendingUpdate = pendingPreSnapshotUpdatesRef.current.get(rowKey) ?? { key: rowKey };
                            pendingPreSnapshotUpdatesRef.current.set(rowKey, { ...existingPendingUpdate, ...changedFields });
                        }
                    }

                    tryFinalizeSnapshot();
                    return;
                }

                if (!gridVisibleRef.current) {
                    return;
                }

                const existing = rowsByIdRef.current.get(rowKey);
                if (!existing) {
                    if (!pendingLiveAddKeysRef.current.has(rowKey)) {
                        return;
                    }

                    if (!isSnapshot) {
                        // Not the item's own snapshot yet - buffer the changed fields instead of
                        // building the row now, otherwise the row would be created with only
                        // these few fields populated and the rest left blank.
                        const changedFields: RowData = {};
                        update.forEachChangedField((fieldName: string, _pos: number, value: string | null) => {
                            if (subscribedFieldSet.has(fieldName)) {
                                changedFields[fieldName] = value;
                            }
                        });
                        const existingPending = pendingLiveAddUpdatesRef.current.get(rowKey) ?? { key: rowKey };
                        pendingLiveAddUpdatesRef.current.set(rowKey, { ...existingPending, ...changedFields });
                        return;
                    }

                    const row: RowData = { key: rowKey };
                    for (const field of subscribedFields) {
                        row[field] = update.getValue(field);
                    }

                    const pendingUpdate = pendingLiveAddUpdatesRef.current.get(rowKey);
                    if (pendingUpdate) {
                        Object.assign(row, pendingUpdate);
                        pendingLiveAddUpdatesRef.current.delete(rowKey);
                    }

                    rowsByIdRef.current.set(rowKey, row);
                    pendingLiveAddKeysRef.current.delete(rowKey);
                    // Not recorded as a latency sample - a row's first appearance (whether from the
                    // initial snapshot or its own second-level subscribe) includes subscription setup
                    // overhead, not steady-state field-update latency.

                    if (gridApiRef.current && gridVisibleRef.current) {
                        gridApiRef.current.applyTransaction({ add: [row] });
                    }
                    return;
                }

                const changedFields: RowData = {};
                update.forEachChangedField((fieldName: string, _pos: number, value: string | null) => {
                    if (subscribedFieldSet.has(fieldName)) {
                        changedFields[fieldName] = value;
                    }
                });
                const updated: RowData = { ...existing, ...changedFields };
                rowsByIdRef.current.set(rowKey, updated);

                recordLatency(updated.updatedDate);

                if (!gridApiRef.current || !gridVisibleRef.current) { return; }

                gridApiRef.current.applyTransaction({ update: [updated] });
                const rowNode = gridApiRef.current.getRowNode(rowKey);
                if (rowNode) {
                    gridApiRef.current.flashCells({
                        rowNodes: [rowNode],
                        columns: Object.keys(changedFields),
                        flashDuration: 1,
                        fadeDuration: 1_000
                    });
                }
            },
            onEndOfSnapshot(itemName: string, _itemPos: number) {
                if (snapshotCompleteRef.current || itemName !== listItemName) {
                    return;
                }

                commandSnapshotEndedRef.current = true;
                tryFinalizeSnapshot();
            }
        });

        const lsClient = new LightstreamerClient(lsUrl, 'TRADES');
        lsClient.addListener({
            onStatusChange(newStatus: string) {
                setStatus(newStatus);
            }
        });
        lsClient.subscribe(subscription);
        lsClient.connect();
        clientRef.current = lsClient;
    }, [clearState, finalizeSnapshot, handlePureItemUpdate, lsUrl, pushMode, recordLatency, snapshotTimeoutSeconds, tryFinalizeSnapshot]);

    const disconnect = useCallback(() => {
        if (clientRef.current) {
            clientRef.current.disconnect();
            clientRef.current = null;
        }
        clearState();
        setIsConnected(false);
        setIsLoadingSnapshot(false);
        setStatus('Disconnected');
    }, [clearState]);

    // Kept current via an effect so the mount-only auto-connect effect below doesn't need
    // `connect` in its dependency array - `connect` is redefined whenever any of its inputs
    // (lsUrl, snapshotTimeoutSeconds, ...) change, and depending on it directly would
    // re-arm the auto-connect timer (and reconnect) on every keystroke in those fields.
    const connectRef = useRef(connect);
    useEffect(() => {
        connectRef.current = connect;
    }, [connect]);

    useEffect(() => {
        const handle = window.setInterval(() => {
            setLatencySummary({ ...latencyAccRef.current });
        }, 500);
        return () => clearInterval(handle);
    }, []);

    // Snapshot rows arrive on `snapshotBufferRef` (ADDs) and `snapshotUpdateCountRef`
    // (UPDATEs merged into rows before their own snapshot commits) as they're registered, but on
    // localhost/demo setups the entire snapshot (even 10k+ rows) can be handed to the Lightstreamer
    // Server in a delay-free loop server-side, which then coalesces the wire delivery into just a
    // couple of WebSocket frames flushed close together. A *fixed* short ease duration doesn't help
    // there: the whole load can finish before enough animation frames elapse to look like counting,
    // so the display just settles at each real jump almost instantly. Instead, scale the ease
    // duration by how large the jump is - a bigger jump takes proportionally longer to visually
    // count through (bounded so huge datasets don't feel sluggish) - so the counters read as
    // continuously live regardless of how bursty the real delivery happens to be.
    useEffect(() => {
        if (!isLoadingSnapshot || !trackLiveSnapshotCount) {
            return undefined;
        }

        let rafHandle = 0;
        const tick = () => {
            setLiveSnapshotRowCount(stepCountAnim(liveCountAnimRef.current, snapshotBufferRef.current.size));
            setLiveSnapshotUpdateCount(stepCountAnim(liveUpdateCountAnimRef.current, snapshotUpdateCountRef.current));
            rafHandle = window.requestAnimationFrame(tick);
        };
        rafHandle = window.requestAnimationFrame(tick);
        return () => window.cancelAnimationFrame(rafHandle);
    }, [isLoadingSnapshot, trackLiveSnapshotCount]);

    useEffect(() => {
        if (clientRef.current) {
            return undefined;
        }

        autoConnectHandleRef.current = window.setTimeout(() => {
            if (!clientRef.current) {
                connectRef.current();
            }
        }, 500);

        return () => {
            if (autoConnectHandleRef.current !== null) {
                clearTimeout(autoConnectHandleRef.current);
                autoConnectHandleRef.current = null;
            }
        };
        // Intentionally mount-only (see connectRef comment above) - must not depend on `connect`.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    useEffect(() => {
        return () => {
            clientRef.current?.disconnect();
        };
    }, []);

    return {
        status,
        isConnected,
        isLoadingSnapshot,
        snapshotStats,
        latencySummary,
        liveSnapshotRowCount,
        liveSnapshotUpdateCount,
        trackLiveSnapshotCount,
        setTrackLiveSnapshotCount,
        columnDefs,
        defaultColDef,
        gridApiRef,
        connect,
        disconnect
    };
}
