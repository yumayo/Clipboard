.PHONY: artifact

artifact:
	rm -rf dist
	rm -rf Clipboard/log
	rm -rf Clipboard/bin
	dotnet.exe build Clipboard --configuration Release -o dist
	(cd dist && zip -r ../Clipboard_${APP_VERSION}.zip .)
	git.exe tag ${APP_VERSION} || true
	git.exe push origin master
	git.exe push origin --tags
	explorer.exe . || true
	echo https://github.com/yumayo/Clipboard/releases/new
