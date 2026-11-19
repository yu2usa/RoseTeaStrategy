using UnityEngine;

namespace RogueRTS
{
    /// <summary>頭上に表示する、常にカメラを向くHPバー。F1 で UI を隠している間は描かない。</summary>
    public class HealthBar : MonoBehaviour
    {
        public Health health;
        public Transform fill;

        Camera cam;
        Renderer[] renderers;
        bool drawn = true;

        void Awake() => renderers = GetComponentsInChildren<Renderer>();

        void LateUpdate()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null || health == null) return;
            if (health.IsDead)
            {
                gameObject.SetActive(false);
                return;
            }

            SetDrawn(GameManager.Instance == null || GameManager.Instance.UiVisible);
            if (!drawn) return;

            transform.rotation = cam.transform.rotation;

            float ratio = health.maxHp > 0f ? Mathf.Clamp01(health.CurrentHp / health.maxHp) : 0f;
            fill.localScale = new Vector3(ratio, 1f, 1f);
            // 左端を揃えたまま縮め、背景より少し手前に出す
            fill.localPosition = new Vector3(-(1f - ratio) * 0.5f, 0f, -0.01f);
        }

        void SetDrawn(bool value)
        {
            if (drawn == value) return;

            drawn = value;
            foreach (Renderer part in renderers) part.enabled = value;
        }
    }
}
