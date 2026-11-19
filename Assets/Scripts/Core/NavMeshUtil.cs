using UnityEngine;
using UnityEngine.AI;

namespace RogueRTS
{
    public static class NavMeshUtil
    {
        /// <summary>NavMesh 上の最寄りの点を返す。見つからなければ元の点をそのまま返す。</summary>
        public static Vector3 Snap(Vector3 position, float maxDistance)
        {
            return NavMesh.SamplePosition(position, out NavMeshHit hit, maxDistance, NavMesh.AllAreas)
                ? hit.position
                : position;
        }

        /// <summary>中心から半径 radius 以内のランダムな水平オフセット。</summary>
        public static Vector3 Scatter(float radius)
        {
            Vector2 circle = Random.insideUnitCircle * radius;
            return new Vector3(circle.x, 0f, circle.y);
        }
    }
}
