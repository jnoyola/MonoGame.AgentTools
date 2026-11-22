extern alias Library;

using System.Text.Json;
using Library::MonoGame.AgentTools.Diagnostics;
using MonoGame.AgentTools.Hud;
using MonoGame.AgentTools.Mcp.Games;
using MonoGame.AgentTools.Mcp.Imaging;
using MonoGame.AgentTools.Mcp.Tools;

namespace MonoGame.AgentTools.Mcp.Tests;

public class ServerTests
{
    [Theory]
    [InlineData("""<OutputType>WinExe</OutputType><PackageReference Include="MonoGame.AgentTools" Version="0.1.0" />""", true)]
    [InlineData("""<OutputType>Exe</OutputType><ProjectReference Include="..\src\MonoGame.AgentTools\MonoGame.AgentTools.csproj" />""", true)]
    [InlineData("""<OutputType>Exe</OutputType><ProjectReference Include="..\MonoGame.AgentTools.Mcp\MonoGame.AgentTools.Mcp.csproj" />""", false)]
    [InlineData("""<PackageReference Include="MonoGame.AgentTools" />""", false)]
    [InlineData("""<OutputType>Exe</OutputType><IsTestProject>true</IsTestProject><PackageReference Include="MonoGame.AgentTools" />""", false)]
    public void Games_are_executables_that_reference_the_library(string project, bool expected) =>
        Assert.Equal(expected, GameProject.IsGame(project));

    [Fact]
    public void Run_command_is_read_after_build_warnings()
    {
        // MSBuild reports native paths, so the test uses this platform's.
        var bin = Path.Combine(Path.GetTempPath(), "game", "bin", "Debug", "net10.0");
        var output = $$"""
            Game.cs(1,1): warning CS0169: The field 'x' is never used
            {
              "Properties": {
                "TargetPath": {{JsonSerializer.Serialize(Path.Combine(bin, "Game.dll"))}},
                "RunCommand": {{JsonSerializer.Serialize(Path.Combine(bin, "Game.exe"))}},
                "RunArguments": "",
                "RunWorkingDirectory": "",
                "AgentToolsEnabled": "true"
              }
            }
            """;

        var run = GameProject.ParseRunCommand(output);
        var disabled = GameProject.ParseRunCommand(output.Replace("\"AgentToolsEnabled\": \"true\"", "\"AgentToolsEnabled\": \"\"", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(bin, "Game.exe"), run.FileName);
        Assert.Equal(bin, run.WorkingDirectory);
        Assert.True(run.ToolsEnabled);
        Assert.False(disabled.ToolsEnabled);
    }

    [Theory]
    [InlineData("", "net8.0;net11.0", null, "net8.0")]
    [InlineData("", "net8.0;net11.0", "NET11.0", "NET11.0")]
    [InlineData("", " net10.0 ", null, "net10.0")]
    [InlineData("net10.0", "", null, null)]
    [InlineData("net10.0", "", "net10.0", null)]
    public void A_multi_targeted_game_runs_its_first_framework_unless_told(string one, string several, string? asked, string? expected)
    {
        var output = $$"""{ "Properties": { "TargetFramework": "{{one}}", "TargetFrameworks": "{{several}}" } }""";
        Assert.Equal(expected, GameProject.ChooseFramework(output, asked));
    }

    [Fact]
    public void A_framework_the_game_doesnt_target_lists_the_ones_it_does()
    {
        var output = """{ "Properties": { "TargetFramework": "", "TargetFrameworks": "net8.0;net11.0" } }""";
        var error = Assert.Throws<ToolException>(() => GameProject.ChooseFramework(output, "net9.0"));
        Assert.Equal("framework 'net9.0' isn't one the project targets: net8.0, net11.0.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Game.cs(1,1): warning CS0169: The field 'x' is never used")]
    [InlineData("Game.cs(1,1): warning CS8618: Non-nullable field '{x}' must contain a value")]
    public void Build_output_without_properties_is_a_clear_error(string output)
    {
        var run = Assert.Throws<ToolException>(() => GameProject.ParseRunCommand(output));
        var frameworks = Assert.Throws<ToolException>(() => GameProject.ChooseFramework(output, null));
        Assert.StartsWith("MSBuild didn't report the game's output path:", run.Message, StringComparison.Ordinal);
        Assert.StartsWith("MSBuild didn't report target frameworks:", frameworks.Message, StringComparison.Ordinal);
        Assert.EndsWith(output, run.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><TargetFramework> net8.0 </TargetFramework></PropertyGroup></Project>", "net8.0")]
    [InlineData("<Project><!-- <TargetFramework>net8.0</TargetFramework> --></Project>", null)]
    [InlineData("<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><TargetFramework Condition=\"'$(X)' == ''\">net8.0</TargetFramework></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><TargetFramework>$(GameFramework)</TargetFramework></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>", null)]
    [InlineData("<Project><PropertyGroup><TargetFramework>net8.0", null)]
    public void Framework_is_read_from_the_project_only_when_nothing_else_can_set_it(string xml, string? expected) =>
        Assert.Equal(expected, GameProject.StatedFramework(xml));

    [Fact]
    public void Properties_are_found_after_warnings_that_contain_braces()
    {
        var output = """
            Directory.Build.props(3,5): warning MSB4011: "{x}" cannot be imported again.
            { "Properties": { "TargetFramework": "", "TargetFrameworks": "net8.0;net11.0" } }
            """;
        Assert.Equal("net8.0", GameProject.ChooseFramework(output, null));
    }

    [Theory]
    [InlineData(new[] { "a", "b c" }, "a \"b c\"")]
    [InlineData(new[] { "" }, "\"\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    [InlineData(new[] { @"C:\out dir\" }, "\"C:\\out dir\\\\\"")]
    [InlineData(new[] { @"C:\no\spaces" }, @"C:\no\spaces")]
    public void Command_lines_quote_like_ArgumentList(string[] arguments, string expected) =>
        Assert.Equal(expected, CommandLine.Join(arguments));

    [Fact]
    public void Picks_evenly_with_both_ends()
    {
        Assert.Equal([0, 3, 6, 9], ContactSheet.PickEvenly(Enumerable.Range(0, 10).ToList(), 4));
        Assert.Equal([0, 1], ContactSheet.PickEvenly([0, 1], 12));
    }

    [Fact]
    public void Contact_sheet_tiles_are_cropped_enlarged_and_labeled()
    {
        var folder = Directory.CreateTempSubdirectory("agenttools-sheet-").FullName;
        try
        {
            var frames = Path.Combine(folder, "frames");
            Directory.CreateDirectory(frames);
            for (var f = 1; f <= 3; f++)
            {
                var image = new RgbImage(64, 36);
                Array.Fill(image.Pixels, (byte)(f * 60));
                File.WriteAllBytes(Path.Combine(frames, $"f{f:0000}.png"), Png.Encode(image));
            }
            File.WriteAllText(Path.Combine(frames, "notes.txt"), "ignored");

            var tiles = SheetTools.SavedFrames(frames, fps: 60);
            var sheet = ContactSheet.Build(tiles, columns: 2, new Crop(8, 8, 32, 16), PixelFont.LoadDefault());

            Assert.Equal([1, 2, 3], tiles.Select(t => t.Frame));
            Assert.Equal(2 / 60.0, tiles[1].Seconds, 9);
            // 32 px crops in 783 px columns are enlarged 4x (the limit): 2 columns, 2 rows, 2 px gaps.
            Assert.Equal((2 * 128 + 2, 2 * 64 + 2), (sheet.Width, sheet.Height));
            var bottomRight = ((sheet.Height - 1) * sheet.Width + sheet.Width - 1) * 3;
            Assert.Equal(0, sheet.Pixels[bottomRight]);                          // empty fourth tile
            var tileCenter = (32 * sheet.Width + 64) * 3;
            Assert.Equal(60, sheet.Pixels[tileCenter]);                          // frame 1 content
            Assert.Contains(sheet.Pixels[..(sheet.Width * 3 * 15)], b => b == 255); // label text
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Comparison_rows_pair_the_same_frame_and_mark_changed_pixels()
    {
        var root = Directory.CreateTempSubdirectory("agenttools-compare-").FullName;
        try
        {
            var workspace = new Workspace(root);
            string Capture(string name, Action<int, RgbImage> draw, params int[] frames)
            {
                var folder = workspace.PrepareCapture(name);
                File.WriteAllText(Path.Combine(folder, "summary.json"), """{ "fps": 60 }""");
                Directory.CreateDirectory(Path.Combine(folder, "frames"));
                foreach (var f in frames)
                {
                    var image = new RgbImage(64, 36);
                    Array.Fill(image.Pixels, (byte)40);
                    draw(f, image);
                    File.WriteAllBytes(Path.Combine(folder, "frames", $"f{f:0000}.png"), Png.Encode(image));
                }
                return folder;
            }
            Capture("before", (_, _) => { }, 1, 2, 3);
            // Frame 2 gains one white pixel, frame 3 one slightly brighter pixel; frame 4 exists only in "after".
            var after = Capture("after", (f, image) =>
            {
                if (f > 1)
                    image.Pixels.AsSpan((10 * 64 + 20) * 3, 3).Fill(f == 2 ? (byte)255 : (byte)46);
            }, 1, 2, 3, 4);

            var sheet = SheetTools.Make(workspace, after, new SheetOptions { Baseline = "before" });

            Assert.Contains("f1 0.017s same, f2 0.033s diff 0.04%, f3 0.050s diff 0.04%", sheet.Caption, StringComparison.Ordinal);
            Assert.Contains("1 frames in range were saved only in B", sheet.Caption, StringComparison.Ordinal);
            Assert.Contains("Comparison .agenttools/captures/after/sheets/vs-before_", sheet.Caption, StringComparison.Ordinal);
            Assert.Contains("A = before, B = after.", sheet.Caption, StringComparison.Ordinal);
            var image = Png.Decode(sheet.Image.DecodedData.ToArray());
            // 64 px frames enlarge 4x (the limit) into 3 columns; one row per shared frame.
            Assert.Equal((3 * 256 + 2 * 2, 3 * 144 + 2 * 2), (image.Width, image.Height));
            var changed = ((144 + 2 + 10 * 4) * image.Width + 2 * (256 + 2) + 20 * 4) * 3;
            Assert.Equal([255, 0, 0], image.Pixels[changed..(changed + 3)]);                  // full red in row 2's diff tile
            var faint = changed + (144 + 2) * image.Width * 3;
            Assert.InRange(image.Pixels[faint], 111, 200);                                    // a dimmer red in row 3
            Assert.Equal(0, image.Pixels[faint + 1]);
            var unchanged = ((144 + 2 + 30 * 4) * image.Width + 2 * (256 + 2) + 40 * 4) * 3;
            Assert.Equal(40 / 3, image.Pixels[unchanged]);                                   // dimmed B elsewhere

            Assert.Throws<ToolException>(() => SheetTools.Make(workspace, after, new SheetOptions { Baseline = "missing" }));
            Assert.Throws<ToolException>(() => SheetTools.Make(workspace, after, new SheetOptions { Baseline = "before", Count = 13 }));

            // Without diff, rows are A and B only.
            var plain = Png.Decode(SheetTools.Make(workspace, after, new SheetOptions { Baseline = "before", Diff = false }).Image.DecodedData.ToArray());
            Assert.Equal(2 * 256 + 2, plain.Width);

            // A HUD over the changed pixel in either capture leaves its area out: the frame counts as the same, shown in blue.
            File.WriteAllText(Path.Combine(after, "summary.json"), """{ "fps": 60, "hud": { "region": [16, 8, 8, 4] } }""");
            var masked = SheetTools.Make(workspace, after, new SheetOptions { Baseline = "before" });
            Assert.Contains("f2 0.033s same, f3 0.050s same", masked.Caption, StringComparison.Ordinal);
            Assert.Contains("The HUD's area (16,8,8,4, blue) isn't compared.", masked.Caption, StringComparison.Ordinal);
            var maskedImage = Png.Decode(masked.Image.DecodedData.ToArray());
            Assert.Equal([20, 40, 110], maskedImage.Pixels[changed..(changed + 3)]);
            Assert.Equal(40 / 3, maskedImage.Pixels[unchanged]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Brief_reads_every_check_from_a_real_summary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.json");
        try
        {
            var frames = Enumerable.Range(1, 120).Select(i => new FrameSample { Frame = i, FrameMs = 1, WallSeconds = i / 60.0, SimSeconds = i / 60.0 }).ToArray();
            for (int i = 0; i < 5; ++i)
                frames[i * 10].FrameMs = 10 + i;
            var info = new ReportInfo
            {
                Mode = "capture", Game = "Test", Width = 1280, Height = 720, WallSeconds = 2, Fps = 60, FrameTimeBasis = "test",
                Notes = ["Esc - exit\nshow frostnova"],
                CustomMetricNames = ["smoke", "core"], CustomMetricValues = [.. frames.SelectMany(f => new[] { f.Frame, double.NaN })],
            };
            Report.WriteSummary(path, info, FrameAnalysis.Run(frames, new WarmupDetector()));

            var brief = CaptureBrief.Format(File.ReadAllText(path));

            Assert.Contains("120 frames, 60 fps: 2 s game time, 2 s wall, 1280x720\n", brief, StringComparison.Ordinal);
            Assert.Contains("Notes: Esc - exit; show frostnova\n", brief, StringComparison.Ordinal);
            Assert.Contains("Hitches (> 3 ms): 5, largest f41 14 ms: ", brief, StringComparison.Ordinal);
            Assert.Contains("; and 2 more\n", brief, StringComparison.Ordinal);
            Assert.Contains("  smoke: 1 → 120, 1 to 120\n", brief, StringComparison.Ordinal);
            Assert.Contains("  core: no value\n", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("?", brief, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Guide_lists_topics_with_summaries_and_returns_one()
    {
        var list = GuideTools.Guide();
        Assert.Contains("- setup: ", list, StringComparison.Ordinal);
        Assert.Contains("- contact-sheets: ", list, StringComparison.Ordinal);
        Assert.StartsWith("# performance", GuideTools.Guide("Performance"), StringComparison.Ordinal);
        Assert.StartsWith("No topic 'nope'.", GuideTools.Guide("nope"), StringComparison.Ordinal);
    }
}

public class WorkspaceTests
{
    [Fact]
    public void Prunes_old_unnamed_captures_and_keeps_named_ones()
    {
        var root = Directory.CreateTempSubdirectory("agenttools-workspace-").FullName;
        try
        {
            var workspace = new Workspace(root);
            workspace.PrepareCapture("baseline");
            var start = DateTime.UtcNow.AddHours(-1);
            for (var i = 0; i < Workspace.KeptAutomaticCaptures + 2; i++)
            {
                var folder = workspace.PrepareCapture($"game-{i:00}", "capture");
                Directory.SetCreationTimeUtc(folder, start.AddMinutes(i));
            }

            var oldest = Path.Combine(workspace.CapturesDirectory, "game-02"); // the oldest left
            workspace.PrepareCapture("game-keep-check", "capture", [oldest]);
            Assert.True(Directory.Exists(oldest)); // a comparison baseline survives pruning

            workspace.PrepareCapture("game-latest", "capture");

            var left = Directory.GetDirectories(workspace.CapturesDirectory).Select(Path.GetFileName).Order().ToList();
            Assert.Contains("baseline", left);
            Assert.Contains("game-latest", left);
            Assert.DoesNotContain("game-00", left);
            Assert.DoesNotContain("game-02", left);
            Assert.DoesNotContain("game-03", left);
            Assert.Equal(Workspace.KeptAutomaticCaptures + 1, left.Count); // ten unnamed and "baseline"
            Assert.Equal("*", File.ReadAllLines(Path.Combine(root, ".agenttools", ".gitignore"))[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unnamed_profiles_and_captures_are_pruned_separately()
    {
        var root = Directory.CreateTempSubdirectory("agenttools-workspace-").FullName;
        try
        {
            var workspace = new Workspace(root);
            var start = DateTime.UtcNow.AddHours(-1);
            var legacy = workspace.PrepareCapture("game-legacy", "capture");
            File.WriteAllText(Path.Combine(legacy, ".automatic"), ""); // marked before there were pools
            Directory.SetCreationTimeUtc(legacy, start);
            for (var i = 0; i < Workspace.KeptAutomaticCaptures - 1; i++)
                Directory.SetCreationTimeUtc(workspace.PrepareCapture($"game-{i:00}", "capture"), start.AddMinutes(1 + i));
            for (var i = 0; i < Workspace.KeptAutomaticCaptures + 3; i++)
                Directory.SetCreationTimeUtc(workspace.PrepareCapture($"game-profile-{i:00}", "profile"), start.AddMinutes(20 + i));

            var left = Directory.GetDirectories(workspace.CapturesDirectory).Select(Path.GetFileName).ToList();
            Assert.Contains("game-legacy", left);
            Assert.Equal(Workspace.KeptAutomaticCaptures, left.Count(n => n!.StartsWith("game-profile-", StringComparison.Ordinal)));

            workspace.PrepareCapture("game-new", "capture");
            Assert.False(Directory.Exists(legacy)); // an empty marker is in the capture pool
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
