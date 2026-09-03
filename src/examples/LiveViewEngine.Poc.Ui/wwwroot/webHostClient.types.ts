export type MessageFormat = 'compact' | 'json';

export type RowData = Record<string, string | null>;

export interface SnapshotEvent {
    type: 'snapshot';
    subscriptionId: number;
    totalCount: number;
    startIndex: number;
    rows: RowData[];
    waitMs: number;
    transferMs: number;
    isPartial?: boolean;
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

export interface RowReplaceEvent {
    type: 'rowReplace';
    subscriptionId: number;
    removedRowId: string;
    removePosition: number;
    insertPosition: number;
    row: RowData;
}

export type DeltaEvent = SnapshotEvent | RowUpdateEvent | RowInsertEvent | RowRemoveEvent | RowReplaceEvent;

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
        kind: 'rejected';
        subscriptionId: number;
        reason: string;
        message: string;
    }
    | {
        kind: 'updateRejected';
        subscriptionId: number;
        reason: string;
        message: string;
    }
    | {
        kind: 'snapshotStart';
        subscriptionId: number;
        startIndex: number;
        totalCount: number;
        isPartial?: boolean;
        fields?: string[];
    }
    | {
        kind: 'snapshotRow';
        subscriptionId: number;
        rowNumber: number;
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
    }
    | {
        kind: 'rowReplace';
        event: RowReplaceEvent;
    };
