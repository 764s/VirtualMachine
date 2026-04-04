const vscode = require("vscode");
const path = require("path");

let client;
let outputChannel;

function activate(context) {
    outputChannel = vscode.window.createOutputChannel("FFVM Debug");
    outputChannel.appendLine("[FFVM] Extension activating...");

    // --- DAP: register adapter descriptor factory for attach mode (always register first) ---
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory("ffvm", {
            createDebugAdapterDescriptor(session, executable) {
                outputChannel.appendLine(`[FFVM] createDebugAdapterDescriptor called: request=${session.configuration.request}, port=${session.configuration.port}`);
                if (session.configuration.request === "attach") {
                    const port = session.configuration.port || 4711;
                    outputChannel.appendLine(`[FFVM] Returning DebugAdapterServer on port ${port}`);
                    return new vscode.DebugAdapterServer(port);
                }
                outputChannel.appendLine("[FFVM] Returning default executable (launch mode)");
                // launch mode: use the declared executable (StandaloneRunner --dap)
                return executable;
            }
        })
    );

    // --- Doc comment template: auto-generate /// template above func/struct ---
    context.subscriptions.push(
        vscode.languages.registerCompletionItemProvider(
            { scheme: "file", language: "ffvm" },
            {
                provideCompletionItems(document, position) {
                    const lineText = document.lineAt(position.line).text;
                    const prefix = lineText.substring(0, position.character);
                    // Only trigger when user types "///"
                    if (!prefix.trimStart().startsWith("///")) return undefined;

                    // Collect existing /// lines above (before current line)
                    const existingDoc = [];
                    for (let i = position.line - 1; i >= 0; i--) {
                        const text = document.lineAt(i).text.trim();
                        if (text.startsWith("///")) {
                            existingDoc.unshift(text.substring(3).trim());
                        } else {
                            break;
                        }
                    }

                    // Look at the next non-empty, non-/// line to find func/struct
                    let nextLine = null;
                    for (let i = position.line + 1; i < document.lineCount; i++) {
                        const text = document.lineAt(i).text.trim();
                        if (text.startsWith("///") || text === "") continue;
                        nextLine = text;
                        break;
                    }
                    if (!nextLine) return undefined;

                    // Parse func signature
                    const funcMatch = nextLine.match(/^func\s+(\w+)\(([^)]*)\)(?:\s*:\s*(\w+))?/);
                    if (funcMatch) {
                        const [, name, paramsStr, returnType] = funcMatch;
                        const params = paramsStr.trim() ? paramsStr.split(",").map(p => p.trim().split(":")[0].trim()) : [];

                        // Check what already exists
                        const hasDesc = existingDoc.some(l => !l.startsWith("@param") && !l.startsWith("@return"));
                        const existingParams = new Set(
                            existingDoc.filter(l => l.startsWith("@param ")).map(l => l.split(/\s+/)[1])
                        );
                        const hasReturn = existingDoc.some(l => l.startsWith("@return"));

                        // Build snippet using SnippetString API for reliable multi-line insertion
                        const snippet = new vscode.SnippetString();
                        let hasContent = false;
                        if (!hasDesc && existingDoc.length === 0) {
                            snippet.appendText("/// ");
                            snippet.appendPlaceholder("Description");
                            hasContent = true;
                        }
                        for (const p of params) {
                            if (!existingParams.has(p)) {
                                if (hasContent) snippet.appendText("\n");
                                snippet.appendText("/// @param " + p + " ");
                                snippet.appendPlaceholder(p);
                                hasContent = true;
                            }
                        }
                        if (returnType && !hasReturn) {
                            if (hasContent) snippet.appendText("\n");
                            snippet.appendText("/// @return ");
                            snippet.appendPlaceholder("return value");
                            hasContent = true;
                        }

                        if (!hasContent) return undefined; // nothing to add

                        const item = new vscode.CompletionItem("/// (doc comment)", vscode.CompletionItemKind.Snippet);
                        item.insertText = snippet;
                        item.filterText = "///";
                        item.preselect = true;
                        item.detail = existingDoc.length > 0
                            ? `Complete doc for func ${name}`
                            : `Generate doc for func ${name}`;
                        item.range = new vscode.Range(
                            position.line, lineText.indexOf("///"),
                            position.line, position.character
                        );
                        item.sortText = "!";
                        return [item];
                    }

                    // Parse struct
                    const structMatch = nextLine.match(/^struct\s+(\w+)/);
                    if (structMatch) {
                        if (existingDoc.some(l => !l.startsWith("@"))) return undefined; // already has desc
                        const snippet = new vscode.SnippetString("/// ");
                        snippet.appendPlaceholder("Description");
                        const item = new vscode.CompletionItem("/// (doc comment)", vscode.CompletionItemKind.Snippet);
                        item.insertText = snippet;
                        item.filterText = "///";
                        item.preselect = true;
                        item.detail = `Generate doc for struct ${structMatch[1]}`;
                        item.range = new vscode.Range(
                            position.line, lineText.indexOf("///"),
                            position.line, position.character
                        );
                        item.sortText = "!";
                        return [item];
                    }

                    return undefined;
                }
            },
            "/" // trigger on '/'
        )
    );

    outputChannel.appendLine("[FFVM] DAP factory registered.");
    try {
        const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

        // Resolve StandaloneRunner relative to workspace, not extension install path
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders || workspaceFolders.length === 0) return;

        const serverCommand = path.join(
            workspaceFolders[0].uri.fsPath,
            "StandaloneRunner", "bin", "Release", "net10.0", "StandaloneRunner"
        );

        const serverOptions = {
            command: serverCommand,
            args: ["--lsp"],
            transport: TransportKind.stdio
        };

        const clientOptions = {
            documentSelector: [{ scheme: "file", language: "ffvm" }]
        };

        client = new LanguageClient(
            "ffvm-lsp",
            "FFVM Language Server",
            serverOptions,
            clientOptions
        );

        client.start().catch(err => {
            console.warn("[FFVM] LSP server failed to start:", err.message);
        });
    } catch (err) {
        console.warn("[FFVM] LSP setup failed:", err.message);
    }
}

function deactivate() {
    if (client) {
        return client.stop();
    }
}

module.exports = { activate, deactivate };
