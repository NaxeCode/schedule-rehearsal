#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
dotnet restore --locked-mode src/Rehearsal.csproj --verbosity quiet
dotnet build src/Rehearsal.csproj -c Release --no-restore --verbosity quiet
python3 scripts/replay.py "$@"
