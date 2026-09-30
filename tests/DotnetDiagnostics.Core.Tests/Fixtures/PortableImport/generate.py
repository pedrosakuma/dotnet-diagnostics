"""Independent known-kind v1/v2 fixture. Never reads production schema/export code."""
import hashlib
import json
import pathlib
import sqlite3
import struct
import tempfile
import zlib

ROOT = pathlib.Path(__file__).parent
OUTPUT = ROOT / "known-counters-v1-v2.ddcapture"
if OUTPUT.exists():
    raise SystemExit("Refusing to overwrite frozen fixture")

SQL = """
CREATE TABLE format(package INTEGER NOT NULL,schema_version INTEGER NOT NULL,record_version INTEGER NOT NULL,index_version INTEGER NOT NULL,writer_version INTEGER NOT NULL,reader_version INTEGER NOT NULL);
CREATE TABLE strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL UNIQUE);
CREATE TABLE artifacts(id TEXT PRIMARY KEY,kind TEXT NOT NULL,name TEXT NOT NULL);
CREATE TABLE occurrences(id INTEGER PRIMARY KEY,artifact_id TEXT NOT NULL REFERENCES artifacts(id),timestamp_ticks INTEGER,thread_id INTEGER,category_id INTEGER REFERENCES strings(id),name_id INTEGER REFERENCES strings(id),numeric_value REAL,duration_ns INTEGER,unit_id INTEGER REFERENCES strings(id));
CREATE TABLE fields(record_id INTEGER NOT NULL REFERENCES occurrences(id),ordinal INTEGER NOT NULL,name_id INTEGER NOT NULL REFERENCES strings(id),kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 4),string_id INTEGER REFERENCES strings(id),int_value INTEGER,double_value REAL,bool_value INTEGER,unit_id INTEGER REFERENCES strings(id),PRIMARY KEY(record_id,ordinal));
CREATE TABLE snapshots(artifact_id TEXT PRIMARY KEY REFERENCES artifacts(id),version INTEGER NOT NULL,json BLOB NOT NULL);
CREATE INDEX ix_occurrence_artifact ON occurrences(artifact_id,id);
CREATE INDEX ix_occurrence_time ON occurrences(artifact_id,timestamp_ticks,id);
CREATE INDEX ix_occurrence_thread ON occurrences(artifact_id,thread_id,id);
CREATE INDEX ix_occurrence_category ON occurrences(artifact_id,category_id,id);
CREATE INDEX ix_occurrence_name ON occurrences(artifact_id,name_id,id);
CREATE INDEX ix_fields_name ON fields(name_id,record_id);
INSERT INTO strings VALUES(1,'counter'),(2,'working-set'),(3,'value'),(4,'bytes');
INSERT INTO artifacts VALUES('cccccccccccccccccccccccccccccccc','counters','Independent counters');
INSERT INTO occurrences VALUES(41,'cccccccccccccccccccccccccccccccc',621355968000000000,42,1,2,NULL,NULL,4);
INSERT INTO fields VALUES(41,0,3,2,NULL,7,NULL,NULL,4);
"""


def encode(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def digest(data):
    return hashlib.sha256(data).hexdigest()


members = []
entries = []
source_hashes = {}
for version in (1, 2):
    with tempfile.TemporaryDirectory() as staging:
        database = pathlib.Path(staging) / "capture.sqlite"
        connection = sqlite3.connect(database)
        connection.executescript(SQL)
        connection.execute("INSERT INTO format VALUES(?,1,1,1,?,?)", (version, version, version))
        connection.commit()
        connection.close()
        data = database.read_bytes()
    axes = dict(packageVersion=version, schemaVersion=1, recordVersion=1,
                indexVersion=1, writerVersion=version, requiredReaderVersion=version)
    quality = dict(Offered=5, Accepted=3, Persisted=1, RecordRejected=1, QueueRejected=1,
                   StorageRejected=2, Pending=0, SourceRejected=None, Interrupted=True,
                   UnknownTail=True, SnapshotRejected=1, IsIncomplete=True, IsComplete=False)
    info = dict(CaptureId="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OwnerId="original-owner",
                Name="Independent known counters", GroupId="independent-group",
                CreatedUtc="2026-01-02T03:04:05+00:00", State=1,
                Artifacts=[dict(ArtifactId="cccccccccccccccccccccccccccccccc",
                                Kind="counters", Name="Independent counters")], Quality=quality)
    features = ["normalized-scalars-v1"]
    if version == 2:
        features.append("artifact-provenance-v1")
    manifest = encode(dict(Info=info, PackageVersion=version, SchemaVersion=1,
                           RecordVersion=1, IndexVersion=1, WriterVersion=version,
                           ReaderVersion=version, RequiredFeatures=features, ReservationBytes=536870912))
    seal = encode(dict(ManifestHash=digest(manifest).upper(), DatabaseHash=digest(data).upper()))
    content = [("manifest.json", manifest), ("capture.sqlite", data), ("seal.json", seal)]
    entry = str(version) * 32
    entries.append(dict(entryId=entry, label="../duplicate-label", sourceCaptureId=info["CaptureId"],
                        format=axes, members=[dict(name=name, bytes=len(value), sha256=digest(value))
                                             for name, value in content]))
    members.extend((f"entries/{entry}/{name}", value) for name, value in content)
    source_hashes[f"v{version}"] = {name: digest(value) for name, value in content}

index = encode(dict(archiveVersion=1, requiredArchiveReaderVersion=1,
                    requiredFeatures=["independent-captures-v1", "stored-zip-v1", "index-sha256-v1"],
                    bundleId="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", createdUtc="2026-01-02T03:04:05+00:00",
                    entries=entries))
index_seal = encode(dict(archiveVersion=1, indexBytes=len(index), indexSha256=digest(index)))
members = [("bundle.json", index), ("bundle.seal.json", index_seal)] + members

archive = bytearray()
directory = bytearray()
for name, data in members:
    name = name.encode("ascii")
    offset = len(archive)
    crc = zlib.crc32(data)
    archive.extend(struct.pack("<I5H3I2H", 0x04034B50, 20, 0x800, 0, 0, 0,
                               crc, len(data), len(data), len(name), 0))
    archive.extend(name)
    archive.extend(data)
    directory.extend(struct.pack("<I6H3I5H2I", 0x02014B50, 20, 20, 0x800, 0, 0, 0,
                                 crc, len(data), len(data), len(name), 0, 0, 0, 0, 0, offset))
    directory.extend(name)
offset = len(archive)
archive.extend(directory)
archive.extend(struct.pack("<I4H2IH", 0x06054B50, 0, 0, len(members), len(members),
                           len(directory), offset, 0))
OUTPUT.write_bytes(archive)
provenance = dict(generator="Python standard-library SQLite and explicit ZIP structs; no production exporter",
                  sqliteVersion=sqlite3.sqlite_version, archiveSha256=digest(archive), members=source_hashes)
(ROOT / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
print(json.dumps(provenance, indent=2))
