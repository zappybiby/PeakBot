// /AI/GraphFollower.Movement.cs
// -----------------------------------------------------------------------------
// Movement/interaction logic for GraphFollower: wall-attach jump, small-step
// climbs/hops, ledge-gap detection, and input translation.
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
        /// <summary>
        /// Orchestrates movement decisions for the bot, including wall-attach
        /// jumps, small obstacle climbs/hops, ledge-gap jumps, and converting a
        /// world-space direction into local input for the Character.
        /// </summary>
        private void HandleMovement(Vector3 moveDir)
        {
            // If intentionally resting, don’t waste energy or attempts.
            if (resting) return;

            bool staminaOK = RegularFrac() >= STAM_CLIMB_FRAC;

            // ── 1) Wall-attach jump ───────────────────────────────────────
            if (staminaOK && !RecentlyExhausted() && ch.data.isGrounded && !ch.data.isClimbing &&
                Time.time >= nextWallAttempt && Regular() >= STAM_ATTACH_ABS)
            {
                const float wallProbeDist = 1.6f;
                const float maxAttachHeight = 12.0f;
                const float minHeightDiff = 1.2f;

                if (Physics.Raycast(ch.Head, moveDir, out RaycastHit hit, wallProbeDist, terrainMask))
                {
                    float heightDiff = hit.point.y - ch.Center.y;
                    bool tallEnough = heightDiff > minHeightDiff && heightDiff < maxAttachHeight;
                    bool steepWall = Vector3.Dot(hit.normal, moveDir) < -0.7f;

                    if (tallEnough && steepWall)
                    {
                        // Budget attach tax from planar distance + burst + headroom.
                        float planar = Vector3.ProjectOnPlane(hit.point - ch.Center, Vector3.up).magnitude;
                        float attachTax = 0.15f * planar; // StartClimb tax (absolute regular units)
                        float burst = 0.20f;              // potential RPCA_ClimbJump later
                        float headroom = 0.10f;           // one tick + safety

                        if (Regular() < (attachTax + burst + headroom))
                        {
                            // Not enough regular to complete the opening sequence; path around and retry later.
                            nextWallAttempt = Time.time + 1f;
                        }
                        else
                        {
                            float around = EstimateNavDistance(ch.Center, player.Center);
                            float direct = Vector3.Distance(ch.Center, player.Center);
                            bool worthClimb = around > direct * DETOUR_FACTOR;
                            Debug.Log($"[WallAttach] around={around:F2}, direct={direct:F2}, DETOUR_FACTOR={DETOUR_FACTOR}, worthClimb={worthClimb}");

                            if (worthClimb)
                            {
                                // Try the physics jump + mid-air attach.
                                StartCoroutine(JumpAndAttach());
                                nextWallAttempt = Time.time + attachFailDelay;
                                Debug.Log($"[WallAttach] nextWallAttempt set to {nextWallAttempt:F2}, attachFailDelay was {attachFailDelay:F2}");

                                attachFailDelay = Mathf.Min(attachFailDelay * 2f, 4f); // exponential back-off
                            }
                            else
                            {
                                // Path around instead; retry after 1 s.
                                Debug.Log("[WallAttach] not worth climb; path around instead");
                                nextWallAttempt = Time.time + 1f;
                            }
                        }
                    }
                }
            }

            // ── 2) Face-block small step / simple climb ───────────────────
            if (staminaOK && !ch.data.isClimbing && !RecentlyExhausted())
            {
                Debug.Log($"[StepClimb] Checking for small obstacle; climbing={ch.data.isClimbing}");

                if (Physics.Raycast(ch.Head, moveDir, out RaycastHit hit, 1.5f, terrainMask))
                {
                    float heightDiff = hit.point.y - ch.Center.y;
                    Debug.Log($"[StepClimb] hit obstacle at {hit.point}, heightDiff={heightDiff:F2}");

                    if (heightDiff > 1.2f)
                    {
                        Debug.Log("[HandleMovement] ▶ Invoke climb");
                        MI_TryClimb?.Invoke(ch.refs.climbing, null);
                    }
                    else
                    {
                        Debug.Log("[HandleMovement] ▶ Small hop");
                        ch.input.jumpWasPressed = true; // small hop
                    }
                }
                else
                {
                    ch.input.jumpWasPressed = false;
                }
            }

            // ── 3) Ledge-gap detection ────────────────────────────────────
            if (staminaOK && !RecentlyExhausted() && Time.time >= nextLedgeAttempt && ch.data.isGrounded && !ch.data.isClimbing)
            {
                Vector3 probe = ch.Center + moveDir.normalized * 0.8f + Vector3.up * 0.3f;
                bool groundAhead = Physics.Raycast(probe, Vector3.down, 1.8f, terrainMask);

                if (!groundAhead && FindLedgeLanding(moveDir, out Vector3 landing))
                {
                    float around = EstimateNavDistance(ch.Center, player.Center);
                    float direct = Vector3.Distance(ch.Center, player.Center);

                    if (around > direct * DETOUR_FACTOR)
                    {
                        Debug.Log("[HandleMovement] ▶ Gap jump RPC");

                        ch.data.lookValues = DirToLook((landing - ch.Center).normalized);
                        if (ch.refs.view.IsMine)
                            ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);

                        nextLedgeAttempt = Time.time + 0.5f;
                    }
                }
            }

            // ── 4) Convert world moveDir → local XY for CharacterInput.
            Vector3 local = ch.transform.InverseTransformDirection(moveDir);
            ch.input.movementInput = new Vector2(local.x * 0.75f, local.z).normalized;
        }

        /// <summary>
        /// Simplified NavJumper-style ledge scan.
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

            var flat = hits.Where(h => Vector3.Angle(h.normal, Vector3.up) < 50f);
            var near = flat.Where(h => Vector3.Distance(h.point, origin) < LEDGE_MAX_DIST);
            var forward = near.Where(h => Vector3.Dot(forwardDir, (h.point - origin).normalized) > 0.5f && h.point.y > origin.y);
            if (!forward.Any()) return false;

            best = forward
                .OrderByDescending(h => Vector3.Dot(forwardDir, h.point - origin))
                .First()
                .point;

            Debug.DrawLine(origin + Vector3.up * 0.1f, best + Vector3.up * 0.1f, Color.green, 1f);
            return true;
        }

        /// <summary>
        /// Convert a world-space direction to (yaw, pitch) look values.
        /// </summary>
        private static Vector2 DirToLook(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-4f) return Vector2.zero;
            dir.Normalize();
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
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

            // Wait ~¼ second to reach apex then call TryClimb (private).
            yield return new WaitForSeconds(0.15f);

            MI_TryClimb?.Invoke(ch.refs.climbing, null);
        }
    }
}
