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
const sharedModuleUrl = await loadTypeScriptModule('/shared.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl]
]);
const serverSortedGridModuleUrl = await loadTypeScriptModule('/serverSortedGrid.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl],
    ['./shared', sharedModuleUrl],
    ["./shared.ts", sharedModuleUrl]
]);
const clientSortedGridModuleUrl = await loadTypeScriptModule('/clientSortedGrid.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl],
    ['./shared', sharedModuleUrl],
    ["./shared.ts", sharedModuleUrl]
]);
const appModuleUrl = await loadTypeScriptModule('/app.ts', [
    ['./shared', sharedModuleUrl],
    ["./shared.ts", sharedModuleUrl],
    ['./serverSortedGrid', serverSortedGridModuleUrl],
    ["./serverSortedGrid.ts", serverSortedGridModuleUrl],
    ['./clientSortedGrid', clientSortedGridModuleUrl],
    ["./clientSortedGrid.ts", clientSortedGridModuleUrl]
]);

try {
    await import(appModuleUrl);
} finally {
    URL.revokeObjectURL(compactProtocolModuleUrl);
    URL.revokeObjectURL(jsonProtocolModuleUrl);
    URL.revokeObjectURL(webHostClientModuleUrl);
    URL.revokeObjectURL(sharedModuleUrl);
    URL.revokeObjectURL(serverSortedGridModuleUrl);
    URL.revokeObjectURL(clientSortedGridModuleUrl);
    URL.revokeObjectURL(appModuleUrl);
}
