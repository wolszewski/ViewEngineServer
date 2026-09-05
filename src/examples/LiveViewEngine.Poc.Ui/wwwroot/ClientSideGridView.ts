import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { WebHostClient, type DeltaEvent, type MessageFormat, type RowData } from './webHostClient';
import {
    ColumnSelectorPanel,
    buildColumnDef,
    defaultCollectionId,
    defaultGridColDef,
    defaultMessageFormat,
    defaultSortColumn,
    useCollectionData,
    useColumnSelection
} from './gridShared';

/**
 * Browser-side tab: subscribes to the entire collection (no pageSize/viewport window), and lets
 * AG Grid's default client-side row model perform all sorting and filtering locally in the browser.
 * No sort/filter interaction is ever forwarded back to the server.
 */
export default function ClientSideGridView(): React.ReactElement {
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState(defaultCollectionId);
    const [messageFormat, setMessageFormat] = useState<MessageFormat>(defaultMessageFormat);
    const [gridVisible, setGridVisible] = useState(true);

    const columnSelection = useColumnSelection([]);
    const { selectedFields } = columnSelection;

    const buildColDef = useCallback((field: string) => buildColumnDef(field), []);
    const data = useCollectionData(buildColDef, { unboundedViewport: true });

    const clientRef = useRef<WebHostClient | null>(null);

    const connect = useCallback(() => {
        data.isReloadingGridRef.current = true;
        data.clearState();
        data.setIsLoadingSnapshot(true);
        data.appendLog(`sync full collection: ${collectionId} | sort/filter applied in browser only`);
        clientRef.current?.connect({
            collectionId,
            sortColumn: defaultSortColumn,
            sortAscending: true,
            startIndex: 0,
            filters: [],
            fields: selectedFields,
            messageFormat
        });
    }, [collectionId, data, messageFormat, selectedFields]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        data.isReloadingGridRef.current = false;
        setStatus('Disconnected');
        data.setIsLoadingSnapshot(false);
        data.clearState();
        data.appendLog('disconnect');
    }, [data]);

    useEffect(() => {
        clientRef.current = new WebHostClient('ws://127.0.0.1:5100/ws', {
            onStatus: setStatus,
            onEvent: (event: DeltaEvent) => data.handleDeltaEventRef.current(event),
            onSubscriptionRejected: (reason, message) => {
                data.isReloadingGridRef.current = false;
                data.setIsLoadingSnapshot(false);
                data.appendLog(`subscription rejected (${reason}): ${message}`);
            },
            onUpdateRejected: (reason, message) => {
                data.appendLog(`view update rejected (${reason}): ${message}`);
            }
        });

        return () => {
            clientRef.current?.disconnect();
            clientRef.current = null;
        };
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    useEffect(() => {
        connect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    useEffect(() => {
        const client = clientRef.current;
        if (!client?.isConnected) {
            return;
        }

        data.isReloadingGridRef.current = true;
        data.clearState();
        data.setIsLoadingSnapshot(true);
        client.connect({
            collectionId,
            sortColumn: defaultSortColumn,
            sortAscending: true,
            startIndex: 0,
            filters: [],
            fields: selectedFields,
            messageFormat
        });
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [collectionId, messageFormat, selectedFields]);

    const rowCountLabel = useMemo(
        () => (data.totalCount !== null ? `${data.totalCount.toLocaleString()} rows total (full collection)` : 'snapshot pending'),
        [data.totalCount]
    );

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
                React.createElement('span', null, rowCountLabel),
                data.snapshotStats
                    ? React.createElement(
                        'span',
                        null,
                        `snapshot ${data.snapshotStats.rowCount.toLocaleString()} rows | `
                        + `wait ${data.snapshotStats.waitMs.toFixed(0)}ms | `
                        + `transfer ${data.snapshotStats.transferMs.toFixed(0)}ms | `
                        + `render ${data.snapshotStats.renderMs.toFixed(0)}ms`
                    )
                    : null,
                React.createElement(
                    'span',
                    null,
                    `Max latency: ${data.latencySummary.maxMs.toFixed(0)} ms • `
                    + `Avg latency (last ${data.latencySummary.sampleCount}): ${data.latencySummary.avgMs.toFixed(0)} ms`
                )
            ),
            React.createElement(
                'div',
                { className: 'log-window' },
                ...data.eventLog.map((line, index) => React.createElement('div', { key: `${line}-${index}`, className: 'log-line' }, line))
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
            React.createElement('button', { type: 'button', onClick: columnSelection.openColumnSelector }, 'Select columns'),
            React.createElement('button', { type: 'button', onClick: connect }, 'Connect'),
            React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect'),
            React.createElement(
                'label',
                { className: 'control-label', style: { flexDirection: 'row', alignItems: 'center', gap: '0.4rem' } },
                React.createElement('input', {
                    type: 'checkbox',
                    checked: gridVisible,
                    onChange: (e: Event) => setGridVisible((e.target as HTMLInputElement).checked)
                }),
                'Show grid'
            )
        ),
        React.createElement(ColumnSelectorPanel, columnSelection),
        React.createElement(
            'div',
            { className: 'grid-wrapper', style: gridVisible ? undefined : { display: 'none' } },
            data.isLoadingSnapshot
                ? React.createElement(
                    'div',
                    { className: 'grid-loader' },
                    React.createElement('div', { className: 'grid-loader-spinner' }),
                    'Loading full collection…'
                )
                : null,
            React.createElement(
                'div',
                { className: 'ag-theme-balham', style: { width: '100%', height: '100%' } },
                React.createElement(AgGridReact<RowData>, {
                    onGridReady: (params) => {
                        data.gridApiRef.current = params.api;
                    },
                    rowData: data.rowData,
                    columnDefs: data.columnDefs,
                    defaultColDef: defaultGridColDef,
                    getRowId: (params) => String(params.data.key ?? params.data.id ?? ''),
                    suppressFieldDotNotation: true,
                    animateRows: true,
                    cellFlashDuration: 1,
                    cellFadeDuration: 1_000
                })
            )
        )
    );
}
