import ts from 'https://esm.sh/typescript@5.6.3';

async function loadTypeScriptModule(path, replacements = []) {
    const source = await fetch(path, { cache: 'no-store' }).then((response) => response.text());
    const adjustedSource = replacements.reduce(
        (text, [from, to]) => text.replaceAll(from, to),
        source
    );
    const transpiled = ts.transpileModule(adjustedSource, {
        compilerOptions: {
            target: ts.ScriptTarget.ES2022,
            module: ts.ModuleKind.ESNext,
            moduleResolution: ts.ModuleResolutionKind.Bundler
        }
    }).outputText;

    return URL.createObjectURL(new Blob([transpiled], { type: 'text/javascript' }));
}

const compactProtocolModuleUrl = await loadTypeScriptModule('/compactProtocol.ts');
const jsonProtocolModuleUrl = await loadTypeScriptModule('/jsonProtocol.ts');
const webHostClientModuleUrl = await loadTypeScriptModule('/webHostClient.ts', [
    ['./compactProtocol', compactProtocolModuleUrl],
    ["./compactProtocol.ts", compactProtocolModuleUrl],
    ['./jsonProtocol', jsonProtocolModuleUrl],
    ["./jsonProtocol.ts", jsonProtocolModuleUrl]
]);
const gridSharedModuleUrl = await loadTypeScriptModule('/gridShared.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl]
]);
const serverSideGridViewModuleUrl = await loadTypeScriptModule('/ServerSideGridView.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl],
    ['./gridShared', gridSharedModuleUrl],
    ["./gridShared.ts", gridSharedModuleUrl]
]);
const clientSideGridViewModuleUrl = await loadTypeScriptModule('/ClientSideGridView.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl],
    ['./gridShared', gridSharedModuleUrl],
    ["./gridShared.ts", gridSharedModuleUrl]
]);
const appModuleUrl = await loadTypeScriptModule('/app.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl],
    ['./gridShared', gridSharedModuleUrl],
    ["./gridShared.ts", gridSharedModuleUrl],
    ['./ServerSideGridView', serverSideGridViewModuleUrl],
    ["./ServerSideGridView.ts", serverSideGridViewModuleUrl],
    ['./ClientSideGridView', clientSideGridViewModuleUrl],
    ["./ClientSideGridView.ts", clientSideGridViewModuleUrl]
]);

try {
    await import(appModuleUrl);
} finally {
    URL.revokeObjectURL(compactProtocolModuleUrl);
    URL.revokeObjectURL(jsonProtocolModuleUrl);
    URL.revokeObjectURL(webHostClientModuleUrl);
    URL.revokeObjectURL(gridSharedModuleUrl);
    URL.revokeObjectURL(serverSideGridViewModuleUrl);
    URL.revokeObjectURL(clientSideGridViewModuleUrl);
    URL.revokeObjectURL(appModuleUrl);
}
