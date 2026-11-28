DROP INDEX entries_by_parent;

CREATE INDEX entries_by_parent_path_key
ON file_entries(root_id, parent_path_key, is_deleted, path_key);
