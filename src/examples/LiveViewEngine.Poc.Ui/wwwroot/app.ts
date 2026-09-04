import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AllCommunityModule, ModuleRegistry } from 'ag-grid-community';
import { sharedAppStyles } from './gridShared';
import ServerSideGridView from './ServerSideGridView';
import ClientSideGridView from './ClientSideGridView';

ModuleRegistry.registerModules([AllCommunityModule]);

type ActiveTab = 'server' | 'client';

function getInitialActiveTab(): ActiveTab {
    const params = new URLSearchParams(window.location.search);
    return params.get('tab') === 'client' ? 'client' : 'server';
}

function App(): React.ReactElement {
    const initialActiveTab = useMemo(() => getInitialActiveTab(), []);
    const [activeTab, setActiveTab] = useState<ActiveTab>(initialActiveTab);

    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        if (activeTab === 'client') {
            params.set('tab', 'client');
        } else {
            params.delete('tab');
        }
        const nextSearch = params.toString();
        window.history.replaceState(
            null, '', `${window.location.pathname}${nextSearch.length > 0 ? `?${nextSearch}` : ''}${window.location.hash}`);
    }, [activeTab]);

    return React.createElement(
        React.Fragment,
        null,
        React.createElement('style', null, sharedAppStyles),
        React.createElement('h1', null, 'LiveViewEngine PoC UI'),
        React.createElement(
            'div',
            { className: 'tabs' },
            React.createElement('button', {
                type: 'button',
                className: activeTab === 'server' ? 'tab-active' : '',
                onClick: () => setActiveTab('server')
            }, 'Server Side Filter/Sort'),
            React.createElement('button', {
                type: 'button',
                className: activeTab === 'client' ? 'tab-active' : '',
                onClick: () => setActiveTab('client')
            }, 'UI Side Filter/Sort')
        ),
        activeTab === 'server'
            ? React.createElement(ServerSideGridView)
            : React.createElement(ClientSideGridView)
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
