# Agent Hands Kit

**AIエージェントに「手足と目」を与え、AI同士を人間用の入り口経由でつなぐための実運用ツール群。**

> 状態：v0.4 公開中（2026-11-26）。無料・オープンソース（MIT ライセンス）。v0.2 で VOICEVOX をエンジン API で操作する `voicevox-harness` を追加、v0.3 で初心者向け導入ガイド（SETUP.md＋公式サイトの日英ガイドページ）を追加、v0.4 で desktop-agent を 1.2.0 に強化（ピクセル／色ヒストグラムの「触覚」、チェックポイント検証、ドライラン、画像・OCR検索、フォーカス束縛とリース）。過去の版は [Releases](https://github.com/thanatos333/agent-hands-kit/releases) からダウンロードできます。
> 公式サイト：https://thanatos333.github.io/agent-hands-kit/
> 本体はすべて実運用で使われている道具のサニタイズ済みコピーで、個人パス・固有名・秘密情報は除去してある。

![An AI agent modelling a hinge in a CAD app, driven only through this kit](docs/assets/demo-hinge.gif)

*An AI agent modelling a hinge in a CAD app (SolidCraft), driven only through this kit — [full video with narration](https://youtu.be/olbPBROgM3w). ／ このキットだけでAIエージェントがCADを操作し、蝶番をモデリングしている様子（[ナレーション付き動画](https://youtu.be/olbPBROgM3w)）*

> **English summary** — Agent Hands Kit is an open-source (MIT) toolkit that gives AI agents hands and eyes on a real Windows PC: a resident desktop agent driven by UI Automation (snapshots, screenshots, clicks, paste/typing input), PowerShell tools, headless wrappers for Blender / Inkscape / GIMP / FreeCAD / KiCad / LTspice, a VOICEVOX speech harness, and file-mailbox patterns for delegating work between AIs. Localhost-only with token auth. Setup guide: [SETUP.md](SETUP.md) (English, with Japanese summaries).

 ## Getting started — 導入はこちら

**New here? Start with the [Setup Guide (SETUP.md)](SETUP.md).** It covers three ways to install, from easiest to most manual:

1. **Let an AI agent do it** — give Claude Code (or another PC-operating AI) the guide link and one copy-paste instruction. Nothing else to do.
2. **Chat AI as your coach** — ChatGPT / Gemini / Copilot walk you through it one step at a time; you send screenshots when stuck.
3. **Manual setup** — full step-by-step: Desktop Agent build & startup task, PowerShell tools, app wrappers, and the optional VOICEVOX harness, each with a verification check.

> 日本語：導入は **[導入ガイド (SETUP.md)](SETUP.md)** が入口です。AIに任せる方法（リンクを貼って「これを適用して」で完了）から、手順通りの手動セットアップまで、初心者向けに図解付きで書いてあります。

## 解く問題

クラウド型パーソナルAI（例：Muse）の実行環境（VM）は、外へは接続できても、外から直接接続を受ける入り口がない。そのため、別のAIやPCからそのAIへ MCP 等で直接指示を出す構成は成立しない。

本キットは発想を変え、**人間用の入り口（デスクトップGUI・チャット入力欄・共有フォルダ）を、AIが使える手足でそのまま使う**ことで、AI間連携を実現する。実際にこの方式で、クラウドAI・PC上のローカルLLM・PC上のコーディングAIの3者が日常的に連携運用されている（2026年10〜11月、実地運用で検証）。

## 3つのパターン

1. **GUIの手足（Desktop Agent）** — Windowsの対話デスクトップを「見て→要素を指定して操作する」常駐エージェント。UI Automation のスナップショット（安定ID・差分・刈り込みでトークン節約）、スクリーンショット、貼り付け/打鍵の二方式入力、条件待ち付きシーケンス実行。localhost＋トークン認証のみで外部に開かない。
2. **ファイル郵便ポスト（Mailbox）** — 直接接続の代わりに、共有フォルダ（Google Drive等）を指示と成果物の受け渡し場所にする。最終行マーカー（`*_OK` / `*_FAILED <reason> next: <retry>`）で成否を機械判定できる規約付き。
3. **相談・委任ラッパー** — 他AIへの相談を、セッション継続・回答のファイル保存・合言葉ドリルまで含めて1コマンド化する。委任は「前提を固定したタスクファイル＋足場の事前構築＋報告フェーズ保証」のハーネスパターンで回す（`patterns/delegation-harness.md`）。

## 構成

| フォルダ | 内容 | 状態 |
|---|---|---|
| `components/desktop-agent/` | C# / .NET デスクトップGUIエージェント本体＋詳細README＋素通しクライアント `desktop.ps1` | ソース一式あり（パス汎用化済み） |
| `components/powershell-tools/` | PowerShell道具群（委任・ジョブ・ロック・Web読み・文書抽出・全文検索・自己スケジュール等）＋ `INDEX.md` | 12本（`$env:AGENT_HOME` へ汎用化済み） |
| `components/app-wrappers/` | 制作アプリのバッチ実行ラッパー（Blender / Inkscape / GIMP / FreeCAD / KiCad / LTspice）＋画像プレビュー `img-peek` | 7本＋ `INDEX.md`。実アプリをヘッドレス実行し、成果物で成否判定 |
| `components/voicevox-harness/` | VOICEVOXをGUIなし（エンジンAPI）で操作する音声合成ハーネス。アクセント/ピッチ計画の表示と、出力WAVのF0実測検証付き | `vv_harness.py`＋`vv-say.ps1`。VOICEVOX本体とPython（numpy/soundfile）が必要 |
| `components/notification-watcher/` | Windows通知をイベント入口にする補助ツール | ソースあり（保存先は環境変数で変更可） |
| `patterns/` | 委任ハーネス等の再利用パターン文書 | あり |
| `mailbox/` | ファイル郵便ポストの設計とテンプレート | あり |
| `docs/` | アーキテクチャ文書＋公式サイト（GitHub Pages で公開） | あり |
| `SECURITY.md` | 安全設計と利用上の注意 | 必読 |

## 設計原則（実運用で学んだこと）

- **確認は要所だけ**：送信先とフォアグラウンドの確認（前1回）と、実行後の表示確認（1回）に絞る。多重確認は遅さとトークンの無駄になる。多少の誤クリックを許容する前提で速度を取る。
- **結果は最終行マーカーで判定**：出力本文ではなく `*_OK` / `*_FAILED` の最終行で成否を決める。長い出力は上限で切ってファイルに逃がす（`cap-output`）。
- **能力は宣言でなく実測で**：設定ファイルに書いてあることではなく、実際に使う経路で小さなドリル（合言葉の往復等）を通してから信用する。
- **汎用部品だけを作る**：特定タスクの失敗に合わせた専用道具や指標は作らない。どの課題にも効くものだけを残す。
- **送信系は明示指示がある時だけ**：メッセージ送信ツールは、ユーザーの明示指示なしに使わない。

## 動作環境

- Windows 10/11（デスクトップエージェント、PowerShell道具群）
- .NET 10 SDK（デスクトップエージェントのビルド）
- PowerShell 5.1 以降
- 任意：Tailscale 等のプライベートネットワーク（クラウドAI⇔PCの接続）、Google Drive 等の共有ストレージ（郵便ポスト）

## ライセンス

MIT License（`LICENSE` 参照）。無料で使える。保守はベストエフォートで、動作保証はない。

## 支援

役に立ったら GitHub Sponsors から支援してもらえると励みになります（リポジトリの Sponsor ボタン、または `.github/FUNDING.yml` 参照）。
GitHub アカウントが無くても、[Polar のチップページ](https://buy.polar.sh/polar_cl_BOe4K4TOobTs8OMsZXgl50XUbXUyXalCr0nxV3jPbbm) から金額自由（$1〜）で支援できます。
