#!/usr/bin/env bash
#
# Installs or upgrades Compose Wellness as a systemd service on a Debian/Ubuntu style host.
#
# Run this script as root from an unpacked release directory, i.e. the directory that
# contains this script, compose-wellness.service and the published application in ./app:
#
#   sudo bash ./install.sh
#
# Settings can be passed as environment variables (see the README for the full list):
#
#   ROOT_DIRECTORY=/srv/docker LISTEN_URL=http://0.0.0.0:5000 sudo -E bash ./install.sh
#
# Re-running the script upgrades the application files. Settings that are not passed keep the
# value from the existing drop-in, so an upgrade never resets your configuration. Put any extra
# systemd settings in a separate drop-in (for example 20-local.conf); this script only ever
# writes 10-install.conf.

set -euo pipefail

SERVICE_NAME="compose-wellness"
INSTALL_DIR="${INSTALL_DIR:-/opt/compose-wellness}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
DROPIN_DIR="/etc/systemd/system/${SERVICE_NAME}.service.d"
DROPIN_FILE="${DROPIN_DIR}/10-install.conf"

die() {
    echo "error: $*" >&2
    exit 1
}

# Reads one value from the existing drop-in, or prints nothing when it is not there.
existing() {
    [[ -f "${DROPIN_FILE}" ]] || return 0
    grep -oP "^${1}=\K.*" "${DROPIN_FILE}" 2>/dev/null | head -n 1 || true
}

# Normalizes yes/no style input to the literal true or false expected by the application.
as_bool() {
    case "${1,,}" in
        true|1|yes|on) echo "true" ;;
        false|0|no|off|"") echo "false" ;;
        *) die "expected true or false, got: $1" ;;
    esac
}

[[ ${EUID} -eq 0 ]] || die "this script must run as root (sudo bash ./install.sh)"
# The install directory is emptied on upgrade, so refuse anything that does not look like ours.
[[ "${INSTALL_DIR}" == /*/compose-wellness ]] || die "INSTALL_DIR must be an absolute path ending in /compose-wellness (got: ${INSTALL_DIR})"
[[ -f "${HERE}/app/ComposeWellness" ]] || die "published application not found at ${HERE}/app/ComposeWellness"
[[ -f "${HERE}/compose-wellness.service" ]] || die "unit file not found at ${HERE}/compose-wellness.service"
command -v systemctl >/dev/null || die "systemd is required"
command -v docker >/dev/null || die "docker is not installed or not on PATH"
docker compose version >/dev/null 2>&1 || die "docker compose (v2 plugin) is not available"
getent group docker >/dev/null || die "the docker group does not exist"

# Effective settings: explicit environment variable, else the value from the current drop-in,
# else the default.
SERVICE_USER="${SERVICE_USER:-$(existing User)}"
SERVICE_USER="${SERVICE_USER:-compose-wellness}"
ROOT_DIRECTORY="${ROOT_DIRECTORY:-$(existing 'Environment=ComposeWellness__RootDirectory')}"
ROOT_DIRECTORY="${ROOT_DIRECTORY:-/opt/stacks}"
LISTEN_URL="${LISTEN_URL:-$(existing 'Environment=ASPNETCORE_URLS')}"
LISTEN_URL="${LISTEN_URL:-http://127.0.0.1:5000}"
ALLOW_ROOT_CHANGE="$(as_bool "${ALLOW_ROOT_CHANGE:-$(existing 'Environment=ComposeWellness__AllowRootDirectoryChange')}")"
REMOVE_ORPHANS="$(as_bool "${REMOVE_ORPHANS:-$(existing 'Environment=ComposeWellness__RemoveOrphans')}")"

# Service account. A system user without a login shell, member of the docker group.
if ! id "${SERVICE_USER}" >/dev/null 2>&1; then
    echo "Creating system user ${SERVICE_USER}"
    useradd --system --user-group --home-dir "/var/lib/${SERVICE_NAME}" --no-create-home \
        --shell /usr/sbin/nologin "${SERVICE_USER}"
fi
usermod -aG docker "${SERVICE_USER}"

# Application files. The install directory holds nothing but the published output,
# so it is safe to replace it completely on upgrade.
if systemctl is-active --quiet "${SERVICE_NAME}"; then
    echo "Stopping ${SERVICE_NAME}"
    systemctl stop "${SERVICE_NAME}"
fi

echo "Installing application to ${INSTALL_DIR}"
mkdir -p "${INSTALL_DIR}"
find "${INSTALL_DIR}" -mindepth 1 -delete
cp -a "${HERE}/app/." "${INSTALL_DIR}/"
chmod 0755 "${INSTALL_DIR}/ComposeWellness"
chown -R root:root "${INSTALL_DIR}"

# Unit file and site specific drop-in. The group is left to systemd, which uses the primary
# group of the service user.
echo "Installing systemd unit ${UNIT_FILE}"
install -m 0644 "${HERE}/compose-wellness.service" "${UNIT_FILE}"
mkdir -p "${DROPIN_DIR}"

echo "Writing ${DROPIN_FILE}"
cat > "${DROPIN_FILE}" <<EOF
# Site specific settings for Compose Wellness, written by install.sh and preserved on upgrade.
# Edit and run: systemctl daemon-reload && systemctl restart ${SERVICE_NAME}
# Put additional settings in another drop-in, for example 20-local.conf.
[Service]
User=${SERVICE_USER}
Environment=ComposeWellness__RootDirectory=${ROOT_DIRECTORY}
Environment=ASPNETCORE_URLS=${LISTEN_URL}
Environment=ComposeWellness__AllowRootDirectoryChange=${ALLOW_ROOT_CHANGE}
Environment=ComposeWellness__RemoveOrphans=${REMOVE_ORPHANS}
EOF
chmod 0644 "${DROPIN_FILE}"

if [[ ! -d "${ROOT_DIRECTORY}" ]]; then
    echo "warning: Docker root directory ${ROOT_DIRECTORY} does not exist yet" >&2
fi

echo "Enabling and starting ${SERVICE_NAME}"
systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}"
systemctl --no-pager --lines=5 status "${SERVICE_NAME}" || true

echo
echo "Compose Wellness is installed."
echo "  Service user:         ${SERVICE_USER}"
echo "  Root directory:       ${ROOT_DIRECTORY}"
echo "  Folder change in UI:  ${ALLOW_ROOT_CHANGE}"
echo "  Remove orphans:       ${REMOVE_ORPHANS}"
echo "  Configuration:        ${DROPIN_FILE}"
echo "  Logs:                 journalctl -u ${SERVICE_NAME} -f"
echo "  Web UI:               ${LISTEN_URL}"
