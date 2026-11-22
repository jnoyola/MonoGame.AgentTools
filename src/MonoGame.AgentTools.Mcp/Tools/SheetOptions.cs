using System.ComponentModel;

namespace MonoGame.AgentTools.Mcp.Tools;

/// <summary>Which saved frames a contact sheet shows, and how.</summary>
internal sealed class SheetOptions
{
    public const string CountDescription =
        "Number of frames, picked evenly with the first and last included. Default 12, at most 48; when comparing, default 6, at most 12.";
    public const string ColumnsDescription = "Frames per row. Default 4. Ignored when comparing: one row per frame.";
    public const string BaselineDescription =
        "A capture to compare with (its name or folder), shown as A; this capture is B. Each row is the same frame "
        + "from both and the pixels that changed (red), with the percentage changed. Both captures need the same fps and size. "
        + "A HUD drawn with hud is left out (blue).";
    public const string DiffDescription = "With baseline: show the changed pixels beside A and B. Default true; false shows just A and B, larger.";

    [Description("Start of the time range in game seconds. Default: the first saved frame.")]
    public double? From { get; init; }

    [Description("End of the time range in game seconds, inclusive. Default: the last saved frame.")]
    public double? To { get; init; }

    [Description(CountDescription)]
    public int? Count { get; init; }

    [Description(ColumnsDescription)]
    public int Columns { get; init; } = 4;

    [Description("Region of each frame to show, 'x,y,width,height' in frame pixels.")]
    public string? Crop { get; init; }

    [Description(BaselineDescription)]
    public string? Baseline { get; init; }

    [Description(DiffDescription)]
    public bool Diff { get; init; } = true;
}
