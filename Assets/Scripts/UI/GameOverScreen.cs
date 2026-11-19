using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueRTS
{
    /// <summary>拠点が破壊されたときの敗北画面。</summary>
    public class GameOverScreen : MonoBehaviour
    {
        public GameObject panel;
        public TMP_Text resultText;
        public Button retryButton;

        void Awake()
        {
            retryButton.onClick.AddListener(() => GameManager.Instance.Restart());
            panel.SetActive(false);
        }

        public void Show(int reachedStage)
        {
            resultText.text = $"到達ステージ: {reachedStage}";
            panel.SetActive(true);
        }
    }
}
