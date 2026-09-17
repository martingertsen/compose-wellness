# <img src="assets/icon.png" alt="" width="40" align="top"> Compose Wellness

**Keep your Docker Compose stacks healthy and up to date.**

A small self-hosted web page with one button, **Update All**, that updates every Docker Compose
stack below a directory on your Docker host and streams the console output to your browser.

```text
/opt/stacks/
├── backend/compose.yml       -> docker compose pull && docker compose up -d
├── frontend/compose.yml      -> docker compose pull && docker compose up -d
├── custom-app/update.sh      -> bash ./update.sh
├── stopped-stack/compose.yml -> skipped, no running containers
├── misc-files/               -> skipped, nothing to update
└── _Archive/old/compose.yml  -> skipped, never inspected below the first level
```

Compose Wellness runs **directly on the host** as a systemd service, not inside a container. That is
what allows it to recreate every container on the machine, including management tools such as a
Compose dashboard, a log viewer or a job scheduler, without cutting off its own output.

## How it works

Every time you press **Update All** the application:

1. Lists the **immediate child directories** of the configured root directory, sorted by name.
   Nothing deeper is ever inspected, so `_Archive/old-stack/compose.yml` is never touched.
2. For each directory, in order:
   - If `update.sh` exists, it runs `bash ./update.sh` in that directory. The script is fully
     responsible for the update. Exit code 0 means success, anything else means failure.
   - Otherwise, if one of `compose.yml`, `compose.yaml`, `docker-compose.yml` or
     `docker-compose.yaml` exists, it runs the standard Compose procedure (see below).
   - Otherwise the directory is skipped.
3. Streams stdout and stderr of every command to the browser as it happens.
4. Continues with the next stack when one fails.
5. Shows a summary with successful, failed and skipped stacks and the total duration.

There is no list of stacks to maintain. Adding a directory with a Compose file makes it part of
the next update.

### Standard Compose procedure

Executed with the stack directory as working directory, so Compose picks up the Compose file and
any `.env` file by itself:

```bash
docker compose ps -q --status running       # is anything running?
docker compose --ansi never pull
docker compose --ansi never up -d           # plus --remove-orphans when RemoveOrphans is enabled
```

- If **no container of the stack is running**, the stack is skipped with
  `Stack is not currently running`. A completely stopped stack was most likely stopped on purpose,
  and an update should not start it again. Use an `update.sh` if you want different behavior for
  a particular stack.
- The check is per stack, not per service. If some services of a running stack are stopped on
  purpose, `up -d` starts them again. Use an `update.sh` for such stacks.
- If `pull` fails, the stack is marked failed and `up` is not executed. This also happens for a
  service that is built locally (`build:` in the Compose file) with an `image:` name that does not
  exist in a registry: `pull` cannot find it and reports an error. Use an `update.sh` for such
  stacks and run the build there.
- If `up` fails, the stack is marked failed.
- The container ids of the project are listed before and after the update (`docker compose ps -a -q`).
  A recreated container always gets a new id, so the result shows `Recreated 1 of 2 containers`
  or `No change`. The same comparison is made around an `update.sh` when a Compose file exists in
  that directory. The summary counts the stacks in which something was recreated.
- `--ansi never` only suppresses terminal color codes so the output stays readable in the browser.
- `--remove-orphans` is off by default. When enabled (`RemoveOrphans`), Compose also deletes
  containers that belong to the project but are no longer defined in its Compose file, for
  example a service you removed from the file. Enable it if you want those cleaned up
  automatically; leave it off if you prefer nothing to be deleted without you asking.

Images are **never pruned**. Old images stay available for troubleshooting and rollback; clean them
up yourself with `docker image prune` when you want to.

### Custom `update.sh`

Put an `update.sh` in a stack directory when the stack needs something other than pull and up, for
example a local image build or a health check after the update. The script runs with the stack
directory as working directory and does not need the executable bit. Its stdin is closed, so
anything that waits for keyboard input fails immediately instead of hanging. Compose is **not**
run automatically after the script; call it from the script if you want that.

If the script leaves a background process attached to its output (for example `something &`
without redirecting), Compose Wellness waits five seconds after the script exits and then moves on;
later output from that process is not shown.

### Concurrency and browser behavior

- Only one update can run at a time, enforced on the server. A second `POST /api/update` while an
  update is running returns `409 Conflict`; the UI disables the button.
- The update is not tied to the browser connection. Close the tab and it keeps running.
- Reopening the page while an update runs shows the current state, replays the retained log and
  continues streaming.
- Compose reports image pulls as one line per progress step and layer. The console shows one
  live line per layer that is overwritten as the download and extraction progress, so a pull
  takes a handful of lines instead of dozens. The log file on disk keeps every line.
- The most recent update stays visible until the next one starts. The two icons next to
  "Follow output" copy the console text to the clipboard and clear the console on this page;
  clearing does not touch the server log, which comes back on the next reload.
- The top bar shows the folder being scanned. With `AllowRootDirectoryChange` enabled, clicking
  it lets you enter another absolute path on the Docker host; the change is saved in
  `settings.json` in the data directory, survives restarts and upgrades, and refreshes the stack
  list. It is off by default because the page has no authentication.
- The page lists the stacks the next update would process (directories with an `update.sh` or a
  Compose file). The refresh button re-reads the directory. Whether a stack is stopped is only
  determined during the update itself.
- The page follows the browser's light or dark mode. The toggle in the top right corner
  overrides it for the current browser session only.

## Requirements

On the Docker host:

- Linux with systemd (tested on Debian based distributions)
- Docker Engine with the Compose v2 plugin (`docker compose version` works), installed from
  Docker's own repository or your distribution's packages. The Ubuntu snap package is not
  supported: it has no `docker` group and no `docker.service` unit, both of which the installer
  and the systemd unit rely on.
- `bash` (for `update.sh` scripts)
- No .NET installation is needed; the release is self-contained.

On the machine that builds the release (can be the same host, or a Windows/macOS workstation):

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer

## Installation

### Install or upgrade from a GitHub release

Run this on the Docker host. Use `compose-wellness-linux-arm64.tar.gz` instead on 64-bit ARM hosts
such as a Raspberry Pi 4 or 5:

```bash
cd ~ && rm -rf compose-wellness-linux-x64 compose-wellness-linux-x64.tar.gz
curl -fLO https://github.com/martingertsen/compose-wellness/releases/latest/download/compose-wellness-linux-x64.tar.gz
tar -xzf compose-wellness-linux-x64.tar.gz && cd compose-wellness-linux-x64
sudo ROOT_DIRECTORY=/srv/docker bash ./install.sh
```

Pass the settings you want on the first install (see the table below). On an upgrade, run the
same commands without any variables: every setting is taken from the existing configuration.
To install a specific version, replace `latest/download` in the URL with `download/v1.0.0`.
Afterwards hard-reload the page in the browser (Ctrl+F5) so it picks up the new scripts and styles.

Upgrading from 1.0.0 requires this manual run once: it installs the helper units for the update
button. From then on every later release can be installed from the web UI, see
[Update from the web UI](#update-from-the-web-ui).

Releases are built by GitHub Actions from the tagged source; see `.github/workflows/release.yml`.

### Update from the web UI

When a newer release exists on GitHub, the page shows "Version x.y.z of Compose Wellness is
available." at the very top of the page, with a link to the release notes, the button
**Update Compose Wellness** and a small "Ignore this version" link. The button is disabled while
an update of your stacks is running. Ignoring hides the notice in that browser until a newer
release exists.
Pressing it:

1. The web service writes `/var/lib/compose-wellness/self-update.request`. That is all it can do:
   it runs as an unprivileged user and cannot replace its own files.
2. `compose-wellness-update.path`, a root-owned systemd unit installed by `install.sh`, notices
   the file and starts `compose-wellness-update.service`.
3. `self-update.sh` removes the trigger file, reads the update repository from the systemd
   configuration of the service (never from the browser), downloads
   `compose-wellness-<arch>.tar.gz` of the latest release, verifies it against the release's
   `SHA256SUMS` when present, and runs that release's `install.sh`. Existing settings are kept
   exactly as with a manual upgrade.
4. The page polls until the new version answers, then reloads. If nothing happens within three
   minutes it says so; check `journalctl -u compose-wellness-update` on the host.

The check runs 10 seconds after the service starts, every 6 hours, and when the page is opened
more than 10 minutes after the previous check, against
`https://api.github.com/repos/<repository>/releases/latest`. On a host without internet access
the check fails quietly and nothing is shown.

Settings (see the table under "What the install script does"):

- `ALLOW_SELF_UPDATE` (default `true`) shows the button and accepts the request. With `false` the
  version notice and link are still shown, without a button. The root helper checks the setting as
  well, so a trigger file created by other means is ignored while it is false.
- `UPDATE_REPOSITORY` (default `martingertsen/compose-wellness`) is the GitHub `owner/repo`
  whose releases are used. Empty disables the check entirely.

Because the page has no authentication, anyone who can reach it can start an upgrade. The
upgrade only ever installs the latest release of the repository configured by root, so the
consequence is limited to a restart and a newer version. Set `ALLOW_SELF_UPDATE=false` if even
that is unwanted.

#### Forks

Set `UPDATE_REPOSITORY=you/your-fork` when installing. The fork's releases must be tagged
`vX.Y.Z` and contain assets named `compose-wellness-linux-x64.tar.gz` and
`compose-wellness-linux-arm64.tar.gz`, which the included release workflow produces. A
`SHA256SUMS` asset is optional; without it the download is installed unverified.

Testing the helper without a newer release:

```bash
sudo touch /var/lib/compose-wellness/self-update.request
journalctl -u compose-wellness-update -n 20
```

The last line should read `Already up to date (x.y.z).`

### Build a release yourself

Use this instead of the steps above if you want to build from source, for example for `linux-arm64`.

From a clone of this repository:

```bash
./deploy/publish.sh              # linux-x64 (Intel/AMD)
./deploy/publish.sh linux-arm64  # Raspberry Pi 4/5 and other 64-bit ARM hosts
```

On Windows:

```powershell
.\deploy\publish.ps1
.\deploy\publish.ps1 -Rid linux-arm64
```

This produces `dist/compose-wellness-<rid>.tar.gz` containing the self-contained application,
`install.sh` and the systemd unit.

Copy the tarball to the host, then:

```bash
tar -xzf compose-wellness-linux-x64.tar.gz
cd compose-wellness-linux-x64
sudo ROOT_DIRECTORY=/srv/docker bash ./install.sh
```

### What the install script does

The same `install.sh` handles first installs and upgrades. It:

- creates a system user `compose-wellness` and adds it to the `docker` group
- copies the application to `/opt/compose-wellness`
- installs `/etc/systemd/system/compose-wellness.service`
- writes your settings to `/etc/systemd/system/compose-wellness.service.d/10-install.conf`
- enables and starts the service
- installs `compose-wellness-update.service` and `compose-wellness-update.path` (the self-update
  helper, see above) and enables the path unit

Variables understood by `install.sh`. A variable that is not passed keeps the value from the
existing drop-in, or the default on a first install:

| Variable            | Default                   | Meaning                                                     |
|---------------------|---------------------------|-------------------------------------------------------------|
| `ROOT_DIRECTORY`    | `/opt/stacks`             | Directory containing the stack directories                  |
| `LISTEN_URL`        | `http://127.0.0.1:5000`   | Address and port of the web UI                              |
| `SERVICE_USER`      | `compose-wellness`          | Account the service and all scripts run as                  |
| `ALLOW_ROOT_CHANGE` | `false`                   | Let the folder be changed from the web UI                   |
| `REMOVE_ORPHANS`    | `false`                   | Pass `--remove-orphans` to `docker compose up`              |
| `ALLOW_SELF_UPDATE` | `true`                    | Show the update button in the web UI                        |
| `UPDATE_REPOSITORY` | `martingertsen/compose-wellness` | GitHub `owner/repo` checked for releases; empty disables the check |
| `INSTALL_DIR`       | `/opt/compose-wellness`     | Where the application files are placed                      |

The script only writes `10-install.conf`. Put any other systemd or application settings in a
second drop-in such as `20-local.conf` in the same directory, which upgrades never touch.

Open the web UI at the listen URL, for example `http://127.0.0.1:5000`.

### About the service user

Everything Compose Wellness starts (`docker compose`, `update.sh`) runs as the service user. The default
`compose-wellness` account is a member of the `docker` group, which is enough for Compose. If your
`update.sh` scripts need to write to bind-mounted directories or run other privileged tools, set
`SERVICE_USER` to an account that has those permissions when installing.

Docker keeps registry logins per Linux user, in that user's home directory. A `docker login` you
ran as yourself therefore does not apply to the service. If a stack pulls images from a private
registry, log in once as the service user; the login is stored in `/var/lib/compose-wellness` and
survives upgrades:

```bash
sudo -u compose-wellness -H docker login ghcr.io
```

Public images from Docker Hub and other public registries need no login.

### Manual installation

If you prefer not to use the script: publish the application, copy the output to
`/opt/compose-wellness`, copy `deploy/compose-wellness.service` to `/etc/systemd/system/`, adjust the
`User=`, `Environment=ComposeWellness__RootDirectory=` and `Environment=ASPNETCORE_URLS=` lines, and run
`systemctl daemon-reload && systemctl enable --now compose-wellness`.

For the update button, also copy `deploy/compose-wellness-update.service` and
`deploy/compose-wellness-update.path` to `/etc/systemd/system/`, place `deploy/self-update.sh`
in the application directory, and run `systemctl enable --now compose-wellness-update.path`.
If you skip these files, also remove the `Environment=ComposeWellness__SelfUpdateTriggerFile=...`
line from the unit, or set `ComposeWellness__AllowSelfUpdate=false`; otherwise the button is
shown but pressing it has no effect because nothing watches the trigger file.

### Uninstall

```bash
sudo systemctl disable --now compose-wellness compose-wellness-update.path
sudo rm -rf /etc/systemd/system/compose-wellness.service /etc/systemd/system/compose-wellness.service.d \
            /etc/systemd/system/compose-wellness-update.service /etc/systemd/system/compose-wellness-update.service.d \
            /etc/systemd/system/compose-wellness-update.path /opt/compose-wellness
sudo systemctl daemon-reload
sudo userdel compose-wellness   # optional
```

## Configuration

Settings live in `appsettings.json` next to the executable and can be overridden with environment
variables, which is how the systemd drop-in configures the service.

```json
{
  "ComposeWellness": {
    "RootDirectory": "/opt/stacks",
    "LogDirectory": null,
    "MaxLogLines": 5000,
    "RetainedLogFiles": 20
  }
}
```

| Setting                          | Environment variable              | Description |
|----------------------------------|-----------------------------------|-------------|
| (not in appsettings.json)        | `ASPNETCORE_URLS`                 | Listen address. Defaults to `http://127.0.0.1:5000`, localhost only. Use `http://0.0.0.0:5000` to listen on all interfaces. Do not add a `Urls` key to appsettings.json: it would override the environment variable. |
| `ComposeWellness:RootDirectory`    | `ComposeWellness__RootDirectory`    | Absolute path whose immediate child directories are the stacks. A folder chosen in the web UI takes precedence. |
| `ComposeWellness:AllowRootDirectoryChange` | `ComposeWellness__AllowRootDirectoryChange` | `false` (default) shows the folder read-only in the web UI; `true` lets it be changed there. |
| `ComposeWellness:RemoveOrphans`    | `ComposeWellness__RemoveOrphans`    | `false` (default) runs `docker compose up -d`; `true` adds `--remove-orphans`. |
| `ComposeWellness:AllowSelfUpdate`  | `ComposeWellness__AllowSelfUpdate`  | `true` (default) shows the update button when a newer release exists; `false` shows only the notice. |
| `ComposeWellness:UpdateRepository` | `ComposeWellness__UpdateRepository` | GitHub `owner/repo` whose latest release is checked every 6 hours. Default `martingertsen/compose-wellness`; empty disables the check. |
| `ComposeWellness:DataDirectory`    | `ComposeWellness__DataDirectory`    | Where `settings.json` with UI changes is stored. The systemd unit sets `/var/lib/compose-wellness`; default is the application directory. |
| `ComposeWellness:LogDirectory`     | `ComposeWellness__LogDirectory`     | Directory for one log file per update. Empty disables disk logging. The systemd unit sets `/var/log/compose-wellness`. |
| `ComposeWellness:MaxLogLines`      | `ComposeWellness__MaxLogLines`      | Lines of the current update kept in memory for the web UI. |
| `ComposeWellness:RetainedLogFiles` | `ComposeWellness__RetainedLogFiles` | Number of update log files kept on disk; older ones are deleted. |

After changing the drop-in file run `sudo systemctl daemon-reload && sudo systemctl restart compose-wellness`.

Service output goes to the journal: `journalctl -u compose-wellness -f`.

## Security

Read this section before exposing the service beyond localhost.

Compose Wellness executes `docker compose` and arbitrary `update.sh` scripts on the host. Anyone who
can reach the web UI can start an update, and anyone who can write to a stack directory can decide
what runs during that update. Access to the Docker socket is equivalent to root on the host.

- The service has **no authentication** in this version. It binds to `127.0.0.1` by default so
  that only local processes can reach it.
- Only expose it on a network you trust completely, or put it behind a reverse proxy that adds
  authentication (for example nginx with basic auth or an identity-aware proxy) and keep the
  service itself bound to localhost.
- The HTTP API takes no commands from the browser. Its three mutable actions are "start an
  update", "change the scanned folder" and "upgrade Compose Wellness to the latest release of the
  configured repository". All require a custom request header, so another website open in your
  browser cannot trigger them. The upgrade is performed by a root-owned systemd unit that reads
  its configuration from root-owned files only; the browser cannot choose what gets installed.
- Changing the folder from the UI is off by default. When enabled, anyone who can reach the page
  can point Compose Wellness at any existing directory on the host, including world-writable ones such
  as `/tmp`, and place an `update.sh` there. Only enable it where you alone can reach the page.
- Anyone who can write to the root directory can place an `update.sh` that runs as the service
  user on the next update. If the directory is a NAS share, make sure only trusted devices can
  write to it.
- Keep the root directory writable only by trusted users; a writable `update.sh` there is a
  command execution path.

## HTTP API

| Method | Path                        | Description |
|--------|-----------------------------|-------------|
| `GET`  | `/api/status`               | Current state, results so far and summary when finished. |
| `GET`  | `/api/stacks`               | Directories the next update would process (those with an `update.sh` or a Compose file), without asking Docker. |
| `GET`  | `/api/settings`             | Current and configured root directory, whether it can be changed, the application version, and the latest release (`latestVersion`, `releaseUrl`, `updateAvailable`, `canSelfUpdate`, `updateRepository`, `checking`). |
| `PUT`  | `/api/settings/root-directory` | Body `{ "rootDirectory": "/abs/path" }`. Requires the `X-Requested-With: ComposeWellness` header. `400` for a missing directory, `403` when disabled, `409` while an update runs. |
| `POST` | `/api/update`               | Starts an update. Requires the header `X-Requested-With: ComposeWellness` (CSRF guard), otherwise `403`. Returns `202 Accepted`, or `409 Conflict` when one is already running. |
| `POST` | `/api/self-update`          | Asks the root helper to upgrade to the latest release. Requires the `X-Requested-With: ComposeWellness` header. `202` with `{ "latestVersion": "1.2.0" }`, `403` when disabled or the helper is not installed, `409` while an update runs or when no newer version is known. |
| `GET`  | `/api/log?after=<sequence>` | Retained log lines with a sequence number greater than `after`. |
| `GET`  | `/api/events?after=<sequence>` | Server-Sent Events stream: a `status` event, replay of retained `log` events, then live events. Browsers resume with the standard `Last-Event-ID` header. |

Example status:

```json
{
  "state": "running",
  "sessionId": "579afab7-b632-49cb-88d9-c792431d4b15",
  "startedAt": "2026-09-14T15:31:29.747+02:00",
  "finishedAt": null,
  "currentStack": "proxy",
  "results": [
    { "name": "backend", "outcome": "success", "duration": "00:00:08.1234567", "message": "Recreated 1 of 1 container", "recreatedContainers": 1 },
    { "name": "frontend", "outcome": "success", "duration": "00:00:03.5000000", "message": "No change", "recreatedContainers": 0 }
  ],
  "summary": null,
  "lastSequence": 42
}
```

`state` is one of `idle`, `running`, `finished` (no failures) or `failed`. `outcome` is one of
`success`, `failed` or `skipped`. When the update has finished, `summary` holds
`{ "successful": 2, "failed": 0, "skipped": 1, "recreated": 1, "duration": "00:02:41.0000000" }`,
where `recreated` counts stacks in which at least one container was recreated.

## Development

```bash
dotnet test                                   # unit tests (no Docker needed)
dotnet run --project src/ComposeWellness -- --ComposeWellness:RootDirectory=/path/to/stacks
```

Project layout:

```text
src/ComposeWellness/
├── Program.cs                      Minimal API, static files, wiring
├── Configuration/                  Strongly typed options
├── Models/                         States, results, log entries
├── Services/
│   ├── RootDirectoryProvider.cs    Configured or UI chosen root directory, persisted in settings.json
│   ├── StackDiscoveryService.cs    Non-recursive directory scan and classification
│   ├── ProcessRunner.cs            Starts executables directly and streams their output
│   ├── StackUpdateService.cs       update.sh or the Compose procedure for one stack
│   ├── UpdateCoordinator.cs        The single global update, in-memory log, event fan-out
│   └── UpdateLogArchive.cs         Optional per-update log files with retention
├── Web/EventStream.cs              Server-Sent Events endpoint
└── wwwroot/                        Plain HTML, CSS and JavaScript
tests/ComposeWellness.Tests/          xUnit tests with fake process runner
deploy/                             systemd unit, install and publish scripts
assets/                             Project icon
.github/workflows/                  CI (build and test) and release (tarballs per architecture)
```

Releases: tag a commit `vX.Y.Z` and push the tag. The release workflow runs the tests, builds
the linux-x64 and linux-arm64 tarballs and attaches them to the GitHub release. Update
`CHANGELOG.md` and the `Version` in `ComposeWellness.csproj` first.

## Not included in this version

Deliberately left out to keep the first release small: authentication, updating single stacks,
scheduling, dry runs, notifications, `.no-update` marker files, image pruning and update history
beyond the log files on disk.

## License

MIT, see [LICENSE](LICENSE).
