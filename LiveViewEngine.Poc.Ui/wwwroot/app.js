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

const webHostClientModuleUrl = await loadTypeScriptModule('/webHostClient.ts');
const appModuleUrl = await loadTypeScriptModule('/app.ts', [
    ['./webHostClient', webHostClientModuleUrl],
    ["./webHostClient.ts", webHostClientModuleUrl]
]);

try {
    await import(appModuleUrl);
} finally {
    URL.revokeObjectURL(webHostClientModuleUrl);
    URL.revokeObjectURL(appModuleUrl);
}
