using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RogueRTS
{
    /// <summary>
    /// F を押している間はスローモーションにして、カーソルに一番近い味方ユニットの頭上に印を出す。
    /// 印が出ているうちに F を離すと、カメラをそのユニットの三人称視点へ飛ばす。
    /// 画面下には、今のカメラのモードと操作のヒントを出す。
    /// </summary>
    public class UnitFocusController : MonoBehaviour
    {
        public Camera cam;
        public RtsCameraController cameraController;
        [Tooltip("候補のユニットの頭上に出す印 (Canvas 直下、アンカーは左下)")]
        public TMP_Text marker;
        public TMP_Text hintText;

        [Tooltip("カーソルからこの距離 (px) 以内にいる味方を候補にする")]
        public float pickRadius = 90f;
        [Tooltip("印を出す位置の、ユニットの足元からの高さ")]
        public float markerHeight = 2.4f;

        /// <summary>F を押して、視点を移すユニットを選んでいる最中か。</summary>
        public bool Aiming { get; private set; }
        /// <summary>今 F を離したら視点が移るユニット。いなければ null。</summary>
        public Unit Candidate { get; private set; }

        Canvas canvas;

        void Awake()
        {
            canvas = marker.GetComponentInParent<Canvas>(true);
            marker.gameObject.SetActive(false);
        }

        void Update()
        {
            GameManager game = GameManager.Instance;
            Keyboard keyboard = Keyboard.current;
            // 報酬選択中や敗北後は使えない。一時停止中は止まったまま選べる
            bool usable = game.CurrentState == GameManager.State.Playing || game.CurrentState == GameManager.State.Paused;
            bool holding = usable && keyboard != null && keyboard.fKey.isPressed;

            if (Aiming && !holding && usable && Candidate != null) cameraController.Follow(Candidate);
            if (holding != Aiming)
            {
                Aiming = holding;
                game.SetSlowMotion(holding);
            }
            Candidate = Aiming ? FindCandidate() : null;

            UpdateMarker();
            hintText.text = Hint();
        }

        Unit FindCandidate()
        {
            // Ctrl で向きを変えている間はカーソルが中央に固定されるので、画面の中央で狙う
            Mouse mouse = Mouse.current;
            Vector2 pointer = mouse == null || Cursor.lockState == CursorLockMode.Locked
                ? new Vector2(cam.pixelWidth, cam.pixelHeight) * 0.5f
                : mouse.position.ReadValue();

            Unit best = null;
            float bestSqr = pickRadius * pickRadius;
            foreach (Unit unit in Unit.All)
            {
                if (unit.Team != Team.Player || unit == cameraController.FollowTarget) continue;

                Vector3 screen = cam.WorldToScreenPoint(unit.transform.position + Vector3.up);
                if (screen.z <= 0f) continue;

                float sqr = ((Vector2)screen - pointer).sqrMagnitude;
                if (sqr > bestSqr) continue;

                best = unit;
                bestSqr = sqr;
            }
            return best;
        }

        void UpdateMarker()
        {
            bool visible = Candidate != null;
            if (marker.gameObject.activeSelf != visible) marker.gameObject.SetActive(visible);
            if (!visible) return;

            // スクリーン座標 (px) を Canvas のスケールに合わせる
            Vector3 screen = cam.WorldToScreenPoint(Candidate.transform.position + Vector3.up * markerHeight);
            marker.rectTransform.anchoredPosition = (Vector2)screen / canvas.scaleFactor;
            marker.text = $"{Candidate.data.displayName}\n▼";
        }

        string Hint()
        {
            if (Aiming)
            {
                return Candidate != null
                    ? $"F を離すと {Candidate.data.displayName} の視点に移ります"
                    : "スロー中: 味方にカーソルを合わせて F を離すと、その視点に移ります";
            }

            switch (cameraController.CurrentMode)
            {
                case RtsCameraController.Mode.Follow:
                    Unit target = cameraController.FollowTarget;
                    string name = target != null ? target.data.displayName : "";
                    return $"{name} の視点　　Ctrl+マウス: 向き　　WASD: 離れる　　G: 通常のカメラ";
                case RtsCameraController.Mode.Free:
                    return "フリーカメラ　　Ctrl+マウス: 向き　　WASD: その向きへ移動　　G: 通常のカメラ";
                default:
                    return "";
            }
        }
    }
}
