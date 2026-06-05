namespace Aegis.Domain;

public static class FeedbackReasons
{
    public const string GoodTone = "good_tone";
    public const string Useful = "useful";
    public const string Clear = "clear";
    public const string Concrete = "concrete";
    public const string GoodCriticism = "good_criticism";
    public const string RespectedConstraint = "respected_constraint";
    public const string BadTone = "bad_tone";
    public const string NotUseful = "not_useful";
    public const string TooVerbose = "too_verbose";
    public const string TooGeneric = "too_generic";
    public const string IgnoredConstraint = "ignored_constraint";
    public const string HallucinatedCapability = "hallucinated_capability";
    public const string RepeatedTopic = "repeated_topic";
    public const string DidNotAnswer = "did_not_answer";
    public const string WrongContext = "wrong_context";
    public const string Other = "other";

    public static bool IsKnown(string reason)
    {
        return IsGoodReason(reason) || IsBadReason(reason);
    }

    public static bool IsGoodReason(string reason)
    {
        return reason is
            GoodTone or
            Useful or
            Clear or
            Concrete or
            GoodCriticism or
            RespectedConstraint or
            Other;
    }

    public static bool IsBadReason(string reason)
    {
        return reason is
            BadTone or
            NotUseful or
            TooVerbose or
            TooGeneric or
            IgnoredConstraint or
            HallucinatedCapability or
            RepeatedTopic or
            DidNotAnswer or
            WrongContext or
            Other;
    }

    public static bool IsKnownForRating(string rating, string reason)
    {
        return rating switch
        {
            FeedbackRatings.Good => IsGoodReason(reason),
            FeedbackRatings.Bad => IsBadReason(reason),
            _ => false
        };
    }
}
