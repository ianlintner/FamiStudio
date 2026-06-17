#!/bin/zsh
# Wrapper so FAMISTUDIO_BIN can point at the fork's built FamiStudio in command-line mode.
# Used by the MCP server's stateless tools (read/write/describe/render/export, semantic-apply).
# Resolves the built FamiStudio.dll relative to this script; prefers Release, falls back to Debug.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
for cfg in Release Debug; do
  dll="$here/../FamiStudio/bin/$cfg/net8.0/FamiStudio.dll"
  if [ -f "$dll" ]; then
    cd "$(dirname "$dll")"   # ensure native dylibs resolve
    exec dotnet "$dll" "$@"
  fi
done
echo "FamiStudio.dll not found. Build the fork first: dotnet build FamiStudio/FamiStudio.Mac.csproj" >&2
exit 1
