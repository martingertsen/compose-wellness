#!/usr/bin/env bash
#
# Builds a self-contained release for Linux and packs it together with the install script.
# Requires the .NET SDK on the machine running this script (not on the target host).
#
#   ./deploy/publish.sh            # linux-x64
#   ./deploy/publish.sh linux-arm64
#
# Output: dist/compose-wellness-<rid>.tar.gz

set -euo pipefail

RID="${1:-linux-x64}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="compose-wellness-${RID}"
OUT="${REPO}/dist/${NAME}"

rm -rf "${OUT}"
mkdir -p "${OUT}/app"

dotnet publish "${REPO}/src/ComposeWellness/ComposeWellness.csproj" \
    --configuration Release \
    --runtime "${RID}" \
    --self-contained true \
    --output "${OUT}/app"

cp "${REPO}/deploy/install.sh" "${REPO}/deploy/compose-wellness.service" "${OUT}/"
chmod +x "${OUT}/install.sh" "${OUT}/app/ComposeWellness" 2>/dev/null || true

tar -czf "${REPO}/dist/${NAME}.tar.gz" -C "${REPO}/dist" "${NAME}"
echo "Created ${REPO}/dist/${NAME}.tar.gz"
