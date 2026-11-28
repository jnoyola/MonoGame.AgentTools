namespace Synthapixel.MonoGame.AgentTools.Hud;

/// <summary>
/// Follows a value up at once and down only after it has stayed lower for <see cref="HoldSeconds"/>, as
/// the peak hold of an audio level meter does. Keeps the HUD panel's edge still while text lengths change.
/// </summary>
internal struct PeakHold(double holdSeconds)
{
    private double _heldSince;

    public readonly double HoldSeconds => holdSeconds;
    public int Value { get; private set; }

    public int Update(int value, double seconds)
    {
        if (value >= Value || seconds - _heldSince >= holdSeconds)
        {
            Value = value;
            _heldSince = seconds;
        }
        return Value;
    }
}
