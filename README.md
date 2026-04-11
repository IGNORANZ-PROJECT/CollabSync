# CollabSync

リリースバージョン: `ver.0.2.5`

正式リリース版です。

CollabSync は、Unity Editor 上で「今だれが何を触っているか」を見える化し、
共有ファイルの競合や編集の衝突を減らすための軽量な共同編集サポートツールです。

専用サーバーや Firebase は不要です。
チームで 1 つの JSON ファイルを共有するだけで、次の情報を同期できます。

- オンライン中のユーザー
- だれがどの Scene / Prefab / Asset を編集中か
- ロック状態
- チームメモ
- 作業履歴
- 管理者権限

## 目次

- [特徴](#特徴)
- [1. インストール](#1-インストール)
- [2. 共有ファイルの準備](#2-共有ファイルの準備)
- [3. 最初のセットアップ](#3-最初のセットアップ)
- [4. User ID について](#4-user-id-について)
- [5. ふだんの使い方](#5-ふだんの使い方)
- [6. ロックの使い方](#6-ロックの使い方)
- [7. 権限の考え方](#7-権限の考え方)
- [8. バックアップと競合回避](#8-バックアップと競合回避)
- [9. Doctor の使いどころ](#9-doctor-の使いどころ)
- [10. おすすめ運用](#10-おすすめ運用)
- [11. よくある質問](#11-よくある質問)
- [12. JSON 構造の例](#12-json-構造の例)
- [13. トラブルシューティング](#13-トラブルシューティング)
- [リリースノート](#リリースノート)
- [14. ライセンス](#14-ライセンス)

## 特徴

- Unity メニュー `Tools > CollabSync` から使えます
- 共有方法は「同じ JSON ファイルを全員が指定する」だけです
- 自動ロックと手動ロックの両方に対応しています
- 共有 JSON のバックアップを自動で作成し、古いものは自動整理します
- 共有 JSON 自体の書き込み競合を自動回避します
- `User ID` ベースでユーザーを識別するため、表示名を変えても別ユーザー扱いになりません

## 1. インストール

### 必要環境

- Unity 2021.3 以降
- macOS または Windows
- チーム全員がアクセスできる共有フォルダ

### 導入手順

#### 推奨: URL で追加する

Git URL:

`https://github.com/IGNORANZ-PROJECT/CollabSync.git`

手順:

1. Unity で `Window > Package Manager` を開きます。
2. 左上の `+` ボタンを押します。
3. `Add package from git URL...` を選びます。
4. 次の URL を貼り付けます。

`https://github.com/IGNORANZ-PROJECT/CollabSync.git`

5. `Add` を押して取り込みます。
6. スクリプトの再コンパイルが終わったら、Unity メニューに `Tools > CollabSync` が出ていることを確認します。

外部パッケージの追加、別サービスのアカウント作成は不要です。

`Git URL` で導入する場合は、同じ内容を `Assets/IGNORANZ-PROJECT` に手動配置しないでください。
Package 導入と手動配置を同時に行うと、GUID conflict や二重読込の原因になります。

#### 代替: 手動で配置する

1. このリポジトリ内の `Editor` フォルダと `Runtime` フォルダを、Unity プロジェクト内の次の場所に配置します。
   `Editor` と `Runtime` が直下に来るようにしてください。
   `package.json` は手動配置では不要です。

```text
Assets/
└─ IGNORANZ-PROJECT/
   └─ CollabSync/
      ├─ Editor/
      └─ Runtime/
```

2. ZIP 展開後などでフォルダ名が `CollabSync-main` や `CollabSync-master` になっている場合は、その中にある `Editor` と `Runtime` を使ってください。
3. Unity を開くか、開いている場合はスクリプトの再コンパイルを待ちます。
4. Unity メニューに `Tools > CollabSync` が出ていることを確認します。

手動配置は、ローカルで直接編集しながら使いたい場合や、Package Manager を使わずに管理したい場合の代替手段です。
すでに `Git URL` で導入している場合は、手動配置版を同時に置かないでください。

## 2. 共有ファイルの準備

CollabSync は、チーム全員が同じ JSON ファイルを使うことで同期します。
そのため、最初に「全員が見られる共有フォルダ」を 1 つ決めてください。

### 共有先の例

- OneDrive の共有フォルダ
- 社内 NAS / SMB 共有
- Dropbox や Google Drive のローカル同期フォルダ
- LAN 上の共有フォルダ

### 重要

- 指定するのは **Web URL ではなく、各 PC から見える実際のファイルパス** です
- `https://...` のような URL は使えません
- チーム全員が **同じ内容に同期される同じ JSON ファイル** を指定する必要があります

### パス例

- Windows:
`C:\Users\<USER>\OneDrive\IGNORANZ\CollabSync\state.json`
- macOS:
`/Users/<USER>/Library/CloudStorage/OneDrive-<Tenant>/PROJECT/CollabSync/state.json`
- NAS / 共有:
`\\SERVER\Share\PROJECT\CollabSync\state.json`

## 3. 最初のセットアップ

### 3.1 共有ファイルを最初に作る人

最初に共有 JSON を作成して書き込んだユーザーは、自動的に `Root Admin` になります。

手順:

1. Unity で `Tools > CollabSync` を開きます
2. `Settings` タブを開きます
3. `Your Name` に自分の表示名を入れます
4. `JSON Path` の `New...` を押して、新しい共有 JSON ファイルを作ります
5. `Resolved Local JSON` に想定どおりのパスが出ていることを確認します
6. `Run Doctor` を実行して、共有ファイルの作成と読み書きが正常か確認します

ここまで終わったら、その JSON ファイルのパスをほかのメンバーに共有してください。

### 3.2 すでにある共有に参加する人

手順:

1. `Tools > CollabSync` を開きます
2. `Settings` タブを開きます
3. `Your Name` に表示名を入れます
4. `JSON Path` の `Choose...` を押して、チームと同じ共有 JSON ファイルを選びます
5. `Resolved Local JSON` が正しい場所を指していることを確認します
6. 必要なら `Run Doctor` を実行します

## 4. User ID について

CollabSync では、各ユーザーに自動で固定の `User ID` が発行されます。
この `User ID` は `Settings` の `Your User ID` で確認できます。

### User ID を使う理由

- 表示名を変更しても同じユーザーとして扱うため
- 管理者権限を表示名ではなく固有 ID で管理するため
- 既読、ロック所有者、作業履歴を安定して追跡するため

### 大事なポイント

- 表示名を変えても、別ユーザーにはなりません
- 管理者追加は **名前ではなく User ID** で行います
- Root Admin がユーザーを削除すると、その `User ID` からの再書き込みをブロックできます

## 5. ふだんの使い方

CollabSync のメイン UI は 4 タブです。

- `Overview`
- `Details`
- `Memos`
- `Settings`

### 5.1 Overview

まず最初に見るべきタブです。
必要最低限の情報だけを表示します。

#### Online

- 現在のオンライン人数を表示します
- 接続に問題がある時はここに警告が出ます
- パネル全体をクリックすると、だれが今どの Scene / Prefab / Asset を編集中か展開表示します

#### Unread / Pinned Memos

- 未読メモとピン留めメモを表示します
- `Mark as read` でその場で既読にできます
- 新着メモが来ると、ウィンドウ上部に通知バーが表示されます

#### Active Locks

- 現在のロック一覧を表示します
- 自分のロックなら `Unlock`
- 他ユーザーのロックなら `Request Unlock`
- `Only related to my work` をオンにすると、自分の現在の作業に関係するロックだけに絞れます

### 5.2 Details

必要な時に詳しく見るためのタブです。

#### Selection Status

今選択している Scene / Prefab / Asset / GameObject に対して、次の情報を表示します。

- だれが編集中か
- どのロックが影響しているか
- 関連メモ数
- 自分が編集中かどうか

#### Users

ユーザー一覧は開閉できます。
開くと各ユーザーごとに次を確認できます。

- オンライン / オフライン
- 現在の作業対象
- ロック数
- 作業履歴
- User ID

`Work History` を押すと、そのユーザーの最近の履歴を展開します。

Root Admin はここから `Delete User` を実行できます。

#### Lock Manager

ロックの詳細管理を行う場所です。

- `All / Mine / Blocking Me` の絞り込み
- 検索
- `Unlock All Mine`
- 管理者による `Force Unlock`

### 5.3 Memos

チームメモを扱うタブです。

できること:

- 新しいメモを追加
- 現在の選択対象にメモを紐付け
- Markdown 表示
- ピン留め
- 既読
- 検索
- `Unread only`
- `Pinned only`
- `Related to selection`

通常削除は投稿者本人のみ可能です。
管理者は他ユーザーのメモに対して `Force Delete` を使えます。

### 5.4 Settings

初回設定と管理機能はこのタブにまとまっています。

#### User

- `Your Name`
- `Your User ID`

#### Language

- `System Default`
- `English`
- `Japanese`

`System Default` は Unity の言語設定ではなく、PC の UI 言語を優先して判定します。
取得できない場合は `PC の既定 UI 言語`、次に `PC カルチャ` を参照し、最後に英語へフォールバックします。
Unity `6000.0.68f1` 以前の 6000.0 系では、日本語 UI 描画時に既知の IMGUI 問題が出ることがあるため、可能なら `6000.0.69f1` 以降を推奨します。

#### Administrators

- 現在の管理者一覧
- Root Admin の確認
- Root Admin による管理者追加 / 削除
- `Global Work History` のオン / オフ

#### Shared State File

- `JSON Path`
- `Choose...` で既存ファイルを指定
- `New...` で新規作成
- `Resolved Local JSON` で最終的に使われる実パスを確認

JSON パスなどの設定は、初回使用時にプロジェクト側の `Assets/IGNORANZ-PROJECT/CollabSyncSettings/Resources/CollabSyncConfig.asset` に保存されます。
そのため、今後の CollabSync 更新で設定が上書きされにくくなっています。

#### Notifications

- `Show memo alert bar`
- `Beep on new memo`

#### Git-aware Locks

- `Keep retained locks until merged`

この設定をオンにすると、手動ロックは解除操作をしてもすぐ消えず、記録済みコミットが保護ブランチに入るまで `Retained` 状態で保持されることがあります。
オフにすると、従来どおり解除操作でその場でロックを消します。

#### Backup Snapshots

- バックアップ一覧の確認
- バックアップフォルダを開く

#### Doctor

- `Run Doctor`
- `Copy Result`
- 問題に応じた解決方法の表示

## 6. ロックの使い方

CollabSync には「自動ロック」と「手動ロック」があります。

### 自動ロック

編集中の dirty な対象に対して、自動で最低限のロックを付けます。

対象例:

- Scene
- Prefab
- GameObject
- Asset
- Script

特徴:

- 必要な時だけ付きます
- 通常の Inspector 編集は object 単位を優先します
- Scene 全体設定の変更は scene 単位ロックになることがあります
- 更新が止まると TTL で自然に失効します
- Git の commit で `HEAD` が変わると、自分の自動ロックは自動解除されます
- Git の commit を検知すると、チームメモに `PushされましたPullをしてください` が自動投稿されます

補足:

- `ProjectSettings` や `Packages/manifest.json` のような project-wide な変更は、深刻な競合を避けるため早めの Push を勧告する warning が出ます

### 手動ロック

明示的にロックしたい場合は次を使います。

#### Tools メニュー

- `Tools > CollabSync` でパネルを開く

#### Project ウィンドウ右クリック

- `CollabSync/Lock`
- `CollabSync/Unlock`
- `CollabSync/Toggle Lock`

#### Hierarchy / GameObject メニュー

- `GameObject/CollabSync/Lock Object (and Scripts)`
- `GameObject/CollabSync/Unlock Object (and Scripts)`
- `GameObject/CollabSync/Toggle Object Lock (and Scripts)`

### ロックの見え方

- `Hierarchy`
- `🔒` 他ユーザーのロック
- `🔐` 自分のロック
- `⚠` 他ユーザーが編集中
- `Project`
- `🔒` 他ユーザーのロック
- `🔐` 自分のロック

### スクリプトの保護

他ユーザーがロックしている `.cs` は、自分の環境で自動的に読み取り専用になります。
これにより、誤って編集する事故を減らします。
また、Unity から script を開いた時や、選択中の script を保存した時は auto-lock が付きやすくなるようにしています。

## 7. 権限の考え方

### 通常ユーザー

- 自分のロックを解除できる
- メモを投稿できる
- 自分のメモを削除できる
- 他ユーザーのロックには `Request Unlock` を送れる

### Admin

通常ユーザーの権限に加えて:

- 他ユーザーのロックを `Force Unlock` できる
- 他ユーザーのメモを `Force Delete` できる
- `Global Work History` を切り替えできる

### Root Admin

Admin の権限に加えて:

- Admin の追加
- Admin の削除
- ユーザー削除

### ユーザー削除

Root Admin の `Delete User` は、単に一覧から消すだけではありません。

- 現在のプレゼンスを削除
- 現在のロックを削除
- 管理者なら管理者権限も削除
- その `User ID` を削除済みとして記録
- 以後、その `User ID` からの新しい書き込みを拒否

## 8. バックアップと競合回避

CollabSync は共有 JSON 自体の安全性にも配慮しています。

### 自動バックアップ

- ロックやメモなど、意味のある変更があった時だけ保存
- Presence だけの更新ではバックアップを増やさない
- バックアップは `.collabsync-backups/` に保存
- 古い世代は自動整理

### 共有 JSON の競合回避

- 排他ロック付きの read-modify-write で更新
- 同時更新が起きても JSON が壊れにくい設計
- JSON が壊れていた場合は有効なバックアップを使って復旧を試行

## 9. Doctor の使いどころ

`Settings > Doctor` は、共有ファイルの診断機能です。

こんな時に使ってください。

- 初回セットアップ直後
- 他メンバーが接続できない時
- JSON パスを変更した時
- 共有ドライブが不安定な時
- 何かおかしいけど原因が分からない時

Doctor で確認する内容:

- JSON の生成可否
- 読み書き可否
- 基本構造の妥当性
- 最新スナップショットの配信

結果は `Copy Result` でそのままコピーできます。
問題が見つかった場合は、UI 内に解決方法も表示されます。

## 10. おすすめ運用

迷ったら次の運用をおすすめします。

1. 作業前に `Overview` を見る
2. 重要な対象は先に手動ロックする
3. 他ユーザーのロックがあれば無理に触らず `Request Unlock` を使う
4. 補足や引き継ぎは `Memos` に残す
5. 異常時は `Settings > Run Doctor`

## 11. よくある質問

### Q. OneDrive の URL をそのまま貼ってもいい？

いいえ。
必要なのは URL ではなく、各 PC から見えるローカル同期フォルダ上の実ファイルパスです。

### Q. 表示名を変えたら別ユーザーになる？

なりません。
CollabSync は内部の `User ID` でユーザーを識別します。

### Q. 他人のロックを解除できない

通常仕様です。
自分のロックだけ解除できます。ほかは `Request Unlock` を使ってください。
強制解除できるのは管理者だけです。

### Q. Root Admin を削除できる？

できません。
Root Admin は削除対象になりません。

### Q. 削除されたユーザーはまた参加できる？

同じ `User ID` のままでは参加できません。
Root Admin が削除済みユーザーとしてブロックした ID からの書き込みは拒否されます。

## 12. JSON 構造の例

```jsonc
{
"updatedAt": 1730000000000,
"presences": [
{
"userId": "a3d9...",
"user": "Alice",
"assetPath": "Assets/Scenes/Main.unity",
"context": "Scene",
"heartbeat": 1730000000000
}
],
"locks": [
{
"assetPath": "Assets/Scripts/",
"ownerId": "b1f2...",
"owner": "Bob",
"reason": "context-menu",
"createdAt": 1730000000000,
"ttlMs": 0
}
],
"adminUserIds": [
"a3d9..."
],
"adminUsers": [
"Alice"
],
"blockedUserIds": [
"c8e4..."
],
"blockedUsers": [
"Charlie"
],
"rootAdminUserId": "a3d9...",
"rootAdminUser": "Alice",
"workHistoryMode": "enabled",
"memos": [
{
"id": "a1b2c3",
"text": "新しいシーン構成を確認",
"authorId": "a3d9...",
"author": "Alice",
"assetPath": "Assets/Scenes/Main.unity",
"createdAt": 1730000000000,
"pinned": true,
"readByUserIds": ["b1f2..."],
"readByUsers": ["Bob"]
}
]
}
```

## 13. トラブルシューティング

| 症状 | 対処 |
| --- | --- |
| JSON が更新されない | `Settings > Resolved Local JSON` を確認し、共有パスとアクセス権を見直してください |
| JSON パスがおかしい | `Settings > JSON Path > Choose...` で実在する共有ファイルを選び直してください |
| JSON が壊れた | 共有 JSON と同じフォルダの `.collabsync-backups/` を確認してください |
| ReadOnly が解除されない | `ScriptLockEnforcer` が動作しているか、現在も他ユーザーのロックが残っていないか確認してください |
| 他人のロックが解除できない | 通常仕様です。自分のロックのみ解除できます |
| 強制解除ボタンが出ない | 自分が Admin 以上か確認してください |
| 管理者追加やユーザー削除ができない | 自分が Root Admin か確認してください |
| 作業履歴が増えない | `Settings > Global Work History` が無効になっていないか確認してください |
| アイコンが出ない | Editor Console のエラー確認、または `Window > Layouts` のリセットを試してください |

## リリースノート

### ver.0.2.5

- `EditingTracker` 内の compile error を修正し、package import 時に `CS0136` で失敗しないように調整
- `0.2.4` で見直した lock scope / destructive operation guard 周辺の実装を整理し、README 上のリリース表記を最新化

### ver.0.2.4

- Lock 情報に `scopeAssetPath` を追加し、object lock がどの scene / prefab に属するかを保持できるように改善
- 自動ロックを selection / dirty の広すぎる判定から見直し、Inspector の property 変更と hierarchy 変更を分けて追跡するように修正
- scene / prefab の hierarchy 変更は親 asset 単位で扱い、通常の object 編集は object 単位を優先するように整理
- Asset の delete / move 前に競合 lock を確認し、scene / prefab 内 object lock を含む危険な破壊操作を止められるように改善
- Project / Window 側でも object lock の親 asset を辿って関連 lock を見やすく調整

### ver.0.2.3

- Inspector 編集時の自動ロック粒度を見直し、通常の GameObject / Component 編集は object 単位で扱いやすく調整
- Scene 全体に関わる設定変更は scene 単位ロックとして扱えるように調整
- Presence に object 単位ターゲット情報を持たせ、他オブジェクトまで scene 全体が編集中に見えにくいように改善
- `ProjectSettings` や `Packages/manifest.json` など project-wide な危険変更を検知した時、早めの Push を勧告する warning を追加

### ver.0.2.2

- `.cs` の自動ロック検知を見直し、script を Unity から開いた時や、選択中 script の保存を検知した時に auto-lock が付きやすいように修正
- Lock / Presence の文脈表示で `.cs` を `Script` として扱うように調整

### ver.0.2.1

- `Runtime/CollabSyncGitUtility.cs.meta` を追加し、Unity Package import 時に `CollabSyncGitUtility` が無視されて compile error になる問題を修正
- `Git-aware Locks` 関連ファイルが immutable な `PackageCache` 内でも正しく認識されるように調整

### ver.0.2.0

- GitHub 向けの `Retained Lock` 機能を追加し、手動ロックを保護ブランチ反映まで保持できるように改善
- `Settings > Git-aware Locks` に `Keep retained locks until merged` を追加し、この機能をオン / オフ切り替えできるように変更
- Lock UI に `State` / `Git` / `Retained Since` を追加し、保持中ロックの状態が分かりやすいように改善
- ロック解除ボタンの文言とツールチップを見直し、`Release Retained` / `Waiting For Merge` など状態に応じた表示に調整

### ver.0.1.12

- Overview のメモ本文表示を詳細タブと同じ Markdown 描画に揃え、概要側でも見出しや強調が反映されるように修正
- 新着メモ通知バーの本文表示にも Markdown 描画を適用
- Overview のピン留めタグ表記を `📌` に変更し、Memos タブの表示と揃えるように調整

### ver.0.1.11

- Overview の未読 / ピン留めメモ表示にも Markdown 描画を適用し、概要欄でも本文の装飾が反映されるように修正
- `JSON Path` の保存処理を見直し、Settings で選択・入力した共有 JSON パスがアップデートや再読み込み後に既定値へ戻りにくいように修正
- 設定参照を `CollabSyncConfig.LoadOrCreate()` に統一し、保存フック側でも project 側設定を確実に使うように修正

### ver.0.1.10

- `CollabSyncWindow` 内のオンライン・ロック・ユーザー集計を短時間キャッシュし、不要な再計算を減らして表示負荷を軽量化
- Project / Hierarchy の overlay 判定を索引ベースに見直し、各行描画時の全件走査を減らして安定化
- `FileSystemWatcher` の更新通知をデバウンスし、共有 JSON 更新時の再読込・再描画の連打を抑えて安定化
- `CollabSyncConfig` と言語判定のキャッシュを追加し、Editor 上での無駄な探索処理を削減

### ver.0.1.9

- 自分の最新 heartbeat を UI 側でも補完するようにし、自分がオンラインなのにオフライン表示になることがある問題を修正
- 旧 name-only ユーザー情報と現在の `userId` 付きユーザー情報を統合し、同一ユーザーが重複して見える問題を修正
- `CollabSyncConfig` をプロジェクト側の `Assets/IGNORANZ-PROJECT/CollabSyncSettings/Resources/CollabSyncConfig.asset` に保存するように変更し、アップデート時に `JSON Path` などの設定が戻りにくいように修正

### ver.0.1.8

- 自分が編集中ターゲットを持っていない時でも presence を送るようにし、オンライン数が `0` になりやすい問題を修正
- 自動言語判定で OS の実際の言語設定を優先して参照するように見直し、Unity 側の言語値に引きずられないように修正
- メモ本文の Markdown 表示に対応
- メモ本文をメタ情報より大きく表示するように調整

### ver.0.1.7

- `System Default` が Unity の `Application.systemLanguage` ではなく、PC の言語設定を優先して判定するように修正
- 自動判定は `PC UI 言語` → `PC の既定 UI 言語` → `PC カルチャ` の順で参照するように整理

### ver.0.1.6

- ファイル位置の変更

### ver.0.1.5

- `System Default` の言語判定を見直し、OS の UI 言語を優先して自動判定するように修正
- 現在どの言語が選ばれているかを `Settings > Language` で明示表示
- Unity `6000.0.68f1` 以前の 6000.0 系で日本語 UI 利用時に出る既知の IMGUI 問題に警告を追加
- パッケージ導入直後の設定アセット生成で保存処理が再入する問題を修正
- `Runtime` / `Editor` の asmdef を追加し、導入時のコンパイル境界を整理
- `Git URL` 導入と手動配置の二重導入を避ける注意書きを README に追加

### ver.0.1.4

- 正式リリース向けの軽量化と安定性調整
- `Tools > CollabSync` でパネルを直接開けるように修正
- ロック操作をパネルと右クリックメニューに整理
- Git の commit 検知時に共有メモ `PushされましたPullをしてください` を自動投稿
- 設定アセットの読込経路を見直し、ウィンドウ表示時の安定性を改善

## 14. ライセンス

MIT License
© IGNORANZ PROJECT
