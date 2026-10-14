const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const read = name => fs.readFileSync(path.join(root, name), "utf8");

function pngSize(file) {
    const data = fs.readFileSync(path.join(root, file));
    assert.equal(data.subarray(0, 8).toString("hex"), "89504e470d0a1a0a", `${file} must be a PNG`);
    return { width: data.readUInt32BE(16), height: data.readUInt32BE(20) };
}

test("manifest declares discoverable Marketplace categories and keywords", () => {
    assert.ok(!manifest.categories.includes("Other"));
    assert.deepEqual(manifest.categories.slice().sort(), ["Debuggers", "Visualization"]);
    for (const keyword of ["dotnet", "diagnostics", "profiling", "eventpipe", "gc", "counters", "heap", "dump"]) {
        assert.ok(manifest.keywords.includes(keyword), `missing keyword ${keyword}`);
    }
    assert.ok(manifest.keywords.length <= 30, "Marketplace accepts at most 30 keywords");
});

test("icon is a raster PNG of at least 128x128 and the banner is configured", () => {
    assert.match(manifest.icon, /\.png$/);
    const { width, height } = pngSize(manifest.icon);
    assert.equal(width, height);
    assert.ok(width >= 128);
    assert.match(manifest.galleryBanner.color, /^#[0-9a-fA-F]{6}$/);
    assert.ok(["dark", "light"].includes(manifest.galleryBanner.theme));
});

test("README screenshots resolve to repository raw URLs after vsce processing", async () => {
    const { ReadmeProcessor } = require("@vscode/vsce/out/package.js");
    const readme = read("README.md");
    const processor = new ReadmeProcessor(manifest, {});
    const processed = await processor.onFile({ path: "extension/readme.md", contents: Buffer.from(readme) });
    const effective = processed.contents.toString();
    const urls = [...effective.matchAll(/!\[[^\]]*\]\(([^)]+\.png)\)/g)].map(m => m[1]).sort();
    const prefix = "https://raw.githubusercontent.com/pedrosakuma/dotnet-diagnostics/main/vscode-extension/media/";
    assert.deepEqual(urls, ["heap-drilldown.png", "live-counters.png", "thread-snapshot.png"].map(f => prefix + f));
    for (const url of urls) {
        const file = "media/" + url.slice(prefix.length);
        assert.ok(pngSize(file).width >= 800);
    }
    assert.match(readme, /illustrative/i);
    assert.match(read("CHANGELOG.md"), /## Unreleased/);
});

test(".vscodeignore ships the icon but not screenshots or generator scripts", () => {
    const lines = read(".vscodeignore").split(/\r?\n/);
    assert.ok(lines.includes("!media/icon.png"));
    assert.ok(lines.includes("media/**"));
    assert.ok(lines.includes("scripts/**"));
});
