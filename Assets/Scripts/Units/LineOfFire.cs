using UnityEngine;

namespace RogueRTS
{
    /// <summary>攻撃が地形に遮られないかを調べる。コライダーを持つもの (地面・岩・防壁・拠点) を地形として扱う。</summary>
    public static class LineOfFire
    {
        static readonly RaycastHit[] hits = new RaycastHit[8];

        /// <summary>
        /// from から to までの間に地形がなければ true。target 自身のコライダーは遮るものに数えない。
        /// overWalls なら防壁も遮るものに数えない (防壁の上から撃つタレット用)。
        /// </summary>
        public static bool IsClear(Vector3 from, Vector3 to, Health target, bool overWalls = false)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.001f) return true;

            int count = Physics.RaycastNonAlloc(
                from, delta / distance, hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider collider = hits[i].collider;
                if (collider.transform.IsChildOf(target.transform)) continue;
                if (overWalls && collider.GetComponentInParent<Structure>() != null) continue;
                return false;
            }
            return true;
        }

        /// <summary>from から direction へ distance 進む間に最初に当たる地形。overWalls なら防壁は素通りする。</summary>
        public static bool TryFindFirstSolid(
            Vector3 from, Vector3 direction, float distance, bool overWalls, out RaycastHit first)
        {
            first = default;
            bool found = false;
            int count = Physics.RaycastNonAlloc(
                from, direction, hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (found && hits[i].distance >= first.distance) continue;
                if (overWalls && hits[i].collider.GetComponentInParent<Structure>() != null) continue;

                first = hits[i];
                found = true;
            }
            return found;
        }
    }
}
