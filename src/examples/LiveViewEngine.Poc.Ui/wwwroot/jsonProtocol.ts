import type { ProtocolFrame, RowData, SnapshotFollowsKind } from './webHostClient.types';

type JsonFrame = {
    type: string;
    subscriptionId?: number;
    snapshotFollows?: string;
    startIndex?: number;
    totalCount?: number;
    isPartial?: boolean;
    fields?: string[];
    row?: RowData;
    rowId?: string;
    rowNumber?: number;
    position?: number;
    removePosition?: number;
    insertPosition?: number;
    removedRowId?: string;
    changedFields?: RowData;
    rows?: RowData[];
};

function parseSnapshotFollows(value: string | undefined): SnapshotFollowsKind {
    return value === 'immediate' || value === 'pending' ? value : 'none';
}

export function parseJsonFrame(frame: string): ProtocolFrame[] {
    const parsed = JSON.parse(frame) as JsonFrame | JsonFrame[];
    const messages = Array.isArray(parsed) ? parsed : [parsed];
    const frames: ProtocolFrame[] = [];

    for (const message of messages) {
        const subscriptionId = Number(message.subscriptionId);
        if (!Number.isInteger(subscriptionId)) {
            continue;
        }

        switch (message.type) {
            case 'subscriptionAccepted':
                frames.push({
                    kind: 'accepted',
                    subscriptionId,
                    snapshotFollows: parseSnapshotFollows(message.snapshotFollows),
                    startIndex: Number(message.startIndex) || 0,
                    totalCount: Number(message.totalCount) || 0,
                    fields: message.fields ?? []
                });
                break;
            case 'snapshotStart':
                frames.push({
                    kind: 'snapshotStart',
                    subscriptionId,
                    startIndex: Number(message.startIndex) || 0,
                    totalCount: Number(message.totalCount) || 0,
                    isPartial: message.isPartial === true,
                    fields: Array.isArray(message.fields) ? message.fields : undefined
                });
                break;
            case 'snapshotRow':
                if (message.row) {
                    frames.push({
                        kind: 'snapshotRow',
                        subscriptionId,
                        rowNumber: message.rowNumber ?? 0,
                        row: message.row
                    });
                }
                break;
            case 'eos':
                frames.push({ kind: 'eos', subscriptionId });
                break;
            case 'snapshot':
                if (message.rows) {
                    const snapshotStart = Number(message.startIndex) || 0;
                    frames.push({
                        kind: 'snapshotStart',
                        subscriptionId,
                        startIndex: snapshotStart,
                        totalCount: Number(message.totalCount) || 0,
                        fields: Array.isArray(message.fields) ? message.fields : undefined
                    });
                    for (let i = 0; i < message.rows.length; i++) {
                        frames.push({
                            kind: 'snapshotRow',
                            subscriptionId,
                            rowNumber: snapshotStart + i,
                            row: message.rows[i]
                        });
                    }
                    frames.push({ kind: 'eos', subscriptionId });
                }
                break;
            case 'rowInsert':
                if (message.row && Number.isInteger(message.position)) {
                    frames.push({
                        kind: 'rowInsert',
                        event: {
                            type: 'rowInsert',
                            subscriptionId,
                            position: Number(message.position),
                            row: message.row
                        }
                    });
                }
                break;
            case 'rowUpdate':
                if (message.rowId && Number.isInteger(message.position)) {
                    frames.push({
                        kind: 'rowUpdate',
                        event: {
                            type: 'rowUpdate',
                            subscriptionId,
                            rowId: message.rowId,
                            position: Number(message.position),
                            changedFields: message.changedFields ?? {}
                        }
                    });
                }
                break;
            case 'rowRemove':
                if (Number.isInteger(message.position)) {
                    frames.push({
                        kind: 'rowRemove',
                        event: {
                            type: 'rowRemove',
                            subscriptionId,
                            position: Number(message.position)
                        }
                    });
                }
                break;
            case 'rowReplace':
                if (message.row
                    && message.removedRowId
                    && Number.isInteger(message.removePosition)
                    && Number.isInteger(message.insertPosition)) {
                    frames.push({
                        kind: 'rowReplace',
                        event: {
                            type: 'rowReplace',
                            subscriptionId,
                            removedRowId: message.removedRowId,
                            removePosition: Number(message.removePosition),
                            insertPosition: Number(message.insertPosition),
                            row: message.row
                        }
                    });
                }
                break;
        }
    }

    return frames;
}
