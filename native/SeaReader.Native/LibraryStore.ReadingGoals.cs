using System.Text.Json;

namespace SeaReader.Native;

internal sealed record ReadingGoalsSettings(bool Enabled = false, int DailyMinutes = 5, int AnnualBooks = 3, bool IncludePdf = false);
internal readonly record struct ReadingGoalDay(DateOnly Day, long Milliseconds, int TargetMinutes);
internal sealed record ReadingGoalsSnapshot(ReadingGoalsSettings Settings, DateOnly Today, IReadOnlyList<ReadingGoalDay> Week,
    int Streak, int BestStreak, int BooksFinished);

public sealed partial class LibraryStore
{
    private const string ReadingGoalsPreference = "reading-goals";
    private static readonly JsonSerializerOptions ReadingGoalsJson = new(JsonSerializerDefaults.Web);

    private static void InitializeReadingGoalsTables(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_reading_goal_days (
            day_number INTEGER PRIMARY KEY,
            milliseconds INTEGER NOT NULL CHECK (typeof(milliseconds) = 'integer' AND milliseconds >= 0),
            target_minutes INTEGER NOT NULL CHECK (target_minutes BETWEEN 1 AND 1440));
        CREATE TABLE IF NOT EXISTS native_reading_goal_finishes (
            book_id TEXT PRIMARY KEY NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            day_number INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS native_reading_goal_finishes_by_day ON native_reading_goal_finishes(day_number);
        """);

    internal Task<ReadingGoalsSettings> LoadReadingGoalsSettingsAsync() => Query(ReadReadingGoalsSettings);

    internal Task SaveReadingGoalsSettingsAsync(ReadingGoalsSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateDailyMinutes(settings.DailyMinutes);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AnnualBooks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.AnnualBooks, 9999);
        string json = JsonSerializer.Serialize(settings, ReadingGoalsJson);
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            db.Execute("""
                INSERT INTO application_preferences(key, value) VALUES (?, ?)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """, ReadingGoalsPreference, json);
            // Persist the new target even before the first interval. A delayed write may still carry the old target.
            db.Execute("""
                INSERT INTO native_reading_goal_days(day_number, milliseconds, target_minutes) VALUES (?, 0, ?)
                ON CONFLICT(day_number) DO UPDATE SET target_minutes=excluded.target_minutes
                """, (long)today.DayNumber, (long)settings.DailyMinutes);
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task AddReadingGoalTimeAsync(IReadOnlyList<ReadingGoalDay> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        var batch = durations.ToArray();
        foreach (var day in batch)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(day.Milliseconds);
            ValidateDailyMinutes(day.TargetMinutes);
        }
        if (batch.Length == 0) return Task.CompletedTask;
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            foreach (var day in batch)
            {
                if (day.Milliseconds == 0) continue;
                db.Execute("""
                    INSERT INTO native_reading_goal_days(day_number, milliseconds, target_minutes) VALUES (?, ?, ?)
                    ON CONFLICT(day_number) DO UPDATE SET milliseconds=milliseconds+excluded.milliseconds
                    """, (long)day.Day.DayNumber, day.Milliseconds, (long)day.TargetMinutes);
            }
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task<ReadingGoalsSnapshot> GetReadingGoalsSnapshotAsync(DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(today.DayNumber, 6);
        return Query(db =>
        {
            db.ExecuteScript("BEGIN;");
            var settings = ReadReadingGoalsSettings(db);
            int lastDay = today.DayNumber;
            var days = db.Read("""
                SELECT day_number, milliseconds, target_minutes FROM native_reading_goal_days
                WHERE day_number <= ? ORDER BY day_number
                """, row => new ReadingGoalDay(DateOnly.FromDayNumber((int)row.Integer(0)), row.Integer(1), (int)row.Integer(2)),
                (long)lastDay);
            var firstOfYear = new DateOnly(today.Year, 1, 1);
            var finished = db.Read("SELECT count(*) FROM native_reading_goal_finishes WHERE day_number BETWEEN ? AND ?",
                row => checked((int)row.Integer(0)), (long)firstOfYear.DayNumber, (long)lastDay).Single();
            db.ExecuteScript("COMMIT;");

            var firstWeekDay = lastDay - 6;
            var week = Enumerable.Range(firstWeekDay, 7)
                .Select(number => new ReadingGoalDay(DateOnly.FromDayNumber(number), 0, settings.DailyMinutes)).ToArray();
            int current = 0, best = 0, lastReached = -2;
            foreach (var recorded in days)
            {
                int number = recorded.Day.DayNumber;
                var day = number == lastDay ? recorded with { TargetMinutes = settings.DailyMinutes } : recorded;
                if (number >= firstWeekDay) week[number - firstWeekDay] = day;
                if (day.Milliseconds < day.TargetMinutes * 60_000L) continue;
                current = number == lastReached + 1 ? current + 1 : 1;
                best = Math.Max(best, current);
                lastReached = number;
            }
            if (lastDay - lastReached > 1) current = 0;
            return new ReadingGoalsSnapshot(settings, today, week, current, best, finished);
        });
    }

    private static ReadingGoalsSettings ReadReadingGoalsSettings(LibraryDatabase db)
    {
        var json = db.Read("SELECT value FROM application_preferences WHERE key=?", row => row.Text(0), ReadingGoalsPreference)
            .FirstOrDefault();
        if (json != null)
        {
            try
            {
                if (JsonSerializer.Deserialize<ReadingGoalsSettings>(json, ReadingGoalsJson) is
                    { DailyMinutes: >= 1 and <= 1440, AnnualBooks: >= 1 and <= 9999 } saved) return saved;
            }
            catch (JsonException) { }
        }
        return new ReadingGoalsSettings();
    }

    private static void ValidateDailyMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 1440);
    }

    private static void RecordReadingGoalFinish(LibraryDatabase db, string bookId, LibraryReadingState state, DateOnly today)
    {
        if (state != LibraryReadingState.Finished)
        {
            db.Execute("DELETE FROM native_reading_goal_finishes WHERE book_id=?", bookId);
            return;
        }

        // Capture only an explicit transition into Finished. Existing Finished books have no known completion date.
        db.Execute("""
            INSERT INTO native_reading_goal_finishes(book_id, day_number)
            SELECT ?, ?
            WHERE NOT EXISTS (SELECT 1 FROM native_library_reading_states WHERE book_id=? AND state=2)
            ON CONFLICT(book_id) DO UPDATE SET day_number=excluded.day_number
            """, bookId, (long)today.DayNumber, bookId);
    }
}
