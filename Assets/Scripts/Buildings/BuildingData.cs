using UnityEngine;

namespace RogueRTS
{
    public enum BuildingType
    {
        Wall,
        Gate,
        Turret,
    }

    /// <summary>コインで設置できる建築物1種類分のデータ。</summary>
    [CreateAssetMenu(menuName = "RogueRTS/Building Data")]
    public class BuildingData : ScriptableObject
    {
        public string displayName = "Building";
        public BuildingType type;
        [Tooltip("設置に必要なコイン")]
        public int cost = 10;
        public GameObject prefab;
        [Tooltip("建築ボタンに出すアイコン")]
        public Sprite icon;

        [Header("タレットのみ")]
        public float attackDamage = 12f;
        public float attackRange = 9f;
        [Tooltip("攻撃1回ごとの間隔 (秒)")]
        public float attackInterval = 0.8f;
    }
}
