namespace IntellectBot.Data;

// Миграция прежних режимов в единое общение, без очистки истории.
internal static class UnifiedChatSchema
{
    internal const string Sql = """
        ALTER TABLE bot_users DROP CONSTRAINT IF EXISTS bot_users_mode_check;
        ALTER TABLE bot_users ADD CONSTRAINT bot_users_mode_check
            CHECK (mode IN ('chat','study','translate','review'));
        ALTER TABLE bot_users ALTER COLUMN mode SET DEFAULT 'chat';
        UPDATE bot_users SET mode='chat';
        """;
}
