# RogueLike_RTS

## 会話のルール

- ユーザーとのやりとりは、報告・質問・説明を含めてすべて日本語で行う。
- コードのコメント、XML ドキュメント (`/// <summary>`)、`[Tooltip]`、`[Header]`、UI の文字も、既存のコードに合わせて日本語で書く。識別子は英語。
- このフォルダは git で管理されていない。変更の差分を取ったり元に戻したりできないので、モデル・シーン・プレハブを上書きする操作は、上書きする前に中身と更新日時を確かめる。

## ゲームの概要

Unity で作っている 3D のローグライク RTS。拠点を守りながら敵のウェーブをしのぎ、ステージを進めていく。

- **進行**: 1 ステージは 3 ウェーブ。全ウェーブが出終わって敵が全滅すると、報酬を 3 つの中から 1 つ選んで次のステージへ進む。ステージが進むごとに、敵の HP は +20%、攻撃力は +10% 上がる。拠点が壊されるとゲームオーバーで、リトライするとシーンを読み込み直す。
- **ウェーブ**:
  - 最初のウェーブまでの準備時間は 15 秒、その後のウェーブの間隔は 50 秒。敵を全滅させると、残り時間が 5 秒まで縮む。
  - ステージをまたいだ通算で 5 ウェーブごとに、ボス (エリートナイト) が 1 体だけ出るボスウェーブになる。ボスウェーブのあとは、次のウェーブまで通常の 3 倍の 150 秒待つ。
  - ステージが進むと、複数の方向から同時に攻めてくる。
- **資源**: ゴールドは毎秒 5 ずつ増え、ユニットの生産に使う。コインは敵を倒すと入り、建築に使う。
- **自軍ユニット** (拠点で生産):
  - 兵士 50、弓兵 70、槍兵 120 (兵士の 1.5 倍のリーチと HP)、パラディン 200 (HP は兵士の 2.5 倍、15% の確率で攻撃を防ぐ。シールドバッシュは正面の敵全員に当たる)。
  - 司祭 140 (HP は弓兵と同じ)。攻撃はせず、3 秒ごとに、近くで最も HP の低い味方ユニット 1 体を 10 回復する。自分は回復できない。回復役は `UnitData.healAmount` が 0 より大きいユニット。
  - 近くの敵に自動で向かっていき、自動で攻撃する。近接攻撃は、受けたダメージに応じて相手を押し戻す。被弾すると一瞬怯む。
- **敵**: 敵兵と敵弓兵は赤いモデル (`*_red`) で、ボスは `heavy_knight2`。ボスは正面に 2 体以上いると薙ぎ払い、それ以外は振り下ろしで攻撃する。
- **建築**: 防壁 15、門 30、タレット 150 (防壁か門の上に置く)。
  - 防壁で拠点を完全に囲うことはできない。門は敵も味方も通れる。
  - 2m のマス目に置く。マスの管理は `BuildGrid`。
- **地形**:
  - 岩と木がある。木の生え方は、ゲームを始めるたびに「まばら」「ほとんど生えない」「樹木エリア」から選ばれる (`ForestGenerator`)。
  - 木は通れず建築もできないが、矢は木を越えて飛ぶ。敵が拠点へ辿り着けなくなる位置には生えない。
- **時間**:
  - 等速のときの `Time.timeScale` は 0.5 で、速度ボタンで 2 倍・3 倍にできる。
  - ウェーブまでのカウントダウンのように画面に秒数を出すものは、`GameManager.ClockDeltaTime` (等速のとき現実の 1 秒で 1 進む) で進める。

### 操作

| 入力 | 動作 |
|---|---|
| WASD / 矢印キー | カメラを動かす |
| ホイール | ズーム |
| Ctrl + マウス | カメラの向きを変える (フリーカメラ) |
| G | 通常の見下ろしカメラに戻す |
| 左ドラッグ / Shift + 左ドラッグ | 範囲選択 / 選択に追加 |
| 右クリック | 選んだユニットを移動させる |
| 1〜5 | 兵士・弓兵・槍兵・司祭・パラディンを生産する |
| Z / X / C | 防壁・門・タレットの設置モード。左クリックかなぞって置き、右クリックか Esc で終える |
| Space | 一時停止 / 再開 |
| F 長押し | スローモーションになり、カーソルの近くの味方に印が出る。離すとそのユニットの三人称視点になる |
| F1 / F2 | UI の表示 / ダメージ数値の表示 (与えたダメージは緑、受けたダメージは赤、回復は青) を切り替える |

## プロジェクト構成

- Unity 6000.0.72f1、URP、Input System (新しいほう)、AI Navigation 2.0、TextMeshPro (フォントは Corporate Logo)。
- `Assets/Scripts`: ゲームのコード。名前空間は `RogueRTS`。asmdef は意図的に置いていないので、Assembly-CSharp に入る。
  - `Core`: 進行・速度・資源・強化補正
  - `Units`: ユニット・HP・矢・アニメーション
  - `Enemy`: ウェーブ・ボス
  - `Buildings`: 建築・マス目・拠点・タレット
  - `Environment`: 木
  - `Player`: カメラ・選択・視点移動
  - `UI`
  - `Rewards`
- `Assets/Editor/RtsPrototypeBuilder.cs`: シーン、プレハブ、データ、Animator コントローラーをコードから作るビルダー。キャラクターごとのモーションの設定 (`CharacterSpec`) もここにある。
- `Assets/Data`: `UnitData`、`BuildingData`、`RewardData` (ScriptableObject)。
- `Assets/Models`: Blockbench で作った ASCII 形式の FBX と png。
  - クリップ名は `animation.<モデル>.<動作>` (idle, walk, attack, death, hurt など)。
  - png のフィルタは Point にする。
  - 色違いのモデルは、元のモデルのモーションを使う。
- `Assets/Scenes/Game.unity`: ゲームのシーン。

## 作業のルール

- **シーンを作り直さない**: `Game.unity` はユーザーがインスペクターで手を入れている。`RtsPrototypeBuilder.Build()` を実行すると、その変更が消える。
  - シーンは Unity CLI で必要な所だけ変えて保存する。
  - ビルダーのコードは、作り直したときに同じ結果になるよう、合わせて直す。
- **ユニットのプレハブ**: プレハブと Animator は `RtsPrototypeBuilder.BuildUnitPrefabs()` で作り直せる。シーンには触らず、プレハブの GUID も変わらない。
  - 手で編集されていないか、更新日時を見てから実行する。
  - 実行中に一時的なオブジェクトを作るので、開いているシーンに未保存の印が付くが、シーンの中身は変わらない。
- **モデルにモーションを足したとき**: `CharacterSpec` にクリップ名を書き、`BuildUnitPrefabs()` を実行する。
  - インポート設定にクリップの一覧が書き込まれたモデルでは、一覧にないモーションは読み込まれない。
  - `EnsureClipLoops` が足りないクリップを一覧に加える。
- **生産できるユニットを足すとき**: シーンの生産パネル (`ProductionPanel`) はユーザーが手で作り直したもの。
  - GridLayoutGroup で 100×100 のマスを 1 列に並べ、最後のマスが生産キューの表示になっている。
  - `UpdateProduction()` はパネルを消して作り直すので使わない。代わりに、生産ボタンを複製して生産キューの手前に置き、`HudController` の `produceButtons` / `produceLabels` と `PlayerBase.producibleUnits` に足す。
  - 列全体の幅 662 (操作説明と建築パネルの間) を保つよう、マスの間隔を詰める。

## Unity CLI (エディタを開いたまま操作する)

- CLI: `C:\Users\ys-03\AppData\Local\unity\bin\unity.exe`
- 毎回 `--project-path C:/Users/ys-03/UnityWorkspaces/RogueLike_RTS --result-only` を付ける。
- コマンドは `unity.exe command <名前> ...` の形で呼ぶ。

| コマンド | 用途 |
|---|---|
| `editor_status`、`list_open_scenes` | 状態の確認 (シーンの `isDirty` も見られる) |
| `eval --code '<C#>'` | エディタ内で C# を実行する (`return` した値が返る) |
| `recompile`、`recompile_status` | 再コンパイルし、`status` が `completed` になるまで待つ |
| `run_tests --mode playmode --async_tests true [--filter <名前>]`、`test_status` | PlayMode テスト (全体で約 2 分) |
| `console --level warning`、`clear_console` | コンソールの確認と消去 |

エディタが閉じているときは、バッチモードでも動かせる。

- 置き場所: `C:\Program Files\Unity\Hub\Editor\6000.0.72f1\Editor\Unity.exe`
- テストは `-runTests -testPlatform PlayMode` を付けて実行する。

## テスト

- `Assets/Tests/PlayMode/GameSmokeTest.cs` が、Game シーンを実際に動かして確かめるスモークテスト。
- テストは別の asmdef にあり、ゲームの型を参照できない。オブジェクト名、`SendMessage`、リフレクション (`Field`、`ComponentOfType` などのヘルパー) で確かめる。
  - `GetComponent("Unit")` は VisualScripting の `Unit` を返してしまうので、`"RogueRTS.Unit"` のように完全な型名で探す。
- `SetUp` では次の 2 つを行う。
  - 位置に依存するテストのために、木を生やさない (`ForestGenerator.PatternOverride = None`)。
  - ユーザーの実際のマウスとキーボードを無効にする。

  入力を使うテストを足すときも、これを保つ。
