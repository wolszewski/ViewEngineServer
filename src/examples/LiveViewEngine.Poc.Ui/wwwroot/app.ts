import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AgGridReact } from 'ag-grid-react';
import { AllCommunityModule, ModuleRegistry, type ColDef, type GridApi } from 'ag-grid-community';
import {
    WebHostClient,
    type DeltaEvent,
    type MessageFormat,
    type RowData,
    type RowInsertEvent,
    type RowRemoveEvent,
    type RowUpdateEvent,
    type SnapshotEvent
} from './webHostClient';

ModuleRegistry.registerModules([AllCommunityModule]);

const defaultPageSizes = [25, 50, 100];
const defaultCollectionId = 'trades';
const defaultSortColumn = 'quantity';
const defaultPageSize = 50;
const defaultMessageFormat: MessageFormat = 'compact';
const latencyWindowSize = 500;
const maxExpandedViewportRows = 5_000;
const slidingViewportRows = 5_000;
const slidingViewportBehindRows = 4_000;
const slidingViewportAheadRows = 1_000;
const impliedFields = new Set(['key']);
const knownTradeColumns = [
    'tradeId',
    'createdDate',
    'updatedDate',
    'accountId',
    'quantity',
    'price',
    'side',
    'status',
    'notional',
    ...Array.from({ length: 30 }, (_, index) => `stringField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 23 }, (_, index) => `intField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, index) => `decimalField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, index) => `enumField${index.toString().padStart(2, '0')}`)
];
const columnGroups: Array<{ label: string; columns: string[] }> = [
    { label: 'string',  columns: ['tradeId', ...Array.from({ length: 30 }, (_, i) => `stringField${i.toString().padStart(2, '0')}`)] },
    { label: 'int',     columns: ['accountId', 'quantity', ...Array.from({ length: 23 }, (_, i) => `intField${i.toString().padStart(2, '0')}`)] },
    { label: 'decimal', columns: ['price', 'notional', ...Array.from({ length: 20 }, (_, i) => `decimalField${i.toString().padStart(2, '0')}`)] },
    { label: 'enum',    columns: ['side', 'status', ...Array.from({ length: 20 }, (_, i) => `enumField${i.toString().padStart(2, '0')}`)] },
    { label: 'date',    columns: ['createdDate', 'updatedDate'] }
];
const knownTradeColumnSet = new Set(knownTradeColumns);
const numericTradeColumnSet = new Set([
    ...columnGroups.find((group) => group.label === 'int')?.columns ?? [],
    ...columnGroups.find((group) => group.label === 'decimal')?.columns ?? []
]);
const dateTradeColumnSet = new Set(columnGroups.find((group) => group.label === 'date')?.columns ?? []);
const filterOperatorSet = new Set(['eq', 'notEq', 'gt', 'gte', 'lt', 'lte', 'contains']);
const textFilterOptions = ['contains', 'equals', 'notEqual'] as const;
const orderedFilterOptions = ['equals', 'notEqual', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual'] as const;
const sharedFilterParams = {
    buttons: ['apply', 'clear'],
    closeOnApply: true,
    maxNumConditions: 1
} as const;

interface AppliedFilter {
    field: string;
    operator: string;
    value: string;
}

interface GridFilterConditionModel {
    type?: string;
    filter?: string | number | null;
    dateFrom?: string | null;
    conditions?: GridFilterConditionModel[];
}

type GridFilterModelState = Record<string, GridFilterConditionModel>;

interface AppUrlState {
    collectionId: string;
    sortColumn: string;
    sortAscending: boolean;
    messageFormat: MessageFormat;
    pageSize: number;
    filters: AppliedFilter[];
    selectedFields: string[];
}

function parsePositiveInteger(value: string | null, fallback: number): number {
    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed < 1) {
        return fallback;
    }

    return Math.floor(parsed);
}

function getInitialUrlState(): AppUrlState {
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
        sortAscending: params.get('dir') !== 'desc',
        messageFormat: params.get('format') === 'json' ? 'json' : defaultMessageFormat,
        pageSize: parsePositiveInteger(params.get('pageSize'), defaultPageSize),
        filters,
        selectedFields
    };
}

function syncUrlState(state: AppUrlState): void {
    const params = new URLSearchParams();

    if (state.collectionId !== defaultCollectionId) {
        params.set('collection', state.collectionId);
    }

    if (state.sortColumn !== defaultSortColumn) {
        params.set('sort', state.sortColumn);
    }

    if (!state.sortAscending) {
        params.set('dir', 'desc');
    }

    if (state.messageFormat !== defaultMessageFormat) {
        params.set('format', state.messageFormat);
    }

    if (state.pageSize !== defaultPageSize) {
        params.set('pageSize', String(state.pageSize));
    }

    for (const filter of state.filters) {
        params.append('filterField', filter.field);
        params.append('filterOperator', filter.operator);
        params.append('filterValue', filter.value);
    }

    for (const column of state.selectedFields) {
        params.append('column', column);
    }

    const nextSearch = params.toString();
    const nextUrl = nextSearch.length > 0
        ? `${window.location.pathname}?${nextSearch}${window.location.hash}`
        : `${window.location.pathname}${window.location.hash}`;
    const currentUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;

    if (nextUrl !== currentUrl) {
        window.history.replaceState(null, '', nextUrl);
    }
}

function isGridFilterConditionModel(value: unknown): value is GridFilterConditionModel {
    return typeof value === 'object' && value !== null;
}

function getAgGridFilterType(field: string): NonNullable<ColDef<RowData>['filter']> {
    if (numericTradeColumnSet.has(field)) {
        return 'agNumberColumnFilter';
    }

    if (dateTradeColumnSet.has(field)) {
        return 'agDateColumnFilter';
    }

    return 'agTextColumnFilter';
}

function toAgGridFilterOperator(operator: string): string | null {
    switch (operator) {
        case 'eq':
            return 'equals';
        case 'notEq':
            return 'notEqual';
        case 'gt':
            return 'greaterThan';
        case 'gte':
            return 'greaterThanOrEqual';
        case 'lt':
            return 'lessThan';
        case 'lte':
            return 'lessThanOrEqual';
        case 'contains':
            return 'contains';
        default:
            return null;
    }
}

function fromAgGridFilterOperator(operator: string | undefined): string | null {
    switch (operator) {
        case 'equals':
            return 'eq';
        case 'notEqual':
            return 'notEq';
        case 'greaterThan':
            return 'gt';
        case 'greaterThanOrEqual':
            return 'gte';
        case 'lessThan':
            return 'lt';
        case 'lessThanOrEqual':
            return 'lte';
        case 'contains':
            return 'contains';
        default:
            return null;
    }
}

function parseNumberValue(raw: string | null | undefined): number | null {
    if (typeof raw !== 'string') {
        return null;
    }

    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : null;
}

function parseDateValue(raw: string | null | undefined): Date | null {
    if (typeof raw !== 'string') {
        return null;
    }

    const timestamp = Date.parse(raw);
    return Number.isFinite(timestamp) ? new Date(timestamp) : null;
}

function compareNullableNumbers(left: string | null | undefined, right: string | null | undefined): number {
    const leftValue = parseNumberValue(left);
    const rightValue = parseNumberValue(right);
    if (leftValue === null && rightValue === null) {
        return 0;
    }

    if (leftValue === null) {
        return -1;
    }

    if (rightValue === null) {
        return 1;
    }

    return leftValue - rightValue;
}

function compareNullableDates(left: string | null | undefined, right: string | null | undefined): number {
    const leftValue = parseDateValue(left)?.getTime() ?? null;
    const rightValue = parseDateValue(right)?.getTime() ?? null;
    if (leftValue === null && rightValue === null) {
        return 0;
    }

    if (leftValue === null) {
        return -1;
    }

    if (rightValue === null) {
        return 1;
    }

    return leftValue - rightValue;
}

function buildGridFilterModel(filters: AppliedFilter[]): GridFilterModelState {
    const model: GridFilterModelState = {};

    for (const filter of filters) {
        const operator = toAgGridFilterOperator(filter.operator);
        if (!operator || !knownTradeColumnSet.has(filter.field)) {
            continue;
        }

        model[filter.field] = dateTradeColumnSet.has(filter.field)
            ? {
                type: operator,
                dateFrom: filter.value
            }
            : {
                type: operator,
                filter: numericTradeColumnSet.has(filter.field) ? Number(filter.value) : filter.value
            };
    }

    return model;
}

function getGridFilterValue(field: string, model: GridFilterConditionModel): string | null {
    if (dateTradeColumnSet.has(field)) {
        return typeof model.dateFrom === 'string' && model.dateFrom.trim().length > 0 ? model.dateFrom : null;
    }

    if (typeof model.filter === 'number') {
        return String(model.filter);
    }

    return typeof model.filter === 'string' && model.filter.trim().length > 0 ? model.filter : null;
}

function buildAppliedFiltersFromGridModel(model: Record<string, unknown>): AppliedFilter[] {
    const filters: AppliedFilter[] = [];

    for (const [field, rawModel] of Object.entries(model)) {
        if (!knownTradeColumnSet.has(field) || !isGridFilterConditionModel(rawModel)) {
            continue;
        }

        const condition = Array.isArray(rawModel.conditions) ? rawModel.conditions[0] : rawModel;
        if (!isGridFilterConditionModel(condition)) {
            continue;
        }

        const operator = fromAgGridFilterOperator(condition.type);
        const value = getGridFilterValue(field, condition);
        if (!operator || value === null) {
            continue;
        }

        filters.push({ field, operator, value });
    }

    return filters;
}

function areFiltersEqual(left: AppliedFilter[], right: AppliedFilter[]): boolean {
    return left.length === right.length
        && left.every((filter, index) => {
            const other = right[index];
            return other !== undefined
                && filter.field === other.field
                && filter.operator === other.operator
                && filter.value === other.value;
        });
}

function buildSubscriptionKey(
    collectionId: string,
    sortColumn: string,
    sortAscending: boolean,
    filters: AppliedFilter[],
    selectedFields: string[]
): string {
    return JSON.stringify([collectionId, sortColumn, sortAscending, filters, selectedFields]);
}

function buildColumnDef(field: string, sortColumn: string, sortAscending: boolean): ColDef<RowData> {
    const filter = getAgGridFilterType(field);
    const columnDef: ColDef<RowData> = {
        field,
        headerName: field,
        filter,
        floatingFilter: true,
        sort: field === sortColumn ? (sortAscending ? 'asc' : 'desc') : undefined,
        filterParams: {
            ...sharedFilterParams,
            filterOptions: filter === 'agTextColumnFilter' ? textFilterOptions : orderedFilterOptions
        }
    };

    if (numericTradeColumnSet.has(field)) {
        columnDef.comparator = (left, right) => compareNullableNumbers(
            typeof left === 'string' ? left : left == null ? null : String(left),
            typeof right === 'string' ? right : right == null ? null : String(right)
        );
        columnDef.filterValueGetter = (params) => parseNumberValue(params.data?.[field] ?? null);
    }

    if (dateTradeColumnSet.has(field)) {
        columnDef.comparator = (left, right) => compareNullableDates(
            typeof left === 'string' ? left : left == null ? null : String(left),
            typeof right === 'string' ? right : right == null ? null : String(right)
        );
        columnDef.filterValueGetter = (params) => parseDateValue(params.data?.[field] ?? null);
    }

    return columnDef;
}

interface SearchableDropdownProps {
    id: string;
    value: string;
    options: string[];
    onInputChange: (value: string) => void;
    onCommit: (value: string) => void;
    inputMode?: React.HTMLAttributes<HTMLInputElement>['inputMode'];
}

function SearchableDropdown({
    id,
    value,
    options,
    onInputChange,
    onCommit,
    inputMode
}: SearchableDropdownProps): React.ReactElement {
    const [isOpen, setIsOpen] = useState(false);
    const wrapperRef = useRef<HTMLDivElement | null>(null);
    const inputRef = useRef<HTMLInputElement | null>(null);

    const filteredOptions = useMemo(() => {
        const normalizedValue = value.trim().toLowerCase();
        return options.filter((option) => option.toLowerCase().includes(normalizedValue));
    }, [options, value]);

    useEffect(() => {
        const handlePointerDown = (event: PointerEvent) => {
            if (!wrapperRef.current?.contains(event.target as Node)) {
                setIsOpen(false);
            }
        };

        document.addEventListener('pointerdown', handlePointerDown);
        return () => document.removeEventListener('pointerdown', handlePointerDown);
    }, []);

    const commitValue = useCallback((nextValue: string) => {
        onCommit(nextValue);
        setIsOpen(false);
    }, [onCommit]);

    return React.createElement(
        'div',
        { className: 'searchable-dropdown', ref: wrapperRef },
        React.createElement(
            'div',
            { className: 'searchable-dropdown-input' },
            React.createElement('input', {
                id,
                ref: inputRef,
                value,
                inputMode,
                onFocus: () => setIsOpen(true),
                onClick: () => setIsOpen(true),
                onChange: (e: Event) => {
                    onInputChange((e.target as HTMLInputElement).value);
                    setIsOpen(true);
                },
                onBlur: () => {
                    window.setTimeout(() => {
                        if (!wrapperRef.current?.contains(document.activeElement)) {
                            commitValue(value);
                        }
                    }, 0);
                },
                onKeyDown: (e: KeyboardEvent) => {
                    if (e.key === 'ArrowDown') {
                        e.preventDefault();
                        setIsOpen(true);
                        return;
                    }

                    if (e.key === 'Enter') {
                        e.preventDefault();
                        commitValue((e.target as HTMLInputElement).value);
                        return;
                    }

                    if (e.key === 'Escape') {
                        e.preventDefault();
                        setIsOpen(false);
                    }
                }
            }),
            React.createElement(
                'button',
                {
                    type: 'button',
                    className: 'searchable-dropdown-toggle',
                    onMouseDown: (e: MouseEvent) => e.preventDefault(),
                    onClick: () => {
                        setIsOpen((current) => !current);
                        inputRef.current?.focus();
                    }
                },
                '▾'
            )
        ),
        isOpen && filteredOptions.length > 0
            ? React.createElement(
                'div',
                { className: 'searchable-dropdown-menu' },
                ...filteredOptions.map((option) => React.createElement(
                    'button',
                    {
                        key: option,
                        type: 'button',
                        className: 'searchable-dropdown-option',
                        onMouseDown: (e: MouseEvent) => e.preventDefault(),
                        onClick: () => {
                            onInputChange(option);
                            commitValue(option);
                        }
                    },
                    option
                ))
            )
            : null
    );
}

function App(): React.ReactElement {
    const initialUrlState = useMemo(() => getInitialUrlState(), []);
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState(initialUrlState.collectionId);
    const [sortColumn, setSortColumn] = useState(initialUrlState.sortColumn);
    const [sortAscending, setSortAscending] = useState(initialUrlState.sortAscending);
    const [messageFormat, setMessageFormat] = useState<MessageFormat>(initialUrlState.messageFormat);
    const [pageSize, setPageSize] = useState(initialUrlState.pageSize);
    const [pageSizeInput, setPageSizeInput] = useState(String(initialUrlState.pageSize));
    const [filters, setFilters] = useState<AppliedFilter[]>(initialUrlState.filters);
    const [selectedFields, setSelectedFields] = useState<string[]>(initialUrlState.selectedFields);
    const [isSelectingColumns, setIsSelectingColumns] = useState(false);
    const [draftColumns, setDraftColumns] = useState<Set<string>>(new Set());
    const [eventLog, setEventLog] = useState<string[]>([
        `order by ${initialUrlState.sortColumn} ${initialUrlState.sortAscending ? 'asc' : 'desc'}`
    ]);
    const [rowData, setRowData] = useState<RowData[]>([]);
    const [totalCount, setTotalCount] = useState<number | null>(null);
    const [columnDefs, setColumnDefs] = useState<ColDef<RowData>[]>([]);
    const [latencySummary, setLatencySummary] = useState({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    const latencyAccRef = useRef({ maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [] as number[], recentTotalMs: 0 });
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [snapshotStats, setSnapshotStats] = useState<{ rowCount: number; loadMs: number } | null>(null);
    const [gridVisible, setGridVisible] = useState(true);
    const gridVisibleRef = useRef(true);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const isApplyingGridStateRef = useRef(false);
    const isReloadingGridRef = useRef(false);
    const scrollViewportDebounceRef = useRef<number | null>(null);
    const visibleRowCountRef = useRef(0);
    const subscribedViewportRef = useRef<{ start: number; end: number } | null>(null);
    const rowsByPositionRef = useRef<Map<number, RowData>>(new Map());
    const totalCountRef = useRef<number | null>(null);
    const datasourceParamsRef = useRef({ collectionId, sortColumn, sortAscending, pageSize, filters: [] as AppliedFilter[], selectedFields, messageFormat });
    const clientRef = useRef<WebHostClient | null>(null);
    const handleDeltaEventRef = useRef<(event: DeltaEvent) => void>(() => {});
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());

    const publishRowsFromWindow = useCallback(() => {
        const window = subscribedViewportRef.current;
        if (!window) {
            const ordered = Array.from(rowsByPositionRef.current.entries())
                .sort((left, right) => left[0] - right[0])
                .map((entry) => entry[1]);
            setRowData(ordered);
            return;
        }

        const nextRows: RowData[] = [];
        for (let position = window.start; position <= window.end; position += 1) {
            const row = rowsByPositionRef.current.get(position);
            if (!row) {
                break;
            }
            nextRows.push(row);
        }
        setRowData(nextRows);
    }, []);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        floatingFilter: true,
        resizable: true,
        enableCellChangeFlash: true,
        sortingOrder: ['asc', 'desc']
    }), []);

    const appendLog = useCallback((entry: string) => {
        setEventLog((current) => [...current.slice(-19), entry]);
    }, []);

    const computeViewportWindow = useCallback((firstRow: number, lastRow: number, effectiveSize: number) => {
        const current = subscribedViewportRef.current;
        const minLastRow = Math.max(firstRow, lastRow);
        if (!current) {
            const start = 0;
            const end = Math.max(minLastRow, firstRow + effectiveSize - 1);
            return { start, end };
        }

        const expandedStart = Math.min(current.start, firstRow);
        const expandedEnd = Math.max(current.end, minLastRow, firstRow + effectiveSize - 1);
        if ((expandedEnd - expandedStart + 1) <= maxExpandedViewportRows) {
            return { start: expandedStart, end: expandedEnd };
        }

        let start = Math.max(0, firstRow - slidingViewportBehindRows);
        let end = start + slidingViewportRows - 1;
        const mustCover = Math.max(minLastRow, firstRow + slidingViewportAheadRows);
        if (end < mustCover) {
            end = mustCover;
            start = Math.max(0, end - slidingViewportRows + 1);
        }
        return { start, end };
    }, []);

    const requestViewportWindow = useCallback(
        (firstRow: number, lastRow: number, options: { includeGridViewportLog: boolean }) => {
            const client = clientRef.current;
            if (!client?.isConnected || firstRow < 0 || lastRow < firstRow) {
                return;
            }

            const p = datasourceParamsRef.current;
            const effectiveSize = Math.max(p.pageSize, visibleRowCountRef.current * 10 || p.pageSize * 4);
            const nextWindow = computeViewportWindow(firstRow, lastRow, effectiveSize);
            if (options.includeGridViewportLog) {
                appendLog(`grid viewport: ${firstRow.toLocaleString()} - ${lastRow.toLocaleString()}`);
            }

            const current = subscribedViewportRef.current;
            const unchanged = current
                && current.start === nextWindow.start
                && current.end === nextWindow.end;
            if (unchanged) {
                return;
            }

            const pageSizeWindow = nextWindow.end - nextWindow.start + 1;
            appendLog(
                `changing viewport: ${nextWindow.start.toLocaleString()} - ${nextWindow.end.toLocaleString()} | page size ${pageSizeWindow}`
            );
            client.setViewport(nextWindow.start, pageSizeWindow);
            subscribedViewportRef.current = nextWindow;
        },
        [appendLog, computeViewportWindow]
    );

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        rowsByPositionRef.current.clear();
        totalCountRef.current = null;
        subscribedViewportRef.current = null;
        setRowData([]);
        if (scrollViewportDebounceRef.current !== null) {
            clearTimeout(scrollViewportDebounceRef.current);
            scrollViewportDebounceRef.current = null;
        }
        setTotalCount(null);
        setSnapshotStats(null);
        latencyAccRef.current = { maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [], recentTotalMs: 0 };
        setLatencySummary({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    }, []);

    const setColumnsFromRow = useCallback((row: RowData | undefined) => {
        if (!row) {
            return;
        }

        const fields = Object.keys(row).filter((f) => !impliedFields.has(f));
        if (fields.length === 0) {
            return;
        }

        setColumnDefs(fields.map((field) => buildColumnDef(field, sortColumn, sortAscending)));
    }, [sortAscending, sortColumn]);

    const recordLatency = useCallback((row: RowData) => {
        const updatedDate = typeof row.updatedDate === 'string' ? row.updatedDate : null;
        if (!updatedDate) {
            return;
        }

        const timestamp = Date.parse(updatedDate);
        if (!Number.isFinite(timestamp)) {
            return;
        }

        const latencyMs = Date.now() - timestamp;
        const acc = latencyAccRef.current;
        const recentLatencies = [...acc.recentLatencies, latencyMs];
        let recentTotalMs = acc.recentTotalMs + latencyMs;
        if (recentLatencies.length > latencyWindowSize) {
            recentTotalMs -= recentLatencies.shift() ?? 0;
        }
        const nextCount = recentLatencies.length;
        const nextAverage = nextCount === 0 ? 0 : recentTotalMs / nextCount;
        latencyAccRef.current = {
            sampleCount: nextCount,
            maxMs: Math.max(acc.maxMs, latencyMs),
            avgMs: nextAverage,
            recentLatencies,
            recentTotalMs
        };
    }, []);

    const applySnapshot = useCallback((snapshot: SnapshotEvent) => {
        const rows = (snapshot.rows ?? []).map((row) => ({ ...row }));

        const snapshotStart = snapshot.startIndex;
        const snapshotEnd = Math.max(snapshotStart, snapshotStart + rows.length - 1);
        appendLog(snapshot.isPartial
            ? `partial snapshot: ${snapshotStart.toLocaleString()} - ${snapshotEnd.toLocaleString()} (${snapshot.loadMs.toFixed(0)}ms)`
            : `snapshot: ${snapshotStart.toLocaleString()} - ${snapshotEnd.toLocaleString()} (${snapshot.loadMs.toFixed(0)}ms)`);

        if (!snapshot.isPartial) {
            rowsByPositionRef.current.clear();
            rowsByIdRef.current.clear();
            subscribedViewportRef.current = {
                start: snapshot.startIndex,
                end: Math.max(snapshot.startIndex, snapshot.startIndex + rows.length - 1)
            };
        }

        // Store rows in caches
        for (let i = 0; i < rows.length; i++) {
            rowsByPositionRef.current.set(snapshot.startIndex + i, rows[i]);
            const rowId = rows[i].key ?? rows[i].id;
            if (rowId) {
                rowsByIdRef.current.set(rowId, rows[i]);
            }
        }

        totalCountRef.current = snapshot.totalCount;
        setTotalCount(snapshot.totalCount);
        setSnapshotStats({ rowCount: rows.length, loadMs: snapshot.loadMs });

        if (rows.length > 0) {
            setColumnsFromRow(rows[0]);
        }

        setIsLoadingSnapshot(false);
        if (!snapshot.isPartial) {
            isReloadingGridRef.current = false;
        }
        publishRowsFromWindow();
    }, [publishRowsFromWindow, setColumnsFromRow]);

    const applyUpdate = useCallback((update: RowUpdateEvent) => {
        const rowId = update.rowId;
        if (!rowId) {
            return;
        }

        const existing = rowsByPositionRef.current.get(update.position) ?? rowsByIdRef.current.get(rowId);
        if (!existing) {
            return;
        }

        const changedFields = update.changedFields ?? {};
        const updated: RowData = { ...existing, ...changedFields };
        rowsByIdRef.current.set(rowId, updated);
        rowsByPositionRef.current.set(update.position, updated);
        publishRowsFromWindow();
    }, [publishRowsFromWindow]);

    const applyInsert = useCallback((insert: RowInsertEvent) => {
        if (columnDefs.length === 0 && insert.row) {
            setColumnsFromRow(insert.row);
        }
        const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => right - left);
        for (const position of positions) {
            if (position >= insert.position) {
                const row = rowsByPositionRef.current.get(position);
                if (row) {
                    rowsByPositionRef.current.set(position + 1, row);
                }
            }
        }
        rowsByPositionRef.current.set(insert.position, { ...insert.row });
        const insertedRowId = insert.row.key ?? insert.row.id;
        if (insertedRowId) {
            rowsByIdRef.current.set(insertedRowId, { ...insert.row });
        }
        const window = subscribedViewportRef.current;
        if (window) {
            rowsByPositionRef.current.delete(window.end + 1);
        }
        publishRowsFromWindow();
    }, [columnDefs.length, publishRowsFromWindow, setColumnsFromRow]);

    const applyRemove = useCallback((remove: RowRemoveEvent) => {
        const removedRow = rowsByPositionRef.current.get(remove.position);
        if (removedRow) {
            const removedRowId = removedRow.key ?? removedRow.id;
            if (removedRowId) {
                rowsByIdRef.current.delete(removedRowId);
            }
        }
        rowsByPositionRef.current.delete(remove.position);
        const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => left - right);
        for (const position of positions) {
            if (position > remove.position) {
                const row = rowsByPositionRef.current.get(position);
                if (row) {
                    rowsByPositionRef.current.set(position - 1, row);
                    rowsByPositionRef.current.delete(position);
                }
            }
        }
        publishRowsFromWindow();
    }, [publishRowsFromWindow]);

    const handleDeltaEvent = useCallback((event: DeltaEvent) => {
        if (event.type === 'snapshot') {
            applySnapshot(event);
        } else if (event.type === 'rowUpdate') {
            recordLatency(event.changedFields ?? {});
            applyUpdate(event);
        } else if (event.type === 'rowInsert') {
            applyInsert(event);
        } else if (event.type === 'rowRemove') {
            applyRemove(event);
        }
    }, [applyInsert, applyRemove, applySnapshot, applyUpdate, recordLatency]);
    handleDeltaEventRef.current = handleDeltaEvent;

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
        isReloadingGridRef.current = true;
        clearState();
        setIsLoadingSnapshot(true);
        datasourceParamsRef.current = { collectionId, sortColumn, sortAscending, pageSize, filters: normalisedFilters, selectedFields, messageFormat };
        const initialSize = Math.max(pageSize, visibleRowCountRef.current * 10 || pageSize * 4);
        const initialWindow = computeViewportWindow(0, Math.max(0, initialSize - 1), initialSize);
        subscribedViewportRef.current = initialWindow;
        appendLog(
            `subscribe: ${collectionId} | order by ${sortColumn} ${sortAscending ? 'asc' : 'desc'} | viewport ${initialWindow.start.toLocaleString()} - ${initialWindow.end.toLocaleString()}`
        );
        clientRef.current?.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize: initialWindow.end - initialWindow.start + 1,
            startIndex: initialWindow.start,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined,
            messageFormat
        });
    }, [appendLog, clearState, collectionId, computeViewportWindow, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        isReloadingGridRef.current = false;
        setStatus('Disconnected');
        setIsLoadingSnapshot(false);
        clearState();
        appendLog('disconnect');
    }, [appendLog, clearState]);

    const onPageSizeChanged = useCallback((nextPageSize: number) => {
        if (!Number.isFinite(nextPageSize) || nextPageSize < 1) {
            return;
        }

        setPageSize(Math.floor(nextPageSize));
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

    const openColumnSelector = useCallback(() => {
        setDraftColumns(new Set(selectedFields.length > 0 ? selectedFields : knownTradeColumns));
        setIsSelectingColumns(true);
    }, [selectedFields]);

    const toggleDraftColumn = useCallback((column: string, checked: boolean) => {
        setDraftColumns((current) => {
            const next = new Set(current);
            if (checked) { next.add(column); } else { next.delete(column); }
            return next;
        });
    }, []);

    const toggleDraftGroup = useCallback((columns: string[]) => {
        setDraftColumns((current) => {
            const allSelected = columns.every((c) => current.has(c));
            const next = new Set(current);
            if (allSelected) { columns.forEach((c) => next.delete(c)); }
            else { columns.forEach((c) => next.add(c)); }
            return next;
        });
    }, []);

    const commitColumns = useCallback(() => {
        const committed = knownTradeColumns.filter((c) => draftColumns.has(c));
        setSelectedFields(committed.length === knownTradeColumns.length ? [] : committed);
        setIsSelectingColumns(false);
    }, [draftColumns]);

    const cancelColumns = useCallback(() => {
        setIsSelectingColumns(false);
    }, []);

    useEffect(() => {
        clientRef.current = new WebHostClient('ws://127.0.0.1:5100/ws', {
            onStatus: setStatus,
            onEvent: (event) => handleDeltaEventRef.current(event)
        });

        return () => {
            clientRef.current?.disconnect();
            clientRef.current = null;
        };
    }, []);

    useEffect(() => {
        const handle = window.setInterval(() => {
            setLatencySummary({ ...latencyAccRef.current });
        }, 500);
        return () => clearInterval(handle);
    }, []);

    useEffect(() => {
        setPageSizeInput(String(pageSize));
    }, [pageSize]);

    useEffect(() => {
        const api = gridApiRef.current;
        if (!api || columnDefs.length === 0) {
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
    }, [columnDefs, normalisedFilters, sortAscending, sortColumn]);

    useEffect(() => {
        syncUrlState({
            collectionId,
            sortColumn,
            sortAscending,
            messageFormat,
            pageSize,
            filters: normalisedFilters,
            selectedFields
        });
    }, [collectionId, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn]);

    useEffect(() => {
        const client = clientRef.current;
        if (!client?.isConnected) {
            return;
        }

        isReloadingGridRef.current = true;
        datasourceParamsRef.current = { collectionId, sortColumn, sortAscending, pageSize, filters: normalisedFilters, selectedFields, messageFormat };
        clearState();
        setIsLoadingSnapshot(true);
        const initialSize = Math.max(pageSize, visibleRowCountRef.current * 10 || pageSize * 4);
        const initialWindow = computeViewportWindow(0, Math.max(0, initialSize - 1), initialSize);
        subscribedViewportRef.current = initialWindow;
        client.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize: initialWindow.end - initialWindow.start + 1,
            startIndex: initialWindow.start,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined,
            messageFormat
        });
    }, [clearState, collectionId, computeViewportWindow, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn]);

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
            .searchable-dropdown {
                position: relative;
                min-width: 14rem;
            }
            .searchable-dropdown-input {
                display: grid;
                grid-template-columns: 1fr auto;
            }
            .searchable-dropdown-input input {
                width: 100%;
                box-sizing: border-box;
                padding: 0.4rem 0.5rem;
                border: 1px solid #c7ced8;
                border-right: 0;
                border-radius: 0.25rem 0 0 0.25rem;
            }
            .searchable-dropdown-toggle {
                border: 1px solid #c7ced8;
                border-radius: 0 0.25rem 0.25rem 0;
                background: #f7f9fc;
                padding: 0 0.75rem;
                cursor: pointer;
            }
            .searchable-dropdown-menu {
                position: absolute;
                top: calc(100% + 0.25rem);
                left: 0;
                right: 0;
                display: flex;
                flex-direction: column;
                max-height: 14rem;
                overflow-y: auto;
                background: white;
                border: 1px solid #c7ced8;
                border-radius: 0.25rem;
                box-shadow: 0 0.4rem 1rem rgba(15, 23, 42, 0.12);
                z-index: 10;
            }
            .searchable-dropdown-option {
                border: 0;
                background: white;
                padding: 0.5rem 0.75rem;
                text-align: left;
                cursor: pointer;
            }
            .searchable-dropdown-option:hover {
                background: #f0f6ff;
            }
            .status {
                margin-bottom: 1rem;
                padding: 0.75rem;
                background: #f0f6ff;
                border-radius: 0.25rem;
            }
            .log-panel {
                margin-bottom: 1rem;
                border: 1px solid #dfe7f1;
                border-radius: 0.5rem;
                background: #f8fafc;
                overflow: hidden;
            }
            .log-header {
                display: flex;
                justify-content: space-between;
                gap: 1rem;
                padding: 0.5rem 0.75rem;
                background: #eaf2ff;
                border-bottom: 1px solid #dfe7f1;
                font-size: 0.8rem;
                font-weight: 600;
                color: #334155;
            }
            .log-window {
                display: flex;
                flex-direction: column;
                gap: 0.18rem;
                max-height: 12rem;
                overflow-y: auto;
                padding: 0.4rem 0.65rem;
                font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', Menlo, monospace;
                font-size: 0.72rem;
                color: #0f172a;
                line-height: 1.2;
            }
            .log-line {
                white-space: pre-wrap;
            }
            .filters {
                display: flex;
                flex-direction: column;
                gap: 0.75rem;
                margin-bottom: 1rem;
            }
            .filter-row {
                display: flex;
                gap: 0.75rem;
                align-items: end;
                flex-wrap: wrap;
                padding: 0.75rem;
                border: 1px solid #dfe7f1;
                border-radius: 0.5rem;
                background: #f9fbff;
            }
            .filter-chip {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 0.75rem;
                padding: 0.6rem 0.75rem;
                border: 1px solid #cbd5e1;
                border-radius: 999px;
                background: #eef6ff;
            }
            .empty-filters {
                color: #475569;
                font-style: italic;
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
        React.createElement('h1', null, 'LiveViewEngine PoC UI'),
        React.createElement(
            'div',
            { className: 'log-panel' },
            React.createElement(
                'div',
                { className: 'log-header' },
                React.createElement('span', null, status),
                React.createElement('span', null, totalCount !== null ? `${totalCount.toLocaleString()} rows total` : 'snapshot pending')
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
            React.createElement('button', { type: 'button', onClick: openColumnSelector }, 'Select columns'),
            React.createElement('button', { type: 'button', onClick: connect }, 'Connect'),
            React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect'),
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
            { className: 'filters' },
            isSelectingColumns
                ? React.createElement(
                    'div',
                    { className: 'filter-row', style: { flexWrap: 'wrap', gap: '0.5rem' } },
                    React.createElement(
                        'div',
                        { style: { width: '100%', display: 'flex', gap: '0.25rem', flexWrap: 'wrap', marginBottom: '0.25rem' } },
                        React.createElement('button', {
                            type: 'button',
                            onClick: () => setDraftColumns(new Set(knownTradeColumns))
                        }, 'All'),
                        React.createElement('button', {
                            type: 'button',
                            onClick: () => setDraftColumns(new Set())
                        }, 'None'),
                        ...columnGroups.map((group) =>
                            React.createElement('button', {
                                key: group.label,
                                type: 'button',
                                onClick: () => toggleDraftGroup(group.columns)
                            }, group.label)
                        )
                    ),
                    ...knownTradeColumns.map((column) =>
                        React.createElement(
                            'label',
                            { key: column, style: { display: 'flex', alignItems: 'center', gap: '0.25rem', cursor: 'pointer' } },
                            React.createElement('input', {
                                type: 'checkbox',
                                checked: draftColumns.has(column),
                                onChange: (e: Event) => toggleDraftColumn(column, (e.target as HTMLInputElement).checked)
                            }),
                            column
                        )
                    ),
                    React.createElement('button', { type: 'button', onClick: commitColumns }, 'Apply'),
                    React.createElement('button', { type: 'button', onClick: cancelColumns }, 'Cancel')
                )
                : null,
            selectedFields.length === 0
                ? React.createElement('div', { className: 'empty-filters' }, 'All columns subscribed (no column filter).')
                : React.createElement('div', { className: 'filter-chip' },
                    React.createElement('span', null, `Columns: ${selectedFields.join(', ')}`)
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
                    onGridReady: (params) => {
                        gridApiRef.current = params.api;
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
                        if (isApplyingGridStateRef.current || isReloadingGridRef.current) {
                            return;
                        }

                        const sortedColumn = event.api.getColumnState()
                            .find((columnState) => columnState.sort === 'asc' || columnState.sort === 'desc');
                        const nextSortColumn = sortedColumn?.colId && knownTradeColumnSet.has(sortedColumn.colId)
                            ? sortedColumn.colId
                            : defaultSortColumn;
                        const nextSortAscending = sortedColumn?.sort !== 'desc';
                        appendLog(`order by ${nextSortColumn} ${nextSortAscending ? 'asc' : 'desc'}`);
                        if (nextSortColumn !== sortColumn) {
                            setSortColumn(nextSortColumn);
                        }
                        if (nextSortAscending !== sortAscending) {
                            setSortAscending(nextSortAscending);
                        }
                    },
                    onFilterChanged: (event) => {
                        if (isApplyingGridStateRef.current || isReloadingGridRef.current || isLoadingSnapshot) {
                            return;
                        }

                        const nextFilters = buildAppliedFiltersFromGridModel(event.api.getFilterModel());
                        if (!areFiltersEqual(filters, nextFilters)) {
                            appendLog(nextFilters.length === 0
                                ? 'filters: none'
                                : `filters: ${nextFilters.map((filter) => `${filter.field} ${filter.operator} ${filter.value}`).join(', ')}`);
                        }
                        setFilters((current) => areFiltersEqual(current, nextFilters) ? current : nextFilters);
                    },
                    onBodyScrollEnd: () => {
                        const api = gridApiRef.current;
                        const client = clientRef.current;
                        if (!api || !client?.isConnected) {
                            return;
                        }
                        const firstRow = api.getFirstDisplayedRowIndex();
                        const lastRow = api.getLastDisplayedRowIndex();
                        const visibleCount = lastRow - firstRow + 1;
                        if (visibleCount > 0) {
                            visibleRowCountRef.current = visibleCount;
                        }
                        if (scrollViewportDebounceRef.current !== null) {
                            clearTimeout(scrollViewportDebounceRef.current);
                        }
                        scrollViewportDebounceRef.current = window.setTimeout(() => {
                            scrollViewportDebounceRef.current = null;
                            const innerApi = gridApiRef.current;
                            if (!innerApi) {
                                return;
                            }
                            const firstRowInner = innerApi.getFirstDisplayedRowIndex();
                            const lastRowInner = innerApi.getLastDisplayedRowIndex();
                            if (firstRowInner >= 0 && lastRowInner >= firstRowInner) {
                                requestViewportWindow(firstRowInner, lastRowInner, { includeGridViewportLog: true });
                            }
                        }, 150);
                    },
                    rowData,
                    columnDefs,
                    defaultColDef,
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

createRoot(document.getElementById('root')!).render(React.createElement(App));
