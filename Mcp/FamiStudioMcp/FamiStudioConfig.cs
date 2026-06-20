namespace FamiStudioMcp;

// Resolves runtime configuration from environment variables.
internal static class FamiStudioConfig
{
    // Path to the FamiStudio executable used for stateless command-line operations.
    public static string? BinaryPath => Environment.GetEnvironmentVariable("FAMISTUDIO_BIN");

    // Base URL of the running app's embedded control server.
    public static string ControlUrl =>
        Environment.GetEnvironmentVariable("FAMISTUDIO_MCP_URL")?.TrimEnd('/') is { Length: > 0 } url
            ? url
            : "http://127.0.0.1:8675";

    public static string RequireBinary()
    {
        var path = BinaryPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "FAMISTUDIO_BIN is not set. Point it at the FamiStudio executable " +
                "(macOS: /Applications/FamiStudio.app/Contents/MacOS/FamiStudio).");
        if (!File.Exists(path))
            throw new InvalidOperationException($"FAMISTUDIO_BIN does not exist: {path}");
        return path;
    }
}
