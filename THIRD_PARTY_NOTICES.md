# Third-party notices

## RVO2 / RVO2-CS

`src/CrowdNav/OrcaSolver.cs` contains an adapted implementation of the ORCA
linear-program construction and solver from RVO2-CS:

- RVO2 Library, Copyright 2008 University of North Carolina at Chapel Hill
- RVO2-CS: https://github.com/snape/RVO2-CS
- License: Apache License 2.0

The original copyright/SPDX notice is retained at the top of `OrcaSolver.cs`.
A copy of the license is provided at `LICENSES/Apache-2.0.txt`.

Adaptations in this repository include: mapping to the project's `Vec2`,
integrating the solver with the height-field path follower, a fixed safety
margin, acceleration limiting, and combining the result with the project's
sequential collision-free commit / goal-slot / yielding logic.

## Three.js

`src/CrowdSim.Interactive/wwwroot/vendor/three.min.js` is Three.js r152.2,
used only by the browser visualisation:

- Three.js, Copyright 2010-2023 three.js authors
- MIT License
- https://threejs.org/
- Local license copy: `src/CrowdSim.Interactive/wwwroot/vendor/THREE-LICENSE.txt`

## Other references

The design document cites Recast & Detour, Unity NavMeshAgent, the A* Pathfinding
Project and RVO2 as background reading. No source code from those projects is
included here except the RVO2-CS adaptation above.
