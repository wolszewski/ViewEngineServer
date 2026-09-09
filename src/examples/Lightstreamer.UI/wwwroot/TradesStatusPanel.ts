import React from 'react';
import { latencyWindowSize, type LatencySummary, type SnapshotStats } from './tradesShared';

export interface TradesStatusPanelProps {
    status: string;
    isLoadingSnapshot: boolean;
    snapshotStats: SnapshotStats | null;
    snapshotTimeoutSeconds: number;
    trackLiveSnapshotCount: boolean;
    liveSnapshotRowCount: number;
    liveSnapshotUpdateCount: number;
    latencySummary: LatencySummary;
}

export default function TradesStatusPanel(props: TradesStatusPanelProps): React.ReactElement {
    const {
        status, isLoadingSnapshot, snapshotStats, snapshotTimeoutSeconds,
        trackLiveSnapshotCount, liveSnapshotRowCount, liveSnapshotUpdateCount, latencySummary
    } = props;

    return React.createElement(
        React.Fragment,
        null,
        React.createElement('div', { className: 'status' }, status),
        React.createElement(
            'div',
            { className: 'status' },
            snapshotStats !== null
                ? `snapshot ${snapshotStats.rowCount.toLocaleString()} rows | `
                    + `wait ${snapshotStats.waitMs.toFixed(0)}ms | `
                    + `transfer ${snapshotStats.transferMs.toFixed(0)}ms | `
                    + `render ${snapshotStats.renderMs.toFixed(0)}ms`
                    + (snapshotStats.incomplete ? ` — incomplete (stopped after ${snapshotTimeoutSeconds}s max wait)` : '')
                : isLoadingSnapshot
                    ? (trackLiveSnapshotCount
                        ? `Loading snapshot… ${liveSnapshotRowCount.toLocaleString()} added, `
                            + `${liveSnapshotUpdateCount.toLocaleString()} updated so far`
                        : 'Loading snapshot…')
                    : 'Snapshot: —'
        ),
        React.createElement(
            'div',
            { className: 'status' },
            `Live updates — Max latency: ${latencySummary.maxMs.toFixed(0)} ms • `
            + `Avg latency (last ${latencyWindowSize}): ${latencySummary.avgMs.toFixed(0)} ms • `
            + `Window samples: ${latencySummary.sampleCount}`
        )
    );
}
