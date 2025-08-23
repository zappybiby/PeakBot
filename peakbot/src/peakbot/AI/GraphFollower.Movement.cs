// /AI/GraphFollower.Movement.cs
// -----------------------------------------------------------------------------
// Movement/interaction logic for GraphFollower: wall-attach jump, small-step
// climbs/hops with hop cooldown and escalation, ledge-gap detection,
// and input translation.
// -----------------------------------------------------------------------------

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        // Hop anti-spam & escalation.
        private float nextHopOkAt;
        private int consecutiveHops;

        /// <summary>
        /// Orchestrates movement decisions for the bot, including wall-attach
        /// jumps, small obstacle climbs/hops, ledge-gap jumps, and converting a
        /// world-space direction into local input for the Character.
        /// </summary>
        private void HandleMovement(Vector3 moveDir)
        {
            if (resting) return;

            bool staminaOK = RegularFrac() >= STAM_CLIMB_FRAC;

            // ── 1) Wall-attach jump ───────────────────────────────────────
            if (staminaOK && !RecentlyExhausted() && data.isGrounded && !data.isClimbing &&
                Time.time >= nextWallAttempt && Regular() >= STAM_ATTACH_ABS)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(moveDir, Vector3.up).normalized;
                const float wallProbeDist   = 2.2f;
                const float maxAttachHeight = 12.0f;
                float minHeightDiff = Mathf.Max(0.5f, bodyHeight * 0.33f);

                if (fwd.sqrMagnitude > 1e-4f &&
                    Physics.Raycast(HeadPos, fwd, out RaycastHit hit, wallProbeDist, terrainMask))
                {
                    float heightDiff = hit.point.y - ch.Center.y;
                    bool tallEnough = heightDiff > minHeightDiff && heightDiff < maxAttachHeight;
                    bool steepWall  = Vector3.Dot(hit.normal, fwd) < -0.5f;

                    if (tallEnough && steepWall)
                    {
                        float planar    = Vector3.ProjectOnPlane(hit.point - ch.Center, Vector3.up).magnitude;
                        float attachTax = 0.15f * planar; // absolute regular units
                        float burst     = 0.20f;
                        float headroom  = 0.10f;

                        if (Regular() < (attachTax + burst + headroom))
                        {
                            nextWallAttempt = Time.time + 1f;
                        }
                        else
                        {
                            float around = EstimateNavDistance(ch.Center, player.Center);
                            float direct = Vector3.Distance(ch.Center, player.Center);
                            bool worthClimb = !float.IsInfinity(around) && (around > direct * DETOUR_FACTOR);

                            if (VERBOSE_LOGS) Debug.Log($"[WallAttach] around={around:F2}, direct={direct:F2}, worthClimb={worthClimb}");

                            if (worthClimb)
                            {
                                StartCoroutine(JumpAndAttach());
                                nextWallAttempt = Time.time + attachFailDelay;

                                if (VERBOSE_LOGS) Debug.Log($"[WallAttach] scheduled next attempt @ {nextWallAttempt:F2}");

                                attachFailDelay = Mathf.Min(attachFailDelay * 2f, 4f);
                                consecutiveHops = 0;
                            }
                            else
                            {
                                nextWallAttempt = Time.time + 1f;
                            }
                        }
                    }
                }
            }

            // ── 2) Face-block small/medium step handling (body-aware) ─────
            if (staminaOK && !data.isClimbing && !RecentlyExhausted())
            {
                Vector3 fwd   = Vector3.ProjectOnPlane(moveDir, Vector3.up).normalized;
                Vector3 chest = ChestPos;

                if (VERBOSE_LOGS) Debug.Log($"[StepClimb] checking; climbing={data.isClimbing}");

                GetStepParams(out float STEP_MIN, out float STEP_MAX, out float STEP_RAY, out float STEP_RAD);

                if (fwd.sqrMagnitude > 1e-4f &&
                    Physics.SphereCast(chest, STEP_RAD, fwd, out RaycastHit hit, STEP_RAY, terrainMask))
                {
                    float step = hit.point.y - feetY;

                    if (VERBOSE_LOGS) Debug.Log($"[StepClimb] hit {hit.collider?.name ?? "col"} at {hit.point}, step={step:F2}, band=[{STEP_MIN:F2},{STEP_MAX:F2}]");

                    if (step >= STEP_MIN && step <= STEP_MAX)
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] ▶ TryClimb (medium step)");
                        MI_TryClimb?.Invoke(ch.refs.climbing, null);
                        ch.input.jumpWasPressed = false;
                        consecutiveHops = 0;
                    }
                    else if (step < STEP_MIN)
                    {
                        if (Time.time >= nextHopOkAt)
                        {
                            if (VERBOSE_LOGS) Debug.Log("[HandleMovement] ▶ Small hop");
                            ch.input.jumpWasPressed = true;
                            nextHopOkAt = Time.time + 0.25f;
                            if (++consecutiveHops >= 3)
                            {
                                if (VERBOSE_LOGS) Debug.Log("[HandleMovement] ▶ Escalate after hop spam → TryClimb");
                                MI_TryClimb?.Invoke(ch.refs.climbing, null);
                                consecutiveHops = 0;
                            }
                        }
                        else
                        {
                            ch.input.jumpWasPressed = false;
                        }
                    }
                    else // step > STEP_MAX → treat as wall/attach territory
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] ▶ Big step → prefer wall attach next");
                        ch.input.jumpWasPressed = false;
                        nextWallAttempt = Mathf.Min(nextWallAttempt, Time.time);
                    }
                }
                else
                {
                    ch.input.jumpWasPressed = false;
                }
            }

            // ── 3) Ledge-gap detection (body-aware probe) ────────────────
            if (staminaOK && !RecentlyExhausted() && Time.time >= nextLedgeAttempt && data.isGrounded && !data.isClimbing)
            {
                Vector3 probe = ChestPos + moveDir.normalized * Mathf.Max(0.8f, bodyRadius * 2f);
                bool groundAhead = Physics.Raycast(
                    probe,
                    Vector3.down,
                    Mathf.Max(1.2f, bodyHeight * 1.0f),
                    terrainMask
                );

                if (!groundAhead && FindLedgeLanding(moveDir, out Vector3 landing))
                {
                    float around = EstimateNavDistance(ch.Center, player.Center);
                    float direct = Vector3.Distance(ch.Center, player.Center);

                    if (!float.IsInfinity(around) && (around > direct * DETOUR_FACTOR))
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] ▶ Gap jump RPC");

                        data.lookValues = DirToLook((landing - ch.Center).normalized);
                        if (ch.refs.view.IsMine)
                            ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);

                        nextLedgeAttempt = Time.time + 0.5f;
                        consecutiveHops = 0;
                    }
                }
            }

            // ── 4) Convert world moveDir → local XY for CharacterInput. ───
            Vector3 local = ch.transform.InverseTransformDirection(moveDir);
            ch.input.movementInput = new Vector2(local.x * 0.75f, local.z).normalized;
        }

        /// <summary>
        /// Compute body-aware step/hop probe parameters (derived from bodyHeight/bodyRadius).
        /// </summary>
        private void GetStepParams(out float min, out float max, out float ray, out float rad)
        {
            min = bodyHeight * 0.20f;                               // previously 0.30f
            max = bodyHeight * 0.80f;                               // previously 1.40f
            ray = Mathf.Max(1.0f, bodyHeight * 0.90f);              // previously 1.6f
            rad = Mathf.Clamp(bodyRadius * 0.60f, 0.15f, 0.35f);    // previously 0.20f
        }

        /// <summary>
        /// Simplified NavJumper-style ledge scan (unchanged structure; uses current settings).
        /// </summary>
        private bool FindLedgeLanding(Vector3 forwardDir, out Vector3 best)
        {
            best = Vector3.zero;
            var hits = new List<RaycastHit>();
            Vector3 origin = ch.Center;

            for (int i = 0; i < LEDGE_CASTS; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * LEDGE_RADIUS;
                Vector3 start = origin + forwardDir.normalized * LEDGE_RADIUS + new Vector3(rnd.x, LEDGE_HEIGHT, rnd.y);
                if (Physics.Raycast(start, Vector3.down, out var h, LEDGE_HEIGHT * 2f, terrainMask))
                    hits.Add(h);
            }

            var flat    = hits.Where(h => Vector3.Angle(h.normal, Vector3.up) < 50f);
            var near    = flat.Where(h => Vector3.Distance(h.point, origin) < LEDGE_MAX_DIST);
            var forward = near.Where(h => Vector3.Dot(forwardDir, (h.point - origin).normalized) > 0.5f && h.point.y > origin.y);
            if (!forward.Any()) return false;

            best = forward
                .OrderByDescending(h => Vector3.Dot(forwardDir, h.point - origin))
                .First()
                .point;

            // Removed Debug.DrawLine to keep runtime-only and minimal.
            return true;
        }

        /// <summary>
        /// Convert a world-space direction to (yaw, pitch) look values.
        /// </summary>
        private static Vector2 DirToLook(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-4f) return Vector2.zero;
            dir.Normalize();
            float yaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(dir.y) * Mathf.Rad2Deg;
            return new Vector2(yaw, pitch);
        }

        /// <summary>
        /// Performs the physics jump then triggers TryClimb mid-air.
        /// </summary>
        private IEnumerator JumpAndAttach()
        {
            attachFailDelay = 1f; // success → reset back-off

            // Broadcast the real jump to all clients so animation syncs.
            if (ch.refs.view.IsMine)
                ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);

            // Wait a short moment to be airborne, then try to attach.
            yield return new WaitForSeconds(0.15f);

            MI_TryClimb?.Invoke(ch.refs.climbing, null);
            consecutiveHops = 0;
        }
    }
}
