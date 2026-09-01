import { AllCommunityModule, ModuleRegistry, type ColDef } from 'ag-grid-community';
import { type MessageFormat, type RowData } from './webHostClient';

ModuleRegistry.registerModules([AllCommunityModule]);

export const defaultCollectionId = 'trades';
export const defaultMessageFormat: MessageFormat = 'compact';
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
export const textFilterOptions = ['contains', 'equals', 'notEqual'] as const;
export const orderedFilterOptions = ['equals', 'notEqual', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual'] as const;
export const sharedFilterParams = {
    buttons: ['apply', 'clear'],
    closeOnApply: true,
    maxNumConditions: 1
} as const;

export function getAgGridFilterType(field: string): NonNullable<ColDef<RowData>['filter']> {
    if (numericTradeColumnSet.has(field)) {
        return 'agNumberColumnFilter';
    }

    if (dateTradeColumnSet.has(field)) {
        return 'agDateColumnFilter';
    }

    return 'agTextColumnFilter';
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

export function buildColumnDef(field: string, sortColumn: string, sortAscending: boolean): ColDef<RowData> {
    const filter = getAgGridFilterType(field);
    const columnDef: ColDef<RowData> = {
        field,
        headerName: field,
        filter,
        floatingFilter: true,
        sort: field === sortColumn && sortColumn !== '' ? (sortAscending ? 'asc' : 'desc') : undefined,
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

export const appStyles = `
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
.tab-bar {
    display: flex;
    gap: 0;
    margin-bottom: 1rem;
    border-bottom: 2px solid #dfe7f1;
}
.tab-button {
    padding: 0.5rem 1.25rem;
    border: 1px solid transparent;
    border-bottom: none;
    border-radius: 0.25rem 0.25rem 0 0;
    background: transparent;
    cursor: pointer;
    font-size: 0.95rem;
    margin-bottom: -2px;
    text-decoration: none;
    color: inherit;
    display: inline-block;
}
.tab-button.active {
    background: white;
    border-color: #dfe7f1;
    font-weight: 600;
    border-bottom: 2px solid white;
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
