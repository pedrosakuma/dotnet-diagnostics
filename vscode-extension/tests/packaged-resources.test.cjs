const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { createVSIX } = require("@vscode/vsce");
const { readZip } = require("@vscode/vsce/out/zip.js");

const root = path.join(__dirname, "..");

test("VSIX contains every declared walkthrough Markdown resource without unrelated media", { timeout: 60000 }, async t => {
    const temp = await fs.mkdtemp(path.join(os.tmpdir(), "dotnet-diagnostics-vsix-"));
    t.after(() => fs.rm(temp, { recursive: true, force: true }));
    const packagePath = path.join(temp, "extension.vsix");
    await createVSIX({ cwd: root, packagePath, dependencies: false });

    // Inspect the archive, not the source tree or vsce's candidate file list.
    const files = await readZip(packagePath, name => !name.endsWith("/") && (
        name === "extension/package.json"
        || name.startsWith("extension/media/")
        || /^extension\/(scripts|src|tests|node_modules)\//.test(name)
    ));
    const manifest = JSON.parse(files.get("extension/package.json").toString("utf8"));
    const markdownPaths = manifest.contributes.walkthroughs.flatMap(walkthrough =>
        walkthrough.steps.flatMap(step => step.media?.markdown ? [step.media.markdown] : []));
    assert.ok(markdownPaths.length > 0, "expected manifest-declared walkthrough Markdown");
    for (const markdown of markdownPaths) {
        const packaged = files.get(`extension/${markdown}`);
        assert.ok(packaged, `VSIX is missing walkthrough Markdown: ${markdown}`);
        assert.deepEqual(packaged, await fs.readFile(path.join(root, markdown)),
            `VSIX walkthrough contents differ from source: ${markdown}`);
        assert.ok(packaged.length > 0, `walkthrough Markdown is empty: ${markdown}`);
    }

    assert.deepEqual([...files.keys()].filter(name => name.startsWith("extension/media/")).sort(),
        ["extension/" + manifest.icon, ...markdownPaths.map(markdown => "extension/" + markdown)].sort());
    assert.ok(![...files.keys()].some(name => /^extension\/(scripts|src|tests|node_modules)\//.test(name)),
        "development files must remain excluded from the VSIX");
});
