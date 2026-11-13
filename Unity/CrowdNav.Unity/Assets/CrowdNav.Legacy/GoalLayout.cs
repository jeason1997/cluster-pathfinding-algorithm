using System;
using System.Collections.Generic;

namespace CrowdNav
{
    public struct GoalSlot
    {
        public int Index;
        public int Ring;
        public Vec2 Position;
        public float Angle;

        public GoalSlot(int index, int ring, Vec2 position, Vec2 center)
        {
            Index = index;
            Ring = ring;
            Position = position;
            Angle = MathF.Atan2(position.Z - center.Z, position.X - center.X);
        }
    }

    /// <summary>
    /// Goal footprint made of collision-free hex-packed standing slots. Reserving a slot for
    /// every unit removes the classic "everyone targets one point" jam. Slot assignment can
    /// either fill rings by angle, or preserve the group's relative formation so rear units
    /// are not sent to the far side of the circle.
    /// </summary>
    public sealed class GoalLayout
    {
        public Vec2 Center;
        public readonly List<GoalSlot> Slots = new List<GoalSlot>();
        public readonly List<int> RingSlotCounts = new List<int>();
        public float Spacing;
        public bool QueueMode;

        public static GoalLayout Build(NavGrid grid, Vec2 requestedCenter, int count, float spacing,
            int maxRings = 40, Vec2 preferredSide = default)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (spacing < grid.AgentRadius * 2f + 0.04f)
                throw new ArgumentException("Slot spacing must exceed unit diameter.");

            bool directional = preferredSide.LengthSq > 1e-4f;
            var layout = new GoalLayout { Spacing = spacing };
            Vec2 center = requestedCenter;
            if (!grid.IsWorldWalkable(center))
            {
                int cell = grid.FindNearestWalkableCell(center);
                if (cell < 0) throw new InvalidOperationException("No walkable cell near goal.");
                center = grid.CellCenter(cell);
            }
            layout.Center = center;

            for (int ring = 0; ring <= maxRings && layout.Slots.Count < count; ring++)
            {
                var ringPositions = new List<Vec2>();
                if (ring == 0)
                {
                    ringPositions.Add(center);
                }
                else
                {
                    int q = ring;
                    int r = -ring;
                    for (int side = 0; side < 6; side++)
                    {
                        for (int step = 0; step < ring; step++)
                        {
                            float wx = spacing * (q + r * 0.5f);
                            float wz = spacing * 0.86602540378f * r;
                            Vec2 p = new Vec2(center.X + wx, center.Z + wz);
                            ringPositions.Add(p);

                            if (side == 0) { q += 0; r += 1; }
                            else if (side == 1) { q -= 1; r += 1; }
                            else if (side == 2) { q -= 1; r += 0; }
                            else if (side == 3) { q += 0; r -= 1; }
                            else if (side == 4) { q += 1; r -= 1; }
                            else { q += 1; r += 0; }
                        }
                    }
                }

                ringPositions.Sort((a, b) =>
                {
                    float aa = MathF.Atan2(a.Z - center.Z, a.X - center.X);
                    float bb = MathF.Atan2(b.Z - center.Z, b.X - center.X);
                    return aa.CompareTo(bb);
                });

                int addedThisRing = 0;
                for (int i = 0; i < ringPositions.Count && layout.Slots.Count < count; i++)
                {
                    Vec2 p = ringPositions[i];
                    if (!grid.IsWorldWalkable(p)) continue;
                    if (directional)
                    {
                        Vec2 radial = p - center;
                        if (radial.LengthSq > 1e-6f &&
                            Vec2.Dot(radial.Normalized(), preferredSide.Normalized()) < -0.10f)
                            continue;
                    }
                    if (!grid.IsSegmentWalkable(center, p)) continue;
                    layout.Slots.Add(new GoalSlot(layout.Slots.Count, ring, p, center));
                    addedThisRing++;
                }
                layout.RingSlotCounts.Add(addedThisRing);
            }

            if (layout.Slots.Count < count)
            {
                // A narrow ledge or an obstacle can make the approach-side half disk too
                // small. Fall back to the full disk rather than failing the command.
                if (directional)
                    return Build(grid, requestedCenter, count, spacing, maxRings, default);
                throw new InvalidOperationException($"Could only fit {layout.Slots.Count}/{count} goal slots near {center}.");
            }

            return layout;
        }

        /// <summary>
        /// Corridor fallback: walk outward from the goal through connected walkable
        /// cells and place standing slots at the requested spacing. This lets a goal
        /// live inside a narrow passage without forcing the slot generator to expand
        /// a huge circle around an impossible open area.
        /// </summary>
        public static GoalLayout BuildQueue(NavGrid grid, Vec2 requestedCenter, int count, float spacing,
            Vec2 approachFrom = default, float maxRadius = 0f)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (spacing < grid.AgentRadius * 2f + 0.04f)
                throw new ArgumentException("Slot spacing must exceed unit diameter.");

            int start = grid.FindNearestWalkableCell(requestedCenter);
            if (start < 0) throw new InvalidOperationException("No walkable cell near goal.");
            grid.CellCoords(start, out int sx, out int sz);
            var layout = new GoalLayout
            {
                Center = grid.CellCenter(start),
                Spacing = spacing,
                QueueMode = true
            };

            int n = grid.Width * grid.Height;
            var dist = new int[n];
            var parent = new int[n];
            Array.Fill(dist, -1);
            Array.Fill(parent, -1);
            parent[start] = -2;
            var q = new Queue<int>();
            dist[start] = 0;
            q.Enqueue(start);
            var cells = new List<int> { start };
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                grid.CellCoords(cur, out int cx, out int cz);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!grid.InBounds(nx, nz)) continue;
                    int ni = grid.Index(nx, nz);
                    if (dist[ni] >= 0 || !grid.IsWalkableCell(nx, nz)) continue;
                    dist[ni] = dist[cur] + 1;
                    parent[ni] = cur;
                    q.Enqueue(ni);
                    cells.Add(ni);
                }
            }
            // Build the queue along the shortest walkable path from the goal back
            // toward the incoming crowd. This keeps a staircase queue in a line
            // instead of scattering slots into every side branch.
            var preferred = new List<int>();
            var used = new bool[n];
            if (approachFrom.LengthSq > 1e-4f)
            {
                int end = grid.FindNearestWalkableCell(approachFrom);
                if (end >= 0 && dist[end] >= 0)
                {
                    var rev = new List<int>();
                    for (int cur = end; cur >= 0 && cur != start; cur = parent[cur])
                        rev.Add(cur);
                    rev.Add(start);
                    rev.Reverse();
                    for (int i = 0; i < rev.Count; i++)
                    {
                        if (!used[rev[i]])
                        {
                            preferred.Add(rev[i]);
                            used[rev[i]] = true;
                        }
                    }
                }
            }

            // Spill into the open area around the incoming crowd only after the
            // corridor path is used; never fill random side rooms near the goal.
            var spill = new List<int>();
            for (int i = 0; i < cells.Count; i++)
                if (!used[cells[i]]) spill.Add(cells[i]);
            spill.Sort((a, b) =>
            {
                float da = approachFrom.LengthSq > 1e-4f
                    ? Vec2.DistanceSq(grid.CellCenter(a), approachFrom) : dist[a];
                float db = approachFrom.LengthSq > 1e-4f
                    ? Vec2.DistanceSq(grid.CellCenter(b), approachFrom) : dist[b];
                int c = da.CompareTo(db);
                return c != 0 ? c : dist[a].CompareTo(dist[b]);
            });
            preferred.AddRange(spill);

            float minSepSq = spacing * spacing * 0.96f;
            for (int i = 0; i < preferred.Count && layout.Slots.Count < count; i++)
            {
                int ni = preferred[i];
                Vec2 p = grid.CellCenter(ni);
                if (maxRadius > 0f &&
                    Vec2.DistanceSq(p, layout.Center) > maxRadius * maxRadius) continue;
                bool free = true;
                for (int k = 0; k < layout.Slots.Count; k++)
                {
                    if (Vec2.DistanceSq(p, layout.Slots[k].Position) < minSepSq)
                    {
                        free = false;
                        break;
                    }
                }
                if (!free) continue;
                layout.Slots.Add(new GoalSlot(layout.Slots.Count, 0, p, layout.Center));
                layout.RingSlotCounts.Add(layout.Slots.Count);
            }
            if (layout.Slots.Count < count)
                throw new InvalidOperationException(
                    $"Could only fit {layout.Slots.Count}/{count} queue slots near {layout.Center}.");
            return layout;
        }

        public int GetRingStart(int ring)
        {
            int start = 0;
            for (int i = 0; i < ring && i < RingSlotCounts.Count; i++) start += RingSlotCounts[i];
            return start;
        }
    }

    public static class GoalAssigner
    {
        private sealed class AgentRadialComparer : IComparer<Agent>
        {
            private readonly Vec2 _center;
            public AgentRadialComparer(Vec2 center) { _center = center; }

            public int Compare(Agent a, Agent b)
            {
                if (ReferenceEquals(a, b)) return 0;
                float da = Vec2.DistanceSq(a.Position, _center);
                float db = Vec2.DistanceSq(b.Position, _center);
                int c = da.CompareTo(db);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            }
        }

        private sealed class AgentAngleComparer : IComparer<Agent>
        {
            private readonly Vec2 _center;
            public AgentAngleComparer(Vec2 center) { _center = center; }

            public int Compare(Agent a, Agent b)
            {
                float aa = MathF.Atan2(a.Position.Z - _center.Z, a.Position.X - _center.X);
                float bb = MathF.Atan2(b.Position.Z - _center.Z, b.Position.X - _center.X);
                int c = aa.CompareTo(bb);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            }
        }

        /// <summary>
        /// By default maps the group's relative formation onto the goal disk and minimises
        /// assignment distance with pairwise swaps. The older ring/angle fill is kept as a
        /// fallback / option; it can send a rear unit to a far-side slot when the whole group
        /// approaches from one direction.
        /// </summary>
        public static void AssignInitial(IReadOnlyList<Agent> agents, GoalLayout layout,
            bool formationPreserving = false, bool queueNarrow = false)
        {
            if (agents.Count > layout.Slots.Count)
                throw new InvalidOperationException("Not enough goal slots.");

            if (queueNarrow)
            {
                AssignQueue(agents, layout);
                return;
            }

            if (formationPreserving)
            {
                AssignFormation(agents, layout);
                return;
            }

            var sorted = new List<Agent>(agents);
            sorted.Sort(new AgentRadialComparer(layout.Center));

            int cursor = 0;
            for (int ring = 0; ring < layout.RingSlotCounts.Count && cursor < sorted.Count; ring++)
            {
                int ringCount = layout.RingSlotCounts[ring];
                if (ringCount <= 0) continue;
                int take = Math.Min(ringCount, sorted.Count - cursor);
                int ringStart = layout.GetRingStart(ring);

                var batch = sorted.GetRange(cursor, take);
                batch.Sort(new AgentAngleComparer(layout.Center));

                int bestRotation = FindBestRotation(batch, layout, ringStart, take);
                for (int i = 0; i < take; i++)
                {
                    GoalSlot slot = layout.Slots[ringStart + ((i + bestRotation) % take)];
                    Agent agent = batch[i];
                    agent.SlotIndex = slot.Index;
                    agent.SlotPosition = slot.Position;
                }
                cursor += take;
            }
        }

        /// <summary>
        /// Rebuild after adding units. Existing units keep their exact old slot whenever that
        /// slot still exists; newcomers fill the newly added outer slots.
        /// </summary>
        public static void AssignPreservingExisting(IReadOnlyList<Agent> agents, GoalLayout layout,
            IReadOnlyList<Agent> existing)
        {
            var used = new bool[layout.Slots.Count];
            var existingSet = new HashSet<int>();
            for (int i = 0; i < existing.Count; i++) existingSet.Add(existing[i].Id);

            for (int i = 0; i < existing.Count; i++)
            {
                Agent a = existing[i];
                int best = -1;
                float bestD = 1e-4f;
                for (int s = 0; s < layout.Slots.Count; s++)
                {
                    if (used[s]) continue;
                    float d = Vec2.DistanceSq(a.SlotPosition, layout.Slots[s].Position);
                    if (d <= bestD)
                    {
                        bestD = d;
                        best = s;
                    }
                }
                if (best >= 0)
                {
                    used[best] = true;
                    a.SlotIndex = best;
                    a.SlotPosition = layout.Slots[best].Position;
                }
            }

            for (int i = 0; i < agents.Count; i++)
            {
                Agent a = agents[i];
                if (existingSet.Contains(a.Id)) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int s = 0; s < layout.Slots.Count; s++)
                {
                    if (used[s]) continue;
                    float d = Vec2.DistanceSq(a.Position, layout.Slots[s].Position);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = s;
                    }
                }
                if (best < 0) throw new InvalidOperationException("No free goal slot for newcomer.");
                used[best] = true;
                a.SlotIndex = best;
                a.SlotPosition = layout.Slots[best].Position;
            }
        }

        /// <summary>
        /// Narrow-corridor goal: fill from the far end backwards, so the first units
        /// to arrive take the deepest slots and later units stop behind them. This is
        /// what prevents a stationary front rank from blocking a single-lane ramp.
        /// </summary>
        private static void AssignQueue(IReadOnlyList<Agent> agents, GoalLayout layout)
        {
            // BuildQueue emits slots in path order from the goal outward. Assigning
            // in that exact order gives the first units the front of the queue and
            // later units the back; a straight-line projection is not reliable on
            // a bending stairway.
            var order = new List<Agent>(agents);
            order.Sort((a, b) =>
            {
                float da = Vec2.DistanceSq(a.Position, layout.Center);
                float db = Vec2.DistanceSq(b.Position, layout.Center);
                int c = da.CompareTo(db);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });

            for (int i = 0; i < order.Count; i++)
            {
                Agent a = order[i];
                GoalSlot s = layout.Slots[i];
                a.SlotIndex = s.Index;
                a.SlotPosition = s.Position;
            }
        }

        /// <summary>
        /// Keeps the group's internal formation instead of filling the goal disk by
        /// angle. Every unit gets the slot that is closest to "its current offset
        /// translated to the goal". The whole crowd therefore translates as a block:
        /// front units take the far/near slots in the same order, and no rear unit is
        /// sent around the crowd to the opposite side of the circle.
        /// </summary>
        private static void AssignFormation(IReadOnlyList<Agent> agents, GoalLayout layout)
        {
            Vec2 centroid = Vec2.Zero;
            for (int i = 0; i < agents.Count; i++) centroid += agents[i].Position;
            centroid /= MathF.Max(1, agents.Count);

            float maxOffset = 0f;
            for (int i = 0; i < agents.Count; i++)
                maxOffset = MathF.Max(maxOffset, Vec2.Distance(agents[i].Position, centroid));

            float maxSlot = 0f;
            for (int i = 0; i < layout.Slots.Count; i++)
                maxSlot = MathF.Max(maxSlot, Vec2.Distance(layout.Slots[i].Position, layout.Center));

            float scale = maxOffset > 1e-3f
                ? MathF.Min(1f, MathF.Max(0.2f, (maxSlot - 0.35f) / maxOffset))
                : 1f;

            var order = new List<Agent>(agents);
            order.Sort((a, b) =>
            {
                float da = Vec2.DistanceSq(a.Position, centroid);
                float db = Vec2.DistanceSq(b.Position, centroid);
                int c = db.CompareTo(da); // outer units first, so they claim outer slots
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });

            var used = new bool[layout.Slots.Count];
            for (int i = 0; i < order.Count; i++)
            {
                Agent a = order[i];
                Vec2 desired = layout.Center + (a.Position - centroid) * scale;
                int best = -1;
                float bestD = float.MaxValue;
                for (int s = 0; s < layout.Slots.Count; s++)
                {
                    if (used[s]) continue;
                    float d = Vec2.DistanceSq(desired, layout.Slots[s].Position);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = s;
                    }
                }
                if (best < 0) throw new InvalidOperationException("No free goal slot.");
                used[best] = true;
                a.SlotIndex = best;
                a.SlotPosition = layout.Slots[best].Position;
            }

            // Greedy nearest can still box a neighbour into a slot on the wrong side.
            // A few pairwise swaps minimise the sum of "translated offset -> slot"
            // distances, which keeps the formation straight and avoids crossing paths.
            var desiredByAgent = new Dictionary<int, Vec2>(order.Count);
            for (int i = 0; i < order.Count; i++)
                desiredByAgent[order[i].Id] = layout.Center + (order[i].Position - centroid) * scale;

            for (int pass = 0; pass < 8; pass++)
            {
                bool improved = false;
                for (int i = 0; i < order.Count; i++)
                {
                    Agent a = order[i];
                    Vec2 da = desiredByAgent[a.Id];
                    for (int j = i + 1; j < order.Count; j++)
                    {
                        Agent b = order[j];
                        Vec2 db = desiredByAgent[b.Id];
                        float direct = Vec2.DistanceSq(da, a.SlotPosition) +
                                       Vec2.DistanceSq(db, b.SlotPosition);
                        float swapped = Vec2.DistanceSq(da, b.SlotPosition) +
                                        Vec2.DistanceSq(db, a.SlotPosition);
                        if (swapped + 1e-4f < direct)
                        {
                            (a.SlotIndex, b.SlotIndex) = (b.SlotIndex, a.SlotIndex);
                            (a.SlotPosition, b.SlotPosition) = (b.SlotPosition, a.SlotPosition);
                            improved = true;
                        }
                    }
                }
                if (!improved) break;
            }
        }

        private static int FindBestRotation(List<Agent> batch, GoalLayout layout, int ringStart, int take)
        {
            if (take <= 1) return 0;
            int best = 0;
            float bestScore = float.MaxValue;
            for (int rot = 0; rot < take; rot++)
            {
                float score = 0f;
                for (int i = 0; i < take; i++)
                {
                    GoalSlot slot = layout.Slots[ringStart + ((i + rot) % take)];
                    float ua = MathF.Atan2(batch[i].Position.Z - layout.Center.Z,
                        batch[i].Position.X - layout.Center.X);
                    score += MathF.Abs(MathUtil.AngleDelta(ua, slot.Angle));
                }
                if (score < bestScore)
                {
                    bestScore = score;
                    best = rot;
                }
            }
            return best;
        }
    }
}
