import type { ProtocolFrame, RowData } from './webHostClient.types';

type JsonFrame = {
    type: string;
    subscriptionId?: number;
    snapshotFollows?: boolean;
    startIndex?: number;
    totalCount?: number;
    isPartial?: boolean;
    fields?: string[];
    row?: RowData;
    rowId?: string;
    position?: number;
    removePosition?: number;
    insertPosition?: number;
    removedRowId?: string;
    changedFields?: RowData;
    rows?: RowData[];
};

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
                    snapshotFollows: message.snapshotFollows === true,
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
                    isPartial: message.isPartial === true
                });
                break;
            case 'snapshotRow':
                if (message.row) {
                    frames.push({
                        kind: 'snapshotRow',
                        subscriptionId,
                        row: message.row
                    });
                }
                break;
            case 'eos':
                frames.push({ kind: 'eos', subscriptionId });
                break;
            case 'snapshot':
                if (message.rows) {
                    frames.push({
                        kind: 'snapshotStart',
                        subscriptionId,
                        startIndex: Number(message.startIndex) || 0,
                        totalCount: Number(message.totalCount) || 0
                    });
                    for (const row of message.rows) {
                        frames.push({
                            kind: 'snapshotRow',
                            subscriptionId,
                            row
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
