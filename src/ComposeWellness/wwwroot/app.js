(function () {
  "use strict";

  const elements = {
    status: document.getElementById("status"),
    currentStack: document.getElementById("current-stack"),
    button: document.getElementById("update-all"),
    message: document.getElementById("message"),
    console: document.getElementById("console"),
    autoscroll: document.getElementById("autoscroll"),
    summary: document.getElementById("summary"),
    summaryTitle: document.getElementById("summary-title"),
    countSuccess: document.getElementById("count-success"),
    countFailed: document.getElementById("count-failed"),
    countSkipped: document.getElementById("count-skipped"),
    countRecreated: document.getElementById("count-recreated"),
    countDuration: document.getElementById("count-duration"),
    startedAt: document.getElementById("started-at"),
    finishedAt: document.getElementById("finished-at"),
    resultsBody: document.querySelector("#results tbody"),
    themeToggle: document.getElementById("theme-toggle"),
    stacks: document.getElementById("stacks"),
    refreshStacks: document.getElementById("refresh-stacks"),
    rootDisplay: document.getElementById("root-display"),
    rootPath: document.getElementById("root-path"),
    rootForm: document.getElementById("root-form"),
    rootInput: document.getElementById("root-input"),
    rootSave: document.getElementById("root-save"),
    rootCancel: document.getElementById("root-cancel"),
    version: document.getElementById("version"),
    selfUpdate: document.getElementById("self-update"),
    selfUpdateText: document.getElementById("self-update-text"),
    selfUpdateLink: document.getElementById("self-update-link"),
    selfUpdateButton: document.getElementById("self-update-button"),
  };

  // Root directory: shown in the top bar, editable in place when the server allows it.
  let rootDirectory = "";
  // Version at page load; a different value from /api/settings later means the upgrade landed.
  let runningVersion = null;
  // True from the moment the server accepted a self-update until it succeeded or timed out.
  let selfUpdating = false;
  // True while an Update All runs; the server refuses a self-update then, so the button follows suit.
  let updateRunning = false;

  function reflectSelfUpdateButton() {
    elements.selfUpdateButton.disabled = selfUpdating || updateRunning;
    elements.selfUpdateButton.title = updateRunning ? "Wait for the running update to finish" : "";
  }

  async function loadSettings() {
    try {
      const response = await fetch("/api/settings");
      if (!response.ok) {
        throw new Error("HTTP " + response.status);
      }
      const settings = await response.json();
      rootDirectory = settings.rootDirectory;
      runningVersion = settings.version || null;
      elements.rootPath.textContent = rootDirectory;
      elements.version.textContent = settings.version ? "Compose Wellness " + settings.version + "." : "";
      elements.rootDisplay.disabled = !settings.canChangeRootDirectory;
      elements.rootDisplay.title = settings.canChangeRootDirectory
        ? "Click to change the folder that is scanned for stacks"
        : "Changing the folder is disabled by configuration";
      renderUpdateNotice(settings);
    } catch (error) {
      elements.rootPath.textContent = "unknown";
      elements.rootDisplay.disabled = true;
    }
  }

  function showRootEditor(show) {
    elements.rootDisplay.hidden = show;
    elements.rootForm.hidden = !show;
    if (show) {
      elements.rootInput.value = rootDirectory;
      elements.rootInput.focus();
      elements.rootInput.select();
    } else if (elements.message.classList.contains("error")) {
      // A validation error from a failed save belongs to the editor; closing it clears the error.
      setMessage("");
    }
  }

  async function saveRootDirectory(event) {
    event.preventDefault();
    const value = elements.rootInput.value.trim();
    if (value === rootDirectory) {
      showRootEditor(false);
      return;
    }

    elements.rootSave.disabled = true;
    try {
      const response = await fetch("/api/settings/root-directory", {
        method: "PUT",
        headers: { "Content-Type": "application/json", "X-Requested-With": "ComposeWellness" },
        body: JSON.stringify({ rootDirectory: value }),
      });
      const body = await response.json().catch(function () { return {}; });
      if (!response.ok) {
        setMessage(body.error || ("The server returned HTTP " + response.status + "."), true);
        return;
      }
      rootDirectory = body.rootDirectory;
      elements.rootPath.textContent = rootDirectory;
      showRootEditor(false);
      setMessage("Folder changed to " + rootDirectory + ".");
      loadStacks();
    } catch (error) {
      setMessage("Could not reach the server: " + error.message, true);
    } finally {
      elements.rootSave.disabled = false;
    }
  }

  elements.rootDisplay.addEventListener("click", function () { showRootEditor(true); });
  elements.rootCancel.addEventListener("click", function () { showRootEditor(false); });
  elements.rootInput.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      showRootEditor(false);
    }
  });
  elements.rootForm.addEventListener("submit", saveRootDirectory);
  loadSettings();

  // Self-update: the server only writes a trigger file; a root-owned systemd unit does the rest
  // and restarts the service, so this page waits for the version to change and then reloads.
  function renderUpdateNotice(settings) {
    if (selfUpdating || !settings.updateAvailable || !settings.latestVersion) {
      if (!selfUpdating) {
        elements.selfUpdate.hidden = true;
      }
      return;
    }
    elements.selfUpdate.classList.remove("error");
    elements.selfUpdateText.textContent = "Version " + settings.latestVersion + " of Compose Wellness is available.";
    elements.selfUpdateLink.hidden = !settings.releaseUrl;
    if (settings.releaseUrl) {
      elements.selfUpdateLink.href = settings.releaseUrl;
    }
    elements.selfUpdateButton.hidden = !settings.canSelfUpdate;
    reflectSelfUpdateButton();
    elements.selfUpdate.hidden = false;
  }

  function setUpdateNotice(text, isError) {
    elements.selfUpdateText.textContent = text;
    elements.selfUpdate.classList.toggle("error", Boolean(isError));
    elements.selfUpdate.hidden = false;
  }

  async function startSelfUpdate() {
    const confirmed = window.confirm("Compose Wellness will download the latest release, restart and reload this page. Continue?");
    if (!confirmed) {
      return;
    }

    elements.selfUpdateButton.disabled = true;
    elements.button.disabled = true;
    try {
      const response = await fetch("/api/self-update", { method: "POST", headers: { "X-Requested-With": "ComposeWellness" } });
      const body = await response.json().catch(function () { return {}; });
      if (!response.ok) {
        setUpdateNotice(body.error || ("The server returned HTTP " + response.status + "."), true);
        reflectSelfUpdateButton();
        elements.button.disabled = false;
        return;
      }
      selfUpdating = true;
      setUpdateNotice("Updating to " + body.latestVersion + ", this takes about a minute. The page reloads when the new version is running.");
      waitForNewVersion(Date.now() + 3 * 60 * 1000);
    } catch (error) {
      setUpdateNotice("Could not reach the server: " + error.message, true);
      reflectSelfUpdateButton();
      elements.button.disabled = false;
    }
  }

  function waitForNewVersion(deadline) {
    setTimeout(async function () {
      try {
        const response = await fetch("/api/settings", { cache: "no-store" });
        if (response.ok) {
          const settings = await response.json();
          if (settings.version && runningVersion !== null && settings.version !== runningVersion) {
            window.location.reload();
            return;
          }
        }
      } catch (error) {
        // The service is restarting; keep polling.
      }
      if (Date.now() < deadline) {
        waitForNewVersion(deadline);
        return;
      }
      selfUpdating = false;
      setUpdateNotice("The update did not finish. On the host run: journalctl -u compose-wellness-update", true);
      reflectSelfUpdateButton();
      elements.button.disabled = false;
    }, 2000);
  }

  elements.selfUpdateButton.addEventListener("click", startSelfUpdate);

  // Detected stacks: what Update All would process right now.
  async function loadStacks() {
    elements.refreshStacks.disabled = true;
    elements.refreshStacks.classList.add("spinning");
    try {
      const response = await fetch("/api/stacks");
      if (!response.ok) {
        throw new Error("HTTP " + response.status);
      }
      renderStacks(await response.json());
    } catch (error) {
      renderStacksMessage("Could not load the stack list: " + error.message);
    } finally {
      elements.refreshStacks.disabled = false;
      elements.refreshStacks.classList.remove("spinning");
    }
  }

  function renderStacks(data) {
    if (data.error) {
      renderStacksMessage(data.error);
      return;
    }
    if (data.rootDirectory && data.rootDirectory !== rootDirectory) {
      // Another browser changed the folder; keep the top bar in sync.
      rootDirectory = data.rootDirectory;
      elements.rootPath.textContent = rootDirectory;
    }
    if (data.stacks.length === 0) {
      renderStacksMessage("No stacks found in " + data.rootDirectory + ". A stack is a directory containing an update.sh or a Compose file.");
      return;
    }

    elements.stacks.textContent = "";
    for (const stack of data.stacks) {
      const chip = document.createElement("span");
      chip.className = "stack-chip";
      chip.title = stack.kind === "customScript" ? "Updated by its own update.sh" : "Compose file: " + stack.composeFile;
      chip.appendChild(document.createTextNode(stack.name));
      if (stack.kind === "customScript") {
        const tag = document.createElement("span");
        tag.className = "tag";
        tag.textContent = "script";
        chip.appendChild(tag);
      }
      elements.stacks.appendChild(chip);
    }
  }

  function renderStacksMessage(text) {
    elements.stacks.textContent = "";
    const message = document.createElement("span");
    message.className = "empty";
    message.textContent = text;
    elements.stacks.appendChild(message);
  }

  elements.refreshStacks.addEventListener("click", loadStacks);
  loadStacks();

  // Theme: follow the browser unless the user toggled manually during this browser session.
  const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");

  function isDarkActive() {
    const forced = document.documentElement.getAttribute("data-theme");
    return forced ? forced === "dark" : darkQuery.matches;
  }

  function reflectTheme() {
    const dark = isDarkActive();
    document.documentElement.classList.toggle("is-dark", dark);
    const label = dark ? "Switch to light mode" : "Switch to dark mode";
    elements.themeToggle.setAttribute("aria-label", label);
    elements.themeToggle.title = label;
  }

  function toggleTheme() {
    const next = isDarkActive() ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", next);
    try {
      sessionStorage.setItem("theme", next);
    } catch (error) {
      // Storage unavailable: the choice still applies until the page is reloaded.
    }
    reflectTheme();
  }

  darkQuery.addEventListener("change", reflectTheme);
  elements.themeToggle.addEventListener("click", toggleTheme);
  reflectTheme();

  // Sequence number of the last log line rendered. Sent to the server so a reconnect only
  // receives what this page has not shown yet.
  let lastSequence = 0;
  let currentSessionId = null;
  let eventSource = null;

  const statusLabels = {
    idle: "Idle",
    running: "Updating",
    finished: "Finished",
    failed: "Failed",
  };

  function pad(value) {
    return String(value).padStart(2, "0");
  }

  function formatTime(isoString) {
    const date = new Date(isoString);
    return pad(date.getHours()) + ":" + pad(date.getMinutes()) + ":" + pad(date.getSeconds());
  }

  function formatDateTime(isoString) {
    const date = new Date(isoString);
    return date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate()) + " " + formatTime(isoString);
  }

  // .NET serializes TimeSpan as "[d.]hh:mm:ss[.fffffff]".
  function formatDuration(timeSpan) {
    const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/.exec(timeSpan || "");
    if (!match) {
      return "-";
    }
    const hours = Number(match[1] || 0) * 24 + Number(match[2]);
    const minutes = Number(match[3]);
    const seconds = Number(match[4]);
    return hours > 0 ? pad(hours) + ":" + pad(minutes) + ":" + pad(seconds) : pad(minutes) + ":" + pad(seconds);
  }

  function setMessage(text, isError) {
    elements.message.textContent = text || "";
    elements.message.classList.toggle("error", Boolean(isError));
  }

  // Compose prints a new line for every progress step of every image layer ("<id> Downloading
  // 6.9MB", "<id> Extracting 15MB", ...). The server log keeps them all; the console shows one
  // live line per layer instead, the way a terminal would overwrite it. Layers are keyed by the
  // 12 character id at the start of the line, and the map is forgotten at every app line so a
  // later command that mentions the same id gets a fresh line.
  const layerLinePattern = /^\s*([0-9a-f]{12})\s+\S/;
  const layerLines = new Map();

  function clearConsole() {
    elements.console.textContent = "";
    layerLines.clear();
  }

  function appendLogEntries(entries) {
    if (entries.length === 0) {
      return;
    }

    const fragment = document.createDocumentFragment();
    for (const entry of entries) {
      lastSequence = Math.max(lastSequence, entry.sequence);

      if (entry.source !== "app") {
        const match = layerLinePattern.exec(entry.text);
        if (match) {
          const existing = layerLines.get(match[1]);
          if (existing) {
            existing.textContent = entry.text;
            continue;
          }
        }
        const out = document.createElement("span");
        out.className = "out";
        out.textContent = entry.text;
        if (match) {
          layerLines.set(match[1], out);
        }
        fragment.appendChild(out);
        fragment.appendChild(document.createTextNode("\n"));
        continue;
      }

      layerLines.clear();
      const line = document.createElement("span");
      line.className = entry.text.startsWith("===") ? "app heading" : "app";
      line.textContent = entry.text === "" ? "" : "[" + formatTime(entry.timestamp) + "] " + entry.text;
      fragment.appendChild(line);
      fragment.appendChild(document.createTextNode("\n"));
    }

    elements.console.appendChild(fragment);

    if (elements.autoscroll.checked) {
      elements.console.scrollTop = elements.console.scrollHeight;
    }
  }

  function renderStatus(status) {
    if (status.sessionId && status.sessionId !== currentSessionId) {
      // A new update started (possibly while this page was disconnected); its log replaces the old one.
      currentSessionId = status.sessionId;
      clearConsole();
      elements.summary.hidden = true;
      // The update just discovered the stacks itself, so the preview is refreshed to match.
      if (status.state === "running") {
        loadStacks();
      }
    }

    elements.status.textContent = statusLabels[status.state] || status.state;
    elements.status.className = "badge " + status.state;
    elements.currentStack.textContent = status.state === "running" && status.currentStack ? status.currentStack : "";
    updateRunning = status.state === "running";
    elements.button.disabled = updateRunning || selfUpdating;
    reflectSelfUpdateButton();

    if (status.state === "running") {
      setMessage("Update in progress. Closing this page does not stop it.");
    } else if (status.state === "idle") {
      setMessage("");
    }

    if (status.summary) {
      renderSummary(status);
    }
  }

  function renderSummary(status) {
    const summary = status.summary;
    elements.summaryTitle.textContent = status.state === "failed" ? "Update finished with failures" : "Update complete";
    elements.countSuccess.textContent = summary.successful;
    elements.countFailed.textContent = summary.failed;
    elements.countSkipped.textContent = summary.skipped;
    elements.countRecreated.textContent = summary.recreated;
    elements.countDuration.textContent = formatDuration(summary.duration);
    elements.startedAt.textContent = status.startedAt ? "Started " + formatDateTime(status.startedAt) + "." : "";
    elements.finishedAt.textContent = status.finishedAt ? "Finished " + formatDateTime(status.finishedAt) + "." : "";

    elements.resultsBody.textContent = "";
    for (const result of status.results) {
      const row = document.createElement("tr");
      row.appendChild(cell("name", result.name));
      row.appendChild(cell("result " + result.outcome, result.outcome.toUpperCase()));
      row.appendChild(cell("duration", result.outcome === "skipped" ? "-" : formatDuration(result.duration)));
      row.appendChild(cell("details", result.message || ""));
      elements.resultsBody.appendChild(row);
    }

    elements.summary.hidden = false;
    setMessage("");
  }

  function cell(className, text) {
    const td = document.createElement("td");
    td.className = className;
    td.textContent = text;
    return td;
  }

  function connect() {
    if (eventSource) {
      eventSource.close();
    }

    // The browser reconnects on its own and sends the last event id it saw, so the query
    // parameter only matters for the very first connection.
    eventSource = new EventSource("/api/events?after=" + lastSequence);

    eventSource.addEventListener("status", function (event) {
      renderStatus(JSON.parse(event.data));
    });

    eventSource.addEventListener("log", function (event) {
      appendLogEntries([JSON.parse(event.data)]);
    });

    eventSource.addEventListener("open", function () {
      if (elements.status.textContent === "Disconnected") {
        setMessage("");
      }
    });

    eventSource.addEventListener("error", function () {
      elements.status.textContent = "Disconnected";
      elements.status.className = "badge";
      setMessage("Connection to the server lost. Reconnecting...", true);
    });
  }

  async function startUpdate() {
    elements.button.disabled = true;
    setMessage("Starting update...");

    try {
      // The server requires this header as a CSRF guard; see Program.cs.
      const response = await fetch("/api/update", { method: "POST", headers: { "X-Requested-With": "ComposeWellness" } });
      if (response.status === 409) {
        setMessage("An update is already running.", true);
      } else if (!response.ok) {
        setMessage("The server returned HTTP " + response.status + ".", true);
        elements.button.disabled = false;
      }
    } catch (error) {
      setMessage("Could not reach the server: " + error.message, true);
      elements.button.disabled = false;
    }
  }

  elements.button.addEventListener("click", startUpdate);
  connect();
})();
