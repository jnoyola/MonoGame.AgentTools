namespace MonoGame.AgentTools.Mcp;

/// <summary>Always loaded by the client, so it stays a few lines; the guide tool holds the rest.</summary>
internal static class ServerInstructions
{
    public const string Text = """
        Runs the user's MonoGame game so you can see what it draws and how it performs.
        - capture: builds and runs the game at a fixed step with a hidden window; returns a brief reading of summary.json (detail 'full' for the JSON) and a labeled contact sheet (pass sheet to choose the time range, frames and crop). Same build and arguments, same frames.
        - contact_sheet: another sheet from an existing capture (a closer time range or crop), without re-running.
        - Before and after a change: name a baseline capture, then pass sheet.against (or contact_sheet against) to see what changed, frame by frame.
        - gif: an animated GIF of a capture (or A beside B) for the user to watch; you get only its path. Offer one when the user should judge motion.
        - profile: runs the game paced in real time with no frames saved, 8 s by default; use it, not capture, for performance numbers.
        - input: capture and profile can press keys, click and move gamepad sticks ("f30 Space down"), or replay a session the user played (each is recorded to .agenttools/sessions); see guide topic input.
        - Check visual changes in motion, not from one frame.
        - guide: setup, options and how to read the measurements.
        """;
}
