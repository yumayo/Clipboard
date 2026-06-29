.PHONY: artifact

GITHUB_RELEASE_URL := https://github.com/yumayo/Clipboard/releases/new
RUNTIME_IDENTIFIER := win-x64
APP_VERSION ?= $(shell git fetch origin --tags >/dev/null 2>&1; latest=$$(git tag --list 'v[0-9]*' --sort=-v:refname | head -n 1); test -n "$$latest" && echo "$$latest" | awk -F. 'BEGIN { OFS="." } { $$NF = $$NF + 1; print }' || echo v0.1)

artifact:
	rm -rf dist
	rm -rf Clipboard/log
	rm -rf Clipboard/bin
	dotnet.exe build Clipboard --configuration Release --runtime $(RUNTIME_IDENTIFIER) --no-self-contained -o dist
	(cd dist && zip -r ../Clipboard_${APP_VERSION}.zip .)
	git tag ${APP_VERSION}
	git push origin master
	git push origin ${APP_VERSION}
	explorer.exe . || true
	cmd.exe /c start "" "$(GITHUB_RELEASE_URL)" || true
