using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RogueRTS.EditorTools
{
    /// <summary>
    /// プロトタイプ用のシーン・プレハブ・データ一式を生成する。
    /// データアセット (ユニット/報酬/マテリアル) は既にあれば上書きせず、シーンとプレハブだけ作り直す。
    /// </summary>
    public static class RtsPrototypeBuilder
    {
        const string ScenePath = "Assets/Scenes/Game.unity";
        const string NavMeshPath = "Assets/Scenes/Game_NavMesh.asset";
        const string MaterialDir = "Assets/Materials";
        const string PrefabDir = "Assets/Prefabs/Units";
        const string BuildingPrefabDir = "Assets/Prefabs/Buildings";
        const string ProjectilePrefabDir = "Assets/Prefabs/Projectiles";
        const string UnitDataDir = "Assets/Data/Units";
        const string BuildingDataDir = "Assets/Data/Buildings";
        const string RewardDir = "Assets/Data/Rewards";
        const string AnimationDir = "Assets/Animations";
        const string EnvironmentPrefabDir = "Assets/Prefabs/Environment";

        // ステージに生やす木のモデル (高さ 4m・葉の幅 2.5m)。当たり判定はマス 1 つ分の箱にする
        const string TreeModelPath = "Assets/Models/tree.fbx";
        const float TreeColliderHeight = 3f;
        const int IgnoreRaycastLayer = 2;

        // タレットのモデルと、その発射モーション (3 秒) の中の時刻。モーションを調べて得た値
        const string TurretModelPath = "Assets/Models/crossbow_turret.fbx";
        const string TurretControllerPath = AnimationDir + "/Turret.controller";
        const float TurretModelScale = 0.85f;
        const float TurretFireStart = 2.4f;    // 引き金を引き始める
        const float TurretReleaseTime = 2.43f; // 矢が放たれる
        const float TurretLoadedTime = 2.3f;   // 巻き上げと装填が終わって構えた

        // 矢のモデルと、その長さ。モデルは -Z を向いていて、原点が矢筈 (弓の弦につがえる側) にある
        const string ArrowModelPath = "Assets/Models/arrow.fbx";
        const float ArrowModelLength = 0.875f;
        // 弓兵のモーションで矢を放つ瞬間の、つがえた矢の先端の位置。モーションを調べて得た値
        static readonly Vector3 ArrowReleasePoint = new Vector3(0.05f, 1.6f, 1.19f);
        const int NotWalkableArea = 1;

        /// <summary>攻撃モーション1つ分の設定。</summary>
        class AttackSpec
        {
            public string trigger, clip;
            // 武器が当たる (矢を放つ) 時刻。等倍再生時の、モーション開始からの秒数
            public float hitTime;
            public bool hitsAllInFront;
            public int minTargets = 1;
            // 条件を満たしたときに実際に使う確率と、当てた相手を押し戻す距離の倍率
            public float useChance = 1f, knockbackMultiplier = 1f;
        }

        /// <summary>
        /// モデルを使うキャラクターの設定。数値はモデルのモーションを調べて得たもの。
        /// 色違いのモデルでも同じモーションを使うので、モーションは clipModel から取り出す。
        /// </summary>
        class CharacterSpec
        {
            public string clipModel, controllerPath;
            // クリップ名の末尾。待機・被弾・ブロックのモーションがなければ idle・hurt・block は null
            public string idle, walk, death, hurt, block;
            // 上から順に、使える条件を満たした最初の攻撃を使う
            public AttackSpec[] attacks;
            public float scale = 1f;
            // 等倍の歩行1周で進む速さ (m/s, scale を掛ける前)
            public float walkCycleSpeed;
            public float maxAttackPlaybackSpeed = 1.6f;
            // 体の大きさや武器の長さのぶん、射程に足す距離
            public float reachBonus;
            // 近接攻撃を受けたとき、ダメージ1あたりに押し戻される距離 (m)
            public float knockbackPerDamage = 0.05f;
            public float agentRadius = 0.5f, agentHeight = 2f, turnSpeed = 720f, acceleration = 30f;
            public float healthBarY = 2.5f, healthBarWidth = 1.2f, healthBarThickness = 0.14f;
        }

        static readonly CharacterSpec SwordsmanSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/swordsman.fbx",
            controllerPath = AnimationDir + "/Swordsman.controller",
            idle = "idle", walk = "walk", death = "death", hurt = "hurt",
            attacks = new[] { new AttackSpec { trigger = "Attack", clip = "attack", hitTime = 0.55f } },
            walkCycleSpeed = 1.08f,
        };

        static readonly CharacterSpec ArcherSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/archer.fbx",
            controllerPath = AnimationDir + "/Archer.controller",
            idle = "idle", walk = "walk", death = "death", hurt = "hurt",
            attacks = new[] { new AttackSpec { trigger = "Attack", clip = "shoot", hitTime = 1.03f } },
            walkCycleSpeed = 1.08f,
        };

        static readonly CharacterSpec SpearmanSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/spearman.fbx",
            controllerPath = AnimationDir + "/Spearman.controller",
            idle = "idle", walk = "walk", death = "death", hurt = "hurt",
            // 突き出した穂先が一番前に届く時刻
            attacks = new[] { new AttackSpec { trigger = "Attack", clip = "thrust", hitTime = 0.47f } },
            walkCycleSpeed = 1.1f,
            // 待機中は槍を立てて持つ (穂先の高さ 2.8) ので、HP バーをその上に出す
            healthBarY = 3f,
        };

        static readonly CharacterSpec PaladinSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/paladin.fbx",
            controllerPath = AnimationDir + "/Paladin.controller",
            // 被弾モーションはないので、殴られても怯まない。防いだときはブロックのモーション
            idle = "idle", walk = "walk", death = "death", hurt = null, block = "block",
            attacks = new[]
            {
                // 3 回に 1 回ほど盾で殴り、正面側の敵をまとめて大きく押し戻す。盾が一番前に出る時刻に当たる
                new AttackSpec
                {
                    trigger = "ShieldBash", clip = "shield_bash", hitTime = 0.47f, hitsAllInFront = true,
                    useChance = 0.3f, knockbackMultiplier = 2.5f,
                },
                // それ以外は剣で斬る。振りかぶった剣が前へ振り下ろされる時刻に当たる
                new AttackSpec { trigger = "Attack", clip = "attack", hitTime = 0.65f },
            },
            walkCycleSpeed = 0.95f,
            healthBarY = 2.8f,
        };

        static readonly CharacterSpec PriestSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/priest.fbx",
            controllerPath = AnimationDir + "/Priest.controller",
            idle = "idle", walk = "walk", death = "death", hurt = "hurt",
            // 攻撃の代わりに杖を掲げて味方を回復する。掲げた杖の宝石が一番大きく光る時刻に回復する
            attacks = new[] { new AttackSpec { trigger = "Heal", clip = "raise_staff", hitTime = 0.75f } },
            walkCycleSpeed = 1.1f,
            // 司教冠の先 (高さ 2.4) より上に出す
            healthBarY = 2.8f,
        };

        static readonly CharacterSpec HeavyKnightSpec = new CharacterSpec
        {
            clipModel = "Assets/Models/heavy_knight2.fbx",
            controllerPath = AnimationDir + "/EnemyBoss.controller",
            walk = "walk", death = "collapse", hurt = null,
            attacks = new[]
            {
                // 正面側に2体以上いれば薙ぎ払いでまとめて攻撃し、それ以外は振り下ろし
                new AttackSpec { trigger = "Sweep", clip = "axe_sweep", hitTime = 0.6f, hitsAllInFront = true, minTargets = 2 },
                new AttackSpec { trigger = "Attack", clip = "axe_slam", hitTime = 1.2f },
            },
            scale = 0.75f,
            walkCycleSpeed = 1.93f,
            // 重い一撃なので攻撃間隔に合わせて速めない (代わりにボス側で攻撃力を上げている)
            maxAttackPlaybackSpeed = 1f,
            // 斧が届く距離。これがないと、自分の射程ぎりぎりから斬りかかる兵士にボスの攻撃が届かない
            reachBonus = 1.2f,
            // 重装なので押し戻されない
            knockbackPerDamage = 0f,
            agentRadius = 0.8f, agentHeight = 3.4f, turnSpeed = 360f, acceleration = 20f,
            healthBarY = 4.3f, healthBarWidth = 2.2f, healthBarThickness = 0.22f,
        };

        // 1回の生成の間、同じキャラクターの Animator コントローラーは1度だけ作る
        static readonly Dictionary<string, AnimatorController> controllersThisBuild =
            new Dictionary<string, AnimatorController>();

        // UI の文字は TextMeshPro で、このフォントを使う
        const string UiFontPath = "Assets/Fonts/Corporate-Logo-Medium-ver3 SDF.asset";

        const string LitShader = "Universal Render Pipeline/Lit";
        const string UnlitShader = "Universal Render Pipeline/Unlit";

        static readonly Vector3 BasePosition = new Vector3(0f, 0f, -28f);
        // 拠点の HP バーの高さ。屋上の旗 (高さ 4.6) より上に出す
        const float BaseHealthBarY = 5.2f;

        // CreateUi の間だけ使う、UI 文字のフォントと影付きマテリアル
        static TMP_FontAsset uiFont;
        static Material uiShadowMaterial;

        class Mats
        {
            public Material ground, rock, player, enemy, dark;
            public Material ring, marker, hpBack, hpPlayer, hpEnemy, shotPlayer, shotEnemy;
            public Material wall, ghostValid, ghostInvalid;
        }

        class UnitSet
        {
            public Unit soldier, archer, spearman, paladin, priest, enemyGrunt, enemyArcher, enemyBoss;
        }

        /// <summary>拠点で生産できるユニット。この並びが生産ボタンと数字キー (1, 2, ...) の並びになる。</summary>
        static Unit[] ProducibleUnits(UnitSet units) =>
            new[] { units.soldier, units.archer, units.spearman, units.priest, units.paladin };

        class UiRefs
        {
            public RectTransform selectionBox;
            public TMP_Text focusMarker;
            public TMP_Text damageNumber;
            public TMP_Text cameraHint;
            public RewardScreen rewardScreen;
            public GameOverScreen gameOverScreen;
        }

        [MenuItem("Tools/RogueRTS/プロトタイプシーンを生成")]
        static void BuildFromMenu()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog(
                    "プロトタイプシーンを生成",
                    $"{ScenePath} とユニットのプレハブを作り直します。\nシーンやプレハブに手で加えた変更は失われます。",
                    "作り直す", "キャンセル"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Build();
        }

        /// <summary>
        /// バッチモードからも呼べる:
        /// Unity.exe -batchmode -quit -projectPath . -executeMethod RogueRTS.EditorTools.RtsPrototypeBuilder.Build
        /// </summary>
        public static void Build()
        {
            string[] dirs =
            {
                "Assets/Scenes", MaterialDir, PrefabDir, BuildingPrefabDir, ProjectilePrefabDir, UnitDataDir, BuildingDataDir, RewardDir,
                AnimationDir, EnvironmentPrefabDir,
            };
            foreach (string dir in dirs) Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            Mats mats = CreateMaterials();
            UnitSet units = CreateUnits(mats);
            BuildingData[] buildings = CreateBuildings(mats);
            RewardData[] rewards = CreateRewards();

            SetupLighting();
            Camera cam = SetupCamera();
            CreateEnvironment(mats);
            PlayerBase playerBase = CreatePlayerBase(mats);
            NavMeshSurface surface = BakeNavMesh();
            Transform[] spawnPoints = CreateEnemySpawnPoints();
            MoveMarker marker = CreateMoveMarker(mats);

            GameObject ghost = CreateVisual(PrimitiveType.Cube, "BuildGhost", null, mats.ghostValid, false);
            ghost.SetActive(false);

            Unit[] producible = ProducibleUnits(units);
            UiRefs ui = CreateUi(producible.Length, buildings.Length);

            var systems = new GameObject("GameSystems");
            var bank = systems.AddComponent<ResourceBank>();

            playerBase.bank = bank;
            playerBase.producibleUnits = producible;

            var build = systems.AddComponent<BuildController>();
            build.cam = cam;
            build.bank = bank;
            build.surface = surface;
            build.options = buildings;
            build.ghost = ghost.GetComponent<MeshRenderer>();
            build.ghostValidMaterial = mats.ghostValid;
            build.ghostInvalidMaterial = mats.ghostInvalid;
            build.enemySpawnPoints = spawnPoints;
            build.forest = AddForest(systems, playerBase);

            var spawner = systems.AddComponent<WaveSpawner>();
            spawner.buildController = build;
            spawner.meleePrefab = units.enemyGrunt;
            spawner.rangedPrefab = units.enemyArcher;
            // モデルがなければ、通常の敵兵の見た目のままボスにする
            spawner.boss.bossPrefab = units.enemyBoss != null ? units.enemyBoss : units.enemyGrunt;
            spawner.spawnPoints = spawnPoints;
            spawner.attackTarget = playerBase.transform;

            var selection = systems.AddComponent<SelectionController>();
            selection.cam = cam;
            selection.selectionBox = ui.selectionBox;
            selection.moveMarker = marker;

            var focus = systems.AddComponent<UnitFocusController>();
            focus.cam = cam;
            focus.cameraController = cam.GetComponent<RtsCameraController>();
            focus.marker = ui.focusMarker;
            focus.hintText = ui.cameraHint;

            var damage = systems.AddComponent<DamageIndicator>();
            damage.cam = cam;
            damage.template = ui.damageNumber;

            var game = systems.AddComponent<GameManager>();
            game.bank = bank;
            game.playerBase = playerBase;
            game.waveSpawner = spawner;
            game.buildController = build;
            game.rewardScreen = ui.rewardScreen;
            game.gameOverScreen = ui.gameOverScreen;
            game.rewardPool = rewards;
            game.startingUnits = new[]
            {
                units.soldier, units.soldier, units.soldier, units.soldier, units.archer, units.archer,
            };

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();
            Debug.Log($"[RogueRTS] {ScenePath} を生成しました。再生ボタンで遊べます。");
        }

        // ---------------------------------------------------------------- アセット

        static Mats CreateMaterials()
        {
            return new Mats
            {
                ground = LoadOrCreateMaterial("Ground", LitShader, new Color(0.33f, 0.45f, 0.30f)),
                rock = LoadOrCreateMaterial("Rock", LitShader, new Color(0.42f, 0.42f, 0.45f)),
                player = LoadOrCreateMaterial("TeamPlayer", LitShader, new Color(0.20f, 0.45f, 0.95f)),
                enemy = LoadOrCreateMaterial("TeamEnemy", LitShader, new Color(0.90f, 0.22f, 0.20f)),
                dark = LoadOrCreateMaterial("Dark", LitShader, new Color(0.10f, 0.10f, 0.12f)),
                ring = LoadOrCreateMaterial("SelectionRing", UnlitShader, new Color(0.30f, 1.00f, 0.40f)),
                marker = LoadOrCreateMaterial("MoveMarker", UnlitShader, new Color(1.00f, 0.95f, 0.30f)),
                hpBack = LoadOrCreateMaterial("HpBarBack", UnlitShader, new Color(0.08f, 0.08f, 0.08f)),
                hpPlayer = LoadOrCreateMaterial("HpBarPlayer", UnlitShader, new Color(0.25f, 0.90f, 0.30f)),
                hpEnemy = LoadOrCreateMaterial("HpBarEnemy", UnlitShader, new Color(0.95f, 0.25f, 0.20f)),
                shotPlayer = LoadOrCreateMaterial("ShotPlayer", UnlitShader, new Color(0.70f, 0.90f, 1.00f)),
                shotEnemy = LoadOrCreateMaterial("ShotEnemy", UnlitShader, new Color(1.00f, 0.65f, 0.30f)),
                wall = LoadOrCreateMaterial("Wall", LitShader, new Color(0.74f, 0.68f, 0.54f)),
                ghostValid = LoadOrCreateMaterial("GhostValid", UnlitShader, new Color(0.30f, 1.00f, 0.40f, 0.45f)),
                ghostInvalid = LoadOrCreateMaterial("GhostInvalid", UnlitShader, new Color(1.00f, 0.25f, 0.20f, 0.45f)),
            };
        }

        /// <summary>色のアルファが 1 未満なら半透明マテリアルとして作る。</summary>
        static Material LoadOrCreateMaterial(string name, string shaderName, Color color)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            material = new Material(Shader.Find(shaderName));
            material.SetColor("_BaseColor", color);
            if (color.a < 1f)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)RenderQueue.Transparent;
            }
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static T LoadOrCreateData<T>(string path, Action<T> init) where T : ScriptableObject
        {
            var data = AssetDatabase.LoadAssetAtPath<T>(path);
            if (data != null) return data;

            data = ScriptableObject.CreateInstance<T>();
            init(data);
            AssetDatabase.CreateAsset(data, path);
            return data;
        }

        static UnitSet CreateUnits(Mats mats)
        {
            UnitData soldier = LoadOrCreateData<UnitData>($"{UnitDataDir}/Soldier.asset", d =>
            {
                d.displayName = "兵士";
                d.maxHp = 100f; d.attackDamage = 10f; d.attackRange = 1.5f; d.attackInterval = 1f;
                d.aggroRange = 18f; d.moveSpeed = 4f; d.cost = 50; d.buildTime = 4f; d.bounty = 0;
            });
            UnitData archer = LoadOrCreateData<UnitData>($"{UnitDataDir}/Archer.asset", d =>
            {
                d.displayName = "弓兵";
                d.maxHp = 35f; d.attackDamage = 9f; d.attackRange = 21f; d.attackInterval = 2.6f;
                d.aggroRange = 26.67f; d.moveSpeed = 3.8f; d.cost = 70; d.buildTime = 5f; d.bounty = 0;
            });
            // 剣士 (兵士) の 1.5 倍のリーチと体力を持つ。それ以外は兵士と同じ
            UnitData spearman = LoadOrCreateData<UnitData>($"{UnitDataDir}/Spearman.asset", d =>
            {
                d.displayName = "槍兵";
                d.maxHp = soldier.maxHp * 1.5f; d.attackDamage = soldier.attackDamage;
                d.attackRange = soldier.attackRange * 1.5f; d.attackInterval = soldier.attackInterval;
                d.aggroRange = soldier.aggroRange; d.moveSpeed = soldier.moveSpeed;
                d.cost = 120; d.buildTime = soldier.buildTime; d.bounty = 0;
            });
            UnitData paladin = LoadOrCreateData<UnitData>($"{UnitDataDir}/Paladin.asset", d =>
            {
                d.displayName = "パラディン";
                d.maxHp = soldier.maxHp * 2.5f; d.attackDamage = soldier.attackDamage;
                d.attackRange = soldier.attackRange; d.attackInterval = soldier.attackInterval;
                d.aggroRange = soldier.aggroRange; d.moveSpeed = soldier.moveSpeed;
                d.cost = 200; d.buildTime = soldier.buildTime; d.bounty = 0;
                d.blockChance = 0.15f;
            });
            // 攻撃はせず、3 秒ごとに近くの味方 1 体の HP を 10 回復する。体力・足の速さ・生産時間は弓兵と同じ
            UnitData priest = LoadOrCreateData<UnitData>($"{UnitDataDir}/Priest.asset", d =>
            {
                d.displayName = "司祭";
                d.maxHp = archer.maxHp; d.attackDamage = 0f; d.attackRange = 6f; d.attackInterval = 3f;
                d.aggroRange = 18f; d.moveSpeed = archer.moveSpeed;
                d.cost = 140; d.buildTime = archer.buildTime; d.bounty = 0;
                d.healAmount = 10f;
            });
            UnitData enemyGrunt = LoadOrCreateData<UnitData>($"{UnitDataDir}/EnemyGrunt.asset", d =>
            {
                d.displayName = "敵兵";
                d.maxHp = 70f; d.attackDamage = 8f; d.attackRange = 1.5f; d.attackInterval = 1.1f;
                d.aggroRange = 18f; d.moveSpeed = 3.5f; d.cost = 0; d.buildTime = 0f; d.bounty = 10;
            });
            UnitData enemyArcher = LoadOrCreateData<UnitData>($"{UnitDataDir}/EnemyArcher.asset", d =>
            {
                d.displayName = "敵弓兵";
                d.maxHp = 25f; d.attackDamage = 7f; d.attackRange = 6.5f; d.attackInterval = 1.4f;
                d.aggroRange = 20f; d.moveSpeed = 3.4f; d.cost = 0; d.buildTime = 0f; d.bounty = 12;
            });

            AssignUnitIcons();

            // 自軍は通常のモデル、敵は赤いモデル。モデルがなければ、近接はカプセル・遠距離は円柱で代用する
            controllersThisBuild.Clear();
            Arrow arrow = CreateArrowPrefab();
            return new UnitSet
            {
                soldier = CreateCharacterPrefab("Soldier", soldier, Team.Player, "Assets/Models/swordsman.fbx", SwordsmanSpec, false, mats)
                          ?? CreateUnitPrefab("Soldier", soldier, Team.Player, PrimitiveType.Capsule, 0.9f, 1.8f, mats),
                archer = CreateCharacterPrefab("Archer", archer, Team.Player, "Assets/Models/archer.fbx", ArcherSpec, true, mats, arrow)
                         ?? CreateUnitPrefab("Archer", archer, Team.Player, PrimitiveType.Cylinder, 0.6f, 1.6f, mats),
                spearman = CreateCharacterPrefab("Spearman", spearman, Team.Player, "Assets/Models/spearman.fbx", SpearmanSpec, false, mats)
                           ?? CreateUnitPrefab("Spearman", spearman, Team.Player, PrimitiveType.Capsule, 0.9f, 1.8f, mats),
                paladin = CreateCharacterPrefab("Paladin", paladin, Team.Player, "Assets/Models/paladin.fbx", PaladinSpec, false, mats)
                          ?? CreateUnitPrefab("Paladin", paladin, Team.Player, PrimitiveType.Capsule, 0.9f, 1.8f, mats),
                priest = CreateCharacterPrefab("Priest", priest, Team.Player, "Assets/Models/priest.fbx", PriestSpec, false, mats)
                         ?? CreateUnitPrefab("Priest", priest, Team.Player, PrimitiveType.Cylinder, 0.6f, 1.6f, mats),
                enemyGrunt = CreateCharacterPrefab("EnemyGrunt", enemyGrunt, Team.Enemy, "Assets/Models/swordsman_red.fbx", SwordsmanSpec, false, mats)
                             ?? CreateUnitPrefab("EnemyGrunt", enemyGrunt, Team.Enemy, PrimitiveType.Capsule, 0.9f, 1.8f, mats),
                enemyArcher = CreateCharacterPrefab("EnemyArcher", enemyArcher, Team.Enemy, "Assets/Models/archer_red.fbx", ArcherSpec, true, mats, arrow)
                              ?? CreateUnitPrefab("EnemyArcher", enemyArcher, Team.Enemy, PrimitiveType.Cylinder, 0.6f, 1.6f, mats),
                // ボスの能力は通常の敵兵の値に、出現時に WaveSpawner が倍率を掛ける
                enemyBoss = CreateCharacterPrefab("EnemyBoss", enemyGrunt, Team.Enemy, "Assets/Models/heavy_knight2.fbx", HeavyKnightSpec, false, mats),
            };
        }

        /// <summary>
        /// ユニットとボスのプレハブだけを作り直す (シーンは触らない)。
        /// プレハブのパスは変わらないので、シーンからの参照はそのまま新しいプレハブを指す。
        /// </summary>
        public static void BuildUnitPrefabs()
        {
            foreach (string dir in new[] { MaterialDir, PrefabDir, ProjectilePrefabDir, UnitDataDir, AnimationDir })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            CreateUnits(CreateMaterials());
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// モデルを使ったユニット。歩行・攻撃・被弾・撃破のモーションを Animator で切り替える。
        /// モデルかモーションが見つからなければ null。
        /// </summary>
        static Unit CreateCharacterPrefab(
            string name, UnitData data, Team team, string modelPath, CharacterSpec spec, bool ranged, Mats mats,
            Arrow arrow = null)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            AnimationClip[] clips = LoadClips(spec.clipModel);
            if (model == null || clips.Length == 0)
            {
                Debug.LogWarning($"[RogueRTS] {modelPath} か {spec.clipModel} が見つからないため、{name} はモデルなしで作ります。");
                return null;
            }

            bool isPlayer = team == Team.Player;
            var root = new GameObject(name);

            // モデルは -Z を向いているので反転し、大きさを合わせる。
            // 撃破などのモーションはモデルの根元を動かすので、向きと大きさはその一段上 (Model) に持たせる
            Transform pivot = CreateChild(root.transform, "Model", Vector3.zero);
            pivot.localRotation = Quaternion.Euler(0f, 180f, 0f);
            pivot.localScale = Vector3.one * spec.scale;
            var character = (GameObject)PrefabUtility.InstantiatePrefab(model, pivot);
            ApplyRestPose(character.transform, modelPath);
            var animator = character.AddComponent<Animator>();
            animator.runtimeAnimatorController = LoadOrCreateController(spec, clips);
            animator.applyRootMotion = false;

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = spec.agentRadius;
            agent.height = spec.agentHeight;
            agent.speed = data.moveSpeed;
            agent.angularSpeed = spec.turnSpeed;
            agent.acceleration = spec.acceleration;
            agent.stoppingDistance = 0.1f;

            var health = root.AddComponent<Health>();
            health.team = team;
            health.maxHp = data.maxHp;
            health.radius = spec.agentRadius;
            health.height = spec.agentHeight;
            // 倒れるモーションを見せてから Unit が消す
            health.destroyOnDeath = false;

            var unit = root.AddComponent<Unit>();
            unit.data = data;
            unit.reachBonus = spec.reachBonus;
            unit.knockbackPerDamage = spec.knockbackPerDamage;
            if (isPlayer) unit.selectionRing = CreateSelectionRing(root.transform, mats);
            if (arrow != null)
            {
                unit.arrowPrefab = arrow;
                unit.muzzle = CreateChild(root.transform, "Muzzle", ArrowReleasePoint);
            }
            // 矢のモデルがなければ、矢を放った瞬間に相手まで線を引く
            else if (ranged)
            {
                unit.shotLine = CreateShotLine(root.transform, isPlayer ? mats.shotPlayer : mats.shotEnemy);
            }

            var motion = root.AddComponent<UnitAnimator>();
            motion.animator = animator;
            motion.walkCycleSpeed = spec.walkCycleSpeed * spec.scale;
            motion.maxAttackPlaybackSpeed = spec.maxAttackPlaybackSpeed;
            motion.attacks = spec.attacks.Select(a => new UnitAnimator.AttackMotion
            {
                trigger = a.trigger,
                duration = FindClip(clips, a.clip).length,
                hitTime = a.hitTime,
                hitsAllInFront = a.hitsAllInFront,
                minTargets = a.minTargets,
                useChance = a.useChance,
                knockbackMultiplier = a.knockbackMultiplier,
            }).ToArray();
            motion.hasHurt = spec.hurt != null;
            motion.hasBlock = spec.block != null;
            motion.corpseDuration = FindClip(clips, spec.death).length + 1.5f;

            CreateHealthBar(root.transform, health, spec.healthBarY, spec.healthBarWidth, spec.healthBarThickness,
                mats.hpBack, isPlayer ? mats.hpPlayer : mats.hpEnemy);

            return SavePrefab(root, $"{PrefabDir}/{name}.prefab").GetComponent<Unit>();
        }

        /// <summary>弓兵や砲台が放つ矢。モデルがなければ null。</summary>
        static Arrow CreateArrowPrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ArrowModelPath);
            if (model == null)
            {
                Debug.LogWarning($"[RogueRTS] {ArrowModelPath} が見つからないため、弓兵と砲台は矢を飛ばさず線で攻撃します。");
                return null;
            }

            var root = new GameObject("Arrow");
            // 刺さる位置を測りやすいよう、矢じりの先端を原点にして +Z へ飛ばす
            Transform pivot = CreateChild(root.transform, "Model", new Vector3(0f, 0f, -ArrowModelLength));
            pivot.localRotation = Quaternion.Euler(0f, 180f, 0f);
            PrefabUtility.InstantiatePrefab(model, pivot);
            root.AddComponent<Arrow>();
            return SavePrefab(root, $"{ProjectilePrefabDir}/Arrow.prefab").GetComponent<Arrow>();
        }

        /// <summary>
        /// モデルの各部位を、FBX に書かれた基本姿勢に戻す。
        /// Unity は最初のクリップの 1 フレーム目をモデルの既定の姿勢にするが、Blockbench のモーションは
        /// キーのない部位を基本姿勢のままにする前提で作られている。Animator はキーのない部位を既定の姿勢で描くので、
        /// これをしないと、たとえば槍兵の槍が歩行・攻撃中も待機モーションの位置に残る。
        /// </summary>
        static void ApplyRestPose(Transform character, string modelPath)
        {
            Dictionary<string, (Vector3 position, Quaternion rotation)> pose = ReadFbxRestPose(modelPath);
            foreach (Transform part in character.GetComponentsInChildren<Transform>(true))
            {
                if (part == character || !pose.TryGetValue(part.name, out var rest)) continue;
                part.localPosition = rest.position;
                part.localRotation = rest.rotation;
            }
        }

        /// <summary>
        /// ASCII の FBX から、部位ごとの基本姿勢 (Lcl Translation / Lcl Rotation) を Unity の座標で読む。
        /// FBX 側は右手系で X 軸が Unity と逆向き、回転は X → Y → Z の順にかかる。バイナリの FBX なら空を返す。
        /// </summary>
        static Dictionary<string, (Vector3 position, Quaternion rotation)> ReadFbxRestPose(string modelPath)
        {
            var pose = new Dictionary<string, (Vector3, Quaternion)>();
            string text = File.ReadAllText(modelPath);
            if (!text.StartsWith("; FBX")) return pose;

            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            float scale = importer.globalScale * (importer.useFileScale ? importer.fileScale : 1f);
            const string modelPattern = "Model: \\d+, \"Model::([^\"]+)\", \"\\w+\" \\{(.*?)\\n\\t\\}";
            foreach (Match model in Regex.Matches(text, modelPattern, RegexOptions.Singleline))
            {
                string body = model.Groups[2].Value;
                Vector3 t = ReadFbxVector(body, "Lcl Translation");
                Vector3 r = ReadFbxVector(body, "Lcl Rotation");
                Quaternion rotation = Quaternion.AngleAxis(-r.z, Vector3.forward)
                    * Quaternion.AngleAxis(-r.y, Vector3.up)
                    * Quaternion.AngleAxis(r.x, Vector3.right);
                pose[model.Groups[1].Value] = (new Vector3(-t.x, t.y, t.z) * scale, rotation);
            }
            return pose;
        }

        /// <summary>FBX のノードの Properties70 から 3 成分の値を読む。書かれていなければ 0。</summary>
        static Vector3 ReadFbxVector(string nodeBody, string property)
        {
            Match match = Regex.Match(nodeBody,
                $"\"{property}\", \"{property}\", \"\", \"A\",([^,\\n]+),([^,\\n]+),([^,\\n]+)");
            if (!match.Success) return Vector3.zero;

            float Component(int i) => float.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture);
            return new Vector3(Component(1), Component(2), Component(3));
        }

        static AnimationClip[] LoadClips(string modelPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__")).ToArray();
        }

        static AnimationClip FindClip(AnimationClip[] clips, string suffix) => clips.First(c => c.name.EndsWith(suffix));

        static GameObject CreateSelectionRing(Transform parent, Mats mats)
        {
            GameObject ring = CreateVisual(PrimitiveType.Cylinder, "SelectionRing", parent, mats.ring, false);
            ring.transform.localScale = new Vector3(1.5f, 0.01f, 1.5f);
            ring.transform.localPosition = new Vector3(0f, 0.03f, 0f);
            ring.SetActive(false);
            return ring;
        }

        /// <summary>
        /// モデルのインポート設定で、名前が clipSuffixes のどれかで終わるクリップをループ再生にする。
        /// 設定を書き込んだあとは、そこに並んだクリップしか読み込まれないので、あとからモデルに足されたモーションも並びに加える。
        /// </summary>
        static void EnsureClipLoops(string modelPath, params string[] clipSuffixes)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            ModelImporterClipAnimation[] takes = importer.defaultClipAnimations;
            List<ModelImporterClipAnimation> clips = importer.clipAnimations.Length > 0
                ? importer.clipAnimations.ToList()
                : takes.ToList();
            ModelImporterClipAnimation[] added = takes.Where(t => clips.All(c => c.takeName != t.takeName)).ToArray();
            clips.AddRange(added);

            bool changed = added.Length > 0;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                if (!clipSuffixes.Any(suffix => clip.name.EndsWith(suffix)) || clip.loopTime) continue;
                clip.loopTime = true;
                changed = true;
            }
            if (!changed) return;

            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();
        }

        static AnimatorController LoadOrCreateController(CharacterSpec spec, AnimationClip[] clips)
        {
            if (controllersThisBuild.TryGetValue(spec.controllerPath, out AnimatorController existing)) return existing;

            EnsureClipLoops(spec.clipModel, new[] { spec.walk, spec.idle }.Where(s => s != null).ToArray());
            AnimatorController controller = CreateCharacterController(spec, LoadClips(spec.clipModel));
            controllersThisBuild[spec.controllerPath] = controller;
            return controller;
        }

        /// <summary>
        /// 待機 / 歩行 / 攻撃 (複数可) / 被弾 / 撃破 の Animator。待機専用のモーションがなければ、
        /// 基本の攻撃モーションの最初の姿勢 (構えた姿勢) で止めて待機にする。
        /// </summary>
        static AnimatorController CreateCharacterController(CharacterSpec spec, AnimationClip[] clips)
        {
            AssetDatabase.DeleteAsset(spec.controllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(spec.controllerPath);
            controller.AddParameter("Moving", AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = "WalkSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f,
            });
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = "AttackSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 1f,
            });
            foreach (AttackSpec attack in spec.attacks)
                controller.AddParameter(attack.trigger, AnimatorControllerParameterType.Trigger);
            if (spec.hurt != null) controller.AddParameter("Hurt", AnimatorControllerParameterType.Trigger);
            if (spec.block != null) controller.AddParameter("Block", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Die", AnimatorControllerParameterType.Trigger);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AnimatorState idle = machine.AddState("Idle");
            if (spec.idle != null)
            {
                idle.motion = FindClip(clips, spec.idle);
            }
            else
            {
                idle.motion = FindClip(clips, spec.attacks[spec.attacks.Length - 1].clip);
                idle.speed = 0f;
            }
            machine.defaultState = idle;

            AnimatorState walk = machine.AddState("Walk");
            walk.motion = FindClip(clips, spec.walk);
            walk.speedParameterActive = true;
            walk.speedParameter = "WalkSpeed";

            AnimatorStateTransition startWalking = idle.AddTransition(walk);
            startWalking.AddCondition(AnimatorConditionMode.If, 0f, "Moving");
            startWalking.hasExitTime = false;
            startWalking.duration = 0.15f;

            AnimatorStateTransition stopWalking = walk.AddTransition(idle);
            stopWalking.AddCondition(AnimatorConditionMode.IfNot, 0f, "Moving");
            stopWalking.hasExitTime = false;
            stopWalking.duration = 0.15f;

            foreach (AttackSpec attack in spec.attacks)
            {
                AnimatorState state = machine.AddState(attack.trigger);
                state.motion = FindClip(clips, attack.clip);
                state.speedParameterActive = true;
                state.speedParameter = "AttackSpeed";
                AddTriggeredState(machine, state, attack.trigger, false);
                ReturnToIdle(state, idle, 0.95f);
            }

            if (spec.hurt != null)
            {
                AnimatorState hurt = machine.AddState("Hurt");
                hurt.motion = FindClip(clips, spec.hurt);
                AddTriggeredState(machine, hurt, "Hurt", true);
                ReturnToIdle(hurt, idle, 0.9f);
            }

            if (spec.block != null)
            {
                AnimatorState block = machine.AddState("Block");
                block.motion = FindClip(clips, spec.block);
                AddTriggeredState(machine, block, "Block", true);
                ReturnToIdle(block, idle, 0.9f);
            }

            AnimatorState death = machine.AddState("Die");
            death.motion = FindClip(clips, spec.death);
            AddTriggeredState(machine, death, "Die", false);

            return controller;
        }

        static void AddTriggeredState(AnimatorStateMachine machine, AnimatorState state, string trigger, bool canRestart)
        {
            AnimatorStateTransition transition = machine.AddAnyStateTransition(state);
            transition.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            transition.canTransitionToSelf = canRestart;
            transition.duration = 0.08f;
        }

        static void ReturnToIdle(AnimatorState state, AnimatorState idle, float exitTime)
        {
            AnimatorStateTransition transition = state.AddTransition(idle);
            transition.hasExitTime = true;
            transition.exitTime = exitTime;
            transition.duration = 0.1f;
        }

        static Unit CreateUnitPrefab(
            string name, UnitData data, Team team, PrimitiveType shape, float width, float height, Mats mats)
        {
            bool isPlayer = team == Team.Player;
            var root = new GameObject(name);

            // Capsule と Cylinder はスケール1で高さ2
            GameObject body = CreateVisual(shape, "Body", root.transform, isPlayer ? mats.player : mats.enemy, true);
            body.transform.localScale = new Vector3(width, height * 0.5f, width);
            body.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);

            // 向きが分かるよう前面に目印を付ける
            GameObject visor = CreateVisual(PrimitiveType.Cube, "Visor", root.transform, mats.dark, true);
            visor.transform.localScale = new Vector3(width * 0.6f, 0.18f, 0.2f);
            visor.transform.localPosition = new Vector3(0f, height * 0.75f, width * 0.45f);

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.5f;
            agent.height = 2f;
            agent.speed = data.moveSpeed;
            agent.angularSpeed = 720f;
            agent.acceleration = 30f;
            agent.stoppingDistance = 0.1f;

            var health = root.AddComponent<Health>();
            health.team = team;
            health.maxHp = data.maxHp;
            health.radius = 0.5f;

            var unit = root.AddComponent<Unit>();
            unit.data = data;

            if (isPlayer) unit.selectionRing = CreateSelectionRing(root.transform, mats);

            CreateHealthBar(root.transform, health, height + 0.5f, 1.2f, 0.14f,
                mats.hpBack, isPlayer ? mats.hpPlayer : mats.hpEnemy);

            unit.shotLine = CreateShotLine(root.transform, isPlayer ? mats.shotPlayer : mats.shotEnemy);

            return SavePrefab(root, $"{PrefabDir}/{name}.prefab").GetComponent<Unit>();
        }

        static ShotLine CreateShotLine(Transform parent, Material material)
        {
            var lineObject = new GameObject("ShotLine");
            lineObject.transform.SetParent(parent, false);
            var line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.positionCount = 2;
            line.startWidth = line.endWidth = 0.07f;
            line.useWorldSpace = true;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            return lineObject.AddComponent<ShotLine>();
        }

        static GameObject SavePrefab(GameObject root, string path)
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ---------------------------------------------------------------- 建築物

        static BuildingData[] CreateBuildings(Mats mats)
        {
            BuildingData wall = LoadOrCreateData<BuildingData>($"{BuildingDataDir}/Wall.asset", d =>
            {
                d.displayName = "防壁"; d.type = BuildingType.Wall; d.cost = 15;
            });
            BuildingData gate = LoadOrCreateData<BuildingData>($"{BuildingDataDir}/Gate.asset", d =>
            {
                d.displayName = "門"; d.type = BuildingType.Gate; d.cost = 30;
            });
            BuildingData turret = LoadOrCreateData<BuildingData>($"{BuildingDataDir}/Turret.asset", d =>
            {
                d.displayName = "タレット"; d.type = BuildingType.Turret; d.cost = 150;
                // 重いクロスボウなので 4 秒に1発。そのぶん1発を重くしている
                d.attackDamage = 30f; d.attackRange = 9f; d.attackInterval = 4f;
            });

            SetPrefab(wall, CreateWallPrefab(mats));
            SetPrefab(gate, CreateGatePrefab(mats));
            SetPrefab(turret, CreateTurretPrefab(mats, turret));
            AssignBuildingIcons();
            return new[] { wall, gate, turret };
        }

        /// <summary>
        /// 防壁・門・タレットのプレハブだけを作り直す (シーンは触らない)。
        /// プレハブのパスは変わらないので、シーンや建築データからの参照はそのまま新しいプレハブを指す。
        /// </summary>
        public static void BuildBuildingPrefabs()
        {
            foreach (string dir in new[] { MaterialDir, BuildingPrefabDir, ProjectilePrefabDir, BuildingDataDir, AnimationDir })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            CreateBuildings(CreateMaterials());
            AssetDatabase.SaveAssets();
        }

        static void SetPrefab(BuildingData data, GameObject prefab)
        {
            if (data.prefab == prefab) return;

            data.prefab = prefab;
            EditorUtility.SetDirty(data);
        }

        /// <summary>1マスを塞ぐ防壁。コライダーごと NavMesh から除外され、誰も通れない。</summary>
        static GameObject CreateWallPrefab(Mats mats)
        {
            const float size = BuildGrid.CellSize, height = 2f;
            var root = new GameObject("Wall");

            GameObject model = GameObject.CreatePrimitive(PrimitiveType.Cube);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            model.transform.localScale = new Vector3(size, height, size);
            model.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            model.GetComponent<MeshRenderer>().sharedMaterial = mats.wall;
            MarkNotWalkable(model);

            // 四隅の胸壁 (見た目だけ)
            for (int i = 0; i < 4; i++)
            {
                GameObject merlon = CreateVisual(PrimitiveType.Cube, "Merlon", root.transform, mats.wall, true);
                merlon.transform.localScale = new Vector3(0.5f, 0.4f, 0.5f);
                merlon.transform.localPosition =
                    new Vector3(i % 2 == 0 ? -0.75f : 0.75f, height + 0.2f, i < 2 ? -0.75f : 0.75f);
            }

            var structure = root.AddComponent<Structure>();
            structure.type = BuildingType.Wall;
            structure.turretMount = CreateChild(root.transform, "TurretMount", new Vector3(0f, height, 0f));
            return SavePrefab(root, $"{BuildingPrefabDir}/Wall.prefab");
        }

        /// <summary>
        /// 敵味方とも通れる門。コライダーを持たないので NavMesh には影響せず、防壁の列に開けた通り道になる。
        /// ただの隙間と違い、上にタレットを載せられる。
        /// </summary>
        static GameObject CreateGatePrefab(Mats mats)
        {
            const float size = BuildGrid.CellSize, height = 2f, roof = 0.4f;
            var root = new GameObject("Gate");

            for (int i = 0; i < 4; i++)
            {
                GameObject pillar = CreateVisual(PrimitiveType.Cube, "Pillar", root.transform, mats.wall, true);
                pillar.transform.localScale = new Vector3(0.5f, height, 0.5f);
                pillar.transform.localPosition =
                    new Vector3(i % 2 == 0 ? -0.75f : 0.75f, height * 0.5f, i < 2 ? -0.75f : 0.75f);
            }

            GameObject roofModel = CreateVisual(PrimitiveType.Cube, "Roof", root.transform, mats.player, true);
            roofModel.transform.localScale = new Vector3(size, roof, size);
            roofModel.transform.localPosition = new Vector3(0f, height + roof * 0.5f, 0f);

            var structure = root.AddComponent<Structure>();
            structure.type = BuildingType.Gate;
            structure.turretMount = CreateChild(root.transform, "TurretMount", new Vector3(0f, height + roof, 0f));
            return SavePrefab(root, $"{BuildingPrefabDir}/Gate.prefab");
        }

        /// <summary>
        /// crossbow_turret のモデルを使ったタレット。台座・クロスボウ・砲手をまとめて目標の方向へ回し、
        /// 発射モーションに合わせて撃つ。モデルがなければ簡易な見た目で作る。
        /// </summary>
        static GameObject CreateTurretPrefab(Mats mats, BuildingData data)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(TurretModelPath);
            AnimationClip shoot = model != null ? LoadClips(TurretModelPath).FirstOrDefault() : null;
            if (shoot == null)
            {
                Debug.LogWarning($"[RogueRTS] {TurretModelPath} が見つからないため、タレットは簡易な見た目になります。");
                return CreatePrimitiveTurretPrefab(mats, data);
            }

            var root = new GameObject("Turret");
            Transform head = CreateChild(root.transform, "Head", Vector3.zero);

            // モデルは -Z を向いているので反転し、マスに収まる大きさにする
            Transform pivot = CreateChild(head, "Model", Vector3.zero);
            pivot.localRotation = Quaternion.Euler(0f, 180f, 0f);
            pivot.localScale = Vector3.one * TurretModelScale;
            var crossbowTurret = (GameObject)PrefabUtility.InstantiatePrefab(model, pivot);
            var animator = crossbowTurret.AddComponent<Animator>();
            animator.runtimeAnimatorController = CreateTurretController(shoot);
            animator.applyRootMotion = false;

            // 矢はクロスボウの先端から出す (クロスボウは発射の反動で動くので、その子にする)
            Transform crossbow = crossbowTurret.GetComponentsInChildren<Transform>().First(t => t.name == "crossbow");
            Transform muzzle = CreateChild(crossbow, "Muzzle", new Vector3(0f, 0.13f, -0.8f));

            var turret = root.AddComponent<Turret>();
            turret.data = data;
            turret.head = head;
            turret.muzzle = muzzle;
            turret.arrowPrefab = CreateArrowPrefab();
            // 矢のモデルがなければ、撃った瞬間に相手まで線を引く
            if (turret.arrowPrefab == null) turret.shotLine = CreateShotLine(root.transform, mats.shotPlayer);
            turret.animator = animator;
            turret.clipLength = shoot.length;
            turret.fireStart = TurretFireStart;
            turret.releaseTime = TurretReleaseTime;
            turret.loadedTime = TurretLoadedTime;
            return SavePrefab(root, $"{BuildingPrefabDir}/Turret.prefab");
        }

        /// <summary>
        /// 発射モーション1つだけの Animator。どの時刻から再生するか・止めるかは Turret が直接指定する。
        /// </summary>
        static AnimatorController CreateTurretController(AnimationClip shoot)
        {
            AssetDatabase.DeleteAsset(TurretControllerPath);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(TurretControllerPath);
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = "ShootSpeed", type = AnimatorControllerParameterType.Float, defaultFloat = 0f,
            });

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState state = machine.AddState("Shoot");
            state.motion = shoot;
            state.speedParameterActive = true;
            state.speedParameter = "ShootSpeed";
            machine.defaultState = state;
            return controller;
        }

        static GameObject CreatePrimitiveTurretPrefab(Mats mats, BuildingData data)
        {
            var root = new GameObject("Turret");

            GameObject pedestal = CreateVisual(PrimitiveType.Cylinder, "Base", root.transform, mats.dark, true);
            pedestal.transform.localScale = new Vector3(1.1f, 0.08f, 1.1f);
            pedestal.transform.localPosition = new Vector3(0f, 0.08f, 0f);

            Transform head = CreateChild(root.transform, "Head", new Vector3(0f, 0.16f, 0f));

            GameObject gunner = CreateVisual(PrimitiveType.Capsule, "Gunner", head, mats.player, true);
            gunner.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            gunner.transform.localPosition = new Vector3(0f, 0.5f, -0.2f);

            GameObject barrel = CreateVisual(PrimitiveType.Cube, "Barrel", head, mats.dark, true);
            barrel.transform.localScale = new Vector3(0.2f, 0.2f, 1f);
            barrel.transform.localPosition = new Vector3(0f, 0.55f, 0.55f);

            var turret = root.AddComponent<Turret>();
            turret.data = data;
            turret.head = head;
            turret.muzzle = CreateChild(head, "Muzzle", new Vector3(0f, 0.55f, 1.05f));
            turret.shotLine = CreateShotLine(root.transform, mats.shotPlayer);
            return SavePrefab(root, $"{BuildingPrefabDir}/Turret.prefab");
        }

        /// <summary>上面に NavMesh が張られないよう、このオブジェクトを歩行不可として扱わせる。</summary>
        static void MarkNotWalkable(GameObject go)
        {
            var modifier = go.AddComponent<NavMeshModifier>();
            modifier.overrideArea = true;
            modifier.area = NotWalkableArea;
        }

        /// <summary>コライダーなしの見た目専用プリミティブを作る。</summary>
        static GameObject CreateVisual(PrimitiveType type, string name, Transform parent, Material material, bool shadows)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);

            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            if (!shadows)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            return go;
        }

        static void CreateHealthBar(
            Transform parent, Health health, float y, float width, float thickness, Material back, Material fill)
        {
            var bar = new GameObject("HealthBar");
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = new Vector3(0f, y, 0f);
            bar.transform.localScale = new Vector3(width, thickness, 1f);

            CreateVisual(PrimitiveType.Quad, "Background", bar.transform, back, false);
            GameObject fillQuad = CreateVisual(PrimitiveType.Quad, "Fill", bar.transform, fill, false);

            var healthBar = bar.AddComponent<HealthBar>();
            healthBar.health = health;
            healthBar.fill = fillQuad.transform;
        }

        static RewardData[] CreateRewards()
        {
            var rewards = new List<RewardData>();
            void Add(string file, string title, string description, RewardType type, float value)
            {
                rewards.Add(LoadOrCreateData<RewardData>($"{RewardDir}/Reward_{file}.asset", r =>
                {
                    r.title = title;
                    r.description = description;
                    r.type = type;
                    r.value = value;
                }));
            }

            Add("AttackUp", "鍛え上げた刃", "全ユニットの攻撃力 +20%", RewardType.AttackUp, 0.2f);
            Add("HealthUp", "頑丈な鎧", "全ユニットの最大HP +25%", RewardType.HealthUp, 0.25f);
            Add("RangeUp", "鷹の目", "全ユニットの射程 +15%", RewardType.RangeUp, 0.15f);
            Add("AttackSpeedUp", "連撃の心得", "全ユニットの攻撃速度 +15%", RewardType.AttackSpeedUp, 0.15f);
            Add("MoveSpeedUp", "俊足", "全ユニットの移動速度 +20%", RewardType.MoveSpeedUp, 0.2f);
            Add("BuildSpeedUp", "熟練の教官", "ユニットの生産速度 +30%", RewardType.BuildSpeedUp, 0.3f);
            Add("IncomeUp", "交易路", "収入 +3 ゴールド/秒", RewardType.IncomeUp, 3f);
            Add("GoldNow", "埋蔵金", "今すぐ 200 ゴールドを得る", RewardType.GoldNow, 200f);
            Add("Reinforcements", "援軍", "兵士 3 体が即座に参戦する", RewardType.Reinforcements, 3f);
            Add("RepairBase", "城壁の修復", "拠点を全回復し、最大HP +25%", RewardType.RepairBase, 0.25f);
            return rewards.ToArray();
        }

        // ---------------------------------------------------------------- シーン

        static void SetupLighting()
        {
            // スカイボックスの焼き込みに頼らず、単色の環境光にしておく
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.62f);

            var light = Object.FindFirstObjectByType<Light>();
            if (light == null) return;

            light.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            light.shadows = LightShadows.Soft;
        }

        static Camera SetupCamera()
        {
            Camera cam = Camera.main;
            cam.farClipPlane = 200f;
            cam.gameObject.AddComponent<RtsCameraController>().ApplyTransform();
            return cam;
        }

        static void CreateEnvironment(Mats mats)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(8f, 1f, 8f); // 80 x 80
            ground.GetComponent<MeshRenderer>().sharedMaterial = mats.ground;

            var obstacles = new GameObject("Obstacles").transform;
            CreateRock(obstacles, mats, new Vector3(-13f, 0f, -6f), new Vector3(7f, 2.5f, 3f), 15f);
            CreateRock(obstacles, mats, new Vector3(11f, 0f, 2f), new Vector3(3f, 2.5f, 9f), -10f);
            CreateRock(obstacles, mats, new Vector3(-4f, 0f, 13f), new Vector3(5f, 2.5f, 4f), 30f);
            CreateRock(obstacles, mats, new Vector3(20f, 0f, -14f), new Vector3(5f, 2.5f, 3f), -25f);
            CreateRock(obstacles, mats, new Vector3(-23f, 0f, 10f), new Vector3(3f, 2.5f, 7f), 5f);
            CreateRock(obstacles, mats, new Vector3(24f, 0f, 18f), new Vector3(4f, 2.5f, 4f), 45f);
        }

        static void CreateRock(Transform parent, Mats mats, Vector3 position, Vector3 size, float yaw)
        {
            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rock.name = "Rock";
            rock.transform.SetParent(parent, false);
            rock.transform.SetPositionAndRotation(position + Vector3.up * (size.y * 0.5f), Quaternion.Euler(0f, yaw, 0f));
            rock.transform.localScale = size;
            rock.GetComponent<MeshRenderer>().sharedMaterial = mats.rock;
            MarkNotWalkable(rock);
        }

        /// <summary>ゲーム開始時にステージへ木を生やす ForestGenerator を systems に付ける。</summary>
        public static ForestGenerator AddForest(GameObject systems, PlayerBase playerBase)
        {
            var forest = systems.AddComponent<ForestGenerator>();
            forest.treePrefab = BuildTreePrefab();
            forest.playerBase = playerBase.transform;
            return forest;
        }

        /// <summary>
        /// ステージに生やす木。当たり判定はマス 1 つ分の箱で、NavMesh の障害物になる。
        /// 矢は木を越えていくよう、レイキャストに当たらないレイヤーに置く。モデルがなければ null。
        /// 向きや大きさのばらつきは ForestGenerator が Model に付ける。
        /// </summary>
        public static GameObject BuildTreePrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(TreeModelPath);
            if (model == null)
            {
                Debug.LogWarning($"[RogueRTS] {TreeModelPath} が見つからないため、ステージに木を生やしません。");
                return null;
            }

            Directory.CreateDirectory(EnvironmentPrefabDir);
            var root = new GameObject("Tree") { layer = IgnoreRaycastLayer };
            var box = root.AddComponent<BoxCollider>();
            float footprint = BuildGrid.CellSize - 0.1f;
            box.size = new Vector3(footprint, TreeColliderHeight, footprint);
            box.center = new Vector3(0f, TreeColliderHeight * 0.5f, 0f);
            MarkNotWalkable(root);

            Transform pivot = CreateChild(root.transform, "Model", Vector3.zero);
            PrefabUtility.InstantiatePrefab(model, pivot);
            return SavePrefab(root, $"{EnvironmentPrefabDir}/Tree.prefab");
        }

        static PlayerBase CreatePlayerBase(Mats mats)
        {
            var root = new GameObject("PlayerBase");
            root.transform.position = BasePosition;

            // 当たり判定だけを持つ。コライダー付きなので NavMesh のベイクで障害物として扱われる
            var model = new GameObject("Model", typeof(BoxCollider));
            model.transform.SetParent(root.transform, false);
            model.transform.localScale = new Vector3(4f, 3f, 4f);
            model.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            MarkNotWalkable(model);
            CreateFort(root.transform, mats);

            var health = root.AddComponent<Health>();
            health.team = Team.Player;
            health.maxHp = 1000f;
            health.radius = 2.5f;
            health.destroyOnDeath = false;
            CreateHealthBar(root.transform, health, BaseHealthBarY, 4.5f, 0.35f, mats.hpBack, mats.hpPlayer);

            var playerBase = root.AddComponent<PlayerBase>();
            playerBase.spawnPoint = CreateChild(root.transform, "SpawnPoint", new Vector3(0f, 0f, 4.5f));
            playerBase.rallyPoint = CreateChild(root.transform, "RallyPoint", new Vector3(0f, 0f, 9f));
            return playerBase;
        }

        /// <summary>
        /// 拠点の見た目。四隅に塔を立て、防壁と同じ胸壁 (上部の凸凹) を載せた砦にする。
        /// 当たり判定は Model (4x4) が持つので、見た目もその内側に収める。
        /// </summary>
        static Transform CreateFort(Transform root, Mats mats)
        {
            const float half = 2f, wallInset = 0.3f, wallHeight = 2.6f;
            const float towerSize = 1.2f, towerHeight = 3.6f;
            var merlonSize = new Vector3(0.4f, 0.45f, 0.4f);

            Transform fort = CreateChild(root, "Fort", Vector3.zero);
            void Block(string name, Material material, Vector3 size, Vector3 center)
            {
                GameObject block = CreateVisual(PrimitiveType.Cube, name, fort, material, true);
                block.transform.localScale = size;
                block.transform.localPosition = center;
            }

            // 塔の間の壁は少し引っ込めて、塔が張り出して見えるようにする
            float wallHalf = half - wallInset;
            Block("Keep", mats.wall, new Vector3(wallHalf * 2f, wallHeight, wallHalf * 2f),
                new Vector3(0f, wallHeight * 0.5f, 0f));

            float towerCenter = half - towerSize * 0.5f;
            float merlonOffset = (towerSize - merlonSize.x) * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? -towerCenter : towerCenter;
                float z = i < 2 ? -towerCenter : towerCenter;
                Block("Tower", mats.wall, new Vector3(towerSize, towerHeight, towerSize),
                    new Vector3(x, towerHeight * 0.5f, z));

                // 塔の上の四隅の胸壁
                for (int j = 0; j < 4; j++)
                {
                    Block("Merlon", mats.wall, merlonSize, new Vector3(
                        x + (j % 2 == 0 ? -merlonOffset : merlonOffset),
                        towerHeight + merlonSize.y * 0.5f,
                        z + (j < 2 ? -merlonOffset : merlonOffset)));
                }
            }

            // 壁の上の胸壁。各辺の塔と塔の間に2つずつ
            float edge = wallHalf - merlonSize.z * 0.5f, merlonY = wallHeight + merlonSize.y * 0.5f;
            foreach (float side in new[] { -edge, edge })
            {
                foreach (float along in new[] { -0.4f, 0.4f })
                {
                    Block("Merlon", mats.wall, merlonSize, new Vector3(along, merlonY, side));
                    Block("Merlon", mats.wall, merlonSize, new Vector3(side, merlonY, along));
                }
            }

            // ユニットが出ていく正面 (+Z) に門、カメラから見える背面に自軍色の垂れ幕
            Block("Door", mats.dark, new Vector3(1f, 1.5f, 0.1f), new Vector3(0f, 0.75f, wallHalf + 0.05f));
            Block("Banner", mats.player, new Vector3(0.7f, 1.1f, 0.06f), new Vector3(0f, 1.55f, -wallHalf - 0.03f));

            // 屋上の旗
            GameObject pole = CreateVisual(PrimitiveType.Cylinder, "FlagPole", fort, mats.dark, true);
            pole.transform.localScale = new Vector3(0.08f, 1f, 0.08f);
            pole.transform.localPosition = new Vector3(0f, wallHeight + 1f, 0f);
            Block("Flag", mats.player, new Vector3(0.9f, 0.55f, 0.05f), new Vector3(0.49f, wallHeight + 1.65f, 0f));
            return fort;
        }

        /// <summary>
        /// 今のシーンの拠点の見た目を砦に作り直す (シーン全体は作り直さない)。
        /// 当たり判定の Model はそのまま使うので、NavMesh を焼き直す必要はない。
        /// </summary>
        public static void RebuildPlayerBaseFort(PlayerBase playerBase)
        {
            Transform root = playerBase.transform;
            Transform model = root.Find("Model");
            if (model.TryGetComponent(out MeshRenderer renderer)) Object.DestroyImmediate(renderer);
            if (model.TryGetComponent(out MeshFilter filter)) Object.DestroyImmediate(filter);

            Transform oldFort = root.Find("Fort");
            if (oldFort != null) Object.DestroyImmediate(oldFort.gameObject);
            CreateFort(root, CreateMaterials()).SetSiblingIndex(model.GetSiblingIndex() + 1);

            Transform bar = root.Find("HealthBar");
            if (bar != null) bar.localPosition = new Vector3(0f, BaseHealthBarY, 0f);

            EditorSceneManager.MarkSceneDirty(playerBase.gameObject.scene);
        }

        static Transform CreateChild(Transform parent, string name, Vector3 localPosition)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = localPosition;
            return child;
        }

        static NavMeshSurface BakeNavMesh()
        {
            var surface = new GameObject("NavMesh").AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            // 建築のたびに実行中に更新するので、変更箇所だけ作り直せるようタイルを細かくする
            surface.overrideTileSize = true;
            surface.tileSize = 64;
            surface.BuildNavMesh();

            // ベイク結果をアセットとして保存し、シーンから参照させる
            AssetDatabase.DeleteAsset(NavMeshPath);
            AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);
            return surface;
        }

        static Transform[] CreateEnemySpawnPoints()
        {
            var parent = new GameObject("EnemySpawnPoints").transform;
            Vector3[] positions =
            {
                new Vector3(-24f, 0f, 33f), new Vector3(0f, 0f, 35f), new Vector3(24f, 0f, 33f),
            };

            var points = new Transform[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                points[i] = CreateChild(parent, $"SpawnPoint{i}", positions[i]);
                points[i].rotation = Quaternion.Euler(0f, 180f, 0f);
            }
            return points;
        }

        static MoveMarker CreateMoveMarker(Mats mats)
        {
            GameObject go = CreateVisual(PrimitiveType.Cylinder, "MoveMarker", null, mats.marker, false);
            var marker = go.AddComponent<MoveMarker>();
            go.SetActive(false);
            return marker;
        }

        static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
            {
                if (existing.path != ScenePath) scenes.Add(existing);
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ---------------------------------------------------------------- UI

        const string HelpTextContent =
            "WASD / 矢印: カメラ移動　　ホイール: ズーム\n" +
            "Ctrl + マウス: カメラの向き　　G: 通常のカメラに戻す\n" +
            "F 長押し: スロー (味方に合わせて離すとその視点へ)\n" +
            "左クリック / ドラッグ: 選択 (Shift で追加)\n" +
            "右クリック: 移動　　1〜5: ユニット生産\n" +
            "Z / X / C: 防壁・門・タレットの設置 (右クリックで終了)\n" +
            "Space: 一時停止\n" +
            "F1: UI の表示切り替え　　F2: ダメージ表示の切り替え";
        static readonly Vector2 HelpTextSize = new Vector2(560f, 178f);

        /// <summary>
        /// 今のシーンの HUD に、一時停止の表示 (画面上部の帯) と、F 長押しで視点を移すユニットの印・カメラのヒントを入れ、
        /// 操作説明を更新する (シーン全体は作り直さない)。すでにあれば作り直す。
        /// </summary>
        public static void AddCameraUi(HudController hud, GameManager game, Camera cam)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            Transform canvas = hud.transform;

            // 一時停止の表示は、報酬画面や敗北画面より奥に置く
            Transform oldPause = canvas.Find("PausePanel");
            if (oldPause != null) Object.DestroyImmediate(oldPause.gameObject);
            hud.pausePanel = CreatePausePanel(canvas);
            hud.pausePanel.transform.SetSiblingIndex(canvas.Find("RewardScreen").GetSiblingIndex());

            foreach (string name in new[] { "FocusMarker", "CameraHintText" })
            {
                Transform old = canvas.Find(name);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            // 印は選択枠と同じく、ほかの UI より奥に描く
            TMP_Text marker = CreateFocusMarker(canvas);
            marker.transform.SetSiblingIndex(canvas.Find("SelectionBox").GetSiblingIndex() + 1);
            var speedPanel = (RectTransform)canvas.Find("SpeedPanel");
            TMP_Text hint = CreateCameraHint(canvas, speedPanel);
            hint.transform.SetSiblingIndex(speedPanel.GetSiblingIndex() + 1);

            if (!game.TryGetComponent(out UnitFocusController focus))
                focus = game.gameObject.AddComponent<UnitFocusController>();
            focus.cam = cam;
            focus.cameraController = cam.GetComponent<RtsCameraController>();
            focus.marker = marker;
            focus.hintText = hint;

            var help = (RectTransform)canvas.Find("HelpText");
            help.GetComponent<TMP_Text>().text = HelpTextContent;
            help.sizeDelta = HelpTextSize;

            EditorUtility.SetDirty(hud);
            EditorUtility.SetDirty(focus);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 今のシーンの HUD にダメージの数字の雛形を入れ、F2 で切り替える DamageIndicator を付けて、
        /// 操作説明を更新する (シーン全体は作り直さない)。すでにあれば作り直す。
        /// </summary>
        public static void AddDamageIndicatorUi(HudController hud, GameManager game, Camera cam)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            Transform canvas = hud.transform;

            Transform old = canvas.Find("DamageNumber");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            TMP_Text number = CreateDamageNumber(canvas);
            number.transform.SetSiblingIndex(canvas.Find("FocusMarker").GetSiblingIndex() + 1);

            if (!game.TryGetComponent(out DamageIndicator damage))
                damage = game.gameObject.AddComponent<DamageIndicator>();
            damage.cam = cam;
            damage.template = number;

            var help = (RectTransform)canvas.Find("HelpText");
            help.GetComponent<TMP_Text>().text = HelpTextContent;
            help.sizeDelta = HelpTextSize;

            EditorUtility.SetDirty(damage);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 一時停止中に画面上部へ出す表示。画面全体は暗くせず、文字の後ろにだけ半透明の灰色の帯を敷く。
        /// ボスの体力バー (上端から 126px まで) とステージの告知 (上端から 198px 以降) の間に置く。
        /// </summary>
        static GameObject CreatePausePanel(Transform canvas)
        {
            RectTransform panel = CreatePanel("PausePanel", canvas, new Color(0.3f, 0.3f, 0.3f, 0.6f));
            Place(panel, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -132f), new Vector2(360f, 60f));
            panel.GetComponent<Image>().raycastTarget = false;

            TMP_Text title = CreateText("PauseText", panel, "一時停止中", 40, TextAlignmentOptions.Center, Color.white);
            Stretch(title.rectTransform);
            title.fontStyle = FontStyles.Bold;
            title.fontSharedMaterial = uiShadowMaterial;

            panel.gameObject.SetActive(false);
            return panel.gameObject;
        }

        /// <summary>F を押している間、視点を移す候補のユニットの頭上に出す印 (名前と ▼)。</summary>
        static TMP_Text CreateFocusMarker(Transform canvas)
        {
            TMP_Text marker = CreateText("FocusMarker", canvas, "", 26, TextAlignmentOptions.Bottom, new Color(0.45f, 0.9f, 1f));
            // 下端の中央 (▼ の先) がユニットの頭上に来るようにする
            Place(marker.rectTransform, Vector2.zero, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(300f, 80f));
            marker.fontStyle = FontStyles.Bold;
            marker.fontSharedMaterial = uiShadowMaterial;
            marker.gameObject.SetActive(false);
            return marker;
        }

        /// <summary>ダメージの数字の雛形。中央がダメージの入った位置に来る。色と内容は DamageIndicator が決める。</summary>
        static TMP_Text CreateDamageNumber(Transform canvas)
        {
            TMP_Text number = CreateText("DamageNumber", canvas, "", 34, TextAlignmentOptions.Center, Color.white);
            Place(number.rectTransform, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160f, 50f));
            number.fontStyle = FontStyles.Bold;
            // 緑の文字が緑の地面に溶け込まないよう、黒く縁取る
            number.fontSharedMaterial = LoadOrCreateOutlinePreset(uiFont);
            number.gameObject.SetActive(false);
            return number;
        }

        /// <summary>速度パネルのすぐ上に出す、今のカメラのモードと操作のヒント。</summary>
        static TMP_Text CreateCameraHint(Transform canvas, RectTransform speedPanel)
        {
            TMP_Text hint = CreateText("CameraHintText", canvas, "", 24, TextAlignmentOptions.Bottom, Color.white);
            float y = speedPanel.anchoredPosition.y + speedPanel.sizeDelta.y + 8f;
            Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, y), new Vector2(1100f, 40f));
            hint.fontSharedMaterial = uiShadowMaterial;
            return hint;
        }

        static UiRefs CreateUi(int produceButtonCount, int buildButtonCount)
        {
            var refs = new UiRefs();
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);

            var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.layer = LayerMask.NameToLayer("UI");
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            Transform canvas = canvasObject.transform;

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            // 選択枠 (一番奥に描く)
            RectTransform box = CreatePanel("SelectionBox", canvas, new Color(0.3f, 1f, 0.4f, 0.18f));
            Place(box, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            box.GetComponent<Image>().raycastTarget = false;
            var outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.3f, 1f, 0.4f, 0.9f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            refs.selectionBox = box;
            refs.focusMarker = CreateFocusMarker(canvas);
            refs.damageNumber = CreateDamageNumber(canvas);

            var hud = canvasObject.AddComponent<HudController>();

            // 左上: 状況表示
            RectTransform info = CreatePanel("InfoPanel", canvas, new Color(0f, 0f, 0f, 0.55f));
            Place(info, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(460f, 186f));
            string[] infoNames = { "GoldText", "CoinText", "StageText", "WaveText", "BaseHpText" };
            var infoTexts = new TMP_Text[infoNames.Length];
            for (int i = 0; i < infoNames.Length; i++)
            {
                infoTexts[i] = CreateText(infoNames[i], info, "", 24, TextAlignmentOptions.Left, Color.white);
                Place(infoTexts[i].rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(14f, -8f - i * 34f), new Vector2(432f, 34f));
            }
            hud.goldText = infoTexts[0];
            hud.coinText = infoTexts[1];
            hud.stageText = infoTexts[2];
            hud.waveText = infoTexts[3];
            hud.baseHpText = infoTexts[4];

            // 下中央: 生産と、そのすぐ上に速度の切り替え
            RectTransform production = CreateProductionPanel(canvas, hud, produceButtonCount);
            RectTransform speed = CreateSpeedPanel(canvas, hud, production);
            hud.queueText = CreateQueueText(canvas, production);
            refs.cameraHint = CreateCameraHint(canvas, speed);

            // 右下: 建築
            const float buttonGap = 16f;
            float buildWidth = buttonGap + buildButtonCount * (IconButtonSize + buttonGap);
            RectTransform buildPanel = CreatePanel("BuildPanel", canvas, new Color(0f, 0f, 0f, 0.55f));
            Place(buildPanel, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-16f, 16f), new Vector2(buildWidth, 145f));

            hud.buildButtons = new Button[buildButtonCount];
            hud.buildLabels = new TMP_Text[buildButtonCount];
            for (int i = 0; i < buildButtonCount; i++)
            {
                hud.buildButtons[i] = CreateButton($"BuildButton{i}", buildPanel, "", 22, out TMP_Text label);
                hud.buildLabels[i] = label;
                AddIconButtonFace(hud.buildButtons[i], label);
            }
            PlaceBuildButtons(buildPanel, hud.buildButtons);

            hud.buildHintText = CreateText("BuildHintText", buildPanel, "", 20, TextAlignmentOptions.Left, Color.white);
            Place(hud.buildHintText.rectTransform, Vector2.zero, Vector2.zero,
                new Vector2(buttonGap, 8f), new Vector2(buildWidth - buttonGap * 2f, 32f));

            // 左下: 操作説明
            TMP_Text help = CreateText("HelpText", canvas, HelpTextContent,
                18, TextAlignmentOptions.BottomLeft, new Color(1f, 1f, 1f, 0.9f));
            Place(help.rectTransform, Vector2.zero, Vector2.zero, new Vector2(16f, 16f), HelpTextSize);
            // 地面の上に直接出る文字は、影付きのマテリアルで読みやすくする
            help.fontSharedMaterial = uiShadowMaterial;

            // 中央上: ステージ/ウェーブの告知
            hud.bannerText = CreateText("BannerText", canvas, "", 52, TextAlignmentOptions.Center, new Color(1f, 0.9f, 0.35f));
            Place(hud.bannerText.rectTransform, new Vector2(0.5f, 0.78f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1000f, 80f));
            hud.bannerText.fontStyle = FontStyles.Bold;
            hud.bannerText.fontSharedMaterial = uiShadowMaterial;

            CreateBossBar(canvas);
            hud.pausePanel = CreatePausePanel(canvas);

            refs.rewardScreen = CreateRewardScreen(canvas);
            refs.gameOverScreen = CreateGameOverScreen(canvas);
            return refs;
        }

        /// <summary>
        /// 画面下中央の生産パネル: ユニットごとの生産ボタンと、生産キューの表示を、同じ大きさのマスで 1 列に並べる。
        /// 左下の操作説明と右下の建築パネルの間に収まるよう、列全体の幅は変えず、ボタンが増えたらマスの間を詰める。
        /// </summary>
        static RectTransform CreateProductionPanel(Transform canvas, HudController hud, int buttonCount)
        {
            const float cellSize = 100f, rowWidth = 662f;
            RectTransform production = CreatePanel("ProductionPanel", canvas, new Color(0f, 0f, 0f, 0.55f));
            Place(production, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, -19f), new Vector2(1000f, 163f));

            // 最後のマスは生産キューの表示
            int cells = buttonCount + 1;
            var grid = production.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(cellSize, cellSize);
            grid.spacing = new Vector2((rowWidth - cells * cellSize) / (cells - 1), 0f);
            grid.childAlignment = TextAnchor.MiddleCenter;

            hud.produceButtons = new Button[buttonCount];
            hud.produceLabels = new TMP_Text[buttonCount];
            for (int i = 0; i < buttonCount; i++)
            {
                hud.produceButtons[i] = CreateButton($"ProduceButton{i}", production, "", 22, out TMP_Text label);
                hud.produceLabels[i] = label;
                AddIconButtonFace(hud.produceButtons[i], label);
            }

            return production;
        }

        // 生産ボタン・建築ボタンのアイコン (Assets/Images の aseprite) と、それを使うデータ
        static readonly (string unitData, string icon)[] UnitIcons =
        {
            ("Soldier", "swordsman"), ("Archer", "archer"), ("Spearman", "lancer"), ("Paladin", "paladin"), ("Priest", "Priest"),
        };
        static readonly (string buildingData, string icon)[] BuildingIcons =
        {
            ("Wall", "wall"), ("Gate", "gate"), ("Turret", "turret"),
        };

        // アイコン付きのボタン (生産・建築) の一辺の長さ
        const float IconButtonSize = 100f;

        static Sprite LoadIcon(string name) =>
            AssetDatabase.LoadAllAssetsAtPath($"Assets/Images/{name}.aseprite").OfType<Sprite>().FirstOrDefault();

        /// <summary>自軍ユニットのデータに、生産ボタンのアイコンを設定する。アイコンがなければそのままにする。</summary>
        public static void AssignUnitIcons()
        {
            foreach ((string unitData, string icon) in UnitIcons)
            {
                var data = AssetDatabase.LoadAssetAtPath<UnitData>($"{UnitDataDir}/{unitData}.asset");
                Sprite sprite = LoadIcon(icon);
                if (data == null || sprite == null || data.icon == sprite) continue;

                data.icon = sprite;
                EditorUtility.SetDirty(data);
            }
        }

        /// <summary>建築物のデータに、建築ボタンのアイコンを設定する。アイコンがなければそのままにする。</summary>
        public static void AssignBuildingIcons()
        {
            foreach ((string buildingData, string icon) in BuildingIcons)
            {
                var data = AssetDatabase.LoadAssetAtPath<BuildingData>($"{BuildingDataDir}/{buildingData}.asset");
                Sprite sprite = LoadIcon(icon);
                if (data == null || sprite == null || data.icon == sprite) continue;

                data.icon = sprite;
                EditorUtility.SetDirty(data);
            }
        }

        /// <summary>
        /// 建築ボタンを、生産ボタンと同じ大きさの正方形にして、パネルの中央に横一列で並べる。
        /// 下に設置のヒントを出す行を空けておく。
        /// </summary>
        static void PlaceBuildButtons(RectTransform panel, Button[] buttons)
        {
            const float gap = 24f, bottom = 40f;
            float rowWidth = buttons.Length * IconButtonSize + (buttons.Length - 1) * gap;
            float left = (panel.sizeDelta.x - rowWidth) * 0.5f;
            for (int i = 0; i < buttons.Length; i++)
            {
                Place((RectTransform)buttons[i].transform, Vector2.zero, Vector2.zero,
                    new Vector2(left + i * (IconButtonSize + gap), bottom), new Vector2(IconButtonSize, IconButtonSize));
            }
        }

        /// <summary>
        /// 生産ボタン・建築ボタンに、アイコン・コスト・ショートカットキーの表示と IconButtonView を付け、
        /// label をアイコンの下の名前にする。アイコンとコストは「Face」にまとめ、マウスを乗せるとそこが中心から拡大する。
        /// </summary>
        static void AddIconButtonFace(Button button, TMP_Text label)
        {
            const float nameHeight = 24f;
            Transform root = button.transform;

            // 背景は描かず、クリックを受ける範囲としてだけ使う。押せないときはアイコンが薄くなる
            button.GetComponent<Image>().color = Color.clear;
            button.transition = Selectable.Transition.None;
            foreach (string old in new[] { "Face", "Hotkey" })
            {
                Transform existing = root.Find(old);
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
            }

            RectTransform face = CreateRect("Face", root);
            face.anchorMin = Vector2.zero;
            face.anchorMax = Vector2.one;
            face.pivot = new Vector2(0.5f, 0.5f);
            face.offsetMin = new Vector2(0f, nameHeight);
            face.offsetMax = Vector2.zero;
            var faceGroup = face.gameObject.AddComponent<CanvasGroup>();
            faceGroup.blocksRaycasts = false;

            RectTransform iconRect = CreateRect("Icon", face);
            Place(iconRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64f, 64f));
            var icon = iconRect.gameObject.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;

            TMP_Text cost = CreateText("Cost", face, "", 34, TextAlignmentOptions.Center, Color.white);
            Stretch(cost.rectTransform);
            cost.fontStyle = FontStyles.Bold;
            cost.textWrappingMode = TextWrappingModes.NoWrap;
            // 「200 コイン」のように長いものは、ボタンの幅に収まるよう縮める
            cost.enableAutoSizing = true;
            cost.fontSizeMin = 14f;
            cost.fontSizeMax = 34f;
            cost.fontSharedMaterial = uiShadowMaterial;
            cost.gameObject.SetActive(false);

            TMP_Text hotkey = CreateText("Hotkey", root, "", 16, TextAlignmentOptions.TopLeft, new Color(1f, 1f, 1f, 0.85f));
            Place(hotkey.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(5f, -3f), new Vector2(24f, 20f));
            hotkey.fontSharedMaterial = uiShadowMaterial;

            // 元のラベルは、アイコンの下の名前にする。長い名前は縮めて 1 行に収める
            label.name = "Name";
            label.transform.SetAsLastSibling();
            RectTransform name = label.rectTransform;
            name.anchorMin = Vector2.zero;
            name.anchorMax = new Vector2(1f, 0f);
            name.pivot = new Vector2(0.5f, 0f);
            name.offsetMin = new Vector2(2f, 2f);
            name.offsetMax = new Vector2(-2f, nameHeight);
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.enableAutoSizing = true;
            label.fontSizeMin = 12f;
            label.fontSizeMax = 18f;
            label.fontSharedMaterial = uiShadowMaterial;
            label.text = "";

            if (!button.TryGetComponent(out IconButtonView view)) view = button.gameObject.AddComponent<IconButtonView>();
            view.face = face;
            view.icon = icon;
            view.costText = cost;
            view.nameText = label;
            view.hotkeyText = hotkey;
            EditorUtility.SetDirty(view);
        }

        /// <summary>
        /// 今のシーンの生産ボタンを、アイコン付きの見た目にする (パネルの並びやボタンそのものは変えない)。
        /// ユニットのデータにアイコンも設定する。すでにあれば作り直す。
        /// </summary>
        public static void AddProduceButtonIcons(HudController hud)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            AssignUnitIcons();
            for (int i = 0; i < hud.produceButtons.Length; i++)
                AddIconButtonFace(hud.produceButtons[i], hud.produceLabels[i]);

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 今のシーンの建築ボタンを、生産ボタンと同じアイコン付きの正方形にする (パネルの位置と大きさは変えない)。
        /// 建築物のデータにアイコンも設定する。すでにあれば作り直す。
        /// </summary>
        public static void AddBuildButtonIcons(HudController hud)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            AssignBuildingIcons();
            for (int i = 0; i < hud.buildButtons.Length; i++)
                AddIconButtonFace(hud.buildButtons[i], hud.buildLabels[i]);
            PlaceBuildButtons((RectTransform)hud.transform.Find("BuildPanel"), hud.buildButtons);

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 生産パネルの上の左端に出す、生産キューの状態 (生産中のユニットと進み具合、待っている数)。
        /// 地面の上に直接出るので、影付きのマテリアルで読みやすくする。
        /// </summary>
        static TMP_Text CreateQueueText(Transform canvas, RectTransform production)
        {
            TMP_Text queue = CreateText("QueueText", canvas, "", 22, TextAlignmentOptions.BottomLeft, Color.white);
            float left = production.anchoredPosition.x - production.sizeDelta.x * production.pivot.x + 12f;
            float y = production.anchoredPosition.y + production.sizeDelta.y + 6f;
            Place(queue.rectTransform, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(left, y), new Vector2(480f, 36f));
            queue.textWrappingMode = TextWrappingModes.NoWrap;
            queue.fontSharedMaterial = uiShadowMaterial;
            return queue;
        }

        /// <summary>
        /// 今のシーンの生産キューの表示を、生産パネルの上の左端に置き直す (シーン全体は作り直さない)。
        /// 生産パネルより手前に描く。
        /// </summary>
        public static void PlaceQueueText(HudController hud)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            var production = (RectTransform)hud.transform.Find("ProductionPanel");
            if (hud.queueText != null) Object.DestroyImmediate(hud.queueText.gameObject);
            hud.queueText = CreateQueueText(hud.transform, production);
            hud.queueText.transform.SetSiblingIndex(production.GetSiblingIndex() + 1);

            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 生産パネルのすぐ上に置く、一時停止ボタンと、時間の流れの速さ (等速・2倍・3倍) を切り替えるボタン。
        /// </summary>
        static RectTransform CreateSpeedPanel(Transform canvas, HudController hud, RectTransform production)
        {
            const float titleWidth = 64f, buttonWidth = 96f, buttonGap = 10f, height = 60f;
            int buttonCount = GameManager.SpeedMultipliers.Length;
            float panelWidth = buttonGap + titleWidth + (buttonCount + 1) * (buttonWidth + buttonGap);
            float y = production.anchoredPosition.y + production.sizeDelta.y + 8f;
            RectTransform panel = CreatePanel("SpeedPanel", canvas, new Color(0f, 0f, 0f, 0.55f));
            Place(panel, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, y), new Vector2(panelWidth, height));

            TMP_Text title = CreateText("SpeedTitle", panel, "速度", 22, TextAlignmentOptions.Center, Color.white);
            Place(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(buttonGap, 0f), new Vector2(titleWidth, height));

            hud.pauseButton = CreateButton("StopButton", panel, "||", 22, out _);
            Place((RectTransform)hud.pauseButton.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(buttonGap + titleWidth, 0f), new Vector2(buttonWidth, height - 16f));

            hud.speedButtons = new Button[buttonCount];
            hud.speedLabels = new TMP_Text[buttonCount];
            for (int i = 0; i < buttonCount; i++)
            {
                Button button = CreateButton($"SpeedButton{i}", panel, "", 22, out TMP_Text label);
                Place((RectTransform)button.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(buttonGap + titleWidth + (i + 1) * (buttonWidth + buttonGap), 0f),
                    new Vector2(buttonWidth, height - 16f));
                hud.speedButtons[i] = button;
                hud.speedLabels[i] = label;
            }
            return panel;
        }

        /// <summary>今のシーンの HUD に速度ボタンを足す (シーン全体は作り直さない)。すでにあれば作り直す。</summary>
        public static void AddSpeedUi(HudController hud)
        {
            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);

            Transform oldPanel = hud.transform.Find("SpeedPanel");
            if (oldPanel != null) Object.DestroyImmediate(oldPanel.gameObject);
            var production = (RectTransform)hud.transform.Find("ProductionPanel");
            CreateSpeedPanel(hud.transform, hud, production).SetSiblingIndex(production.GetSiblingIndex() + 1);

            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>
        /// 今のシーンの生産できるユニットを更新する (シーン全体は作り直さない)。
        /// ユニットのプレハブを作り直して拠点の生産リストに入れ、生産パネルをボタンの数に合わせて作り直す。
        /// </summary>
        public static void UpdateProduction(PlayerBase playerBase, HudController hud)
        {
            foreach (string dir in new[] { MaterialDir, PrefabDir, ProjectilePrefabDir, UnitDataDir, AnimationDir })
                Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            playerBase.producibleUnits = ProducibleUnits(CreateUnits(CreateMaterials()));

            uiFont = LoadUiFont();
            uiShadowMaterial = LoadOrCreateShadowPreset(uiFont);
            Transform oldPanel = hud.transform.Find("ProductionPanel");
            int siblingIndex = oldPanel.GetSiblingIndex();
            Object.DestroyImmediate(oldPanel.gameObject);
            CreateProductionPanel(hud.transform, hud, playerBase.producibleUnits.Length).SetSiblingIndex(siblingIndex);

            Transform help = hud.transform.Find("HelpText");
            if (help != null) help.GetComponent<TMP_Text>().text = HelpTextContent;

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(playerBase);
            EditorUtility.SetDirty(hud);
            EditorSceneManager.MarkSceneDirty(hud.gameObject.scene);
        }

        /// <summary>画面上部: ボスの名前と、その下に大きな体力バー。</summary>
        static void CreateBossBar(Transform canvas)
        {
            const float width = 900f;

            // スクリプトは常にアクティブな親に置き、子のパネルだけ表示を切り替える
            RectTransform root = CreateRect("BossBar", canvas);
            Place(root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(width, 110f));
            var bar = root.gameObject.AddComponent<BossHealthBar>();

            RectTransform panel = CreateRect("BossPanel", root);
            Stretch(panel);
            bar.panel = panel.gameObject;

            bar.nameText = CreateText("BossNameText", panel, "", 40, TextAlignmentOptions.Center, new Color(1f, 0.85f, 0.4f));
            Place(bar.nameText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(width, 52f));
            bar.nameText.fontStyle = FontStyles.Bold;
            bar.nameText.fontSharedMaterial = uiShadowMaterial;

            RectTransform back = CreatePanel("BossHpBack", panel, new Color(0f, 0f, 0f, 0.7f));
            Place(back, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -58f), new Vector2(width, 40f));
            back.GetComponent<Image>().raycastTarget = false;

            // 枠の内側でアンカーを使って伸縮させる (残り 0 で幅 0 になるよう、余白は親側で取る)
            RectTransform track = CreateRect("BossHpTrack", back);
            Stretch(track);
            track.offsetMin = new Vector2(4f, 4f);
            track.offsetMax = new Vector2(-4f, -4f);

            RectTransform fill = CreatePanel("BossHpFill", track, new Color(0.85f, 0.15f, 0.15f));
            Stretch(fill);
            fill.GetComponent<Image>().raycastTarget = false;
            bar.fill = fill;

            bar.hpText = CreateText("BossHpText", back, "", 24, TextAlignmentOptions.Center, Color.white);
            Stretch(bar.hpText.rectTransform);
            bar.hpText.fontSharedMaterial = uiShadowMaterial;

            panel.gameObject.SetActive(false);
        }

        static RewardScreen CreateRewardScreen(Transform canvas)
        {
            // スクリプトは常にアクティブな親に置き、子のパネルだけ表示を切り替える
            RectTransform root = CreateRect("RewardScreen", canvas);
            Stretch(root);
            var screen = root.gameObject.AddComponent<RewardScreen>();

            RectTransform panel = CreatePanel("RewardPanel", root, new Color(0f, 0f, 0f, 0.78f));
            Stretch(panel);
            screen.panel = panel.gameObject;

            screen.headerText = CreateText("HeaderText", panel, "ステージ クリア！", 56, TextAlignmentOptions.Center,
                new Color(1f, 0.9f, 0.35f));
            screen.headerText.fontStyle = FontStyles.Bold;
            Place(screen.headerText.rectTransform, new Vector2(0.5f, 0.82f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1200f, 80f));

            TMP_Text sub = CreateText("SubText", panel, "報酬を 1 つ選んでください", 30, TextAlignmentOptions.Center, Color.white);
            Place(sub.rectTransform, new Vector2(0.5f, 0.74f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200f, 50f));

            const int choices = 3;
            screen.choiceButtons = new Button[choices];
            screen.titleTexts = new TMP_Text[choices];
            screen.descriptionTexts = new TMP_Text[choices];
            for (int i = 0; i < choices; i++)
            {
                Button card = CreateButton($"RewardChoice{i}", panel, "", 30, out TMP_Text title);
                Place((RectTransform)card.transform, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f),
                    new Vector2((i - 1) * 430f, 0f), new Vector2(390f, 320f));

                title.name = "Title";
                title.color = new Color(1f, 0.9f, 0.35f);
                title.fontStyle = FontStyles.Bold;
                title.fontSize = 34;
                Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, -30f), new Vector2(350f, 60f));

                TMP_Text description = CreateText("Description", card.transform, "", 26, TextAlignmentOptions.Top, Color.white);
                Place(description.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, -120f), new Vector2(340f, 170f));

                screen.choiceButtons[i] = card;
                screen.titleTexts[i] = title;
                screen.descriptionTexts[i] = description;
                MakeWhiteButton(card, title, description);
            }

            panel.gameObject.SetActive(false);
            return screen;
        }

        static GameOverScreen CreateGameOverScreen(Transform canvas)
        {
            RectTransform root = CreateRect("GameOverScreen", canvas);
            Stretch(root);
            var screen = root.gameObject.AddComponent<GameOverScreen>();

            RectTransform panel = CreatePanel("GameOverPanel", root, new Color(0f, 0f, 0f, 0.82f));
            Stretch(panel);
            screen.panel = panel.gameObject;

            TMP_Text title = CreateText("TitleText", panel, "敗北", 96, TextAlignmentOptions.Center, new Color(0.95f, 0.3f, 0.25f));
            title.fontStyle = FontStyles.Bold;
            Place(title.rectTransform, new Vector2(0.5f, 0.68f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 130f));

            TMP_Text reason = CreateText("ReasonText", panel, "拠点が破壊されました", 32, TextAlignmentOptions.Center, Color.white);
            Place(reason.rectTransform, new Vector2(0.5f, 0.56f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 50f));

            screen.resultText = CreateText("ResultText", panel, "", 32, TextAlignmentOptions.Center, Color.white);
            Place(screen.resultText.rectTransform, new Vector2(0.5f, 0.49f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1000f, 50f));

            screen.retryButton = CreateButton("RetryButton", panel, "もう一度", 34, out TMP_Text retryLabel);
            Place((RectTransform)screen.retryButton.transform, new Vector2(0.5f, 0.34f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(300f, 84f));
            MakeWhiteButton(screen.retryButton, retryLabel);

            panel.gameObject.SetActive(false);
            return screen;
        }

        // 白いボタンの上の文字の色
        static readonly Color WhiteButtonTitleColor = new Color(0.1f, 0.1f, 0.12f);
        static readonly Color WhiteButtonTextColor = new Color(0.22f, 0.22f, 0.26f);

        /// <summary>
        /// ボタンを白くし、上の文字を読める暗い色にする (最初の文字は見出しとして一番暗く)。
        /// ふだんは白のまま、マウスを乗せると少し暗く、押すともう少し暗くなる。
        /// </summary>
        static void MakeWhiteButton(Button button, params TMP_Text[] texts)
        {
            button.image.color = Color.white;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.selectedColor = Color.white;
            colors.highlightedColor = new Color(0.85f, 0.85f, 0.85f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            button.colors = colors;

            for (int i = 0; i < texts.Length; i++)
                texts[i].color = i == 0 ? WhiteButtonTitleColor : WhiteButtonTextColor;
        }

        /// <summary>今のシーンの報酬カードと、敗北画面の「もう一度」ボタンを白くする (シーン全体は作り直さない)。</summary>
        public static void WhitenResultButtons(RewardScreen rewardScreen, GameOverScreen gameOverScreen)
        {
            for (int i = 0; i < rewardScreen.choiceButtons.Length; i++)
            {
                MakeWhiteButton(rewardScreen.choiceButtons[i], rewardScreen.titleTexts[i], rewardScreen.descriptionTexts[i]);
                EditorUtility.SetDirty(rewardScreen.choiceButtons[i]);
                EditorUtility.SetDirty(rewardScreen.choiceButtons[i].image);
                EditorUtility.SetDirty(rewardScreen.titleTexts[i]);
                EditorUtility.SetDirty(rewardScreen.descriptionTexts[i]);
            }

            Button retry = gameOverScreen.retryButton;
            TMP_Text retryLabel = retry.GetComponentInChildren<TMP_Text>(true);
            MakeWhiteButton(retry, retryLabel);
            EditorUtility.SetDirty(retry);
            EditorUtility.SetDirty(retry.image);
            EditorUtility.SetDirty(retryLabel);
            EditorSceneManager.MarkSceneDirty(rewardScreen.gameObject.scene);
        }

        static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        static RectTransform CreatePanel(string name, Transform parent, Color color)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.gameObject.AddComponent<Image>().color = color;
            return rect;
        }

        static TMP_FontAsset LoadUiFont()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(UiFontPath);
            if (font != null) return font;

            Debug.LogWarning($"[RogueRTS] {UiFontPath} が見つからないため、TMP の既定フォントを使います (日本語は表示されません)。");
            return TMP_Settings.defaultFontAsset;
        }

        /// <summary>
        /// 地面の上に直接表示する文字用の、影 (アンダーレイ) 付きマテリアルプリセット。
        /// フォントのアトラスを共有するので、フォントアセットを複製するより軽い。
        /// </summary>
        static Material LoadOrCreateShadowPreset(TMP_FontAsset font)
        {
            // 名前をフォントアセット名で始めると、TMP の Material Preset 欄に候補として出る
            string path = $"{MaterialDir}/{font.name} Shadow.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            material = new Material(font.material);
            material.EnableKeyword("UNDERLAY_ON");
            material.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.75f));
            material.SetFloat("_UnderlayOffsetX", 1f);
            material.SetFloat("_UnderlayOffsetY", -1f);
            material.SetFloat("_UnderlayDilate", 0.2f);
            material.SetFloat("_UnderlaySoftness", 0.3f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static Material LoadOrCreateOutlinePreset(TMP_FontAsset font)
        {
            // 名前をフォントアセット名で始めると、TMP の Material Preset 欄に候補として出る
            string path = $"{MaterialDir}/{font.name} Outline.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            material = new Material(font.material);
            material.EnableKeyword("OUTLINE_ON");
            material.SetColor("_OutlineColor", Color.black);
            material.SetFloat("_OutlineWidth", 0.3f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static TextMeshProUGUI CreateText(
            string name, Transform parent, string content, float fontSize, TextAlignmentOptions alignment, Color color)
        {
            RectTransform rect = CreateRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = uiFont;
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            // ただの文字がクリックを奪わないようにする
            text.raycastTarget = false;
            return text;
        }

        static Button CreateButton(string name, Transform parent, string label, int fontSize, out TMP_Text labelText)
        {
            RectTransform rect = CreatePanel(name, parent, new Color(0.24f, 0.32f, 0.46f, 1f));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();

            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.8f, 0.8f, 0.8f);
            colors.highlightedColor = Color.white;
            colors.selectedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.pressedColor = new Color(0.55f, 0.55f, 0.55f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.7f);
            button.colors = colors;

            // WASD (カメラ操作) でボタンのフォーカスが動かないようにする
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            labelText = CreateText("Label", rect, label, fontSize, TextAlignmentOptions.Center, Color.white);
            Stretch(labelText.rectTransform);
            return button;
        }
    }
}
