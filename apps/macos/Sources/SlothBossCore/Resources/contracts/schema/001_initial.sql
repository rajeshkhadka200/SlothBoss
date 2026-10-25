-- SlothBoss workspace schema, migration 001.
-- Executed verbatim by both the Windows and macOS clients inside one transaction.
-- Conventions (see docs/DATA_MODEL.md):
--   * ids are lowercase UUID v4 strings
--   * timestamps are UTC ISO-8601 with milliseconds: 2026-10-24T09:30:00.000Z
--   * due dates are calendar dates: YYYY-MM-DD
--   * booleans are INTEGER 0/1
-- PRAGMA foreign_keys = ON must be set on every connection.

CREATE TABLE pages (
    id            TEXT PRIMARY KEY NOT NULL,
    parent_id     TEXT NULL REFERENCES pages(id) ON DELETE CASCADE,
    title         TEXT NOT NULL DEFAULT '',
    content_json  TEXT NOT NULL DEFAULT '{"version":1,"blocks":[]}',
    -- Plain-text projection of content_json, written by the client on every save.
    -- Used for search indexing and AI retrieval; never edited directly.
    content_text  TEXT NOT NULL DEFAULT '',
    sort_order    REAL NOT NULL DEFAULT 0,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    is_favorite   INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
    is_archived   INTEGER NOT NULL DEFAULT 0 CHECK (is_archived IN (0, 1)),
    CHECK (parent_id IS NULL OR parent_id <> id)
);
CREATE INDEX idx_pages_parent ON pages(parent_id, sort_order);
CREATE INDEX idx_pages_favorite ON pages(is_favorite) WHERE is_favorite = 1;

-- Cycle prevention: a page may not be re-parented under itself or any descendant.
CREATE TRIGGER pages_no_cycle_update
BEFORE UPDATE OF parent_id ON pages
WHEN NEW.parent_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'page hierarchy cycle')
    WHERE EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT NEW.parent_id
            UNION
            SELECT p.parent_id FROM pages p JOIN ancestors a ON p.id = a.id
            WHERE p.parent_id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = NEW.id
    );
END;

CREATE TABLE tasks (
    id            TEXT PRIMARY KEY NOT NULL,
    title         TEXT NOT NULL CHECK (length(trim(title)) > 0),
    description   TEXT NULL,
    status        TEXT NOT NULL DEFAULT 'todo' CHECK (status IN ('todo', 'in_progress', 'done')),
    priority      TEXT NOT NULL DEFAULT 'medium' CHECK (priority IN ('low', 'medium', 'high', 'urgent')),
    due_date      TEXT NULL CHECK (due_date IS NULL OR due_date GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    -- Deleting a page keeps its tasks but unlinks them.
    page_id       TEXT NULL REFERENCES pages(id) ON DELETE SET NULL,
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    completed_at  TEXT NULL,
    CHECK ((status = 'done') = (completed_at IS NOT NULL))
);
CREATE INDEX idx_tasks_page ON tasks(page_id);
CREATE INDEX idx_tasks_status ON tasks(status, due_date);

-- Page-to-page references, derived by the client from links in content_json
-- (slothboss://page/<id>) on every save.
CREATE TABLE page_links (
    source_page_id TEXT NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
    target_page_id TEXT NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
    PRIMARY KEY (source_page_id, target_page_id)
);
CREATE INDEX idx_page_links_target ON page_links(target_page_id);

CREATE TABLE settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);

-- Audit log of every AI-proposed workspace mutation (contracts/schemas/action.schema.json).
CREATE TABLE ai_actions (
    id              TEXT PRIMARY KEY NOT NULL,
    batch_id        TEXT NOT NULL,
    action_type     TEXT NOT NULL CHECK (action_type IN
                        ('create_task', 'create_page', 'draft_page_content', 'add_page_reference', 'update_task')),
    payload_json    TEXT NOT NULL,
    summary         TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'proposed' CHECK (status IN
                        ('proposed', 'rejected', 'applied', 'failed', 'undone')),
    result_json     TEXT NULL,
    undo_json       TEXT NULL,
    error           TEXT NULL,
    model           TEXT NULL,
    created_at      TEXT NOT NULL,
    decided_at      TEXT NULL,
    undone_at       TEXT NULL
);
CREATE INDEX idx_ai_actions_created ON ai_actions(created_at);

-- Full-text index over non-archived pages and all tasks. Maintained entirely by triggers
-- so both clients get identical indexing behavior.
CREATE VIRTUAL TABLE search_index USING fts5(
    kind UNINDEXED,
    ref_id UNINDEXED,
    title,
    body,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER pages_ai AFTER INSERT ON pages WHEN NEW.is_archived = 0 BEGIN
    INSERT INTO search_index(kind, ref_id, title, body) VALUES ('page', NEW.id, NEW.title, NEW.content_text);
END;
CREATE TRIGGER pages_au AFTER UPDATE OF title, content_text, is_archived ON pages BEGIN
    DELETE FROM search_index WHERE kind = 'page' AND ref_id = OLD.id;
    INSERT INTO search_index(kind, ref_id, title, body)
        SELECT 'page', NEW.id, NEW.title, NEW.content_text WHERE NEW.is_archived = 0;
END;
CREATE TRIGGER pages_ad AFTER DELETE ON pages BEGIN
    DELETE FROM search_index WHERE kind = 'page' AND ref_id = OLD.id;
END;

CREATE TRIGGER tasks_ai AFTER INSERT ON tasks BEGIN
    INSERT INTO search_index(kind, ref_id, title, body) VALUES ('task', NEW.id, NEW.title, coalesce(NEW.description, ''));
END;
CREATE TRIGGER tasks_au AFTER UPDATE OF title, description ON tasks BEGIN
    DELETE FROM search_index WHERE kind = 'task' AND ref_id = OLD.id;
    INSERT INTO search_index(kind, ref_id, title, body) VALUES ('task', NEW.id, NEW.title, coalesce(NEW.description, ''));
END;
CREATE TRIGGER tasks_ad AFTER DELETE ON tasks BEGIN
    DELETE FROM search_index WHERE kind = 'task' AND ref_id = OLD.id;
END;
