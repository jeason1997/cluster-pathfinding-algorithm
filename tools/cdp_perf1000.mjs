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
await sleep(3000);
await ev(`(() => { const s=document.getElementById('mapSelect'); s.value='neicheng_walk01'; s.dispatchEvent(new Event('change',{bubbles:true})); return true; })()`);
await sleep(2500);
await ev(`(() => { const n=document.getElementById('countNumber'); n.value=1000; document.getElementById('applyCount').click(); return true; })()`);
await sleep(4000);
await ev(`(() => { const s=document.getElementById('speedSelect'); s.value='4'; s.dispatchEvent(new Event('change',{bubbles:true})); return true; })()`);
await ev(`document.getElementById('suggestGoalBtn').click()`);
await sleep(4000);
const fps = await ev(`new Promise(res => { let n=0; const t0=performance.now(); function f(t){ n++; if(t-t0<2000) requestAnimationFrame(f); else res(n/((t-t0)/1000)); } requestAnimationFrame(f); })`);
console.log("1000", await ev(`({count:document.getElementById('ovUnits').textContent, arrived:document.getElementById('ovArrived').textContent, state:document.getElementById('ovState').textContent, t:document.getElementById('tNow').textContent, min:document.getElementById('ovMin').textContent})`));
console.log("fps", fps.toFixed(1));
await cmd("Page.captureScreenshot", { format: "png" }).then(r => fs.writeFileSync("out/perf_1000.png", Buffer.from(r.data, "base64")));
console.log("screenshot", fs.statSync("out/perf_1000.png").size, "exceptions", exceptions);
ws.close();
