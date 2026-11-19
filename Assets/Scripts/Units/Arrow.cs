using UnityEngine;
using UnityEngine.AI;

namespace RogueRTS
{
    /// <summary>
    /// 弓兵や砲台が放つ矢。放物線を描いて飛び、途中で最初に触れた敵にダメージを与えて消える。味方はすり抜ける。
    /// 地形 (地面・岩・防壁など、コライダーを持つもの) に当たるとそこに刺さって残り、しばらくすると消える。
    /// オブジェクトの原点が矢じりの先端で、+Z が飛ぶ向き。
    /// </summary>
    public class Arrow : MonoBehaviour
    {
        // 地形に遮られないかを調べるときに、飛ぶ道筋を分ける数
        const int ArcSegments = 6;
        const float MinFlightTime = 0.05f;
        const float Thickness = 0.05f;
        // 刺さったときに地形へめり込ませる長さ
        const float StickDepth = 0.12f;
        // 地面の外へ飛び出した矢は、ここまで落ちたら消す
        const float FallLimit = -2f;

        [Tooltip("水平方向に飛ぶ速さ (m/s)")]
        public float speed = 30f;
        [Tooltip("落ちる加速度 (m/s²)。大きいほど山なりに飛ぶ")]
        public float gravity = 12f;
        [Tooltip("地形に刺さってから消えるまでの秒数")]
        public float stuckDuration = 5f;

        Team team;
        float damage;
        Vector3 start;
        Vector3 velocity;
        float elapsed;
        // この時刻までは防壁を素通りする
        float overWallsUntil;

        /// <summary>
        /// 矢を放つ。aim を通る放物線で飛ぶ。target が動いていれば、着くころの位置を狙う。
        /// origin は射手の体の位置で、そこから from (弓の先) までの間にある敵や地形にも当たる。
        /// overWalls なら、aim に着くまでは防壁を素通りする (防壁の上から撃つ砲台用)。
        /// </summary>
        public void Launch(
            Team shooter, float hitDamage, Vector3 origin, Vector3 from, Vector3 aim, Health target, bool overWalls = false)
        {
            team = shooter;
            damage = hitDamage;
            start = from;

            if (target != null && target.TryGetComponent(out NavMeshAgent agent) && agent.enabled)
                aim += agent.velocity * FlightTime(from, aim);

            velocity = InitialVelocity(from, aim);
            overWallsUntil = overWalls ? FlightTime(from, aim) : 0f;
            transform.SetPositionAndRotation(from, Quaternion.LookRotation(velocity));
            Advance(origin, from, overWalls);
        }

        /// <summary>from から aim へ飛ばしたとき、途中で地形に当たらなければ true。</summary>
        public bool HasClearFlight(Vector3 from, Vector3 aim, Health target, bool overWalls = false)
        {
            Vector3 initial = InitialVelocity(from, aim);
            float duration = FlightTime(from, aim);
            Vector3 previous = from;
            for (int i = 1; i <= ArcSegments; i++)
            {
                Vector3 next = PositionAt(from, initial, duration * i / ArcSegments);
                if (!LineOfFire.IsClear(previous, next, target, overWalls)) return false;
                previous = next;
            }
            return true;
        }

        void Update()
        {
            // フレームが長引いても途中の防壁に刺さらないよう、この移動を始める時刻で判定する
            bool overWalls = elapsed < overWallsUntil;
            elapsed += Time.deltaTime;
            Vector3 next = PositionAt(start, velocity, elapsed);
            if (Advance(transform.position, next, overWalls)) return;

            transform.SetPositionAndRotation(next, Quaternion.LookRotation(velocity + Vector3.down * (gravity * elapsed)));
            if (next.y < FallLimit) Destroy(gameObject);
        }

        /// <summary>from から to まで進む間に、敵か地形に当たったら true。</summary>
        bool Advance(Vector3 from, Vector3 to, bool overWalls)
        {
            Vector3 step = to - from;
            float length = step.magnitude;
            if (length < 0.0001f) return false;

            Vector3 direction = step / length;
            bool hitSolid = LineOfFire.TryFindFirstSolid(from, direction, length, overWalls, out RaycastHit hit);

            Health victim = Health.FindFirstEnemyOnPath(team, from, to, Thickness, out float victimAlong);
            if (victim != null && (!hitSolid || victimAlong * length <= hit.distance))
            {
                victim.TakeDamage(damage);
                Destroy(gameObject);
                return true;
            }
            if (!hitSolid) return false;

            // 拠点のようにコライダーで当たる敵なら、ダメージを与えたうえで刺さる
            Health struck = hit.collider.GetComponentInParent<Health>();
            if (struck != null && struck.team != team) struck.TakeDamage(damage);

            transform.position = hit.point + direction * StickDepth;
            enabled = false;
            Destroy(gameObject, stuckDuration);
            return true;
        }

        float FlightTime(Vector3 from, Vector3 to) => Mathf.Max(Health.FlatDistance(from, to) / speed, MinFlightTime);

        Vector3 InitialVelocity(Vector3 from, Vector3 to)
        {
            float time = FlightTime(from, to);
            return (to - from) / time + Vector3.up * (0.5f * gravity * time);
        }

        Vector3 PositionAt(Vector3 from, Vector3 initial, float time)
        {
            return from + initial * time + Vector3.down * (0.5f * gravity * time * time);
        }
    }
}
