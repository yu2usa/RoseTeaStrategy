using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RogueRTS
{
    /// <summary>
    /// F2 でオンにすると、ダメージが入るたびにその場所へ数字を出す。敵に与えたダメージは緑、自軍が受けたダメージは赤。
    /// HP が回復したときは、回復した量を青で出す。
    /// 数字は昇りながら薄れて消える。Canvas に描くので、F1 で UI を隠すと一緒に隠れる。
    /// </summary>
    public class DamageIndicator : MonoBehaviour
    {
        class Number
        {
            public TMP_Text text;
            public Vector3 position;
            public float age;
        }

        public Camera cam;
        [Tooltip("数字の雛形 (Canvas 直下、アンカーは左下)。複製して使い回す")]
        public TMP_Text template;
        public Color dealtColor = new Color(0.35f, 1f, 0.35f);
        public Color receivedColor = new Color(1f, 0.25f, 0.25f);
        [Tooltip("攻撃を防いだときに出す「ブロック」の色")]
        public Color blockedColor = new Color(0.55f, 0.85f, 1f);
        [Tooltip("回復した量の色。「ブロック」の水色と見分けられるよう、濃い青にする")]
        public Color healedColor = new Color(0.25f, 0.5f, 1f);
        [Tooltip("数字が消えるまでの実時間 (秒)。ゲームの速さには左右されない")]
        public float duration = 1f;
        [Tooltip("消えるまでに昇る高さ (Canvas の単位)")]
        public float rise = 80f;
        [Tooltip("重なりにくいよう、出す位置を水平にずらす最大距離 (m)")]
        public float scatter = 0.4f;

        /// <summary>数字を出す設定か。</summary>
        public bool Showing { get; private set; }

        readonly List<Number> numbers = new List<Number>();
        Canvas canvas;

        void Awake()
        {
            canvas = template.GetComponentInParent<Canvas>(true);
            template.gameObject.SetActive(false);
        }

        void OnEnable()
        {
            Health.AnyDamaged += OnAnyDamaged;
            Health.AnyBlocked += OnAnyBlocked;
            Health.AnyHealed += OnAnyHealed;
        }

        void OnDisable()
        {
            Health.AnyDamaged -= OnAnyDamaged;
            Health.AnyBlocked -= OnAnyBlocked;
            Health.AnyHealed -= OnAnyHealed;
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame) Toggle();

            foreach (Number number in numbers)
            {
                if (number.text.gameObject.activeSelf) UpdateNumber(number);
            }
        }

        void Toggle()
        {
            Showing = !Showing;
            if (Showing) return;

            foreach (Number number in numbers) number.text.gameObject.SetActive(false);
        }

        void OnAnyDamaged(Health victim, float amount)
        {
            Spawn(victim, Mathf.Max(1, Mathf.RoundToInt(amount)).ToString(),
                victim.team == Team.Enemy ? dealtColor : receivedColor);
        }

        void OnAnyBlocked(Health victim) => Spawn(victim, "ブロック", blockedColor);

        void OnAnyHealed(Health healed, float amount) =>
            Spawn(healed, "+" + Mathf.Max(1, Mathf.RoundToInt(amount)), healedColor);

        void Spawn(Health victim, string content, Color color)
        {
            if (!Showing) return;

            Number number = GetFreeNumber();
            Vector2 offset = Random.insideUnitCircle * scatter;
            number.position = victim.transform.position + new Vector3(offset.x, victim.height + 0.2f, offset.y);
            number.age = 0f;
            number.text.text = content;
            number.text.color = color;
            number.text.gameObject.SetActive(true);
            UpdateNumber(number);
        }

        Number GetFreeNumber()
        {
            foreach (Number number in numbers)
            {
                if (!number.text.gameObject.activeSelf) return number;
            }

            TMP_Text text = Instantiate(template, template.transform.parent);
            // HUD や報酬画面より奥に描く
            text.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            var created = new Number { text = text };
            numbers.Add(created);
            return created;
        }

        void UpdateNumber(Number number)
        {
            number.age += Time.unscaledDeltaTime;
            float progress = number.age / duration;
            if (progress >= 1f)
            {
                number.text.gameObject.SetActive(false);
                return;
            }

            // スクリーン座標 (px) を Canvas のスケールに合わせる
            Vector3 screen = cam.WorldToScreenPoint(number.position);
            number.text.enabled = screen.z > 0f;
            number.text.rectTransform.anchoredPosition = (Vector2)screen / canvas.scaleFactor + Vector2.up * (rise * progress);

            // 後半で薄れて消える
            Color color = number.text.color;
            color.a = Mathf.Clamp01((1f - progress) * 2f);
            number.text.color = color;
        }
    }
}
