"""Create an independent synthetic fixture, never query or rewrite archived data."""
import hashlib
import json
import pathlib
import sqlite3

ROOT = pathlib.Path(__file__).resolve().parent
SQL = """
CREATE TABLE format(package INTEGER NOT NULL,schema_version INTEGER NOT NULL,record_version INTEGER NOT NULL,index_version INTEGER NOT NULL,writer_version INTEGER NOT NULL,reader_version INTEGER NOT NULL);
INSERT INTO format VALUES(2,1,1,1,2,2);
CREATE TABLE strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL UNIQUE);
CREATE TABLE artifacts(id TEXT PRIMARY KEY,kind TEXT NOT NULL,name TEXT NOT NULL);
CREATE TABLE occurrences(
  id INTEGER PRIMARY KEY,artifact_id TEXT NOT NULL REFERENCES artifacts(id),
  timestamp_ticks INTEGER,thread_id INTEGER,category_id INTEGER REFERENCES strings(id),
  name_id INTEGER REFERENCES strings(id),numeric_value REAL,duration_ns INTEGER,unit_id INTEGER REFERENCES strings(id));
CREATE TABLE fields(
  record_id INTEGER NOT NULL REFERENCES occurrences(id),ordinal INTEGER NOT NULL,
  name_id INTEGER NOT NULL REFERENCES strings(id),kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 4),
  string_id INTEGER REFERENCES strings(id),int_value INTEGER,double_value REAL,bool_value INTEGER,
  unit_id INTEGER REFERENCES strings(id),PRIMARY KEY(record_id,ordinal));
CREATE TABLE snapshots(artifact_id TEXT PRIMARY KEY REFERENCES artifacts(id),version INTEGER NOT NULL,json BLOB NOT NULL);
CREATE INDEX ix_occurrence_artifact ON occurrences(artifact_id,id);
CREATE INDEX ix_occurrence_time ON occurrences(artifact_id,timestamp_ticks,id);
CREATE INDEX ix_occurrence_thread ON occurrences(artifact_id,thread_id,id);
CREATE INDEX ix_occurrence_category ON occurrences(artifact_id,category_id,id);
CREATE INDEX ix_occurrence_name ON occurrences(artifact_id,name_id,id);
CREATE INDEX ix_fields_name ON fields(name_id,record_id);
INSERT INTO artifacts VALUES('11111111111111111111111111111111','synthetic','Independent v2 scalar fixture');
INSERT INTO strings VALUES(1,'sample'),(2,'value'),(3,'offline');
INSERT INTO occurrences VALUES(1,'11111111111111111111111111111111',638942688000000000,7,NULL,1,1.5,123,NULL);
INSERT INTO fields VALUES(1,0,2,1,3,NULL,NULL,NULL,NULL);
INSERT INTO snapshots VALUES('11111111111111111111111111111111',1,x'7b7d');
"""

if __name__ == "__main__":
    target = ROOT / "v2.sqlite"
    if target.exists():
        raise SystemExit("Frozen fixture already exists; refusing to rewrite it.")
    with sqlite3.connect(target) as db:
        db.executescript(SQL)
    provenance = {
        "origin": "Independent known SQL executed only to CREATE this synthetic v2 fixture; no production writer used.",
        "sqlite": sqlite3.sqlite_version,
        "artifactId": "11111111111111111111111111111111",
        "format": [2, 1, 1, 1, 2, 2],
        "rows": [1, 3, 1, 1, 1, 1],
        "snapshotSemantics": "Synthetic JSON; no supported typed-codec validity is asserted.",
        "databaseSha256": hashlib.sha256(target.read_bytes()).hexdigest(),
        "generatorSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
    }
    (ROOT / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
