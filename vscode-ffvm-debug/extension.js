const { LanguageClient, TransportKind } = require("vscode-languageclient/node");
const path = require("path");

let client;

function activate(context) {
    // Path to the LSP server executable
    const serverCommand = path.join(
        context.extensionPath, "..", "StandaloneRunner", "bin", "Release", "net8.0", "StandaloneRunner"
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

    client.start();
}

function deactivate() {
    if (client) {
        return client.stop();
    }
}

module.exports = { activate, deactivate };
