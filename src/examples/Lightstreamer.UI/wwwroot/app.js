import ts from 'https://esm.sh/typescript@5.6.3';

async function loadTypeScriptModule(path) {
    const source = await fetch(path, { cache: 'no-store' }).then((r) => r.text());
    const transpiled = ts.transpileModule(source, {
        compilerOptions: {
            target: ts.ScriptTarget.ES2022,
            module: ts.ModuleKind.ESNext,
            moduleResolution: ts.ModuleResolutionKind.Bundler
        }
    }).outputText;
    return URL.createObjectURL(new Blob([transpiled], { type: 'text/javascript' }));
}

const appUrl = await loadTypeScriptModule('/app.ts');
try {
    await import(appUrl);
} finally {
    URL.revokeObjectURL(appUrl);
}
