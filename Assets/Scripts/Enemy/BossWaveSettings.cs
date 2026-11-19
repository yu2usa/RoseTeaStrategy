using UnityEngine;

namespace RogueRTS
{
    /// <summary>ボスウェーブの設定。WaveSpawner のインスペクターから変更できる。</summary>
    [System.Serializable]
    public class BossWaveSettings
    {
        [Tooltip("何ウェーブごとにボスウェーブにするか (ステージをまたいだ通算で数える)。0 でボスを出さない")]
        public int waveInterval = 5;

        [Tooltip("ボスが出てから次のウェーブまでの時間を、通常のウェーブの間隔の何倍にするか")]
        public float nextWaveDelayMultiplier = 3f;

        [Tooltip("画面上部の体力バーの上に表示する名前")]
        public string bossName = "エリートナイト";

        [Tooltip("ボスのプレハブ。能力はこのプレハブの UnitData (通常の敵) に下の倍率を掛けたものになる")]
        public Unit bossPrefab;

        [Header("通常の敵に対する倍率")]
        public float hpMultiplier = 5f;
        public float speedMultiplier = 0.6f;
        [Tooltip("1回の攻撃のダメージ倍率。振り下ろしのモーションが長く攻撃の間隔が空くぶんを補う")]
        public float attackDamageMultiplier = 2f;
        [Tooltip("撃破したときに得るコインの倍率")]
        public float bountyMultiplier = 5f;
    }
}
