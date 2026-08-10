import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry, type ColDef, type GridApi } from 'ag-grid-community';
import {
    WebHostClient,
    type DeltaEvent,
    type RowData,
    type RowInsertEvent,
    type RowRemoveEvent,
    type RowUpdateEvent,
    type SnapshotEvent
} from './webHostClient';

ModuleRegistry.registerModules([AllCommunityModule]);

const defaultTotalCountAssumption = 10_000;

function App(): React.ReactElement {
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState('trades');
    const [sortColumn, setSortColumn] = useState('quantity');
    const [pageSize, setPageSize] = useState(50);
    const [pageIndex, setPageIndex] = useState(0);
    const [totalCount, setTotalCount] = useState<number | null>(null);
    const [columnDefs, setColumnDefs] = useState<ColDef<RowData>[]>([]);

    const effectiveTotalCount = totalCount ?? defaultTotalCountAssumption;
    const maxPageIndex = Math.max(0, Math.ceil(effectiveTotalCount / pageSize) - 1);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const clientRef = useRef<WebHostClient | null>(null);
    const initialRowData = useMemo<RowData[]>(() => [], []);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const orderedIdsRef = useRef<string[]>([]);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        resizable: true,
        enableCellChangeFlash: true
    }), []);

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        orderedIdsRef.current = [];
        setColumnDefs([]);
        setTotalCount(null);
        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', []);
        }
    }, []);

    const setColumnsFromRow = useCallback((row: RowData | undefined) => {
        if (!row) {
            return;
        }

        const fields = Object.keys(row);
        if (fields.length === 0) {
            return;
        }

        setColumnDefs(fields.map((field) => ({
            field,
            headerName: field
        })));
    }, []);

    const applySnapshot = useCallback((snapshot: SnapshotEvent) => {
        const rows = (snapshot.rows ?? []).map((row) => ({ ...row }));
        rowsByIdRef.current.clear();
        orderedIdsRef.current = [];
        for (const row of rows) {
            const rowId = row.key ?? row.id;
            if (!rowId) {
                continue;
            }
            rowsByIdRef.current.set(rowId, row);
            orderedIdsRef.current.push(rowId);
        }

        setTotalCount(snapshot.totalCount);
        setPageIndex(Math.floor((snapshot.startIndex ?? 0) / pageSize));

        if (rows.length > 0) {
            setColumnsFromRow(rows[0]);
        } else {
            setColumnDefs([]);
        }

        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', rows);
        }
    }, [pageSize, setColumnsFromRow]);

    const applyUpdate = useCallback((update: RowUpdateEvent) => {
        const rowId = update.rowId;
        if (!rowId) {
            return;
        }

        const existing = rowsByIdRef.current.get(rowId);
        if (!existing) {
            return;
        }

        const changedFields = update.changedFields ?? {};
        const updated: RowData = { ...existing, ...changedFields };
        rowsByIdRef.current.set(rowId, updated);

        const api = gridApiRef.current;
        if (!api) {
            return;
        }

        api.applyTransaction({ update: [updated] });
        const rowNode = api.getRowNode(rowId);
        if (rowNode) {
            api.flashCells({
                rowNodes: [rowNode],
                columns: Object.keys(changedFields),
                flashDuration: 1,
                fadeDuration: 1_000
            });
        }
    }, []);

    const applyInsert = useCallback((insert: RowInsertEvent) => {
        const row = { ...(insert.row ?? {}) };
        const rowId = row.key ?? row.id;
        if (!rowId) {
            return;
        }

        if (columnDefs.length === 0) {
            setColumnsFromRow(row);
        }

        const position = Number.isInteger(insert.position) ? insert.position : orderedIdsRef.current.length;
        const clampedPosition = Math.max(0, Math.min(position, orderedIdsRef.current.length));

        orderedIdsRef.current.splice(clampedPosition, 0, rowId);
        rowsByIdRef.current.set(rowId, row);

        if (gridApiRef.current) {
            gridApiRef.current.applyTransaction({
                add: [row],
                addIndex: clampedPosition
            });
        }
    }, [columnDefs.length, setColumnsFromRow]);

    const applyRemove = useCallback((remove: RowRemoveEvent) => {
        const position = remove.position;
        if (!Number.isInteger(position) || position < 0 || position >= orderedIdsRef.current.length) {
            return;
        }

        const rowId = orderedIdsRef.current[position];
        orderedIdsRef.current.splice(position, 1);
        const row = rowsByIdRef.current.get(rowId);
        rowsByIdRef.current.delete(rowId);
        if (!row || !gridApiRef.current) {
            return;
        }

        gridApiRef.current.applyTransaction({ remove: [row] });
    }, []);

    const handleDeltaEvent = useCallback((event: DeltaEvent) => {
        if (event.type === 'snapshot') {
            applySnapshot(event);
        } else if (event.type === 'rowUpdate') {
            applyUpdate(event);
        } else if (event.type === 'rowInsert') {
            applyInsert(event);
        } else if (event.type === 'rowRemove') {
            applyRemove(event);
        }
    }, [applyInsert, applyRemove, applySnapshot, applyUpdate]);

    const connect = useCallback(() => {
        clearState();
        const startIndex = pageIndex * pageSize;
        clientRef.current?.connect({
            collectionId,
            sortColumn,
            pageSize,
            startIndex
        });
    }, [clearState, collectionId, pageIndex, pageSize, sortColumn]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        setStatus('Disconnected');
        clearState();
    }, [clearState]);

    const goToPage = useCallback((nextPageIndex: number) => {
        const clamped = Math.max(0, Math.min(nextPageIndex, maxPageIndex));
        setPageIndex(clamped);
        clientRef.current?.setViewport(clamped * pageSize, pageSize);
    }, [maxPageIndex, pageSize]);

    const onPageSizeChanged = useCallback((nextPageSize: number) => {
        setPageSize(nextPageSize);
        setPageIndex(0);
        clientRef.current?.setViewport(0, nextPageSize);
    }, []);

    useEffect(() => {
        clientRef.current = new WebHostClient('ws://127.0.0.1:5100/ws', {
            onStatus: setStatus,
            onEvent: handleDeltaEvent
        });

        return () => {
            clientRef.current?.disconnect();
            clientRef.current = null;
        };
    }, [handleDeltaEvent]);

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
            .status {
                margin-bottom: 1rem;
                padding: 0.75rem;
                background: #f0f6ff;
                border-radius: 0.25rem;
            }
            .pager {
                display: flex;
                align-items: center;
                gap: 0.75rem;
                margin-bottom: 1rem;
            }
            .ag-theme-balham {
                --ag-value-change-value-highlight-background-color: #b7f7b7;
            }
            `
        ),
        React.createElement('h1', null, 'LiveViewEngine PoC UI'),
        React.createElement('div', { className: 'status' }, status),
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
                'Sort column',
                React.createElement('input', {
                    value: sortColumn,
                    onChange: (e: Event) => setSortColumn((e.target as HTMLInputElement).value)
                })
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Page size',
                React.createElement(
                    'select',
                    {
                        value: pageSize,
                        onChange: (e: Event) => onPageSizeChanged(Number((e.target as HTMLSelectElement).value))
                    },
                    React.createElement('option', { value: 25 }, '25'),
                    React.createElement('option', { value: 50 }, '50'),
                    React.createElement('option', { value: 100 }, '100')
                )
            ),
            React.createElement('button', { onClick: connect }, 'Connect'),
            React.createElement('button', { onClick: disconnect }, 'Disconnect')
        ),
        React.createElement(
            'div',
            { className: 'pager' },
            React.createElement('button', { onClick: () => goToPage(pageIndex - 1), disabled: pageIndex <= 0 }, 'Prev'),
            React.createElement(
                'span',
                null,
                `Page ${pageIndex + 1} / ${maxPageIndex + 1} (${effectiveTotalCount.toLocaleString()} rows)`
            ),
            React.createElement(
                'button',
                { onClick: () => goToPage(pageIndex + 1), disabled: pageIndex >= maxPageIndex },
                'Next'
            )
        ),
        React.createElement(
            'div',
            { className: 'ag-theme-balham', style: { width: '100%', height: '70vh' } },
            React.createElement(AgGridReact<RowData>, {
                onGridReady: (params) => {
                    gridApiRef.current = params.api;
                },
                rowData: initialRowData,
                columnDefs,
                defaultColDef,
                getRowId: (params) => String(params.data.key ?? params.data.id ?? ''),
                suppressFieldDotNotation: true,
                animateRows: true,
                cellFlashDuration: 1,
                cellFadeDuration: 1_000
            })
        )
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
