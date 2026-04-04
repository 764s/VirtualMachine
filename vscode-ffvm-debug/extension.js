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
