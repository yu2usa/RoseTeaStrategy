using System.Collections.Generic;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>
    /// 建築用のマス目。どのマスに何があるかと、「敵が歩いて拠点まで辿り着けるか」を管理する。
    /// 防壁は誰も通れず、門は誰でも通れる (防壁の列に開けた通り道)。
    /// </summary>
    public class BuildGrid
    {
        public enum Cell : byte
        {
            Free,
            /// <summary>歩けるが建築はできない (敵の出現地点の周り)。</summary>
            Reserved,
            /// <summary>岩や木などの固定障害物。少しでもかかっていれば障害物扱い。</summary>
            Obstacle,
            Base,
            Wall,
            Gate,
        }

        public const float CellSize = 2f;

        static readonly Vector2Int[] Neighbors4 =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        };

        static readonly Vector3[] Directions8 =
        {
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f),
            new Vector3(0.7071f, 0f, 0.7071f), new Vector3(-0.7071f, 0f, 0.7071f),
            new Vector3(0.7071f, 0f, -0.7071f), new Vector3(-0.7071f, 0f, -0.7071f),
        };

        readonly Vector2 min;
        readonly int width;
        readonly int height;
        readonly Cell[,] cells;
        readonly Structure[,] structures;

        // 歩いて行き来できるマスのまとまりごとの番号 (歩けないマスは -1)
        readonly int[,] region;
        readonly List<bool> regionTouchesBase = new List<bool>();

        readonly Queue<Vector2Int> queue = new Queue<Vector2Int>();
        readonly int[,] visitStamp;
        int stamp;

        public BuildGrid(Vector2 worldMin, Vector2Int size)
        {
            min = worldMin;
            width = size.x;
            height = size.y;
            cells = new Cell[width, height];
            structures = new Structure[width, height];
            region = new int[width, height];
            visitStamp = new int[width, height];
        }

        public int Width => width;
        public int Height => height;

        public bool InBounds(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < width && c.y < height;

        public Vector2Int WorldToCell(Vector3 position)
        {
            return new Vector2Int(
                Mathf.FloorToInt((position.x - min.x) / CellSize),
                Mathf.FloorToInt((position.z - min.y) / CellSize));
        }

        public Vector3 CellCenter(Vector2Int c)
        {
            return new Vector3(min.x + (c.x + 0.5f) * CellSize, 0f, min.y + (c.y + 0.5f) * CellSize);
        }

        public Cell Get(Vector2Int c) => InBounds(c) ? cells[c.x, c.y] : Cell.Obstacle;

        public Structure GetStructure(Vector2Int c) => InBounds(c) ? structures[c.x, c.y] : null;

        /// <summary>初期化用。変更後は RecomputeRegions を呼ぶこと。</summary>
        public void Set(Vector2Int c, Cell value) => cells[c.x, c.y] = value;

        public void AddStructure(Vector2Int c, Structure structure)
        {
            cells[c.x, c.y] = structure.type == BuildingType.Gate ? Cell.Gate : Cell.Wall;
            structures[c.x, c.y] = structure;
            RecomputeRegions();
        }

        /// <summary>
        /// 空きマス c を固定障害物 (木) にする。mustReach のいずれかから拠点へ歩いて行けなくなるなら、
        /// 何もせず false を返す。
        /// </summary>
        public bool TryAddObstacle(Vector2Int c, IReadOnlyList<Vector3> mustReach)
        {
            if (Get(c) != Cell.Free || WouldCutOffBase(c, mustReach)) return false;

            cells[c.x, c.y] = Cell.Obstacle;
            RecomputeRegions();
            return true;
        }

        static bool Walkable(Cell cell) => cell == Cell.Free || cell == Cell.Reserved || cell == Cell.Gate;

        int RegionAt(Vector2Int c) => InBounds(c) ? region[c.x, c.y] : -1;

        bool IsBaseAdjacent(Vector2Int c)
        {
            foreach (Vector2Int offset in Neighbors4)
            {
                if (Get(c + offset) == Cell.Base) return true;
            }
            return false;
        }

        /// <summary>歩けるマスを、互いに行き来できるまとまりに塗り分ける。</summary>
        public void RecomputeRegions()
        {
            regionTouchesBase.Clear();
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++) region[x, y] = -1;
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (region[x, y] >= 0 || !Walkable(cells[x, y])) continue;

                    int id = regionTouchesBase.Count;
                    bool touchesBase = false;
                    region[x, y] = id;
                    queue.Enqueue(new Vector2Int(x, y));
                    while (queue.Count > 0)
                    {
                        Vector2Int c = queue.Dequeue();
                        touchesBase |= IsBaseAdjacent(c);
                        foreach (Vector2Int offset in Neighbors4)
                        {
                            Vector2Int n = c + offset;
                            if (!InBounds(n) || region[n.x, n.y] >= 0 || !Walkable(cells[n.x, n.y])) continue;
                            region[n.x, n.y] = id;
                            queue.Enqueue(n);
                        }
                    }
                    regionTouchesBase.Add(touchesBase);
                }
            }
        }

        /// <summary>
        /// 位置に対応する歩けるマス。岩に少しかかっているマスにいる場合は、隣の歩けるマスで代用する。
        /// </summary>
        bool TryGetWalkableCellNear(Vector3 position, out Vector2Int cell)
        {
            cell = WorldToCell(position);
            if (RegionAt(cell) >= 0) return true;

            Vector2Int origin = cell;
            bool found = false;
            float bestSqr = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    var candidate = new Vector2Int(origin.x + dx, origin.y + dy);
                    if (RegionAt(candidate) < 0) continue;

                    float sqr = (CellCenter(candidate) - position).sqrMagnitude;
                    if (sqr >= bestSqr) continue;

                    bestSqr = sqr;
                    cell = candidate;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// blocked のマスを防壁で塞ぐと、mustReach のいずれかの位置から拠点へ歩いて行けなくなるか。
        /// </summary>
        public bool WouldCutOffBase(Vector2Int blocked, IReadOnlyList<Vector3> mustReach)
        {
            Cell original = cells[blocked.x, blocked.y];
            cells[blocked.x, blocked.y] = Cell.Wall;
            RecomputeRegions();

            bool cutOff = false;
            foreach (Vector3 position in mustReach)
            {
                if (!TryGetWalkableCellNear(position, out Vector2Int c)) continue;
                if (regionTouchesBase[region[c.x, c.y]]) continue;

                cutOff = true;
                break;
            }

            cells[blocked.x, blocked.y] = original;
            RecomputeRegions();
            return cutOff;
        }

        /// <summary>
        /// from にいる敵が、防壁を越えずに target へ近づける場合、その接近先を返す。
        /// 拠点のように歩けないマスにある対象は、外周のうち歩いて行ける点を選ぶ。
        /// </summary>
        public bool TryGetEnemyApproach(Vector3 from, Vector3 targetPosition, float targetRadius, out Vector3 point)
        {
            point = targetPosition;
            if (!TryGetWalkableCellNear(from, out Vector2Int fromCell)) return false;

            int myRegion = region[fromCell.x, fromCell.y];
            if (RegionAt(WorldToCell(targetPosition)) == myRegion) return true;

            bool found = false;
            float bestSqr = float.MaxValue;
            float ring = targetRadius + 0.8f;
            foreach (Vector3 direction in Directions8)
            {
                Vector3 candidate = targetPosition + direction * ring;
                if (RegionAt(WorldToCell(candidate)) != myRegion) continue;

                float sqr = (candidate - from).sqrMagnitude;
                if (sqr >= bestSqr) continue;

                bestSqr = sqr;
                point = candidate;
                found = true;
            }
            return found;
        }

        /// <summary>from から歩いて行ける、拠点に隣接したマスのうち最も近いものの中心を返す。</summary>
        public bool TryFindEnemyGoal(Vector3 from, out Vector3 goal)
        {
            goal = default;
            if (!TryGetWalkableCellNear(from, out Vector2Int start)) return false;

            stamp++;
            queue.Clear();
            queue.Enqueue(start);
            visitStamp[start.x, start.y] = stamp;

            while (queue.Count > 0)
            {
                Vector2Int c = queue.Dequeue();
                if (IsBaseAdjacent(c))
                {
                    goal = CellCenter(c);
                    queue.Clear();
                    return true;
                }

                foreach (Vector2Int offset in Neighbors4)
                {
                    Vector2Int n = c + offset;
                    if (RegionAt(n) < 0 || visitStamp[n.x, n.y] == stamp) continue;
                    visitStamp[n.x, n.y] = stamp;
                    queue.Enqueue(n);
                }
            }
            return false;
        }
    }
}
