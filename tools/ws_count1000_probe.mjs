const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); if (m.type === "state") { console.log("state", m.count, m.agents.length); return; } console.log("MSG", m.type, m.ok, m.error || "", m.count || ""); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("init");
console.log("sending map1000");
send({ type: "map", id: "neicheng_walk01", count: 1000 });
try { const g = await type("world", 20000); console.log("WORLD", g.count, g.mapId); } catch(e) { console.log("ERR", e.message); }
ws.close();
