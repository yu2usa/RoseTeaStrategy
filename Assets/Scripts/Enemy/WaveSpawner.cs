using System;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>
    /// 敵AI。一定間隔で敵の波を出現させ、拠点へ攻撃移動させるだけ。
    /// 一定ウェーブごとに、ボスが1体だけ出るボスウェーブになる。
    /// ステージ内の全ウェーブを出し終え、敵が全滅したら StageCleared を発火する。
    /// </summary>
    public class WaveSpawner : MonoBehaviour
    {
        public Unit meleePrefab;
        public Unit rangedPrefab;
        public Transform[] spawnPoints;
        [Tooltip("敵が目指す場所 (自軍拠点)")]
        public Transform attackTarget;
        [Tooltip("あれば、防壁や門を避けて拠点へ向かう経路をここから求める")]
        public BuildController buildController;

        [Header("ウェーブ")]
        public int wavesPerStage = 3;
        [Tooltip("ステージ開始から最初のウェーブまでの準備時間 (秒)。ここからの秒数は、等速のとき現実の秒数と同じ速さで進む")]
        public float prepTime = 15f;
        [Tooltip("ウェーブが出てから次のウェーブまでの時間 (秒)。ボスウェーブのあとは、ボス設定の倍率を掛けた時間になる")]
        public float timeBetweenWaves = 50f;
        [Tooltip("敵を全滅させた場合、次のウェーブまでの待ち時間をここまで短縮する (秒)")]
        public float clearedWaitTime = 5f;

        [Header("敵の数 = 基本 + ステージ増加 + ウェーブ増加")]
        public int baseEnemyCount = 4;
        public int enemiesPerStage = 2;
        public int enemiesPerWave = 1;

        [Header("ボスウェーブ")]
        public BossWaveSettings boss = new BossWaveSettings();

        public event Action StageCleared;
        /// <summary>ボスが出現したとき、そのボスと名前を渡して呼ばれる。</summary>
        public event Action<Unit, string> BossSpawned;

        /// <summary>このステージで出現済みのウェーブ数。</summary>
        public int CurrentWave { get; private set; }
        /// <summary>ステージをまたいだ通算の出現済みウェーブ数。</summary>
        public int TotalWaves { get; private set; }
        public float TimeToNextWave { get; private set; }
        public bool AllWavesSpawned => CurrentWave >= wavesPerStage;
        public bool LastWaveWasBoss { get; private set; }
        public bool NextWaveIsBoss => IsBossWave(TotalWaves + 1);

        int stage;
        bool running;

        void Start()
        {
            if (buildController != null) buildController.NavMeshUpdated += RedirectEnemies;
        }

        public void BeginStage(int stageNumber)
        {
            stage = stageNumber;
            CurrentWave = 0;
            TimeToNextWave = prepTime;
            running = true;
        }

        void Update()
        {
            if (!running) return;

            bool fieldCleared = Health.CountAlive(Team.Enemy) == 0;

            if (AllWavesSpawned)
            {
                if (!fieldCleared) return;
                running = false;
                StageCleared?.Invoke();
                return;
            }

            // 画面に秒数で出すので、等速で現実の 1 秒ごとに 1 減らす (ゲーム内の動きの速さとは別)
            TimeToNextWave -= GameManager.Instance.ClockDeltaTime;
            if (CurrentWave > 0 && fieldCleared)
                TimeToNextWave = Mathf.Min(TimeToNextWave, clearedWaitTime);

            if (TimeToNextWave > 0f) return;

            SpawnWave();
            // ボスと戦う間は、次のウェーブまで長めに猶予を与える
            TimeToNextWave = LastWaveWasBoss ? timeBetweenWaves * boss.nextWaveDelayMultiplier : timeBetweenWaves;
        }

        bool IsBossWave(int totalWaveNumber)
        {
            return boss.bossPrefab != null && boss.waveInterval > 0 && totalWaveNumber % boss.waveInterval == 0;
        }

        void SpawnWave()
        {
            TotalWaves++;
            LastWaveWasBoss = IsBossWave(TotalWaves);
            if (LastWaveWasBoss) SpawnBoss();
            else SpawnEnemies();
            CurrentWave++;
        }

        void SpawnEnemies()
        {
            int count = baseEnemyCount + (stage - 1) * enemiesPerStage + CurrentWave * enemiesPerWave;
            // ステージが進むほど複数方向から同時に攻めてくる
            int lanes = Mathf.Clamp(1 + (stage - 1) / 2, 1, spawnPoints.Length);
            int firstLane = UnityEngine.Random.Range(0, spawnPoints.Length);
            bool allowRanged = rangedPrefab != null && (stage > 1 || CurrentWave > 0);

            for (int i = 0; i < count; i++)
            {
                Transform point = spawnPoints[(firstLane + i % lanes) % spawnPoints.Length];
                Unit prefab = allowRanged && i % 3 == 2 ? rangedPrefab : meleePrefab;
                Vector3 position = NavMeshUtil.Snap(point.position + NavMeshUtil.Scatter(3f), 5f);

                Unit unit = Instantiate(prefab, position, point.rotation);
                unit.AttackMoveTo(GoalFrom(position));
            }
        }

        /// <summary>体力が多く足の遅いボスを1体だけ出す。能力は通常の敵の値に倍率を掛けたもの。</summary>
        void SpawnBoss()
        {
            Transform point = spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)];
            Vector3 position = NavMeshUtil.Snap(point.position, 5f);

            Unit unit = Instantiate(boss.bossPrefab, position, point.rotation);
            unit.SetIndividualMultipliers(
                boss.hpMultiplier, boss.speedMultiplier, boss.attackDamageMultiplier, boss.bountyMultiplier);
            unit.AttackMoveTo(GoalFrom(position));

            BossSpawned?.Invoke(unit, boss.bossName);
        }

        /// <summary>from から歩いて行ける、拠点のすぐ隣の地点。</summary>
        Vector3 GoalFrom(Vector3 from)
        {
            if (buildController != null && buildController.Grid.TryFindEnemyGoal(from, out Vector3 goal))
                return NavMeshUtil.Snap(goal, 2f);
            return NavMeshUtil.Snap(attackTarget.position, 10f);
        }

        /// <summary>建築で道が変わったら、今いる敵の目的地を選び直す。</summary>
        void RedirectEnemies()
        {
            foreach (Unit unit in Unit.All)
            {
                if (unit.Team == Team.Enemy) unit.AttackMoveTo(GoalFrom(unit.transform.position));
            }
        }

        void OnDrawGizmos()
        {
            if (spawnPoints == null) return;

            Gizmos.color = Color.red;
            foreach (Transform point in spawnPoints)
            {
                if (point != null) Gizmos.DrawWireSphere(point.position, 3f);
            }
        }
    }
}
