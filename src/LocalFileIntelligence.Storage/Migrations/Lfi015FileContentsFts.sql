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

CREATE TRIGGER root_content_disabled
AFTER UPDATE OF enabled, status ON index_roots
WHEN NEW.enabled = 0 OR NEW.status IN ('offline', 'access_denied')
BEGIN
    DELETE FROM file_contents WHERE file_id IN (SELECT id FROM file_entries WHERE root_id = NEW.id);
    UPDATE file_entries SET extraction_status = 'pending'
    WHERE root_id = NEW.id AND extraction_status = 'extracted';
END;
