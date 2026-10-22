namespace IntellectBot.Data;

internal static class AutomationSchema
{
    public const string Sql = """
        ALTER TABLE bot_users
            ADD COLUMN IF NOT EXISTS time_zone TEXT
            CHECK (
                time_zone IS NULL OR
                char_length(time_zone) BETWEEN 1 AND 100
            );

        CREATE TABLE IF NOT EXISTS agent_actions (
            id UUID PRIMARY KEY,
            owner_chat_id BIGINT NOT NULL
                REFERENCES bot_users(chat_id) ON DELETE CASCADE,
            telegram_update_id BIGINT NOT NULL CHECK (telegram_update_id >= 0),
            proposal_index INTEGER NOT NULL DEFAULT 0
                CHECK (proposal_index BETWEEN 0 AND 1),
            kind TEXT NOT NULL DEFAULT 'add_reminder'
                CHECK (kind = 'add_reminder'),
            text TEXT NOT NULL
                CHECK (
                    char_length(text) BETWEEN 1 AND 500 AND
                    btrim(text) <> ''
                ),
            remind_at TIMESTAMPTZ NOT NULL,
            time_zone TEXT NOT NULL
                CHECK (
                    char_length(time_zone) BETWEEN 1 AND 100 AND
                    btrim(time_zone) <> ''
                ),
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK (
                    status IN (
                        'pending', 'confirmed', 'cancelled',
                        'expired', 'executed'
                    )
                ),
            created_at TIMESTAMPTZ NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL,
            confirmed_at TIMESTAMPTZ,
            executed_at TIMESTAMPTZ,

            UNIQUE (owner_chat_id, id),
            UNIQUE (owner_chat_id, telegram_update_id, proposal_index),

            CHECK (remind_at > created_at),
            CHECK (
                expires_at > created_at AND
                expires_at <= created_at + INTERVAL '5 minutes'
            ),
            CHECK (
                (status IN ('confirmed', 'executed')) =
                (confirmed_at IS NOT NULL)
            ),
            CHECK (
                confirmed_at IS NULL OR
                (
                    confirmed_at >= created_at AND
                    confirmed_at < expires_at
                )
            ),
            CHECK (
                (status = 'executed') = (executed_at IS NOT NULL)
            ),
            CHECK (
                executed_at IS NULL OR
                executed_at >= confirmed_at
            )
        );

        CREATE INDEX IF NOT EXISTS agent_actions_owner_status_idx
            ON agent_actions(owner_chat_id, status, expires_at);

        CREATE TABLE IF NOT EXISTS agent_reminders (
            id UUID PRIMARY KEY,
            owner_chat_id BIGINT NOT NULL
                REFERENCES bot_users(chat_id) ON DELETE CASCADE,
            idempotency_key UUID NOT NULL,
            text TEXT NOT NULL
                CHECK (
                    char_length(text) BETWEEN 1 AND 500 AND
                    btrim(text) <> ''
                ),
            remind_at TIMESTAMPTZ NOT NULL,
            time_zone TEXT NOT NULL
                CHECK (
                    char_length(time_zone) BETWEEN 1 AND 100 AND
                    btrim(time_zone) <> ''
                ),
            created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

            UNIQUE (owner_chat_id, idempotency_key),
            FOREIGN KEY (owner_chat_id, idempotency_key)
                REFERENCES agent_actions(owner_chat_id, id)
                ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS agent_reminders_owner_time_idx
            ON agent_reminders(owner_chat_id, remind_at);

        CREATE TABLE IF NOT EXISTS agent_audits (
            id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            owner_chat_id BIGINT NOT NULL
                REFERENCES bot_users(chat_id) ON DELETE CASCADE,
            request_id UUID NOT NULL
                CHECK (
                    request_id <>
                    '00000000-0000-0000-0000-000000000000'::uuid
                ),
            created_at TIMESTAMPTZ NOT NULL,
            action TEXT NOT NULL
                CHECK (action IN ('respond', 'clarify', 'call_tool')),
            tools JSONB NOT NULL,
            reason TEXT NOT NULL
                CHECK (
                    char_length(reason) BETWEEN 1 AND 300 AND
                    btrim(reason) <> ''
                ),

            UNIQUE (owner_chat_id, request_id),
            CHECK (
                CASE
                    WHEN jsonb_typeof(tools) = 'array'
                    THEN jsonb_array_length(tools) <= 2
                    ELSE FALSE
                END
            )
        );

        CREATE INDEX IF NOT EXISTS agent_audits_owner_created_idx
            ON agent_audits(owner_chat_id, created_at DESC, id DESC);
        """;
}
