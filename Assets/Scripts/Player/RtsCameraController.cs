using UnityEngine;
using UnityEngine.InputSystem;

namespace RogueRTS
{
    /// <summary>
    /// カメラの操作。次の 3 つのモードを切り替えて使う。
    /// 通常: 見下ろし型の RTS カメラ。WASD/矢印キーで地面上の注視点を動かし、ホイールで注視点との距離を変える。
    /// フリー: Ctrl を押しながらマウスを動かすと向きが変わり、WASD でその向きへ進む。通常のときに Ctrl+マウスを使うとこれになる。
    /// 追跡: 味方ユニットを後ろから見る三人称視点。Ctrl+マウスでユニットの周りを回り込める。
    ///       WASD を押すか、ユニットが倒れるとカメラが外れてフリーになる。
    /// G を押すといつでも通常に戻る。通常と追跡へは、今の位置から飛んでいくように切り替わる。
    /// </summary>
    public class RtsCameraController : MonoBehaviour
    {
        public enum Mode { Rts, Free, Follow }

        [Header("移動")]
        public float panSpeed = 22f;
        public Vector2 boundsMin = new Vector2(-38f, -38f);
        public Vector2 boundsMax = new Vector2(38f, 38f);

        [Header("ズーム")]
        public float minDistance = 10f;
        public float maxDistance = 45f;
        [Tooltip("ホイール1目盛りあたりの距離変化")]
        public float zoomStep = 4f;
        public float zoomSmoothing = 12f;

        [Header("視点")]
        public float pitch = 55f;
        [Tooltip("カメラが見ている地面上の点")]
        public Vector3 focus = new Vector3(0f, 0f, -20f);
        public float distance = 28f;

        [Header("フリー")]
        [Tooltip("Ctrl+マウスで向きを変えるときの、マウス 1px あたりの角度")]
        public float lookSensitivity = 0.15f;
        public float freeMoveSpeed = 18f;
        [Tooltip("フリーのカメラが、移動範囲 (boundsMin/Max) からはみ出せる距離")]
        public float freeBoundsMargin = 30f;
        public float freeMinHeight = 0.5f;
        public float freeMaxHeight = 80f;

        [Header("追跡 (三人称視点)")]
        [Tooltip("カメラが見る点の、ユニットの足元からの高さ")]
        public float followLookHeight = 1.5f;
        public float followDistance = 6f;
        public float followMinDistance = 2.5f;
        public float followMaxDistance = 15f;
        [Tooltip("ホイール1目盛りあたりの、ユニットとの距離の変化")]
        public float followZoomStep = 1.5f;
        [Tooltip("追跡を始めたときの見下ろす角度")]
        public float followPitch = 18f;
        [Tooltip("ユニットが向きを変えたとき、カメラが背中側へ回り込む速さ")]
        public float followTurnRate = 3f;

        [Tooltip("通常や追跡に切り替えたとき、カメラが飛んでいくのにかける秒数")]
        public float flyDuration = 0.7f;

        public Mode CurrentMode { get; private set; } = Mode.Rts;
        /// <summary>追跡しているユニット。追跡以外のモードでは null。</summary>
        public Unit FollowTarget { get; private set; }

        float targetDistance;
        float freeYaw, freePitch;
        // 追跡中の向き。heading はなめらかに追いかけるユニットの向きで、orbitYaw は Ctrl+マウスで足したずれ
        float followHeading, orbitYaw, orbitPitch;
        float followCurrentDistance, followTargetDistance;
        bool flying;
        float flyTime;
        Vector3 flyFromPosition;
        Quaternion flyFromRotation;
        bool cursorLocked;

        void Awake() => targetDistance = distance;

        void OnDisable() => LockCursor(false);

        void Update()
        {
            // 一時停止中や報酬選択中 (timeScale = 0) でも動かせるよう、スケールされない時間を使う
            float dt = Time.unscaledDeltaTime;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard != null && keyboard.gKey.wasPressedThisFrame) ReturnToDefault();
            if (CurrentMode == Mode.Follow && (FollowTarget == null || FollowTarget.Health.IsDead)) EnterFree();

            // Ctrl を押している間はカーソルを中央に固定し、マウスの動きを向きの変更に使う
            bool looking = keyboard != null && keyboard.ctrlKey.isPressed && mouse != null;
            LockCursor(looking);
            Vector2 look = looking ? mouse.delta.ReadValue() * lookSensitivity : Vector2.zero;
            Vector2 move = ReadMoveInput();
            // 環境によって1目盛りが 1 だったり 120 だったりするので ±1 に丸める
            float scroll = mouse != null ? Mathf.Clamp(mouse.scroll.ReadValue().y, -1f, 1f) : 0f;

            if ((CurrentMode == Mode.Rts && look != Vector2.zero) || (CurrentMode == Mode.Follow && move != Vector2.zero))
                EnterFree();

            switch (CurrentMode)
            {
                case Mode.Rts: UpdateRts(move, scroll, dt); break;
                case Mode.Free: UpdateFree(look, move, scroll, dt); break;
                case Mode.Follow: UpdateFollow(look, scroll, dt); break;
            }
        }

        /// <summary>カメラを unit の後ろへ飛ばし、三人称視点で追いかける。</summary>
        public void Follow(Unit unit)
        {
            FollowTarget = unit;
            CurrentMode = Mode.Follow;
            followHeading = unit.transform.eulerAngles.y;
            orbitYaw = 0f;
            orbitPitch = followPitch;
            followCurrentDistance = followTargetDistance = followDistance;
            StartFlight();
        }

        /// <summary>通常の見下ろし型のカメラに戻る。今見ている場所 (追跡中ならそのユニット) が注視点になる。</summary>
        public void ReturnToDefault()
        {
            if (CurrentMode == Mode.Rts) return;

            Vector3 point = CurrentMode == Mode.Follow && FollowTarget != null
                ? FollowTarget.transform.position
                : GroundPointInView();
            focus = new Vector3(
                Mathf.Clamp(point.x, boundsMin.x, boundsMax.x), 0f, Mathf.Clamp(point.z, boundsMin.y, boundsMax.y));

            FollowTarget = null;
            CurrentMode = Mode.Rts;
            StartFlight();
        }

        /// <summary>今の位置と向きのまま、フリーのカメラにする。</summary>
        void EnterFree()
        {
            Vector3 euler = transform.eulerAngles;
            freeYaw = euler.y;
            freePitch = Mathf.DeltaAngle(0f, euler.x);
            FollowTarget = null;
            CurrentMode = Mode.Free;
            flying = false;
        }

        void UpdateRts(Vector2 move, float scroll, float dt)
        {
            if (move != Vector2.zero)
            {
                // 引いているときほど速く動かす
                float speed = panSpeed * Mathf.Lerp(0.6f, 1.5f, Mathf.InverseLerp(minDistance, maxDistance, distance));
                focus += new Vector3(move.x, 0f, move.y).normalized * (speed * dt);
                focus.x = Mathf.Clamp(focus.x, boundsMin.x, boundsMax.x);
                focus.z = Mathf.Clamp(focus.z, boundsMin.y, boundsMax.y);
            }

            targetDistance = Mathf.Clamp(targetDistance - scroll * zoomStep, minDistance, maxDistance);
            distance = Mathf.Lerp(distance, targetDistance, 1f - Mathf.Exp(-zoomSmoothing * dt));

            Quaternion rotation = Quaternion.Euler(pitch, 0f, 0f);
            SetPose(focus - rotation * Vector3.forward * distance, rotation, dt);
        }

        void UpdateFree(Vector2 look, Vector2 move, float scroll, float dt)
        {
            freeYaw += look.x;
            freePitch = Mathf.Clamp(freePitch - look.y, -85f, 85f);
            Quaternion rotation = Quaternion.Euler(freePitch, freeYaw, 0f);

            // 前後は見ている向きそのもの (下を向いていれば下がっていく)、左右は水平に動く
            Vector3 velocity = Vector3.ClampMagnitude(rotation * new Vector3(move.x, 0f, move.y), 1f) * freeMoveSpeed;
            Vector3 position = transform.position + velocity * dt + rotation * Vector3.forward * (scroll * zoomStep);
            position.x = Mathf.Clamp(position.x, boundsMin.x - freeBoundsMargin, boundsMax.x + freeBoundsMargin);
            position.y = Mathf.Clamp(position.y, freeMinHeight, freeMaxHeight);
            position.z = Mathf.Clamp(position.z, boundsMin.y - freeBoundsMargin, boundsMax.y + freeBoundsMargin);
            transform.SetPositionAndRotation(position, rotation);
        }

        void UpdateFollow(Vector2 look, float scroll, float dt)
        {
            Transform target = FollowTarget.transform;
            followHeading = Mathf.LerpAngle(followHeading, target.eulerAngles.y, 1f - Mathf.Exp(-followTurnRate * dt));
            orbitYaw += look.x;
            orbitPitch = Mathf.Clamp(orbitPitch - look.y, -10f, 75f);

            followTargetDistance = Mathf.Clamp(
                followTargetDistance - scroll * followZoomStep, followMinDistance, followMaxDistance);
            followCurrentDistance = Mathf.Lerp(
                followCurrentDistance, followTargetDistance, 1f - Mathf.Exp(-zoomSmoothing * dt));

            Quaternion rotation = Quaternion.Euler(orbitPitch, followHeading + orbitYaw, 0f);
            Vector3 position = target.position + Vector3.up * followLookHeight
                - rotation * Vector3.forward * followCurrentDistance;
            // 見上げたときに地面へ潜らないようにする
            position.y = Mathf.Max(position.y, freeMinHeight);
            SetPose(position, rotation, dt);
        }

        /// <summary>今の位置から飛び始める。以降の SetPose は、しばらく出発点と行き先の間を補間する。</summary>
        void StartFlight()
        {
            flying = true;
            flyTime = 0f;
            flyFromPosition = transform.position;
            flyFromRotation = transform.rotation;
        }

        void SetPose(Vector3 position, Quaternion rotation, float dt)
        {
            if (flying)
            {
                flyTime += dt;
                float t = Mathf.SmoothStep(0f, 1f, flyTime / flyDuration);
                position = Vector3.Lerp(flyFromPosition, position, t);
                rotation = Quaternion.Slerp(flyFromRotation, rotation, t);
                flying = flyTime < flyDuration;
            }
            transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>カメラの正面に見えている地面の点。地面を向いていなければ真下の点。</summary>
        Vector3 GroundPointInView()
        {
            var ray = new Ray(transform.position, transform.forward);
            var ground = new Plane(Vector3.up, Vector3.zero);
            if (ray.direction.y < -0.05f && ground.Raycast(ray, out float enter)) return ray.GetPoint(enter);
            return new Vector3(transform.position.x, 0f, transform.position.z);
        }

        void LockCursor(bool locked)
        {
            if (locked == cursorLocked) return;

            cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        static Vector2 ReadMoveInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return Vector2.zero;

            Vector2 move = Vector2.zero;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
            return move;
        }

        /// <summary>通常のモードの位置と向きにすぐ合わせる (シーンを作るときに使う)。</summary>
        public void ApplyTransform()
        {
            transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
            transform.position = focus - transform.forward * distance;
        }
    }
}
