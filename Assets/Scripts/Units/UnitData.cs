using UnityEngine;

namespace RogueRTS
{
    [CreateAssetMenu(menuName = "RogueRTS/Unit Data")]
    public class UnitData : ScriptableObject
    {
        public string displayName = "Unit";

        [Header("戦闘")]
        public float maxHp = 100f;
        public float attackDamage = 10f;
        public float attackRange = 1.5f;
        [Tooltip("攻撃1回ごとの間隔 (秒)")]
        public float attackInterval = 1f;
        [Tooltip("この距離以内の敵に自動で向かっていく")]
        public float aggroRange = 18f;
        [Tooltip("敵の攻撃を受けたとき、ダメージを受けずに防ぐ確率 (0〜1)")]
        [Range(0f, 1f)] public float blockChance;

        [Header("回復")]
        [Tooltip("0 より大きければ攻撃をしない回復役になる。攻撃の代わりに attackInterval ごとに、attackRange 以内の味方 1 体 (自分以外) の HP をこの量だけ回復する")]
        public float healAmount;

        [Header("移動")]
        public float moveSpeed = 3.5f;

        [Header("生産 (自軍ユニット)")]
        public int cost = 50;
        public float buildTime = 4f;
        [Tooltip("生産ボタンに出すアイコン")]
        public Sprite icon;

        [Header("撃破時にプレイヤーが得るコイン (敵ユニット)")]
        public int bounty = 10;
    }
}
