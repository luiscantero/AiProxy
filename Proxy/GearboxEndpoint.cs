using System.Text.Json;
using AiProxy.Auth;
using AiProxy.Pipeline.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Proxy;

/// <summary>
/// Web UI + control API for the gearbox ("model shift") middleware.
///
/// <list type="bullet">
///   <item><c>GET  /gearbox</c>       — a self-contained HTML shifter the user opens in a browser.</item>
///   <item><c>GET  /gearbox/state</c> — JSON describing the configured gears and the engaged one.</item>
///   <item><c>POST /gearbox/shift</c> — <c>{ "position": "3" }</c> engages a gear (or "N" for Neutral).</item>
/// </list>
///
/// The engaged gear is held in <see cref="GearboxState"/>; the middleware reads it per request.
/// </summary>
public static partial class GearboxEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Serves the gearbox control page.</summary>
    public static IResult Page() => Results.Content(HtmlPage, "text/html; charset=utf-8");

    /// <summary>Returns the configured gears plus the currently engaged position.</summary>
    public static IResult GetState(IOptions<AiProxyOptions> options, GearboxState state) =>
        Results.Json(BuildState(options.Value.Gearbox, state), JsonOptions);

    /// <summary>Engages a gear by position (or "N" for Neutral) and returns the updated state.</summary>
    public static async Task<IResult> Shift(
        HttpContext context,
        IOptions<AiProxyOptions> options,
        GearboxState state,
        ILogger<GearboxState> logger)
    {
        var gearbox = options.Value.Gearbox;

        ShiftRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ShiftRequest>(
                context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "Invalid JSON body." }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);
        }

        var position = body?.Position?.Trim();
        if (string.IsNullOrEmpty(position))
        {
            return Results.Json(new { error = "Missing 'position'." }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);
        }

        var isNeutral = string.Equals(position, GearboxState.Neutral, StringComparison.OrdinalIgnoreCase);
        var known = isNeutral || gearbox.Gears.Any(
            g => string.Equals(g.Position, position, StringComparison.OrdinalIgnoreCase));

        if (!known)
        {
            return Results.Json(new { error = $"Unknown gear position '{position}'." }, JsonOptions, statusCode: StatusCodes.Status404NotFound);
        }

        state.Selected = isNeutral ? GearboxState.Neutral : position;
        LogGearChanged(logger, GearboxMiddleware.DescribeSelection(gearbox, state));
        return Results.Json(BuildState(gearbox, state), JsonOptions);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Gearbox shifted; engaged gear: {Selection}.")]
    private static partial void LogGearChanged(ILogger logger, string selection);

    private static object BuildState(GearboxOptions gearbox, GearboxState state) => new
    {
        enabled = gearbox.Enabled,
        selected = state.Selected,
        neutral = GearboxState.Neutral,
        gears = gearbox.Gears.Select(g => new
        {
            position = g.Position,
            label = g.Label,
            model = g.Model
        })
    };

    private sealed class ShiftRequest
    {
        public string? Position { get; set; }
    }

    // A self-contained page (no external assets) so it works fully offline on the loopback proxy.
    private const string HtmlPage = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>AiProxy · Model Shift</title>
<style>
  :root {
    --bg: #0e1116;
    --panel: #171b22;
    --panel-2: #1f242d;
    --edge: #2b323d;
    --text: #d7dce3;
    --muted: #8b93a1;
    --accent: #f2b705;
    --accent-dim: #7a6413;
    --green: #35c46a;
    --knob-1: #e9edf2;
    --knob-2: #aab2bd;
    --knob-3: #5c636e;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: grid; place-items: center;
    font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
    color: var(--text);
    background: radial-gradient(1200px 800px at 50% -10%, #1b2430, #0e1116 60%);
  }
  .box {
    width: 340px; padding: 18px; border-radius: 18px;
    background: linear-gradient(180deg, var(--panel), var(--panel-2));
    border: 1px solid var(--edge);
    box-shadow: 0 24px 60px rgba(0,0,0,.55), inset 0 1px 0 rgba(255,255,255,.04);
  }
  .head {
    display: flex; align-items: center; justify-content: space-between;
    letter-spacing: .28em; font-size: 12px; font-weight: 600; color: var(--muted);
    text-transform: uppercase; margin-bottom: 14px;
  }
  .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--accent);
    box-shadow: 0 0 10px var(--accent); }
  .status {
    background: #10141a; border: 1px solid var(--edge); border-radius: 12px;
    padding: 12px 14px; margin-bottom: 16px;
  }
  .status .gear { font-size: 13px; letter-spacing: .18em; color: var(--muted); }
  .status .gear b { color: var(--accent); }
  .status .model {
    margin-top: 6px; font-size: 15px; font-weight: 600; color: var(--text);
    overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  }
  .status .model.neutral { color: var(--green); }
  .shifter {
    position: relative; background: #0b0e13; border: 1px solid var(--edge);
    border-radius: 14px; padding: 18px; display: grid; gap: 10px;
  }
  .row { display: grid; gap: 10px; }
  .cell {
    appearance: none; cursor: pointer; color: var(--text);
    background: linear-gradient(180deg, #232833, #171c25);
    border: 1px solid var(--edge); border-radius: 12px;
    padding: 12px 8px; text-align: center; transition: transform .08s, border-color .15s, box-shadow .15s;
    display: flex; flex-direction: column; gap: 3px; align-items: center;
    /* Grid items default to min-content width; without this a long label widens the column. */
    min-width: 0;
  }
  .cell:hover { border-color: #3b93ff55; transform: translateY(-1px); }
  .cell .pos { font-size: 18px; font-weight: 700; line-height: 1; }
  .cell .lbl {
    font-size: 10px; letter-spacing: .12em; text-transform: uppercase; color: var(--muted);
    width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  }
  .cell.active {
    border-color: var(--accent);
    background: radial-gradient(120px 60px at 50% 0%, #3a3413, #171c25);
    box-shadow: 0 0 0 1px var(--accent), 0 10px 24px rgba(242,183,5,.18);
  }
  .cell.active .pos { color: var(--accent); }
  .neutral-cell {
    grid-column: 1 / -1;
    background: linear-gradient(180deg, #1a212b, #10161e);
  }
  .neutral-cell.active {
    border-color: var(--green);
    box-shadow: 0 0 0 1px var(--green), 0 10px 24px rgba(53,196,106,.18);
  }
  .neutral-cell.active .pos { color: var(--green); }
  .foot { margin-top: 12px; font-size: 11px; color: var(--muted); text-align: center; }
  .foot.err { color: #ff7676; }
  .disabled { text-align: center; padding: 30px 10px; color: var(--muted); }
  .disabled code { color: var(--accent); }
</style>
</head>
<body>
  <div class="box">
    <div class="head"><span>Model Shift</span><span class="dot"></span></div>
    <div id="app"></div>
    <div class="foot" id="foot">connecting…</div>
  </div>

<script>
const app = document.getElementById("app");
const foot = document.getElementById("foot");
const NEUTRAL = "N";

async function load() {
  try {
    const res = await fetch("gearbox/state");
    if (!res.ok) throw new Error("state " + res.status);
    render(await res.json());
    foot.className = "foot";
    foot.textContent = "Shift a gear to re-route every request to that model.";
  } catch (e) {
    foot.className = "foot err";
    foot.textContent = "Could not reach the proxy: " + e.message;
  }
}

async function shift(position) {
  try {
    const res = await fetch("gearbox/shift", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ position })
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error || res.status);
    }
    render(await res.json());
    foot.className = "foot";
    foot.textContent = "Shift a gear to re-route every request to that model.";
  } catch (e) {
    foot.className = "foot err";
    foot.textContent = "Shift failed: " + e.message;
  }
}

function esc(s) {
  return String(s ?? "").replace(/[&<>"]/g, c =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

function render(state) {
  if (!state.enabled) {
    app.innerHTML = '<div class="disabled">Gearbox is disabled.<br>Set <code>Gearbox.Enabled</code> to <code>true</code> in appsettings.json.</div>';
    return;
  }

  const gears = state.gears || [];
  const isNeutral = (state.selected || NEUTRAL).toUpperCase() === NEUTRAL;
  const active = gears.find(g => g.position.toLowerCase() === (state.selected || "").toLowerCase());

  // Status readout.
  const gearName = isNeutral ? NEUTRAL : esc(state.selected);
  const modelText = isNeutral
    ? "Neutral · client picks the model"
    : (active ? esc(active.model || active.label || active.position) : "—");

  // H-pattern: odd gears on the top row, even on the bottom, Neutral spanning the middle.
  const top = [], bottom = [];
  gears.forEach((g, i) => (i % 2 === 0 ? top : bottom).push(g));
  const cols = Math.max(top.length, bottom.length, 1);

  const cell = g => {
    const on = active && g.position.toLowerCase() === active.position.toLowerCase();
    const lbl = g.label || g.model || "";
    return `<button class="cell${on ? " active" : ""}" data-pos="${esc(g.position)}" title="${esc(g.model)}">
      <span class="pos">${esc(g.position)}</span><span class="lbl">${esc(lbl)}</span></button>`;
  };
  const pad = n => Array.from({ length: cols - n }, () => "<span></span>").join("");

  app.innerHTML = `
    <div class="status">
      <div class="gear">GEAR · <b>${gearName}</b></div>
      <div class="model${isNeutral ? " neutral" : ""}">${modelText}</div>
    </div>
    <div class="shifter">
      <div class="row" style="grid-template-columns: repeat(${cols}, 1fr)">
        ${top.map(cell).join("")}${pad(top.length)}
      </div>
      <button class="cell neutral-cell${isNeutral ? " active" : ""}" data-pos="${NEUTRAL}">
        <span class="pos">N</span><span class="lbl">Neutral</span></button>
      <div class="row" style="grid-template-columns: repeat(${cols}, 1fr)">
        ${bottom.map(cell).join("")}${pad(bottom.length)}
      </div>
    </div>`;

  app.querySelectorAll("[data-pos]").forEach(el =>
    el.addEventListener("click", () => shift(el.getAttribute("data-pos"))));
}

load();
</script>
</body>
</html>
""";
}
