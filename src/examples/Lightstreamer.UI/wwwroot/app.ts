import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry, type ColDef, type GridApi } from 'ag-grid-community';

ModuleRegistry.registerModules([AllCommunityModule]);

declare const LightstreamerClient: any;
declare const Subscription: any;

const normalizeLsUrl = (value: string): string => value.trim().replace(/\/+$/, '').replace(/\/lightstreamer\/?$/i, '');

// Same "grid" query-param convention as the other example UIs: absent or "1" means visible
// (the default); "0" means hidden.
function getInitialGridVisible(): boolean {
    return new URLSearchParams(window.location.search).get('grid') !== '0';
}

function syncGridVisibleParam(gridVisible: boolean): void {
    const params = new URLSearchParams(window.location.search);
    if (gridVisible) {
        params.delete('grid');
    } else {
        params.set('grid', '0');
    }

    const nextSearch = params.toString();
    window.history.replaceState(
        null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
}

const defaultLsUrl = normalizeLsUrl(
    window.location.port === '5112' ? 'http://127.0.0.1:8080' : window.location.origin
);
type PushMode = 'twoLevel' | 'pure';
// 'twoLevel' = COMMAND item carries only key/command; row fields arrive via a per-key
// second-level MERGE subscription. 'pure' = COMMAND item carries the full row fields directly
// on every ADD/UPDATE, no second-level subscription involved.
const pushModeItemNames: Record<PushMode, string> = {
    twoLevel: 'TRADES_ALL',
    pure: 'TRADES_ALL_PURE'
};
const pushModeDataAdapters: Record<PushMode, string> = {
    twoLevel: 'trades-command-adapter',
    pure: 'trades-pure-command-adapter'
};
const defaultPushMode: PushMode = 'twoLevel';

// Same "mode" query-param convention as "grid": absent or "twoLevel" means the default;
// "pure" selects pure command mode. Any other/invalid value falls back to the default.
function getInitialPushMode(): PushMode {
    const value = new URLSearchParams(window.location.search).get('mode');
    return value === 'pure' ? 'pure' : defaultPushMode;
}

function syncPushModeParam(pushMode: PushMode): void {
    const params = new URLSearchParams(window.location.search);
    if (pushMode === defaultPushMode) {
        params.delete('mode');
    } else {
        params.set('mode', pushMode);
    }

    const nextSearch = params.toString();
    window.history.replaceState(
        null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
}

const subscribedFields = [
    'tradeId', 'createdDate', 'updatedDate', 'accountId', 'quantity',
    'price', 'side', 'status', 'isAlgo', 'isManualReview', 'notional', 'variedNumber',
    ...Array.from({ length: 30 }, (_, i) => `stringField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 23 }, (_, i) => `intField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `decimalField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `enumField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `boolField${i.toString().padStart(2, '0')}`)
];
const subscribedFieldSet = new Set(subscribedFields);
const defaultSnapshotTimeoutSeconds = 10;
const snapshotAddGraceMs = 300;
const latencyWindowSize = 500;

type RowData = Record<string, string | null>;

function App(): React.ReactElement {
    const [lsUrl, setLsUrl] = useState(defaultLsUrl);
    const [pushMode, setPushMode] = useState<PushMode>(() => getInitialPushMode());
    const [status, setStatus] = useState('Disconnected');
    const [isConnected, setIsConnected] = useState(false);
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [snapshotTimeoutSeconds, setSnapshotTimeoutSeconds] = useState(defaultSnapshotTimeoutSeconds);
    const [snapshotStats, setSnapshotStats] = useState<{ rowCount: number; waitMs: number; transferMs: number; renderMs: number; incomplete: boolean } | null>(null);
    const [latencySummary, setLatencySummary] = useState({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    const latencyAccRef = useRef({ maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [] as number[], recentTotalMs: 0 });
    const autoConnectHandleRef = useRef<number | null>(null);
    const [columnDefs] = useState<ColDef<RowData>[]>(() =>
        subscribedFields.map((field) => ({ field, headerName: field }))
    );
    const [gridVisible, setGridVisible] = useState(() => getInitialGridVisible());
    const gridVisibleRef = useRef(gridVisible);
    useEffect(() => {
        syncGridVisibleParam(gridVisible);
    }, [gridVisible]);
    useEffect(() => {
        syncPushModeParam(pushMode);
    }, [pushMode]);
    const initialRowData = useMemo<RowData[]>(() => [], []);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const clientRef = useRef<any>(null);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const snapshotBufferRef = useRef<Map<string, RowData>>(new Map());
    const snapshotCommandKeysRef = useRef<Set<string>>(new Set());
    const snapshotPendingKeysRef = useRef<Set<string>>(new Set());
    const snapshotRowsReceivedRef = useRef<Set<string>>(new Set());
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
        snapshotRowsReceivedRef.current.clear();
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
        const rowCount = gridVisibleRef.current ? rows.length : snapshotRowsReceivedRef.current.size;
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
                snapshotCommandKeysRef.current.delete(rowKey);
                snapshotPendingKeysRef.current.delete(rowKey);
                snapshotRowsReceivedRef.current.delete(rowKey);
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
            if (command === 'ADD' && !snapshotCommandKeysRef.current.has(rowKey)) {
                snapshotCommandKeysRef.current.add(rowKey);
                snapshotPendingKeysRef.current.add(rowKey);
            }

            const row: RowData = { key: rowKey };
            for (const field of subscribedFields) {
                row[field] = update.getValue(field);
            }

            snapshotBufferRef.current.set(rowKey, row);
            snapshotRowsReceivedRef.current.add(rowKey);
            snapshotPendingKeysRef.current.delete(rowKey);
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
                                snapshotRowsReceivedRef.current.delete(commandKey);
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
                        snapshotRowsReceivedRef.current.add(rowKey);
                        snapshotPendingKeysRef.current.delete(rowKey);
                    } else {
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

    return React.createElement(
        React.Fragment,
        null,
        React.createElement(
            'style',
            null,
            `
            body {
                font-family: Arial, sans-serif;
                margin: 2rem;
            }
            .controls {
                display: flex;
                gap: 1rem;
                align-items: end;
                flex-wrap: wrap;
                margin-bottom: 1rem;
            }
            .control-label {
                display: flex;
                flex-direction: column;
                gap: 0.25rem;
                font-size: 0.95rem;
            }
            .control-label input[type="text"] {
                padding: 0.4rem 0.5rem;
                border: 1px solid #c7ced8;
                border-radius: 0.25rem;
                min-width: 20rem;
            }
            .status {
                margin-bottom: 1rem;
                padding: 0.75rem;
                background: #f0f6ff;
                border-radius: 0.25rem;
            }
            .ag-theme-balham {
                --ag-value-change-value-highlight-background-color: #b7f7b7;
            }
            .grid-wrapper {
                position: relative;
                width: 100%;
                height: 70vh;
            }
            .grid-loader {
                position: absolute;
                inset: 0;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                background: rgba(255, 255, 255, 0.75);
                z-index: 100;
                gap: 0.75rem;
                font-size: 1rem;
                color: #334155;
            }
            .grid-loader-spinner {
                width: 2rem;
                height: 2rem;
                border: 3px solid #cbd5e1;
                border-top-color: #3b82f6;
                border-radius: 50%;
                animation: spin 0.7s linear infinite;
            }
            @keyframes spin {
                to { transform: rotate(360deg); }
            }
            `
        ),
        React.createElement('h1', null, 'Lightstreamer PoC UI'),
        React.createElement('div', { className: 'status' }, status),
        React.createElement(
            'div',
            { className: 'status' },
            snapshotStats !== null
                ? `snapshot ${snapshotStats.rowCount.toLocaleString()} rows | `
                    + `wait ${snapshotStats.waitMs.toFixed(0)}ms | `
                    + `transfer ${snapshotStats.transferMs.toFixed(0)}ms | `
                    + `render ${snapshotStats.renderMs.toFixed(0)}ms`
                    + (snapshotStats.incomplete ? ` — incomplete (stopped after ${snapshotTimeoutSeconds}s max wait)` : '')
                : 'Snapshot: —'
        ),
        React.createElement(
            'div',
            { className: 'status' },
            `Live updates — Max latency: ${latencySummary.maxMs.toFixed(0)} ms • Avg latency (last ${latencyWindowSize}): ${latencySummary.avgMs.toFixed(0)} ms • Window samples: ${latencySummary.sampleCount}`
        ),
        React.createElement(
            'div',
            { className: 'controls' },
            React.createElement(
                'label',
                { className: 'control-label' },
                'Lightstreamer URL',
                React.createElement('input', {
                    type: 'text',
                    value: lsUrl,
                    disabled: isConnected,
                    onChange: (e: Event) => setLsUrl(normalizeLsUrl((e.target as HTMLInputElement).value))
                })
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Push mode',
                React.createElement(
                    'select',
                    {
                        value: pushMode,
                        disabled: isConnected,
                        onChange: (e: Event) => setPushMode((e.target as HTMLSelectElement).value as PushMode)
                    },
                    React.createElement('option', { value: 'twoLevel' }, 'Two-level push (COMMAND + MERGE)'),
                    React.createElement('option', { value: 'pure' }, 'Pure command mode')
                )
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Max wait (s)',
                React.createElement('input', {
                    type: 'number',
                    min: 1,
                    value: snapshotTimeoutSeconds,
                    disabled: isConnected,
                    onChange: (e: Event) => {
                        const parsed = Number((e.target as HTMLInputElement).value);
                        if (Number.isFinite(parsed) && parsed >= 1) {
                            setSnapshotTimeoutSeconds(Math.floor(parsed));
                        }
                    }
                })
            ),
            !isConnected
                ? React.createElement('button', { type: 'button', onClick: connect, disabled: isLoadingSnapshot }, 'Connect')
                : React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect'),
            React.createElement(
                'label',
                { className: 'control-label', style: { flexDirection: 'row', alignItems: 'center', gap: '0.4rem' } },
                React.createElement('input', {
                    type: 'checkbox',
                    checked: gridVisible,
                    onChange: (e: Event) => {
                        const checked = (e.target as HTMLInputElement).checked;
                        gridVisibleRef.current = checked;
                        setGridVisible(checked);
                    }
                }),
                'Show grid'
            )
        ),
        gridVisible
            ? React.createElement(
                'div',
                { className: 'grid-wrapper' },
                isLoadingSnapshot
                    ? React.createElement(
                        'div',
                        { className: 'grid-loader' },
                        React.createElement('div', { className: 'grid-loader-spinner' }),
                        'Loading snapshot…'
                    )
                    : null,
                React.createElement(
                    'div',
                    { className: 'ag-theme-balham', style: { width: '100%', height: '100%' } },
                    React.createElement(AgGridReact<RowData>, {
                        onGridReady: (params) => { gridApiRef.current = params.api; },
                        rowData: initialRowData,
                        columnDefs,
                        defaultColDef,
                        getRowId: (params) => String(params.data.key ?? ''),
                        suppressFieldDotNotation: true,
                        animateRows: true,
                        cellFlashDuration: 1,
                        cellFadeDuration: 1_000
                    })
                )
            )
            : null
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
