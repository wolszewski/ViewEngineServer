import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';

ModuleRegistry.registerModules([AllCommunityModule]);

const subscribeRetryIntervalMs = 1_000;

function App() {
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState('trades');
    const [sortColumn, setSortColumn] = useState('quantity');
    const [pageSize, setPageSize] = useState(50);
    const [columnDefs, setColumnDefs] = useState([]);

    const socketRef = useRef(null);
    const gridApiRef = useRef(null);
    const hasReceivedSnapshotRef = useRef(false);
    const subscribeRetryRef = useRef(null);
    const rowsByIdRef = useRef(new Map());
    const orderedIdsRef = useRef([]);
    const initialRowData = useMemo(() => [], []);

    const defaultColDef = useMemo(() => ({
        sortable: true,
        filter: true,
        resizable: true,
        enableCellChangeFlash: true
    }), []);

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        orderedIdsRef.current = [];
        hasReceivedSnapshotRef.current = false;
        setColumnDefs([]);
        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', []);
        }
    }, []);

    const stopSubscribeRetry = useCallback(() => {
        if (subscribeRetryRef.current !== null) {
            clearInterval(subscribeRetryRef.current);
            subscribeRetryRef.current = null;
        }
    }, []);

    const sendSubscribe = useCallback(() => {
        const socket = socketRef.current;
        if (!socket || socket.readyState !== WebSocket.OPEN) {
            return;
        }

        socket.send(JSON.stringify({
            type: 'subscribe',
            collectionId,
            sortColumn,
            sortAscending: true,
            startIndex: 0,
            pageSize: Number(pageSize),
            filters: []
        }));
    }, [collectionId, pageSize, sortColumn]);

    const setColumnsFromRow = useCallback((row) => {
        if (!row || Object.keys(row).length === 0) {
            return;
        }

        setColumnDefs(Object.keys(row).map((field) => ({
            field,
            headerName: field
        })));
    }, []);

    const startSubscribeRetry = useCallback(() => {
        stopSubscribeRetry();
        subscribeRetryRef.current = setInterval(() => {
            const socket = socketRef.current;
            if (!socket || socket.readyState !== WebSocket.OPEN) {
                stopSubscribeRetry();
                return;
            }

            if (hasReceivedSnapshotRef.current) {
                stopSubscribeRetry();
                return;
            }

            setStatus('Connected (waiting for collection/snapshot)');
            sendSubscribe();
        }, subscribeRetryIntervalMs);
    }, [sendSubscribe, stopSubscribeRetry]);

    const applySnapshot = useCallback((snapshot) => {
        hasReceivedSnapshotRef.current = true;
        stopSubscribeRetry();
        setStatus('Connected');

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

        if (rows.length > 0) {
            setColumnsFromRow(rows[0]);
        } else {
            setColumnDefs([]);
        }

        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', rows);
        }
    }, [setColumnsFromRow, stopSubscribeRetry]);

    const applyUpdate = useCallback((update) => {
        const rowId = update.rowId;
        if (!rowId) {
            return;
        }

        const existing = rowsByIdRef.current.get(rowId);
        if (!existing) {
            return;
        }

        const changedFields = update.changedFields ?? {};
        const updated = { ...existing, ...changedFields };
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

    const applyInsert = useCallback((insert) => {
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

        const api = gridApiRef.current;
        if (!api) {
            return;
        }

        api.applyTransaction({
            add: [row],
            addIndex: clampedPosition
        });
    }, [columnDefs.length, setColumnsFromRow]);

    const applyRemove = useCallback((remove) => {
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

    const connect = useCallback(() => {
        const existing = socketRef.current;
        if (existing && existing.readyState === WebSocket.OPEN) {
            return;
        }

        clearState();
        setStatus('Connecting...');

        const socket = new WebSocket('ws://127.0.0.1:5100/ws');
        socketRef.current = socket;

        socket.addEventListener('open', () => {
            setStatus('Connected');
            sendSubscribe();
            startSubscribeRetry();
        });

        socket.addEventListener('message', (event) => {
            const events = JSON.parse(event.data);
            for (const evt of events) {
                if (evt.type === 'snapshot') {
                    applySnapshot(evt);
                } else if (evt.type === 'rowUpdate') {
                    applyUpdate(evt);
                } else if (evt.type === 'rowInsert') {
                    applyInsert(evt);
                } else if (evt.type === 'rowRemove') {
                    applyRemove(evt);
                }
            }
        });

        socket.addEventListener('close', () => {
            stopSubscribeRetry();
            setStatus('Disconnected');
            hasReceivedSnapshotRef.current = false;
        });
    }, [applyInsert, applyRemove, applySnapshot, applyUpdate, clearState, sendSubscribe, startSubscribeRetry, stopSubscribeRetry]);

    const disconnect = useCallback(() => {
        stopSubscribeRetry();
        const socket = socketRef.current;
        socketRef.current = null;
        if (socket) {
            socket.close();
        }
        clearState();
        hasReceivedSnapshotRef.current = false;
        setStatus('Disconnected');
    }, [clearState, stopSubscribeRetry]);

    useEffect(() => {
        return () => {
            disconnect();
            clearState();
        };
    }, [clearState, disconnect]);

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
                    onChange: (e) => setCollectionId(e.target.value)
                })
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Sort column',
                React.createElement('input', {
                    value: sortColumn,
                    onChange: (e) => setSortColumn(e.target.value)
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
                        onChange: (e) => setPageSize(Number(e.target.value))
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
            { className: 'ag-theme-balham', style: { width: '100%', height: '75vh' } },
            React.createElement(AgGridReact, {
                onGridReady: (params) => {
                    gridApiRef.current = params.api;
                },
                rowData: initialRowData,
                columnDefs,
                defaultColDef,
                getRowId: (params) => params.data.key ?? params.data.id,
                suppressFieldDotNotation: true,
                animateRows: true,
                cellFlashDuration: 1,
                cellFadeDuration: 1_000
            })
        )
    );
}

createRoot(document.getElementById('root')).render(React.createElement(App));
