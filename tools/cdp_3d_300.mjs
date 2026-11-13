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
await cmd("Page.enable"); await cmd("Runtime.enable");
await cmd("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await cmd("Page.navigate", { url: "http://127.0.0.1:8799/" });
await sleep(2500);
await ev(`(() => { const n=document.getElementById('countNumber'); n.value=300; document.getElementById('applyCount').click(); return true; })()`);
await sleep(2500);
console.log("count", await ev("document.getElementById('ovUnits').textContent"));
const p = await ev("window.__demo.screenForWorld(49,48)");
console.log("click", p);
await cmd("Input.dispatchMouseEvent", { type: "mouseMoved", x: p.x, y: p.y });
await cmd("Input.dispatchMouseEvent", { type: "mousePressed", x: p.x, y: p.y, button: "left", buttons: 1, clickCount: 1 });
await cmd("Input.dispatchMouseEvent", { type: "mouseReleased", x: p.x, y: p.y, button: "left", buttons: 0, clickCount: 1 });
await sleep(2000);
console.log("goal", await ev("document.getElementById('goalText').textContent"));
await sleep(12000);
console.log("state", await ev(`({ t:document.getElementById('tNow').textContent, n:document.getElementById('ovUnits').textContent, a:document.getElementById('ovArrived').textContent, s:document.getElementById('ovState').textContent, min:document.getElementById('ovMin').textContent })`));
const shot = await cmd("Page.captureScreenshot", { format: "png" });
fs.writeFileSync("out/interactive_3d_300.png", Buffer.from(shot.data, "base64"));
console.log("screenshot", fs.statSync("out/interactive_3d_300.png").size, "exceptions", exceptions);
ws.close();
