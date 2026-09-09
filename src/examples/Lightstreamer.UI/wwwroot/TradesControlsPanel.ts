import React from 'react';
import type { PushMode } from './tradesShared';

export interface TradesControlsPanelProps {
    lsUrl: string;
    setLsUrl: (value: string) => void;
    pushMode: PushMode;
    setPushMode: (value: PushMode) => void;
    snapshotTimeoutSeconds: number;
    setSnapshotTimeoutSeconds: (value: number) => void;
    isConnected: boolean;
    isLoadingSnapshot: boolean;
    connect: () => void;
    disconnect: () => void;
    gridVisible: boolean;
    setGridVisible: (value: boolean) => void;
    trackLiveSnapshotCount: boolean;
    setTrackLiveSnapshotCount: (value: boolean) => void;
}

export default function TradesControlsPanel(props: TradesControlsPanelProps): React.ReactElement {
    const {
        lsUrl, setLsUrl, pushMode, setPushMode, snapshotTimeoutSeconds, setSnapshotTimeoutSeconds,
        isConnected, isLoadingSnapshot, connect, disconnect, gridVisible, setGridVisible,
        trackLiveSnapshotCount, setTrackLiveSnapshotCount
    } = props;

    return React.createElement(
        'div',
        { className: 'controls' },
        React.createElement(
            'label',
            { className: 'control-label' },
            'Lightstreamer URL',
            React.createElement('input', {
                type: 'text',
                value: lsUrl,
                disabled: isConnected,
                onChange: (e: Event) => setLsUrl((e.target as HTMLInputElement).value)
            })
        ),
        React.createElement(
            'label',
            { className: 'control-label' },
            'Push mode',
            React.createElement(
                'select',
                {
                    value: pushMode,
                    disabled: isConnected,
                    onChange: (e: Event) => setPushMode((e.target as HTMLSelectElement).value as PushMode)
                },
                React.createElement('option', { value: 'twoLevel' }, 'Two-level push (COMMAND + MERGE)'),
                React.createElement('option', { value: 'pure' }, 'Pure command mode')
            )
        ),
        React.createElement(
            'label',
            { className: 'control-label' },
            'Max wait (s)',
            React.createElement('input', {
                type: 'number',
                min: 1,
                value: snapshotTimeoutSeconds,
                disabled: isConnected,
                onChange: (e: Event) => {
                    const parsed = Number((e.target as HTMLInputElement).value);
                    if (Number.isFinite(parsed) && parsed >= 1) {
                        setSnapshotTimeoutSeconds(Math.floor(parsed));
                    }
                }
            })
        ),
        !isConnected
            ? React.createElement('button', { type: 'button', onClick: connect, disabled: isLoadingSnapshot }, 'Connect')
            : React.createElement('button', { type: 'button', onClick: disconnect }, 'Disconnect'),
        React.createElement(
            'label',
            { className: 'control-label', style: { flexDirection: 'row', alignItems: 'center', gap: '0.4rem' } },
            React.createElement('input', {
                type: 'checkbox',
                checked: gridVisible,
                onChange: (e: Event) => setGridVisible((e.target as HTMLInputElement).checked)
            }),
            'Show grid'
        ),
        React.createElement(
            'label',
            { className: 'control-label', style: { flexDirection: 'row', alignItems: 'center', gap: '0.4rem' } },
            React.createElement('input', {
                type: 'checkbox',
                checked: trackLiveSnapshotCount,
                onChange: (e: Event) => setTrackLiveSnapshotCount((e.target as HTMLInputElement).checked)
            }),
            'Live loaded count'
        )
    );
}
