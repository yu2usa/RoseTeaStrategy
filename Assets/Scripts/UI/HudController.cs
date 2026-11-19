using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RogueRTS
{
    /// <summary>
    /// 資源・ステージ情報・拠点HP・生産キューの表示と、ユニット生産ボタン (数字キーでも可)・建築ボタン・速度ボタン・一時停止ボタン。
    /// 選んでいる速度ボタンと、一時停止中の一時停止ボタンは黄色にする。
    /// </summary>
    public class HudController : MonoBehaviour
    {
        public TMP_Text goldText;
        public TMP_Text coinText;
        public TMP_Text stageText;
        public TMP_Text waveText;
        public TMP_Text baseHpText;
        public TMP_Text queueText;
        public TMP_Text bannerText;

        [Header("生産ボタン (拠点の producibleUnits と同じ並び)")]
        public Button[] produceButtons;
        public TMP_Text[] produceLabels;

        [Header("建築ボタン (BuildController の options と同じ並び)")]
        public Button[] buildButtons;
        public TMP_Text[] buildLabels;
        public TMP_Text buildHintText;
        public Color buildSelectedColor = new Color(1f, 0.9f, 0.35f);

        [Header("速度ボタン (GameManager.SpeedMultipliers と同じ並び)")]
        [Tooltip("絵のボタン (Image に Sprite があるもの) なら文字は出さず、選ばれたら絵を黄色にする")]
        public Button[] speedButtons;
        public TMP_Text[] speedLabels;
        [Tooltip("押すたびに一時停止と再開を切り替える。中にあるボタンも同じ働きをする")]
        public Button pauseButton;
        public Color speedSelectedColor = new Color(1f, 0.9f, 0.35f);

        public float bannerDuration = 2.5f;

        [Tooltip("一時停止中だけ表示するパネル")]
        public GameObject pausePanel;

        GameManager game;
        Graphic[][] speedHighlights;
        Graphic[] pauseHighlights;
        Canvas canvas;
        GraphicRaycaster raycaster;
        float bannerTimer;
        int shownStage;
        int shownWave;

        void Start()
        {
            game = GameManager.Instance;
            canvas = GetComponent<Canvas>();
            raycaster = GetComponent<GraphicRaycaster>();

            Unit[] units = game.playerBase.producibleUnits;
            for (int i = 0; i < produceButtons.Length; i++)
            {
                bool used = i < units.Length;
                produceButtons[i].gameObject.SetActive(used);
                if (!used) continue;

                int index = i;
                UnitData data = units[i].data;
                if (produceButtons[i].TryGetComponent(out IconButtonView view))
                    view.Show(data.icon, data.displayName, $"{data.cost}<size=60%> G</size>", (i + 1).ToString());
                else
                    produceLabels[i].text = $"{data.displayName} [{i + 1}]\n{data.cost} G";
                produceButtons[i].onClick.AddListener(() => game.playerBase.TryQueue(index));
            }

            BuildController build = game.buildController;
            for (int i = 0; i < buildButtons.Length; i++)
            {
                bool used = build != null && i < build.options.Length;
                buildButtons[i].gameObject.SetActive(used);
                if (!used) continue;

                int index = i;
                BuildingData option = build.options[i];
                string hotkey = BuildController.HotkeyLabel(i);
                if (buildButtons[i].TryGetComponent(out IconButtonView view))
                    view.Show(option.icon, option.displayName, $"{option.cost}<size=50%> コイン</size>", hotkey);
                else
                    buildLabels[i].text = $"{option.displayName} [{hotkey}]\n{option.cost} コイン";
                buildButtons[i].onClick.AddListener(() => build.ToggleOption(index));
            }

            int[] speeds = GameManager.SpeedMultipliers;
            speedHighlights = new Graphic[speedButtons.Length][];
            for (int i = 0; i < speedButtons.Length; i++)
            {
                bool used = i < speeds.Length;
                speedButtons[i].gameObject.SetActive(used);
                if (!used) continue;

                int multiplier = speeds[i];
                // 絵で速さを表すボタンには文字を重ねない
                if (speedButtons[i].image.sprite == null) speedLabels[i].text = multiplier == 1 ? "等速" : $"{multiplier}倍";
                speedButtons[i].onClick.AddListener(() => game.SetSpeed(multiplier));
                speedHighlights[i] = Highlights(speedButtons[i]);
            }

            if (pauseButton != null)
            {
                // 縦棒ごとにボタンになっていても、どこを押しても同じように働かせる
                foreach (Button part in pauseButton.GetComponentsInChildren<Button>(true))
                    part.onClick.AddListener(game.TogglePause);
                pauseHighlights = Highlights(pauseButton);
            }

            bannerText.gameObject.SetActive(false);
        }

        void Update()
        {
            ResourceBank bank = game.bank;
            WaveSpawner spawner = game.waveSpawner;
            PlayerBase playerBase = game.playerBase;
            int enemies = Health.CountAlive(Team.Enemy);

            goldText.text = $"ゴールド: {bank.Gold}  (+{bank.incomePerSecond:0.#}/秒)";
            coinText.text = $"コイン: {bank.Coins}";
            stageText.text = $"ステージ {game.Stage}   ウェーブ {spawner.CurrentWave}/{spawner.wavesPerStage}";
            string nextWave = spawner.NextWaveIsBoss ? "ボスウェーブ" : "次のウェーブ";
            waveText.text = spawner.AllWavesSpawned
                ? $"残りの敵: {enemies}"
                : $"{nextWave}まで {Mathf.CeilToInt(spawner.TimeToNextWave)} 秒   敵: {enemies}";
            baseHpText.text =
                $"拠点HP: {Mathf.CeilToInt(playerBase.Health.CurrentHp)}/{Mathf.CeilToInt(playerBase.Health.maxHp)}";

            Unit current = playerBase.CurrentItem;
            queueText.text = current == null
                ? "生産キュー: 空"
                : $"生産中: {current.data.displayName} {playerBase.Progress01 * 100f:0}%   待ち: {playerBase.QueueCount - 1}";

            UpdateProduceButtons(playerBase);
            UpdateBuildButtons(game.buildController);
            UpdateSpeedButtons();
            UpdateBanner(spawner);
            if (pausePanel != null) pausePanel.SetActive(game.CurrentState == GameManager.State.Paused);
            UpdateVisibility();
        }

        /// <summary>F1 で UI を隠す。報酬画面と敗北画面は操作しないと先へ進めないので、隠していても出す。</summary>
        void UpdateVisibility()
        {
            bool mustShow = game.CurrentState == GameManager.State.ChoosingReward
                || game.CurrentState == GameManager.State.GameOver;
            bool visible = game.UiVisible || mustShow;
            if (canvas.enabled == visible) return;

            canvas.enabled = visible;
            raycaster.enabled = visible;
        }

        void UpdateBuildButtons(BuildController build)
        {
            if (build == null) return;

            bool playing = game.CurrentState == GameManager.State.Playing;
            int count = Mathf.Min(buildButtons.Length, build.options.Length);
            for (int i = 0; i < count; i++)
            {
                buildButtons[i].interactable = playing;
                buildLabels[i].color = build.SelectedIndex == i ? buildSelectedColor : Color.white;
            }

            BuildingData selected = build.Selected;
            if (selected == null)
            {
                buildHintText.text = "";
                return;
            }

            // 置けない場所にカーソルがあるときは、その理由を出す
            string problem = BuildController.Describe(build.HoverResult);
            buildHintText.text = problem.Length > 0
                ? problem
                : $"{selected.displayName}: 左クリックで設置 / 右クリックで終了";
        }

        /// <summary>選ばれたときに黄色にする部分。文字のあるボタンは文字、絵だけのボタンは絵 (中の Image すべて)。</summary>
        static Graphic[] Highlights(Button button)
        {
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null && label.text.Length > 0) return new Graphic[] { label };
            return button.GetComponentsInChildren<Image>(true);
        }

        void UpdateSpeedButtons()
        {
            // 一時停止中も、再開したときの速さのボタンを選ばれたままにする
            int count = Mathf.Min(speedButtons.Length, GameManager.SpeedMultipliers.Length);
            for (int i = 0; i < count; i++)
                Highlight(speedHighlights[i], GameManager.SpeedMultipliers[i] == game.SpeedMultiplier);

            if (pauseHighlights != null) Highlight(pauseHighlights, game.CurrentState == GameManager.State.Paused);
        }

        void Highlight(Graphic[] parts, bool selected)
        {
            foreach (Graphic part in parts) part.color = selected ? speedSelectedColor : Color.white;
        }

        void UpdateProduceButtons(PlayerBase playerBase)
        {
            bool playing = game.CurrentState == GameManager.State.Playing;
            Keyboard keyboard = Keyboard.current;

            int count = Mathf.Min(produceButtons.Length, playerBase.producibleUnits.Length);
            for (int i = 0; i < count; i++)
            {
                produceButtons[i].interactable = playing && playerBase.CanQueue(i);

                // 数字キー 1, 2, ... でも生産できる
                if (playing && keyboard != null && keyboard[Key.Digit1 + i].wasPressedThisFrame)
                    playerBase.TryQueue(i);
            }
        }

        void UpdateBanner(WaveSpawner spawner)
        {
            if (game.Stage != shownStage)
            {
                shownStage = game.Stage;
                shownWave = 0;
                ShowBanner($"ステージ {shownStage}");
            }
            else if (spawner.CurrentWave != shownWave)
            {
                shownWave = spawner.CurrentWave;
                if (shownWave > 0) ShowBanner(spawner.LastWaveWasBoss ? "ボス襲来！" : $"ウェーブ {shownWave} 襲来！");
            }

            if (bannerTimer <= 0f) return;

            bannerTimer -= Time.deltaTime;
            Color color = bannerText.color;
            color.a = Mathf.Clamp01(bannerTimer / 0.5f);
            bannerText.color = color;
            if (bannerTimer <= 0f) bannerText.gameObject.SetActive(false);
        }

        void ShowBanner(string message)
        {
            bannerText.text = message;
            bannerText.gameObject.SetActive(true);
            bannerTimer = bannerDuration;
        }
    }
}
