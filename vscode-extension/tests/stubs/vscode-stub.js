// Minimal stand-in for the "vscode" module API surface touched merely by
// requiring/evaluating out/extension.js (class field initializers, enums
// referenced at module scope, etc.) so tests can exercise pure functions
// like renderHtml() without a real VS Code host.
class ThemeIcon {
  constructor(id) { this.id = id; }
}
class TreeItem {
  constructor(label, collapsibleState) {
    this.label = label;
    this.collapsibleState = collapsibleState;
  }
}
const TreeItemCollapsibleState = { None: 0, Collapsed: 1, Expanded: 2 };
module.exports = {
  ThemeIcon,
  TreeItem,
  TreeItemCollapsibleState,
  window: {
    createOutputChannel: () => ({ appendLine() {}, dispose() {} }),
    createTreeView: () => ({ dispose() {} }),
    showQuickPick: async () => undefined,
    createWebviewPanel: () => ({ webview: {}, dispose() {}, onDidDispose() {} }),
  },
  commands: { registerCommand: () => ({ dispose() {} }) },
  Uri: { file: p => ({ fsPath: p }) },
  ViewColumn: { One: 1 },
};
