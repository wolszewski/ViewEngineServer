import type { ProtocolFrame, RowData } from './webHostClient.types';

const escapePrefix = '\\';
const nullToken = '~';

export function parseCompactFrame(frame: string, currentFields: string[]): ProtocolFrame[] {
    const tokens = splitTokens(frame);
    const kind = tokens[0];

    if (kind === 'A') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        if (!Number.isInteger(subscriptionId)) {
            return [];
        }

        return [{
            kind: 'accepted',
            subscriptionId,
            snapshotFollows: tokens[2] === '1',
            startIndex: parseInt(tokens[3] ?? '0', 10) || 0,
            totalCount: parseInt(tokens[4] ?? '0', 10) || 0,
            fields: tokens.slice(5).map(decodeToken).filter((value): value is string => value !== null)
        }];
    }

    if (kind === 'P') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        if (!Number.isInteger(subscriptionId)) {
            return [];
        }

        return [{
            kind: 'snapshotStart',
            subscriptionId,
            startIndex: parseInt(tokens[2] ?? '0', 10) || 0,
            totalCount: parseInt(tokens[3] ?? '0', 10) || 0
        }];
    }

    if (kind === 'S') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        if (!Number.isInteger(subscriptionId)) {
            return [];
        }

        return [{
            kind: 'snapshotRow',
            subscriptionId,
            row: buildFullRow(tokens[2] ?? '', tokens.slice(3), currentFields)
        }];
    }

    if (kind === 'EOS') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        if (!Number.isInteger(subscriptionId)) {
            return [];
        }

        return [{ kind: 'eos', subscriptionId }];
    }

    if (kind === 'I') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        const position = parseInt(tokens[3] ?? '', 10);
        if (!Number.isInteger(subscriptionId) || !Number.isInteger(position)) {
            return [];
        }

        return [{
            kind: 'rowInsert',
            event: {
                type: 'rowInsert',
                subscriptionId,
                position,
                row: buildFullRow(tokens[2] ?? '', tokens.slice(4), currentFields)
            }
        }];
    }

    if (kind === 'U') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        const position = parseInt(tokens[3] ?? '', 10);
        const rowId = decodeToken(tokens[2] ?? '');
        if (!Number.isInteger(subscriptionId) || !Number.isInteger(position) || !rowId) {
            return [];
        }

        const changedFields: RowData = {};
        let fieldIndex = 0;
        for (const token of tokens.slice(4)) {
            if (/^\^\d+$/.test(token)) {
                fieldIndex += parseInt(token.slice(1), 10);
                continue;
            }

            const fieldName = currentFields[fieldIndex];
            if (fieldName) {
                changedFields[fieldName] = decodeToken(token);
            }
            fieldIndex++;
        }

        return [{
            kind: 'rowUpdate',
            event: {
                type: 'rowUpdate',
                subscriptionId,
                rowId,
                position,
                changedFields
            }
        }];
    }

    if (kind === 'D') {
        const subscriptionId = parseInt(tokens[1] ?? '', 10);
        const position = parseInt(tokens[3] ?? '', 10);
        if (!Number.isInteger(subscriptionId) || !Number.isInteger(position)) {
            return [];
        }

        return [{
            kind: 'rowRemove',
            event: {
                type: 'rowRemove',
                subscriptionId,
                position
            }
        }];
    }

    return [];
}

function buildFullRow(rawRowId: string, rawValues: string[], currentFields: string[]): RowData {
    const row: RowData = {
        key: decodeToken(rawRowId)
    };

    for (let i = 0; i < currentFields.length; i++) {
        row[currentFields[i]] = decodeToken(rawValues[i] ?? '');
    }

    return row;
}

function splitTokens(frame: string): string[] {
    const tokens: string[] = [];
    let current = '';
    let escaping = false;

    for (const ch of frame) {
        if (escaping) {
            current += escapePrefix + ch;
            escaping = false;
            continue;
        }

        if (ch === escapePrefix) {
            escaping = true;
            continue;
        }

        if (ch === '|') {
            tokens.push(current);
            current = '';
            continue;
        }

        current += ch;
    }

    tokens.push(current);
    return tokens;
}

function decodeToken(token: string): string | null {
    if (token === nullToken) {
        return null;
    }

    if (token.length === 0) {
        return '';
    }

    let decoded = '';
    let escaping = false;
    for (const ch of token) {
        if (escaping) {
            decoded += ch;
            escaping = false;
            continue;
        }

        if (ch === escapePrefix) {
            escaping = true;
            continue;
        }

        decoded += ch;
    }

    return decoded;
}
