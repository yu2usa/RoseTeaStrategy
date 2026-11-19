using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

/// <summary>
/// Game シーンを実際に動かして一通りの流れを確認するスモークテスト。
/// ゲーム本体は Assembly-CSharp にあり asmdef から型を参照できないため、オブジェクト名で確認している。
/// </summary>
public class GameSmokeTest
{
    // 等速 (始めの速さ) のときの Time.timeScale
    const float NormalTimeScale = 0.5f;

    Keyboard keyboard;
    Mouse mouse;
    InputSettings.BackgroundBehavior savedBackgroundBehavior;
    // エディタを開いたまま実行したとき、実機のマウスやキーボードの操作がテストに混ざらないよう止めておく
    readonly List<InputDevice> disabledDevices = new List<InputDevice>();

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // フォーカスのないバッチモードでも仮想デバイスの入力がゲームに届くようにする
        savedBackgroundBehavior = InputSystem.settings.backgroundBehavior;
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        foreach (InputDevice device in InputSystem.devices.ToArray())
        {
            if (!(device is Pointer || device is Keyboard) || !device.enabled) continue;
            InputSystem.DisableDevice(device);
            disabledDevices.Add(device);
        }
        keyboard = InputSystem.AddDevice<Keyboard>();
        mouse = InputSystem.AddDevice<Mouse>();

        // 位置を決め打ちしているテストが多いので、木の生えないステージで動かす。木のテストは自分で読み込み直す
        SetForestPatternOverride("None");
        yield return SceneManager.LoadSceneAsync("Game");
        yield return null;
    }

    [TearDown]
    public void TearDown()
    {
        SetForestPatternOverride(null);
        Time.timeScale = 1f;
        InputSystem.RemoveDevice(keyboard);
        InputSystem.RemoveDevice(mouse);
        foreach (InputDevice device in disabledDevices)
        {
            if (device.added) InputSystem.EnableDevice(device);
        }
        disabledDevices.Clear();
        InputSystem.settings.backgroundBehavior = savedBackgroundBehavior;
#if UNITY_EDITOR
        InputSystem.settings.editorInputBehaviorInPlayMode =
            InputSettings.EditorInputBehaviorInPlayMode.PointersAndKeyboardsRespectGameViewFocus;
#endif
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator ClearingStage_OffersThreeRewards_AndChoosingOneStartsNextStage()
    {
        Assert.AreEqual(6, CountAgents(IsPlayerUnit), "開始時の自軍ユニット数");

        Time.timeScale = 10f;
        Button produceSoldier = GameObject.Find("ProduceButton0").GetComponent<Button>();
        int peakEnemies = 0;
        float deadline = Time.realtimeSinceStartup + 240f;

        while (GameObject.Find("RewardPanel") == null)
        {
            Assert.IsNull(GameObject.Find("GameOverPanel"), "ステージ1で拠点が破壊された");
            Assert.Less(Time.realtimeSinceStartup, deadline, "ステージ1がクリアされないままタイムアウト");

            // ゴールドが貯まるたびに兵士を生産し続けるプレイヤーを模す
            if (produceSoldier.interactable) produceSoldier.onClick.Invoke();
            peakEnemies = Mathf.Max(peakEnemies, CountAgents(IsEnemyUnit));
            yield return null;
        }

        Assert.Greater(peakEnemies, 0, "敵が一度も出現していない");
        Assert.Greater(CountAgents(IsPlayerUnit), 6, "ユニットが生産されていない");
        Assert.AreEqual(0, CountAgents(IsEnemyUnit), "敵が残っているのに勝利になった");
        Assert.AreEqual(0f, Time.timeScale, "報酬選択中は時間が止まる");
        Assert.Greater(Coins(), 0, "敵を倒してもコインが増えていない");

        string[] titles = Enumerable.Range(0, 3)
            .Select(i => GameObject.Find($"RewardChoice{i}").transform.Find("Title").GetComponent<TMP_Text>().text)
            .ToArray();
        Assert.IsTrue(titles.All(t => !string.IsNullOrEmpty(t)), "報酬名が空");
        Assert.AreEqual(3, titles.Distinct().Count(), "報酬の3択が重複している");

        GameObject.Find("RewardChoice0").GetComponent<Button>().onClick.Invoke();
        yield return null;
        yield return null;

        Assert.IsNull(GameObject.Find("RewardPanel"), "報酬を選んだのに画面が閉じない");
        Assert.AreEqual(NormalTimeScale, Time.timeScale);
        StringAssert.Contains("ステージ 2", GameObject.Find("StageText").GetComponent<TMP_Text>().text);
    }

    [UnityTest]
    public IEnumerator BaseDestroyed_ShowsGameOver_AndRetryRestarts()
    {
        GameObject.Find("PlayerBase").SendMessage("TakeDamage", 1e9f);
        yield return null;

        Assert.IsNotNull(GameObject.Find("GameOverPanel"), "拠点が破壊されても敗北画面が出ない");
        Assert.AreEqual(0f, Time.timeScale);

        GameObject.Find("RetryButton").GetComponent<Button>().onClick.Invoke();
        yield return null;
        yield return null;

        Assert.IsNull(GameObject.Find("GameOverPanel"));
        Assert.AreEqual(NormalTimeScale, Time.timeScale);
        StringAssert.Contains("ステージ 1", GameObject.Find("StageText").GetComponent<TMP_Text>().text);
    }

    [UnityTest]
    public IEnumerator Camera_PansWithWasd_AndZoomsWithWheel()
    {
        Transform cam = Camera.main.transform;
        Vector3 start = cam.position;

        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W, Key.D));
        yield return new WaitForSecondsRealtime(0.5f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;

        Assert.Greater(cam.position.z, start.z + 2f, "W で奥へ動かない");
        Assert.Greater(cam.position.x, start.x + 2f, "D で右へ動かない");
        Assert.AreEqual(start.y, cam.position.y, 0.01f, "移動だけで高さが変わった");

        Vector2 center = new Vector2(Camera.main.pixelWidth, Camera.main.pixelHeight) * 0.5f;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = center, scroll = new Vector2(0f, 120f) });
        yield return new WaitForSecondsRealtime(0.5f);

        Assert.Less(cam.position.y, start.y - 1f, "ホイールでズームインしない");
    }

    [UnityTest]
    public IEnumerator DragSelectsUnits_RightClickMovesThem_ClickOnGroundDeselects()
    {
        Camera cam = Camera.main;
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f); // 初期ユニットが集合地点に着くのを待つ

        Assert.AreEqual(0, SelectedUnits().Length, "最初は何も選択されていない");

        // 画面のほぼ全体をドラッグして全ユニットを囲む
        var from = new Vector2(cam.pixelWidth * 0.02f, cam.pixelHeight * 0.35f);
        var to = new Vector2(cam.pixelWidth * 0.98f, cam.pixelHeight * 0.97f);
        yield return SendMouse(from);
        yield return SendMouse(from, MouseButton.Left);
        yield return SendMouse(to, MouseButton.Left);
        Assert.IsNotNull(GameObject.Find("SelectionBox"), "ドラッグ中に選択枠が出ない");
        yield return SendMouse(to);

        Transform[] selected = SelectedUnits();
        Assert.AreEqual(6, selected.Length, "ドラッグで全ユニットが選択されない");
        Assert.IsNull(GameObject.Find("SelectionBox"), "ドラッグ後も選択枠が残っている");

        // 右クリックで移動命令
        var goal = new Vector3(12f, 0f, -12f);
        Vector3 goalOnScreen = cam.WorldToScreenPoint(goal);
        Assert.IsTrue(cam.pixelRect.Contains(goalOnScreen), "テスト用の移動先が画面外");
        float before = selected.Average(t => Vector3.Distance(t.position, goal));

        yield return SendMouse(goalOnScreen);
        yield return SendMouse(goalOnScreen, MouseButton.Right);
        yield return SendMouse(goalOnScreen);
        yield return new WaitForSeconds(6f);

        float after = selected.Average(t => Vector3.Distance(t.position, goal));
        Assert.Less(after, 3f, $"右クリックした地点へ移動していない (平均距離 {before:0.0} → {after:0.0})");

        // 何もない地面をクリックすると選択解除
        Vector3 emptyGround = cam.WorldToScreenPoint(new Vector3(-12f, 0f, -22f));
        yield return SendMouse(emptyGround);
        yield return SendMouse(emptyGround, MouseButton.Left);
        yield return SendMouse(emptyGround);

        Assert.AreEqual(0, SelectedUnits().Length, "地面クリックで選択解除されない");
    }

    [UnityTest]
    public IEnumerator Walls_CannotFullyEncloseBase_ButGateKeepsItOpen_TurretsGoOnTop()
    {
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f); // 初期ユニットが拠点前から離れるのを待つ
        Time.timeScale = 1f;

        GameObject systems = GameObject.Find("GameSystems");
        systems.SendMessage("AddCoins", 2000);

        // 1マスだけ残して防壁で囲む
        systems.SendMessage("SelectOption", (object)0);
        foreach (Vector3 cell in RingAroundBase())
        {
            if (cell != GateCell) systems.SendMessage("TryPlaceAtWorld", cell);
        }
        Assert.AreEqual(19, CountNamed("Wall(Clone)"), "輪の防壁が置けていない");

        // 最後の1マスを防壁で塞ぐと完全に囲うことになるので置けない
        systems.SendMessage("TryPlaceAtWorld", GateCell);
        Assert.AreEqual(19, CountNamed("Wall(Clone)"), "防壁で完全に囲えてしまった");

        // 門は通り道なので、最後の1マスにも置ける
        systems.SendMessage("SelectOption", (object)1);
        systems.SendMessage("TryPlaceAtWorld", GateCell);
        Assert.AreEqual(1, CountNamed("Gate(Clone)"), "最後の1マスに門が置けない");

        yield return null;
        Assert.AreEqual(2000 - 19 * 15 - 30, Coins(), "設置したぶんだけコインが減る");

        // NavMesh の更新を待ってから、輪の外と中を結ぶ経路を調べる
        yield return new WaitForSecondsRealtime(1.5f);
        var path = new NavMeshPath();

        Assert.IsTrue(NavMesh.CalculatePath(new Vector3(1f, 0f, -20f), new Vector3(1f, 0f, -25f), NavMesh.AllAreas, path));
        Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "門を通って輪の中へ入れない");
        Assert.Less(PathLength(path), 8f, "門をまっすぐ通り抜けられない");

        // 防壁は越えられないので、西側からは門まで回り込むことになる
        Assert.IsTrue(NavMesh.CalculatePath(new Vector3(-8f, 0f, -27f), new Vector3(-3f, 0f, -27f), NavMesh.AllAreas, path));
        Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, "門まで回り込む経路がない");
        Assert.Greater(PathLength(path), 15f, "防壁を通り抜けてしまう");

        // タレットは防壁か門の上にだけ、1マスに1基まで置ける
        int coinsBeforeTurrets = Coins();
        var wallCell = new Vector3(-1f, 0f, -23f);
        systems.SendMessage("SelectOption", (object)2);
        systems.SendMessage("TryPlaceAtWorld", new Vector3(11f, 0f, -15f));
        Assert.AreEqual(0, CountNamed("Turret(Clone)"), "地面にタレットが置けてしまった");
        systems.SendMessage("TryPlaceAtWorld", wallCell);
        systems.SendMessage("TryPlaceAtWorld", wallCell);
        systems.SendMessage("TryPlaceAtWorld", GateCell);
        Assert.AreEqual(2, CountNamed("Turret(Clone)"), "防壁と門の上に1基ずつ置けるはず");
        Assert.IsTrue(Named("Turret(Clone)").All(t => t.position.y >= 1.9f), "タレットが建築物の上に載っていない");

        yield return null;
        Assert.AreEqual(coinsBeforeTurrets - 2 * 150, Coins(), "タレットは別途コインがかかる");
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator EnemiesEnterThroughTheGate_AndTurretOnItKillsThemForCoins()
    {
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f);

        // 自軍ユニットを全滅させ、タレットだけで戦わせる
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        yield return null;

        // 拠点を防壁で囲み、北側の1マスだけ門にして、その上にタレットを置く
        GameObject systems = GameObject.Find("GameSystems");
        systems.SendMessage("AddCoins", 19 * 15 + 30 + 150);
        systems.SendMessage("SelectOption", (object)0);
        foreach (Vector3 cell in RingAroundBase())
        {
            if (cell != GateCell) systems.SendMessage("TryPlaceAtWorld", cell);
        }
        systems.SendMessage("SelectOption", (object)1);
        systems.SendMessage("TryPlaceAtWorld", GateCell);
        systems.SendMessage("SelectOption", (object)2);
        systems.SendMessage("TryPlaceAtWorld", GateCell);
        systems.SendMessage("SelectOption", (object)-1);

        Assert.AreEqual(19, CountNamed("Wall(Clone)"));
        Assert.AreEqual(1, CountNamed("Gate(Clone)"));
        Assert.AreEqual(1, CountNamed("Turret(Clone)"));
        Assert.IsNotNull(Named("Turret(Clone)")[0].GetComponentInChildren<Animator>(), "タレットにモデルの Animator がない");
        yield return null;
        Assert.AreEqual(0, Coins(), "ちょうど使い切っているはず");

        // 入口は門だけなので、拠点が傷つけば敵が門を通り抜けたことになる。
        // コインが増えれば、タレットが敵を倒したことになる
        Time.timeScale = 10f;
        float deadline = Time.realtimeSinceStartup + 120f;
        while (Coins() == 0 || BaseHp() >= 1000)
        {
            Assert.IsNull(GameObject.Find("GameOverPanel"), "確認が済む前に拠点が破壊された");
            Assert.Less(Time.realtimeSinceStartup, deadline,
                $"タイムアウト (コイン {Coins()}, 拠点HP {BaseHp()}): 敵が門を通れていないか、タレットが敵を倒せていない");
            yield return null;
        }
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator BossWave_SpawnsOneSlowTankyBoss_WithNameAndHealthBarAtTop()
    {
        // インスペクターのボス設定を読み、間隔だけ 1 にして最初のウェーブをボスウェーブにする
        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        object boss = Field(spawner, "boss");
        boss.GetType().GetField("waveInterval").SetValue(boss, 1);
        string bossName = (string)Field(boss, "bossName");
        float hpMultiplier = (float)Field(boss, "hpMultiplier");
        float speedMultiplier = (float)Field(boss, "speedMultiplier");

        Assert.IsNull(GameObject.Find("BossPanel"), "ボスがいないのに体力バーが出ている");

        Time.timeScale = 10f;
        float deadline = Time.realtimeSinceStartup + 60f;
        while (CountAgents(IsEnemyUnit) == 0)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "ボスウェーブが始まらない");
            yield return null;
        }
        Time.timeScale = 1f;
        yield return null;

        NavMeshAgent[] enemies = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
            .Where(a => a.enabled && IsEnemyUnit(a.name)).ToArray();
        Assert.AreEqual(1, enemies.Length, "ボスウェーブではボスが1体だけ出る");
        NavMeshAgent bossAgent = enemies[0];

        // ステージ1なので敵全体の補正は掛かっておらず、元の敵の能力 x 倍率になる
        object data = Field(ComponentOfType(bossAgent.gameObject, "RogueRTS.Unit"), "data");
        float baseSpeed = (float)Field(data, "moveSpeed");
        float baseHp = (float)Field(data, "maxHp");
        Assert.AreEqual(baseSpeed * speedMultiplier, bossAgent.speed, 0.01f, "移動速度に倍率が掛かっていない");

        int maxHp = Mathf.CeilToInt(baseHp * hpMultiplier);
        Assert.IsNotNull(GameObject.Find("BossPanel"), "画面上部にボスの体力バーが出ない");
        Assert.AreEqual(bossName, GameObject.Find("BossNameText").GetComponent<TMP_Text>().text, "ボスの名前が設定と違う");
        Assert.AreEqual($"{maxHp} / {maxHp}", GameObject.Find("BossHpText").GetComponent<TMP_Text>().text, "体力に倍率が掛かっていない");

        // 拠点へ向かって歩いている間は歩行モーション
        Animator animator = bossAgent.GetComponentInChildren<Animator>();
        Assert.IsNotNull(animator, "ボスにモデルの Animator がない");
        yield return new WaitForSeconds(0.5f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Walk"), "移動中に歩行モーションになっていない");

        bossAgent.SendMessage("TakeDamage", maxHp * 0.25f);
        yield return null;
        Assert.AreEqual(0.75f, GameObject.Find("BossHpFill").GetComponent<RectTransform>().anchorMax.x, 0.01f,
            "ダメージを受けてもバーが縮まない");

        int coinsBefore = Coins();
        GameObject bossObject = bossAgent.gameObject;
        bossAgent.SendMessage("TakeDamage", 1e9f);
        yield return new WaitForSeconds(0.3f);
        Assert.IsNull(GameObject.Find("BossPanel"), "ボスを倒しても体力バーが消えない");
        Assert.Greater(Coins(), coinsBefore, "ボスを倒してもコインが入らない");

        // 倒れるモーションを見せてから消える
        Assert.IsTrue(bossObject != null, "倒れるモーションを見せる前に消えた");
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Die"), "撃破時に倒れるモーションになっていない");
        Assert.IsFalse(bossAgent.enabled, "倒れている間も移動しようとしている");

        Time.timeScale = 4f;
        deadline = Time.realtimeSinceStartup + 30f;
        while (bossObject != null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "倒れたボスがいつまでも消えない");
            yield return null;
        }
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator Boss_WalksToBase_AndAxeDamageLandsAtTheImpactOfTheSwing()
    {
        // ボスだけにするため、自軍ユニットを全滅させ、最初のウェーブをボスにする
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        object boss = Field(spawner, "boss");
        boss.GetType().GetField("waveInterval").SetValue(boss, 1);
        float damageMultiplier = (float)Field(boss, "attackDamageMultiplier");

        // 振り始めを見逃さないよう、倍速は控えめにする
        Time.timeScale = 5f;
        float deadline = Time.realtimeSinceStartup + 120f;
        Animator animator = null;
        while (animator == null || !animator.GetCurrentAnimatorStateInfo(0).IsName("Attack"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "ボスが拠点に着いて斧を振らない");
            NavMeshAgent first = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                .FirstOrDefault(a => IsEnemyUnit(a.name));
            if (first != null && animator == null) animator = first.GetComponentInChildren<Animator>();
            yield return null;
        }
        Time.timeScale = 1f;

        // 振りかぶっている間はまだ当たらず、振り下ろした瞬間にダメージが入る
        int hpAtSwingStart = BaseHp();
        yield return new WaitForSeconds(0.6f);
        Assert.AreEqual(hpAtSwingStart, BaseHp(), "振りかぶっている途中でダメージが入った");

        yield return new WaitForSeconds(1.2f);
        object data = Field(ComponentOfType(animator.GetComponentInParent<NavMeshAgent>().gameObject, "RogueRTS.Unit"), "data");
        float damage = (float)Field(data, "attackDamage") * damageMultiplier;
        Assert.AreEqual(hpAtSwingStart - Mathf.RoundToInt(damage), BaseHp(), "振り下ろしのダメージが1回分入っていない");
    }

    [UnityTest]
    public IEnumerator Soldier_WhenHit_PlaysHurtAndIsStunnedForAMoment_ThenNotAgainRightAway()
    {
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f); // 集合地点に着いて待機するのを待つ
        Time.timeScale = 1f;

        NavMeshAgent soldier = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
            .First(a => a.name.StartsWith("Soldier"));
        Animator animator = soldier.GetComponentInChildren<Animator>();
        Assert.IsNotNull(animator, "兵士にモデルの Animator がない");

        soldier.SendMessage("TakeDamage", 1f);
        soldier.SendMessage("MoveTo", soldier.transform.position + new Vector3(8f, 0f, 0f));
        yield return new WaitForSeconds(0.1f);
        AnimatorStateInfo now = animator.GetCurrentAnimatorStateInfo(0);
        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
        Assert.IsTrue(now.IsName("Hurt") || next.IsName("Hurt"), "被弾しても怯むモーションにならない");
        Assert.IsTrue(soldier.isStopped, "硬直中なのに移動命令で動いた");

        yield return new WaitForSeconds(0.4f);
        Assert.IsFalse(soldier.isStopped, "0.2 秒を過ぎても硬直が解けない");
        Assert.Greater(soldier.velocity.magnitude, 0.5f, "硬直が解けたあと移動命令どおりに動かない");

        // 怯んだ直後は、もう一度殴られても止まらない
        soldier.SendMessage("TakeDamage", 1f);
        yield return null;
        Assert.IsFalse(soldier.isStopped, "怯んだ直後に続けて怯んだ");
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator Spearman_ProducedWithKey3_IdlesAndWalks_AndThrustsFromBeyondASoldiersReach()
    {
        // 槍兵だけで戦わせるため、初期の自軍ユニットを全滅させる
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        GameObject.Find("GameSystems").SendMessage("AddGold", 1000);
        yield return null;

        Assert.AreEqual("槍兵 [3]", ProduceButtonCaption(2), "3 番目の生産ボタンが槍兵になっていない");
        StringAssert.StartsWith("120", ProduceButtonText(2, "Face/Cost"), "槍兵のコストが 120 になっていない");

        yield return PressKey(Key.Digit3);
        Time.timeScale = 4f;
        float deadline = Time.realtimeSinceStartup + 30f;
        NavMeshAgent spearman = null;
        while (spearman == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "3 キーで槍兵が生産されない");
            spearman = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                .FirstOrDefault(a => a.enabled && a.name.StartsWith("Spearman"));
            yield return null;
        }

        // 剣士 (兵士) の 1.5 倍の体力とリーチ
        object data = Field(ComponentOfType(spearman.gameObject, "RogueRTS.Unit"), "data");
        var producible = (Component[])Field(ComponentOfType(GameObject.Find("PlayerBase"), "RogueRTS.PlayerBase"), "producibleUnits");
        object soldierData = Field(producible[0], "data");
        Assert.AreEqual((float)Field(soldierData, "maxHp") * 1.5f, (float)Field(data, "maxHp"), 0.01f, "体力が兵士の 1.5 倍でない");
        Assert.AreEqual((float)Field(soldierData, "attackRange") * 1.5f, (float)Field(data, "attackRange"), 0.01f, "リーチが兵士の 1.5 倍でない");

        // 集合地点へ歩き、着いたら待機モーションを再生する
        Animator animator = spearman.GetComponentInChildren<Animator>();
        Assert.IsNotNull(animator, "槍兵にモデルの Animator がない");
        Time.timeScale = 1f;
        yield return new WaitForSeconds(0.3f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Walk"), "移動中に歩行モーションになっていない");
        while (animator.IsInTransition(0)) yield return null;
        Assert.Less(SpearGripOffset(spearman.gameObject), 0.15f, "歩行中に槍が手から離れている");

        Time.timeScale = 4f;
        deadline = Time.realtimeSinceStartup + 30f;
        while (!animator.GetCurrentAnimatorStateInfo(0).IsName("Idle"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "集合地点に着いても待機モーションにならない");
            yield return null;
        }
        Time.timeScale = 1f;
        StringAssert.EndsWith("idle", animator.GetCurrentAnimatorClipInfo(0)[0].clip.name, "待機中に待機モーションを使っていない");
        float idleTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        yield return new WaitForSeconds(0.3f);
        Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, idleTime, "待機モーションが止まっている");

        // 正面から敵兵を向かわせ、兵士の届かない距離から突き始めるかを見る
        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        var gruntPrefab = (Component)Field(spawner, "meleePrefab");
        Component grunt = Object.Instantiate(gruntPrefab, spearman.transform.position + new Vector3(0f, 0f, 8f), Quaternion.Euler(0f, 180f, 0f));
        deadline = Time.realtimeSinceStartup + 20f;
        while (!animator.GetCurrentAnimatorStateInfo(0).IsName("Attack") && !animator.GetNextAnimatorStateInfo(0).IsName("Attack"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "敵兵が近づいても突かない");
            yield return null;
        }

        // 兵士のリーチ 1.5 + 相手の半径 0.5 より遠い
        float distance = Vector3.Distance(spearman.transform.position, grunt.transform.position);
        Assert.Greater(distance, 2.2f, $"兵士でも届く距離 ({distance:0.00}) まで近づいてから突いた");

        // 穂先が届く瞬間にダメージが入る
        float hpAtThrustStart = CurrentHp(grunt.gameObject);
        yield return new WaitForSeconds(0.2f);
        Assert.AreEqual(hpAtThrustStart, CurrentHp(grunt.gameObject), "突き出す前にダメージが入った");
        Assert.Less(SpearGripOffset(spearman.gameObject), 0.15f, "突きの最中に槍が手から離れている");
        yield return new WaitForSeconds(0.5f);
        Assert.Less(CurrentHp(grunt.gameObject), hpAtThrustStart, "突いてもダメージが入らない");
    }

    [UnityTest, Timeout(300000)]
    public IEnumerator Boss_SweepsWhenSeveralUnitsAreInFront_AndHitsThemAllAtOnce()
    {
        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        object boss = Field(spawner, "boss");
        boss.GetType().GetField("waveInterval").SetValue(boss, 1);
        // 確かめ終わる前に自軍に倒されないよう、このテストでは体力を多めにする
        boss.GetType().GetField("hpMultiplier").SetValue(boss, 50f);
        // 次のウェーブのボスが加わって兵士がばらけないよう、出てくるボスは最初の 1 体だけにする
        spawner.GetType().GetField("timeBetweenWaves").SetValue(spawner, 9999f);

        // ボスが自軍ユニットに近づくまで早送りし、そこからは等速で薙ぎ払いを待つ
        Time.timeScale = 5f;
        float deadline = Time.realtimeSinceStartup + 120f;
        NavMeshAgent bossAgent = null;
        while (true)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "ボスが自軍ユニットのところまで来ない");
            bossAgent = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                .FirstOrDefault(a => a.enabled && a.name.StartsWith("EnemyBoss"));
            if (bossAgent != null && Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                    .Any(a => a.enabled && IsPlayerUnit(a.name) && Vector3.Distance(a.transform.position, bossAgent.transform.position) < 12f))
                break;
            yield return null;
        }
        Time.timeScale = 1f;

        Animator animator = bossAgent.GetComponentInChildren<Animator>();
        while (!animator.GetCurrentAnimatorStateInfo(0).IsName("Sweep") && !animator.GetNextAnimatorStateInfo(0).IsName("Sweep"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "自軍ユニットに囲まれても薙ぎ払わない");
            Assert.IsTrue(bossAgent.enabled, "薙ぎ払う前にボスが倒された");
            yield return null;
        }

        // 薙ぎ払いが当たる前の体力を控え、当たったあとに何体減ったかを数える (敵はボスだけ)
        GameObject[] units = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
            .Where(a => a.enabled && IsPlayerUnit(a.name)).Select(a => a.gameObject).ToArray();
        float[] before = units.Select(CurrentHp).ToArray();
        yield return new WaitForSeconds(0.9f);
        int hit = units.Where((u, i) => u == null || CurrentHp(u) < before[i]).Count();
        Assert.GreaterOrEqual(hit, 2, "薙ぎ払いで複数のユニットにまとめて当たっていない");
    }

    [UnityTest]
    public IEnumerator SpaceKey_PausesTheGame_ShowsOnlyABarAtTheTop_CameraStillMoves_AndPressingItAgainResumes()
    {
        yield return null;
        Assert.IsNull(GameObject.Find("PausePanel"), "最初から一時停止の表示が出ている");

        yield return PressKey(Key.Space);
        Assert.AreEqual(0f, Time.timeScale, "スペースキーで止まらない");
        GameObject pause = GameObject.Find("PausePanel");
        Assert.IsNotNull(pause, "一時停止の表示が出ない");

        // 画面全体は暗くせず、上部に半透明の灰色の帯と文字だけを出す
        var corners = new Vector3[4];
        ((RectTransform)pause.transform).GetWorldCorners(corners);
        Assert.Greater(corners[0].y, Screen.height * 0.7f, "一時停止の表示が画面の上部にない");
        Assert.Less(corners[2].x - corners[0].x, Screen.width * 0.5f, "一時停止の表示が画面全体を覆っている");
        Color band = pause.GetComponent<Image>().color;
        Assert.Less(band.a, 1f, "帯が半透明でない");
        Assert.AreEqual(band.r, band.g, 0.01f, "帯が灰色でない");
        Assert.AreEqual(band.r, band.b, 0.01f, "帯が灰色でない");
        Assert.AreEqual("一時停止中", pause.GetComponentInChildren<TMP_Text>().text);

        // 止まっている間は、次のウェーブまでの残り時間も減らない
        string frozen = GameObject.Find("WaveText").GetComponent<TMP_Text>().text;
        yield return new WaitForSecondsRealtime(1.2f);
        Assert.AreEqual(frozen, GameObject.Find("WaveText").GetComponent<TMP_Text>().text, "一時停止中に時間が進んだ");

        // 止まっていてもカメラは動かせる
        Transform cam = Camera.main.transform;
        Vector3 before = cam.position;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
        yield return new WaitForSecondsRealtime(0.4f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        Assert.Greater(cam.position.z, before.z + 2f, "一時停止中にカメラが動かない");

        yield return PressKey(Key.Space);
        Assert.AreEqual(NormalTimeScale, Time.timeScale, "もう一度スペースキーを押しても再開しない");
        Assert.IsNull(GameObject.Find("PausePanel"), "再開しても一時停止の表示が消えない");
    }

    [UnityTest]
    public IEnumerator SpeedButtons_AboveProduction_StartAtNormalSpeed_AndSwitchToDoubleAndTriple()
    {
        yield return null;

        // 速度パネルは生産パネルのすぐ上にあり、横方向は重なっている
        Vector3[] speed = new Vector3[4], production = new Vector3[4];
        ((RectTransform)GameObject.Find("SpeedPanel").transform).GetWorldCorners(speed);
        ((RectTransform)GameObject.Find("ProductionPanel").transform).GetWorldCorners(production);
        // ぴったり接していると、画面の大きさによって計算誤差で下端がわずかに下回ることがある
        Assert.GreaterOrEqual(speed[0].y, production[1].y - 0.01f, "速度ボタンが生産パネルの上にない");
        Assert.Less(speed[0].y - production[1].y, 40f, "速度ボタンが生産パネルから離れすぎている");
        Assert.Less(speed[0].x, production[2].x, "速度ボタンが生産パネルの真上にない");
        Assert.Greater(speed[2].x, production[0].x, "速度ボタンが生産パネルの真上にない");

        // 速度ボタンは ▶ の数で速さを表す絵のボタンで、文字は重ねない
        Image[] icons = Enumerable.Range(0, 3).Select(i => GameObject.Find($"SpeedButton{i}").GetComponent<Image>()).ToArray();
        Assert.IsTrue(Enumerable.Range(0, 3).All(i => GameObject.Find($"SpeedButton{i}").transform.Find("Label").GetComponent<TMP_Text>().text == ""),
            "速度ボタンの絵に文字が重なっている");
        Color yellow = new Color(1f, 0.9f, 0.35f);

        // 始めは等速 (これまでの半分の速さ) で、等速のボタンが黄色
        Assert.AreEqual(NormalTimeScale, Time.timeScale, "始めの速さが等速でない");
        Assert.AreEqual(yellow, icons[0].color, "等速のボタンが黄色になっていない");
        Assert.AreEqual(Color.white, icons[2].color);

        Button Speed(int i) => GameObject.Find($"SpeedButton{i}").GetComponent<Button>();
        Speed(2).onClick.Invoke();
        yield return null;
        Assert.AreEqual(NormalTimeScale * 3f, Time.timeScale, "3倍にならない");
        Assert.AreEqual(yellow, icons[2].color, "3倍のボタンが黄色になっていない");
        Assert.IsTrue(GameObject.Find("SpeedButton2").GetComponentsInChildren<Image>().All(image => image.color == yellow),
            "3倍のボタンの ▶ が全部黄色になっていない");
        Assert.AreEqual(Color.white, icons[0].color, "等速のボタンが黄色のまま");

        Speed(1).onClick.Invoke();
        yield return null;
        Assert.AreEqual(NormalTimeScale * 2f, Time.timeScale, "2倍にならない");

        // 一時停止中は止まったままで、再開すると選んだ速さで動く
        yield return PressKey(Key.Space);
        Assert.AreEqual(0f, Time.timeScale);
        yield return PressKey(Key.Space);
        Assert.AreEqual(NormalTimeScale * 2f, Time.timeScale, "再開すると選んだ速さに戻らない");

        // 一時停止ボタンは、縦棒のどちらを押しても一時停止と再開を切り替え、止まっている間は黄色になる
        Transform stop = GameObject.Find("StopButton").transform;
        Button[] stopParts = stop.GetComponentsInChildren<Button>();
        Assert.IsTrue(stop.GetComponentsInChildren<Image>().All(image => image.color == Color.white), "動いているのに一時停止ボタンが黄色");
        stopParts[0].onClick.Invoke();
        yield return null;
        Assert.AreEqual(0f, Time.timeScale, "一時停止ボタンで止まらない");
        Assert.IsTrue(stop.GetComponentsInChildren<Image>().All(image => image.color == yellow), "一時停止中なのに一時停止ボタンが黄色でない");
        Assert.AreEqual(yellow, icons[1].color, "一時停止中に、再開したときの速さのボタンが選ばれて見えない");
        stopParts[stopParts.Length - 1].onClick.Invoke();
        yield return null;
        Assert.AreEqual(NormalTimeScale * 2f, Time.timeScale, "一時停止ボタンをもう一度押しても再開しない");
        Assert.AreEqual(Color.white, stop.GetComponent<Image>().color, "再開したのに一時停止ボタンが黄色のまま");

        yield return PressKey(Key.Space);
        Speed(2).onClick.Invoke();
        yield return null;
        Assert.AreEqual(0f, Time.timeScale, "一時停止中に速さを変えると動き出した");
        yield return PressKey(Key.Space);
        Assert.AreEqual(NormalTimeScale * 3f, Time.timeScale, "一時停止中に選んだ速さで再開しない");

        Speed(0).onClick.Invoke();
        yield return null;
        Assert.AreEqual(NormalTimeScale, Time.timeScale, "等速に戻らない");
    }

    [UnityTest]
    public IEnumerator CtrlMouse_TurnsTheCamera_WasdMovesThatWay_GReturnsToTheDefaultCamera()
    {
        yield return null;
        Transform cam = Camera.main.transform;
        Quaternion defaultRotation = cam.rotation;
        float defaultHeight = cam.position.y;

        // Ctrl を押していなければ、マウスを動かしても向きは変わらない
        InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(40f, 0f) });
        yield return null;
        Assert.Less(Quaternion.Angle(defaultRotation, cam.rotation), 0.01f, "Ctrl を押さずに向きが変わった");

        // 左へ振り向きつつ、少し見上げる
        yield return CtrlLook(new Vector2(-30f, 10f), 12);
        Assert.AreEqual("Free", CameraMode(), "Ctrl+マウスでフリーのカメラにならない");
        Assert.Greater(Quaternion.Angle(defaultRotation, cam.rotation), 30f, "Ctrl+マウスで向きが変わらない");
        Assert.Less(cam.forward.x, -0.3f, "マウスを左へ動かしたのに左を向かない");

        Vector3 before = cam.position;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
        yield return new WaitForSecondsRealtime(0.4f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        Vector3 moved = cam.position - before;
        Assert.Greater(Vector3.Dot(moved, cam.forward), 3f, "W で向いている方へ進まない");
        Assert.Less(Vector3.Angle(moved, cam.forward), 3f, "W で向いている方からそれて進んだ");
        StringAssert.Contains("フリーカメラ", GameObject.Find("CameraHintText").GetComponent<TMP_Text>().text);

        // G で元の見下ろし型のカメラに戻る (ズームの距離もそのまま)
        yield return PressKey(Key.G);
        yield return new WaitForSecondsRealtime(1f);
        Assert.AreEqual("Rts", CameraMode(), "G で通常のカメラに戻らない");
        Assert.Less(Quaternion.Angle(defaultRotation, cam.rotation), 0.1f, "G で元の向きに戻らない");
        Assert.AreEqual(defaultHeight, cam.position.y, 0.05f, "G で元の高さに戻らない");
        Assert.AreEqual("", GameObject.Find("CameraHintText").GetComponent<TMP_Text>().text);

        // 戻ったあとは、これまで通り WASD で水平に動く
        before = cam.position;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
        yield return new WaitForSecondsRealtime(0.4f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        Assert.Greater(cam.position.z, before.z + 2f, "通常のカメラに戻ったあと W で奥へ動かない");
        Assert.AreEqual(before.y, cam.position.y, 0.01f, "通常のカメラに戻ったあと、移動で高さが変わった");
    }

    [UnityTest]
    public IEnumerator FHeld_SlowsTime_ReleasingOverAnAlly_FliesCameraBehindIt_CtrlMouseCirclesIt_WasdLetsGo()
    {
        yield return new WaitForSeconds(0.5f);
        Transform cam = Camera.main.transform;

        yield return SendMouse(OnScreen(FirstAlly()));
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F));
        yield return null;
        yield return null;
        Assert.AreEqual(NormalTimeScale * 0.25f, Time.timeScale, 1e-4f, "F を押している間スローモーションにならない");
        var candidate = (Component)Property(FocusController(), "Candidate");
        Assert.IsNotNull(candidate, "カーソルを合わせた味方が候補にならない");
        GameObject target = candidate.gameObject;
        string name = (string)Field(Field(candidate, "data"), "displayName");
        GameObject marker = GameObject.Find("FocusMarker");
        Assert.IsNotNull(marker, "候補のユニットの頭上に印が出ない");
        StringAssert.Contains(name, marker.GetComponent<TMP_Text>().text);
        Assert.AreEqual("Rts", CameraMode(), "F を離す前にカメラが移った");

        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        yield return null;
        Assert.AreEqual(NormalTimeScale, Time.timeScale, "F を離してもスローモーションが解けない");
        Assert.IsNull(GameObject.Find("FocusMarker"), "F を離しても印が消えない");
        Assert.AreEqual("Follow", CameraMode(), "F を離してもユニットの視点に移らない");

        // 飛び終わると、ユニットの後ろの近くから見ている (三人称視点)
        yield return new WaitForSecondsRealtime(1f);
        AssertThirdPerson(cam, target, "飛んだあと");
        Assert.Less(Vector3.Angle(Flat(cam.forward), Flat(target.transform.forward)), 90f, "ユニットの後ろから見ていない");
        StringAssert.Contains($"{name} の視点", GameObject.Find("CameraHintText").GetComponent<TMP_Text>().text);

        // Ctrl+マウスでユニットの周りを回り込む。ユニットからは外れない
        Vector3 around = Flat(cam.position - target.transform.position);
        yield return CtrlLook(new Vector2(30f, 0f), 12);
        Assert.AreEqual("Follow", CameraMode(), "向きを変えただけでユニットから外れた");
        Assert.Greater(Vector3.Angle(around, Flat(cam.position - target.transform.position)), 25f,
            "Ctrl+マウスで向きが変わらない");
        AssertThirdPerson(cam, target, "向きを変えたあと");

        // WASD を押すとユニットから外れて、向いている方へ進む
        Vector3 before = cam.position;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
        yield return new WaitForSecondsRealtime(0.4f);
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
        Assert.AreEqual("Free", CameraMode(), "WASD を押してもユニットから外れない");
        Assert.Greater(Vector3.Dot(cam.position - before, cam.forward), 3f, "外れたあと、向いている方へ進まない");
    }

    [UnityTest]
    public IEnumerator FocusWorksWhilePaused_AndTheCameraLetsGoWhenTheFollowedAllyDies()
    {
        yield return new WaitForSeconds(0.5f);
        Transform cam = Camera.main.transform;
        yield return PressKey(Key.Space);
        Assert.AreEqual(0f, Time.timeScale);

        // 止まっている間も、止まったまま視点を移せる
        yield return SendMouse(OnScreen(FirstAlly()));
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F));
        yield return null;
        yield return null;
        Assert.AreEqual(0f, Time.timeScale, "一時停止中に F を押したら動き出した");
        var candidate = (Component)Property(FocusController(), "Candidate");
        Assert.IsNotNull(candidate, "一時停止中に視点を移すユニットを選べない");
        GameObject target = candidate.gameObject;

        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return new WaitForSecondsRealtime(1f);
        Assert.AreEqual(0f, Time.timeScale, "F を離したら動き出した");
        AssertThirdPerson(cam, target, "一時停止中に飛んだあと");

        yield return PressKey(Key.Space);
        Assert.AreEqual(NormalTimeScale, Time.timeScale);

        // 追いかけているユニットが倒れると、カメラはその場で外れる
        target.SendMessage("TakeDamage", 1e9f);
        yield return null;
        Assert.AreEqual("Free", CameraMode(), "ユニットが倒れてもカメラが外れない");
        Vector3 detached = cam.position;
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.Less(Vector3.Distance(detached, cam.position), 0.01f, "外れたあともカメラが動いている");
        StringAssert.Contains("フリーカメラ", GameObject.Find("CameraHintText").GetComponent<TMP_Text>().text);

        yield return PressKey(Key.G);
        yield return new WaitForSecondsRealtime(1f);
        Assert.AreEqual("Rts", CameraMode(), "G で通常のカメラに戻らない");
        Assert.Less(Quaternion.Angle(Quaternion.Euler(55f, 0f, 0f), cam.rotation), 0.1f, "G で見下ろし型の向きに戻らない");
    }

    [UnityTest]
    public IEnumerator MeleeKnockback_PushesAwayInProportionToDamage_ButNotTheHeavyKnight()
    {
        // 押し戻された先で味方とぶつかると、重なりを分け合って押し戻しが短くなる。
        // 距離を正しく測るため、兵士を 1 体だけ残して初期の自軍ユニットを倒しておく
        NavMeshAgent soldier = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
            .First(a => a.name.StartsWith("Soldier"));
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (agent != soldier && IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }

        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f); // 集合地点に着いて待機するのを待つ
        Time.timeScale = 1f;
        Assert.AreEqual(1, CountAgents(IsPlayerUnit), "兵士のほかに自軍ユニットが残っている");

        Component unit = ComponentOfType(soldier.gameObject, "RogueRTS.Unit");
        float perDamage = (float)Field(unit, "knockbackPerDamage");
        Assert.Greater(perDamage, 0f, "兵士が押し戻されない設定になっている");

        // 北側から殴られたことにして、南へ押し戻されるかを見る
        foreach (float damage in new[] { 8f, 20f })
        {
            // 目的地へ戻ろうとして結果がぶれないよう、その場で待機させてから押し戻す
            soldier.SendMessage("MoveTo", soldier.transform.position);
            yield return new WaitForSeconds(0.3f);

            Vector3 start = soldier.transform.position;
            unit.GetType().GetMethod("Knockback").Invoke(unit, new object[] { start + new Vector3(0f, 0f, 2f), damage });
            yield return new WaitForSeconds(0.25f);

            Vector3 moved = soldier.transform.position - start;
            Assert.AreEqual(damage * perDamage, moved.magnitude, 0.15f, $"ダメージ {damage} に比例した距離だけ押し戻されていない");
            Assert.Less(moved.z, 0f, "殴られた方向と反対へ押し戻されていない");
        }

        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        object bossPrefab = Field(Field(spawner, "boss"), "bossPrefab");
        Assert.AreEqual(0f, (float)Field(bossPrefab, "knockbackPerDamage"), "heavy knight が押し戻される設定になっている");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator Archer_ReleasesAnArrow_ThatFliesToTheEnemy_AndOnlyThenDoesDamage()
    {
        NavMeshAgent archer = KeepOnlyPlayerUnit("Archer");
        archer.Warp(new Vector3(0f, 0f, -18f));
        GameObject enemy = SpawnEnemy(new Vector3(0f, 0f, -2f));
        Time.timeScale = 1f;
        yield return null;
        float hp = CurrentHp(enemy);

        Transform arrow = null;
        float deadline = Time.realtimeSinceStartup + 15f;
        while (arrow == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "弓兵が矢を放たない");
            Assert.AreEqual(hp, CurrentHp(enemy), "矢を放つ前にダメージが入った");
            yield return null;
            arrow = Named("Arrow(Clone)").FirstOrDefault();
        }

        Assert.Less(Vector3.Distance(arrow.position, archer.transform.position), 3f, "矢が弓兵の手元から出ていない");
        Assert.Less(Vector3.Angle(Flat(arrow.forward), Flat(enemy.transform.position - arrow.position)), 15f, "矢が敵の方を向いていない");
        Assert.IsNull(archer.GetComponentInChildren<LineRenderer>(), "矢を飛ばすようになったのに線が残っている");

        // 飛んでいる間はまだ当たらず、山なりに進み、敵に届いて消えるときにダメージが入る
        float launched = Time.time, launchHeight = arrow.position.y, peak = launchHeight;
        while (arrow != null)
        {
            if (StillFarFrom(arrow, enemy)) Assert.AreEqual(hp, CurrentHp(enemy), "矢が届く前にダメージが入った");
            peak = Mathf.Max(peak, arrow.position.y);
            Assert.Less(Time.time - launched, 3f, "矢が届かない");
            yield return null;
        }
        Assert.Greater(Time.time - launched, 0.2f, "飛ぶ間もなく当たった");
        Assert.Greater(peak, launchHeight, "矢が山なりに飛んでいない");

        float damage = (float)Field(Field(ComponentOfType(archer.gameObject, "RogueRTS.Unit"), "data"), "attackDamage");
        Assert.AreEqual(hp - damage, CurrentHp(enemy), 0.01f, "矢のダメージが 1 回分入っていない");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator ArrowThatMisses_SticksInTheGround_StaysForAWhile_ThenDisappears()
    {
        NavMeshAgent archer = KeepOnlyPlayerUnit("Archer");
        archer.Warp(new Vector3(0f, 0f, -18f));
        GameObject enemy = SpawnEnemy(new Vector3(0f, 0f, -2f));
        Time.timeScale = 1f;

        Transform arrow = null;
        float deadline = Time.realtimeSinceStartup + 15f;
        while (arrow == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "弓兵が矢を放たない");
            yield return null;
            arrow = Named("Arrow(Clone)").FirstOrDefault();
        }

        // 狙っていた敵が飛んでいる間に倒れると、矢はそのまま地面まで飛ぶ
        enemy.SendMessage("TakeDamage", 1e9f);
        while (!IsStuck(arrow))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "矢が地面に刺さらない");
            yield return null;
        }

        Assert.AreEqual(0f, arrow.position.y, 0.2f, "矢の先端が地面の位置にない");
        Assert.Less(arrow.forward.y, 0f, "矢が地面へ向かう向きのまま刺さっていない");

        Vector3 stuckAt = arrow.position;
        float duration = (float)Field(ComponentOfType(arrow.gameObject, "RogueRTS.Arrow"), "stuckDuration");
        yield return new WaitForSeconds(duration - 0.5f);
        Assert.IsTrue(arrow != null, "矢がすぐに消えた");
        Assert.AreEqual(stuckAt, arrow.position, "刺さった矢が動いた");

        yield return new WaitForSeconds(1f);
        Assert.IsTrue(arrow == null, "時間がたっても矢が消えない");
    }

    [UnityTest]
    public IEnumerator ArrowThatHitsARockOrWall_SticksThere_WithoutPassingThrough()
    {
        KeepOnlyPlayerUnit("Archer");
        Time.timeScale = 1f;

        GameObject.Find("GameSystems").SendMessage("AddCoins", 15);
        GameObject.Find("GameSystems").SendMessage("SelectOption", (object)0);
        GameObject.Find("GameSystems").SendMessage("TryPlaceAtWorld", new Vector3(1f, 0f, -9f));
        GameObject.Find("GameSystems").SendMessage("SelectOption", (object)-1);
        yield return new WaitForFixedUpdate();

        var rock = new Vector3(11f, 1.2f, 2f);
        var wall = new Vector3(1f, 1.2f, -9f);
        foreach (Vector3 solid in new[] { rock, wall })
        {
            Transform arrow = LaunchArrow("Player", 10f, solid + new Vector3(-8f, 0.5f, 0f), solid);
            yield return new WaitForSeconds(0.6f);

            Assert.IsTrue(IsStuck(arrow), $"{solid} に向けた矢が刺さらない");
            Collider[] touching = Physics.OverlapSphere(arrow.position, 0.05f);
            Assert.IsTrue(touching.Any(c => c.name == "Rock" || c.name == "Model"), $"{solid} に向けた矢の先端が地形の中にない");
            Assert.Less(arrow.position.x, solid.x, "地形を貫通した");
        }
    }

    [UnityTest]
    public IEnumerator EnemyArrow_DamagesTheBaseAndSticksInIt_ButAPlayerArrowDoesNot()
    {
        // 敵の矢は途中の自軍ユニットに当たるので、拠点の前を空けておく
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        yield return null;
        var front = new Vector3(0f, 1.5f, -18f);
        var basePoint = new Vector3(0f, 1.2f, -28f);
        int hp = BaseHp();

        Transform playerArrow = LaunchArrow("Player", 50f, front, basePoint);
        Time.timeScale = 1f;
        yield return new WaitForSeconds(0.6f);
        Assert.IsTrue(IsStuck(playerArrow), "拠点に当たった矢が刺さらない");
        Assert.AreEqual(hp, BaseHp(), "自軍の矢で拠点が傷ついた");

        Transform enemyArrow = LaunchArrow("Enemy", 50f, front, basePoint);
        yield return new WaitForSeconds(0.6f);
        Assert.IsTrue(IsStuck(enemyArrow), "拠点に当たった矢が刺さらない");
        Assert.AreEqual(hp - 50, BaseHp(), "敵の矢のダメージが拠点に入っていない");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator Archer_DoesNotShootThroughARock_ButWalksAroundUntilItHasAClearShot()
    {
        // 岩 (11, 0, 2) を挟んで、射程内に敵を置く
        NavMeshAgent archer = KeepOnlyPlayerUnit("Archer");
        var start = new Vector3(6f, 0f, 2f);
        archer.Warp(start);
        GameObject enemy = SpawnEnemy(new Vector3(16f, 0f, 2f));
        Time.timeScale = 1f;
        yield return null;
        float hp = CurrentHp(enemy);

        float deadline = Time.realtimeSinceStartup + 40f;
        while (CurrentHp(enemy) >= hp)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "岩を回り込んで敵を撃てていない");
            if (Named("Arrow(Clone)").Length > 0)
                Assert.Greater(Vector3.Distance(archer.transform.position, start), 1f, "岩の陰から動かずに矢を放った");
            yield return null;
        }

        Assert.AreEqual(0, Named("Arrow(Clone)").Count(IsStuckInRock), "矢が岩に当たっている");
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator LongReachAttack_DoesNotPassThroughAWall_ButLandsOnceAroundIt()
    {
        // どちらも歩いて 2 秒では回り込めない長さの防壁を挟み、防壁より長いリーチを持たせた槍兵を置く
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        GameObject systems = GameObject.Find("GameSystems");
        systems.SendMessage("AddCoins", 15 * 10);
        systems.SendMessage("SelectOption", (object)0);
        for (int x = -9; x <= 9; x += 2) systems.SendMessage("TryPlaceAtWorld", new Vector3(x, 0f, -15f));
        systems.SendMessage("SelectOption", (object)-1);
        Assert.AreEqual(10, CountNamed("Wall(Clone)"));
        Time.timeScale = 1f;
        yield return new WaitForSeconds(1f); // NavMesh の更新を待つ

        Component playerBase = ComponentOfType(GameObject.Find("PlayerBase"), "RogueRTS.PlayerBase");
        var producible = (Component[])Field(playerBase, "producibleUnits");
        Component spearmanPrefab = producible.First(u => u.name == "Spearman");
        var spearman = (Component)Object.Instantiate((Object)spearmanPrefab, new Vector3(1f, 0f, -17f), Quaternion.identity);
        spearman.GetType().GetField("reachBonus").SetValue(spearman, 8f);
        GameObject enemy = SpawnEnemy(new Vector3(1f, 0f, -13f));
        yield return null;
        float hp = CurrentHp(enemy);

        yield return new WaitForSeconds(1.5f);
        Assert.AreEqual(hp, CurrentHp(enemy), "槍が防壁を貫通した");

        float deadline = Time.realtimeSinceStartup + 40f;
        while (CurrentHp(enemy) >= hp)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "防壁を回り込んでも槍が当たらない");
            yield return null;
        }
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator Turret_CannotHitAnEnemyBehindARock_ButShootsOneInTheOpen()
    {
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }

        // 岩 (11, 0, 2) の西側に砲台を置く
        GameObject systems = GameObject.Find("GameSystems");
        var cell = new Vector3(7f, 0f, 3f);
        systems.SendMessage("AddCoins", 15 + 150);
        systems.SendMessage("SelectOption", (object)0);
        systems.SendMessage("TryPlaceAtWorld", cell);
        systems.SendMessage("SelectOption", (object)2);
        systems.SendMessage("TryPlaceAtWorld", cell);
        systems.SendMessage("SelectOption", (object)-1);
        Assert.AreEqual(1, CountNamed("Turret(Clone)"));

        GameObject hidden = SpawnEnemy(new Vector3(15f, 0f, 3f));
        Time.timeScale = 4f;
        yield return null;
        float hiddenHp = CurrentHp(hidden);
        yield return new WaitForSeconds(6f);
        Assert.AreEqual(hiddenHp, CurrentHp(hidden), "岩の陰の敵に砲台の矢が当たった");

        GameObject open = SpawnEnemy(new Vector3(7f, 0f, -3f));
        yield return null;
        float openHp = CurrentHp(open);
        float deadline = Time.realtimeSinceStartup + 30f;
        while (CurrentHp(open) >= openHp)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "砲台が見通しのよい敵を撃たない");
            yield return null;
        }
        Assert.AreEqual(hiddenHp, CurrentHp(hidden), "岩の陰の敵に砲台の矢が当たった");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator Turret_FiresAnArrowOverTheNextWall_AndDamageLandsWhenItArrives()
    {
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }

        // 防壁を 3 マス並べて真ん中に砲台を置き、隣の防壁のすぐ向こうに敵を立たせる
        GameObject systems = GameObject.Find("GameSystems");
        systems.SendMessage("AddCoins", 3 * 15 + 150);
        systems.SendMessage("SelectOption", (object)0);
        foreach (float x in new[] { -1f, 1f, 3f }) systems.SendMessage("TryPlaceAtWorld", new Vector3(x, 0f, -7f));
        systems.SendMessage("SelectOption", (object)2);
        systems.SendMessage("TryPlaceAtWorld", new Vector3(1f, 0f, -7f));
        systems.SendMessage("SelectOption", (object)-1);
        Assert.AreEqual(1, CountNamed("Turret(Clone)"));
        Transform turret = Named("Turret(Clone)")[0];
        Assert.IsNull(turret.GetComponentInChildren<LineRenderer>(), "矢を飛ばすようになったのに線が残っている");

        GameObject enemy = SpawnEnemy(new Vector3(5.5f, 0f, -7f));
        Time.timeScale = 1f;
        yield return null;
        float hp = CurrentHp(enemy);

        Transform arrow = null;
        float deadline = Time.realtimeSinceStartup + 15f;
        while (arrow == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "砲台が矢を放たない");
            Assert.AreEqual(hp, CurrentHp(enemy), "矢を放つ前にダメージが入った");
            yield return null;
            arrow = Named("Arrow(Clone)").FirstOrDefault();
        }
        Assert.Less(Flat(arrow.position - turret.position).magnitude, 2.5f, "矢が砲台の先から出ていない");
        Assert.Greater(arrow.position.y, 2f, "矢が防壁より上から出ていない");

        // 隣の防壁に刺さらず飛び越えて、敵に届いたときにダメージが入る。刺さった矢は消えずに残るので時間切れになる
        float launched = Time.time;
        while (arrow != null)
        {
            if (StillFarFrom(arrow, enemy)) Assert.AreEqual(hp, CurrentHp(enemy), "矢が届く前にダメージが入った");
            Assert.Less(Time.time - launched, 2f, "矢が敵に届かない");
            yield return null;
        }

        float damage = (float)Field(Field(ComponentOfType(turret.gameObject, "RogueRTS.Turret"), "data"), "attackDamage");
        Assert.AreEqual(hp - damage, CurrentHp(enemy), 0.01f, "矢のダメージが 1 回分入っていない");
    }

    [UnityTest]
    public IEnumerator F1_HidesHudHealthBarsAndSelectionRing_ControlsStillWork_F1AgainBringsThemBack()
    {
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f); // 集合地点に着いて待機するのを待つ
        Time.timeScale = 1f;

        GameObject ally = FirstAlly();
        Canvas canvas = GameObject.Find("Canvas").GetComponent<Canvas>();
        Renderer[] Bars() => Named("HealthBar").SelectMany(bar => bar.GetComponentsInChildren<Renderer>()).ToArray();
        Assert.IsTrue(canvas.enabled, "始めから UI が隠れている");
        Assert.Greater(Bars().Length, 0, "ヘルスバーが見つからない");
        Assert.IsTrue(Bars().All(part => part.enabled), "始めからヘルスバーが隠れている");

        yield return PressKey(Key.F1);
        Assert.IsFalse(canvas.enabled, "F1 で HUD が隠れない");
        Assert.IsTrue(Bars().All(part => !part.enabled), "F1 でヘルスバーが隠れない");

        // 隠している間に出てきたユニットのヘルスバーも隠れている
        SpawnEnemy(new Vector3(0f, 0f, 10f));
        yield return null;
        yield return null;
        Assert.IsTrue(Bars().All(part => !part.enabled), "隠している間に出たユニットのヘルスバーが見えている");

        // 隠れたボタンの場所をクリックしても押されず、後ろのユニットを選択できる
        Vector2 hiddenButton = GameObject.Find("ProduceButton0").transform.position;
        yield return SendMouse(hiddenButton, MouseButton.Left);
        yield return SendMouse(hiddenButton);
        StringAssert.Contains("空", GameObject.Find("QueueText").GetComponent<TMP_Text>().text, "隠れたボタンが押された");

        Vector2 onAlly = OnScreen(ally);
        yield return SendMouse(onAlly, MouseButton.Left);
        yield return SendMouse(onAlly);
        Assert.AreEqual(1, SelectedUnits().Length, "UI を隠すとユニットを選択できない");
        Renderer ring = Named("SelectionRing")[0].GetComponent<Renderer>();
        Assert.IsFalse(ring.enabled, "F1 で選択リングが隠れない");

        // 移動命令も出せて、目印は出ない
        Vector2 ground = OnScreen(ally) + new Vector2(120f, 0f);
        yield return SendMouse(ground, MouseButton.Right);
        Transform marker = Named("MoveMarker").FirstOrDefault();
        Assert.IsNotNull(marker, "UI を隠すと移動命令を出せない");
        Assert.IsFalse(marker.GetComponent<Renderer>().enabled, "F1 で移動の目印が隠れない");
        yield return SendMouse(ground);

        yield return PressKey(Key.F1);
        Assert.IsTrue(canvas.enabled, "もう一度 F1 を押しても HUD が戻らない");
        Assert.IsTrue(Bars().All(part => part.enabled), "もう一度 F1 を押してもヘルスバーが戻らない");
        Assert.IsTrue(ring.enabled, "もう一度 F1 を押しても選択リングが戻らない");
    }

    [UnityTest]
    public IEnumerator F1_StillShowsTheGameOverScreen_WhichNeedsAnAnswer()
    {
        Canvas canvas = GameObject.Find("Canvas").GetComponent<Canvas>();
        yield return PressKey(Key.F1);
        Assert.IsFalse(canvas.enabled, "F1 で HUD が隠れない");

        GameObject.Find("PlayerBase").SendMessage("TakeDamage", 1e9f);
        yield return null;
        yield return null;
        Assert.IsNotNull(GameObject.Find("GameOverPanel"), "敗北画面が出ない");
        Assert.IsTrue(canvas.enabled, "UI を隠していると敗北画面が見えない");
        Assert.IsTrue(canvas.GetComponent<GraphicRaycaster>().enabled, "UI を隠していると敗北画面のボタンを押せない");
    }

    [UnityTest]
    public IEnumerator F2_ShowsDamageNumbers_GreenWhenDealt_RedWhenReceived_ThatFadeAway()
    {
        Time.timeScale = 1f;
        yield return null;
        GameObject enemy = SpawnEnemy(new Vector3(0f, 0f, 10f));
        GameObject ally = FirstAlly();
        yield return null;

        // 始めはオフ
        enemy.SendMessage("TakeDamage", 5f);
        yield return null;
        Assert.AreEqual(0, CountNamed("DamageNumber(Clone)"), "F2 を押す前からダメージが表示された");

        yield return PressKey(Key.F2);
        enemy.SendMessage("TakeDamage", 12f);
        ally.SendMessage("TakeDamage", 7f);
        yield return null;

        TMP_Text[] numbers = Named("DamageNumber(Clone)").Select(n => n.GetComponent<TMP_Text>()).ToArray();
        Assert.AreEqual(2, numbers.Length, "ダメージの数字が 2 つ出ていない");
        TMP_Text dealt = numbers.First(n => n.text == "12");
        TMP_Text received = numbers.First(n => n.text == "7");
        Assert.Greater(dealt.color.g, 0.8f, "与えたダメージが緑でない");
        Assert.Less(dealt.color.r, 0.5f, "与えたダメージが緑でない");
        Assert.Greater(received.color.r, 0.8f, "受けたダメージが赤でない");
        Assert.Less(received.color.g, 0.5f, "受けたダメージが赤でない");

        // ダメージが入った相手の近くに出る
        Canvas canvas = dealt.canvas;
        Vector2 shown = dealt.rectTransform.anchoredPosition * canvas.scaleFactor;
        Vector2 expected = Camera.main.WorldToScreenPoint(enemy.transform.position + Vector3.up * 2f);
        Assert.Less(Vector2.Distance(shown, expected), 120f, "数字が敵の近くに出ていない");

        // 撃破した一撃も表示する
        enemy.SendMessage("TakeDamage", 1000f);
        yield return null;
        Assert.IsTrue(Named("DamageNumber(Clone)").Any(n => n.GetComponent<TMP_Text>().text == "1000"), "撃破した一撃が表示されない");

        // 昇りながら薄れて消える
        Vector2 before = received.rectTransform.anchoredPosition;
        float alphaBefore = received.color.a;
        yield return new WaitForSecondsRealtime(0.8f);
        Assert.Greater(received.rectTransform.anchoredPosition.y, before.y, "数字が昇っていない");
        Assert.Less(received.color.a, alphaBefore, "数字が薄れていない");
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.AreEqual(0, CountNamed("DamageNumber(Clone)"), "時間がたっても数字が消えない");

        yield return PressKey(Key.F2);
        ally.SendMessage("TakeDamage", 7f);
        yield return null;
        Assert.AreEqual(0, CountNamed("DamageNumber(Clone)"), "もう一度 F2 を押しても表示が止まらない");
    }

    [UnityTest]
    public IEnumerator ProduceButton_ShowsIconAndName_HoverSwapsIconForCostAndGrowsSmoothly_LeavingRestoresIt()
    {
        yield return null;
        var button = (RectTransform)GameObject.Find("ProduceButton1").transform;
        Transform face = button.Find("Face");
        Image icon = face.Find("Icon").GetComponent<Image>();
        GameObject cost = face.Find("Cost").gameObject;

        Assert.AreEqual("弓兵 [2]", ProduceButtonCaption(1), "アイコンの下にユニット名が出ていない");
        Assert.IsTrue(icon.gameObject.activeInHierarchy, "アイコンが出ていない");
        Assert.AreEqual("archer", icon.sprite.name, "弓兵のアイコンになっていない");
        Assert.IsFalse(cost.activeSelf, "マウスを乗せていないのにコストが出ている");
        Assert.AreEqual(Vector3.one, face.localScale);

        // マウスを乗せると、アイコンがコストに替わって少しずつ拡大する
        Vector2 center = button.TransformPoint(button.rect.center);
        yield return SendMouse(center);
        Assert.IsFalse(icon.gameObject.activeSelf, "マウスを乗せてもアイコンが消えない");
        Assert.IsTrue(cost.activeSelf, "マウスを乗せてもコストが出ない");
        StringAssert.StartsWith("70", cost.GetComponent<TMP_Text>().text, "弓兵のコストが出ていない");
        Assert.AreEqual(Color.white, cost.GetComponent<TMP_Text>().color, "コストの文字が白でない");

        var scales = new List<float> { face.localScale.x };
        float until = Time.realtimeSinceStartup + 0.5f;
        while (Time.realtimeSinceStartup < until)
        {
            yield return null;
            scales.Add(face.localScale.x);
        }
        Assert.Greater(scales.Max(), 1.15f, "マウスを乗せても拡大しない");
        Assert.AreEqual(1.2f, scales.Last(), 0.01f, "拡大したあと落ち着かない");
        Assert.IsTrue(scales.Any(s => s > 1.02f && s < 1.18f), "一瞬で拡大していて、なめらかに大きくなっていない");

        // 外すと元のアイコンと大きさに戻る
        yield return SendMouse(center + new Vector2(0f, 400f));
        Assert.IsTrue(icon.gameObject.activeSelf, "マウスを外してもアイコンに戻らない");
        Assert.IsFalse(cost.activeSelf, "マウスを外してもコストが消えない");
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.AreEqual(1f, face.localScale.x, 0.01f, "マウスを外しても元の大きさに戻らない");
    }

    [UnityTest]
    public IEnumerator BuildButtons_ShowIconsAndNames_HoverSwapsIconForCost()
    {
        yield return null;
        string[] names = { "防壁 [Z]", "門 [X]", "タレット [C]" };
        string[] icons = { "wall", "gate", "turret" };
        string[] costs = { "15", "30", "150" };
        for (int i = 0; i < names.Length; i++)
        {
            Transform button = GameObject.Find($"BuildButton{i}").transform;
            Assert.AreEqual(names[i], ButtonCaption(button), $"{i + 1} 番目の建築ボタンの名前が違う");
            Assert.AreEqual(icons[i], button.Find("Face/Icon").GetComponent<Image>().sprite.name, $"{names[i]} のアイコンが違う");
            Assert.AreEqual(Color.clear, button.GetComponent<Image>().color, $"{names[i]} のボタンに背景が残っている");
        }

        var gate = (RectTransform)GameObject.Find("BuildButton1").transform;
        Vector2 center = gate.TransformPoint(gate.rect.center);
        yield return SendMouse(center);
        GameObject cost = gate.Find("Face/Cost").gameObject;
        Assert.IsTrue(cost.activeSelf, "建築ボタンにマウスを乗せてもコストが出ない");
        Assert.IsFalse(gate.Find("Face/Icon").gameObject.activeSelf, "建築ボタンにマウスを乗せてもアイコンが消えない");
        StringAssert.StartsWith(costs[1], cost.GetComponent<TMP_Text>().text, "門のコストが出ていない");
        Assert.AreEqual(Color.white, cost.GetComponent<TMP_Text>().color, "コストの文字が白でない");

        yield return SendMouse(center + new Vector2(0f, 400f));
        Assert.IsFalse(cost.activeSelf, "マウスを外してもコストが消えない");
    }

    /// <summary>生産ボタンの「ユニット名 [生産キー]」。</summary>
    static string ProduceButtonCaption(int index) => ButtonCaption(GameObject.Find($"ProduceButton{index}").transform);

    /// <summary>アイコン付きのボタンの「名前 [ショートカットキー]」。</summary>
    static string ButtonCaption(Transform button) =>
        $"{button.Find("Name").GetComponent<TMP_Text>().text} [{button.Find("Hotkey").GetComponent<TMP_Text>().text}]";

    static string ProduceButtonText(int index, string path) =>
        GameObject.Find($"ProduceButton{index}").transform.Find(path).GetComponent<TMP_Text>().text;

    /// <summary>自軍ユニットを、名前が prefix で始まる 1 体だけにして返す。</summary>
    static NavMeshAgent KeepOnlyPlayerUnit(string prefix)
    {
        NavMeshAgent kept = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
            .First(a => a.name.StartsWith(prefix));
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (agent != kept && IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        return kept;
    }

    /// <summary>命令を与えずに敵兵を置く。索敵範囲に相手がいなければその場で待機する。</summary>
    static GameObject SpawnEnemy(Vector3 position)
    {
        object prefab = Field(GameObject.Find("GameSystems").GetComponent("WaveSpawner"), "meleePrefab");
        return ((Component)Object.Instantiate((Object)prefab, position, Quaternion.identity)).gameObject;
    }

    /// <summary>弓兵の矢を from から aim へ放つ。team は放った陣営の名前。</summary>
    static Transform LaunchArrow(string team, float damage, Vector3 from, Vector3 aim)
    {
        object prefab = Field(Field(GameObject.Find("GameSystems").GetComponent("WaveSpawner"), "rangedPrefab"), "arrowPrefab");
        var arrow = (Component)Object.Instantiate((Object)prefab);
        System.Type teamType = prefab.GetType().Assembly.GetType("RogueRTS.Team");
        arrow.GetType().GetMethod("Launch").Invoke(
            arrow, new object[] { System.Enum.Parse(teamType, team), damage, from, from, aim, null, false });
        return arrow.transform;
    }

    /// <summary>
    /// 矢が unit に当たるには、まだ 1 フレームでは届かない距離にあるか。
    /// 当たったフレームは、矢が消えるより先にダメージが入るので、ダメージの有無はこの間だけ確かめられる。
    /// </summary>
    static bool StillFarFrom(Transform arrow, GameObject unit) =>
        Flat(arrow.position - unit.transform.position).magnitude > 1f + 30f * Time.deltaTime;

    static bool IsStuck(Transform arrow) => !((Behaviour)ComponentOfType(arrow.gameObject, "RogueRTS.Arrow")).enabled;

    static bool IsStuckInRock(Transform arrow) =>
        IsStuck(arrow) && Physics.OverlapSphere(arrow.position, 0.05f).Any(c => c.name == "Rock");

    [UnityTest, Timeout(300000)]
    public IEnumerator Paladin_ProducedWithKey5_Blocks15Percent_IdlesWalks_AndUsesSwordAndShieldBash()
    {
        // パラディンだけで戦わせるため、初期の自軍ユニットを全滅させる
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        GameObject.Find("GameSystems").SendMessage("AddGold", 1000);
        yield return null;

        Assert.AreEqual("パラディン [5]", ProduceButtonCaption(4), "5 番目の生産ボタンがパラディンになっていない");
        StringAssert.StartsWith("200", ProduceButtonText(4, "Face/Cost"), "パラディンのコストが 200 になっていない");

        yield return PressKey(Key.Digit5);
        Time.timeScale = 4f;
        float deadline = Time.realtimeSinceStartup + 30f;
        NavMeshAgent paladin = null;
        while (paladin == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "5 キーでパラディンが生産されない");
            paladin = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                .FirstOrDefault(a => a.enabled && a.name.StartsWith("Paladin"));
            yield return null;
        }

        // 剣士 (兵士) の 2.5 倍の体力と、15% のブロック
        Component unit = ComponentOfType(paladin.gameObject, "RogueRTS.Unit");
        object data = Field(unit, "data");
        var producible = (Component[])Field(ComponentOfType(GameObject.Find("PlayerBase"), "RogueRTS.PlayerBase"), "producibleUnits");
        Assert.AreEqual((float)Field(Field(producible[0], "data"), "maxHp") * 2.5f, (float)Field(data, "maxHp"), 0.01f,
            "体力が兵士の 2.5 倍でない");
        Assert.AreEqual(0.15f, (float)Field(data, "blockChance"), 0.001f, "ブロック率が 15% でない");

        // 集合地点に着いたら待機モーション
        Animator animator = paladin.GetComponentInChildren<Animator>();
        Assert.IsNotNull(animator, "パラディンにモデルの Animator がない");
        deadline = Time.realtimeSinceStartup + 20f;
        while (!(paladin.velocity.magnitude > 0.5f && animator.GetCurrentAnimatorStateInfo(0).IsName("Walk")))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "集合地点へ向かう間に歩行モーションにならない");
            yield return null;
        }
        deadline = Time.realtimeSinceStartup + 20f;
        while (paladin.velocity.magnitude > 0.05f || !animator.GetCurrentAnimatorStateInfo(0).IsName("Idle"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "止まっても待機モーションにならない");
            yield return null;
        }
        Time.timeScale = 1f;

        // 防ぐと体力が減らず、ブロックのモーション。防がないときは普通に減る
        Component health = ComponentOfType(paladin.gameObject, "RogueRTS.Health");
        health.GetType().GetField("blockChance").SetValue(health, 1f);
        float hp = CurrentHp(paladin.gameObject);
        paladin.SendMessage("TakeDamage", 30f);
        yield return new WaitForSeconds(0.15f);
        Assert.AreEqual(hp, CurrentHp(paladin.gameObject), 0.01f, "防いだのに体力が減った");
        AnimatorStateInfo now = animator.GetCurrentAnimatorStateInfo(0);
        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
        Assert.IsTrue(now.IsName("Block") || next.IsName("Block"), "防いだのにブロックのモーションにならない");

        health.GetType().GetField("blockChance").SetValue(health, 0f);
        paladin.SendMessage("TakeDamage", 30f);
        yield return null;
        Assert.AreEqual(hp - 30f, CurrentHp(paladin.gameObject), 0.01f, "防がなかったのに体力が減らない");
        health.GetType().GetField("blockChance").SetValue(health, 0.15f);

        // 剣とシールドバッシュの両方を使って敵と戦う (どちらも確率なので、両方出るまで待つ)
        Time.timeScale = 4f;
        deadline = Time.realtimeSinceStartup + 120f;
        bool sword = false, bash = false;
        while (!(sword && bash))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, $"剣 {sword} / シールドバッシュ {bash}: 両方の攻撃を使っていない");
            if (paladin == null || !paladin.enabled)
            {
                Assert.Fail($"確かめ終わる前にパラディンが倒れた (剣 {sword} / シールドバッシュ {bash})");
            }
            sword |= animator.GetCurrentAnimatorStateInfo(0).IsName("Attack");
            bash |= animator.GetCurrentAnimatorStateInfo(0).IsName("ShieldBash");
            // 倒されないよう、体力を保っておく
            if (CurrentHp(paladin.gameObject) < 100f) health.SendMessage("HealFull");
            yield return null;
        }
    }

    [UnityTest, Timeout(180000)]
    public IEnumerator Priest_ProducedWithKey4_Every3sHealsTheWeakestNearbyAllyBy10_NotItself_ShowsBlueNumbers_AndNeverAttacks()
    {
        // 司祭と、回復を受ける兵士だけにするため、初期の自軍ユニットを全滅させる
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        GameObject.Find("GameSystems").SendMessage("AddGold", 1000);
        yield return null;

        Assert.AreEqual("司祭 [4]", ProduceButtonCaption(3), "4 番目の生産ボタンが司祭になっていない");
        StringAssert.StartsWith("140", ProduceButtonText(3, "Face/Cost"), "司祭のコストが 140 になっていない");

        yield return PressKey(Key.Digit4);
        Time.timeScale = 4f;
        float deadline = Time.realtimeSinceStartup + 30f;
        NavMeshAgent priest = null;
        while (priest == null)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "4 キーで司祭が生産されない");
            priest = Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
                .FirstOrDefault(a => a.enabled && a.name.StartsWith("Priest"));
            yield return null;
        }

        // 体力は弓兵と同じ
        var producible = (Component[])Field(ComponentOfType(GameObject.Find("PlayerBase"), "RogueRTS.PlayerBase"), "producibleUnits");
        float archerHp = (float)Field(Field(producible[1], "data"), "maxHp");
        Component priestHealth = ComponentOfType(priest.gameObject, "RogueRTS.Health");
        Assert.AreEqual(archerHp, (float)Field(priestHealth, "maxHp"), 0.01f, "体力が弓兵と同じでない");

        // 集合地点へ歩き、着いたら待機モーション
        Animator animator = priest.GetComponentInChildren<Animator>();
        Assert.IsNotNull(animator, "司祭にモデルの Animator がない");
        deadline = Time.realtimeSinceStartup + 20f;
        while (!(priest.velocity.magnitude > 0.5f && animator.GetCurrentAnimatorStateInfo(0).IsName("Walk")))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "集合地点へ向かう間に歩行モーションにならない");
            yield return null;
        }
        deadline = Time.realtimeSinceStartup + 20f;
        while (priest.velocity.magnitude > 0.05f || !animator.GetCurrentAnimatorStateInfo(0).IsName("Idle"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "止まっても待機モーションにならない");
            yield return null;
        }
        Time.timeScale = 1f;
        StringAssert.EndsWith("idle", animator.GetCurrentAnimatorClipInfo(0)[0].clip.name, "待機中に待機モーションを使っていない");

        // 両隣に兵士を置き、片方を大きく傷つける。司祭自身はもっと傷ついているが、自分は回復しない
        Vector3 at = priest.transform.position;
        GameObject weak = ((Component)Object.Instantiate((Object)producible[0], at + new Vector3(2.5f, 0f, 0f), Quaternion.identity)).gameObject;
        GameObject hurt = ((Component)Object.Instantiate((Object)producible[0], at + new Vector3(-2.5f, 0f, 0f), Quaternion.identity)).gameObject;
        yield return null;
        weak.SendMessage("TakeDamage", 50f);
        hurt.SendMessage("TakeDamage", 30f);
        priest.SendMessage("TakeDamage", 20f);
        float priestHp = CurrentHp(priest.gameObject);
        yield return PressKey(Key.F2);

        // HP が上がるまで待ち、上がった時刻と回復した量を返す
        float healedAt = 0f;
        IEnumerator WaitForHeal(GameObject target, float hpBefore)
        {
            float limit = Time.realtimeSinceStartup + 15f;
            while (CurrentHp(target) <= hpBefore)
            {
                Assert.Less(Time.realtimeSinceStartup, limit, "傷ついた味方が近くにいても回復しない");
                yield return null;
            }
            healedAt = Time.time;
        }

        float weakHp = CurrentHp(weak), hurtHp = CurrentHp(hurt);
        yield return WaitForHeal(weak, weakHp);
        Assert.AreEqual(weakHp + 10f, CurrentHp(weak), 0.01f, "最も体力の低い味方が 10 回復していない");
        Assert.AreEqual(hurtHp, CurrentHp(hurt), 0.01f, "最も体力の低い味方より先に、ほかの味方を回復した");
        Assert.AreEqual(priestHp, CurrentHp(priest.gameObject), 0.01f, "司祭が自分を回復した");
        AnimatorStateInfo now = animator.GetCurrentAnimatorStateInfo(0);
        Assert.IsTrue(now.IsName("Heal"), "杖を掲げるモーションの最中に回復していない");
        StringAssert.EndsWith("raise_staff", animator.GetCurrentAnimatorClipInfo(0)[0].clip.name, "回復に杖を掲げるモーションを使っていない");

        // 回復した量を青い数字で出す
        TMP_Text number = Named("DamageNumber(Clone)").Select(n => n.GetComponent<TMP_Text>()).FirstOrDefault(n => n.text == "+10");
        Assert.IsNotNull(number, "回復した量が表示されない");
        Assert.Greater(number.color.b, 0.8f, "回復した量が青で表示されていない");
        Assert.Less(number.color.r, 0.5f, "回復した量が青で表示されていない");

        // 3 秒ごとに回復する (まだ最も体力が低いので、同じ味方をもう一度回復する)
        float firstHeal = healedAt;
        weakHp = CurrentHp(weak);
        yield return WaitForHeal(weak, weakHp);
        Assert.AreEqual(3f, healedAt - firstHeal, 0.15f, "回復の間隔が 3 秒でない");
        Assert.AreEqual(hurtHp, CurrentHp(hurt), 0.01f, "最も体力の低い味方より先に、ほかの味方を回復した");
        Assert.AreEqual(priestHp, CurrentHp(priest.gameObject), 0.01f, "司祭が自分を回復した");

        // 攻撃はしない: 兵士を下げ、すぐ前に敵兵を置いても、敵兵の体力は減らない
        weak.SendMessage("TakeDamage", 1e9f);
        hurt.SendMessage("TakeDamage", 1e9f);
        deadline = Time.realtimeSinceStartup + 10f;
        while (animator.GetCurrentAnimatorStateInfo(0).IsName("Heal") || animator.IsInTransition(0))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "杖を掲げるモーションが終わらない");
            yield return null;
        }
        GameObject enemy = SpawnEnemy(at + new Vector3(0f, 0f, 2f));
        yield return null;
        float enemyHp = CurrentHp(enemy);
        float until = Time.time + 4f;
        while (Time.time < until)
        {
            // 敵兵に倒されないよう、司祭の体力を保っておく
            if (CurrentHp(priest.gameObject) < 20f) priestHealth.SendMessage("HealFull");
            Assert.IsFalse(animator.GetCurrentAnimatorStateInfo(0).IsName("Heal"), "傷ついた味方がいないのに杖を掲げた");
            yield return null;
        }
        Assert.AreEqual(enemyHp, CurrentHp(enemy), 0.01f, "司祭が敵を攻撃した");
    }

    [UnityTest, Timeout(60000)]
    public IEnumerator PaladinShieldBash_HitsEveryEnemyInFrontAtOnce()
    {
        // パラディンだけで戦わせるため、初期の自軍ユニットを全滅させる
        foreach (NavMeshAgent agent in Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (IsPlayerUnit(agent.name)) agent.SendMessage("TakeDamage", 1e9f);
        }
        yield return null;

        var producible = (Component[])Field(ComponentOfType(GameObject.Find("PlayerBase"), "RogueRTS.PlayerBase"), "producibleUnits");
        var start = new Vector3(0f, 0f, -14f);
        var paladin = (Component)Object.Instantiate((Object)producible[3], start, Quaternion.identity);
        // 確かめやすいよう、この個体は毎回シールドバッシュを使うようにする
        object[] attacks = (object[])Field(ComponentOfType(paladin.gameObject, "RogueRTS.UnitAnimator"), "attacks");
        object bash = attacks.First(a => (string)Field(a, "trigger") == "ShieldBash");
        bash.GetType().GetField("useChance").SetValue(bash, 1f);
        // 防ぐと振りが中断されるので、防がないようにする (Start でデータの値が入ったあとに変える)
        yield return null;
        Component health = ComponentOfType(paladin.gameObject, "RogueRTS.Health");
        health.GetType().GetField("blockChance").SetValue(health, 0f);

        // 正面に敵兵を 3 体並べる。並んだ時点で全員が射程に入っている
        GameObject[] enemies =
        {
            SpawnEnemy(start + new Vector3(-1.2f, 0f, 1.6f)),
            SpawnEnemy(start + new Vector3(0f, 0f, 2f)),
            SpawnEnemy(start + new Vector3(1.2f, 0f, 1.6f)),
        };

        Animator animator = paladin.GetComponentInChildren<Animator>();
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!animator.GetCurrentAnimatorStateInfo(0).IsName("ShieldBash") && !animator.GetNextAnimatorStateInfo(0).IsName("ShieldBash"))
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "敵兵が正面にいてもシールドバッシュを使わない");
            yield return null;
        }

        // 盾が当たる瞬間を過ぎ、次の攻撃が当たる前に、何体の体力が減ったかを数える
        float[] before = enemies.Select(CurrentHp).ToArray();
        yield return new WaitForSeconds(0.75f);
        int hit = enemies.Where((e, i) => e == null || CurrentHp(e) < before[i]).Count();
        Assert.AreEqual(enemies.Length, hit, "シールドバッシュが正面の敵兵全員に当たっていない");
    }

    [UnityTest]
    public IEnumerator WaveCountdown_AtNormalSpeed_GoesDownOneSecondPerRealSecond_WithoutChangingGameSpeed()
    {
        yield return null;
        Component game = GameObject.Find("GameSystems").GetComponent("GameManager");
        float normalTimeScale = (float)Field(game, "normalTimeScale");
        Assert.AreEqual(normalTimeScale, Time.timeScale, 0.001f, "等速のときのゲーム内の速さが変わっている");

        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        float TimeToNextWave() => (float)spawner.GetType().GetProperty("TimeToNextWave").GetValue(spawner);

        float before = TimeToNextWave();
        float startReal = Time.realtimeSinceStartup;
        yield return new WaitForSecondsRealtime(2f);
        float elapsedReal = Time.realtimeSinceStartup - startReal;
        Assert.AreEqual(elapsedReal, before - TimeToNextWave(), 0.25f, "等速なのに、カウントが現実の 1 秒で 1 減っていない");

        // 2 倍速では 2 倍の速さで減る
        GameObject.Find("SpeedButton1").GetComponent<Button>().onClick.Invoke();
        before = TimeToNextWave();
        startReal = Time.realtimeSinceStartup;
        yield return new WaitForSecondsRealtime(1f);
        elapsedReal = Time.realtimeSinceStartup - startReal;
        Assert.AreEqual(elapsedReal * 2f, before - TimeToNextWave(), 0.3f, "2 倍速で、カウントが 2 倍の速さで減っていない");
    }

    [UnityTest]
    public IEnumerator AfterABossWave_TheNextWaveWaitsThreeTimesTheNormalInterval()
    {
        // 1 ウェーブ目は通常、2 ウェーブ目をボスウェーブにする
        Component spawner = GameObject.Find("GameSystems").GetComponent("WaveSpawner");
        object boss = Field(spawner, "boss");
        boss.GetType().GetField("waveInterval").SetValue(boss, 2);
        float normalInterval = (float)Field(spawner, "timeBetweenWaves");
        System.Reflection.PropertyInfo timeToNextWave = spawner.GetType().GetProperty("TimeToNextWave");
        bool LastWaveWasBoss() => (bool)spawner.GetType().GetProperty("LastWaveWasBoss").GetValue(spawner);

        // カウントを 0 にして、すぐ次のウェーブを出させる
        timeToNextWave.SetValue(spawner, 0f);
        yield return null;
        yield return null;
        Assert.IsFalse(LastWaveWasBoss(), "1 ウェーブ目がボスウェーブになっている");
        Assert.AreEqual(normalInterval, (float)timeToNextWave.GetValue(spawner), 0.5f, "通常のウェーブのあとの待ち時間が、ウェーブの間隔と違う");

        timeToNextWave.SetValue(spawner, 0f);
        yield return null;
        yield return null;
        Assert.IsTrue(LastWaveWasBoss(), "2 ウェーブ目がボスウェーブにならない");
        Assert.AreEqual(normalInterval * 3f, (float)timeToNextWave.GetValue(spawner), 0.5f, "ボスウェーブのあと、次のウェーブまで通常の 3 倍待たない");
    }

    [UnityTest, Timeout(180000)]
    public IEnumerator Trees_GrowSparselyAlmostNoneOrAsAGrove_AwayFromBaseAndSpawns_AndEnemiesCanStillReachTheBase()
    {
        Component forest = ComponentOfType(GameObject.Find("GameSystems"), "RogueRTS.ForestGenerator");
        Assert.AreEqual("Random", Field(forest, "pattern").ToString(), "ゲームを始めるたびに生え方を選ぶ設定になっていない");

        Vector3 basePosition = GameObject.Find("PlayerBase").transform.position;
        Vector3[] spawns = Named("EnemySpawnPoints")[0].Cast<Transform>().Select(t => t.position).ToArray();

        // 並びは毎回変わるので、それぞれの生え方を何回か作り直して確かめる
        foreach (string pattern in new[] { "Sparse", "Few", "Grove" })
        {
            for (int round = 0; round < 3; round++)
            {
                SetForestPatternOverride(pattern);
                yield return SceneManager.LoadSceneAsync("Game");
                yield return null;

                string when = $"{pattern} ({round + 1} 回目)";
                forest = ComponentOfType(GameObject.Find("GameSystems"), "RogueRTS.ForestGenerator");
                Assert.AreEqual(pattern, Property(forest, "CurrentPattern").ToString(), when);

                Transform[] trees = ((IEnumerable<Transform>)Property(forest, "Trees")).ToArray();
                Vector3[] positions = trees.Select(t => t.position).ToArray();
                float closest = ClosestPairDistance(positions);
                int crowded = positions.Count(p => positions.Count(q => Vector3.Distance(p, q) < 3f) - 1 >= 4);
                switch (pattern)
                {
                    case "Sparse":
                        Assert.That(trees.Length, Is.InRange(20, 40), $"{when}: 本数");
                        Assert.GreaterOrEqual(closest, 5f - 0.01f, $"{when}: 木が固まって生えている");
                        break;
                    case "Few":
                        Assert.That(trees.Length, Is.InRange(1, 5), $"{when}: 本数");
                        Assert.GreaterOrEqual(closest, 10f - 0.01f, $"{when}: 木が固まって生えている");
                        break;
                    case "Grove":
                        Assert.GreaterOrEqual(trees.Length, 25, $"{when}: 本数");
                        Assert.GreaterOrEqual(crowded, 10, $"{when}: 木が密集したエリアがない");
                        break;
                }

                foreach (Transform tree in trees)
                {
                    Assert.GreaterOrEqual(FlatDistance(tree.position, basePosition), 14f, $"{when}: 拠点のそばに木がある");
                    foreach (Vector3 spawn in spawns)
                        Assert.GreaterOrEqual(FlatDistance(tree.position, spawn), 8f, $"{when}: 敵の出現地点のそばに木がある");

                    // 木の根元は歩けず、建築もできない
                    Assert.IsFalse(NavMesh.SamplePosition(tree.position, out _, 0.5f, NavMesh.AllAreas),
                        $"{when}: 木の根元を歩けてしまう");
                    Assert.AreEqual("Obstacle", GridCell(tree.position), $"{when}: 木のマスに建築できてしまう");

                    // 矢は木を越えていく (攻撃が遮られるかの判定に使うレイが木に当たらない)
                    Vector3 across = tree.position + new Vector3(-0.98f, 1.2f, 0f);
                    Assert.IsFalse(Physics.Raycast(across, Vector3.right, 1.96f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore), $"{when}: 木が矢を遮る");
                }

                // どの出現地点からも、木をよけて拠点の前まで歩いて行ける
                Vector3 baseFront = NavMeshPositionNear(basePosition + new Vector3(0f, 0f, 4.5f));
                foreach (Vector3 spawn in spawns)
                {
                    var path = new NavMeshPath();
                    NavMesh.CalculatePath(NavMeshPositionNear(spawn), baseFront, NavMesh.AllAreas, path);
                    Assert.AreEqual(NavMeshPathStatus.PathComplete, path.status, $"{when}: {spawn} から拠点へ行けない");
                }
            }
        }
    }

    static void SetForestPatternOverride(string pattern)
    {
        System.Type forestType = System.Type.GetType("RogueRTS.ForestGenerator, Assembly-CSharp");
        System.Type patternType = System.Type.GetType("RogueRTS.ForestPattern, Assembly-CSharp");
        object value = pattern == null ? null : System.Enum.Parse(patternType, pattern);
        forestType.GetField("PatternOverride").SetValue(null, value);
    }

    /// <summary>position を含む建築マスの種類 (Free, Obstacle など)。</summary>
    static string GridCell(Vector3 position)
    {
        object grid = Property(ComponentOfType(GameObject.Find("GameSystems"), "RogueRTS.BuildController"), "Grid");
        object cell = grid.GetType().GetMethod("WorldToCell").Invoke(grid, new object[] { position });
        return grid.GetType().GetMethod("Get").Invoke(grid, new[] { cell }).ToString();
    }

    static Vector3 NavMeshPositionNear(Vector3 position)
    {
        Assert.IsTrue(NavMesh.SamplePosition(position, out NavMeshHit hit, 5f, NavMesh.AllAreas), $"{position} の近くに NavMesh がない");
        return hit.position;
    }

    static float FlatDistance(Vector3 a, Vector3 b) => Vector3.Distance(Flat(a), Flat(b));

    static float ClosestPairDistance(Vector3[] positions)
    {
        float closest = float.MaxValue;
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = i + 1; j < positions.Length; j++)
                closest = Mathf.Min(closest, FlatDistance(positions[i], positions[j]));
        }
        return closest;
    }

    static object Field(object target, string name) => target.GetType().GetField(name).GetValue(target);
    static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);

    static string CameraMode() =>
        Property(ComponentOfType(Camera.main.gameObject, "RogueRTS.RtsCameraController"), "CurrentMode").ToString();
    static Component FocusController() => ComponentOfType(GameObject.Find("GameSystems"), "RogueRTS.UnitFocusController");

    static GameObject FirstAlly() => Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None)
        .First(a => a.enabled && IsPlayerUnit(a.name)).gameObject;
    static Vector2 OnScreen(GameObject unit) => Camera.main.WorldToScreenPoint(unit.transform.position + Vector3.up);
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>カメラが unit のすぐ後ろ上から unit を見ている (三人称視点) こと。</summary>
    static void AssertThirdPerson(Transform cam, GameObject unit, string when)
    {
        Vector3 toUnit = unit.transform.position + Vector3.up * 1.5f - cam.position;
        Assert.Less(toUnit.magnitude, 8f, $"{when}: カメラがユニットのそばにいない");
        Assert.Less(Vector3.Angle(cam.forward, toUnit), 5f, $"{when}: カメラがユニットを見ていない");
        Assert.Less(cam.position.y, 6f, $"{when}: 見下ろしのままで、三人称視点になっていない");
    }

    // GetComponent("Unit") だと Visual Scripting の同名クラスに解決されてしまうので、完全名で探す
    static Component ComponentOfType(GameObject go, string fullName)
    {
        return go.GetComponents<Component>().First(c => c.GetType().FullName == fullName);
    }

    [UnityTest]
    public IEnumerator BuildMode_HotkeyThenClickPlacesWall_DragPaints_RightClickExits()
    {
        Camera cam = Camera.main;
        Time.timeScale = 4f;
        yield return new WaitForSeconds(4f);
        Time.timeScale = 1f;
        GameObject.Find("GameSystems").SendMessage("AddCoins", 100);

        yield return PressKey(Key.Z);
        Vector3 first = cam.WorldToScreenPoint(new Vector3(11f, 0f, -15f));
        yield return SendMouse(first);
        Assert.IsNotNull(GameObject.Find("BuildGhost"), "設置モードでプレビューが出ない");

        yield return SendMouse(first, MouseButton.Left);
        yield return SendMouse(first);
        Transform[] placed = Named("Wall(Clone)");
        Assert.AreEqual(1, placed.Length, "クリックで防壁が置けない");
        Assert.Less(Vector3.Distance(placed[0].position, new Vector3(11f, 0f, -15f)), 0.01f, "マスの中心に置かれていない");
        Assert.AreEqual(85, Coins());

        // 押したままなぞると、通ったマスに続けて置かれる
        Vector3 second = cam.WorldToScreenPoint(new Vector3(13f, 0f, -15f));
        Vector3 third = cam.WorldToScreenPoint(new Vector3(15f, 0f, -15f));
        yield return SendMouse(second, MouseButton.Left);
        yield return SendMouse(third, MouseButton.Left);
        yield return SendMouse(third);
        Assert.AreEqual(3, CountNamed("Wall(Clone)"), "なぞって連続設置できない");
        Assert.AreEqual(55, Coins());
        Assert.AreEqual(0, SelectedUnits().Length, "設置モード中にユニットが選択された");

        // 右クリックで終了し、その後のクリックでは置かれない
        yield return SendMouse(third, MouseButton.Right);
        yield return SendMouse(third);
        Assert.IsNull(GameObject.Find("BuildGhost"), "右クリックで設置モードが終わらない");

        Vector3 elsewhere = cam.WorldToScreenPoint(new Vector3(9f, 0f, -11f));
        yield return SendMouse(elsewhere, MouseButton.Left);
        yield return SendMouse(elsewhere);
        Assert.AreEqual(3, CountNamed("Wall(Clone)"), "設置モードを抜けたのに置かれた");
    }

    IEnumerator PressKey(Key key)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        yield return null;
        yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
    }

    /// <summary>Ctrl を押したまま、マウスを 1 フレームに delta ずつ、frames フレームの間動かす。</summary>
    IEnumerator CtrlLook(Vector2 delta, int frames)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.LeftCtrl));
        for (int i = 0; i < frames; i++)
        {
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = delta });
            yield return null;
        }
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null;
    }

    /// <summary>仮想マウスの状態を送り、1フレーム進める。</summary>
    IEnumerator SendMouse(Vector2 position, MouseButton? pressed = null)
    {
        var state = new MouseState { position = position };
        if (pressed.HasValue) state = state.WithButton(pressed.Value);
        InputSystem.QueueStateEvent(mouse, state);
        yield return null;
        yield return null;
    }

    static bool IsPlayerUnit(string name) =>
        name.StartsWith("Soldier") || name.StartsWith("Archer") || name.StartsWith("Spearman") || name.StartsWith("Paladin")
        || name.StartsWith("Priest");
    static bool IsEnemyUnit(string name) => name.StartsWith("Enemy");

    /// <summary>生きているユニットの数。倒れるモーション中のユニットはエージェントが止まっているので数えない。</summary>
    static int CountAgents(System.Func<string, bool> nameFilter)
    {
        return Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None).Count(a => a.enabled && nameFilter(a.name));
    }

    static float CurrentHp(GameObject unit) =>
        (float)ComponentOfType(unit, "RogueRTS.Health").GetType().GetProperty("CurrentHp").GetValue(ComponentOfType(unit, "RogueRTS.Health"));

    /// <summary>槍の握りの部分と右手の距離。槍を手に持っていれば 0 に近い。</summary>
    static float SpearGripOffset(GameObject spearman)
    {
        Renderer Part(string name) => spearman.GetComponentsInChildren<Renderer>().First(r => r.name == name);
        return Vector3.Distance(Part("spear_grip").bounds.center, Part("right_glove").bounds.center);
    }

    static readonly Vector3 GateCell = new Vector3(1f, 0f, -23f);

    /// <summary>拠点 (x: -2..2, z: -30..-26) を1マス空けて囲む、20マスの輪。</summary>
    static List<Vector3> RingAroundBase()
    {
        var ring = new List<Vector3>();
        for (int x = -5; x <= 5; x += 2)
        {
            ring.Add(new Vector3(x, 0f, -23f));
            ring.Add(new Vector3(x, 0f, -33f));
        }
        for (int z = -31; z <= -25; z += 2)
        {
            ring.Add(new Vector3(-5f, 0f, z));
            ring.Add(new Vector3(5f, 0f, z));
        }
        return ring;
    }

    static Transform[] Named(string name)
    {
        return Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == name).ToArray();
    }

    static int CountNamed(string name) => Named(name).Length;

    /// <summary>HUD の「コイン: 123」から所持コインを読む。</summary>
    static int Coins()
    {
        string text = GameObject.Find("CoinText").GetComponent<TMP_Text>().text;
        return int.Parse(text.Substring(text.IndexOf(':') + 1).Trim());
    }

    /// <summary>HUD の「拠点HP: 950/1000」から現在HPを読む。</summary>
    static int BaseHp()
    {
        string text = GameObject.Find("BaseHpText").GetComponent<TMP_Text>().text;
        int start = text.IndexOf(':') + 1;
        return int.Parse(text.Substring(start, text.IndexOf('/') - start).Trim());
    }

    static float PathLength(NavMeshPath path)
    {
        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return length;
    }

    /// <summary>選択リングが表示されているユニット。</summary>
    static Transform[] SelectedUnits()
    {
        return Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.name == "SelectionRing")
            .Select(t => t.parent)
            .ToArray();
    }
}
