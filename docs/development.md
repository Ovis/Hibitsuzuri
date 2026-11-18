# 開発手順

配布イメージを使う導入手順は [README](../README.md)、変更時に維持する仕様は [仕様・実装の申し送り](implementation-handoff.md) を参照。

## ソースから起動する

リポジトリ直下で作業する。.NET SDKの指定は [global.json](../global.json) を参照。JavaScriptのテストにはNode.js 24、コンテナの検証にはLinuxコンテナを実行できるDockerとCompose v2を使う。

~~~sh
dotnet restore Hibitsuzuri.slnx
dotnet run --project src/Hibitsuzuri.Web/Hibitsuzuri.Web.csproj -- --Site:PublicOrigin=http://localhost:5180 --Storage:Directory=../../data/local
~~~

起動プロファイルの `Development` で [http://localhost:5180](http://localhost:5180) を開く。`Storage:Directory` の相対パスはWebプロジェクトのコンテンツルート基準で、上の例ではリポジトリ直下の `data/local` が保存先になる。起動時にDBが作られ、初回登録コードはそのディレクトリの `setup-token.txt` に生成される。

ローカルHTTPは初回登録・日記編集などの確認に使う。パスキーやWeb Pushを配置時に近い条件で試すときは、共有テスト環境やローカルプロキシでブラウザが信頼できるHTTPSを用意する。

## 開発用のDocker Compose

リポジトリ直下の [compose-dev-sample.yml](../compose-dev-sample.yml) を使う。[.env.dev.sample](../.env.dev.sample) を `.env.dev` にコピーし、開発専用のディレクトリ・イメージ名・HTTPSオリジンを指定する。データ・バックアップ・ログの保存先を事前に作成し、設定したUID・GIDで書き込めるようにする。接続経路は [READMEのHTTPS設定](../README.md#2-httpsの接続経路を設定する) と同じ要件で用意する。プロキシ用ネットワークやポートの追加は、配置専用のComposeファイルとして分けることもできる。

~~~sh
docker compose --env-file .env.dev -f compose-dev-sample.yml config --quiet
docker compose --env-file .env.dev -f compose-dev-sample.yml build app
docker compose --env-file .env.dev -f compose-dev-sample.yml up -d --no-build --wait
docker compose --env-file .env.dev -f compose-dev-sample.yml ps
~~~

追加のComposeファイルを使う場合は、すべてのコマンドで基本ファイルの後ろに同じ `-f` を付ける。ディレクトリとコンテナの対応を確認する場合は、`docker compose ps --all --quiet app` に同じオプションを付け、取得したコンテナIDを `docker inspect` に渡す。配布用環境と開発用環境で同じデータディレクトリを共有しない。 [Composeファイルの結合](https://docs.docker.com/compose/how-tos/multiple-compose-files/merge/)

## テストと整形

~~~sh
dotnet test Hibitsuzuri.slnx --configuration Release --verbosity minimal -m:1 --disable-build-servers
node --test "tests/**/*.test.mjs"
dotnet format Hibitsuzuri.slnx --no-restore --verify-no-changes
~~~

.NET側は実SQLiteとテスト用Webホストを使い、HTTP経由で機能を確認する。Node側は日記の下書き、画像操作、パスキーの応答処理、提供元データ更新ツールを確認する。日時の境界にはテスト用時計を使い、通知テストでは実際の送信先に接続しない。

[CI](../.github/workflows/ci.yml) は `main` 向けのPRと `main` へのpushで起動し、タグpush時の検証にも使われる。.NET・Nodeのテストに加え、[検証用Compose](../.github/compose.ci.yml) で配布用Dockerfileをビルドする。空の保存先でヘルスチェックが通り、初回登録コードが生成されることまで確認する。

画面や端末に依存する変更は、検証用データを使って次を確認する。

| 変更箇所 | 追加で確認すること |
| --- | --- |
| 共通画面・日記編集 | PC・スマホ幅の表示、保存と再読み込み、下書き、同時編集、画像、編集履歴 |
| 繰り返しタスク・カレンダー | 月末・うるう日、祝日調整、完了済みの回、「今回のみ」「今回以降」、停止と再開 |
| 認証・通知 | 実際の認証器でパスキー登録と再ログイン、復旧、対応端末でのPush受信・クリック・ログアウト後の停止 |
| 出力・復元 | 検証専用アカウントで画像あり／なし、追加・全置き換え、Markdown上書き、エラー時の既存データ保持 |

## DBの変更

EF Coreのローカルツールは [ツールマニフェスト](../.config/dotnet-tools.json) で管理する。起動時にDB移行が走るため、スキーマを変更したら新規の保存先に加え、変更前の版で作った検証用DBからの更新も確認する。一度配布したマイグレーションは履歴として残し、次のマイグレーションで変更する。初期化時のDB作成とモデルの一致は [StorageTests](../tests/Hibitsuzuri.Tests/StorageTests.cs) を参照。

## リリースとイメージ配布

1. GitHub上で対象コミットが `main` にあり、CIが成功していることを確認する。
2. 対象コミットに `vX.Y.Z`、プレリリースの場合は `vX.Y.Z-beta` または `vX.Y.Z-betaN` のタグを付けてpushする。連番 `N` は1以上で先頭にゼロを付けない。
3. [リリース用Actions](../.github/workflows/release.yml) がタグのコミットでCIを実行し、成功するとドラフトを作る。リリースノートと公開種別を確認し、GitHub上で手動公開する。ベータのタグはプレリリースとして公開する。
4. 公開イベントでは、そのタグとコミットに対応するタグpush時のCI結果を確認する。成功したドラフト作成ジョブが見つかると、同じコミットをチェックアウトして `linux/amd64` イメージをビルドする。
5. GHCRの固定タグ `ghcr.io/ovis/hibitsuzuri:<Gitタグ>` でイメージを取得できることを確認する。初回配布時のみ、パッケージの設定画面で公開範囲をPublicへ変更する。その後、認証していない環境でpullし、[README](../README.md) の手順で新規起動・初回登録を確認する。 [GHCRの公開範囲](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility)

ドラフト作成に失敗した場合は、タグpush時のActionsを確認して再実行する。公開後のビルド・pushが失敗した場合は公開イベント側を再実行する。ドラフト作成の成功が見つからない場合は、先にタグpush時の実行を成功させる。コード修正が必要な場合は `main` に修正を取り込み、新しいバージョンのタグを使う。
