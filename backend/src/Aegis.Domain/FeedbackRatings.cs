namespace Aegis.Domain;

public static class FeedbackRatings
{
    public const string Good = "good";
    public const string Bad = "bad";

    public static bool IsKnown(string rating)
    {
        return rating is Good or Bad;
    }
}
