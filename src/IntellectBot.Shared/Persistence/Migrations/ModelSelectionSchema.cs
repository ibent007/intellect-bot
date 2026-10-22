namespace IntellectBot.Data;

// До собственного выбора пользователя действует LLM_MODEL из конфигурации.
internal static class ModelSelectionSchema
{
    internal const string Sql = """
        ALTER TABLE bot_users ADD COLUMN IF NOT EXISTS model_id TEXT
            CHECK (model_id IS NULL OR char_length(model_id) BETWEEN 1 AND 128);
        """;
}
