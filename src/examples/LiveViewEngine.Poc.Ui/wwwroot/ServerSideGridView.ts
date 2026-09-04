import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgGridReact } from 'ag-grid-react';
import { WebHostClient, type DeltaEvent, type MessageFormat, type RowData } from './webHostClient';
import {
    ColumnSelectorPanel,
    SearchableDropdown,
    areFiltersEqual,
    buildAppliedFiltersFromGridModel,
    buildColumnDef,
    buildGridFilterModel,
    buildViewportWindow,
    defaultCollectionId,
    defaultGridColDef,
    defaultMessageFormat,
    defaultPageSize,
    defaultPageSizes,
    defaultSortColumn,
    defaultViewportThresholdPercent,
    defaultViewportThresholdPercents,
    filterOperatorSet,
    getViewportExpansionRow,
    getViewportPageCount,
    knownTradeColumnSet,
    knownTradeColumns,
    parsePercentInteger,
    parsePositiveInteger,
    useCollectionData,
    useColumnSelection,
    type AppliedFilter,
    type ViewportWindow
} from './gridShared';

interface ServerUrlState {
    collectionId: string;
    sortColumn: string;
    sortAscending: boolean;
    messageFormat: MessageFormat;
    pageSize: number;
    viewportThresholdPercent: number;
    filters: AppliedFilter[];
    selectedFields: string[];
}

function getInitialUrlState(): ServerUrlState {
    const params = new URLSearchParams(window.location.search);
    const filterFields = params.getAll('filterField');
    const filterOperatorsFromUrl = params.getAll('filterOperator');
    const filterValues = params.getAll('filterValue');
    const filterCount = Math.min(filterFields.length, filterOperatorsFromUrl.length, filterValues.length);
    const filters: AppliedFilter[] = [];

    for (let index = 0; index < filterCount; index += 1) {
        const field = filterFields[index];
        if (!knownTradeColumnSet.has(field)) {
            continue;
        }

        const operator = filterOperatorSet.has(filterOperatorsFromUrl[index])
            ? filterOperatorsFromUrl[index]
            : 'eq';
        filters.push({
            field,
            operator,
            value: filterValues[index]
        });
    }

    const selectedColumnSet = new Set(params.getAll('column').filter((column) => knownTradeColumnSet.has(column)));
    const selectedFields = selectedColumnSet.size === 0 || selectedColumnSet.size === knownTradeColumns.length
        ? []
        : knownTradeColumns.filter((column) => selectedColumnSet.has(column));
    const sortColumn = params.get('sort');

    return {
        collectionId: params.get('collection')?.trim() || defaultCollectionId,
        sortColumn: sortColumn && knownTradeColumnSet.has(sortColumn) ? sortColumn : defaultSortColumn,
        sortAscending: params.get('dir') === 'asc',
        messageFormat: params.get('format') === 'json' ? 'json' : defaultMessageFormat,
        pageSize: parsePositiveInteger(params.get('pageSize'), defaultPageSize),
        viewportThresholdPercent: parsePercentInteger(params.get('viewportThreshold'), defaultViewportThresholdPercent),
        filters,
        selectedFields
    };
}

function syncUrlState(state: ServerUrlState): void {
    const params = new URLSearchParams();

    if (state.collectionId !== defaultCollectionId) {
        params.set('collection', state.collectionId);
    }

    if (state.sortColumn !== defaultSortColumn) {
        params.set('sort', state.sortColumn);
    }

    if (state.sortAscending) {
        params.set('dir', 'asc');
    }

    if (state.messageFormat !== defaultMessageFormat) {
        params.set('format', state.messageFormat);
    }

    if (state.pageSize !== defaultPageSize) {
        params.set('pageSize', String(state.pageSize));
    }

    if (state.viewportThresholdPercent !== defaultViewportThresholdPercent) {
        params.set('viewportThreshold', String(state.viewportThresholdPercent));
    }

    for (const filter of state.filters) {
        params.append('filterField', filter.field);
        params.append('filterOperator', filter.operator);
        params.append('filterValue', filter.value);
    }

    if (state.selectedFields.length !== knownTradeColumns.length) {
        for (const column of state.selectedFields) {
            params.append('column', column);
        }
    }

    const nextSearch = params.toString();
    window.history.replaceState(
        null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
}

export default function ServerSideGridView(): React.ReactElement {
    const initialUrlState = useMemo(() => getInitialUrlState(), []);
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState(initialUrlState.collectionId);
    const [sortColumn, setSortColumn] = useState(initialUrlState.sortColumn);
    const [sortAscending, setSortAscending] = useState(initialUrlState.sortAscending);
    const [messageFormat, setMessageFormat] = useState<MessageFormat>(initialUrlState.messageFormat);
    const [pageSize, setPageSize] = useState(initialUrlState.pageSize);
    const [pageSizeInput, setPageSizeInput] = useState(String(initialUrlState.pageSize));
    const [viewportThresholdPercent, setViewportThresholdPercent] = useState(initialUrlState.viewportThresholdPercent);
    const [viewportThresholdInput, setViewportThresholdInput] = useState(String(initialUrlState.viewportThresholdPercent));
    const [filters, setFilters] = useState<AppliedFilter[]>(initialUrlState.filters);
    const [gridVisible, setGridVisible] = useState(true);

    const columnSelection = useColumnSelection(initialUrlState.selectedFields);
    const { selectedFields } = columnSelection;

    const buildColDef = useCallback(
        (field: string) => buildColumnDef(field),
        []
    );
    const data = useCollectionData(buildColDef, { unboundedViewport: false });

    const pageSizeRef = useRef(pageSize);
    const isMountedRef = useRef(false);
    const scrollViewportDebounceRef = useRef<number | null>(null);
    const clientRef = useRef<WebHostClient | null>(null);
    const isApplyingGridStateRef = useRef(false);

    const resetViewportToFirstPage = useCallback((): ViewportWindow => {
        const initialWindow = buildViewportWindow(1, pageSizeRef.current, null);
        data.subscribedViewportRef.current = initialWindow;
        data.pendingScrollToTopRef.current = true;
        return initialWindow;
    }, [data.pendingScrollToTopRef, data.subscribedViewportRef]);

    const computeViewportWindow = useCallback((firstRow: number): ViewportWindow => {
        const current = data.subscribedViewportRef.current;
        if (!current) {
            return buildViewportWindow(1, pageSizeRef.current, data.totalCountRef.current);
        }

        let nextWindow = current;
        while (firstRow >= getViewportExpansionRow(nextWindow, pageSizeRef.current, viewportThresholdPercent)) {
            const expandedWindow = buildViewportWindow(
                getViewportPageCount(nextWindow, pageSizeRef.current) + 1,
                pageSizeRef.current,
                data.totalCountRef.current
            );
            if (expandedWindow.start === nextWindow.start && expandedWindow.end === nextWindow.end) {
                break;
            }
            nextWindow = expandedWindow;
        }

        return nextWindow;
    }, [data.subscribedViewportRef, data.totalCountRef, viewportThresholdPercent]);

    const requestViewportWindow = useCallback(
        (firstRow: number, lastRow: number, options: { includeGridViewportLog: boolean }) => {
            const client = clientRef.current;
            if (!client?.isConnected || firstRow < 0 || lastRow < firstRow) {
                return;
            }

            const nextWindow = computeViewportWindow(firstRow);
            if (options.includeGridViewportLog) {
                data.appendLog(`grid viewport: ${firstRow.toLocaleString()} - ${lastRow.toLocaleString()}`);
            }

            const current = data.subscribedViewportRef.current;
            const unchanged = current
                && current.start === nextWindow.start
                && current.end === nextWindow.end;
            if (unchanged) {
                return;
            }

            const pageSizeWindow = nextWindow.end - nextWindow.start + 1;
            data.appendLog(
                `changing viewport: ${nextWindow.start.toLocaleString()} - ${nextWindow.end.toLocaleString()} | page size ${pageSizeWindow}`
            );
            client.setViewport(nextWindow.start, pageSizeWindow);
            data.subscribedViewportRef.current = nextWindow;
        },
        [computeViewportWindow, data]
    );

    const normalisedFilters = useMemo(
        () => filters
            .filter((filter) => filter.field && filter.field.trim().length > 0)
            .map((filter) => ({
                field: filter.field,
                operator: filter.operator,
                value: filter.value
            })),
        [filters]
    );

    const connect = useCallback(() => {
        data.isReloadingGridRef.current = true;
        data.clearState();
        data.setIsLoadingSnapshot(true);
        const initialWindow = resetViewportToFirstPage();
        data.appendLog(
            `sync view: ${collectionId} | order by ${sortColumn} ${sortAscending ? 'asc' : 'desc'} | viewport ${initialWindow.start.toLocaleString()} - ${initialWindow.end.toLocaleString()}`
        );
        clientRef.current?.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize: initialWindow.end - initialWindow.start + 1,
            startIndex: initialWindow.start,
            filters: normalisedFilters,
            fields: selectedFields,
            messageFormat
        });
    }, [collectionId, data, messageFormat, normalisedFilters, resetViewportToFirstPage, selectedFields, sortAscending, sortColumn]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        data.isReloadingGridRef.current = false;
        setStatus('Disconnected');
        data.setIsLoadingSnapshot(false);
        data.clearState();
        data.appendLog('disconnect');
    }, [data]);

    const onPageSizeChanged = useCallback((nextPageSize: number) => {
        if (!Number.isFinite(nextPageSize) || nextPageSize < 1) {
            return;
        }

        setPageSize(Math.floor(nextPageSize));
    }, []);

    const onViewportThresholdChanged = useCallback((nextViewportThresholdPercent: number) => {
        if (!Number.isFinite(nextViewportThresholdPercent)) {
            return;
        }

        setViewportThresholdPercent(Math.min(100, Math.max(1, Math.floor(nextViewportThresholdPercent))));
    }, []);

    const commitPageSize = useCallback((nextPageSize: string) => {
        const normalizedPageSize = Number(nextPageSize.trim());
        if (!Number.isFinite(normalizedPageSize) || normalizedPageSize < 1) {
            setPageSizeInput(String(pageSize));
            return;
        }

        const wholePageSize = Math.floor(normalizedPageSize);
        setPageSizeInput(String(wholePageSize));
        if (wholePageSize !== pageSize) {
            onPageSizeChanged(wholePageSize);
        }
    }, [onPageSizeChanged, pageSize]);

    const commitViewportThreshold = useCallback((nextViewportThreshold: string) => {
        const normalizedViewportThreshold = Number(nextViewportThreshold.trim());
        if (!Number.isFinite(normalizedViewportThreshold)) {
            setViewportThresholdInput(String(viewportThresholdPercent));
            return;
        }

        const wholeViewportThreshold = Math.min(100, Math.max(1, Math.floor(normalizedViewportThreshold)));
        setViewportThresholdInput(String(wholeViewportThreshold));
        if (wholeViewportThreshold !== viewportThresholdPercent) {
            onViewportThresholdChanged(wholeViewportThreshold);
        }
    }, [onViewportThresholdChanged, viewportThresholdPercent]);

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
        setPageSizeInput(String(pageSize));
        pageSizeRef.current = pageSize;
    }, [pageSize]);

    useEffect(() => {
        setViewportThresholdInput(String(viewportThresholdPercent));
    }, [viewportThresholdPercent]);

    useEffect(() => {
        const api = data.gridApiRef.current;
        if (!api || data.columnDefs.length === 0) {
            return;
        }

        isApplyingGridStateRef.current = true;
        api.applyColumnState({
            defaultState: { sort: null },
            state: [{ colId: sortColumn, sort: sortAscending ? 'asc' : 'desc' }]
        });
        api.setFilterModel(buildGridFilterModel(normalisedFilters));
        window.setTimeout(() => {
            isApplyingGridStateRef.current = false;
        }, 0);
    }, [data.columnDefs, data.gridApiRef, normalisedFilters, sortAscending, sortColumn]);

    useEffect(() => {
        syncUrlState({
            collectionId,
            sortColumn,
            sortAscending,
            messageFormat,
            pageSize,
            viewportThresholdPercent,
            filters: normalisedFilters,
            selectedFields
        });
    }, [collectionId, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn, viewportThresholdPercent]);

    useEffect(() => {
        const client = clientRef.current;
        if (!client?.isConnected) {
            return;
        }

        data.isReloadingGridRef.current = true;
        data.clearState();
        data.setIsLoadingSnapshot(true);
        const initialWindow = resetViewportToFirstPage();
        client.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize: initialWindow.end - initialWindow.start + 1,
            startIndex: initialWindow.start,
            filters: normalisedFilters,
            fields: selectedFields,
            messageFormat
        });
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [collectionId, messageFormat, normalisedFilters, selectedFields, sortAscending, sortColumn]);

    useEffect(() => {
        if (!isMountedRef.current) {
            isMountedRef.current = true;
            return;
        }

        const client = clientRef.current;
        if (!client?.isConnected) {
            return;
        }

        const nextWindow = buildViewportWindow(1, pageSize, data.totalCountRef.current);
        data.subscribedViewportRef.current = nextWindow;
        data.pendingScrollToTopRef.current = true;
        client.setViewport(nextWindow.start, nextWindow.end - nextWindow.start + 1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [pageSize]);

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
                    data.totalCount !== null ? `${data.totalCount.toLocaleString()} rows total` : 'snapshot pending'
                ),
                data.snapshotStats
                    ? React.createElement(
                        'span',
                        null,
                        `snapshot ${data.snapshotStats.rowCount.toLocaleString()} rows | `
                        + `wait ${data.snapshotStats.waitMs.toFixed(0)}ms | `
                        + `transfer ${data.snapshotStats.transferMs.toFixed(0)}ms | `
                        + `render ${data.snapshotStats.renderMs.toFixed(0)}ms`
                    )
                    : null
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
                'Page size',
                React.createElement(SearchableDropdown, {
                    id: 'page-size',
                    value: pageSizeInput,
                    options: defaultPageSizes.map((size) => String(size)),
                    onInputChange: setPageSizeInput,
                    onCommit: commitPageSize,
                    inputMode: 'numeric'
                })
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Expand threshold %',
                React.createElement(SearchableDropdown, {
                    id: 'viewport-threshold',
                    value: viewportThresholdInput,
                    options: defaultViewportThresholdPercents.map((percent) => String(percent)),
                    onInputChange: setViewportThresholdInput,
                    onCommit: commitViewportThreshold,
                    inputMode: 'numeric'
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
                    'Loading snapshot…'
                )
                : null,
            React.createElement(
                'div',
                { className: 'ag-theme-balham', style: { width: '100%', height: '100%' } },
                React.createElement(AgGridReact<RowData>, {
                    onGridReady: (params) => {
                        data.gridApiRef.current = params.api;
                        isApplyingGridStateRef.current = true;
                        params.api.applyColumnState({
                            defaultState: { sort: null },
                            state: [{ colId: sortColumn, sort: sortAscending ? 'asc' : 'desc' }]
                        });
                        params.api.setFilterModel(buildGridFilterModel(normalisedFilters));
                        window.setTimeout(() => {
                            isApplyingGridStateRef.current = false;
                        }, 0);
                    },
                    onSortChanged: (event) => {
                        if (isApplyingGridStateRef.current || data.isReloadingGridRef.current) {
                            return;
                        }

                        const sortedColumns = event.api.getColumnState()
                            .filter((columnState) => columnState.sort === 'asc' || columnState.sort === 'desc');
                        if (sortedColumns.length !== 1) {
                            // Zero sorted columns is the transient state produced while our own
                            // applyColumnState call clears the previous sort before reapplying the
                            // current one. More than one sorted column is a stale/mixed state AG Grid
                            // can report right after rowData is refreshed post-sort (a leftover marker
                            // plus the new one). Neither reflects a genuine single-column user click -
                            // this tool only supports single-column sort - so both must be ignored
                            // rather than deriving an incorrect combination that would silently
                            // override the server-side subscription's sort.
                            return;
                        }

                        const sortedColumn = sortedColumns[0];
                        if (!knownTradeColumnSet.has(sortedColumn.colId ?? '')) {
                            return;
                        }

                        const nextSortColumn = sortedColumn.colId as string;
                        const nextSortAscending = sortedColumn.sort !== 'desc';
                        data.appendLog(`order by ${nextSortColumn} ${nextSortAscending ? 'asc' : 'desc'}`);
                        if (nextSortColumn !== sortColumn) {
                            setSortColumn(nextSortColumn);
                        }
                        if (nextSortAscending !== sortAscending) {
                            setSortAscending(nextSortAscending);
                        }
                    },
                    onFilterChanged: (event) => {
                        if (isApplyingGridStateRef.current || data.isReloadingGridRef.current || data.isLoadingSnapshot) {
                            return;
                        }

                        const nextFilters = buildAppliedFiltersFromGridModel(event.api.getFilterModel());
                        if (!areFiltersEqual(filters, nextFilters)) {
                            data.appendLog(nextFilters.length === 0
                                ? 'filters: none'
                                : `filters: ${nextFilters.map((filter) => `${filter.field} ${filter.operator} ${filter.value}`).join(', ')}`);
                        }
                        setFilters((current) => areFiltersEqual(current, nextFilters) ? current : nextFilters);
                    },
                    onBodyScrollEnd: () => {
                        const api = data.gridApiRef.current;
                        const client = clientRef.current;
                        if (!api || !client?.isConnected || data.isLoadingSnapshot || data.isReloadingGridRef.current) {
                            return;
                        }
                        const firstRow = api.getFirstDisplayedRowIndex();
                        const lastRow = api.getLastDisplayedRowIndex();
                        if (scrollViewportDebounceRef.current !== null) {
                            clearTimeout(scrollViewportDebounceRef.current);
                        }
                        scrollViewportDebounceRef.current = window.setTimeout(() => {
                            scrollViewportDebounceRef.current = null;
                            const innerApi = data.gridApiRef.current;
                            if (!innerApi || data.isReloadingGridRef.current) {
                                return;
                            }
                            const firstRowInner = innerApi.getFirstDisplayedRowIndex();
                            const lastRowInner = innerApi.getLastDisplayedRowIndex();
                            if (firstRowInner >= 0 && lastRowInner >= firstRowInner) {
                                requestViewportWindow(firstRowInner, lastRowInner, { includeGridViewportLog: true });
                            }
                        }, 150);
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
