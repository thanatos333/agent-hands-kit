# desktop-agent — デスクトップ GUI 操作エージェント

ユーザーの Windows PC の実デスクトップ（ログイン中セッション）を、ブラウザツールのように
「見て（screenshot / snapshot）→ 要素を指定して操作する」ための道具。
2026-11-11 に設計・実装・実機スモークテスト済み。

## アーキテクチャ（2層）

SSH セッションは Windows のセッション0で動くため、ユーザーの対話デスクトップを
見たりクリックしたりできない。そのため2層に分けた。

- **常駐本体（サーバー）**: `desktop-agent.exe serve`
  - スケジュールタスク `AronaDesktopAgent`（ログオン時起動、対話セッション＝セッション1、
    最上位権限）で常駐。タスクトレイに常駐表示アイコンを持つ。
  - `http://127.0.0.1:8791/` のみで待ち受け（外部に開かない）。`X-Token` ヘッダ認証
    （トークンは `%USERPROFILE%\tools\desktop-agent\token.txt`、ACL で本人のみ）。
  - デスクトップ操作は単一ロックで直列化し、応答ごとに実測 `elapsedMs` を返す。
  - `timeBeginPeriod(1)` でタイマ分解能を上げ、精密ウェイトは sleep+spin ハイブリッド。
- **CLI クライアント**: 同じ exe のサブコマンド（`desktop-agent.exe status` 等）。
  トークンを読んで localhost に HTTP で投げ、JSON をそのまま標準出力に出す。
  SSH（クラウドAI）からも、PC上のAIの `tools\desktop.ps1`（素通しラッパー）からも同じに使える。

実装は C# / .NET 10（UI Automation・SendInput・GDI キャプチャが最も確実なため）。
単一ファイル exe として `%USERPROFILE%\tools\desktop-agent\desktop-agent.exe` に配置。
ソースはこのフォルダに一式ある。ビルドは
`dotnet publish -c Release -o %USERPROFILE%\tools\desktop-agent` で再配置し、
常駐タスクを再起動すると反映される。

## 命令一覧（すべて JSON 応答）

| 命令 | 内容 |
|---|---|
| `ping` / `status` | 生存確認 / 状態（セッション、ロック状態、input desktop、無入力秒数、メモリ使用量） |
| `idle` | ユーザーの最終入力からの経過秒（使っている最中かの判定） |
| `monitors` | モニタ構成・実 DPI・スケール |
| `windows` / `focus <hwnd\|題名>` | 可視ウィンドウ一覧と前面化 |
| `launch <exe/アプリ名>` | 対話セッションでアプリ起動（cmd 経由でファイル名解決も可） |
| `screenshot [--window h] [--element id] [--region x,y,w,h] [--format png\|jpg] [--quality n] [--scale s]` | 画面/ウィンドウ/要素/矩形の撮影。保存先パスと実測 `captureMs` を返す。保存先は `...\desktop-agent\shots\` |
| `snapshot [hwnd] [--max n] [--region x,y,w,h] [--diff]` | UI Automation ツリーを対話要素だけに刈り込んで返す（下記） |
| `click / double_click / right_click <id \| x y>` | 要素は Invoke/Toggle パターン優先、無ければ座標クリック |
| `setvalue <id> <text>` / `scrollintoview <id>` | 値の設定（入力欄への直接入力）、要素の表示 |
| `type <text>` | Unicode テキスト入力（SendInput、サロゲートペア対応） |
| `key <chord>` | 例: `ctrl+s`、`alt+f4`、`vk:0x73`、`scan:0x3B` |
| `keydown / keyup <key>` | キーの押し分け（修飾キー・任意キー） |
| `hold <key> <ms>` | 指定ミリ秒の長押し。実測値と誤差を返す |
| `sequence --file steps.json`（または `--json`） | 複数ステップを本体側で精密実行。各ステップ `{"do":..., "delay":ms先行待ち}`、実測の開始時刻・所要時間を返す |
| `mouse <x> <y>` / `mouserel <dx> <dy>` / `mousepath <x> <y> <ms>` / `drag <x1> <y1> <x2> <y2> [ms]` / `scroll <dx> <dy> [x y]` | マウス絶対/相対/時間付き補間移動、ドラッグ、ホイール |
| `raw <down\|up> <vk> / scan:<code>` | 生の SendInput 1イベント（任意の VK / スキャンコード、上位命令で足りない場面用） |
| `clipboard get / set <text>` | クリップボード読み書き |
| `paste <text> / paste --file <f>` | 【v1.1】設定→読み戻し検証→Ctrl+V を本体側で完結（セッション違いの事故防止） |
| `click --ixy x,y --img WxH` | 【v1.1】画像（縮小スクショ）座標を指定すると本体が実ピクセルへ変換 |
| `stop` | 本体の安全な終了（タスク再起動で復帰） |

### v1.2（2026-11-26、メディバン実演 exp4 の実地フィードバックから）

描画作業で実際に困った6点への汎用対策。「手足は本物だが触覚と固有感覚がない」への回答。

- **触覚プリミティブ**: `pixel x y`（1点の色を数値で返す、約5ms）と
  `histogram [--rect|--window] [--top n]`（領域の主要色と面積比）。スポイト結果の
  確認やベタ塗りの成否が、スクショ往復なしに数値で検証できる。
- **検証句の拡張**: sequence の `precond` / `expect` 条件に `pixel`
  （`[x,y,"#RRGGBB",tol]`）と `regionDominant`（`[x,y,w,h,"#RRGGBB",tol,minFrac]`）を追加。
- **チェックポイント**: `{"do":"checkpoint", ...}` ステップは状態をその場で検証し、
  不一致なら sequence を中断して観測値を報告する。`pixelChanged` / `regionChanged` は
  sequence 開始時点のベースラインとの差分を見る（「描いたはずなのに変わっていない」を検出）。
- **レシート**: sequence の結果に、触れた範囲（path 点・クリック・ドラッグ端点）の
  切り抜き画像パスを自動添付（`--no-receipt` で無効化）。
- **ドライラン**: `sequence --dry-run` はカーソル軌跡だけ再生し、ボタンもキーも押さない。
  本番前に形状だけ確認できる。
- **find-image / find-text**: UIA が浅いアプリ向けの目のフォールバック。
  `find-image --file template.png`（画像テンプレート照合、粗→細の2段探索）、
  `find-text <text> [--lang ja-JP]`（Windows 標準 OCR。要・言語パック）。
- **相対アンカー**: `click --anchor <hwnd|題名> --rel dx,dy`（負値は右端・下端から）。
  ウィンドウやパネルが動いてもずれない。
- **座標のピクセル統一**: raw マウスイベントが正規化 `dx/dy` に加えてピクセル
  `x/y` を受理し、本体側で変換する。sequence ファイルは全てピクセルで書ける。
- **フォーカス束縛**: 全コマンド共通 `--hwnd <n>`。対象ウィンドウが前面でない入力は
  送信前に拒否する。sequence では全ステップを監視し、前面を奪われたら中断する。
- **フォーカス・リース**: `lease acquire [--ttl sec] / release / status`（`--owner <name>`、
  環境変数 `DESKTOP_AGENT_OWNER` でも指定可）。保持中は他オーナーの入力を拒否する。
  入力は自動で20秒の仮リースも持つが、明示 acquire は仮リースを横取りできる
  （ドリルで発見した詰まりへの修正）。定期ジョブ同士・作業同士のデスクトップ競合対策。

### v1.1（2026-11-12、Fable 5.2 相談を踏まえた第1〜2段）

- **sequence の条件待ち**: ステップに `"precond"` / `"expect"`（条件オブジェクト:
  `process` 一致 / `window` 部分一致 / `uiaText` 要素名の部分一致、`precondTimeoutMs`
  既定 3000・`expectTimeoutMs` 既定 5000）を付けると、固定 sleep ではなく
  観測可能な状態を待ってから進む。失敗時は観測した前面状態付きで報告する。
- **応答ダイジェスト**: デスクトップ操作の応答に `fg`（前面プロセス名・タイトル・hwnd）を
  常時添付。状態確認の往復を減らす。
- **paste 操作**: 上表。クリップボードは Windows セッション別で、SSH 側で設定しても
  対話セッションに届かない事故（2026-11-12 実証）への恒久対策。
- **ロック判定の再確認**: ロック読み取りは 400ms 間隔で最大4回再確認してから拒否する
  （セキュアデスクトップの一過性フリッカーで「ロック中」と誤報し作業が止まった件の対策）。
- **画像座標クリック**: 上表。モデルが見る縮小画像の座標系のまま指定できる。
- 往復遅延の主因は SSH 接続確立（約2.2秒/回、本体実行は約0.15秒）だったため、
  クラウドAI側は ControlMaster で接続を使い回す運用にした（約0.6秒/回）。

### snapshot の設計（トークン節約が要点）

ブラウザ側で a11y スナップショットが 277KB になった教訓から、以下を入れた。

- ControlView を歩いてから「面白い要素」（パターン持ち／フォーカス可能／名前のある
  コンテンツ種別）だけを残すボトムアップ刈り込み。名前なしのただの Pane 等は落とす。
- 記録は省略記法：`{id, p=親, t=種別, n=名前, r=[x,y,w,h], en, off, foc, pat, val, aid}`。
  凡例は応答の `legend` に毎回付く。
- **要素 id は安定 ID**：パスキー（種別＋AutomationId 優先、次に名前）から採番し、
  同一ウィンドウの再 snapshot でも同じ要素は同じ id を保つ。古い id のまま click できる。
  ただしルート（ウィンドウ）のキーは題名に依存させない（編集で題名が変わるため）。
  コンテナの「名前」が変わるとその配下の id は振り直される（例：メモ帳のタブ改名）。
- `--diff`：前回からの追加（elements）・変更（changed）・削除（removed=id のみ）を返す。
- `--region`：矩形と交差する部分木だけに絞る（ダイアログだけ見る等に有効。実測 118→16 要素）。
- WinUI の PopupHost 部分木（キーヒント等の過渡ノイズ）は刈り込み対象。
  開いたメニュー等は別トップレベルウィンドウとして `windows` に出るのでそちらを snapshot する。

## 実測レイテンシ（2026-11-11、2560×1440・1画面、この PC）

| 操作 | 実測 |
|---|---|
| status / ping 応答 | 2〜35ms（初回 JIT 後は一桁 ms 級） |
| screenshot 全画面 JPG q80（2560×1440） | capture 56ms（433KB） |
| screenshot ウィンドウ JPG | capture 25ms |
| screenshot 半分縮小 JPG | capture 51ms（縮小処理込み） |
| snapshot（メモ帳 46 要素） | 263ms（UIA ツリー走査が支配的） |
| click（Invoke） | 3〜57ms |
| type 45 文字 | 9ms |
| hold 400ms 指定 | 実測 400ms（誤差 0） |
| sequence 4 ステップ（delay+hold 混在、総 1.41s） | 開始オフセット誤差 0.4〜9.3ms（累積）、hold 500ms→実測 501ms、250ms→257ms |
| 常駐メモリ | 起動直後 WS 57.8MB / プライベート 16.2MB。スモーク一式後 WS 約100MB / プライベート約23MB。待機時 CPU はほぼ 0%（ポーリングなし） |

### 正直なボトルネック

本体側の入力実行・タイミング精度は上記の通り ms 級で、「ゆっくり走る車シミュレーション」
程度の連続操作（事前に sequence で組んだ操作列）は成立する設計になっている。
**実際の律速はモデル側の判断周期**（screenshot を見て→考えて→次を決める、の往復で
数秒〜数十秒）であり、フレーム単位の反射的操作はできない。やれるのは
「観察→ sequence で数秒分の操作をまとめて精密実行→再観察」のループまで。
snapshot も大きなアプリでは数百 ms〜秒級になり得る（メモ帳で 263ms）。

## 安全仕様

- 通信は 127.0.0.1 のみ＋トークン認証。トークンファイルはユーザー ACL のみ。
- 全アクションを `%USERPROFILE%\tools\desktop-agent\logs\agent-YYYYMMDD.log`
  （JSON Lines、時刻・命令・成否）に記録。
- トレイアイコンで常駐を明示（メニュー： 状態表示 / 停止）。
- 緊急停止は4通り： ①トレイメニュー「停止」 ②`desktop-agent.exe stop`
  ③タスク `AronaDesktopAgent` の停止 ④`STOP` ファイルを置く
  （存在する間、ping 以外の全命令を拒否。実測で拒否を確認済み）。
- **画面ロック中・UAC セキュアデスクトップ表示中は全デスクトップ操作を拒否**
  （input desktop 名が "Default" 以外なら拒否。越えようとしない）。
  昇格済みプロセスとして動くため通常の UAC ダイアログ自体は出ないが、
  セキュアデスクトップに切り替わった場合は検出して止まる。

## 既知の限界

- スクリーンショットは画面合成の取得（CopyFromScreen）。背後に隠れた部分は撮れない
  （ウィンドウ指定でも重なった他ウィンドウが写る）。PrintWindow は使っていない。
- ロック状態の実地テストはしていない（彼の PC を勝手にロックできないため）。
  検出ロジック（OpenInputDesktop 名）と拒否経路はコード上で担保し、
  非ロック状態での正常応答のみ実測。
- キー入力は「前面ウィンドウの、キーボードフォーカスを持つ要素」に届く。
  `focus` はウィンドウを前面化するだけで、入力欄にフォーカスが無いと
  type / ctrl+a 等は効かない（メモ帳で実証： 要素を click してから打つのが確実）。
- `SetForegroundWindow` の制限回避に Alt タップの小細工を使っている。稀に前面化に
  失敗する場合は応答の `foregroundNow:false` で分かる。
- 要素 id は「同一ウィンドウ・同一セッション内」でのみ有効。本体を再起動すると
  キャッシュは消える。ウィンドウを閉じた後の古い id はエラーになる。
- DirectInput 等の独自入力経路を持つ一部のゲーム/アプリでは SendInput が
  効かないことがある（一般論）。`raw` のスキャンコード指定で改善する場合がある。
- コンソールサブシステムの exe のため、ログオン直後に一瞬コンソール窓が見える
  ことがある（serve 起動直後に自ら隠す実装）。動作には影響しない。
- PC上のAI用ラッパーは JSON をそのまま返す素通し。

## スモークテスト結果（2026-11-11 実施、C 案ベンチ走行中につきメモ帳レベルのみ）

1. screenshot 全画面 → Sydney Opera House の壁紙、デスクトップアイコン列、
   タスクバー（7:54 / 2026/11/11）を目視で確認できた。
2. snapshot（メモ帳） → 文書・メニュー・ツールバー・タイトルバーの 46 要素を
   安定 id 付きで取得。`--diff` で入力後の変更 5 件・追加 2 件だけが返ることを確認。
3. メモ帳 launch → 文書要素 click → 日本語混じり 45 文字を type →
   ウィンドウ撮影で入力を目視確認（ステータスバー「45 文字」まで一致）→
   保存確認ダイアログを領域 snapshot で特定し「保存しない」を Invoke で押して終了。
   別途、空にしてから閉じるボタン Invoke での終了も確認。
4. idle は実入力に応じて変化（不在時 約1875 秒 → 操作後 22 秒）。locked 検出は
   非ロック状態での正常系を確認（上記の通りロック実地は未実施）。
5. sequence + hold のタイミングは本体の実測レポートで裏取り済み（上表）。
6. clipboard set/get の往復、STOP ファイルでの拒否、も確認済み。
