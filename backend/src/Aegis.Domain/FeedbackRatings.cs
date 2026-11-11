// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
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
