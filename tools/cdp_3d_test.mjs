import fs from "node:fs";

const sleep = ms => new Promise(r => setTimeout(r, ms));
const targets = await fetch("http://127.0.0.1:9223/json/list").then(r => r.json());
const page = targets.find(t => t.type === "page");
if (!page) throw new Error("no page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});
let id = 0;
const pending = new Map();
const exceptions = [];
ws.addEventListener("message", ev => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) {
    const p = pending.get(m.id); pending.delete(m.id);
    if (m.error) p.reject(new Error(JSON.stringify(m.error))); else p.resolve(m.result);
  }
  if (m.method === "Runtime.exceptionThrown") exceptions.push(m.params.exceptionDetails.text);
});
function cmd(method, params = {}) {
  const n = ++id;
  ws.send(JSON.stringify({ id: n, method, params }));
  return new Promise((resolve, reject) => pending.set(n, { resolve, reject }));
}
async function evalv(expr) {
  const r = await cmd("Runtime.evaluate", { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text || "eval failed");
  return r.result.value;
}
await cmd("Page.enable");
await cmd("Runtime.enable");
await cmd("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await cmd("Page.navigate", { url: "http://127.0.0.1:8799/" });
await sleep(3000);

const before = await evalv("window.__demo.getCamera()");
const c = { x: 800, y: 500 };
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: c.x, y: c.y });
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: c.x, y: c.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: c.x + 180, y: c.y + 40, button: "left", buttons: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: c.x + 180, y: c.y + 40, button: "left", buttons: 0, clickCount: 1 });
await sleep(600);
const after = await evalv("window.__demo.getCamera()");
console.log("orbit", { before, after });

const unit = await evalv("window.__demo.screenForUnit(0)");
console.log("unit screen", unit);
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: unit.x, y: unit.y });
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: unit.x, y: unit.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: unit.x, y: unit.y, button: "left", buttons: 0, clickCount: 1 });
await sleep(500);
console.log("follow", await evalv("window.__demo.getFollow()"));
console.log("followText", await evalv("document.getElementById('followText').textContent"));
const shot = await cmd("Page.captureScreenshot", { format: "png" });
fs.writeFileSync("out/interactive_3d_follow.png", Buffer.from(shot.data, "base64"));
console.log("screenshot out/interactive_3d_follow.png", fs.statSync("out/interactive_3d_follow.png").size);
console.log("exceptions", exceptions);
ws.close();
