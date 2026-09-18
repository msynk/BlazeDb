#!/usr/bin/env bash
# Checks that the packed .nupkg files carry what consumers depend on but the build cannot
# fail over: the source generator inside the BlazeDb package (packed from a hardcoded output
# path) and the ES modules inside BlazeDb.Browser. A package missing either restores fine and
# fails only in the consumer's build or at runtime in the browser.
set -euo pipefail

artifacts="${1:-artifacts}"

require() {
  local package="$1" entry="$2"
  local file
  file=$(ls "$artifacts"/"$package".[0-9]*.nupkg 2>/dev/null | head -n 1)
  if [ -z "$file" ]; then
    echo "::error::No $package package found in $artifacts"
    exit 1
  fi
  if ! unzip -Z1 "$file" | grep -q -- "$entry"; then
    echo "::error::$file does not contain $entry"
    unzip -Z1 "$file"
    exit 1
  fi
  echo "$file contains $entry"
}

require BlazeDb "analyzers/dotnet/cs/BlazeDb.SourceGen.dll"
require BlazeDb "buildTransitive/BlazeDb.targets"
require BlazeDb.Browser "blazedb-opfs.js"
require BlazeDb.Browser "blazedb-idb.js"
require BlazeDb.Browser "blazedb-crypto.js"
require BlazeDb.Browser "blazedb-tabs.js"
require BlazeDb.EntityFrameworkCore "lib/net10.0/BlazeDb.EntityFrameworkCore.dll"
