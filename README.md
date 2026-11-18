# Hibitsuzuri

Hibitsuzuriは、自分のサーバーで日記をつけ、過去の記録を振り返るためのWebアプリです。日記はログインした本人だけが閲覧できます。

- 日記帳ごとにMarkdownで記録し、画像を添付できます。編集履歴やゴミ箱から以前の記録に戻せます。
- カレンダー、本文検索、過去の同じ月日から日記を探せます。
- 繰り返しタスク、記念日、端末へのリマインダーを管理できます。
- 対応ブラウザではパスキーでログインできます。
- PWAでホーム画面にも追加できます。日記の閲覧・保存にはサーバーへの接続が必要です。
- 個人バックアップとMarkdown ZIPで、日記を手元に保存できます。

## Dockerで導入する

Linux x86-64、Docker Engine、Docker Compose v2、HTTPSを終端するリバースプロキシが必要です。アプリは1台のコンテナで起動し、データはホスト上のローカルディスクに保存します。

### 1. 設定と保存先を用意する

[GitHub Releases](https://github.com/Ovis/Hibitsuzuri/releases)で使うバージョンを選び、同じタグのソースアーカイブから [compose-sample.yml](compose-sample.yml) と [.env.sample](.env.sample) を取り出します。配置先で次を実行します。

~~~sh
cp compose-sample.yml compose.yml
cp .env.sample .env
sudo install -d -m 0700 -o 1654 -g 1654 data backups logs
~~~

`.env` を編集し、`HIBITSUZURI_IMAGE` の `vX.Y.Z` を公開済みのタグに、`PUBLIC_ORIGIN` をブラウザで開くHTTPSのURLに置き換えます。URLは `https://diary.example.com` のようにサブパスを付けずに指定します。

`DATA_DIRECTORY=./data`、`BACKUP_DIRECTORY=./backups`、`LOG_DIRECTORY=./logs` に設定します。別の場所に保存する場合は、対応するディレクトリを事前に作成します。既定のコンテナ実行ユーザーは `1654:1654` です。`APP_UID`・`APP_GID` を変える場合は、ディレクトリの所有者も合わせます。暦日と通知時刻には `HIBITSUZURI_TIMEZONE` が使われ、既定値は `Asia/Tokyo` です。

### 2. HTTPSの接続経路を設定する

リバースプロキシでTLSを終端し、すべてのパスをアプリのHTTPポート `8080` に転送します。元の `Host` を維持し、HTTPS接続ではプロキシが `X-Forwarded-Proto: https` を設定してください。

- プロキシもコンテナで動かす場合は、既存のプロキシ用ネットワークへ `app` を接続し、同じネットワーク上のアプリへ転送します。アプリのHTTPポートをホストへ公開する必要はありません。
- プロキシをDockerホスト上で動かす場合は、`compose.yml` の `app` に `ports: ["127.0.0.1:18080:8080"]` を追加し、`http://127.0.0.1:18080` へ転送します。

サンプルの `.env` にある `DeploymentManaged` は、アプリに届く接続を信頼できるプロキシに限定する配置向けです。接続元IPをアプリ側でも限定する場合は `REVERSE_PROXY_MODE=KnownProxies` に変更し、Composeの `app.environment` に `ReverseProxy__KnownProxies__0` として直近のプロキシのIPアドレスを追加します。

画像や復元ZIPのアップロードが途中で失敗する場合は、プロキシのリクエスト容量とタイムアウトも確認してください。

### 3. 起動して最初の管理者を登録する

~~~sh
docker compose config --quiet
docker compose pull app
docker compose up -d --wait
docker compose ps
~~~

`PUBLIC_ORIGIN` を開いて初回登録画面へ進みます。管理者の登録には、データディレクトリに生成された `setup-token.txt` の1行目を使います。上の配置例では次のコマンドで確認できます。

~~~sh
sudo head -n 1 data/setup-token.txt
~~~

初回登録コードは24時間有効です。期限切れの場合は、初回登録が済む前に `docker compose restart app` で再発行します。管理者の登録が成功するとコードは失効します。続けて画面に表示されるリカバリーコードを安全な場所に保存し、保存確認を完了してください。

登録後は日記帳を作成して使い始められます。パスキーの追加、通知の有効化、追加ユーザーの登録受付は設定画面で管理できます。

## バックアップと復元

「設定 → エクスポート・バックアップ・復元」から操作します。

| 形式 | 主な用途 | 取り込み時の動作 |
| --- | --- | --- |
| 個人バックアップZIP | 日記帳・日記・履歴・テンプレート・タスク・記念日をまとめて保存 | 既存データを残して追加するか、ゴミ箱を含む記録全体を置き換えるかを選ぶ |
| Markdown ZIP | 日記の現在の本文をファイルとして保存し、外部でも編集する | 日記帳ごとに取り込み先を選び、同じ日付の本文を上書きする。上書き対象の編集履歴は削除される |

どちらも画像の有無を選択できます。画像なしの個人バックアップから画像の実体は戻りません。画像なしのMarkdown ZIPでは、同じアカウントに元画像が残っていれば再利用できます。1回の上限は圧縮後2GB、展開後4GBです。ZIPは暗号化されないため、保管先を管理してください。

Markdownを書き出してから編集する場合は、日記の本文を変更し、ZIP内の構成情報・日記帳フォルダー・日付付きファイル名・日付ヘッダーを残して取り込んでください。

サーバー障害に備えるには、アプリを停止した状態で、設定とデータディレクトリ全体を別の場所にコピーします。データディレクトリにはSQLite、画像、暗号鍵が含まれます。アプリ内の個人バックアップには認証情報が含まれません。Composeの `backups` は、運用側が作成したバックアップを置くための保存先です。

## 更新する

1. [リリースノート](https://github.com/Ovis/Hibitsuzuri/releases)で更新先の変更点を確認します。
2. `docker compose stop app` で停止し、設定とデータディレクトリをバックアップします。
3. `.env` の `HIBITSUZURI_IMAGE` を更新し、`docker compose pull app` と `docker compose up -d --wait` を実行します。
4. 起動後にログインし、日記と画像が開くことを確認します。起動に失敗した場合は `docker compose logs --tail 100 app` で状態を確認できます。

## ライセンス

- [MIT License](LICENSE)

## 開発向け資料

- [開発手順](docs/development.md)
- [仕様・実装の申し送り](docs/implementation-handoff.md)
