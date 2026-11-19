using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace RogueRTS
{
    /// <summary>
    /// 左クリック/ドラッグで自軍ユニットを選択し (Shift で追加選択)、右クリックで移動命令を出す。
    /// </summary>
    public class SelectionController : MonoBehaviour
    {
        public Camera cam;
        [Tooltip("Canvas 直下に置いた選択枠用の Image (アンカー・ピボットは左下)")]
        public RectTransform selectionBox;
        public MoveMarker moveMarker;

        [Tooltip("これ以上マウスを動かしたらドラッグ扱いにする (px)")]
        public float dragThreshold = 8f;
        [Tooltip("クリック選択で許容する、ユニットとカーソルの距離 (px)")]
        public float clickRadius = 40f;
        [Tooltip("複数ユニットを移動させたときの整列間隔")]
        public float formationSpacing = 1.6f;

        readonly List<Unit> selected = new List<Unit>();
        Canvas canvas;
        Vector2 dragStart;
        bool pressing;

        void Awake()
        {
            canvas = selectionBox.GetComponentInParent<Canvas>(true);
            selectionBox.gameObject.SetActive(false);
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            GameManager game = GameManager.Instance;
            // 建築の設置モード中は、クリックを建築に譲る
            bool building = game.buildController != null && game.buildController.CapturesInput;
            if (mouse == null || game.CurrentState != GameManager.State.Playing || building)
            {
                EndDrag();
                return;
            }

            Vector2 position = mouse.position.ReadValue();

            if (mouse.leftButton.wasPressedThisFrame && !IsPointerOverUI())
            {
                pressing = true;
                dragStart = position;
            }

            if (pressing)
            {
                bool isDrag = (position - dragStart).sqrMagnitude > dragThreshold * dragThreshold;
                ShowBox(isDrag, dragStart, position);

                if (!mouse.leftButton.isPressed)
                {
                    EndDrag();
                    Keyboard keyboard = Keyboard.current;
                    bool additive = keyboard != null && keyboard.shiftKey.isPressed;
                    if (!additive) ClearSelection();

                    if (isDrag) SelectInRect(dragStart, position);
                    else SelectAt(position);
                }
            }

            if (mouse.rightButton.wasPressedThisFrame && !IsPointerOverUI())
                OrderMove(position);
        }

        static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        void EndDrag()
        {
            pressing = false;
            selectionBox.gameObject.SetActive(false);
        }

        void ShowBox(bool visible, Vector2 a, Vector2 b)
        {
            selectionBox.gameObject.SetActive(visible);
            if (!visible) return;

            // スクリーン座標 (px) を Canvas のスケールに合わせる
            float scale = canvas.scaleFactor;
            selectionBox.anchoredPosition = Vector2.Min(a, b) / scale;
            selectionBox.sizeDelta = new Vector2(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y)) / scale;
        }

        void ClearSelection()
        {
            foreach (Unit unit in selected)
            {
                if (unit != null) unit.SetSelected(false);
            }
            selected.Clear();
        }

        void Select(Unit unit)
        {
            if (selected.Contains(unit)) return;

            selected.Add(unit);
            unit.SetSelected(true);
        }

        void SelectInRect(Vector2 a, Vector2 b)
        {
            Rect rect = Rect.MinMaxRect(
                Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            foreach (Unit unit in Unit.All)
            {
                if (unit.Team != Team.Player) continue;
                if (TryGetScreenPoint(unit, out Vector2 point) && rect.Contains(point)) Select(unit);
            }
        }

        void SelectAt(Vector2 screenPosition)
        {
            Unit best = null;
            float bestSqr = clickRadius * clickRadius;
            foreach (Unit unit in Unit.All)
            {
                if (unit.Team != Team.Player) continue;
                if (!TryGetScreenPoint(unit, out Vector2 point)) continue;

                float sqr = (point - screenPosition).sqrMagnitude;
                if (sqr > bestSqr) continue;

                best = unit;
                bestSqr = sqr;
            }

            if (best != null) Select(best);
        }

        /// <summary>ユニットの胴体あたりのスクリーン座標。カメラの後ろにいる場合は false。</summary>
        bool TryGetScreenPoint(Unit unit, out Vector2 point)
        {
            Vector3 screen = cam.WorldToScreenPoint(unit.transform.position + Vector3.up);
            point = screen;
            return screen.z > 0f;
        }

        void OrderMove(Vector2 screenPosition)
        {
            selected.RemoveAll(unit => unit == null);
            if (selected.Count == 0) return;

            Ray ray = cam.ScreenPointToRay(screenPosition);
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (!ground.Raycast(ray, out float enter)) return;
            if (!NavMesh.SamplePosition(ray.GetPoint(enter), out NavMeshHit center, 5f, NavMesh.AllAreas)) return;

            // 全員が同じ1点を取り合わないよう、目的地を中心に格子状に並べる
            int columns = Mathf.CeilToInt(Mathf.Sqrt(selected.Count));
            int rows = Mathf.CeilToInt(selected.Count / (float)columns);
            for (int i = 0; i < selected.Count; i++)
            {
                var offset = new Vector3(
                    i % columns - (columns - 1) * 0.5f, 0f, i / columns - (rows - 1) * 0.5f) * formationSpacing;
                selected[i].MoveTo(NavMeshUtil.Snap(center.position + offset, 2f));
            }

            if (moveMarker != null) moveMarker.Show(center.position);
        }
    }
}
