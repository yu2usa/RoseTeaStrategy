using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace RogueRTS
{
    /// <summary>
    /// ゲーム全体の進行役。ステージ開始 → 全ウェーブ撃破で報酬選択 → 次のステージ、を繰り返す。
    /// 拠点が破壊されたらゲームオーバー。プレイ中はスペースキーで一時停止できる。
    /// 時間の流れは速度ボタンで等速・2倍・3倍に切り替えられ、始めは等速。スローモーションはそれより優先する。
    /// F1 で UI (HUD・ヘルスバー・選択リングなど) の表示を切り替えられる。
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public enum State { Playing, Paused, ChoosingReward, GameOver }

        public static GameManager Instance { get; private set; }

        public ResourceBank bank;
        public PlayerBase playerBase;
        public WaveSpawner waveSpawner;
        public BuildController buildController;
        public RewardScreen rewardScreen;
        public GameOverScreen gameOverScreen;

        [Header("開始時に配置されるユニット")]
        public Unit[] startingUnits;

        [Header("報酬")]
        public RewardData[] rewardPool;
        public int rewardChoices = 3;

        [Header("ステージが1進むごとの敵の強化率")]
        public float enemyHpGrowth = 0.2f;
        public float enemyDamageGrowth = 0.1f;

        [Header("ゲーム速度")]
        [Tooltip("等速のときの Time.timeScale。速度ボタンの倍率はこれに掛かる")]
        public float normalTimeScale = 0.5f;
        [Tooltip("スローモーション中の速さ。等速に対する倍率")]
        public float slowMotionFactor = 0.25f;

        /// <summary>速度ボタンで選べる倍率。1 が等速。</summary>
        public static readonly int[] SpeedMultipliers = { 1, 2, 3 };

        public State CurrentState { get; private set; }
        public int Stage { get; private set; }
        public int SpeedMultiplier { get; private set; } = 1;
        public bool SlowMotion { get; private set; }
        /// <summary>F1 で切り替える、UI を表示する設定か。</summary>
        public bool UiVisible { get; private set; } = true;

        readonly RunModifiers playerMods = new RunModifiers();
        readonly RunModifiers enemyMods = new RunModifiers();

        public RunModifiers ModsFor(Team team) => team == Team.Player ? playerMods : enemyMods;

        float PlayingTimeScale => normalTimeScale * (SlowMotion ? slowMotionFactor : SpeedMultiplier);

        /// <summary>
        /// このフレームに進んだ「時計の秒数」。ゲーム内の動き (Time.deltaTime) は等速でも normalTimeScale 倍で流れるが、
        /// こちらは等速のときに現実の 1 秒で 1 進む。速度ボタン・スロー・一時停止には同じように従う。
        /// ウェーブまでのカウントダウンのように、秒数を画面に出す時間はこれで進める。
        /// </summary>
        public float ClockDeltaTime => normalTimeScale > 0f ? Time.deltaTime / normalTimeScale : Time.deltaTime;

        void Awake()
        {
            Instance = this;
            Time.timeScale = PlayingTimeScale;
        }

        void Start()
        {
            waveSpawner.StageCleared += OnStageCleared;
            playerBase.Health.Died += OnBaseDestroyed;
            Health.AnyDied += OnAnyDied;

            foreach (Unit prefab in startingUnits)
                playerBase.SpawnUnit(prefab);

            StartStage(1);
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.spaceKey.wasPressedThisFrame) TogglePause();
            if (keyboard.f1Key.wasPressedThisFrame) ToggleUi();
        }

        void ToggleUi()
        {
            UiVisible = !UiVisible;
            foreach (Unit unit in Unit.All) unit.RefreshSelectionRing();
        }

        /// <summary>プレイ中なら一時停止し、一時停止中なら再開する。報酬選択中や敗北後は何もしない。</summary>
        public void TogglePause()
        {
            if (CurrentState == State.Playing)
            {
                Time.timeScale = 0f;
                CurrentState = State.Paused;
            }
            else if (CurrentState == State.Paused)
            {
                Time.timeScale = PlayingTimeScale;
                CurrentState = State.Playing;
            }
        }

        /// <summary>時間の流れを等速の multiplier 倍にする。止まっている間は、再開したときからこの速さになる。</summary>
        public void SetSpeed(int multiplier)
        {
            SpeedMultiplier = multiplier;
            if (CurrentState == State.Playing) Time.timeScale = PlayingTimeScale;
        }

        /// <summary>スローモーションにする / 解く。止まっている間は、再開したときから反映される。</summary>
        public void SetSlowMotion(bool on)
        {
            SlowMotion = on;
            if (CurrentState == State.Playing) Time.timeScale = PlayingTimeScale;
        }

        void OnDestroy()
        {
            Health.AnyDied -= OnAnyDied;
            if (Instance == this) Instance = null;
        }

        void StartStage(int stage)
        {
            Stage = stage;
            enemyMods.hpMul = 1f + enemyHpGrowth * (stage - 1);
            enemyMods.damageMul = 1f + enemyDamageGrowth * (stage - 1);

            CurrentState = State.Playing;
            Time.timeScale = PlayingTimeScale;
            waveSpawner.BeginStage(stage);
        }

        void OnAnyDied(Health dead)
        {
            if (dead.team != Team.Enemy) return;
            if (dead.TryGetComponent(out Unit unit)) bank.AddCoins(unit.Bounty);
        }

        void OnStageCleared()
        {
            if (CurrentState != State.Playing) return;

            CurrentState = State.ChoosingReward;
            Time.timeScale = 0f;
            rewardScreen.Show(Stage, PickRewards(rewardChoices), OnRewardChosen);
        }

        void OnRewardChosen(RewardData reward)
        {
            ApplyReward(reward);
            StartStage(Stage + 1);
        }

        void OnBaseDestroyed(Health _)
        {
            CurrentState = State.GameOver;
            Time.timeScale = 0f;
            gameOverScreen.Show(Stage);
        }

        public void Restart()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        List<RewardData> PickRewards(int count)
        {
            var pool = new List<RewardData>(rewardPool);
            var picked = new List<RewardData>();
            while (picked.Count < count && pool.Count > 0)
            {
                int index = Random.Range(0, pool.Count);
                picked.Add(pool[index]);
                pool.RemoveAt(index);
            }
            return picked;
        }

        void ApplyReward(RewardData reward)
        {
            switch (reward.type)
            {
                case RewardType.AttackUp: playerMods.damageMul += reward.value; break;
                case RewardType.HealthUp: playerMods.hpMul += reward.value; break;
                case RewardType.RangeUp: playerMods.rangeMul += reward.value; break;
                case RewardType.AttackSpeedUp: playerMods.attackSpeedMul += reward.value; break;
                case RewardType.MoveSpeedUp: playerMods.moveSpeedMul += reward.value; break;
                case RewardType.BuildSpeedUp: playerMods.buildSpeedMul += reward.value; break;
                case RewardType.IncomeUp: bank.incomePerSecond += reward.value; break;
                case RewardType.GoldNow: bank.AddGold(Mathf.RoundToInt(reward.value)); break;
                case RewardType.Reinforcements:
                    for (int i = 0; i < Mathf.RoundToInt(reward.value); i++)
                        playerBase.SpawnUnit(playerBase.producibleUnits[0]);
                    break;
                case RewardType.RepairBase:
                    playerBase.Health.SetMaxHp(playerBase.Health.maxHp * (1f + reward.value));
                    playerBase.Health.HealFull();
                    break;
            }

            // 補正の変化を、すでに場にいる自軍ユニットへ反映する
            foreach (Unit unit in Unit.All)
            {
                if (unit.Team == Team.Player) unit.RefreshStats();
            }
        }
    }
}
