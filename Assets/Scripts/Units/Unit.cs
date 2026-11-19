using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RogueRTS
{
    /// <summary>
    /// 移動と自動戦闘を行うユニット。
    /// 待機中・攻撃移動中は索敵範囲内の最も近い敵を追って攻撃し、移動命令中は敵を無視する。
    /// UnitAnimator があれば、攻撃モーションの当たりの瞬間にダメージを与え、被弾すると怯んで少し硬直し、
    /// 撃破時は倒れてから消える。矢を持つユニットは、その瞬間に矢を放つ (ダメージは矢が当たったときに与える)。
    /// 地形に遮られて届かない相手は攻撃せず、届く所まで歩いて回り込む。
    /// 回復役 (UnitData.healAmount が 0 より大きい) は攻撃せず、敵の代わりに索敵範囲内で最も HP の低い味方 (自分以外) へ近づき、
    /// 攻撃と同じ間隔とモーションの流れで回復する。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent), typeof(Health))]
    public class Unit : MonoBehaviour
    {
        const float ThinkInterval = 0.25f;
        const float TurnSpeed = 720f;
        // 射程がこれ以下の攻撃を近接攻撃とみなし、当てた相手を押し戻す
        const float MeleeRangeLimit = 3f;
        const float MaxKnockback = 1.5f;
        const float KnockbackDuration = 0.15f;

        public UnitData data;
        public GameObject selectionRing;
        public ShotLine shotLine;
        [Tooltip("放つ矢。なければ攻撃はすぐ相手に当たる")]
        public Arrow arrowPrefab;
        [Tooltip("矢を放つ位置 (このユニットの直下に置く)")]
        public Transform muzzle;
        [Tooltip("体の大きさや武器の長さのぶん、射程に足す距離 (大型のユニット用)")]
        public float reachBonus;
        [Tooltip("近接攻撃を受けたとき、ダメージ1あたりに押し戻される距離 (m)。0 なら押し戻されない")]
        public float knockbackPerDamage = 0.05f;

        static readonly List<Unit> all = new List<Unit>();
        public static IReadOnlyList<Unit> All => all;

        // 範囲攻撃で巻き込む相手を集める作業用
        static readonly List<Health> nearbyEnemies = new List<Health>();

        public Health Health { get; private set; }
        public Team Team => Health.team;

        enum Order { Idle, Move, AttackMove }

        NavMeshAgent agent;
        UnitAnimator animations;
        RunModifiers mods;
        Order order;
        Vector3 orderDestination;
        Health target;
        bool targetInSight;
        float thinkTimer;
        float attackCooldown;
        float stuckTime;

        // 敵専用: 防壁に阻まれて歩いて届かない相手を狙わないための情報
        BuildGrid grid;
        System.Func<Health, bool> canEngage;

        System.Func<Health, bool> canStrike;

        // ボスなど、この個体だけに掛かる倍率 (陣営全体の補正とは別)
        float hpMultiplier = 1f;
        float speedMultiplier = 1f;
        float damageMultiplier = 1f;
        float bountyMultiplier = 1f;

        // 攻撃モーション中: 当たりの瞬間にダメージを与え、振り終わるまでその場にとどまる
        UnitAnimator.AttackMotion swingMotion;
        Health swingTarget;
        Vector3 swingAimPoint;
        float swingTimer;
        float swingHitTimer;

        // 被弾による硬直と、続けて怯まない時間
        float stunTimer;
        float hurtImmunityTimer;

        // 近接攻撃で押し戻されている間の初速と残り時間
        Vector3 knockbackVelocity;
        float knockbackTimer;

        float AttackRange => data.attackRange * mods.rangeMul + reachBonus;
        float Damage => data.attackDamage * mods.damageMul * damageMultiplier;
        bool IsMelee => data.attackRange <= MeleeRangeLimit;
        bool Heals => data.healAmount > 0f;

        /// <summary>撃破したときにプレイヤーが得るコイン。</summary>
        public int Bounty => Mathf.RoundToInt(data.bounty * bountyMultiplier);

        void Awake()
        {
            Health = GetComponent<Health>();
            agent = GetComponent<NavMeshAgent>();
            // 全ユニットの索敵が同じフレームに重ならないようにずらす
            thinkTimer = Random.value * ThinkInterval;
            canStrike = HasClearShot;
            SetSelected(false);

            TryGetComponent(out animations);
            Health.Damaged += OnDamaged;
            Health.Blocked += OnBlocked;
            Health.Died += OnDied;
        }

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        void Start()
        {
            RefreshStats();

            BuildController build = GameManager.Instance.buildController;
            if (Team == Team.Enemy && build != null)
            {
                grid = build.Grid;
                canEngage = CanEngage;
            }
        }

        /// <summary>データと陣営補正から実際の能力値を反映し直す。</summary>
        public void RefreshStats()
        {
            mods = GameManager.Instance.ModsFor(Team);
            Health.SetMaxHp(data.maxHp * mods.hpMul * hpMultiplier);
            Health.blockChance = data.blockChance;
            agent.speed = data.moveSpeed * mods.moveSpeedMul * speedMultiplier;
        }

        /// <summary>ボスなど、この個体だけの能力倍率を設定する。</summary>
        public void SetIndividualMultipliers(float hp, float speed, float damage, float bounty)
        {
            hpMultiplier = hp;
            speedMultiplier = speed;
            damageMultiplier = damage;
            bountyMultiplier = bounty;
            RefreshStats();
        }

        public void SetSelected(bool selected)
        {
            if (selectionRing == null) return;

            selectionRing.SetActive(selected);
            RefreshSelectionRing();
        }

        /// <summary>F1 で UI を隠している間は、選択中でも選択リングを描かない。</summary>
        public void RefreshSelectionRing()
        {
            if (selectionRing == null) return;

            GameManager game = GameManager.Instance;
            selectionRing.GetComponent<Renderer>().enabled = game == null || game.UiVisible;
        }

        /// <summary>敵を無視して目的地へ移動する。到着後は待機に戻る。</summary>
        public void MoveTo(Vector3 destination)
        {
            if (!agent.isOnNavMesh) return;

            order = Order.Move;
            target = null;
            stuckTime = 0f;
            agent.isStopped = false;
            agent.SetDestination(destination);
        }

        /// <summary>道中の敵と戦いながら目的地へ向かう。</summary>
        public void AttackMoveTo(Vector3 destination)
        {
            if (!agent.isOnNavMesh) return;

            order = Order.AttackMove;
            orderDestination = destination;
            agent.isStopped = false;
            agent.SetDestination(destination);
        }

        void Update()
        {
            attackCooldown -= Time.deltaTime;
            hurtImmunityTimer -= Time.deltaTime;

            if (!agent.isOnNavMesh) return;

            // 押し戻しは硬直中や攻撃中にも進める
            if (knockbackTimer > 0f) UpdateKnockback();

            // 硬直中は移動命令を受けても動かない
            if (stunTimer > 0f)
            {
                stunTimer -= Time.deltaTime;
                agent.isStopped = stunTimer > 0f;
                return;
            }

            if (swingTimer > 0f)
            {
                UpdateSwing();
                return;
            }

            if (order == Order.Move)
            {
                UpdateMoveOrder();
                return;
            }

            // 目標が倒れたら (回復役なら目標が全快したら) すぐ次を探す
            bool targetLost = !ReferenceEquals(target, null) && (target == null || target.IsDead || (Heals && !target.IsWounded));
            if (targetLost) target = null;

            thinkTimer -= Time.deltaTime;
            if (thinkTimer <= 0f || targetLost)
            {
                thinkTimer = ThinkInterval;
                Think();
            }

            if (target == null) return;

            if (targetInSight && InAttackRange(target))
            {
                agent.isStopped = true;
                Face(target.transform.position);
                if (attackCooldown <= 0f) Attack();
            }
            else
            {
                agent.isStopped = false;
            }
        }

        void UpdateMoveOrder()
        {
            if (agent.pathPending) return;

            bool arrived = agent.remainingDistance <= agent.stoppingDistance + 0.1f;
            // 味方に押されて目的地に入れない場合も到着扱いにする
            stuckTime = agent.velocity.sqrMagnitude < 0.05f ? stuckTime + Time.deltaTime : 0f;
            if (!arrived && stuckTime < 0.75f) return;

            order = Order.Idle;
            agent.ResetPath();
        }

        void Think()
        {
            if (Heals)
            {
                target = FindAllyToHeal();
            }
            else
            {
                // 今すぐ攻撃できる相手を優先し、いなければ歩いて近づける最も近い相手を追う
                float strikeRange = Mathf.Min(AttackRange, data.aggroRange);
                target = Health.FindNearestEnemy(Team, transform.position, strikeRange, canStrike);
                if (target == null) target = Health.FindNearestEnemy(Team, transform.position, data.aggroRange, canEngage);
            }
            targetInSight = target != null && HasClearShot(target);

            if (target != null)
            {
                if (!(targetInSight && InAttackRange(target)) && TryGetChasePoint(target, out Vector3 chasePoint))
                {
                    agent.isStopped = false;
                    agent.SetDestination(chasePoint);
                }
                return;
            }

            agent.isStopped = false;
            if (order == Order.AttackMove)
            {
                if ((agent.destination - orderDestination).sqrMagnitude > 0.25f)
                    agent.SetDestination(orderDestination);
            }
            else if (agent.hasPath)
            {
                agent.ResetPath();
            }
        }

        /// <summary>索敵範囲内で傷ついている味方ユニット (自分以外) のうち、HP が最も低いもの。同じなら近いほう。いなければ null。</summary>
        Health FindAllyToHeal()
        {
            Health best = null;
            float bestDistance = 0f;
            foreach (Unit ally in all)
            {
                Health h = ally.Health;
                if (ally == this || ally.Team != Team || !h.IsWounded) continue;

                float distance = h.EdgeDistanceFrom(transform.position);
                if (distance > data.aggroRange) continue;
                if (best != null && (h.CurrentHp > best.CurrentHp || (h.CurrentHp == best.CurrentHp && distance >= bestDistance)))
                    continue;

                best = h;
                bestDistance = distance;
            }
            return best;
        }

        bool InAttackRange(Health t) => t.EdgeDistanceFrom(transform.position) <= AttackRange;

        /// <summary>歩いて近づける相手だけを狙う。</summary>
        bool CanEngage(Health t) => TryGetChasePoint(t, out _);

        /// <summary>今いる場所から t への攻撃が、地形に遮られないか。</summary>
        bool HasClearShot(Health t)
        {
            Vector3 body = Health.AimPoint;
            Vector3 aim = t.AimPoint;
            // 矢を持たないユニットと、弓の先より近い相手には、体からまっすぐ届くかを見る
            if (arrowPrefab == null || t.EdgeDistanceFrom(transform.position) <= muzzle.localPosition.z)
                return LineOfFire.IsClear(body, aim, t);

            // 相手の方を向いて放つときの、弓の先の位置から調べる
            Vector3 toTarget = t.transform.position - transform.position;
            toTarget.y = 0f;
            Vector3 from = transform.position + Quaternion.LookRotation(toTarget) * muzzle.localPosition;
            return LineOfFire.IsClear(body, from, t) && arrowPrefab.HasClearFlight(from, aim, t);
        }

        /// <summary>対象へ近づくための目的地。歩いて辿り着けない相手なら false。</summary>
        bool TryGetChasePoint(Health t, out Vector3 point)
        {
            if (grid != null)
            {
                if (!grid.TryGetEnemyApproach(transform.position, t.transform.position, t.radius, out point))
                    return false;
                point = NavMeshUtil.Snap(point, 1.5f);
                return true;
            }

            // 対象の外周上で自分に最も近い点。拠点のように NavMesh がくり抜かれた対象でも近づけるようにする
            Vector3 toSelf = transform.position - t.transform.position;
            toSelf.y = 0f;
            point = NavMeshUtil.Snap(t.transform.position + toSelf.normalized * t.radius, 3f);
            return true;
        }

        void Face(Vector3 point)
        {
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(direction), TurnSpeed * Time.deltaTime);
        }

        void Attack()
        {
            float interval = data.attackInterval / mods.attackSpeedMul;

            if (animations != null)
            {
                StartSwing(interval);
                return;
            }

            attackCooldown = interval;
            if (Heals)
            {
                target.Heal(data.healAmount);
                return;
            }
            if (shotLine != null)
                shotLine.Show(transform.position + Vector3.up, target.transform.position + Vector3.up);
            Strike(target);
        }

        /// <summary>攻撃モーションを始める。ダメージは当たりの瞬間 (UpdateSwing) に与える。</summary>
        void StartSwing(float interval)
        {
            UnitAnimator.AttackMotion motion = animations.ChooseAttack(CountEnemiesInFront());
            float playbackSpeed = animations.AttackPlaybackSpeed(motion, interval);

            swingMotion = motion;
            swingTarget = target;
            swingAimPoint = target.AimPoint;
            swingTimer = motion.duration / playbackSpeed;
            swingHitTimer = motion.hitTime / playbackSpeed;
            attackCooldown = Mathf.Max(interval, swingTimer);
            animations.PlayAttack(motion, playbackSpeed);
        }

        void UpdateSwing()
        {
            swingTimer -= Time.deltaTime;
            if (swingMotion == null) return;

            // 弓は放つ瞬間まで狙いを追う。相手が倒れたら、最後に狙った所へ放つ
            if (arrowPrefab != null && swingTarget != null && !swingTarget.IsDead)
            {
                swingAimPoint = swingTarget.AimPoint;
                Face(swingAimPoint);
            }

            swingHitTimer -= Time.deltaTime;
            if (swingHitTimer > 0f) return;

            Hit(swingMotion);
            swingMotion = null;
            swingTarget = null;
        }

        // 振りかぶる間に相手が少し動いても当たるよう、射程に余裕を持たせる
        float HitReach => AttackRange + 1f;

        void Hit(UnitAnimator.AttackMotion motion)
        {
            if (Heals)
            {
                // 狙った味方が倒れたり離れすぎたりしていなければ回復する
                if (swingTarget != null && !swingTarget.IsDead && swingTarget.EdgeDistanceFrom(transform.position) <= HitReach)
                    swingTarget.Heal(data.healAmount);
                return;
            }

            if (arrowPrefab != null)
            {
                Health aimedAt = swingTarget != null && !swingTarget.IsDead ? swingTarget : null;
                Instantiate(arrowPrefab).Launch(Team, Damage, Health.AimPoint, muzzle.position, swingAimPoint, aimedAt);
                return;
            }

            if (motion.hitsAllInFront)
            {
                nearbyEnemies.Clear();
                Health.CollectEnemiesWithin(Team, transform.position, HitReach, nearbyEnemies);
                // ダメージで相手が倒れても一覧が変わらないよう、写しを回す
                foreach (Health enemy in nearbyEnemies.ToArray())
                {
                    if (IsInFront(enemy) && HasClearShot(enemy)) Strike(enemy, motion.knockbackMultiplier);
                }
                return;
            }

            if (swingTarget == null || swingTarget.IsDead || swingTarget.EdgeDistanceFrom(transform.position) > HitReach)
                return;
            if (!HasClearShot(swingTarget)) return;

            if (shotLine != null)
                shotLine.Show(transform.position + Vector3.up * 1.4f, swingTarget.transform.position + Vector3.up);
            Strike(swingTarget, motion.knockbackMultiplier);
        }

        /// <summary>
        /// ダメージを与える。近接攻撃なら、当たった相手をダメージに比例して押し戻す (knockbackScale 倍)。
        /// 防がれたときは押し戻さない。
        /// </summary>
        void Strike(Health victim, float knockbackScale = 1f)
        {
            float damage = Damage;
            if (!victim.TakeDamage(damage)) return;
            if (IsMelee && !victim.IsDead && victim.TryGetComponent(out Unit unit))
                unit.Knockback(transform.position, damage * knockbackScale);
        }

        /// <summary>from から離れる向きに、damage に比例した距離だけ押し戻される (knockbackPerDamage が 0 なら何もしない)。</summary>
        public void Knockback(Vector3 from, float damage)
        {
            if (!enabled || knockbackPerDamage <= 0f || !agent.isOnNavMesh) return;

            Vector3 away = transform.position - from;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = -transform.forward;

            // 一定の割合で減速しながら、KnockbackDuration の間にちょうど distance だけ動く初速
            float distance = Mathf.Min(damage * knockbackPerDamage, MaxKnockback);
            knockbackVelocity = away.normalized * (2f * distance / KnockbackDuration);
            knockbackTimer = KnockbackDuration;
            // 押し戻されている間は自分では動かない (歩いて打ち消さないように)
            stunTimer = Mathf.Max(stunTimer, KnockbackDuration);
        }

        void UpdateKnockback()
        {
            float before = knockbackTimer;
            knockbackTimer = Mathf.Max(0f, knockbackTimer - Time.deltaTime);
            // このフレームの間の平均速度 (初速 x 残り時間の割合の平均) で動かす。NavMesh の外へは出ない
            float averageSpeedRatio = (before + knockbackTimer) / (2f * KnockbackDuration);
            agent.Move(knockbackVelocity * ((before - knockbackTimer) * averageSpeedRatio));
        }

        /// <summary>正面側 (真横より少し後ろまで) にいるか。薙ぎ払いやシールドバッシュなど、範囲攻撃の当たり判定に使う。</summary>
        bool IsInFront(Health other)
        {
            Vector3 direction = other.transform.position - transform.position;
            direction.y = 0f;
            return direction.sqrMagnitude < 0.01f || Vector3.Dot(transform.forward, direction.normalized) > -0.3f;
        }

        int CountEnemiesInFront()
        {
            if (animations.attacks.Length < 2) return 1;

            nearbyEnemies.Clear();
            Health.CollectEnemiesWithin(Team, transform.position, AttackRange, nearbyEnemies);
            int count = 0;
            foreach (Health enemy in nearbyEnemies)
            {
                if (IsInFront(enemy) && HasClearShot(enemy)) count++;
            }
            return count;
        }

        /// <summary>
        /// モーション付きのユニットは被弾すると怯み、少しの間動けない。当たる前の攻撃は中断し、
        /// 硬直が解けたらすぐ振り直せるようにする。怯んだ直後はしばらく続けて怯まない。
        /// </summary>
        void OnDamaged(Health _)
        {
            if (animations == null || !animations.hasHurt || hurtImmunityTimer > 0f) return;

            if (swingMotion != null) attackCooldown = 0f;
            swingMotion = null;
            swingTarget = null;
            swingTimer = 0f;

            stunTimer = animations.hurtStun;
            hurtImmunityTimer = animations.hurtImmunity;
            if (agent.isOnNavMesh) agent.isStopped = true;
            animations.PlayHurt();
        }

        /// <summary>
        /// 攻撃を防いだら盾を構え、少しの間その場にとどまる。当たる前の自分の攻撃は中断し、構えを解いたらすぐ振り直す。
        /// </summary>
        void OnBlocked(Health _)
        {
            if (animations == null || !animations.hasBlock) return;

            if (swingMotion != null) attackCooldown = 0f;
            swingMotion = null;
            swingTarget = null;
            swingTimer = 0f;

            stunTimer = Mathf.Max(stunTimer, animations.blockRecovery);
            if (agent.isOnNavMesh) agent.isStopped = true;
            animations.PlayBlock();
        }

        /// <summary>モーション付きのユニットは倒れるモーションを見せてから消える (それ以外は Health がすぐ消す)。</summary>
        void OnDied(Health _)
        {
            if (animations == null) return;

            swingMotion = null;
            swingTarget = null;
            SetSelected(false);
            agent.enabled = false;
            enabled = false;
            animations.PlayDeath();
            Destroy(gameObject, animations.corpseDuration);
        }
    }
}
