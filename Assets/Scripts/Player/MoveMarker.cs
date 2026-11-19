using UnityEngine;

namespace RogueRTS
{
    /// <summary>移動命令を出した地点に一瞬だけ表示され、縮みながら消えるマーカー。F1 で UI を隠している間は描かない。</summary>
    public class MoveMarker : MonoBehaviour
    {
        public float size = 1.6f;
        public float duration = 0.4f;

        float timer;
        Renderer body;

        void Awake() => body = GetComponent<Renderer>();

        public void Show(Vector3 position)
        {
            transform.position = position + Vector3.up * 0.05f;
            timer = duration;
            gameObject.SetActive(true);
            ApplyScale();
        }

        void Update()
        {
            timer -= Time.unscaledDeltaTime;
            if (timer <= 0f)
            {
                gameObject.SetActive(false);
                return;
            }
            ApplyScale();
        }

        void ApplyScale()
        {
            float s = size * Mathf.Clamp01(timer / duration);
            transform.localScale = new Vector3(s, 0.01f, s);
            body.enabled = GameManager.Instance.UiVisible;
        }
    }
}
