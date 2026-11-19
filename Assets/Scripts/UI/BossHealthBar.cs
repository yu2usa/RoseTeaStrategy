using TMPro;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>画面上部に大きく出すボスの体力バー。ボスの名前はバーの上に表示する。</summary>
    public class BossHealthBar : MonoBehaviour
    {
        [Tooltip("ボスがいる間だけ表示する部分")]
        public GameObject panel;
        public TMP_Text nameText;
        public TMP_Text hpText;
        [Tooltip("体力の割合に合わせてアンカーで横幅を縮める部分")]
        public RectTransform fill;

        Health boss;

        void Awake() => panel.SetActive(false);

        void Start() => GameManager.Instance.waveSpawner.BossSpawned += Show;

        void Show(Unit unit, string bossName)
        {
            boss = unit.Health;
            nameText.text = bossName;
            panel.SetActive(true);
            Refresh();
        }

        void Update()
        {
            if (ReferenceEquals(boss, null)) return;

            if (boss == null || boss.IsDead)
            {
                boss = null;
                panel.SetActive(false);
                return;
            }
            Refresh();
        }

        void Refresh()
        {
            float ratio = boss.maxHp > 0f ? Mathf.Clamp01(boss.CurrentHp / boss.maxHp) : 0f;
            fill.anchorMax = new Vector2(ratio, 1f);
            hpText.text = $"{Mathf.CeilToInt(boss.CurrentHp)} / {Mathf.CeilToInt(boss.maxHp)}";
        }
    }
}
