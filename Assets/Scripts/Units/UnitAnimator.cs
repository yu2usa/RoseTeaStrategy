using System;
using UnityEngine;
using UnityEngine.AI;

namespace RogueRTS
{
    /// <summary>
    /// モデルのアニメーションをユニットの行動に合わせる。移動中は歩行、攻撃で攻撃モーション、
    /// 被弾で怯み、撃破で倒れるモーション。攻撃・被弾・撃破のタイミングは Unit から呼ばれる。
    /// このコンポーネントがあると、Unit は攻撃モーションの当たりの瞬間にダメージを与える。
    /// </summary>
    [RequireComponent(typeof(Unit))]
    public class UnitAnimator : MonoBehaviour
    {
        static readonly int MovingId = Animator.StringToHash("Moving");
        static readonly int WalkSpeedId = Animator.StringToHash("WalkSpeed");
        static readonly int AttackSpeedId = Animator.StringToHash("AttackSpeed");
        static readonly int HurtId = Animator.StringToHash("Hurt");
        static readonly int BlockId = Animator.StringToHash("Block");
        static readonly int DieId = Animator.StringToHash("Die");

        [Serializable]
        public class AttackMotion
        {
            [Tooltip("Animator のトリガー名。同じ名前のステートを再生する")]
            public string trigger = "Attack";
            [Tooltip("モーションの長さ (秒, 等倍再生時)")]
            public float duration = 1f;
            [Tooltip("モーションの開始から、武器が当たる (矢を放つ) までの秒数 (等倍再生時)")]
            public float hitTime = 0.5f;
            [Tooltip("射程内で正面側にいる敵全員に当たる (薙ぎ払い)。オフなら狙った1体だけ")]
            public bool hitsAllInFront;
            [Tooltip("射程内の正面側に、敵がこの数以上いるときだけ使う")]
            public int minTargets = 1;
            [Tooltip("条件を満たしたとき、実際にこの攻撃を使う確率 (0〜1)。外れたら次の攻撃を見る")]
            [Range(0f, 1f)] public float useChance = 1f;
            [Tooltip("当たった相手を押し戻す距離の倍率 (近接攻撃のみ)")]
            public float knockbackMultiplier = 1f;
        }

        public Animator animator;

        [Header("歩行")]
        [Tooltip("歩行モーションを等倍で再生したときに進む速さ (m/s)。移動速度に合わせて再生速度を変え、足の滑りを防ぐ")]
        public float walkCycleSpeed = 1.5f;
        [Tooltip("歩行モーションの再生速度の上限")]
        public float maxWalkPlaybackSpeed = 3f;

        [Header("攻撃 (上から順に、使える条件を満たした最初のものを使う)")]
        public AttackMotion[] attacks = { new AttackMotion() };
        [Tooltip("攻撃間隔がモーションより短いとき、モーションをこの倍率まで速めて間隔に合わせる。1 なら速めない")]
        public float maxAttackPlaybackSpeed = 1.6f;

        [Header("被弾")]
        [Tooltip("被弾モーションがあるか。なければ怯まない")]
        public bool hasHurt;
        [Tooltip("被弾したときに動けなくなる秒数")]
        public float hurtStun = 0.2f;
        [Tooltip("怯んだあと、続けて怯まない秒数。攻撃を出せないまま殴られ続けるのを防ぐ")]
        public float hurtImmunity = 1.5f;

        [Header("ブロック")]
        [Tooltip("攻撃を防いだときのモーションがあるか")]
        public bool hasBlock;
        [Tooltip("攻撃を防いだあと、盾を構えたまま動かない秒数")]
        public float blockRecovery = 0.3f;

        [Header("撃破")]
        [Tooltip("倒れ始めてから消えるまでの秒数 (倒れるモーションの長さ + 余韻)")]
        public float corpseDuration = 4f;

        NavMeshAgent agent;

        void Awake() => agent = GetComponent<NavMeshAgent>();

        void Update()
        {
            float speed = agent.enabled ? agent.velocity.magnitude : 0f;
            animator.SetBool(MovingId, speed > 0.1f);
            float playback = walkCycleSpeed > 0f ? speed / walkCycleSpeed : 1f;
            animator.SetFloat(WalkSpeedId, Mathf.Min(playback, maxWalkPlaybackSpeed));
        }

        /// <summary>射程内の正面側にいる敵の数に応じて、使う攻撃を選ぶ。</summary>
        public AttackMotion ChooseAttack(int enemiesInFront)
        {
            foreach (AttackMotion attack in attacks)
            {
                if (enemiesInFront < attack.minTargets) continue;
                if (attack.useChance >= 1f || UnityEngine.Random.value < attack.useChance) return attack;
            }
            return attacks[attacks.Length - 1];
        }

        /// <summary>攻撃間隔に収まるよう速めた再生速度 (1 〜 maxAttackPlaybackSpeed)。</summary>
        public float AttackPlaybackSpeed(AttackMotion attack, float interval)
        {
            return Mathf.Clamp(attack.duration / interval, 1f, maxAttackPlaybackSpeed);
        }

        public void PlayAttack(AttackMotion attack, float playbackSpeed)
        {
            animator.SetFloat(AttackSpeedId, playbackSpeed);
            animator.SetTrigger(attack.trigger);
        }

        public void PlayHurt()
        {
            ResetAttackTriggers();
            animator.SetTrigger(HurtId);
        }

        public void PlayBlock()
        {
            ResetAttackTriggers();
            animator.SetTrigger(BlockId);
        }

        public void PlayDeath()
        {
            ResetAttackTriggers();
            if (hasHurt) animator.ResetTrigger(HurtId);
            if (hasBlock) animator.ResetTrigger(BlockId);
            animator.SetTrigger(DieId);
        }

        void ResetAttackTriggers()
        {
            foreach (AttackMotion attack in attacks) animator.ResetTrigger(attack.trigger);
        }
    }
}
