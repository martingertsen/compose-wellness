#!/usr/bin/env bash
#
# Self-update for Compose Wellness. Started by compose-wellness-update.service (as root) when
# the web UI has created the trigger file. Downloads the latest release of the repository
# configured for the web service and hands over to that release's install.sh, which stops,
# replaces and restarts the service exactly like a manual upgrade.
#
# Nothing here trusts anything the service user can write: the trigger file content is ignored
# and the repository comes from the root-owned systemd configuration.

set -euo pipefail

SERVICE_NAME="compose-wellness"
TRIGGER_FILE="/var/lib/${SERVICE_NAME}/self-update.request"
INSTALL_DIR="${INSTALL_DIR:-/opt/compose-wellness}"
DEFAULT_REPOSITORY="martingertsen/compose-wellness"
CURL=(curl -fSsL --retry 2 -H "Accept: application/vnd.github+json" -H "User-Agent: ComposeWellness-self-update")

log() { echo "$*"; }
die() { echo "error: $*" >&2; exit 1; }

# Remove the trigger first: PathExists would otherwise start this unit again as soon as it exits.
rm -f "${TRIGGER_FILE}"

[[ ${EUID} -eq 0 ]] || die "must run as root"
command -v curl >/dev/null || die "curl is not installed"
command -v tar >/dev/null || die "tar is not installed"
command -v sha256sum >/dev/null || die "sha256sum is not installed"

# Effective environment of the web service, including every drop-in. Root-owned by definition.
# An explicitly empty value disables the check; only an absent variable falls back to the default.
SERVICE_ENV="$(systemctl show "${SERVICE_NAME}" -p Environment --value | tr ' ' '\n')"

# The web UI hides the button when AllowSelfUpdate is false, but the trigger file can also be
# created by anything running as the service user, so the root side enforces the setting too.
ALLOW_SELF_UPDATE="$(grep -oP '^ComposeWellness__AllowSelfUpdate=\K.*' <<<"${SERVICE_ENV}" | head -n 1 || true)"
if [[ "${ALLOW_SELF_UPDATE,,}" == "false" ]]; then
    log "Self-update is disabled (ComposeWellness__AllowSelfUpdate=false)."
    exit 0
fi

if grep -q '^ComposeWellness__UpdateRepository=' <<<"${SERVICE_ENV}"; then
    REPOSITORY="$(grep -oP '^ComposeWellness__UpdateRepository=\K.*' <<<"${SERVICE_ENV}" | head -n 1 || true)"
else
    REPOSITORY="${DEFAULT_REPOSITORY}"
fi
if [[ -z "${REPOSITORY}" ]]; then
    log "Update repository is empty; self-update is disabled."
    exit 0
fi
[[ "${REPOSITORY}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "invalid update repository: ${REPOSITORY}"

case "$(uname -m)" in
    x86_64) RID="linux-x64" ;;
    aarch64) RID="linux-arm64" ;;
    *) die "unsupported architecture: $(uname -m)" ;;
esac

log "Checking latest release of ${REPOSITORY}"
TAG="$("${CURL[@]}" "https://api.github.com/repos/${REPOSITORY}/releases/latest" \
    | grep -oP '"tag_name"\s*:\s*"\K[^"]+' | head -n 1 || true)"
[[ -n "${TAG}" ]] || die "could not read the latest release tag"
LATEST="${TAG#v}"
LATEST="${LATEST#V}"
[[ "${LATEST}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "latest release tag is not a version: ${TAG}"

# timeout guards against a binary that does not understand --version and starts the web host instead.
INSTALLED="$(timeout 10 "${INSTALL_DIR}/ComposeWellness" --version 2>/dev/null || echo unknown)"
if [[ "${INSTALLED}" == "${LATEST}" ]]; then
    log "Already up to date (${LATEST})."
    exit 0
fi
log "Installed ${INSTALLED}, latest ${LATEST}: upgrading"

# The previous run handed over to install.sh with exec, so its trap never removed its work
# directory. Clean up here instead of leaving a few hundred megabytes per update behind.
rm -rf /var/tmp/compose-wellness-update.* 2>/dev/null || true

WORK="$(mktemp -d /var/tmp/compose-wellness-update.XXXXXX)"
trap 'rm -rf "${WORK}"' EXIT
NAME="compose-wellness-${RID}"
BASE_URL="https://github.com/${REPOSITORY}/releases/download/${TAG}"

log "Downloading ${BASE_URL}/${NAME}.tar.gz"
"${CURL[@]}" -o "${WORK}/${NAME}.tar.gz" "${BASE_URL}/${NAME}.tar.gz"

if "${CURL[@]}" -o "${WORK}/SHA256SUMS" "${BASE_URL}/SHA256SUMS" 2>/dev/null; then
    log "Verifying checksum"
    (cd "${WORK}" && sha256sum -c --ignore-missing SHA256SUMS) || die "checksum verification failed"
else
    log "Release has no SHA256SUMS; skipping checksum verification"
fi

tar -xzf "${WORK}/${NAME}.tar.gz" -C "${WORK}"
[[ -f "${WORK}/${NAME}/install.sh" ]] || die "install.sh missing from the release"
[[ -f "${WORK}/${NAME}/app/ComposeWellness" ]] || die "application missing from the release"

# install.sh keeps every setting from the existing drop-in. exec replaces this script, so
# install.sh emptying INSTALL_DIR (where this script lives) is harmless. The trap does not run
# after exec; install.sh does not need the download once it has copied the files, and any
# leftovers land in /var/tmp on disk (not a tmpfs) and are removed by the system's periodic
# cleanup.
log "Running install.sh from release ${TAG}"
cd "${WORK}/${NAME}"
exec env INSTALL_DIR="${INSTALL_DIR}" bash ./install.sh
