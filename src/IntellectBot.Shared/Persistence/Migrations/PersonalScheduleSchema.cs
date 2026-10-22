namespace IntellectBot.Data;

// Личные занятия: даты, время и принадлежность пользователю.
internal static class PersonalScheduleSchema
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS schedule_lessons (
            id UUID PRIMARY KEY,
            owner_chat_id BIGINT NOT NULL REFERENCES bot_users(chat_id) ON DELETE CASCADE,
            lesson_date DATE NOT NULL,
            start_time TIME NOT NULL,
            end_time TIME NOT NULL,
            title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
            location TEXT NOT NULL CHECK (length(location) <= 200),
            CHECK (end_time > start_time),
            UNIQUE(owner_chat_id, lesson_date, start_time, end_time, title, location)
        );
        CREATE INDEX IF NOT EXISTS schedule_owner_date_idx ON schedule_lessons(owner_chat_id,lesson_date,start_time);
        """;
}
