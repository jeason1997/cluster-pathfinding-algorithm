const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const queue = [], waiters = [];
ws.addEventListener("message", e => {
  const msg = JSON.parse(e.data);
  if (waiters.length) waiters.shift()(msg); else queue.push(msg);
});
function next(timeout = 8000) {
  if (queue.length) return Promise.resolve(queue.shift());
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error("timeout")), timeout);
    waiters.push(m => { clearTimeout(t); resolve(m); });
  });
}
async function type(t, timeout = 8000) {
  const end = Date.now() + timeout;
  for (;;) {
    const m = await next(Math.max(100, end - Date.now()));
    if (m.type === t) return m;
  }
}
const send = o => ws.send(JSON.stringify(o));
const sleep = ms => new Promise(r => setTimeout(r, ms));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });

let world = await type("world");
console.log("default", world.mapId, world.mapName, "maps", world.maps);
console.log("terrain", world.terrain.width, world.terrain.height, "walkable", !!world.terrain.walkable, "start", world.start);
await type("init");
send({ type: "speed", value: 4 });

async function testMap(id, count) {
  send({ type: "map", id, count });
  world = await type("world", 15000);
  const reset = await type("reset", 15000);
  console.log(id, "world", world.mapId, "terrain", world.terrain.width, world.terrain.height,
    "walkable", !!world.terrain.walkable, "start", world.start, "agents", reset.agents.length);
  if (reset.agents.length !== count) {
    console.log("AGENT COUNT MISMATCH", reset.agents.length, count);
  }
  const g = world.suggestedGoal;
  send({ type: "goal", x: g[0], z: g[1] });
  const ack = await type("goal", 15000);
  console.log("goal ack", ack.ok, ack.goal, "slots", ack.slots ? ack.slots.length : 0,
    "radius", ack.goalRadius, "error", ack.error || "");
  if (!ack.ok) return;
  let last = null;
  const start = Date.now();
  while (Date.now() - start < 30000) {
    const m = await next(6000);
    if (m.type === "state") { last = m; if (m.finalized) break; }
  }
  console.log("result", { t: last?.t, arrived: last?.arrived, count: last?.count,
    minSep: last?.minSep, finalized: last?.finalized, goalR: last?.goalRadius });
}

await testMap("neicheng_walk01", 120);
await testMap("neicheng_walk01", 300);
await testMap("flagship", 120);
ws.close();
