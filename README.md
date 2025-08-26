# PeakBot repo overview (organized)

## Config

* **Config/BotCloneSettings.cs** — Tunables for prefab, speed, sprint and despawn distances, stamina thresholds, detour factor, wall-hang cap, ledge scan. 

## Bootstrap

* **Core/BotClonePlugin.cs** — BepInEx entry. Builds runtime NavMesh (campfire slice or full), generates NavPoints, caches prefab, spawns clone on F10, recenters slice while the player moves.
* **Core/CloneSpawner.cs** — Spawns clone near player via Photon, warps agent onto NavMesh, scales speed, attaches `GraphFollower` and `NavDiag`, disables other AI, starts cosmetic replication.
* **Core/CosmeticReplicator.cs** — Photon event to mirror local cosmetics onto the bot and apply them.
* **Core/PrefabCache.cs** — Finds and caches a bot prefab and injects it into Photon’s prefab pool with a runtime fallback.

## World segmentation and nav data

* **Core/CampfireSegmentation.cs** — Computes world-space slice AABB between campfires, selects primary axis by variance, clamps to world bounds, includes edge and shape checks.
* **Runtime/RuntimeNavMesh.cs** — Builds NavMesh at runtime (optional AABB), replaces previous data, re-enables agents that are off-mesh.
* **Runtime/RuntimeNavPointGenerator.cs** — Generates NavPoint graph (grid sampling or triangulation fallback), connects line-of-sight neighbors with adaptive radius, mirrors connections.

## Follower AI

### Decision state and sensors

* **AI/State/Blackboard.cs** — sensors and decision inputs (`StepInfo`, `WallAttachInfo`, `GapInfo`, `Blackboard`).
* **AI/Perception/Perception.cs** — reads the world/game state and packages it into a snapshot (Blackboard) that the Brain (BotBrain) can reason over.
* **AI/Decision/BotBrain.cs** — Scores actions (Follow, Sprint, Rest, Hop, WallAttach, GapJump) with cooldowns and thresholds, outputs `BotDecision` (reason, optional target, optional strafe).
* **AI/GraphFollower.BrainBridge.cs** — Creates brain and perception, builds `Blackboard`, toggles sprint and rest with debounce, passes actions to executor.

### Navigation and movement

* **AI/GraphFollower.Core.cs** — Main loop: NavMesh steering then graph or straight fallback, apply decision, run action, stuck detection, hang cap, body metrics.
* **AI/GraphFollower.Navigation.cs** — NavMesh path refresh and steering, decides when to fall back, graph candidate selection with cones and greedy scoring (alignment, distance change, smoothness).
* **AI/GraphFollower.Movement.cs** — Strafe blending (clamp, accel, decel) and `DirToLook` yaw/pitch conversion; returns movement vector from brain strafe and rest state.
* **AI/GraphFollower.DetourCost.cs** — A\* over `NavPoint` graph to estimate path length; nearest-node lookup; binary heap implementation.
* **AI/GraphFollower.Stamina.cs** — Helpers for regular stamina absolute and fraction, recent exhaustion gate.
* **AI/GraphFollower.Actions.cs** — Executes hop, gap jump (aim toward landing), wall attach (jump then climb); safe jump RPC; optional delayed climb retry; jump gating aligned with game checks.

## diagnostics


* **Core/NavDiag.cs** — NavMesh diagnostics logger (triangulation size, on-mesh checks, agent types).
* **AI/Debug/DecisionTrace.cs** — Debug log of chosen action, reason, and key values (distance, stamina, detour, step, wall, gap).