export type MessageFormat = 'compact' | 'json';

export type RowData = Record<string, string | null>;

export interface SnapshotEvent {
    type: 'snapshot';
    subscriptionId: number;
    totalCount: number;
    startIndex: number;
    rows: RowData[];
}

export interface RowUpdateEvent {
    type: 'rowUpdate';
    subscriptionId: number;
    rowId: string;
    position: number;
    changedFields: RowData;
}

export interface RowInsertEvent {
    type: 'rowInsert';
    subscriptionId: number;
    position: number;
    row: RowData;
}

export interface RowRemoveEvent {
    type: 'rowRemove';
    subscriptionId: number;
    position: number;
}

export type DeltaEvent = SnapshotEvent | RowUpdateEvent | RowInsertEvent | RowRemoveEvent;

export interface FilterRequest {
    field: string;
    operator: string;
    value: string;
}

export interface SubscribeRequest {
    collectionId: string;
    sortColumn: string;
    sortAscending: boolean;
    pageSize: number;
    startIndex: number;
    filters: FilterRequest[];
    fields?: string[];
    sendSnapshot?: boolean;
    messageFormat?: MessageFormat;
}

export type ProtocolFrame =
    | {
        kind: 'accepted';
        subscriptionId: number;
        snapshotFollows: boolean;
        startIndex: number;
        totalCount: number;
        fields: string[];
    }
    | {
        kind: 'snapshotStart';
        subscriptionId: number;
        startIndex: number;
        totalCount: number;
    }
    | {
        kind: 'snapshotRow';
        subscriptionId: number;
        row: RowData;
    }
    | {
        kind: 'eos';
        subscriptionId: number;
    }
    | {
        kind: 'rowInsert';
        event: RowInsertEvent;
    }
    | {
        kind: 'rowUpdate';
        event: RowUpdateEvent;
    }
    | {
        kind: 'rowRemove';
        event: RowRemoveEvent;
    };
