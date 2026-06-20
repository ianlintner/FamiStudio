using System.Text.RegularExpressions;

namespace FamiStudioMcp;

// Minimal reader for the FamiStudio Text format, enough to produce a structured summary.
// The format is tab-indented lines like:
//   Project Version="..." Name="..." Author="..."
//       Instrument Name="Lead"
//       Song Name="Intro" Length="8" PatternLength="16" ...
//           Channel Type="Square1"
//               Pattern Name="p0"
//                   Note Time="0" Value="C4" ...
internal static class FamiStudioText
{
    private static readonly Regex AttrRegex = new("(\\w+)=\"([^\"]*)\"", RegexOptions.Compiled);

    public static object Describe(string text)
    {
        string? projectName = null, author = null, copyright = null;
        var songs = new List<Dictionary<string, object?>>();
        var instruments = new List<string?>();
        Dictionary<string, object?>? currentSong = null;
        var channelCount = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart('\t', ' ');

            if (trimmed.StartsWith("Project"))
            {
                var a = ParseAttrs(trimmed);
                a.TryGetValue("Name", out projectName);
                a.TryGetValue("Author", out author);
                a.TryGetValue("Copyright", out copyright);
            }
            else if (trimmed.StartsWith("Instrument"))
            {
                var a = ParseAttrs(trimmed);
                instruments.Add(a.GetValueOrDefault("Name"));
            }
            else if (trimmed.StartsWith("Song"))
            {
                FinalizeSong(currentSong, channelCount, songs);
                channelCount = 0;

                var a = ParseAttrs(trimmed);
                currentSong = new Dictionary<string, object?>
                {
                    ["name"] = a.GetValueOrDefault("Name"),
                    ["length"] = ToInt(a.GetValueOrDefault("Length")),
                    ["patternLength"] = ToInt(a.GetValueOrDefault("PatternLength")),
                    ["loopPoint"] = ToInt(a.GetValueOrDefault("LoopPoint")),
                };
            }
            else if (trimmed.StartsWith("Channel"))
            {
                channelCount++;
            }
        }

        FinalizeSong(currentSong, channelCount, songs);

        return new
        {
            name = projectName,
            author,
            copyright,
            songCount = songs.Count,
            instrumentCount = instruments.Count,
            instruments,
            songs,
        };
    }

    private static void FinalizeSong(Dictionary<string, object?>? song, int channelCount, List<Dictionary<string, object?>> songs)
    {
        if (song == null) return;
        song["channels"] = channelCount;
        songs.Add(song);
    }

    private static Dictionary<string, string> ParseAttrs(string line)
    {
        var dict = new Dictionary<string, string>();
        foreach (Match m in AttrRegex.Matches(line))
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        return dict;
    }

    private static int? ToInt(string? s) => int.TryParse(s, out var v) ? v : null;
}
