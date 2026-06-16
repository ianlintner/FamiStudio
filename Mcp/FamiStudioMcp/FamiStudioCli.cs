using System.Diagnostics;
using System.Text;

namespace FamiStudioMcp;

// Thin wrapper around the FamiStudio command-line app for stateless (no running app) operations.
// Invocation form:  FamiStudio <input> <command> <output> [-options]
internal static class FamiStudioCli
{
    public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static async Task<CliResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var bin = FamiStudioConfig.RequireBinary();

        var psi = new ProcessStartInfo
        {
            FileName = bin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);

        return new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // Exports a project (any supported input) to FamiStudio Text and returns the text content.
    public static async Task<string> ExportTextAsync(string inputPath, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"fs_mcp_{Guid.NewGuid():N}.txt");
        try
        {
            var result = await RunAsync(new[] { inputPath, "famistudio-txt-export", tmp }, ct);
            if (!result.Ok || !File.Exists(tmp))
                throw new InvalidOperationException(
                    $"FamiStudio text export failed (exit {result.ExitCode}).\n{result.StdOut}\n{result.StdErr}");
            return await File.ReadAllTextAsync(tmp, ct);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
