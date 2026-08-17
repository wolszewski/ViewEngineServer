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
const defaultPageSizes = [25, 50, 100];
const filterOperators = [
    { label: 'equals', value: 'eq' },
    { label: 'not equals', value: 'notEq' },
    { label: 'greater than', value: 'gt' },
    { label: 'greater than or equal', value: 'gte' },
    { label: 'less than', value: 'lt' },
    { label: 'less than or equal', value: 'lte' },
    { label: 'contains', value: 'contains' }
] as const;
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
    const [status, setStatus] = useState('Disconnected');
    const [collectionId, setCollectionId] = useState('trades');
    const [sortColumn, setSortColumn] = useState('quantity');
    const [sortAscending, setSortAscending] = useState(true);
    const [pageSize, setPageSize] = useState(50);
    const [pageSizeInput, setPageSizeInput] = useState('50');
    const [pageIndex, setPageIndex] = useState(0);
    const [filters, setFilters] = useState<Array<{ field: string; operator: string; value: string }>>([]);
    const [isAddingFilter, setIsAddingFilter] = useState(false);
    const [draftFilter, setDraftFilter] = useState({
        field: knownTradeColumns[0],
        operator: 'eq',
        value: ''
    });
    const [selectedFields, setSelectedFields] = useState<string[]>([]);
    const [isSelectingColumns, setIsSelectingColumns] = useState(false);
    const [draftColumns, setDraftColumns] = useState<Set<string>>(new Set());
    const [totalCount, setTotalCount] = useState<number | null>(null);
    const [columnDefs, setColumnDefs] = useState<ColDef<RowData>[]>([]);

    const effectiveTotalCount = totalCount ?? defaultTotalCountAssumption;
    const maxPageIndex = Math.max(0, Math.ceil(effectiveTotalCount / pageSize) - 1);

    const gridApiRef = useRef<GridApi<RowData> | null>(null);
    const clientRef = useRef<WebHostClient | null>(null);
    const handleDeltaEventRef = useRef<(event: DeltaEvent) => void>(() => {});
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

        const fields = Object.keys(row).filter((f) => !impliedFields.has(f));
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
        const startIndex = pageIndex * pageSize;
        clientRef.current?.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize,
            startIndex,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined
        });
    }, [clearState, collectionId, normalisedFilters, pageIndex, pageSize, selectedFields, sortAscending, sortColumn]);

    const disconnect = useCallback(() => {
        clientRef.current?.disconnect();
        setStatus('Disconnected');
        clearState();
    }, [clearState]);

    const goToPage = useCallback((nextPageIndex: number) => {
        const clamped = Math.max(0, Math.min(nextPageIndex, maxPageIndex));
        setPageIndex(clamped);
    }, [maxPageIndex]);

    const onPageSizeChanged = useCallback((nextPageSize: number) => {
        if (!Number.isFinite(nextPageSize) || nextPageSize < 1) {
            return;
        }

        const normalizedPageSize = Math.floor(nextPageSize);
        setPageSize(normalizedPageSize);
        setPageIndex(0);
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

    const addFilter = useCallback(() => {
        setIsAddingFilter(true);
    }, []);

    const commitFilter = useCallback(() => {
        const nextFilter = {
            field: draftFilter.field,
            operator: draftFilter.operator,
            value: draftFilter.value
        };

        if (!nextFilter.field || !nextFilter.field.trim()) {
            return;
        }

        setFilters((current) => [...current, nextFilter]);
        setDraftFilter({
            field: knownTradeColumns[0],
            operator: 'eq',
            value: ''
        });
        setIsAddingFilter(false);
    }, [draftFilter]);

    const cancelFilter = useCallback(() => {
        setIsAddingFilter(false);
        setDraftFilter({
            field: knownTradeColumns[0],
            operator: 'eq',
            value: ''
        });
    }, []);

    const removeFilter = useCallback((index: number) => {
        setFilters((current) => current.filter((_, currentIndex) => currentIndex !== index));
    }, []);

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
        setPageSizeInput(String(pageSize));
    }, [pageSize]);

    useEffect(() => {
        const client = clientRef.current;
        if (!client?.isConnected) {
            return;
        }

        client.connect({
            collectionId,
            sortColumn,
            sortAscending,
            pageSize,
            startIndex: pageIndex * pageSize,
            filters: normalisedFilters,
            fields: selectedFields.length > 0 ? selectedFields : undefined
        });
    }, [collectionId, filters, normalisedFilters, pageIndex, pageSize, selectedFields, sortAscending, sortColumn]);

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
            .pager {
                display: flex;
                align-items: center;
                gap: 0.75rem;
                margin-bottom: 1rem;
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
                React.createElement(
                    'select',
                    {
                        value: sortColumn,
                        onChange: (e: Event) => setSortColumn((e.target as HTMLSelectElement).value)
                    },
                    ...knownTradeColumns.map((column) => React.createElement('option', { key: column, value: column }, column))
                )
            ),
            React.createElement(
                'label',
                { className: 'control-label' },
                'Sort direction',
                React.createElement(
                    'select',
                    {
                        value: sortAscending ? 'asc' : 'desc',
                        onChange: (e: Event) => setSortAscending((e.target as HTMLSelectElement).value === 'asc')
                    },
                    React.createElement('option', { value: 'asc' }, 'Ascending'),
                    React.createElement('option', { value: 'desc' }, 'Descending')
                )
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
            React.createElement('button', { type: 'button', onClick: addFilter }, 'Add filter'),
            React.createElement('button', { type: 'button', onClick: openColumnSelector }, 'Select columns'),
            React.createElement('button', { type: 'button', onClick: connect }, 'Connect'),
            React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect')
        ),
        React.createElement(
            'div',
            { className: 'filters' },
            isAddingFilter
                ? React.createElement(
                    'div',
                    { className: 'filter-row' },
                    React.createElement(
                        'label',
                        { className: 'control-label' },
                        'Field',
                        React.createElement(
                            'select',
                            {
                                value: draftFilter.field,
                                onChange: (e: Event) => setDraftFilter((current) => ({ ...current, field: (e.target as HTMLSelectElement).value }))
                            },
                            ...knownTradeColumns.map((column) => React.createElement('option', { key: column, value: column }, column))
                        )
                    ),
                    React.createElement(
                        'label',
                        { className: 'control-label' },
                        'Operator',
                        React.createElement(
                            'select',
                            {
                                value: draftFilter.operator,
                                onChange: (e: Event) => setDraftFilter((current) => ({ ...current, operator: (e.target as HTMLSelectElement).value }))
                            },
                            ...filterOperators.map((operator) => React.createElement('option', { key: operator.value, value: operator.value }, operator.label))
                        )
                    ),
                    React.createElement(
                        'label',
                        { className: 'control-label' },
                        'Value',
                        React.createElement('input', {
                            value: draftFilter.value,
                            onChange: (e: Event) => setDraftFilter((current) => ({ ...current, value: (e.target as HTMLInputElement).value }))
                        })
                    ),
                    React.createElement('button', { type: 'button', onClick: commitFilter }, 'Add'),
                    React.createElement('button', { type: 'button', onClick: cancelFilter }, 'Cancel')
                )
                : null,
            filters.length === 0
                ? React.createElement('div', { className: 'empty-filters' }, 'No filters added.')
                : filters.map((filter, index) => React.createElement(
                    'div',
                    { key: `${filter.field}-${filter.operator}-${index}`, className: 'filter-chip' },
                    React.createElement('span', null, `${filter.field} ${filterOperators.find((operator) => operator.value === filter.operator)?.label ?? filter.operator} ${filter.value}`),
                    React.createElement('button', { type: 'button', onClick: () => removeFilter(index) }, 'Remove')
                ))
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
