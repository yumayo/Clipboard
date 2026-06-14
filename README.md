# 説明
Clipboard.exeはクリップボードの変更通知を受けて、%APPDATA%\yumayo\yyyyMMdd\0001.txtに記録していく常駐アプリケーションです。
Ctrlを押し続けてコピーする場合、上書きではなく文字列を連結してクリップボードに保存されます。

# 使い方
Clipboard.exeをダブルクリックしてください。
タスクトレイにクリップボードのアイコンが表示されると思います。

# ビルド方法

```sh
wsl
make artifact APP_VERSION=v0.xx
```
