#!/usr/bin/env bash
# Builds the Native AOT binary into ./dist
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Distro-built .NET SDKs (e.g. Ubuntu's) ship their AOT compiler for their own RID; Microsoft's for linux-x64.
RID="${RID:-}"
if [[ -z "$RID" ]]; then
    native=$(dotnet --info | awk '/^ *RID:/ {print $2; exit}')
    if ls /usr/lib/dotnet/packs 2>/dev/null | grep -q "ILCompiler" && [[ -n "$native" ]]; then RID="$native"; else RID="linux-x64"; fi
fi
echo "Building for $RID"
dotnet publish src/CddaCompanion/CddaCompanion.csproj -c Release -r "$RID" -o dist
rm -f dist/*.dbg
echo "Done: dist/cdda-companion"
