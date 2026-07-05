using System.Globalization;
using System.Text;

namespace Aegis.Application.Chat;

public sealed record ConversationCursor(DateTimeOffset UpdatedAt, Guid Id)
{
    public string Encode()
    {
        var raw = $"ticks|{UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{Id:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? value, out ConversationCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var parts = raw.Split('|');
            if (parts.Length == 3 &&
                string.Equals(parts[0], "ticks", StringComparison.Ordinal) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                Guid.TryParse(parts[2], out var ticksId))
            {
                cursor = new ConversationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), ticksId);
                return true;
            }

            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ||
                !Guid.TryParse(parts[1], out var id))
            {
                return false;
            }

            // Legacy cursors from v0.1.4-dev encoded milliseconds. New cursors use ticks
            // so Postgres microsecond precision cannot skip records inside the same millisecond.
            cursor = new ConversationCursor(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
