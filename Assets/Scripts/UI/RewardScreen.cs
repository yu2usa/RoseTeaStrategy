using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RogueRTS
{
    /// <summary>ステージクリア時の報酬選択画面。提示された候補から1つ選ばせる。</summary>
    public class RewardScreen : MonoBehaviour
    {
        [Tooltip("表示/非表示を切り替える画面全体のパネル")]
        public GameObject panel;
        public TMP_Text headerText;
        public Button[] choiceButtons;
        public TMP_Text[] titleTexts;
        public TMP_Text[] descriptionTexts;

        IReadOnlyList<RewardData> options;
        Action<RewardData> onChosen;

        void Awake()
        {
            for (int i = 0; i < choiceButtons.Length; i++)
            {
                int index = i;
                choiceButtons[i].onClick.AddListener(() => Choose(index));
            }
            panel.SetActive(false);
        }

        public void Show(int clearedStage, IReadOnlyList<RewardData> rewardOptions, Action<RewardData> chosenCallback)
        {
            options = rewardOptions;
            onChosen = chosenCallback;
            headerText.text = $"ステージ {clearedStage} クリア！";

            for (int i = 0; i < choiceButtons.Length; i++)
            {
                bool used = i < options.Count;
                choiceButtons[i].gameObject.SetActive(used);
                if (!used) continue;

                titleTexts[i].text = options[i].title;
                descriptionTexts[i].text = options[i].description;
            }

            panel.SetActive(true);
        }

        void Choose(int index)
        {
            panel.SetActive(false);
            onChosen?.Invoke(options[index]);
        }
    }
}
