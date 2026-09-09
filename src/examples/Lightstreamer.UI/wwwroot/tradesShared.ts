export type RowData = Record<string, string | null>;

export type PushMode = 'twoLevel' | 'pure';

export interface SnapshotStats {
    rowCount: number;
    waitMs: number;
    transferMs: number;
    renderMs: number;
    incomplete: boolean;
}

export interface LatencySummary {
    maxMs: number;
    avgMs: number;
    sampleCount: number;
}

export const normalizeLsUrl = (value: string): string =>
    value.trim().replace(/\/+$/, '').replace(/\/lightstreamer\/?$/i, '');

// Same "grid" query-param convention as the other example UIs: absent or "1" means visible
// (the default); "0" means hidden.
export function getInitialGridVisible(): boolean {
    return new URLSearchParams(window.location.search).get('grid') !== '0';
}

export function syncGridVisibleParam(gridVisible: boolean): void {
    const params = new URLSearchParams(window.location.search);
    if (gridVisible) {
        params.delete('grid');
    } else {
        params.set('grid', '0');
    }

    const nextSearch = params.toString();
    window.history.replaceState(
        null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
}

export const defaultLsUrl = normalizeLsUrl(
    window.location.port === '5112' ? 'http://127.0.0.1:8080' : window.location.origin
);

// 'twoLevel' = COMMAND item carries only key/command; row fields arrive via a per-key
// second-level MERGE subscription. 'pure' = COMMAND item carries the full row fields directly
// on every ADD/UPDATE, no second-level subscription involved.
export const pushModeItemNames: Record<PushMode, string> = {
    twoLevel: 'TRADES_ALL',
    pure: 'TRADES_ALL_PURE'
};
export const pushModeDataAdapters: Record<PushMode, string> = {
    twoLevel: 'trades-command-adapter',
    pure: 'trades-pure-command-adapter'
};
export const defaultPushMode: PushMode = 'twoLevel';

// Same "mode" query-param convention as "grid": absent or "twoLevel" means the default;
// "pure" selects pure command mode. Any other/invalid value falls back to the default.
export function getInitialPushMode(): PushMode {
    const value = new URLSearchParams(window.location.search).get('mode');
    return value === 'pure' ? 'pure' : defaultPushMode;
}

export function syncPushModeParam(pushMode: PushMode): void {
    const params = new URLSearchParams(window.location.search);
    if (pushMode === defaultPushMode) {
        params.delete('mode');
    } else {
        params.set('mode', pushMode);
    }

    const nextSearch = params.toString();
    window.history.replaceState(
        null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
}

export const subscribedFields = [
    'tradeId', 'createdDate', 'updatedDate', 'accountId', 'quantity',
    'price', 'side', 'status', 'isAlgo', 'isManualReview', 'notional', 'variedNumber',
    ...Array.from({ length: 30 }, (_, i) => `stringField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 23 }, (_, i) => `intField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `decimalField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `enumField${i.toString().padStart(2, '0')}`),
    ...Array.from({ length: 20 }, (_, i) => `boolField${i.toString().padStart(2, '0')}`)
];
export const subscribedFieldSet = new Set(subscribedFields);
export const defaultSnapshotTimeoutSeconds = 10;
export const snapshotAddGraceMs = 300;
export const latencyWindowSize = 500;

export const sharedAppStyles = `
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
.control-label input[type="text"] {
    padding: 0.4rem 0.5rem;
    border: 1px solid #c7ced8;
    border-radius: 0.25rem;
    min-width: 20rem;
}
.status {
    margin-bottom: 1rem;
    padding: 0.75rem;
    background: #f0f6ff;
    border-radius: 0.25rem;
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
