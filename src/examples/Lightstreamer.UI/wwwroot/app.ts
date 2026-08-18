import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry, type ColDef, type GridApi } from 'ag-grid-community';

ModuleRegistry.registerModules([AllCommunityModule]);

declare const LightstreamerClient: any;
declare const Subscription: any;

const defaultLsUrl = window.location.origin;
const commandListItem = 'TRADES_ALL';
const subscribedFields = [
    'tradeId', 'createdDate', 'updatedDate', 'accountId', 'quantity',
    'price', 'side', 'status', 'notional',
    ...Array.from({ length: 30 }, (_, i) => `stringField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 23 }, (_, i) => `intField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `decimalField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `enumField${i.toString().padStart(2, '0')}`)
];
const subscribedFieldSet = new Set(subscribedFields);
const snapshotTimeoutMs = 10_000;
const snapshotAddGraceMs = 300;
const latencyWindowSize = 500;

type RowData = Record<string, string | null>;

function App(): React.ReactElement {
    const [lsUrl, setLsUrl] = useState(defaultLsUrl);
    const [status, setStatus] = useState('Disconnected');
    const [isConnected, setIsConnected] = useState(false);
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [snapshotStats, setSnapshotStats] = useState<{ rowCount: number; loadMs: number } | null>(null);
    const [latencySummary, setLatencySummary] = useState({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    const latencyAccRef = useRef({ maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [] as number[], recentTotalMs: 0 });
    const [columnDefs] = useState<ColDef<RowData>[]>(() =>
        subscribedFields.map((field) => ({ field, headerName: field }))
    );
    const [gridVisible, setGridVisible] = useState(true);
    const gridVisibleRef = useRef(true);
    const initialRowData = useMemo<RowData[]>(() => [], []);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const clientRef = useRef<any>(null);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const snapshotBufferRef = useRef<Map<string, RowData>>(new Map());
    const snapshotCommandKeysRef = useRef<Set<string>>(new Set());
    const snapshotRowsReceivedRef = useRef<Set<string>>(new Set());
    const pendingPreSnapshotUpdatesRef = useRef<Map<string, RowData>>(new Map());
    const pendingLiveAddKeysRef = useRef<Set<string>>(new Set());
    const commandSnapshotEndedRef = useRef(false);
    const snapshotCompleteRef = useRef(false);
    const subscribeTimeRef = useRef<number | null>(null);
    const snapshotCompletionTimeRef = useRef<number | null>(null);
    const snapshotTimeoutHandleRef = useRef<number | null>(null);
    const snapshotFinalizeGraceHandleRef = useRef<number | null>(null);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        resizable: true,
        enableCellChangeFlash: true
    }), []);

    const recordLatency = useCallback((updatedDate: string | null | undefined) => {
        if (!updatedDate) { return; }
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
        snapshotRowsReceivedRef.current.clear();
        pendingPreSnapshotUpdatesRef.current.clear();
        pendingLiveAddKeysRef.current.clear();
        commandSnapshotEndedRef.current = false;
        snapshotCompleteRef.current = false;
        subscribeTimeRef.current = null;
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

    const finalizeSnapshot = useCallback(() => {
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
        const loadMs = subscribeTimeRef.current !== null
            ? completionTime - subscribeTimeRef.current
            : 0;
        subscribeTimeRef.current = null;
        snapshotCompletionTimeRef.current = null;

        const rows = Array.from(snapshotBufferRef.current.values());
        snapshotBufferRef.current.clear();
        for (const row of rows) {
            rowsByIdRef.current.set(row.key as string, row);
        }

        setSnapshotStats({ rowCount: rows.length, loadMs });
        setIsLoadingSnapshot(false);

        if (gridApiRef.current && gridVisibleRef.current) {
            gridApiRef.current.setGridOption('rowData', rows);
        }
    }, []);

    const tryFinalizeSnapshot = useCallback(() => {
        if (snapshotCompleteRef.current || !commandSnapshotEndedRef.current) {
            return;
        }

        for (const key of snapshotCommandKeysRef.current) {
            if (!snapshotRowsReceivedRef.current.has(key)) {
                snapshotCompletionTimeRef.current = null;
                if (snapshotFinalizeGraceHandleRef.current !== null) {
                    clearTimeout(snapshotFinalizeGraceHandleRef.current);
                    snapshotFinalizeGraceHandleRef.current = null;
                }
                return;
            }
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

    const connect = useCallback(() => {
        clearState();
        setIsConnected(true);
        setIsLoadingSnapshot(true);
        subscribeTimeRef.current = performance.now();

        snapshotTimeoutHandleRef.current = window.setTimeout(finalizeSnapshot, snapshotTimeoutMs);

        const subscription = new Subscription('COMMAND', [commandListItem], ['key', 'command']);
        subscription.setDataAdapter('trades-command-adapter');
        subscription.setCommandSecondLevelDataAdapter('trades-merge-adapter');
        subscription.setCommandSecondLevelFields(subscribedFields);
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
            onItemUpdate(update: any) {
                const itemName: string = update.getItemName();
                const isSnapshot = update.isSnapshot();
                const command = update.getValue('command');
                const commandKey = update.getValue('key');
                const rowKey = commandKey ?? itemName;
                const hasRowPayload = subscribedFields.some((field) => update.getValue(field) !== null);

                if (itemName === commandListItem) {
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
                                snapshotCommandKeysRef.current.add(commandKey);
                            } else if (command === 'DELETE') {
                                snapshotCommandKeysRef.current.delete(commandKey);
                                snapshotRowsReceivedRef.current.delete(commandKey);
                                snapshotBufferRef.current.delete(commandKey);
                                pendingPreSnapshotUpdatesRef.current.delete(commandKey);
                            }
                        } else if (command === 'DELETE') {
                            pendingLiveAddKeysRef.current.delete(commandKey);
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

                const existing = rowsByIdRef.current.get(rowKey);
                if (!existing) {
                    if (!pendingLiveAddKeysRef.current.has(rowKey)) {
                        return;
                    }

                    const row: RowData = { key: rowKey };
                    for (const field of subscribedFields) {
                        row[field] = update.getValue(field);
                    }
                    rowsByIdRef.current.set(rowKey, row);
                    pendingLiveAddKeysRef.current.delete(rowKey);
                    recordLatency(row.updatedDate);

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
                if (snapshotCompleteRef.current || itemName !== commandListItem) {
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
    }, [clearState, finalizeSnapshot, lsUrl, recordLatency, tryFinalizeSnapshot]);

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

    useEffect(() => {
        const handle = window.setInterval(() => {
            setLatencySummary({ ...latencyAccRef.current });
        }, 500);
        return () => clearInterval(handle);
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
                ? `Snapshot: ${snapshotStats.rowCount.toLocaleString()} rows loaded in ${snapshotStats.loadMs.toFixed(0)} ms`
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
                    onChange: (e: Event) => setLsUrl((e.target as HTMLInputElement).value)
                })
            ),
            !isConnected
                ? React.createElement('button', { type: 'button', onClick: connect }, 'Connect')
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
        React.createElement(
            'div',
            { className: 'grid-wrapper', style: gridVisible ? undefined : { display: 'none' } },
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
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
