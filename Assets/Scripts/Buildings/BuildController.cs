using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace RogueRTS
{
    public enum PlaceResult
    {
        Ok,
        OutOfBounds,
        NotEnoughCoins,
        Occupied,
        UnitInTheWay,
        WouldEnclose,
        NeedsStructure,
        HasTurret,
    }

    /// <summary>
    /// コインを使った建築。建築物を選ぶと設置モードになり、左クリック (押したままなぞってもよい) で設置、
    /// 右クリックか Esc で終了する。防壁と門は地面のマスに、タレットは防壁か門の上に置く。
    /// 防壁は誰も通れず、門は敵味方とも通れる。敵の進む向きを絞るために使う。
    /// 敵が拠点へ辿り着けなくなる置き方 (防壁で完全に囲う) はできない。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class BuildController : MonoBehaviour
    {
        static readonly Key[] Hotkeys = { Key.Z, Key.X, Key.C, Key.V, Key.B };
        static readonly Vector2Int NoCell = new Vector2Int(int.MinValue, int.MinValue);

        public Camera cam;
        public ResourceBank bank;
        public NavMeshSurface surface;
        [Tooltip("設置できる建築物 (並び順がボタンとホットキー Z, X, C... に対応)")]
        public BuildingData[] options;

        [Header("設置プレビュー")]
        public MeshRenderer ghost;
        public Material ghostValidMaterial;
        public Material ghostInvalidMaterial;

        [Header("グリッド (1マス = 2m)")]
        public Vector2 gridMin = new Vector2(-40f, -40f);
        public Vector2Int gridSize = new Vector2Int(40, 40);

        [Header("敵の出現地点。この周りには建築できない")]
        public Transform[] enemySpawnPoints;
        public float spawnClearRadius = 4f;

        [Tooltip("あれば、NavMesh を作る前にステージに木を生やす")]
        public ForestGenerator forest;

        /// <summary>建築によって NavMesh が更新された直後に呼ばれる。</summary>
        public event Action NavMeshUpdated;

        public BuildGrid Grid { get; private set; }
        public int SelectedIndex { get; private set; } = -1;
        public BuildingData Selected => SelectedIndex >= 0 ? options[SelectedIndex] : null;
        /// <summary>カーソル位置に今選んでいる建築物を置けるか。</summary>
        public PlaceResult HoverResult { get; private set; }
        /// <summary>設置モード中 (終了した同じフレームを含む) はユニット選択や移動命令にマウスを使わせない。</summary>
        public bool CapturesInput => SelectedIndex >= 0 || lastActiveFrame == Time.frameCount;

        readonly List<Vector3> mustReachBase = new List<Vector3>();
        readonly Collider[] overlapBuffer = new Collider[8];
        Transform structureRoot;
        AsyncOperation navMeshUpdate;
        bool navMeshDirty;
        bool painting;
        Vector2Int lastPaintedCell;
        int lastActiveFrame = -1;

        public static string HotkeyLabel(int index) => index < Hotkeys.Length ? Hotkeys[index].ToString() : "";

        public static string Describe(PlaceResult result)
        {
            switch (result)
            {
                case PlaceResult.NotEnoughCoins: return "コインが足りません";
                case PlaceResult.Occupied: return "ここには設置できません";
                case PlaceResult.UnitInTheWay: return "ユニットがいる場所には設置できません";
                case PlaceResult.WouldEnclose: return "防壁で完全に囲うことはできません (門なら置けます)";
                case PlaceResult.NeedsStructure: return "防壁か門の上にのみ設置できます";
                case PlaceResult.HasTurret: return "すでにタレットがあります";
                default: return "";
            }
        }

        void Awake()
        {
            Grid = new BuildGrid(gridMin, gridSize);
            ScanStaticCells();
            if (forest != null) forest.Plant(Grid, enemySpawnPoints);

            // ベイク済みの NavMesh アセットを実行中に書き換えないよう、実行時用のデータを作り直してから使う。
            // 生やした木も障害物になるよう、木を生やしたあとに作る
            Physics.SyncTransforms();
            surface.BuildNavMesh();

            structureRoot = new GameObject("Structures").transform;
            ghost.gameObject.SetActive(false);
        }

        /// <summary>岩や拠点が占めているマスと、敵の出現地点まわりのマスを記録する。</summary>
        void ScanStaticCells()
        {
            Physics.SyncTransforms();
            float half = BuildGrid.CellSize * 0.5f - 0.02f;
            var halfExtents = new Vector3(half, 0.9f, half);

            for (int x = 0; x < Grid.Width; x++)
            {
                for (int y = 0; y < Grid.Height; y++)
                {
                    var cell = new Vector2Int(x, y);
                    // 地面のコライダーに触れないよう、少し浮かせた箱で調べる
                    int hits = Physics.OverlapBoxNonAlloc(
                        Grid.CellCenter(cell) + Vector3.up * 1.1f, halfExtents, overlapBuffer);
                    if (hits == 0) continue;

                    bool isBase = false;
                    for (int i = 0; i < hits; i++)
                        isBase |= overlapBuffer[i].GetComponentInParent<PlayerBase>() != null;
                    Grid.Set(cell, isBase ? BuildGrid.Cell.Base : BuildGrid.Cell.Obstacle);
                }
            }

            int reach = Mathf.CeilToInt(spawnClearRadius / BuildGrid.CellSize);
            foreach (Transform point in enemySpawnPoints)
            {
                Vector2Int center = Grid.WorldToCell(point.position);
                for (int dx = -reach; dx <= reach; dx++)
                {
                    for (int dy = -reach; dy <= reach; dy++)
                    {
                        var cell = new Vector2Int(center.x + dx, center.y + dy);
                        if (Grid.Get(cell) != BuildGrid.Cell.Free) continue;
                        if (Health.FlatDistance(Grid.CellCenter(cell), point.position) > spawnClearRadius) continue;
                        Grid.Set(cell, BuildGrid.Cell.Reserved);
                    }
                }
            }

            Grid.RecomputeRegions();
        }

        void Update()
        {
            UpdateNavMesh();

            Mouse mouse = Mouse.current;
            if (mouse == null || GameManager.Instance.CurrentState != GameManager.State.Playing)
            {
                SelectOption(-1);
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                for (int i = 0; i < options.Length && i < Hotkeys.Length; i++)
                {
                    if (keyboard[Hotkeys[i]].wasPressedThisFrame) ToggleOption(i);
                }
            }

            if (SelectedIndex < 0) return;
            lastActiveFrame = Time.frameCount;

            if (mouse.rightButton.wasPressedThisFrame || (keyboard != null && keyboard.escapeKey.wasPressedThisFrame))
            {
                SelectOption(-1);
                return;
            }

            bool hovering = TryGetHoveredCell(mouse.position.ReadValue(), out Vector2Int cell);
            HoverResult = hovering ? Check(cell, Selected) : PlaceResult.OutOfBounds;
            UpdateGhost(hovering, cell);

            if (mouse.leftButton.wasPressedThisFrame && !IsPointerOverUI())
            {
                painting = true;
                lastPaintedCell = NoCell;
            }

            // 押したままなぞると、通ったマスに続けて設置する
            if (painting && hovering && cell != lastPaintedCell)
            {
                lastPaintedCell = cell;
                if (HoverResult == PlaceResult.Ok) Place(cell, Selected);
            }

            if (!mouse.leftButton.isPressed) painting = false;
        }

        void UpdateNavMesh()
        {
            if (navMeshUpdate != null)
            {
                if (!navMeshUpdate.isDone) return;
                navMeshUpdate = null;
                NavMeshUpdated?.Invoke();
            }

            if (!navMeshDirty) return;

            navMeshDirty = false;
            navMeshUpdate = surface.UpdateNavMesh(surface.navMeshData);
        }

        /// <summary>設置モードに入る。-1 で終了。</summary>
        public void SelectOption(int index)
        {
            SelectedIndex = index >= 0 && index < options.Length ? index : -1;
            painting = false;
            if (SelectedIndex < 0) ghost.gameObject.SetActive(false);
        }

        /// <summary>同じものをもう一度選ぶと設置モードを終了する。</summary>
        public void ToggleOption(int index) => SelectOption(SelectedIndex == index ? -1 : index);

        /// <summary>選択中の建築物を、ワールド座標が指すマスに設置する。</summary>
        public bool TryPlaceAtWorld(Vector3 position)
        {
            if (Selected == null) return false;

            Vector2Int cell = Grid.WorldToCell(position);
            if (Check(cell, Selected) != PlaceResult.Ok) return false;

            Place(cell, Selected);
            return true;
        }

        public PlaceResult Check(Vector2Int cell, BuildingData option)
        {
            if (!Grid.InBounds(cell)) return PlaceResult.OutOfBounds;

            if (option.type == BuildingType.Turret)
            {
                Structure structure = Grid.GetStructure(cell);
                if (structure == null) return PlaceResult.NeedsStructure;
                if (structure.Turret != null) return PlaceResult.HasTurret;
            }
            else
            {
                if (Grid.Get(cell) != BuildGrid.Cell.Free) return PlaceResult.Occupied;
                if (AnyUnitIn(cell)) return PlaceResult.UnitInTheWay;
                // 門は通り道のままなので、道を塞ぐ心配があるのは防壁だけ
                if (option.type == BuildingType.Wall && Grid.WouldCutOffBase(cell, CollectMustReachBase()))
                    return PlaceResult.WouldEnclose;
            }

            return bank.Coins >= option.cost ? PlaceResult.Ok : PlaceResult.NotEnoughCoins;
        }

        bool AnyUnitIn(Vector2Int cell)
        {
            foreach (Unit unit in Unit.All)
            {
                if (Grid.WorldToCell(unit.transform.position) == cell) return true;
            }
            return false;
        }

        /// <summary>拠点まで歩いて行けなければならない位置: 敵の出現地点と、今いる敵すべて。</summary>
        List<Vector3> CollectMustReachBase()
        {
            mustReachBase.Clear();
            foreach (Transform point in enemySpawnPoints) mustReachBase.Add(point.position);
            foreach (Unit unit in Unit.All)
            {
                if (unit.Team == Team.Enemy) mustReachBase.Add(unit.transform.position);
            }
            return mustReachBase;
        }

        void Place(Vector2Int cell, BuildingData option)
        {
            bank.TrySpendCoins(option.cost);

            if (option.type == BuildingType.Turret)
            {
                Structure host = Grid.GetStructure(cell);
                GameObject turret = Instantiate(
                    option.prefab, host.turretMount.position, Quaternion.identity, host.turretMount);
                host.Turret = turret.GetComponent<Turret>();
                return;
            }

            GameObject placed = Instantiate(option.prefab, Grid.CellCenter(cell), Quaternion.identity, structureRoot);
            Grid.AddStructure(cell, placed.GetComponent<Structure>());
            // 門はコライダーを持たず NavMesh を変えないので、作り直すのは防壁のときだけ
            if (option.type == BuildingType.Wall) navMeshDirty = true;
        }

        bool TryGetHoveredCell(Vector2 screenPosition, out Vector2Int cell)
        {
            cell = NoCell;
            Ray ray = cam.ScreenPointToRay(screenPosition);
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (!ground.Raycast(ray, out float enter)) return false;

            cell = Grid.WorldToCell(ray.GetPoint(enter));
            return Grid.InBounds(cell);
        }

        void UpdateGhost(bool visible, Vector2Int cell)
        {
            ghost.gameObject.SetActive(visible);
            if (!visible) return;

            Vector3 center = Grid.CellCenter(cell);
            if (Selected.type == BuildingType.Turret)
            {
                Structure host = Grid.GetStructure(cell);
                float mountHeight = host != null ? host.turretMount.position.y : 0f;
                ghost.transform.position = center + Vector3.up * (mountHeight + 0.6f);
                ghost.transform.localScale = new Vector3(0.9f, 1.2f, 0.9f);
            }
            else
            {
                ghost.transform.position = center + Vector3.up;
                ghost.transform.localScale = new Vector3(1.9f, 2f, 1.9f);
            }

            ghost.sharedMaterial = HoverResult == PlaceResult.Ok ? ghostValidMaterial : ghostInvalidMaterial;
        }

        static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }
    }
}
