# CollabSync for Unity
IGNORANZ PROJECT 制作の、**Unity エディタ専用・軽量コラボ支援ツール**です。  
プロジェクト内の「誰がどのファイルを編集しているか」「どのファイルにロックが掛かっているか」を、
シンプルな JSON ファイル 1つで共有します。

> 🔧 バックエンドは **ローカル JSON ファイルのみ** を前提とした構成です。  
> JSON 自体のリアルタイム共有には、別途ファイル同期ツール（例: OneDrive + FileSync）を利用します。

---

## ✨ 機能概要

### 1. Presence（編集中ユーザーの可視化）
- Unity エディタ上で
  - **どのユーザーが**
  - **どのシーン / アセットを編集しているか**
  を JSON に書き出し、他クライアントと共有します。
- Hierarchy 上には、他人が同じシーンを編集している場合に **⚠ / 🔒 アイコン** を表示。

### 2. Lock（ファイルロック）
- プロジェクト内のアセット（ファイル / フォルダ）に対して、**論理ロック** を設定できます。
- ロック情報も JSON に保存され、他のメンバーにも共有されます。
- ロックは以下の 2 経路で利用可能です:
  - Project ビューのコンテキストメニュー（右クリック → `CollabSync/Lock` / `Unlock`）
  - CollabSync ウィンドウの「Active Locks」一覧から、自分のロックのみ解除

### 3. Team Memos（チームメモ・既読管理付き）
- チーム内の共有メモを JSON 上で管理し、Unity ウィンドウから閲覧・追加・ピン留めができます。
- 各メモには **既読ユーザー一覧（Read by）** が付き、
  「誰が読んだか」を確認できます。
- 既読情報も JSON に保存されるため、全メンバー間で同期されます。

---

## 📂 フォルダ構成（例）

```text
Assets/
  IGNORANZ PROJECT/
    CollabSync/
      Runtime/
        CollabSyncConfig.cs
        CollabSyncTypes.cs        // Memo / Lock / Presence / State など
        LocalJsonBackend.cs       // JSON 読み書き・イベント通知
      Editor/
        CollabSyncWindow.cs       // メインウィンドウ（Activity / Memos タブ）
        HierarchyOverlay.cs       // Hierarchy に ⚠ / 🔒 を表示
        CollabSyncContextMenu.cs  // Project ビューの Lock/Unlock メニュー
        CollabSyncStateCache.cs   // スナップショットキャッシュ
        CollabSyncDoctor.cs       // JSON のチェック＆簡易修復ツール
```

※ 実際のファイル名・構成はプロジェクトに合わせて調整してください。

---

## 🔌 依存無しのシンプル設計

この CollabSync は、**Firebase などのクラウド SDK に依存せず**、
1本の JSON ファイルを介して状態を共有する構成になっています。

- バックエンドは **`LocalJsonBackend` のみ**
- JSON パス（デフォルト）:  
  `ProjectSettings/CollabSyncLocal.json`
- JSON の中身には
  - `presences` : ユーザーごとの編集中情報
  - `memos`     : チームメモ（既読情報付き）
  - `locks`     : ファイルロック
  - `updatedAt` : 最終更新時刻（Unix ms）
  が保存されます。

> 💡 JSON が共有さえされていれば、どのクライアントも同じ情報を見られます。  
> 「どうやって共有するか」は別ツールに任せる思想です。

---

## 🌐 JSON のリアルタイム共有について

CollabSync は **JSON をローカルに書き出すだけ** なので、
複数マシンで同じ JSON を使うには、別途ファイル同期が必要です。

### 推奨フロー例

1. Unity プロジェクトの `ProjectSettings/CollabSyncLocal.json` を、
   OneDrive / Dropbox / Google Drive などの **同期フォルダに置く**
2. もしくは、IGNORANZ PROJECT のファイル同期ツール **FileSync** を使って、
   JSON を同期フォルダとローカルの間で安全に同期する

---

## 📦 関連ツールの紹介

CollabSync をより快適に使うために、以下の 2 つのツールを併用することを強くおすすめします。

### 1. FileSync（ファイル同期ツール）
- リポジトリ: <https://github.com/IGNORANZ-PROJECT/FileSync>
- 役割:
  - ローカルの JSON ファイルと、OneDrive などの共有フォルダとの間で、
    **安全に同期** を取るためのスクリプト群です。
  - 差分チェックやバックアップを行いながら、
    「誰かの上書きで JSON が壊れる」リスクを下げます。
- おすすめの使い方:
  - CollabSync の JSON 用に **専用のフォルダ** を決めて、
    FileSync でそこだけ同期する
  - CRON / タスクスケジューラなどと組み合わせて定期実行

### 2. PythonRunner（Unity から Python 実行）
- リポジトリ: <https://github.com/IGNORANZ-PROJECT/PythonRunner>
- 役割:
  - Unity Editor から、ワンクリックで Python スクリプトを実行するためのツールです。
- CollabSync との組み合わせ例:
  - 「CollabSync 用 FileSync スクリプト」を Python で書いておき、
    - Unity メニューのボタン
    - もしくはホットキー
    から **直接 FileSync を叩けるようにする**
  - これにより、
    - Unity 起動 → CollabSync ウィンドウを開く
    - `Tools > PythonRunner > Run FileSync` などを実行
    - → JSON が最新状態に同期される
    という運用がしやすくなります。

> ✅ **ポイント**:  
> 「CollabSync = 状態を書くだけ」  
> 「FileSync = ファイルを運ぶ」  
> 「PythonRunner = Unity からそのファイル運びスクリプトを起動する」  
> という三層構造で考えると整理しやすいです。

---

## 🧰 使い方（Unity 側）

### 1. 初期セットアップ

1. `CollabSyncConfig` を作成・設定  
   - メニュー `IGNORANZ/CollabSyncConfig` などから作成（または自動生成）  
   - `localJsonPath` を適切な場所に指定  
     - 例: `ProjectSettings/CollabSyncLocal.json`  
     - 例: 外部同期フォルダ配下: `../Shared/CollabSync/CollabSyncState.json`

2. 「Tools > CollabSync > RunDoctor」を実行  
   - JSON の作成 / 読み書きテスト / スナップショットのブロードキャストを一括で確認できます。  
   - `[OK]` ログが出ていれば最低限の環境は整っています。

### 2. Activity / Locks を見る

- メニュー: `Tools > CollabSync > Open`
- タブ構成:
  - **Activity タブ**
    - 「現在編集中のユーザー」と「どのアセットを編集しているか」が一覧表示
  - **Memos タブ**
    - チームメモの閲覧 / 追加 / ピン留め / 既読管理

### 3. Lock / Unlock（Project ビュー）

- Project ウィンドウでファイル or フォルダを選択し、右クリック:
  - `Assets > CollabSync > Lock`
  - `Assets > CollabSync > Unlock`
- これにより、選択中のアセットに対して CollabSync JSON 上でロック情報を追加/削除します。
- ロックされているファイルは、他のメンバーの CollabSyncWindow の「Active Locks」に表示されます。

### 4. 既読管理付きメモ

- `Tools > CollabSync > Open` → `Memos` タブ
- メモを追加すると、
  - `author` と `createdAt` が自動で設定されます。
  - 作成者本人は自動的に既読扱いになります。
- `Mark as read` ボタンで、自分の既読フラグを立てることができます。

---

## ⚠ 運用上の注意

- **バイナリブレンドではない**  
  CollabSync は「誰が編集中か」「どこにロックがあるか」を共有するだけで、
  実際の Unity シーンやアセットのマージは行いません。
  - 大きな変更は、Git や Plastic SCM などの VCS と併用してください。
- **JSON の競合**  
  - 基本的には「最後に書いた人の状態」が反映されます。
  - ファイル同期ツール側（FileSync 等）のバックアップ・差分オプションを活用してください。
- **タイムスタンプ更新**  
  - JSON の `updatedAt` は CollabSync が自動で更新します。
  - 手動編集は推奨されません。

---

## 📄 ライセンス
MIT LICENSE
