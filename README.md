# 説明
Clipboard.exeはクリップボードの変更通知を受けて、%APPDATA%\yumayo\clipboard.dbに履歴を記録していく常駐アプリケーションです。
旧バージョンの%APPDATA%\yumayo\clipboard配下の履歴とsettings.jsonは、起動時にSQLiteへ移行されます。
Ctrlを押し続けてコピーする場合、上書きではなく文字列を連結してクリップボードに保存されます。

# 使い方
Clipboard.exeをダブルクリックしてください。
タスクトレイにクリップボードのアイコンが表示されると思います。

Windows + Cを押すと、その時点のクリップボードの内容を最前面のウィンドウで表示します。
テキスト、画像、HTML・RTFのテキスト、コピーしたファイルのパスを表示できます。
表示中にWindows + Cを押すと内容を更新し、Escまたは閉じるボタンで閉じます。
テキストはShift + マウスホイールで横スクロールできます。

# ビルド方法

```sh
wsl
make artifact
```
