import ts from 'https://esm.sh/typescript@5.6.3';

async function loadTypeScriptModule(path, replacements = []) {
    const source = await fetch(path, { cache: 'no-store' }).then((response) => response.text());
    const adjustedSource = [...replacements]
        .sort((a, b) => b[0].length - a[0].length)
        .reduce((text, [from, to]) => text.replaceAll(from, to), source);
    const transpiled = ts.transpileModule(adjustedSource, {
        compilerOptions: {
            target: ts.ScriptTarget.ES2022,
            module: ts.ModuleKind.ESNext,
            moduleResolution: ts.ModuleResolutionKind.Bundler
        }
    }).outputText;

    return URL.createObjectURL(new Blob([transpiled], { type: 'text/javascript' }));
}

const tradesSharedModuleUrl = await loadTypeScriptModule('/tradesShared.ts');
const useTradesSubscriptionModuleUrl = await loadTypeScriptModule('/useTradesSubscription.ts', [
    ['./tradesShared', tradesSharedModuleUrl],
    ['./tradesShared.ts', tradesSharedModuleUrl]
]);
const tradesStatusPanelModuleUrl = await loadTypeScriptModule('/TradesStatusPanel.ts', [
    ['./tradesShared', tradesSharedModuleUrl],
    ['./tradesShared.ts', tradesSharedModuleUrl]
]);
const tradesControlsPanelModuleUrl = await loadTypeScriptModule('/TradesControlsPanel.ts', [
    ['./tradesShared', tradesSharedModuleUrl],
    ['./tradesShared.ts', tradesSharedModuleUrl]
]);
const tradesGridViewModuleUrl = await loadTypeScriptModule('/TradesGridView.ts', [
    ['./tradesShared', tradesSharedModuleUrl],
    ['./tradesShared.ts', tradesSharedModuleUrl]
]);
const tradesAppModuleUrl = await loadTypeScriptModule('/TradesApp.ts', [
    ['./tradesShared', tradesSharedModuleUrl],
    ["./tradesShared.ts", tradesSharedModuleUrl],
    ['./useTradesSubscription', useTradesSubscriptionModuleUrl],
    ["./useTradesSubscription.ts", useTradesSubscriptionModuleUrl],
    ['./TradesStatusPanel', tradesStatusPanelModuleUrl],
    ["./TradesStatusPanel.ts", tradesStatusPanelModuleUrl],
    ['./TradesControlsPanel', tradesControlsPanelModuleUrl],
    ["./TradesControlsPanel.ts", tradesControlsPanelModuleUrl],
    ['./TradesGridView', tradesGridViewModuleUrl],
    ["./TradesGridView.ts", tradesGridViewModuleUrl]
]);
const appModuleUrl = await loadTypeScriptModule('/app.ts', [
    ['./TradesApp', tradesAppModuleUrl],
    ["./TradesApp.ts", tradesAppModuleUrl]
]);

try {
    await import(appModuleUrl);
} finally {
    URL.revokeObjectURL(tradesSharedModuleUrl);
    URL.revokeObjectURL(useTradesSubscriptionModuleUrl);
    URL.revokeObjectURL(tradesStatusPanelModuleUrl);
    URL.revokeObjectURL(tradesControlsPanelModuleUrl);
    URL.revokeObjectURL(tradesGridViewModuleUrl);
    URL.revokeObjectURL(tradesAppModuleUrl);
    URL.revokeObjectURL(appModuleUrl);
}
