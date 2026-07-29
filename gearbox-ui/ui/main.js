const { invoke } = window.__TAURI__.core;
const { getCurrentWindow } = window.__TAURI__.window;

const NEUTRAL = "N";
const POLL_MS = 5000;

const app = document.getElementById("app");
const foot = document.getElementById("foot");
const dot = document.getElementById("dot");
const pinButton = document.getElementById("pin");
const settingsToggle = document.getElementById("settings-toggle");
const settingsForm = document.getElementById("settings");
const baseUrlInput = document.getElementById("base-url");

let current = null;
let busy = false;
let pinned = false;
let errored = false;

const READY_MESSAGE = "Shift a gear to re-route every request to that model.";

// ----------------------------------------------------------------------
// Rendering
// ----------------------------------------------------------------------

function say(message, isError = false) {
  foot.textContent = message;
  foot.className = isError ? "foot err" : "foot";
  errored = isError;
}

function online(isOnline) {
  dot.className = isOnline === null ? "dot" : isOnline ? "dot online" : "dot offline";
}

function notice(html) {
  const div = document.createElement("div");
  div.className = "notice";
  div.append(...html);
  app.replaceChildren(div);
}

function cellButton(position, label, { active, neutral }) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "cell";
  if (neutral) button.classList.add("neutral-cell");
  if (active) button.classList.add("active");
  button.disabled = busy;

  const pos = document.createElement("span");
  pos.className = "pos";
  pos.textContent = position;

  const lbl = document.createElement("span");
  lbl.className = "lbl";
  lbl.textContent = label ?? "";

  button.append(pos, lbl);
  button.addEventListener("click", () => shift(position));
  return button;
}

function render(state) {
  current = state;

  if (!state.enabled) {
    const code = document.createElement("code");
    code.textContent = "Gearbox.Enabled";
    notice([
      "Gearbox is disabled.",
      document.createElement("br"),
      "Set ", code, " to true in appsettings.json."
    ]);
    return;
  }

  const gears = state.gears ?? [];
  const selected = state.selected ?? NEUTRAL;
  const isNeutral = selected.toUpperCase() === (state.neutral ?? NEUTRAL).toUpperCase();
  const active = gears.find(g => g.position.toLowerCase() === selected.toLowerCase());

  // Status readout.
  const status = document.createElement("div");
  status.className = "status";

  const gearLine = document.createElement("div");
  gearLine.className = "gear";
  gearLine.append("GEAR · ");
  const bold = document.createElement("b");
  bold.textContent = isNeutral ? NEUTRAL : selected;
  gearLine.append(bold);

  const modelLine = document.createElement("div");
  modelLine.className = isNeutral ? "model neutral" : "model";
  modelLine.textContent = isNeutral
    ? "Neutral · client picks the model"
    : (active ? (active.model || active.label || active.position) : "—");

  status.append(gearLine, modelLine);

  // H-pattern: odd gears on the top row, even on the bottom, Neutral spanning the middle.
  const top = [];
  const bottom = [];
  gears.forEach((g, i) => (i % 2 === 0 ? top : bottom).push(g));
  const columns = Math.max(top.length, bottom.length, 1);

  const makeRow = list => {
    const row = document.createElement("div");
    row.className = "row";
    row.style.gridTemplateColumns = `repeat(${columns}, 1fr)`;
    list.forEach(g => {
      const button = cellButton(g.position, g.label || g.model || "", {
        active: active && g.position.toLowerCase() === active.position.toLowerCase(),
        neutral: false
      });
      button.title = g.model || "";
      row.append(button);
    });
    for (let i = list.length; i < columns; i++) {
      row.append(document.createElement("span"));
    }
    return row;
  };

  const shifter = document.createElement("div");
  shifter.className = "shifter";
  shifter.append(
    makeRow(top),
    cellButton(NEUTRAL, "Neutral", { active: isNeutral, neutral: true }),
    makeRow(bottom)
  );

  app.replaceChildren(status, shifter);
}

// ----------------------------------------------------------------------
// Proxy calls
// ----------------------------------------------------------------------

async function refresh(quiet = false) {
  if (busy) return;
  try {
    render(await invoke("fetch_state"));
    online(true);
    // A quiet poll leaves the footer alone, except when it is still showing a stale
    // error from a previous failure — the proxy is clearly back now.
    if (!quiet || errored) say(READY_MESSAGE);
  } catch (e) {
    online(false);
    say(String(e), true);
  }
}

async function shift(position) {
  if (busy) return;
  busy = true;
  if (current) render(current); // disable the pad while the shift is in flight
  try {
    render(await invoke("shift", { position }));
    online(true);
    say(`Engaged ${position}.`);
  } catch (e) {
    online(false);
    say(String(e), true);
  } finally {
    busy = false;
    if (current) render(current);
  }
}

// ----------------------------------------------------------------------
// Chrome
// ----------------------------------------------------------------------

pinButton.addEventListener("click", async () => {
  pinned = !pinned;
  pinButton.classList.toggle("on", pinned);
  await getCurrentWindow().setAlwaysOnTop(pinned);
});

settingsToggle.addEventListener("click", () => {
  settingsForm.hidden = !settingsForm.hidden;
  settingsToggle.classList.toggle("on", !settingsForm.hidden);
  if (!settingsForm.hidden) baseUrlInput.focus();
});

settingsForm.addEventListener("submit", async event => {
  event.preventDefault();
  try {
    const saved = await invoke("save_settings", { baseUrl: baseUrlInput.value });
    baseUrlInput.value = saved.baseUrl;
    settingsForm.hidden = true;
    settingsToggle.classList.remove("on");
    await refresh();
  } catch (e) {
    say(String(e), true);
  }
});

// Number keys engage a gear, N returns to Neutral. Unknown positions are ignored rather than
// sent to the proxy just to come back as a 404.
document.addEventListener("keydown", event => {
  if (event.target === baseUrlInput || !current?.enabled) return;
  const key = event.key.toUpperCase();
  const known = key === NEUTRAL
    || (current.gears ?? []).some(g => g.position.toUpperCase() === key);
  if (known) shift(key);
});

async function start() {
  const settings = await invoke("load_settings");
  baseUrlInput.value = settings.baseUrl;
  await refresh();
  setInterval(() => refresh(true), POLL_MS);
}

start();
