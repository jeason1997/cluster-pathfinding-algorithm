const ws = new WebSocket("ws://127.0.0.1:8799/ws");
ws.binaryType = "arraybuffer";
const q = [], wait = [];
ws.addEventListener("message", e => {
  if (typeof e.data === "string") { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); return; }
  const dv = new DataView(e.data);
  if (dv.getUint32(0, true) !== 0x43534E31) return;
  const m = { type: "state", t: dv.getFloat32(8, true), count: dv.getInt32(12, true),
    arrived: dv.getInt32(16, true), stepMs: dv.getFloat32(36, true), pending: dv.getInt32(40, true) };
  wait.length ? wait.shift()(m) : q.push(m);
});
function next(t = 20000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 30000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "map", id: "neicheng_walk01", count: 1000 });
const w = await type("world"); await type("reset");
send({ type: "goal", x: w.suggestedGoal[0], z: w.suggestedGoal[1] });
await type("goal");
let n = 0, maxStep = 0, maxPending = 0, firstT = null, lastT = null, final = null;
const t0 = Date.now();
while (Date.now() - t0 < 8000) {
  const m = await next(5000);
  if (m.type !== "state") continue;
  n++; if (firstT === null) firstT = m.t; lastT = m.t;
  maxStep = Math.max(maxStep, m.stepMs); maxPending = Math.max(maxPending, m.pending);
  final = m;
}
console.log({ states: n, simSeconds: lastT - firstT, stateHz: (n / (lastT - firstT)).toFixed(1),
  maxStepMs: maxStep.toFixed(2), maxPending, arrived: final.arrived, count: final.count });
ws.close();
