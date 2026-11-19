using UnityEngine;

namespace RogueRTS
{
    public enum RewardType
    {
        AttackUp,
        HealthUp,
        RangeUp,
        AttackSpeedUp,
        MoveSpeedUp,
        BuildSpeedUp,
        IncomeUp,
        GoldNow,
        Reinforcements,
        RepairBase,
    }

    /// <summary>ステージクリア時に選べる報酬1つ分。効果の中身は GameManager.ApplyReward を参照。</summary>
    [CreateAssetMenu(menuName = "RogueRTS/Reward Data")]
    public class RewardData : ScriptableObject
    {
        public string title;
        [TextArea] public string description;
        public RewardType type;
        [Tooltip("倍率系は 0.2 = +20%。ゴールド・援軍は実数")]
        public float value;
    }
}
