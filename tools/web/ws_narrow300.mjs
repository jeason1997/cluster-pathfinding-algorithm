const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "speed", value: 4 });
send({ type: "map", id: "neicheng_walk01", count: 300 });
await type("world"); await type("reset");
send({ type: "goal", x: 123, z: 78 });
const a = await type("goal");
console.log("goal", a.ok, a.goal, "slots", a.slots?.length, "R", a.goalRadius, a.error || "");
let last = null, minSep = 99;
const start = Date.now();
while (Date.now() - start < 90000) {
  const m = await next(8000);
  if (m.type !== "state") continue;
  last = m; minSep = Math.min(minSep, m.minSep);
  if (m.t % 10 < 0.4) console.log("t", m.t.toFixed(1), "arr", m.arrived, "/", m.count, "yield", m.yields, "min", m.minSep.toFixed(3));
  if (m.finalized) break;
}
console.log("RESULT", { t: last?.t, arrived: last?.arrived, count: last?.count, minSep, finalized: last?.finalized });
ws.close();

