using System;
using System.Collections.Generic;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>HPを持ち、攻撃対象になるもの (ユニット・拠点)。</summary>
    public class Health : MonoBehaviour
    {
        public Team team;
        public float maxHp = 100f;
        [Tooltip("攻撃側が射程を測るときに使う、この対象の半径")]
        public float radius = 0.5f;
        [Tooltip("矢が当たる体の高さ")]
        public float height = 2f;
        public bool destroyOnDeath = true;

        public float CurrentHp { get; private set; }
        public bool IsDead { get; private set; }
        /// <summary>遠距離攻撃で狙う位置 (胸のあたり)。</summary>
        public Vector3 AimPoint => transform.position + Vector3.up * (height * 0.6f);

        /// <summary>ダメージを受けて、まだ生きているときに呼ばれる。</summary>
        public event Action<Health> Damaged;
        /// <summary>攻撃を防いだときに呼ばれる。</summary>
        public event Action<Health> Blocked;
        public static event Action<Health> AnyBlocked;
        public event Action<Health> Died;
        public static event Action<Health> AnyDied;
        /// <summary>ダメージが入ったときに呼ばれる (撃破した一撃も含む)。受けた側とダメージ量を渡す。</summary>
        public static event Action<Health, float> AnyDamaged;
        /// <summary>HP が回復したときに呼ばれる。回復した側と、実際に回復した量を渡す。</summary>
        public static event Action<Health, float> AnyHealed;

        static readonly List<Health> all = new List<Health>();

        // コライダーを持つ対象 (拠点) には、矢はコライダーで当たる
        bool hasCollider;

        void Awake()
        {
            CurrentHp = maxHp;
            hasCollider = GetComponentInChildren<Collider>() != null;
        }

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        [Tooltip("攻撃を受けたとき、ダメージを受けずに防ぐ確率 (0〜1)。ユニットは UnitData の値が入る")]
        [Range(0f, 1f)] public float blockChance;

        /// <summary>ダメージを受ける。blockChance の確率で防ぐ。防いだときや、すでに倒れているときは false。</summary>
        public bool TakeDamage(float amount)
        {
            if (IsDead) return false;

            if (blockChance > 0f && UnityEngine.Random.value < blockChance)
            {
                Blocked?.Invoke(this);
                AnyBlocked?.Invoke(this);
                return false;
            }

            CurrentHp = Mathf.Max(0f, CurrentHp - amount);
            AnyDamaged?.Invoke(this, amount);
            if (CurrentHp > 0f)
            {
                Damaged?.Invoke(this);
                return true;
            }

            IsDead = true;
            Died?.Invoke(this);
            AnyDied?.Invoke(this);
            if (destroyOnDeath) Destroy(gameObject);
            return true;
        }

        public void HealFull()
        {
            if (!IsDead) CurrentHp = maxHp;
        }

        /// <summary>HP を amount だけ回復する (最大HP まで)。倒れていたら何もしない。実際に回復した量を返す。</summary>
        public float Heal(float amount)
        {
            if (IsDead) return 0f;

            float healed = Mathf.Min(amount, maxHp - CurrentHp);
            if (healed <= 0f) return 0f;

            CurrentHp += healed;
            AnyHealed?.Invoke(this, healed);
            return healed;
        }

        public bool IsWounded => !IsDead && CurrentHp < maxHp;

        /// <summary>最大HPを変更する。現在HPは割合を保ったまま追従する。</summary>
        public void SetMaxHp(float newMaxHp)
        {
            float ratio = maxHp > 0f ? CurrentHp / maxHp : 1f;
            maxHp = newMaxHp;
            CurrentHp = newMaxHp * ratio;
        }

        public static int CountAlive(Team team)
        {
            int count = 0;
            foreach (Health h in all)
            {
                if (h.team == team && !h.IsDead) count++;
            }
            return count;
        }

        /// <summary>高さを無視した水平距離。</summary>
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>from から対象の外周までの距離。</summary>
        public float EdgeDistanceFrom(Vector3 from)
        {
            return Mathf.Max(0f, FlatDistance(from, transform.position) - radius);
        }

        /// <summary>myTeam から見た敵のうち、外周まで range 以内にいるものをすべて results に入れる。</summary>
        public static void CollectEnemiesWithin(Team myTeam, Vector3 from, float range, List<Health> results)
        {
            foreach (Health h in all)
            {
                if (h.team != myTeam && !h.IsDead && h.EdgeDistanceFrom(from) <= range) results.Add(h);
            }
        }

        /// <summary>
        /// myTeam から見た敵のうち、maxRange 以内で最も近いものを返す。いなければ null。
        /// filter を渡すと、それが true を返す相手だけを候補にする。
        /// </summary>
        public static Health FindNearestEnemy(Team myTeam, Vector3 from, float maxRange, Func<Health, bool> filter = null)
        {
            Health best = null;
            float bestDistance = maxRange;
            foreach (Health h in all)
            {
                if (h.team == myTeam || h.IsDead) continue;

                float distance = h.EdgeDistanceFrom(from);
                if (distance > bestDistance) continue;
                if (filter != null && !filter(h)) continue;

                best = h;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// a から b へ飛ぶ太さ thickness の物体が最初に触れる、myTeam から見た敵。いなければ null。
        /// along は触れた位置の割合 (a が 0, b が 1)。
        /// </summary>
        public static Health FindFirstEnemyOnPath(Team myTeam, Vector3 a, Vector3 b, float thickness, out float along)
        {
            Health first = null;
            along = 1f;
            var origin = new Vector2(a.x, a.z);
            var flat = new Vector2(b.x - a.x, b.z - a.z);
            float flatSqr = flat.sqrMagnitude;
            foreach (Health h in all)
            {
                if (h.team == myTeam || h.IsDead || h.hasCollider) continue;

                Vector3 feet = h.transform.position;
                Vector2 toCenter = new Vector2(feet.x, feet.z) - origin;
                float closest = flatSqr > 0f ? Mathf.Clamp01(Vector2.Dot(toCenter, flat) / flatSqr) : 0f;
                if (first != null && closest >= along) continue;
                if ((toCenter - flat * closest).magnitude > h.radius + thickness) continue;

                float heightAboveFeet = Mathf.Lerp(a.y, b.y, closest) - feet.y;
                if (heightAboveFeet < 0f || heightAboveFeet > h.height) continue;

                first = h;
                along = closest;
            }
            return first;
        }
    }
}
