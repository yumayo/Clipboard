# 説明
Clipboard.exeはクリップボードの変更通知を受けて、%APPDATA%\yumayo\clipboard.dbに履歴を記録していく常駐アプリケーションです。
旧バージョンの%APPDATA%\yumayo\clipboard配下の履歴とsettings.jsonは、起動時にSQLiteへ移行されます。
Clipboard経由で書き込んだ内容（結合・表形式への変換・画像編集・履歴からの復元）は、新しい履歴には追加しません。
画像編集では元の履歴の画像と編集状態を更新し、その項目から編集後の画像を貼り付けたり、再編集したりできます。
複数画像の編集結果は、選択した履歴のうち一覧で先頭の項目に保存します。

# 使い方
Clipboard.exeをダブルクリックしてください。
タスクトレイにクリップボードのアイコンが表示されると思います。

Windows + Vでクリップボードの履歴を表示します。
検索欄に入力すると履歴を検索します。検索中は進捗バーと見つかった件数を表示し、完了すると一致した件数を表示します。

Windows + Cでクリップボードの積み上げを開始します。開始時は0件で、その後にコピーしたテキストを順番にためます。
積み上げ中はマウスカーソルの右上にスナップ数を表示します。同じテキストを再コピーした場合も1件として追加します。
Windows + Vで積み上げたテキストをコピーした順にまとめて貼り付け、積み上げを終了します。この間は履歴画面を表示しません。
区切りには設定の「連結文字」を使用します。HTML・RTFはテキストに変換し、画像は対象外です。
Escで貼り付けずに積み上げを取り消せます。積み上げ中に再びWindows + Cを押しても、ためた内容は保持します。
0件のままWindows + Vを押した場合は、貼り付けずに積み上げを終了します。終了後のWindows + Vは通常どおり履歴を表示します。
従来のWindows + 数字 + Vによる件数指定は廃止しました。

Windows + Fを押すと、その時点のクリップボードの内容を最前面のウィンドウで表示します。
テキスト、画像、HTML・RTFのテキスト、コピーしたファイルのパスを表示できます。
表示中にWindows + Fを押すと内容を更新し、Escまたは閉じるボタンで閉じます。
Shift + マウスホイールで横スクロールできます。
Ctrl + マウスホイールでテキストや画像を拡大・縮小し、Ctrl + 0で100%に戻します。
画像は最初はウィンドウに収めて表示し、100%では元のサイズで表示します。

# 開発用コンテナ

`docker-compose.yml` に .NET 10 SDK の開発コンテナを用意しています。
ホスト側でリポジトリのルートから起動してください。

```sh
docker compose up -d dev
```

初回起動では `mcr.microsoft.com` からSDKイメージを取得します。
初回ビルドでは開発コンテナからNuGetへ接続し、依存パッケージとWindows向けの参照パックを取得します。
NuGetパッケージはDockerボリュームに保存されます。

コンテナ名はComposeの既定の `clipboard-dev-1` を使用します。
`.aicontainer` ではDockerプロキシ名 `dev`、実行先 `clipboard-dev-1`、許可コマンド `dotnet` を使用します。
ホスト側でこの設定を許可してAIコンテナを再作成した後、AIコンテナ内から次のコマンドで確認・ビルドできます。
開発コンテナ内のリポジトリは `/workspace` にあるため、`dp` には `-c /workspace` を指定してください。

```sh
dp -c /workspace dotnet --info
dp -c /workspace dotnet build Clipboard/Clipboard.csproj --configuration Release --runtime win-x64 --no-self-contained
```

LinuxからWPFをビルドできるように、開発コンテナの環境変数で `EnableWindowsTargeting=true` を指定しています。
アプリの起動や動画再生など、WPFのGUI動作確認にはWindows環境が必要です。

# ビルド方法

```sh
wsl
make artifact
```
