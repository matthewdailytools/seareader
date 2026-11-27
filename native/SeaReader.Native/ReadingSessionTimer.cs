namespace SeaReader.Native;

internal sealed class ReadingSessionTimer(TimeProvider clock)
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SampleLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    private readonly Dictionary<DateOnly, (long Ticks, int TargetMinutes)> pending = [];
    private bool counting;
    private long lastSample;
    private long lastInteraction;
    private DateTimeOffset lastUtc;
    private int lastTarget;

    internal void Sample(bool shouldCount, int targetMinutes) =>
        SampleAt(clock.GetTimestamp(), clock.GetUtcNow(), shouldCount, targetMinutes);

    internal void MarkInteraction()
    {
        if (!counting) return;
        var now = clock.GetTimestamp();
        if (clock.GetElapsedTime(lastInteraction, now) >= IdleLimit)
            SampleAt(now, clock.GetUtcNow(), true, lastTarget);
        lastInteraction = now;
    }

    internal ReadingGoalDay[] TakePending()
    {
        var batch = new List<ReadingGoalDay>(pending.Count);
        foreach (var (day, time) in pending.ToArray())
        {
            var milliseconds = time.Ticks / TimeSpan.TicksPerMillisecond;
            if (milliseconds == 0) continue;
            batch.Add(new ReadingGoalDay(day, milliseconds, time.TargetMinutes));
            var remaining = time.Ticks % TimeSpan.TicksPerMillisecond;
            if (remaining == 0) pending.Remove(day);
            else pending[day] = (remaining, time.TargetMinutes);
        }
        return batch.ToArray();
    }

    internal void ReturnPending(IReadOnlyList<ReadingGoalDay> values)
    {
        foreach (var value in values)
        {
            var current = pending.GetValueOrDefault(value.Day);
            pending[value.Day] = (current.Ticks + value.Milliseconds * TimeSpan.TicksPerMillisecond,
                current.Ticks == 0 ? value.TargetMinutes : current.TargetMinutes);
        }
    }

    private void SampleAt(long timestamp, DateTimeOffset utc, bool shouldCount, int targetMinutes)
    {
        if (counting)
        {
            var elapsed = clock.GetElapsedTime(lastSample, timestamp);
            var idleTimeLeft = IdleLimit - clock.GetElapsedTime(lastInteraction, lastSample);
            // A long gap or wall-clock adjustment cannot tell us how much of the gap was spent reading.
            if (elapsed > TimeSpan.Zero && elapsed <= SampleLimit && idleTimeLeft > TimeSpan.Zero &&
                (utc - lastUtc - elapsed).Duration() <= ClockTolerance)
                AddInterval(lastUtc, elapsed <= idleTimeLeft ? elapsed : idleTimeLeft, lastTarget);
        }
        else if (shouldCount) lastInteraction = timestamp;

        counting = shouldCount;
        lastSample = timestamp;
        lastUtc = utc;
        lastTarget = targetMinutes;
    }

    private void AddInterval(DateTimeOffset start, TimeSpan elapsed, int targetMinutes)
    {
        var firstDay = LocalDay(start);
        var lastDay = LocalDay(start + elapsed);
        if (firstDay == lastDay)
        {
            Add(firstDay, elapsed.Ticks, targetMinutes);
            return;
        }

        // Search UTC instants, so dates on either side of a time-zone offset change use elapsed time.
        long left = 0, right = elapsed.Ticks;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (LocalDay(start.AddTicks(middle)) == firstDay) left = middle + 1;
            else right = middle;
        }
        Add(firstDay, left, targetMinutes);
        Add(lastDay, elapsed.Ticks - left, targetMinutes);
    }

    private DateOnly LocalDay(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, clock.LocalTimeZone).DateTime);

    private void Add(DateOnly day, long ticks, int targetMinutes)
    {
        if (ticks > 0) pending[day] = (pending.GetValueOrDefault(day).Ticks + ticks, targetMinutes);
    }
}
