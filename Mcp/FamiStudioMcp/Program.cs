using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// FamiStudio MCP server.
//
// Exposes tools that let Claude (and other MCP clients) author, inspect, render, and live-drive
// FamiStudio. Two families of tools:
//   - Stateless: operate on FamiStudio Text / project files via the FamiStudio command-line app
//     (read/write/describe/render/export). No running app required.
//   - Live: drive a running FamiStudio launched with "-mcpserver", via its HTTP control server
//     (open/play/stop/seek/select song/get+apply text/save).
//
// Configuration (environment variables):
//   FAMISTUDIO_BIN  - path to the FamiStudio executable used for stateless CLI operations.
//                     On macOS this is inside the app bundle, e.g.
//                     /Applications/FamiStudio.app/Contents/MacOS/FamiStudio
//   FAMISTUDIO_MCP_URL - base URL of the running app's control server (default http://127.0.0.1:8675/).

var builder = Host.CreateApplicationBuilder(args);

// The MCP stdio transport owns stdout for protocol messages, so route all logging to stderr.
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
