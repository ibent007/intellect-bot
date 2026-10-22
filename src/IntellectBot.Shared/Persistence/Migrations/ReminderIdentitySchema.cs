namespace IntellectBot.Data;

// Старые дубликаты сохраняются. Каноническая запись получает ключ для защиты новых вставок.
internal static class ReminderIdentitySchema
{
    public const string Sql = """
        ALTER TABLE agent_reminders ADD COLUMN IF NOT EXISTS identity_text TEXT;
        WITH canonical AS (
            SELECT DISTINCT ON (owner_chat_id, remind_at,
                lower(regexp_replace(btrim(text), '\s+', ' ', 'g')))
                id
            FROM agent_reminders
            ORDER BY owner_chat_id, remind_at,
                lower(regexp_replace(btrim(text), '\s+', ' ', 'g')),
                created_at, id
        )
        UPDATE agent_reminders
        SET identity_text=lower(regexp_replace(btrim(text), '\s+', ' ', 'g'))
        WHERE id IN (SELECT id FROM canonical);

        CREATE UNIQUE INDEX IF NOT EXISTS agent_reminders_identity_idx
            ON agent_reminders(owner_chat_id, remind_at, identity_text)
            WHERE identity_text IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS agent_reminders_owner_id_idx
            ON agent_reminders(owner_chat_id, id);
        ALTER TABLE agent_reminders ADD CONSTRAINT agent_reminders_identity_matches
            CHECK (identity_text IS NULL OR
                identity_text=lower(regexp_replace(btrim(text), '\s+', ' ', 'g')));

        ALTER TABLE agent_actions ADD COLUMN IF NOT EXISTS result_reminder_id UUID;
        UPDATE agent_actions AS action SET result_reminder_id=reminder.id
        FROM agent_reminders AS reminder
        WHERE reminder.owner_chat_id=action.owner_chat_id
            AND reminder.idempotency_key=action.id AND action.status='executed';
        ALTER TABLE agent_actions ADD CONSTRAINT agent_actions_result_owner_fk
            FOREIGN KEY (owner_chat_id, result_reminder_id)
            REFERENCES agent_reminders(owner_chat_id, id)
            DEFERRABLE INITIALLY DEFERRED;
        """;
}
