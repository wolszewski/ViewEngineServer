import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import React from 'react';
import type { ColDef, GridApi } from 'ag-grid-community';
import type {
    DeltaEvent,
    MessageFormat,
    RowData,
    RowInsertEvent,
    RowReplaceEvent,
    RowRemoveEvent,
    RowUpdateEvent,
    SnapshotEvent
} from './webHostClient';

export const defaultPageSizes = [25, 50, 100, 200];
export const defaultCollectionId = 'trades';
export const defaultSortColumn = 'tradeId';
export const defaultPageSize = 200;
export const defaultViewportThresholdPercents = [25, 50, 75];
export const defaultViewportThresholdPercent = 50;
export const defaultMessageFormat: MessageFormat = 'compact';
export const latencyWindowSize = 500;
export const impliedFields = new Set(['key']);

export const knownTradeColumns = [
    'tradeId',
    'createdDate',
    'updatedDate',
    'accountId',
    'quantity',
    'price',
    'side',
    'status',
    'isAlgo',
    'isManualReview',
    'notional',
    'variedNumber',
    ...Array.from({ length: 30 }, (_, index) => `stringField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 23 }, (_, index) => `intField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, index) => `decimalField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, index) => `enumField${index.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, index) => `boolField${index.toString().padStart(2, '0')}`)
];

export const columnGroups: Array<{ label: string; columns: string[] }> = [
    { label: 'string',  columns: ['tradeId', ...Array.from({ length: 30 }, (_, i) => `stringField${i.toString().padStart(2, '0')}`)] },
    { label: 'int',     columns: ['accountId', 'quantity', ...Array.from({ length: 23 }, (_, i) => `intField${i.toString().padStart(2, '0')}`)] },
    { label: 'decimal', columns: ['price', 'notional', 'variedNumber', ...Array.from({ length: 20 }, (_, i) => `decimalField${i.toString().padStart(2, '0')}`)] },
    { label: 'enum',    columns: ['side', 'status', ...Array.from({ length: 20 }, (_, i) => `enumField${i.toString().padStart(2, '0')}`)] },
    { label: 'boolean', columns: ['isAlgo', 'isManualReview', ...Array.from({ length: 20 }, (_, i) => `boolField${i.toString().padStart(2, '0')}`)] },
    { label: 'date',    columns: ['createdDate', 'updatedDate'] }
];

export const knownTradeColumnSet = new Set(knownTradeColumns);
export const numericTradeColumnSet = new Set([
    ...columnGroups.find((group) => group.label === 'int')?.columns ?? [],
    ...columnGroups.find((group) => group.label === 'decimal')?.columns ?? []
]);
export const dateTradeColumnSet = new Set(columnGroups.find((group) => group.label === 'date')?.columns ?? []);
export const filterOperatorSet = new Set(['eq', 'notEq', 'gt', 'gte', 'lt', 'lte', 'contains']);
export const textFilterOptions = ['contains', 'equals', 'notEqual'] as const;
export const orderedFilterOptions = ['equals', 'notEqual', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual'] as const;
export const sharedFilterParams = {
    buttons: ['apply', 'clear'],
    closeOnApply: true,
    maxNumConditions: 1
} as const;

/**
 * Stable module-level object (not recreated per render) passed as `defaultColDef` to AgGridReact.
 * A fresh object reference on every render makes AG Grid treat it as a gridOptions change, which
 * triggers it to re-derive column sort state from each colDef's declared `sort` - see buildColumnDef.
 */
export const defaultGridColDef: ColDef<RowData> = {
    sortable: true,
    filter: true,
    floatingFilter: true,
    resizable: true,
    enableCellChangeFlash: true,
    sortingOrder: ['asc', 'desc']
};

export const sharedAppStyles = `
body {
    font-family: Arial, sans-serif;
    margin: 2rem;
}
.tabs {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1rem;
}
.tabs button {
    padding: 0.5rem 1rem;
    border: 1px solid #c7ced8;
    background: #f7f9fc;
    border-radius: 0.25rem;
    cursor: pointer;
}
.tabs button.tab-active {
    background: #2563eb;
    border-color: #2563eb;
    color: white;
    font-weight: 600;
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
`;

export interface AppliedFilter {
    field: string;
    operator: string;
    value: string;
}

export interface GridFilterConditionModel {
    type?: string;
    filter?: string | number | null;
    dateFrom?: string | null;
    conditions?: GridFilterConditionModel[];
}

export type GridFilterModelState = Record<string, GridFilterConditionModel>;

export interface ViewportWindow {
    start: number;
    end: number;
}

export interface SnapshotStats {
    rowCount: number;
    waitMs: number;
    transferMs: number;
    renderMs: number;
}

export interface LatencySummary {
    maxMs: number;
    avgMs: number;
    sampleCount: number;
}

export function parsePositiveInteger(value: string | null, fallback: number): number {
    if (typeof value !== 'string' || value.trim().length === 0) {
        return fallback;
    }

    const parsed = Number(value);
    if (!Number.isFinite(parsed) || parsed < 1) {
        return fallback;
    }

    return Math.floor(parsed);
}

export function parsePercentInteger(value: string | null, fallback: number): number {
    if (typeof value !== 'string' || value.trim().length === 0) {
        return fallback;
    }

    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
        return fallback;
    }

    return Math.min(100, Math.max(1, Math.floor(parsed)));
}

export function buildViewportWindow(pageCount: number, pageSize: number, totalCount: number | null): ViewportWindow {
    const normalizedPageCount = Math.max(1, Math.floor(pageCount));
    const end = (normalizedPageCount * pageSize) - 1;
    if (totalCount === null || totalCount < 1) {
        return { start: 0, end };
    }

    return {
        start: 0,
        end: Math.min(end, totalCount - 1)
    };
}

export function getViewportPageCount(window: ViewportWindow, pageSize: number): number {
    return Math.max(1, Math.ceil((window.end - window.start + 1) / pageSize));
}

export function getViewportExpansionRow(window: ViewportWindow, pageSize: number, viewportThresholdPercent: number): number {
    const thresholdOffset = Math.min(pageSize - 1, Math.ceil((pageSize * viewportThresholdPercent) / 100));
    return ((getViewportPageCount(window, pageSize) - 1) * pageSize) + thresholdOffset;
}

export function isGridFilterConditionModel(value: unknown): value is GridFilterConditionModel {
    return typeof value === 'object' && value !== null;
}

export function getAgGridFilterType(field: string): NonNullable<ColDef<RowData>['filter']> {
    if (numericTradeColumnSet.has(field)) {
        return 'agNumberColumnFilter';
    }

    if (dateTradeColumnSet.has(field)) {
        return 'agDateColumnFilter';
    }

    return 'agTextColumnFilter';
}

export function toAgGridFilterOperator(operator: string): string | null {
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

export function fromAgGridFilterOperator(operator: string | undefined): string | null {
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

export function parseNumberValue(raw: string | null | undefined): number | null {
    if (typeof raw !== 'string') {
        return null;
    }

    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : null;
}

export function parseDateValue(raw: string | null | undefined): Date | null {
    if (typeof raw !== 'string') {
        return null;
    }

    const timestamp = Date.parse(raw);
    return Number.isFinite(timestamp) ? new Date(timestamp) : null;
}

export function compareNullableNumbers(left: string | null | undefined, right: string | null | undefined): number {
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

export function compareNullableDates(left: string | null | undefined, right: string | null | undefined): number {
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

export function buildGridFilterModel(filters: AppliedFilter[]): GridFilterModelState {
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

export function getGridFilterValue(field: string, model: GridFilterConditionModel): string | null {
    if (dateTradeColumnSet.has(field)) {
        return typeof model.dateFrom === 'string' && model.dateFrom.trim().length > 0 ? model.dateFrom : null;
    }

    if (typeof model.filter === 'number') {
        return String(model.filter);
    }

    return typeof model.filter === 'string' && model.filter.trim().length > 0 ? model.filter : null;
}

export function buildAppliedFiltersFromGridModel(model: Record<string, unknown>): AppliedFilter[] {
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

export function areFiltersEqual(left: AppliedFilter[], right: AppliedFilter[]): boolean {
    return left.length === right.length
        && left.every((filter, index) => {
            const other = right[index];
            return other !== undefined
                && filter.field === other.field
                && filter.operator === other.operator
                && filter.value === other.value;
        });
}

/**
 * Builds a column definition. `sort` is intentionally never set here: this tool controls sort state
 * exclusively at runtime via `api.applyColumnState`. Baking a `sort` value into a colDef is only an
 * "initial declaration" in AG Grid's model, and AG Grid re-derives sort state from colDef whenever
 * gridOptions changes (e.g. a new `defaultColDef` object reference on re-render) - a stale baked-in
 * value would then silently override whatever `applyColumnState` had set at runtime.
 */
export function buildColumnDef(field: string): ColDef<RowData> {
    const filter = getAgGridFilterType(field);
    const columnDef: ColDef<RowData> = {
        field,
        headerName: field,
        filter,
        floatingFilter: true,
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

export interface SearchableDropdownProps {
    id: string;
    value: string;
    options: string[];
    onInputChange: (value: string) => void;
    onCommit: (value: string) => void;
    inputMode?: React.HTMLAttributes<HTMLInputElement>['inputMode'];
}

export function SearchableDropdown({
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

export interface ColumnSelectionApi {
    selectedFields: string[];
    setSelectedFields: (fields: string[]) => void;
    isSelectingColumns: boolean;
    draftColumns: Set<string>;
    openColumnSelector: () => void;
    toggleDraftColumn: (column: string, checked: boolean) => void;
    toggleDraftGroup: (columns: string[]) => void;
    selectAllDraftColumns: () => void;
    selectNoDraftColumns: () => void;
    commitColumns: () => void;
    cancelColumns: () => void;
}

export function useColumnSelection(initialSelectedFields: string[]): ColumnSelectionApi {
    const [selectedFields, setSelectedFields] = useState<string[]>(initialSelectedFields);
    const [isSelectingColumns, setIsSelectingColumns] = useState(false);
    const [draftColumns, setDraftColumns] = useState<Set<string>>(new Set());

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

    const selectAllDraftColumns = useCallback(() => {
        setDraftColumns(new Set(knownTradeColumns));
    }, []);

    const selectNoDraftColumns = useCallback(() => {
        setDraftColumns(new Set());
    }, []);

    const commitColumns = useCallback(() => {
        const committed = knownTradeColumns.filter((c) => draftColumns.has(c));
        setSelectedFields(committed.length === knownTradeColumns.length ? [] : committed);
        setIsSelectingColumns(false);
    }, [draftColumns]);

    const cancelColumns = useCallback(() => {
        setIsSelectingColumns(false);
    }, []);

    return {
        selectedFields,
        setSelectedFields,
        isSelectingColumns,
        draftColumns,
        openColumnSelector,
        toggleDraftColumn,
        toggleDraftGroup,
        selectAllDraftColumns,
        selectNoDraftColumns,
        commitColumns,
        cancelColumns
    };
}

export function ColumnSelectorPanel(props: ColumnSelectionApi): React.ReactElement {
    const {
        selectedFields,
        isSelectingColumns,
        draftColumns,
        toggleDraftColumn,
        toggleDraftGroup,
        selectAllDraftColumns,
        selectNoDraftColumns,
        commitColumns,
        cancelColumns
    } = props;

    return React.createElement(
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
                        onClick: selectAllDraftColumns
                    }, 'All'),
                    React.createElement('button', {
                        type: 'button',
                        onClick: selectNoDraftColumns
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
    );
}

interface PendingSnapshotRenderMeasure {
    rowCount: number;
    waitMs: number;
    transferMs: number;
    startedAt: number;
    startIndex: number;
    endIndex: number;
}

export interface CollectionDataApi {
    rowData: RowData[];
    totalCount: number | null;
    columnDefs: ColDef<RowData>[];
    isLoadingSnapshot: boolean;
    setIsLoadingSnapshot: (value: boolean) => void;
    snapshotStats: SnapshotStats | null;
    latencySummary: LatencySummary;
    eventLog: string[];
    appendLog: (entry: string) => void;
    clearState: () => void;
    handleDeltaEventRef: React.MutableRefObject<(event: DeltaEvent) => void>;
    gridApiRef: React.MutableRefObject<GridApi<RowData> | null>;
    isReloadingGridRef: React.MutableRefObject<boolean>;
    pendingScrollToTopRef: React.MutableRefObject<boolean>;
    subscribedViewportRef: React.MutableRefObject<ViewportWindow | null>;
    totalCountRef: React.MutableRefObject<number | null>;
}

/**
 * Applies the live delta event stream (snapshot/insert/update/remove/replace) into row-by-position
 * and row-by-id caches, and republishes the visible row array.
 *
 * When `unboundedViewport` is true (browser-side full-collection subscriptions), the viewport window
 * is never set, so the "trim the row that fell off the page" logic in applyInsert/applyRemove/
 * applyReplace never fires - every row the server sends stays in the cache, which is required since
 * there is no server-side page boundary to enforce.
 */
export function useCollectionData(
    buildColDef: (field: string) => ColDef<RowData>,
    options: { unboundedViewport: boolean }
): CollectionDataApi {
    const { unboundedViewport } = options;
    const [eventLog, setEventLog] = useState<string[]>([]);
    const [rowData, setRowData] = useState<RowData[]>([]);
    const [totalCount, setTotalCount] = useState<number | null>(null);
    const [columnDefs, setColumnDefs] = useState<ColDef<RowData>[]>([]);
    const [latencySummary, setLatencySummary] = useState<LatencySummary>({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    const latencyAccRef = useRef({ maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [] as number[], recentTotalMs: 0 });
    const [isLoadingSnapshot, setIsLoadingSnapshot] = useState(false);
    const [snapshotStats, setSnapshotStats] = useState<SnapshotStats | null>(null);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const isReloadingGridRef = useRef(false);
    const pendingScrollToTopRef = useRef(false);
    const subscribedViewportRef = useRef<ViewportWindow | null>(null);
    const rowsByPositionRef = useRef<Map<number, RowData>>(new Map());
    const rowsByIdRef = useRef<Map<string, RowData>>(new Map());
    const totalCountRef = useRef<number | null>(null);
    const pendingSnapshotRenderMeasureRef = useRef<PendingSnapshotRenderMeasure | null>(null);
    const handleDeltaEventRef = useRef<(event: DeltaEvent) => void>(() => {});

    const appendLog = useCallback((entry: string) => {
        setEventLog((current) => [...current.slice(-19), entry]);
    }, []);

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

    const columnFieldsRef = useRef<string[] | null>(null);

    const adjustTotalCount = useCallback((delta: number) => {
        if (totalCountRef.current === null) {
            return;
        }

        const nextTotalCount = totalCountRef.current + delta;
        totalCountRef.current = nextTotalCount;
        setTotalCount(nextTotalCount);
    }, []);

    const clearState = useCallback(() => {
        rowsByIdRef.current.clear();
        rowsByPositionRef.current.clear();
        totalCountRef.current = null;
        subscribedViewportRef.current = null;
        pendingSnapshotRenderMeasureRef.current = null;
        columnFieldsRef.current = null;
        setRowData([]);
        setTotalCount(null);
        setSnapshotStats(null);
        latencyAccRef.current = { maxMs: 0, avgMs: 0, sampleCount: 0, recentLatencies: [], recentTotalMs: 0 };
        setLatencySummary({ maxMs: 0, avgMs: 0, sampleCount: 0 });
    }, []);

    /**
     * Rebuilds columnDefs only when the visible field set actually changes. AG Grid treats a brand
     * new columnDefs array as if the columns were recreated, which re-applies each column's initial
     * `sort`/`filter` declarations - if this happened on every snapshot (e.g. after every sort/filter
     * round-trip), it could clobber the sort/filter state that `applyColumnState`/`setFilterModel`
     * had just established, causing the grid to intermittently revert to a stale sort. Ongoing
     * sort/filter reflection is handled exclusively via applyColumnState/setFilterModel elsewhere.
     */
    const setColumnsFromRow = useCallback((row: RowData | undefined) => {
        if (!row) {
            return;
        }

        const fields = Object.keys(row).filter((f) => !impliedFields.has(f));
        if (fields.length === 0) {
            return;
        }

        const previousFields = columnFieldsRef.current;
        const unchanged = previousFields !== null
            && previousFields.length === fields.length
            && previousFields.every((field, index) => field === fields[index]);
        if (unchanged) {
            return;
        }

        columnFieldsRef.current = fields;
        setColumnDefs(fields.map((field) => buildColDef(field)));
    }, [buildColDef]);

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

        let isNoOpFullSnapshot = false;

        if (!snapshot.isPartial && !unboundedViewport) {
            const currentViewport = subscribedViewportRef.current;
            let cacheCoversViewport = false;
            if (currentViewport !== null) {
                cacheCoversViewport = true;
                for (let position = currentViewport.start; position <= currentViewport.end; position += 1) {
                    if (!rowsByPositionRef.current.has(position)) {
                        cacheCoversViewport = false;
                        break;
                    }
                }
            }

            isNoOpFullSnapshot = rows.length === 0
                && snapshot.totalCount === totalCountRef.current
                && currentViewport !== null
                && snapshot.startIndex === currentViewport.start
                && cacheCoversViewport;

            if (!isNoOpFullSnapshot) {
                rowsByPositionRef.current.clear();
                rowsByIdRef.current.clear();
            }
            subscribedViewportRef.current = rows.length > 0
                ? { start: snapshot.startIndex, end: snapshot.startIndex + rows.length - 1 }
                : (isNoOpFullSnapshot
                    ? currentViewport
                    : { start: snapshot.startIndex, end: snapshot.startIndex });
        } else if (!snapshot.isPartial && unboundedViewport) {
            // An unchanged subscribe/updateview request can legitimately come back as a non-partial
            // snapshot with zero rows and the same totalCount as before - the server's way of saying
            // "you already have this view, nothing to resend". Unlike the bounded-viewport branch
            // above, there's no viewport window to compare positions against here, so treat
            // "0 rows + matching totalCount + we already have the full set cached" as that same
            // no-op signal instead of unconditionally wiping an otherwise-complete cache.
            isNoOpFullSnapshot = rows.length === 0
                && snapshot.totalCount === totalCountRef.current
                && rowsByPositionRef.current.size === totalCountRef.current;

            if (!isNoOpFullSnapshot) {
                rowsByPositionRef.current.clear();
                rowsByIdRef.current.clear();
            }
        }

        if (isNoOpFullSnapshot) {
            appendLog(`snapshot resend acknowledged: already up to date `
                + `(${snapshot.waitMs.toFixed(0)}ms wait, ${snapshot.transferMs.toFixed(0)}ms transfer)`);
        } else {
            appendLog(snapshot.isPartial
                ? `partial snapshot received: ${snapshotStart.toLocaleString()} - ${snapshotEnd.toLocaleString()} `
                    + `(${snapshot.waitMs.toFixed(0)}ms wait, ${snapshot.transferMs.toFixed(0)}ms transfer)`
                : `snapshot received: ${snapshotStart.toLocaleString()} - ${snapshotEnd.toLocaleString()} `
                    + `(${snapshot.waitMs.toFixed(0)}ms wait, ${snapshot.transferMs.toFixed(0)}ms transfer)`);
        }

        for (let i = 0; i < rows.length; i++) {
            rowsByPositionRef.current.set(snapshot.startIndex + i, rows[i]);
            const rowId = rows[i].key ?? rows[i].id;
            if (rowId) {
                rowsByIdRef.current.set(rowId, rows[i]);
            }
        }

        totalCountRef.current = snapshot.totalCount;
        setTotalCount(snapshot.totalCount);
        if (!snapshot.isPartial && !isNoOpFullSnapshot) {
            pendingSnapshotRenderMeasureRef.current = {
                rowCount: rows.length,
                waitMs: snapshot.waitMs,
                transferMs: snapshot.transferMs,
                startedAt: performance.now(),
                startIndex: snapshotStart,
                endIndex: snapshotEnd
            };
        }

        if (rows.length > 0) {
            setColumnsFromRow(rows[0]);
        }

        setIsLoadingSnapshot(false);
        if (!snapshot.isPartial) {
            isReloadingGridRef.current = false;
        }
        publishRowsFromWindow();
        if (!snapshot.isPartial && pendingScrollToTopRef.current) {
            pendingScrollToTopRef.current = false;
            window.setTimeout(() => {
                gridApiRef.current?.ensureIndexVisible(0, 'top');
            }, 0);
        }
    }, [appendLog, publishRowsFromWindow, setColumnsFromRow, unboundedViewport]);

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
                    `snapshot rendered: ${pending.startIndex.toLocaleString()} - ${pending.endIndex.toLocaleString()} `
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
        adjustTotalCount(1);
        const window = subscribedViewportRef.current;
        if (window) {
            rowsByPositionRef.current.delete(window.end + 1);
        }
        publishRowsFromWindow();
    }, [adjustTotalCount, columnDefs.length, publishRowsFromWindow, setColumnsFromRow]);

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
        adjustTotalCount(-1);
        publishRowsFromWindow();
    }, [adjustTotalCount, publishRowsFromWindow]);

    const applyReplace = useCallback((replace: RowReplaceEvent) => {
        if (columnDefs.length === 0 && replace.row) {
            setColumnsFromRow(replace.row);
        }

        const removedRow = rowsByPositionRef.current.get(replace.removePosition);
        if (removedRow) {
            const removedRowId = removedRow.key ?? removedRow.id;
            if (removedRowId) {
                rowsByIdRef.current.delete(removedRowId);
            }
        } else if (replace.removedRowId) {
            rowsByIdRef.current.delete(replace.removedRowId);
        }

        rowsByPositionRef.current.delete(replace.removePosition);
        const afterRemovePositions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => left - right);
        for (const position of afterRemovePositions) {
            if (position > replace.removePosition) {
                const row = rowsByPositionRef.current.get(position);
                if (row) {
                    rowsByPositionRef.current.set(position - 1, row);
                    rowsByPositionRef.current.delete(position);
                }
            }
        }

        const positions = Array.from(rowsByPositionRef.current.keys()).sort((left, right) => right - left);
        for (const position of positions) {
            if (position >= replace.insertPosition) {
                const row = rowsByPositionRef.current.get(position);
                if (row) {
                    rowsByPositionRef.current.set(position + 1, row);
                }
            }
        }
        rowsByPositionRef.current.set(replace.insertPosition, { ...replace.row });
        const insertedRowId = replace.row.key ?? replace.row.id;
        if (insertedRowId) {
            rowsByIdRef.current.set(insertedRowId, { ...replace.row });
        }

        const window = subscribedViewportRef.current;
        if (window) {
            rowsByPositionRef.current.delete(window.end + 1);
        }
        publishRowsFromWindow();
    }, [columnDefs.length, publishRowsFromWindow, setColumnsFromRow]);

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
        } else if (event.type === 'rowReplace') {
            applyReplace(event);
        }
    }, [applyInsert, applyRemove, applyReplace, applySnapshot, applyUpdate, recordLatency]);
    handleDeltaEventRef.current = handleDeltaEvent;

    return {
        rowData,
        totalCount,
        columnDefs,
        isLoadingSnapshot,
        setIsLoadingSnapshot,
        snapshotStats,
        latencySummary,
        eventLog,
        appendLog,
        clearState,
        handleDeltaEventRef,
        gridApiRef,
        isReloadingGridRef,
        pendingScrollToTopRef,
        subscribedViewportRef,
        totalCountRef
    };
}
