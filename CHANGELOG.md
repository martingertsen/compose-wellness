# Changelog

All notable changes to Compose Wellness. Versions follow [Semantic Versioning](https://semver.org/).

## 1.1.2 - 2026-09-16

- The live console shows one line per image layer during `docker compose pull`, overwritten as
  the download progresses, instead of a new line for every progress step. Log files are unchanged.

## 1.1.1 - 2026-09-15

- Maintenance release used to exercise the new self-update button. No functional changes.

## 1.1.0 - 2026-09-15

- The page checks GitHub for a newer release and shows the available version above the footer.
- **Update Compose Wellness** button: a root-owned systemd path unit installed
  by `install.sh` downloads the latest release and runs its `install.sh`. No console needed.
  Configurable with `ALLOW_SELF_UPDATE` (default true) and `UPDATE_REPOSITORY` (default
  `martingertsen/compose-wellness`, empty disables the check). Upgrading from 1.0.0 needs one
  manual `install.sh` run to install the helper units.
- `ComposeWellness --version` prints the version.
- Releases include a `SHA256SUMS` file; the self-update verifies the download against it.
- `GET /api/settings` reports `latestVersion`, `releaseUrl`, `updateAvailable`, `canSelfUpdate`
  and `updateRepository`. New `POST /api/self-update`.

## 1.0.0 - 2026-09-15

First public release.

- One button, **Update All**, updates every Docker Compose stack found directly below a
  configurable root directory. Discovery is dynamic and never recursive.
- An `update.sh` in a stack directory takes over the update of that stack.
- Standard stacks run `docker compose pull` and `docker compose up -d`; `--remove-orphans` is
  optional (`RemoveOrphans`, off by default). Completely stopped stacks are skipped.
- The result of every stack shows whether containers were actually recreated, detected by
  comparing container ids before and after the update.
- Live console output in the browser over Server-Sent Events, with reconnect and replay. Stack
  headings show progress, for example `=== backend === [3 of 19]`.
- One failing stack never stops the others. Only one update runs at a time, enforced on the server.
- Detected stacks are listed on the page with a refresh button. The scanned folder is shown in the
  top bar and can optionally be changed there (`AllowRootDirectoryChange`, off by default); the
  choice is persisted in `settings.json`.
- Dark mode follows the browser, with a toggle for the current browser session. The application
  version is shown in the page footer and returned by `GET /api/settings`.
- Runs directly on the Linux host as a systemd service, so it can recreate every container
  including management tools. Self-contained releases for linux-x64 and linux-arm64 are built by
  GitHub Actions; `install.sh` installs and upgrades in place and keeps existing settings.
- Security: binds to localhost by default, CSRF guard on state-changing requests, no paths or
  commands accepted from the browser, executables started without a shell.
- Optional per-update log files on disk with retention. No automatic image pruning.
