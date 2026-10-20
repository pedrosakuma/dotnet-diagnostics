const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const Module = require("node:module");
const resolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
    return request === "vscode" ? path.join(__dirname, "stubs", "vscode-stub.js")
        : resolve.call(this, request, ...rest);
};
const { WEBVIEW_FIELD_STYLES } = require("../out/webviewFields.js");
const { renderHtml, renderDumpAnalysisHtml } = require("../out/extension.js");
const { renderHistoryHtml } = require("../out/captureHistoryPanel.js");

const panels = {
    live: renderHtml({ processId: 42, managedEntrypointAssemblyName: "Sample" }, "fields", 120),
    dump: renderDumpAnalysisHtml("sample.dmp", "fields"),
    saved: renderHistoryHtml("fields", "csp", {
        name: "Saved", captureId: "a".repeat(32), createdUtc: "2026-10-20",
        artifacts: [{ artifactId: "b".repeat(32), name: "Heap", kind: "heap-snapshot", supportedViews: ["top-types"] }],
    }),
};

for (const [name, html] of Object.entries(panels)) {
    test(`${name} panel embeds shared field styles inside its nonce-protected style block`, () => {
        const css = html.match(/<style nonce="fields">([\s\S]*?)<\/style>/)?.[1];
        assert.ok(css?.includes(WEBVIEW_FIELD_STYLES));
        assert.doesNotMatch(css, /button,\s*select\s*\{/);
        assert.doesNotMatch(css, /\.statics-form input\s*\{[^}]*min-width:\s*20rem/);
    });
}

test("field rules use separate native tokens and preserve visible focus and state styling", () => {
    for (const token of ["input-foreground", "input-background", "input-border", "input-placeholderForeground",
        "dropdown-foreground", "dropdown-background", "dropdown-border", "contrastBorder", "focusBorder",
        "disabledForeground", "inputValidation-errorBackground", "inputValidation-errorBorder"]) {
        assert.ok(WEBVIEW_FIELD_STYLES.includes(`--vscode-${token}`), token);
    }
    assert.match(WEBVIEW_FIELD_STYLES, /select:focus-visible/);
    assert.match(WEBVIEW_FIELD_STYLES, /select option:disabled/);
    assert.match(WEBVIEW_FIELD_STYLES, /input\[type="search"\]::placeholder/);
    assert.match(WEBVIEW_FIELD_STYLES, /input\[type="text"\]\[aria-invalid="true"\]/);
    assert.match(WEBVIEW_FIELD_STYLES, /font:inherit/);
    assert.doesNotMatch(WEBVIEW_FIELD_STYLES, /#[0-9a-f]{3,8}\b|rgb\(/i);
});

test("shared field rules do not restyle range or checkbox inputs or remove native select appearance", () => {
    for (const rule of WEBVIEW_FIELD_STYLES.matchAll(/([^{}]+)\{[^{}]*\}/g)) {
        for (const selector of rule[1].split(",")) {
            if (selector.includes("input")) assert.match(selector, /input\[type="(?:text|search)"\]/);
        }
    }
    assert.doesNotMatch(WEBVIEW_FIELD_STYLES, /appearance\s*:\s*none|outline\s*:\s*(?:0|none)/);
    assert.match(WEBVIEW_FIELD_STYLES, /max-width:100%/);
    assert.match(WEBVIEW_FIELD_STYLES, /min-width:0/);
});
