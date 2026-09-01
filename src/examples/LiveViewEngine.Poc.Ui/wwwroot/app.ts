import React, { useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { appStyles } from './shared';
import { ServerSortedGrid } from './serverSortedGrid';
import { ClientSortedGrid } from './clientSortedGrid';

type Route = 'server-sorted' | 'client-sorted';

const routePaths: Record<Route, string> = {
    'server-sorted': '/server-sorted',
    'client-sorted': '/client-sorted'
};

function routeFromPathname(pathname: string): Route {
    return pathname.startsWith('/client-sorted') ? 'client-sorted' : 'server-sorted';
}

function App(): React.ReactElement {
    const [route, setRoute] = useState<Route>(() => routeFromPathname(window.location.pathname));

    useEffect(() => {
        const handlePopState = () => setRoute(routeFromPathname(window.location.pathname));
        window.addEventListener('popstate', handlePopState);
        return () => window.removeEventListener('popstate', handlePopState);
    }, []);

    useEffect(() => {
        // Canonicalize the URL (e.g. "/" -> "/server-sorted") without discarding query params.
        const canonicalPath = routePaths[route];
        if (window.location.pathname !== canonicalPath) {
            window.history.replaceState(null, '', `${canonicalPath}${window.location.search}${window.location.hash}`);
        }
    }, [route]);

    const navigate = useCallback((nextRoute: Route) => (e: MouseEvent) => {
        e.preventDefault();
        if (nextRoute === route) {
            return;
        }

        window.history.pushState(null, '', routePaths[nextRoute]);
        setRoute(nextRoute);
    }, [route]);

    return React.createElement(
        React.Fragment,
        null,
        React.createElement('style', null, appStyles),
        React.createElement('h1', null, 'LiveViewEngine PoC UI'),
        React.createElement(
            'div',
            { className: 'tab-bar' },
            React.createElement('a', {
                href: routePaths['server-sorted'],
                className: `tab-button${route === 'server-sorted' ? ' active' : ''}`,
                onClick: navigate('server-sorted')
            }, 'Server-sorted'),
            React.createElement('a', {
                href: routePaths['client-sorted'],
                className: `tab-button${route === 'client-sorted' ? ' active' : ''}`,
                onClick: navigate('client-sorted')
            }, 'Client-sorted (all data)')
        ),
        route === 'client-sorted'
            ? React.createElement(ClientSortedGrid)
            : React.createElement(ServerSortedGrid)
    );
}

createRoot(document.getElementById('root')!).render(React.createElement(App));
