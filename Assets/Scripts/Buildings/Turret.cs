using UnityEngine;

namespace RogueRTS
{
    /// <summary>
    /// 防壁や門の上に置く、砲手つきのタレット。射程内の最も近い敵へ向きを変えて撃つ。
    /// 発射モーションがあれば「装填済みで構える → 撃つ → 巻き上げて装填 → 構える」の順に再生し、
    /// 矢を放つ瞬間に本物の矢を飛ばす。なければ攻撃間隔ごとにすぐ撃つ。ダメージは矢が当たったときに与える。
    /// 防壁の上から撃つので防壁には遮られないが、岩や拠点の陰にいる敵は狙えない。
    /// </summary>
    public class Turret : MonoBehaviour
    {
        const float ThinkInterval = 0.25f;
        const float TurnSpeed = 540f;
        // この角度まで向いたら撃ち始める
        const float AimTolerance = 12f;

        static readonly int ShootState = Animator.StringToHash("Shoot");
        static readonly int ShootSpeedId = Animator.StringToHash("ShootSpeed");

        public BuildingData data;
        [Tooltip("目標の方向へ水平に回す部分 (台座ごと砲手も回る)")]
        public Transform head;
        public Transform muzzle;
        [Tooltip("放つ矢。なければ線を引いて、攻撃はすぐ相手に当たる")]
        public Arrow arrowPrefab;
        public ShotLine shotLine;

        [Header("発射モーション (なければ攻撃間隔ごとにすぐ撃つ)")]
        public Animator animator;
        [Tooltip("発射モーション全体の長さ (秒)")]
        public float clipLength = 3f;
        [Tooltip("引き金を引き始める時刻 (秒)。装填済みの構えから、ここを再生して撃つ")]
        public float fireStart = 2.4f;
        [Tooltip("矢が放たれる時刻 (秒)")]
        public float releaseTime = 2.43f;
        [Tooltip("巻き上げと装填が終わって構えた姿勢の時刻 (秒)")]
        public float loadedTime = 2.3f;

        enum Phase { Loaded, Firing, Reloading }

        Health target;
        System.Func<Health, bool> canShoot;
        float thinkTimer;
        float cooldown;

        Phase phase;
        float clipTime;
        bool released;

        /// <summary>撃つ → 装填 → 構え の1周を、攻撃間隔に収める再生速度。</summary>
        float PlaybackSpeed =>
            Mathf.Max(1f, (clipLength - fireStart + loadedTime) / Mathf.Max(0.1f, data.attackInterval));

        void Start()
        {
            canShoot = HasClearShot;
            // 最初から装填済みで構えておく
            if (animator != null) Hold(loadedTime);
        }

        void Update()
        {
            cooldown -= Time.deltaTime;
            UpdateTarget();
            if (target != null) Aim();

            if (animator == null)
            {
                if (target != null && cooldown <= 0f)
                {
                    cooldown = data.attackInterval;
                    Shoot(target);
                }
                return;
            }

            UpdateMotion();
        }

        void UpdateTarget()
        {
            if (target != null && (target.IsDead || target.EdgeDistanceFrom(transform.position) > data.attackRange))
                target = null;

            thinkTimer -= Time.deltaTime;
            if (thinkTimer > 0f) return;

            thinkTimer = ThinkInterval;
            // 狙っている相手が地形の陰に入ったら、別の相手を探す
            if (target == null || !HasClearShot(target)) target = FindTarget();
        }

        Health FindTarget() => Health.FindNearestEnemy(Team.Player, transform.position, data.attackRange, canShoot);

        bool HasClearShot(Health t)
        {
            return arrowPrefab != null
                ? arrowPrefab.HasClearFlight(muzzle.position, t.AimPoint, t, overWalls: true)
                : LineOfFire.IsClear(muzzle.position, t.AimPoint, t, overWalls: true);
        }

        void UpdateMotion()
        {
            switch (phase)
            {
                case Phase.Loaded:
                    if (target != null && IsAimedAt(target)) Play(Phase.Firing, fireStart);
                    break;

                case Phase.Firing:
                    clipTime += Time.deltaTime * PlaybackSpeed;
                    if (!released && clipTime >= releaseTime)
                    {
                        released = true;
                        // 構えている間に狙いが倒れていたら、そのとき射程内にいる別の敵を撃つ
                        Health victim = target != null ? target : FindTarget();
                        if (victim != null) Shoot(victim);
                    }
                    if (clipTime >= clipLength) Play(Phase.Reloading, 0f);
                    break;

                case Phase.Reloading:
                    clipTime += Time.deltaTime * PlaybackSpeed;
                    if (clipTime >= loadedTime) Hold(loadedTime);
                    break;
            }
        }

        void Play(Phase next, float fromTime)
        {
            phase = next;
            clipTime = fromTime;
            released = false;
            animator.SetFloat(ShootSpeedId, PlaybackSpeed);
            animator.Play(ShootState, 0, fromTime / clipLength);
        }

        void Hold(float atTime)
        {
            phase = Phase.Loaded;
            clipTime = atTime;
            animator.SetFloat(ShootSpeedId, 0f);
            animator.Play(ShootState, 0, atTime / clipLength);
        }

        bool IsAimedAt(Health t)
        {
            Vector3 direction = t.transform.position - head.position;
            direction.y = 0f;
            return direction.sqrMagnitude < 0.01f || Vector3.Angle(head.forward, direction) <= AimTolerance;
        }

        void Aim()
        {
            Vector3 direction = target.transform.position - head.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;

            head.rotation = Quaternion.RotateTowards(
                head.rotation, Quaternion.LookRotation(direction), TurnSpeed * Time.deltaTime);
        }

        void Shoot(Health victim)
        {
            if (arrowPrefab != null)
            {
                Vector3 from = muzzle.position;
                Instantiate(arrowPrefab).Launch(Team.Player, data.attackDamage, from, from, victim.AimPoint, victim, overWalls: true);
                return;
            }

            // 撃つ直前に地形の陰へ入った相手には当たらない
            if (!HasClearShot(victim)) return;

            shotLine.Show(muzzle.position, victim.transform.position + Vector3.up);
            victim.TakeDamage(data.attackDamage);
        }
    }
}
