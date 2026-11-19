using UnityEngine;

namespace RogueRTS
{
    /// <summary>攻撃した瞬間だけ表示される、攻撃者から対象への線。</summary>
    [RequireComponent(typeof(LineRenderer))]
    public class ShotLine : MonoBehaviour
    {
        public float duration = 0.08f;

        LineRenderer line;
        float timer;

        void Awake()
        {
            line = GetComponent<LineRenderer>();
            line.enabled = false;
        }

        public void Show(Vector3 from, Vector3 to)
        {
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.enabled = true;
            timer = duration;
        }

        void Update()
        {
            if (!line.enabled) return;

            timer -= Time.deltaTime;
            if (timer <= 0f) line.enabled = false;
        }
    }
}
