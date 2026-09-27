namespace Aegis.Infrastructure.Calendar;

public sealed class GoogleCalendarOptions
{
    public const string SectionName = "GoogleCalendar";

    public int[] TimedEventDefaultReminders { get; set; } = [4320, 1440, 240, 60, 0];

    // Relative to midnight in the Aegis reference timezone: D-3 14:00, D-1 14:00, D-1 20:00.
    public int[] AllDayDefaultReminders { get; set; } = [3480, 600, 240];

    public static bool ValidMinutes(int[]? values) => values is { Length: <= 5 } &&
        values.All(minutes => minutes is >= 0 and <= 40320) && values.Distinct().Count() == values.Length;
}
