DROP TRIGGER root_content_disabled;

CREATE TRIGGER root_content_disabled
AFTER UPDATE OF enabled, status ON index_roots
WHEN NEW.enabled = 0 OR NEW.status IN ('offline', 'access_denied')
BEGIN
    DELETE FROM file_contents WHERE file_id IN (SELECT id FROM file_entries WHERE root_id = NEW.id);
    UPDATE file_entries SET extraction_status = 'pending'
    WHERE root_id = NEW.id AND extraction_status = 'extracted';
    UPDATE index_jobs SET status = 'cancelled', completed_at = NEW.updated_at, lease_owner = NULL, lease_until = NULL
    WHERE root_id = NEW.id AND status IN ('queued', 'running');
END;

-- Apply the new invalidation rule to roots that went offline before this upgrade.
UPDATE index_roots SET status = 'offline', index_revision = index_revision + 1 WHERE status = 'offline';
