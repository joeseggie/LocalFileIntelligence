-- Current schema snapshot after applying the immutable Phase 1 migrations.
PRAGMA foreign_keys = ON;
BEGIN IMMEDIATE;

CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY CHECK (version > 0),
    name TEXT NOT NULL UNIQUE,
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
    applied_at TEXT NOT NULL CHECK (applied_at GLOB '????-??-??T??:??:??.???Z')
) STRICT;

CREATE TABLE index_roots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL,
    path_key TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
    status TEXT NOT NULL CHECK (status IN ('pending', 'scanning', 'ready', 'paused', 'offline', 'access_denied', 'error')),
    volume_identity TEXT NOT NULL,
    directory_identity TEXT NOT NULL,
    policy_revision INTEGER NOT NULL DEFAULT 1 CHECK (policy_revision > 0),
    index_revision INTEGER NOT NULL DEFAULT 0 CHECK (index_revision >= 0),
    scan_generation INTEGER NOT NULL DEFAULT 0 CHECK (scan_generation >= 0),
    created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??.???Z'),
    updated_at TEXT NOT NULL CHECK (updated_at GLOB '????-??-??T??:??:??.???Z'),
    last_scan_at TEXT CHECK (last_scan_at GLOB '????-??-??T??:??:??.???Z'),
    last_error TEXT
) STRICT;

CREATE TABLE file_entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    relative_path TEXT NOT NULL CHECK (length(relative_path) > 0),
    path_key TEXT NOT NULL,
    parent_path_key TEXT NOT NULL,
    name TEXT NOT NULL,
    extension TEXT NOT NULL,
    entry_type TEXT NOT NULL CHECK (entry_type IN ('file', 'directory')),
    size_bytes INTEGER NOT NULL DEFAULT 0 CHECK (size_bytes >= 0),
    created_at_fs TEXT NOT NULL CHECK (created_at_fs GLOB '????-??-??T??:??:??.???Z'),
    modified_at_fs TEXT NOT NULL CHECK (modified_at_fs GLOB '????-??-??T??:??:??.???Z'),
    last_seen_at TEXT NOT NULL CHECK (last_seen_at GLOB '????-??-??T??:??:??.???Z'),
    last_indexed_at TEXT CHECK (last_indexed_at GLOB '????-??-??T??:??:??.???Z'),
    sha256 TEXT CHECK (length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
    extraction_status TEXT NOT NULL CHECK (extraction_status IN ('pending', 'extracted', 'unsupported', 'failed')),
    indexing_status TEXT NOT NULL CHECK (indexing_status IN ('pending', 'indexed', 'excluded', 'inaccessible', 'failed')),
    is_deleted INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
    deleted_at TEXT CHECK (deleted_at GLOB '????-??-??T??:??:??.???Z'),
    last_error TEXT,
    volume_file_identity TEXT,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    last_seen_generation INTEGER NOT NULL DEFAULT 0 CHECK (last_seen_generation >= 0),
    UNIQUE (root_id, path_key),
    UNIQUE (root_id, id),
    UNIQUE (id, revision),
    CHECK ((is_deleted = 1 AND deleted_at IS NOT NULL) OR (is_deleted = 0 AND deleted_at IS NULL)),
    CHECK (entry_type = 'file' OR (size_bytes = 0 AND sha256 IS NULL))
) STRICT;

CREATE INDEX entries_by_parent_path_key ON file_entries(root_id, parent_path_key, is_deleted, path_key);
CREATE INDEX entries_by_generation ON file_entries(root_id, last_seen_generation);
CREATE INDEX entries_by_extension ON file_entries(root_id, extension, is_deleted);
CREATE INDEX entries_by_hash ON file_entries(sha256, size_bytes)
WHERE sha256 IS NOT NULL AND is_deleted = 0 AND entry_type = 'file';

CREATE TABLE file_contents (
    file_id INTEGER PRIMARY KEY,
    file_revision INTEGER NOT NULL,
    extracted_text TEXT NOT NULL,
    extractor TEXT NOT NULL,
    extractor_version TEXT NOT NULL,
    extracted_at TEXT NOT NULL CHECK (extracted_at GLOB '????-??-??T??:??:??.???Z'),
    content_length INTEGER NOT NULL CHECK (content_length BETWEEN 0 AND 2097152),
    FOREIGN KEY (file_id, file_revision) REFERENCES file_entries(id, revision) ON DELETE CASCADE
) STRICT;

CREATE TRIGGER content_requires_current_file
BEFORE INSERT ON file_contents
WHEN NOT EXISTS (
    SELECT 1 FROM file_entries f JOIN index_roots r ON r.id = f.root_id
    WHERE f.id = NEW.file_id AND f.revision = NEW.file_revision
      AND f.entry_type = 'file' AND f.is_deleted = 0
      AND f.indexing_status = 'indexed' AND f.extraction_status = 'extracted'
      AND r.enabled = 1 AND r.status NOT IN ('offline', 'access_denied')
)
BEGIN
    SELECT RAISE(ABORT, 'content requires a current authorized file revision');
END;

CREATE TRIGGER content_update_requires_current_file
BEFORE UPDATE ON file_contents
WHEN NOT EXISTS (
    SELECT 1 FROM file_entries f JOIN index_roots r ON r.id = f.root_id
    WHERE f.id = NEW.file_id AND f.revision = NEW.file_revision
      AND f.entry_type = 'file' AND f.is_deleted = 0
      AND f.indexing_status = 'indexed' AND f.extraction_status = 'extracted'
      AND r.enabled = 1 AND r.status NOT IN ('offline', 'access_denied')
)
BEGIN
    SELECT RAISE(ABORT, 'content requires a current authorized file revision');
END;

CREATE VIRTUAL TABLE file_contents_fts USING fts5(
    extracted_text,
    content = 'file_contents',
    content_rowid = 'file_id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER file_contents_insert AFTER INSERT ON file_contents
BEGIN
    INSERT INTO file_contents_fts(rowid, extracted_text) VALUES (NEW.file_id, NEW.extracted_text);
END;

CREATE TRIGGER file_contents_delete AFTER DELETE ON file_contents
BEGIN
    INSERT INTO file_contents_fts(file_contents_fts, rowid, extracted_text)
    VALUES ('delete', OLD.file_id, OLD.extracted_text);
END;

CREATE TRIGGER file_contents_update AFTER UPDATE ON file_contents
BEGIN
    INSERT INTO file_contents_fts(file_contents_fts, rowid, extracted_text)
    VALUES ('delete', OLD.file_id, OLD.extracted_text);
    INSERT INTO file_contents_fts(rowid, extracted_text) VALUES (NEW.file_id, NEW.extracted_text);
END;

CREATE TRIGGER entry_content_invalidated
BEFORE UPDATE OF revision, is_deleted, indexing_status, extraction_status, entry_type ON file_entries
WHEN NEW.revision <> OLD.revision OR NEW.is_deleted = 1 OR NEW.entry_type <> 'file'
  OR NEW.indexing_status <> 'indexed' OR NEW.extraction_status <> 'extracted'
BEGIN
    DELETE FROM file_contents WHERE file_id = OLD.id;
END;

CREATE TABLE scan_runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    generation INTEGER NOT NULL CHECK (generation > 0),
    scan_type TEXT NOT NULL CHECK (scan_type IN ('initial', 'manual', 'reconciliation')),
    status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'incomplete', 'cancelled', 'failed')),
    started_at TEXT NOT NULL CHECK (started_at GLOB '????-??-??T??:??:??.???Z'),
    completed_at TEXT CHECK (completed_at GLOB '????-??-??T??:??:??.???Z'),
    files_discovered INTEGER NOT NULL DEFAULT 0 CHECK (files_discovered >= 0),
    files_processed INTEGER NOT NULL DEFAULT 0 CHECK (files_processed >= 0),
    files_failed INTEGER NOT NULL DEFAULT 0 CHECK (files_failed >= 0),
    last_relative_path TEXT,
    error_message TEXT,
    UNIQUE (root_id, generation),
    CHECK (
        (status = 'running' AND completed_at IS NULL)
        OR (status <> 'running' AND completed_at IS NOT NULL AND completed_at >= started_at)
    )
) STRICT;

CREATE TABLE index_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id INTEGER,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    job_type TEXT NOT NULL CHECK (job_type IN ('scan', 'hash', 'extract', 'reconcile', 'purge')),
    dedupe_key TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'completed', 'failed', 'cancelled')),
    priority INTEGER NOT NULL DEFAULT 50 CHECK (priority BETWEEN 0 AND 100),
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??.???Z'),
    started_at TEXT CHECK (started_at GLOB '????-??-??T??:??:??.???Z'),
    completed_at TEXT CHECK (completed_at GLOB '????-??-??T??:??:??.???Z'),
    next_attempt_at TEXT NOT NULL CHECK (next_attempt_at GLOB '????-??-??T??:??:??.???Z'),
    error_message TEXT,
    expected_file_revision INTEGER CHECK (expected_file_revision > 0),
    expected_policy_revision INTEGER NOT NULL CHECK (expected_policy_revision > 0),
    lease_owner TEXT,
    lease_until TEXT CHECK (lease_until GLOB '????-??-??T??:??:??.???Z'),
    FOREIGN KEY (root_id, file_id) REFERENCES file_entries(root_id, id) ON DELETE CASCADE,
    CHECK (
        (status = 'running' AND lease_owner IS NOT NULL AND lease_until IS NOT NULL AND started_at IS NOT NULL)
        OR (status <> 'running' AND lease_owner IS NULL AND lease_until IS NULL)
    ),
    CHECK (
        (job_type IN ('hash', 'extract') AND file_id IS NOT NULL AND expected_file_revision IS NOT NULL)
        OR job_type IN ('scan', 'reconcile', 'purge')
    )
) STRICT;

CREATE UNIQUE INDEX jobs_active_dedupe ON index_jobs(root_id, job_type, dedupe_key)
WHERE status IN ('queued', 'running');
CREATE INDEX jobs_ready ON index_jobs(status, next_attempt_at, priority);

CREATE TRIGGER root_content_disabled
AFTER UPDATE OF enabled, status ON index_roots
WHEN NEW.enabled = 0 OR NEW.status = 'access_denied'
BEGIN
    DELETE FROM file_contents WHERE file_id IN (SELECT id FROM file_entries WHERE root_id = NEW.id);
    UPDATE file_entries SET extraction_status = 'pending'
    WHERE root_id = NEW.id AND extraction_status = 'extracted';
    UPDATE index_jobs SET status = 'cancelled', completed_at = NEW.updated_at, lease_owner = NULL, lease_until = NULL
    WHERE root_id = NEW.id AND status IN ('queued', 'running');
END;

CREATE TABLE excluded_paths (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    relative_path TEXT NOT NULL,
    path_key TEXT NOT NULL,
    recursive INTEGER NOT NULL DEFAULT 1 CHECK (recursive IN (0, 1)),
    created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??.???Z'),
    UNIQUE (root_id, path_key)
) STRICT;

CREATE TABLE settings (
    name TEXT PRIMARY KEY NOT NULL,
    value_json TEXT NOT NULL CHECK (json_valid(value_json)),
    updated_at TEXT NOT NULL CHECK (updated_at GLOB '????-??-??T??:??:??.???Z')
) STRICT;

CREATE TABLE mcp_clients (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_id TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    capability_sha256 BLOB NOT NULL UNIQUE CHECK (length(capability_sha256) = 32),
    enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0, 1)),
    locality TEXT NOT NULL DEFAULT 'unknown' CHECK (locality IN ('local', 'cloud', 'unknown')),
    created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??.???Z'),
    last_seen_at TEXT CHECK (last_seen_at GLOB '????-??-??T??:??:??.???Z')
) STRICT;

CREATE TABLE client_root_grants (
    client_id INTEGER NOT NULL REFERENCES mcp_clients(id) ON DELETE CASCADE,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    allow_metadata INTEGER NOT NULL DEFAULT 1 CHECK (allow_metadata IN (0, 1)),
    allow_content INTEGER NOT NULL DEFAULT 0 CHECK (allow_content IN (0, 1)),
    granted_at TEXT NOT NULL CHECK (granted_at GLOB '????-??-??T??:??:??.???Z'),
    PRIMARY KEY (client_id, root_id),
    CHECK (allow_content <= allow_metadata)
) STRICT;

CREATE TABLE agent_sessions (
    id TEXT PRIMARY KEY NOT NULL,
    client_id INTEGER NOT NULL REFERENCES mcp_clients(id) ON DELETE CASCADE,
    owner_sid TEXT NOT NULL,
    started_at TEXT NOT NULL CHECK (started_at GLOB '????-??-??T??:??:??.???Z'),
    last_seen_at TEXT NOT NULL CHECK (last_seen_at GLOB '????-??-??T??:??:??.???Z'),
    ended_at TEXT CHECK (ended_at GLOB '????-??-??T??:??:??.???Z')
) STRICT;

CREATE TABLE audit_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    client_id INTEGER REFERENCES mcp_clients(id) ON DELETE SET NULL,
    session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL,
    root_id INTEGER REFERENCES index_roots(id) ON DELETE SET NULL,
    operation TEXT NOT NULL,
    outcome TEXT NOT NULL CHECK (outcome IN ('allowed', 'denied', 'failed')),
    error_code TEXT,
    correlation_id TEXT NOT NULL,
    created_at TEXT NOT NULL CHECK (created_at GLOB '????-??-??T??:??:??.???Z')
) STRICT;
CREATE INDEX audit_events_by_time ON audit_events(created_at);

-- Reserved for a future approval workflow. No initial API or worker executes these rows.
CREATE TABLE pending_operations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    requested_by_session TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL,
    root_id INTEGER NOT NULL REFERENCES index_roots(id) ON DELETE CASCADE,
    operation_type TEXT NOT NULL,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json) AND length(payload_json) <= 65536),
    status TEXT NOT NULL CHECK (status IN ('pending_approval', 'approved', 'executing', 'completed', 'rejected', 'expired', 'cancelled')),
    requested_at TEXT NOT NULL CHECK (requested_at GLOB '????-??-??T??:??:??.???Z'),
    expires_at TEXT NOT NULL CHECK (expires_at GLOB '????-??-??T??:??:??.???Z'),
    approved_at TEXT CHECK (approved_at GLOB '????-??-??T??:??:??.???Z'),
    approved_by_sid TEXT,
    CHECK (expires_at > requested_at),
    CHECK (
        status NOT IN ('approved', 'executing', 'completed')
        OR (approved_at IS NOT NULL AND approved_by_sid IS NOT NULL)
    )
) STRICT;

COMMIT;
