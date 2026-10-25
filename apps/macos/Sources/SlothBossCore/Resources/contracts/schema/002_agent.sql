-- SlothBoss workspace schema, migration 002: durable agent runs.
-- An agent run is a bounded, persisted plan-execute-verify workflow over explicitly approved sources.
-- Every state change, step attempt and file operation is written before and after it happens so an
-- interrupted run can be reconciled against the real filesystem on restart.

-- Folders the user has explicitly approved for agent access. Tools can never reach outside these.
CREATE TABLE approved_folders (
    id          TEXT PRIMARY KEY NOT NULL,
    path        TEXT NOT NULL UNIQUE,
    label       TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

CREATE TABLE agent_runs (
    id                     TEXT PRIMARY KEY NOT NULL,
    goal                   TEXT NOT NULL CHECK (length(trim(goal)) > 0),
    mode                   TEXT NOT NULL CHECK (mode IN ('assist', 'autopilot', 'supervised')),
    planner                TEXT NOT NULL,           -- e.g. 'gemma:gemma4:e2b-it-qat' or 'rules'
    folder_id              TEXT NULL REFERENCES approved_folders(id) ON DELETE SET NULL,
    folder_path            TEXT NULL,               -- snapshot of the approved root used by this run
    state                  TEXT NOT NULL CHECK (state IN (
                               'created', 'planning', 'awaiting_approval', 'ready', 'running', 'paused',
                               'needs_input', 'retrying', 'verifying', 'completed', 'partially_completed',
                               'failed', 'cancelled')),
    state_reason           TEXT NULL,
    plan_json              TEXT NULL,
    plan_version           INTEGER NOT NULL DEFAULT 0,
    plan_approved_version  INTEGER NOT NULL DEFAULT 0,
    budget_json            TEXT NOT NULL,
    usage_json             TEXT NOT NULL,
    artifacts_json         TEXT NOT NULL DEFAULT '{}',
    report_json            TEXT NULL,
    control                TEXT NULL CHECK (control IS NULL OR control IN ('pause', 'cancel')),
    undone_at              TEXT NULL,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    started_at             TEXT NULL,
    finished_at            TEXT NULL
);
CREATE INDEX idx_agent_runs_state ON agent_runs(state, updated_at);

CREATE TABLE agent_steps (
    id               TEXT PRIMARY KEY NOT NULL,
    run_id           TEXT NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    seq              INTEGER NOT NULL,
    plan_version     INTEGER NOT NULL,
    tool             TEXT NOT NULL,
    tool_version     INTEGER NOT NULL,
    args_json        TEXT NOT NULL,
    purpose          TEXT NOT NULL,
    risk             TEXT NOT NULL CHECK (risk IN ('read', 'create', 'modify')),
    approval         TEXT NOT NULL CHECK (approval IN ('not_required', 'required', 'approved', 'rejected')),
    status           TEXT NOT NULL CHECK (status IN (
                         'pending', 'awaiting_approval', 'running', 'succeeded', 'failed', 'skipped',
                         'rejected', 'uncertain', 'superseded')),
    attempts         INTEGER NOT NULL DEFAULT 0,
    -- run id + plan version + seq + tool: a retried step reuses its key, so side effects are not repeated.
    idempotency_key  TEXT NOT NULL UNIQUE,
    excluded_json    TEXT NULL,                     -- item indexes the user excluded when approving
    result_json      TEXT NULL,
    error            TEXT NULL,
    verification_json TEXT NULL,
    created_at       TEXT NOT NULL,
    started_at       TEXT NULL,
    finished_at      TEXT NULL,
    decided_at       TEXT NULL
);
CREATE INDEX idx_agent_steps_run ON agent_steps(run_id, plan_version, seq);

-- Individual filesystem side effects, journaled before execution (status 'running') and after.
CREATE TABLE agent_file_ops (
    id          TEXT PRIMARY KEY NOT NULL,
    run_id      TEXT NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    step_id     TEXT NULL REFERENCES agent_steps(id) ON DELETE SET NULL,
    seq         INTEGER NOT NULL,
    op          TEXT NOT NULL CHECK (op IN ('create_folder', 'move', 'create_file')),
    source_rel  TEXT NULL,
    target_rel  TEXT NOT NULL,
    sha256      TEXT NULL,
    size        INTEGER NULL,
    reason      TEXT NOT NULL DEFAULT '',
    status      TEXT NOT NULL CHECK (status IN (
                    'proposed', 'excluded', 'running', 'applied', 'skipped', 'failed', 'undone', 'undo_failed')),
    note        TEXT NULL,
    error       TEXT NULL,
    applied_at  TEXT NULL,
    undone_at   TEXT NULL
);
CREATE INDEX idx_agent_file_ops_run ON agent_file_ops(run_id, seq);

-- Human-readable activity log.
CREATE TABLE agent_events (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id    TEXT NOT NULL REFERENCES agent_runs(id) ON DELETE CASCADE,
    step_id   TEXT NULL,
    at        TEXT NOT NULL,
    level     TEXT NOT NULL CHECK (level IN ('info', 'warning', 'error')),
    kind      TEXT NOT NULL,
    message   TEXT NOT NULL,
    data_json TEXT NULL
);
CREATE INDEX idx_agent_events_run ON agent_events(run_id, id);
