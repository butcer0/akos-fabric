CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE repository_session (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    repository_profile    text NOT NULL,
    profile_revision_sha  text NOT NULL,
    source_control_provider text NOT NULL,
    image_reference       text NOT NULL,
    image_digest          text NOT NULL,
    status                text NOT NULL CHECK (status IN (
        'created',
        'published',
        'starting',
        'running',
        'processing_results',
        'completed',
        'failed',
        'cancelled'
    )),
    message_id            uuid NOT NULL UNIQUE,
    container_name        text UNIQUE,
    container_id          text,
    request_payload       jsonb NOT NULL,
    requested_by_subject  text NOT NULL,
    requested_by_client_id text NOT NULL,
    requested_by_token_id text,
    trace_id              text,
    created_at            timestamptz NOT NULL DEFAULT now(),
    published_at          timestamptz,
    started_at            timestamptz,
    completed_at          timestamptz,
    failure_code          text,
    failure_message       text
);

CREATE TABLE work_item_run (
    id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    repository_session_id uuid NOT NULL REFERENCES repository_session(id),
    sequence_number       integer NOT NULL,
    jira_issue_id         text NOT NULL,
    jira_key              text NOT NULL,
    jira_updated_at       timestamptz NOT NULL,
    jira_snapshot         jsonb NOT NULL,
    status                text NOT NULL CHECK (status IN (
        'queued',
        'planning',
        'coding',
        'verifying',
        'judging',
        'revising',
        'accepted',
        'branch_pushed',
        'change_request_created',
        'blocked',
        'failed'
    )),
    base_commit_sha       text,
    branch_name           text,
    candidate_commit_sha  text,
    change_request_id     text,
    change_request_number text,
    change_request_url    text,
    plan_json             jsonb,
    candidate_json        jsonb,
    verification_json     jsonb,
    judgment_json         jsonb,
    model_usage_json      jsonb,
    started_at            timestamptz,
    completed_at          timestamptz,
    failure_code          text,
    failure_message       text,
    UNIQUE (repository_session_id, jira_key),
    UNIQUE (repository_session_id, sequence_number)
);

CREATE UNIQUE INDEX ux_work_item_active_jira_key
ON work_item_run(jira_key)
WHERE status IN (
    'queued',
    'planning',
    'coding',
    'verifying',
    'judging',
    'revising',
    'accepted',
    'branch_pushed'
);

CREATE TABLE ledger_entry (
    id                    bigserial PRIMARY KEY,
    repository_session_id uuid NOT NULL REFERENCES repository_session(id),
    work_item_run_id      uuid REFERENCES work_item_run(id),
    entry_type            text NOT NULL,
    payload               jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at           timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_ledger_entry_session_time
ON ledger_entry(repository_session_id, occurred_at);

CREATE INDEX ix_work_item_run_jira_key
ON work_item_run(jira_key);
