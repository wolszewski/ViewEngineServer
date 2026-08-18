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
const placeholderKeyPrefix = '__ph_';
const scrollBoundaryThreshold = 0.3;
const scrollDebounceMs = 150;
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

function makePlaceholder(index: number): RowData {
    return { key: `${placeholderKeyPrefix}${index}` };
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
    const virtualRowsRef = useRef<RowData[]>([]);
    const loadedWindowRef = useRef<{ start: number; end: number } | null>(null);
    const scrollDebounceRef = useRef<number | null>(null);
    const pageSizeRef = useRef(pageSize);
    const totalCountRef = useRef<number | null>(totalCount);
    pageSizeRef.current = pageSize;
    totalCountRef.current = totalCount;
    const clientRef = useRef<WebHostClient | null>(null);
    const handleDeltaEventRef = useRef<(event: DeltaEvent) => void>(() => {});
    const initialRowData = useMemo<RowData[]>(() => [], []);
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const orderedIdsRef = useRef<string[]>([]);

    const defaultColDef = useMemo<ColDef<RowData>>(() => ({
        sortable: true,
        filter: true,
        floatingFilter: true,
        resizable: true,
        enableCellChangeFlash: true,
        sortingOrder: ['asc', 'desc']
    }), []);

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        orderedIdsRef.current = [];
        virtualRowsRef.current = [];
        loadedWindowRef.current = null;
        if (scrollDebounceRef.current !== null) {
            clearTimeout(scrollDebounceRef.current);
            scrollDebounceRef.current = null;
        }
        setColumnDefs([]);
        setTotalCount(null);
        setSnapshotStats(null);
        latencyAccRef.current = { maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [], recentTotalMs: 0 };
        setLatencySummary({ maxMs: 0, avgMs: 0, sampleCount: 0 });
        if (gridApiRef.current) {
            gridApiRef.current.setGridOption('rowData', []);
        }
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
        const snapStart = snapshot.startIndex ?? 0;
        const newTotalCount = snapshot.totalCount;

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

        if (virtualRowsRef.current.length !== newTotalCount) {
            const newBuffer = Array.from({ length: newTotalCount }, (_, i) => makePlaceholder(i));
            for (let i = 0; i < rows.length && snapStart + i < newTotalCount; i++) {
                newBuffer[snapStart + i] = rows[i];
            }
            virtualRowsRef.current = newBuffer;
        } else {
            const old = loadedWindowRef.current;
            if (old) {
                for (let i = old.start; i < old.end && i < virtualRowsRef.current.length; i++) {
                    virtualRowsRef.current[i] = makePlaceholder(i);
                }
            }
            for (let i = 0; i < rows.length && snapStart + i < newTotalCount; i++) {
                virtualRowsRef.current[snapStart + i] = rows[i];
            }
        }

        loadedWindowRef.current = { start: snapStart, end: snapStart + rows.length };

        setTotalCount(newTotalCount);
        setSnapshotStats({ rowCount: rows.length, loadMs: snapshot.loadMs });
        setIsLoadingSnapshot(false);

        if (rows.length > 0) {
            setColumnsFromRow(rows[0]);
        } else {
            setColumnDefs([]);
        }

        if (gridApiRef.current && gridVisibleRef.current) {
            gridApiRef.current.setGridOption('rowData', [...virtualRowsRef.current]);
        }
    }, [setColumnsFromRow]);

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
        if (!api || !gridVisibleRef.current) {
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

        if (gridApiRef.current && gridVisibleRef.current) {
            const loadedStart = loadedWindowRef.current?.start ?? 0;
            gridApiRef.current.applyTransaction({
                add: [row],
                addIndex: loadedStart + clampedPosition
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
        if (!row || !gridApiRef.current || !gridVisibleRef.current) {
            return;
        }

        gridApiRef.current.applyTransaction({ remove: [row] });
    }, []);

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
        clearState();
        setIsLoadingSnapshot(true);
        clientRef.current?.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize,
            startIndex: 0,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined,
            messageFormat
        });
    }, [clearState, collectionId, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        setStatus('Disconnected');
        setIsLoadingSnapshot(false);
        clearState();
    }, [clearState]);

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

        clearState();
        setIsLoadingSnapshot(true);
        client.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize,
            startIndex: 0,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined,
            messageFormat
        });
    }, [clearState, collectionId, filters, messageFormat, normalisedFilters, pageSize, selectedFields, sortAscending, sortColumn]);

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
            { className: 'status' },
            totalCount !== null
                ? `${totalCount.toLocaleString()} rows total`
                : ''
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
                        if (isApplyingGridStateRef.current) {
                            return;
                        }

                        const sortedColumn = event.api.getColumnState()
                            .find((columnState) => columnState.sort === 'asc' || columnState.sort === 'desc');
                        const nextSortColumn = sortedColumn?.colId && knownTradeColumnSet.has(sortedColumn.colId)
                            ? sortedColumn.colId
                            : defaultSortColumn;
                        const nextSortAscending = sortedColumn?.sort !== 'desc';
                        if (nextSortColumn !== sortColumn) {
                            setSortColumn(nextSortColumn);
                        }
                        if (nextSortAscending !== sortAscending) {
                            setSortAscending(nextSortAscending);
                        }
                    },
                    onFilterChanged: (event) => {
                        if (isApplyingGridStateRef.current) {
                            return;
                        }

                        const nextFilters = buildAppliedFiltersFromGridModel(event.api.getFilterModel());
                        setFilters((current) => areFiltersEqual(current, nextFilters) ? current : nextFilters);
                    },
                    onBodyScroll: (event) => {
                        const loadedWindow = loadedWindowRef.current;
                        const currentTotalCount = totalCountRef.current;
                        if (!loadedWindow || currentTotalCount === null || !clientRef.current?.isConnected) {
                            return;
                        }

                        const firstVisible = event.api.getFirstDisplayedRowIndex();
                        const lastVisible = event.api.getLastDisplayedRowIndex();
                        const currentPageSize = pageSizeRef.current;
                        const threshold = Math.max(5, Math.floor(currentPageSize * scrollBoundaryThreshold));
                        const nearBottom = lastVisible >= loadedWindow.end - threshold;
                        const nearTop = firstVisible <= loadedWindow.start + threshold && loadedWindow.start > 0;

                        if (!nearBottom && !nearTop) {
                            return;
                        }

                        if (scrollDebounceRef.current !== null) {
                            clearTimeout(scrollDebounceRef.current);
                        }

                        scrollDebounceRef.current = window.setTimeout(() => {
                            scrollDebounceRef.current = null;
                            const api = gridApiRef.current;
                            if (!api || !clientRef.current?.isConnected) {
                                return;
                            }

                            const first = api.getFirstDisplayedRowIndex();
                            const last = api.getLastDisplayedRowIndex();
                            const mid = Math.floor((first + last) / 2);
                            const newStart = Math.max(0, mid - Math.floor(currentPageSize / 2));
                            const clampedStart = Math.min(newStart, Math.max(0, currentTotalCount - currentPageSize));

                            if (clampedStart !== loadedWindowRef.current?.start) {
                                setIsLoadingSnapshot(true);
                                clientRef.current.setViewport(clampedStart, currentPageSize);
                            }
                        }, scrollDebounceMs);
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
        )
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
