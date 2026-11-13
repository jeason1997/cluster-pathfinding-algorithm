import fs from "node:fs";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const targets = await fetch("http://127.0.0.1:9223/json/list").then(r => r.json());
const page = targets.find(t => t.type === "page");
if (!page) throw new Error("no page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.addEventListener("open", res); ws.addEventListener("error", rej); });
let id = 0; const pending = new Map(); const exceptions = [];
ws.addEventListener("message", ev => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result); }
  if (m.method === "Runtime.exceptionThrown") exceptions.push(m.params.exceptionDetails.text);
});
function cmd(method, params = {}) { const n = ++id; ws.send(JSON.stringify({ id: n, method, params })); return new Promise((resolve, reject) => pending.set(n, { resolve, reject })); }
async function ev(expr) { const r = await cmd("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.text); return r.result.value; }
function drag(button, buttons, x, y, dx, dy) {
  return (async () => {
    await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
    await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button, buttons, clickCount: 1 });
    await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: x + dx, y: y + dy, button, buttons });
    await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: x + dx, y: y + dy, button, buttons: 0, clickCount: 1 });
  })();
}
await cmd("Page.enable"); await cmd("Runtime.enable");
await cmd("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await cmd("Page.navigate", { url: "http://127.0.0.1:8799/" });
await sleep(3000);

const center = await ev("window.__demo.screenForWorld(32,32)");
const before = await ev("window.__demo.screenForWorld(32,32)");
await drag("middle", 4, 700, 500, 180, 0);
await sleep(500);
const after = await ev("window.__demo.screenForWorld(32,32)");
console.log("3d middle pan: before", before, "after", after);

await ev("document.querySelector('#viewToggle button[data-view=\"2d\"]').click()");
await sleep(700);
console.log("mode", await ev("window.__demo.getViewMode()"), await ev("document.getElementById('modePill').textContent"));
const b2 = await ev("window.__demo.screenForWorld(32,32)");
await drag("middle", 4, 700, 500, 180, 0);
await sleep(500);
const a2 = await ev("window.__demo.screenForWorld(32,32)");
console.log("2d middle pan: before", b2, "after", a2);
const g2 = await ev("window.__demo.screenForWorld(49,48)");
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: g2.x, y: g2.y });
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: g2.x, y: g2.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: g2.x, y: g2.y, button: "left", buttons: 0, clickCount: 1 });
await sleep(800);
console.log("2d goal click", await ev("document.getElementById('goalText').textContent"));
const shot = await cmd("Page.captureScreenshot", { format: "png" });
fs.writeFileSync("out/interactive_2d.png", Buffer.from(shot.data, "base64"));
console.log("screenshot out/interactive_2d.png", fs.statSync("out/interactive_2d.png").size);
console.log("exceptions", exceptions);
ws.close();
