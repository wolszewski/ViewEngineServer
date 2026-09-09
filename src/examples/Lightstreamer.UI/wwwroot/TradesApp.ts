import React, { useEffect, useMemo, useState } from 'react';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import TradesControlsPanel from './TradesControlsPanel';
import TradesGridView from './TradesGridView';
import TradesStatusPanel from './TradesStatusPanel';
import { useTradesSubscription } from './useTradesSubscription';
import {
    defaultLsUrl,
    defaultSnapshotTimeoutSeconds,
    getInitialGridVisible,
    getInitialPushMode,
    normalizeLsUrl,
    sharedAppStyles,
    syncGridVisibleParam,
    syncPushModeParam,
    type PushMode,
    type RowData
} from './tradesShared';

ModuleRegistry.registerModules([AllCommunityModule]);

export default function TradesApp(): React.ReactElement {
    const [lsUrl, setLsUrl] = useState(defaultLsUrl);
    const [pushMode, setPushMode] = useState<PushMode>(() => getInitialPushMode());
    const [snapshotTimeoutSeconds, setSnapshotTimeoutSeconds] = useState(defaultSnapshotTimeoutSeconds);
    const [gridVisible, setGridVisible] = useState(() => getInitialGridVisible());
    const initialRowData = useMemo<RowData[]>(() => [], []);

    useEffect(() => {
        syncGridVisibleParam(gridVisible);
    }, [gridVisible]);
    useEffect(() => {
        syncPushModeParam(pushMode);
    }, [pushMode]);

    const trades = useTradesSubscription({ lsUrl, pushMode, snapshotTimeoutSeconds, gridVisible });

    return React.createElement(
        React.Fragment,
        null,
        React.createElement('style', null, sharedAppStyles),
        React.createElement('h1', null, 'Lightstreamer PoC UI'),
        React.createElement(TradesStatusPanel, {
            status: trades.status,
            isLoadingSnapshot: trades.isLoadingSnapshot,
            snapshotStats: trades.snapshotStats,
            snapshotTimeoutSeconds,
            trackLiveSnapshotCount: trades.trackLiveSnapshotCount,
            liveSnapshotRowCount: trades.liveSnapshotRowCount,
            liveSnapshotUpdateCount: trades.liveSnapshotUpdateCount,
            latencySummary: trades.latencySummary
        }),
        React.createElement(TradesControlsPanel, {
            lsUrl,
            setLsUrl: (value: string) => setLsUrl(normalizeLsUrl(value)),
            pushMode,
            setPushMode,
            snapshotTimeoutSeconds,
            setSnapshotTimeoutSeconds,
            isConnected: trades.isConnected,
            isLoadingSnapshot: trades.isLoadingSnapshot,
            connect: trades.connect,
            disconnect: trades.disconnect,
            gridVisible,
            setGridVisible,
            trackLiveSnapshotCount: trades.trackLiveSnapshotCount,
            setTrackLiveSnapshotCount: trades.setTrackLiveSnapshotCount
        }),
        React.createElement(TradesGridView, {
            gridVisible,
            isLoadingSnapshot: trades.isLoadingSnapshot,
            trackLiveSnapshotCount: trades.trackLiveSnapshotCount,
            liveSnapshotRowCount: trades.liveSnapshotRowCount,
            liveSnapshotUpdateCount: trades.liveSnapshotUpdateCount,
            columnDefs: trades.columnDefs,
            defaultColDef: trades.defaultColDef,
            initialRowData,
            gridApiRef: trades.gridApiRef
        })
    );
}
