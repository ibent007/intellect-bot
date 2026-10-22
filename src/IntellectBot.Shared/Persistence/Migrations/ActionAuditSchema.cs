namespace IntellectBot.Data;

// Аудит различает работу модели, настройки и личное расписание.
internal static class ActionAuditSchema
{
    internal const string Sql = """
        ALTER TABLE agent_audits DROP CONSTRAINT IF EXISTS agent_audits_action_check;
        ALTER TABLE agent_audits ADD CONSTRAINT agent_audits_action_check
            CHECK (action IN ('respond','clarify','call_tool','settings','reset','schedule_read','schedule_write'));
        """;
}
