import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { type ColDef, type GridApi } from 'ag-grid-community';
import {
    WebHostClient,
    type DeltaEvent,
    type MessageFormat,
    type RowData
} from './webHostClient';
import { buildColumnDef, defaultCollectionId, defaultMessageFormat, impliedFields } from './shared';

interface OperationStats {
    kind: 'sort' | 'filter';
    ms: number;
}

export function ClientSortedGrid(): React.ReactElement {
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState(defaultCollectionId);
    const [messageFormat, setMessageFormat] = useState<MessageFormat>(defaultMessageFormat);
    const [rowData, setRowData] = useState<RowData[]>([]);
    const [columnDefs, setColumnDefs] = useState<ColDef<RowData>[]>([]);
    const [totalCount, setTotalCount] = useState<number | null>(null);
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [isWaitingForCollection, setIsWaitingForCollection] = useState(false);
    const [snapshotStats, setSnapshotStats] = useState<{
        rowCount: number;
        waitMs: number;
        transferMs: number;
        renderMs: number;
    } | null>(null);
    const [operationStats, setOperationStats] = useState<OperationStats | null>(null);
    const [eventLog, setEventLog] = useState<string[]>([]);

    const clientRef = useRef<WebHostClient | null>(null);
    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const rowsByPositionRef = useRef<Map<number, RowData>>(new Map());
    const handleDeltaEventRef = useRef<(event: DeltaEvent) => void>(() => {});
    const pendingSnapshotRenderMeasureRef = useRef<{
        rowCount: number;
        waitMs: number;
        transferMs: number;
        startedAt: number;
    } | null>(null);
    const pendingOperationMeasureRef = useRef<{ kind: 'sort' | 'filter'; startedAt: number } | null>(null);

    const appendLog = useCallback((entry: string) => {
        setEventLog((current) => [...current.slice(-19), entry]);
    }, []);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        floatingFilter: true,
        resizable: true,
        enableCellChangeFlash: true
    }), []);

    const handleDeltaEvent = useCallback((event: DeltaEvent) => {
        if (event.type === 'snapshot') {
            const rows = event.rows.map((row) => ({ ...row }));
            appendLog(
                `snapshot received: ${rows.length.toLocaleString()} rows `
                + `(${event.waitMs.toFixed(0)}ms wait, ${event.transferMs.toFixed(0)}ms transfer)`
            );

            rowsByIdRef.current.clear();
            rowsByPositionRef.current.clear();
            rows.forEach((row, index) => {
                const rowId = row.key ?? row.id;
                if (rowId) {
                    rowsByIdRef.current.set(rowId, row);
                }
                rowsByPositionRef.current.set(event.startIndex + index, row);
            });

            setTotalCount(event.totalCount);
            setIsLoadingSnapshot(false);
            setIsWaitingForCollection(false);

            if (rows.length > 0) {
                const fields = Object.keys(rows[0]).filter((f) => !impliedFields.has(f));
                if (fields.length > 0) {
                    setColumnDefs(fields.map((field) => buildColumnDef(field, '', false)));
                }
            }

            pendingSnapshotRenderMeasureRef.current = {
                rowCount: rows.length,
                waitMs: event.waitMs,
                transferMs: event.transferMs,
                startedAt: performance.now()
            };

            setRowData(rows);
            return;
        }

        if (event.type === 'rowInsert') {
            if (columnDefs.length === 0 && event.row) {
                const fields = Object.keys(event.row).filter((f) => !impliedFields.has(f));
                if (fields.length > 0) {
                    setColumnDefs(fields.map((field) => buildColumnDef(field, '', false)));
                }
            }

            const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => right - left);
            for (const position of positions) {
                if (position >= event.position) {
                    const row = rowsByPositionRef.current.get(position);
                    if (row) {
                        rowsByPositionRef.current.set(position + 1, row);
                    }
                }
            }

            const insertedRow = { ...event.row };
            rowsByPositionRef.current.set(event.position, insertedRow);
            const insertedRowId = insertedRow.key ?? insertedRow.id;
            if (insertedRowId) {
                rowsByIdRef.current.set(insertedRowId, insertedRow);
            }

            setTotalCount((current) => (current === null ? null : current + 1));
            gridApiRef.current?.applyTransactionAsync({ add: [insertedRow] });
            return;
        }

        if (event.type === 'rowUpdate') {
            const existing = rowsByPositionRef.current.get(event.position) ?? rowsByIdRef.current.get(event.rowId);
            if (!existing) {
                return;
            }

            const updated: RowData = { ...existing, ...(event.changedFields ?? {}) };
            const rowId = updated.key ?? updated.id ?? event.rowId;
            if (rowId) {
                rowsByIdRef.current.set(rowId, updated);
            }
            rowsByPositionRef.current.set(event.position, updated);
            gridApiRef.current?.applyTransactionAsync({ update: [updated] });
            return;
        }

        if (event.type === 'rowRemove') {
            const removedRow = rowsByPositionRef.current.get(event.position);
            if (!removedRow) {
                return;
            }

            const removedRowId = removedRow.key ?? removedRow.id;
            if (removedRowId) {
                rowsByIdRef.current.delete(removedRowId);
            }
            rowsByPositionRef.current.delete(event.position);
            const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => left - right);
            for (const position of positions) {
                if (position > event.position) {
                    const row = rowsByPositionRef.current.get(position);
                    if (row) {
                        rowsByPositionRef.current.set(position - 1, row);
                        rowsByPositionRef.current.delete(position);
                    }
                }
            }

            setTotalCount((current) => (current === null ? null : Math.max(0, current - 1)));
            gridApiRef.current?.applyTransactionAsync({ remove: [removedRow] });
            return;
        }

        if (event.type === 'rowReplace') {
            if (columnDefs.length === 0 && event.row) {
                const fields = Object.keys(event.row).filter((f) => !impliedFields.has(f));
                if (fields.length > 0) {
                    setColumnDefs(fields.map((field) => buildColumnDef(field, '', false)));
                }
            }

            const removedRow = rowsByPositionRef.current.get(event.removePosition);
            if (removedRow) {
                const removedRowId = removedRow.key ?? removedRow.id;
                if (removedRowId) {
                    rowsByIdRef.current.delete(removedRowId);
                }
            } else if (event.removedRowId) {
                rowsByIdRef.current.delete(event.removedRowId);
            }

            rowsByPositionRef.current.delete(event.removePosition);
            const afterRemovePositions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => left - right);
            for (const position of afterRemovePositions) {
                if (position > event.removePosition) {
                    const row = rowsByPositionRef.current.get(position);
                    if (row) {
                        rowsByPositionRef.current.set(position - 1, row);
                        rowsByPositionRef.current.delete(position);
                    }
                }
            }

            const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => right - left);
            for (const position of positions) {
                if (position >= event.insertPosition) {
                    const row = rowsByPositionRef.current.get(position);
                    if (row) {
                        rowsByPositionRef.current.set(position + 1, row);
                    }
                }
            }

            const insertedRow = { ...event.row };
            rowsByPositionRef.current.set(event.insertPosition, insertedRow);
            const insertedRowId = insertedRow.key ?? insertedRow.id;
            if (insertedRowId) {
                rowsByIdRef.current.set(insertedRowId, insertedRow);
            }

            gridApiRef.current?.applyTransactionAsync({
                remove: removedRow ? [removedRow] : (event.removedRowId ? [{ key: event.removedRowId } as RowData] : []),
                add: [insertedRow]
            });
        }
    }, [appendLog, columnDefs.length]);

    handleDeltaEventRef.current = handleDeltaEvent;

    // Measures the time from a client-side sort/filter change until the grid has repainted
    // the reordered/refiltered rows (double requestAnimationFrame = after next paint).
    const measureOperation = useCallback((kind: 'sort' | 'filter') => {
        pendingOperationMeasureRef.current = { kind, startedAt: performance.now() };

        let cancelled = false;
        let secondFrameId = 0;
        const firstFrameId = window.requestAnimationFrame(() => {
            secondFrameId = window.requestAnimationFrame(() => {
                if (cancelled) {
                    return;
                }

                const pending = pendingOperationMeasureRef.current;
                if (!pending || pending.kind !== kind) {
                    return;
                }

                const ms = performance.now() - pending.startedAt;
                setOperationStats({ kind, ms });
                appendLog(`client-side ${kind} applied in ${ms.toFixed(1)}ms`);
                pendingOperationMeasureRef.current = null;
            });
        });

        return () => {
            cancelled = true;
            window.cancelAnimationFrame(firstFrameId);
            if (secondFrameId !== 0) {
                window.cancelAnimationFrame(secondFrameId);
            }
        };
    }, [appendLog]);

    useEffect(() => {
        const pending = pendingSnapshotRenderMeasureRef.current;
        if (!pending || isLoadingSnapshot) {
            return;
        }

        let cancelled = false;
        let secondFrameId = 0;
        const firstFrameId = window.requestAnimationFrame(() => {
            secondFrameId = window.requestAnimationFrame(() => {
                if (cancelled) {
                    return;
                }

                const renderMs = performance.now() - pending.startedAt;
                setSnapshotStats({
                    rowCount: pending.rowCount,
                    waitMs: pending.waitMs,
                    transferMs: pending.transferMs,
                    renderMs
                });
                appendLog(
                    `snapshot rendered: ${pending.rowCount.toLocaleString()} rows `
                    + `(${pending.waitMs.toFixed(0)}ms wait, ${pending.transferMs.toFixed(0)}ms transfer, `
                    + `${renderMs.toFixed(0)}ms render)`
                );
                pendingSnapshotRenderMeasureRef.current = null;
            });
        });

        return () => {
            cancelled = true;
            window.cancelAnimationFrame(firstFrameId);
            if (secondFrameId !== 0) {
                window.cancelAnimationFrame(secondFrameId);
            }
        };
    }, [appendLog, isLoadingSnapshot, rowData]);

    const connectWith = useCallback((nextCollectionId: string, nextMessageFormat: MessageFormat) => {
        setIsLoadingSnapshot(true);
        setIsWaitingForCollection(false);
        setRowData([]);
        setColumnDefs([]);
        setTotalCount(null);
        setSnapshotStats(null);
        setOperationStats(null);
        rowsByIdRef.current.clear();
        rowsByPositionRef.current.clear();
        appendLog(`subscribing to all rows of ${nextCollectionId}`);
        clientRef.current?.connect({
            collectionId: nextCollectionId,
            sortAscending: false,
            startIndex: 0,
            filters: [],
            messageFormat: nextMessageFormat
        });
    }, [appendLog]);

    useEffect(() => {
        clientRef.current = new WebHostClient('ws://127.0.0.1:5100/ws', {
            onStatus: setStatus,
            onEvent: (event) => handleDeltaEventRef.current(event),
            onWaitingForCollection: () => setIsWaitingForCollection(true)
        });

        connectWith(collectionId, messageFormat);

        return () => {
            clientRef.current?.disconnect();
            clientRef.current = null;
        };
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        setStatus('Disconnected');
        setIsLoadingSnapshot(false);
        setIsWaitingForCollection(false);
        appendLog('disconnect');
    }, [appendLog]);

    const reconnect = useCallback(() => {
        if (!clientRef.current) {
            return;
        }
        appendLog(`reconnecting to ${collectionId}`);
        connectWith(collectionId, messageFormat);
    }, [appendLog, collectionId, connectWith, messageFormat]);

    return React.createElement(
        React.Fragment,
        null,
        React.createElement(
            'div',
            { className: 'log-panel' },
            React.createElement(
                'div',
                { className: 'log-header' },
                React.createElement('span', null, status),
                React.createElement(
                    'span',
                    null,
                    totalCount !== null ? `${totalCount.toLocaleString()} rows total` : 'snapshot pending'
                ),
                snapshotStats
                    ? React.createElement(
                        'span',
                        null,
                        `snapshot ${snapshotStats.rowCount.toLocaleString()} rows | `
                        + `wait ${snapshotStats.waitMs.toFixed(0)}ms | `
                        + `transfer ${snapshotStats.transferMs.toFixed(0)}ms | `
                        + `render ${snapshotStats.renderMs.toFixed(0)}ms`
                    )
                    : null,
                operationStats
                    ? React.createElement(
                        'span',
                        null,
                        `last client-side ${operationStats.kind}: ${operationStats.ms.toFixed(1)}ms`
                    )
                    : null
            ),
            React.createElement(
                'div',
                { className: 'log-window' },
                ...eventLog.map((line, index) => React.createElement('div', { key: `${line}-${index}`, className: 'log-line' }, line))
            )
        ),
        React.createElement(
            'div',
            { className: 'controls' },
            React.createElement(
                'label',
                { className: 'control-label' },
                'Collection',
                React.createElement('input', {
                    value: collectionId,
                    onChange: (e: Event) => setCollectionId((e.target as HTMLInputElement).value)
                })
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Message format',
                React.createElement(
                    'select',
                    {
                        value: messageFormat,
                        onChange: (e: Event) => setMessageFormat((e.target as HTMLSelectElement).value as MessageFormat)
                    },
                    React.createElement('option', { value: 'compact' }, 'Compact'),
                    React.createElement('option', { value: 'json' }, 'JSON')
                )
            ),
            React.createElement('button', { type: 'button', onClick: reconnect }, 'Connect'),
            React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect')
        ),
        React.createElement(
            'div',
            { className: 'grid-wrapper' },
            isLoadingSnapshot
                ? React.createElement(
                    'div',
                    { className: 'grid-loader' },
                    React.createElement('div', { className: 'grid-loader-spinner' }),
                    isWaitingForCollection
                        ? 'Waiting for collection…'
                        : 'Loading snapshot…',
                    isWaitingForCollection
                        ? React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect')
                        : null
                )
                : status === 'Disconnected'
                    ? React.createElement(
                        'div',
                        { className: 'grid-loader' },
                        React.createElement('button', { type: 'button', onClick: reconnect }, 'Reconnect')
                    )
                    : null,
            React.createElement(
                'div',
                { className: 'ag-theme-balham', style: { width: '100%', height: '100%' } },
                React.createElement(AgGridReact<RowData>, {
                    onGridReady: (params) => {
                        gridApiRef.current = params.api;
                    },
                    onSortChanged: () => measureOperation('sort'),
                    onFilterChanged: () => measureOperation('filter'),
                    rowData,
                    columnDefs,
                    defaultColDef,
                    getRowId: (params) => String(params.data.key ?? params.data.id ?? ''),
                    suppressFieldDotNotation: true,
                    animateRows: false
                })
            )
        )
    );
}
