using System.Collections.Generic;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>ステージに生える木の生え方。</summary>
    public enum ForestPattern
    {
        [InspectorName("ランダム")] Random,
        [InspectorName("まばら")] Sparse,
        [InspectorName("ほとんど生えない")] Few,
        [InspectorName("樹木エリア")] Grove,
        [InspectorName("なし")] None,
    }

    /// <summary>
    /// 地形として、ステージに木を生やす。ゲームを始めるたびに「まばら」「ほとんど生えない」
    /// 「一部に樹木エリア」のどれかの生え方を選ぶ。
    /// 木は建築マス 1 つを占め、誰も通れず建築もできない。矢は木を越えていく。
    /// 拠点や敵の出現地点のまわりと、敵が拠点へ辿り着けなくなる位置には生やさない。
    /// BuildController が NavMesh を作る前に Plant を呼ぶ。
    /// </summary>
    public class ForestGenerator : MonoBehaviour
    {
        /// <summary>次に読み込むシーンで、インスペクターの設定の代わりに使う生え方。テストで地形を固定するためのもの。</summary>
        public static ForestPattern? PatternOverride;

        public GameObject treePrefab;
        [Tooltip("ランダムにすると、下の重みで生え方を選ぶ")]
        public ForestPattern pattern = ForestPattern.Random;
        [Tooltip("0 なら毎回違う並びにする。それ以外なら、この値で生え方と並びを固定する")]
        public int seed;

        [Header("生え方を選ぶ重み (ランダムのとき)")]
        public float sparseWeight = 1f;
        public float fewWeight = 1f;
        public float groveWeight = 1f;

        [Header("木を生やさない場所")]
        public Transform playerBase;
        public float baseClearRadius = 14f;
        public float spawnClearRadius = 8f;

        [Header("まばら")]
        [Tooltip("本数 (x 〜 y)")]
        public Vector2Int sparseCount = new Vector2Int(28, 40);
        [Tooltip("木と木の中心の最小の間隔")]
        public float sparseSpacing = 5f;

        [Header("ほとんど生えない")]
        [Tooltip("本数 (x 〜 y)")]
        public Vector2Int fewCount = new Vector2Int(2, 5);
        public float fewSpacing = 10f;

        [Header("樹木エリア")]
        [Tooltip("エリアの数 (x 〜 y)")]
        public Vector2Int groveCount = new Vector2Int(1, 2);
        [Tooltip("エリアの半径 (x 〜 y)。縁は不規則に出入りする")]
        public Vector2 groveRadius = new Vector2(8f, 12f);
        [Tooltip("エリアの中で木が生えているマスの割合")]
        [Range(0f, 1f)] public float groveDensity = 0.8f;
        [Tooltip("エリアの外にまばらに生やす本数 (x 〜 y)")]
        public Vector2Int groveStrayCount = new Vector2Int(3, 8);

        [Header("見た目")]
        [Tooltip("木の大きさの倍率 (x 〜 y)")]
        public Vector2 scaleRange = new Vector2(0.9f, 1.25f);
        [Tooltip("見た目をマスの中心からずらす最大の距離。並びが格子に見えないようにする")]
        public float jitter = 0.3f;

        /// <summary>今のステージの生え方。Plant の前は None。</summary>
        public ForestPattern CurrentPattern { get; private set; } = ForestPattern.None;
        public IReadOnlyList<Transform> Trees => trees;

        readonly List<Transform> trees = new List<Transform>();
        readonly List<Vector3> mustReachBase = new List<Vector3>();
        System.Random rng;
        BuildGrid grid;
        Transform[] enemySpawns;
        Transform treeRoot;

        /// <summary>生え方を選んで木を生やし、生やしたマスを grid に障害物として書き込む。</summary>
        public void Plant(BuildGrid buildGrid, Transform[] enemySpawnPoints)
        {
            grid = buildGrid;
            enemySpawns = enemySpawnPoints;
            rng = seed != 0 ? new System.Random(seed) : new System.Random();

            CurrentPattern = PatternOverride ?? pattern;
            if (CurrentPattern == ForestPattern.Random) CurrentPattern = PickPattern();
            if (CurrentPattern == ForestPattern.None || treePrefab == null) return;

            mustReachBase.Clear();
            foreach (Transform point in enemySpawns) mustReachBase.Add(point.position);
            treeRoot = new GameObject("Trees").transform;

            switch (CurrentPattern)
            {
                case ForestPattern.Sparse:
                    Scatter(Range(sparseCount), sparseSpacing);
                    break;
                case ForestPattern.Few:
                    Scatter(Range(fewCount), fewSpacing);
                    break;
                case ForestPattern.Grove:
                    var centers = new List<Vector3>();
                    int groves = Range(groveCount);
                    for (int i = 0; i < groves; i++) PlantGrove(centers);
                    Scatter(Range(groveStrayCount), sparseSpacing);
                    break;
            }
        }

        ForestPattern PickPattern()
        {
            float total = sparseWeight + fewWeight + groveWeight;
            if (total <= 0f) return ForestPattern.None;

            float pick = (float)rng.NextDouble() * total;
            if (pick < sparseWeight) return ForestPattern.Sparse;
            return pick < sparseWeight + fewWeight ? ForestPattern.Few : ForestPattern.Grove;
        }

        /// <summary>空いているマスのどこかに、互いに spacing 以上離して count 本生やす。</summary>
        void Scatter(int count, float spacing)
        {
            int planted = 0;
            foreach (Vector2Int cell in Shuffled(CandidateCells()))
            {
                if (planted >= count) break;
                if (HasTreeWithin(grid.CellCenter(cell), spacing)) continue;
                if (TryPlant(cell)) planted++;
            }
        }

        /// <summary>
        /// 不規則な形の林を 1 つ作る。中心は拠点や出現地点から離し、ほかの林とは重ならないようにする。
        /// </summary>
        void PlantGrove(List<Vector3> centers)
        {
            float radius = Range(groveRadius);
            var candidates = new List<Vector2Int>();
            foreach (Vector2Int cell in CandidateCells())
            {
                // ステージの端に寄りすぎて、林の大半が外にはみ出さないようにする
                int cellsFromEdge = Mathf.Min(Mathf.Min(cell.x, grid.Width - 1 - cell.x), Mathf.Min(cell.y, grid.Height - 1 - cell.y));
                if (cellsFromEdge * BuildGrid.CellSize < radius * 0.6f) continue;

                Vector3 position = grid.CellCenter(cell);
                if (playerBase != null &&
                    Health.FlatDistance(position, playerBase.position) < baseClearRadius + radius * 0.5f) continue;
                if (System.Array.Exists(enemySpawns,
                        p => Health.FlatDistance(position, p.position) < spawnClearRadius + radius * 0.5f)) continue;
                if (centers.Exists(c => Health.FlatDistance(position, c) < radius + groveRadius.y)) continue;
                candidates.Add(cell);
            }
            if (candidates.Count == 0) return;

            Vector3 center = grid.CellCenter(candidates[rng.Next(candidates.Count)]);
            centers.Add(center);

            // 縁をノイズで出入りさせ、円ではなく自然な形にする
            var noiseOffset = new Vector2(rng.Next(1000), rng.Next(1000));
            var cells = new List<Vector2Int>();
            foreach (Vector2Int cell in CandidateCells())
            {
                Vector3 position = grid.CellCenter(cell);
                float distance = Health.FlatDistance(position, center);
                float noise = Mathf.PerlinNoise(noiseOffset.x + position.x * 0.12f, noiseOffset.y + position.z * 0.12f);
                float edge = radius * (0.7f + 0.6f * noise);
                if (distance > edge) continue;

                // 縁に近いところは木を減らし、林の外へ少しずつまばらになっていくようにする
                float density = distance < edge * 0.75f ? groveDensity : groveDensity * 0.5f;
                if (rng.NextDouble() < density) cells.Add(cell);
            }

            foreach (Vector2Int cell in Shuffled(cells)) TryPlant(cell);
        }

        /// <summary>木を生やしてよい、空いているマス。</summary>
        List<Vector2Int> CandidateCells()
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    var cell = new Vector2Int(x, y);
                    if (grid.Get(cell) != BuildGrid.Cell.Free) continue;
                    if (IsKeptClear(grid.CellCenter(cell))) continue;
                    cells.Add(cell);
                }
            }
            return cells;
        }

        bool IsKeptClear(Vector3 position)
        {
            if (playerBase != null && Health.FlatDistance(position, playerBase.position) < baseClearRadius) return true;
            foreach (Transform point in enemySpawns)
            {
                if (Health.FlatDistance(position, point.position) < spawnClearRadius) return true;
            }
            return false;
        }

        bool HasTreeWithin(Vector3 position, float distance)
        {
            foreach (Transform tree in trees)
            {
                if (Health.FlatDistance(tree.position, position) < distance) return true;
            }
            return false;
        }

        /// <summary>敵が拠点へ辿り着けなくならなければ、cell に木を生やす。</summary>
        bool TryPlant(Vector2Int cell)
        {
            if (!grid.TryAddObstacle(cell, mustReachBase)) return false;

            GameObject tree = Instantiate(treePrefab, grid.CellCenter(cell), Quaternion.identity, treeRoot);
            trees.Add(tree.transform);

            // 当たり判定 (根元) はマスに揃えたまま、見た目だけ向き・大きさ・位置をばらつかせる
            Transform model = tree.transform.Find("Model");
            if (model != null)
            {
                model.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * model.localRotation;
                model.localScale *= Range(scaleRange);
                Vector2 offset = new Vector2(Range(-1f, 1f), Range(-1f, 1f)) * jitter;
                model.localPosition += new Vector3(offset.x, 0f, offset.y);
            }
            return true;
        }

        List<Vector2Int> Shuffled(List<Vector2Int> cells)
        {
            for (int i = cells.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (cells[i], cells[j]) = (cells[j], cells[i]);
            }
            return cells;
        }

        float Range(float min, float max) => min + (float)rng.NextDouble() * (max - min);
        float Range(Vector2 range) => Range(range.x, range.y);
        int Range(Vector2Int range) => rng.Next(range.x, Mathf.Max(range.x, range.y) + 1);
    }
}
