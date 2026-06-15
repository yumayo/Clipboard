.PHONY: artifact

GITHUB_RELEASE_URL := https://github.com/yumayo/Clipboard/releases/new
RUNTIME_IDENTIFIER := win-x64

artifact:
	rm -rf dist
	rm -rf Clipboard/log
	rm -rf Clipboard/bin
	dotnet.exe build Clipboard --configuration Release --runtime $(RUNTIME_IDENTIFIER) --no-self-contained -o dist
	(cd dist && zip -r ../Clipboard_${APP_VERSION}.zip .)
	git.exe tag ${APP_VERSION} || true
	git.exe push origin master
	git.exe push origin --tags
	explorer.exe . || true
	cmd.exe /c start "" "$(GITHUB_RELEASE_URL)" || true
