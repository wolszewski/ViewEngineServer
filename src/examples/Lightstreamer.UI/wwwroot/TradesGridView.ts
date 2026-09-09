import React from 'react';
import { AgGridReact } from 'ag-grid-react';
import type { ColDef, GridApi, GridReadyEvent } from 'ag-grid-community';
import type { RowData } from './tradesShared';

export interface TradesGridViewProps {
    gridVisible: boolean;
    isLoadingSnapshot: boolean;
    trackLiveSnapshotCount: boolean;
    liveSnapshotRowCount: number;
    liveSnapshotUpdateCount: number;
    columnDefs: ColDef<RowData>[];
    defaultColDef: ColDef<RowData>;
    initialRowData: RowData[];
    gridApiRef: React.MutableRefObject<GridApi<RowData> | null>;
}

export default function TradesGridView(props: TradesGridViewProps): React.ReactElement | null {
    const {
        gridVisible, isLoadingSnapshot, trackLiveSnapshotCount, liveSnapshotRowCount, liveSnapshotUpdateCount,
        columnDefs, defaultColDef, initialRowData, gridApiRef
    } = props;

    if (!gridVisible) {
        return null;
    }

    return React.createElement(
        'div',
        { className: 'grid-wrapper' },
        isLoadingSnapshot
            ? React.createElement(
                'div',
                { className: 'grid-loader' },
                React.createElement('div', { className: 'grid-loader-spinner' }),
                trackLiveSnapshotCount
                    ? `Loading snapshot… ${liveSnapshotRowCount.toLocaleString()} added, `
                        + `${liveSnapshotUpdateCount.toLocaleString()} updated`
                    : 'Loading snapshot…'
            )
            : null,
        React.createElement(
            'div',
            { className: 'ag-theme-balham', style: { width: '100%', height: '100%' } },
            React.createElement(AgGridReact<RowData>, {
                onGridReady: (params: GridReadyEvent<RowData>) => { gridApiRef.current = params.api; },
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
    );
}
