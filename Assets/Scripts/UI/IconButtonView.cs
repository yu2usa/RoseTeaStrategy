using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RogueRTS
{
    /// <summary>
    /// 生産ボタンや建築ボタンの見た目。アイコンの下に名前、左上にショートカットキーを出す。
    /// マウスを乗せている間はアイコンをコストの数字に替えて少し弾むように拡大し、外すと元のアイコンに戻る。
    /// 押せないとき (ゴールド不足など) はアイコンとコストを薄く表示する。
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class IconButtonView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Tooltip("マウスを乗せると拡大する部分 (アイコンとコスト)。ピボットは中央")]
        public RectTransform face;
        public Image icon;
        public TMP_Text costText;
        public TMP_Text nameText;
        public TMP_Text hotkeyText;

        [Tooltip("アイコンの 1 ピクセルを何倍の大きさで描くか")]
        public float iconPixelScale = 1.5f;
        [Tooltip("マウスを乗せたときの拡大率")]
        public float hoverScale = 1.2f;
        [Tooltip("拡大・縮小にかかる実時間 (秒)。ゲームの速さや一時停止には左右されない")]
        public float scaleDuration = 0.18f;
        [Tooltip("押せないときのアイコンとコストの不透明度")]
        [Range(0f, 1f)] public float disabledAlpha = 0.45f;

        /// <summary>マウスが乗っているか。</summary>
        public bool Hovered { get; private set; }

        Button button;
        CanvasGroup faceGroup;
        bool hasIcon;
        float fromScale = 1f;
        float toScale = 1f;
        float scaleTimer;

        void Awake()
        {
            button = GetComponent<Button>();
            faceGroup = face.GetComponent<CanvasGroup>();
            scaleTimer = scaleDuration;
            SetHovered(false);
        }

        void OnDisable()
        {
            SetHovered(false);
            face.localScale = Vector3.one;
            scaleTimer = scaleDuration;
        }

        /// <summary>アイコン・名前・コスト (リッチテキスト可) と、ショートカットキーを表示する。</summary>
        public void Show(Sprite iconSprite, string displayName, string cost, string hotkey)
        {
            nameText.text = displayName;
            hotkeyText.text = hotkey;
            costText.text = cost;

            hasIcon = iconSprite != null;
            icon.sprite = iconSprite;
            // ドット絵がぼやけないよう、元のピクセル数に一定の倍率を掛けた大きさで描く
            if (hasIcon) icon.rectTransform.sizeDelta = iconSprite.rect.size * iconPixelScale;
            SetHovered(Hovered);
        }

        public void OnPointerEnter(PointerEventData eventData) => SetHovered(true);
        public void OnPointerExit(PointerEventData eventData) => SetHovered(false);

        void SetHovered(bool hovered)
        {
            if (hovered != Hovered)
            {
                fromScale = face.localScale.x;
                toScale = hovered ? hoverScale : 1f;
                scaleTimer = 0f;
            }

            Hovered = hovered;
            icon.gameObject.SetActive(!hovered && hasIcon);
            // アイコンがなければ、乗せていなくてもコストを出しておく
            costText.gameObject.SetActive(hovered || !hasIcon);
        }

        void Update()
        {
            if (scaleTimer < scaleDuration)
            {
                scaleTimer += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(scaleTimer / scaleDuration);
                // 大きくなるときは少し行き過ぎてから落ち着き、戻るときはなめらかに縮む
                float eased = toScale > fromScale ? EaseOutBack(t) : EaseOutCubic(t);
                face.localScale = Vector3.one * Mathf.LerpUnclamped(fromScale, toScale, eased);
            }

            faceGroup.alpha = button.interactable ? 1f : disabledAlpha;
        }

        static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

        static float EaseOutBack(float t)
        {
            const float overshoot = 1.70158f;
            float u = t - 1f;
            return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
        }
    }
}
