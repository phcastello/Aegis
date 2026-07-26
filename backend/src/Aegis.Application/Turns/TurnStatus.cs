namespace Aegis.Application.Turns;

public enum TurnStatus
{
    Created,
    GeneratingText,
    TextCompleted,
    RequestingSpeech,
    StreamingAudio,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}
