#!/usr/bin/env bash
#
# Builds and packages the Jellyfin plugin, taking the version from version.json.
#
# This is the only place packaging is implemented. The Docker build, CI and the release
# workflow all call it, so there is no second copy to drift out of step — which is exactly
# what happened when the Dockerfile and csproj each carried their own version literal.
#
# Usage:  scripts/package-plugin.sh [output-dir]
# Output: key=value lines on stdout, suitable for appending to $GITHUB_OUTPUT.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-${root}/artifacts}"

version="$(jq -r '.version' "${root}/version.json")"
abi="$(jq -r '.targetAbi' "${root}/version.json")"

if ! printf '%s' "${version}" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
  echo "version.json: version must be MAJOR.MINOR.PATCH, got '${version}'" >&2
  exit 1
fi

# Jellyfin expects a four-part assembly version.
plugin="${version}.0"
name="jellyfin-plugin-jetio-${plugin}.zip"

staging="$(mktemp -d)"
trap 'rm -rf "${staging}"' EXIT

dotnet publish "${root}/src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj" \
  -c Release -o "${staging}/publish" \
  -p:Version="${version}" \
  -p:AssemblyVersion="${plugin}" \
  -p:FileVersion="${plugin}" \
  --nologo >&2

mkdir -p "${staging}/pkg" "${out}"

# Jellyfin wants the assembly and meta.json only; deps.json and pdb confuse its loader.
cp "${staging}/publish/Jellyfin.Plugin.Jetio.dll" "${staging}/pkg/"
jq --arg v "${plugin}" --arg abi "${abi}" \
  '.version = $v | .targetAbi = $abi' \
  "${root}/src/Jellyfin.Plugin.Jetio/meta.json" > "${staging}/pkg/meta.json"

# Clear older packages: when this writes into jetio's wwwroot, the manifest endpoint lists
# whatever it finds there, and a stale build would keep being offered alongside this one.
rm -f "${out}"/jellyfin-plugin-jetio-*.zip
(cd "${staging}/pkg" && zip -q -r "${out}/${name}" .)

echo "version=${version}"
echo "plugin=${plugin}"
echo "abi=${abi}"
echo "name=${name}"
echo "path=${out}/${name}"
