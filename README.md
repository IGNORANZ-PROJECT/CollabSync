# CollabSync

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

## 特徴

- Unity メニュー `Tools > CollabSync` から使えます
- 共有方法は「同じ JSON ファイルを全員が指定する」だけです
- 自動ロックと手動ロックの両方に対応しています
- メモは現在の選択対象だけでなく、URL や Asset Path にも紐付けできます
- 共有 JSON のバックアップを自動で作成し、古いものは自動整理します
- 共有 JSON 自体の書き込み競合を自動回避します
- Git の commit 系変更を検知すると、自動ロック解除と Pull 依頼メモを出せます
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
6. 取り込み後、CollabSync は自動で `Assets/IGNORANZ PROJECT/CollabSync/` に展開されます。
7. スクリプトの再コンパイルが終わったら、Unity メニューに `Tools > CollabSync` が出ていることを確認します。

Git URL 経由のインストールでも、最終的な配置先は `Assets/IGNORANZ PROJECT/CollabSync/` です。
外部パッケージの追加、別サービスのアカウント作成は不要です。

#### 代替: 手動で配置する

1. このリポジトリの中身を、Unity プロジェクト内の次の場所に配置します。
   `Editor` と `Runtime` が直下に来るようにしてください。

```text
Assets/
└─ IGNORANZ PROJECT/
   └─ CollabSync/
      ├─ Editor/
      └─ Runtime/
```

2. ZIP 展開後などでフォルダ名が `CollabSync-main` や `CollabSync-master` になっている場合は、`CollabSync` に揃えてください。
3. Unity を開くか、開いている場合はスクリプトの再コンパイルを待ちます。
4. Unity メニューに `Tools > CollabSync` が出ていることを確認します。

手動配置は、ローカルで直接編集しながら使いたい場合や、Package Manager を使わずに管理したい場合の代替手段です。

## 2. 共有ファイルの準備

CollabSync は、チーム全員が同じ JSON ファイルを使うことで同期します。
そのため、最初に「全員が見られる共有フォルダ」を 1 つ決めてください。

### 共有先の例

- OneDrive の共有フォルダ
- 社内 NAS / SMB 共有
- Dropbox や Google Drive のローカル同期フォルダ
- LAN 上の共有フォルダ

### 重要

- ここで指定するのは **Web URL ではなく、各 PC から見える実際のファイルパス** です
- `https://...` のような URL は `JSON Path` には使えません
- これは共有状態ファイルの設定に限った話で、`Memos` のリンク先として URL を使うことはできます
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
- URL / Asset Path を直接入力してメモに紐付け
- ピン留め
- 既読
- 検索
- `Unread only`
- `Pinned only`
- `Related to selection`
- URL が紐付いたメモを `Open Link` で開く
- Unity プロジェクト内の Asset Path が紐付いたメモを `Ping` で表示する

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

#### Notifications

- `Show memo alert bar`
- `Beep on new memo`

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

特徴:

- 必要な時だけ付きます
- 更新が止まると TTL で自然に失効します
- ローカルの `commit` / `merge` / `cherry-pick` で `HEAD` が変わると、自分の自動ロックは自動解除されます
- 同時に、チームメモへ「最新の変更が共有されていたら Pull をお願いします」という自動通知を投稿します

### 手動ロック

明示的にロックしたい場合は次を使います。

#### Tools メニュー

- `Tools > CollabSync > Lock Current Selection`
- `Tools > CollabSync > Unlock Current Selection`

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
これは `Settings` の `JSON Path` の話です。`Memos` のリンク先として URL を貼るのは問題ありません。

### Q. Memos に URL を貼れる？

貼れます。
`Memos` タブの `Link target` で `URL / Asset Path` を選ぶと、Web URL や Unity の Asset Path を直接紐付けできます。

### Q. Package Manager 追加後に `has no meta file, but it's in an immutable folder` が出る

PackageCache に古い Git パッケージ内容が残っている可能性があります。

対処:

1. Unity を閉じます
2. プロジェクト内の `Library/PackageCache/com.ignoranz.collabsync@...` フォルダを削除します
3. 必要なら `Packages/packages-lock.json` から `com.ignoranz.collabsync` を外すか、Package Manager で一度 `Remove` します
4. もう一度 `https://github.com/IGNORANZ-PROJECT/CollabSync.git` から追加します

正常に取り込めれば、CollabSync は自動で `Assets/IGNORANZ PROJECT/CollabSync/` に展開されます。

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

## 14. ライセンス

MIT License
© IGNORANZ PROJECT
