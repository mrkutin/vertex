# SemVer version stamped into Go binaries via -ldflags "-X main.Version".
# Bump per CLAUDE.md "Versioning" rule on every build/deploy.
VERSION ?= 2.5.1
LDFLAGS := -X main.Version=$(VERSION)

.PHONY: build build-all build-client build-gateway build-exit build-admin clean test deploy-r3s deploy-sto deploy-ams deploy-mtl backup-mtl restore-mtl backup-twb restore-twb gen-ios build-ios build-ios-release build-ios-sim archive-ios export-ios-ipa upload-ios-appstore validate-ios-appstore clean-ios gen-macos build-macos build-macos-release archive-macos clean-macos build-android build-android-release bundle-android clean-android build-windows-release release-ios release-macos release-android release-go release-windows dev-site build-site preview-site clean-site deploy-site build-inline-setup build-inline deploy-inline-r3s

# Build all binaries for all platforms
build-all: build-client build-gateway build-exit build-admin

# macOS client (darwin/arm64)
build-client:
	go build -ldflags "$(LDFLAGS)" -o dist/cli/darwin-arm64/vtx-client ./clients/cli/

# Gateway for R3S (linux/arm64)
build-gateway:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -ldflags "$(LDFLAGS)" -o dist/gateway/linux-arm64/vtx-gateway ./clients/gateway/

# Exit node for servers (linux/amd64)
build-exit:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags "$(LDFLAGS)" -o dist/exit/linux-amd64/vtx-exit ./server/cmd/exit/

# Admin tool for brokers (linux/amd64)
build-admin:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags "$(LDFLAGS)" -o dist/admin/linux-amd64/vtx-admin ./server/cmd/admin/

# Build server binaries only (linux/amd64 + linux/arm64)
build: build-gateway build-exit build-admin

# Deploy to R3S (gateway + vtx-proxy.sh)
deploy-r3s: build-gateway
	scp dist/gateway/linux-arm64/vtx-gateway r3s:/tmp/vtx-gateway-new
	scp clients/gateway/r3s/vtx-proxy.sh r3s:/tmp/vtx-proxy-new
	ssh r3s "systemctl stop vtx-gateway && \
		cp /tmp/vtx-gateway-new /usr/local/bin/vtx-gateway && \
		cp /tmp/vtx-proxy-new /usr/local/bin/vtx-proxy.sh && \
		chmod +x /usr/local/bin/vtx-gateway /usr/local/bin/vtx-proxy.sh && \
		systemctl start vtx-gateway"

# ── twb broker (Timeweb Moscow) is BACKED UP, NOT DEPLOYED. ──
# Same restore-via-make pattern as the mtl exit below. The backup at
# ~/Backups/vertex/twb-broker/latest/ carries the mosquitto config, the
# Let's Encrypt cert, the synced auth state and the systemd override.

backup-twb:
	scripts/backup-broker.sh twb $(if $(HOST),$(HOST),twb)

# Spin the backed-up twb broker back up on a fresh host. HOST=<ssh-alias>
# is required. By default the script tries to re-issue the TLS cert via
# Let's Encrypt — pass REUSE_CERT=1 to skip that and install the backup
# cert as-is (faster, but the cert expires on the backup's schedule).
restore-twb:
	@test -n "$(HOST)" || { echo "Usage: make restore-twb HOST=<ssh-alias> [REUSE_CERT=1]"; exit 1; }
	scripts/restore-broker.sh $(if $(REUSE_CERT),--reuse-cert) twb $(HOST)

# ── mtl exit (Montreal, ca-central-1) is BACKED UP, NOT DEPLOYED. ──
# To bring it back to live, run `make restore-mtl HOST=<ssh-alias>` against a
# fresh host. The backup at ~/Backups/vertex/mtl-exit/latest/ carries the
# MQTT password, DH private key, identity database and systemd unit; the
# Makefile target wires up the binary + systemd, and the RESTORE.md inside
# the backup carries the broker-user and DNS checklist.

# Snapshot a live exit (e.g. after config tweaks) into ~/Backups/vertex/<id>-exit/<date>/
backup-mtl:
	scripts/backup-exit.sh mtl $(if $(HOST),$(HOST),aws)

# Spin up the backed-up mtl exit on a fresh host. HOST=<ssh-alias> required.
restore-mtl: build-exit
	@test -n "$(HOST)" || { echo "Usage: make restore-mtl HOST=<ssh-alias>"; exit 1; }
	scripts/restore-exit.sh mtl $(HOST)

# Deploy a fresh binary build to an already-running mtl host (post-restore updates).
deploy-mtl: build-exit
	@test -n "$(HOST)" || { echo "Usage: make deploy-mtl HOST=<ssh-alias>"; exit 1; }
	scp dist/exit/linux-amd64/vtx-exit $(HOST):/tmp/vtx-exit-new
	ssh $(HOST) "sudo systemctl stop vtx-exit && \
		sudo cp /tmp/vtx-exit-new /usr/local/bin/vtx-exit && \
		sudo chmod +x /usr/local/bin/vtx-exit && \
		sudo systemctl start vtx-exit"

# Deploy to STO exit
deploy-sto: build-exit
	scp dist/exit/linux-amd64/vtx-exit sto:/tmp/vtx-exit-new
	ssh sto "sudo systemctl stop vtx-exit && \
		sudo cp /tmp/vtx-exit-new /usr/local/bin/vtx-exit && \
		sudo chmod +x /usr/local/bin/vtx-exit && \
		sudo systemctl start vtx-exit"

# Deploy to AMS exit
deploy-ams: build-exit
	scp dist/exit/linux-amd64/vtx-exit ams:/tmp/vtx-exit-new
	ssh ams "systemctl stop vtx-exit && \
		cp /tmp/vtx-exit-new /usr/local/bin/vtx-exit && \
		chmod +x /usr/local/bin/vtx-exit && \
		systemctl start vtx-exit"

# ── R3S-inline (in-path router variant, independent of clients/gateway/) ──
# Uses existing vtx-gateway binary as-is. Only one new Go binary: vtx-inline-setup.
build-inline-setup:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -ldflags "$(LDFLAGS)" -o dist/r3s-inline/linux-arm64/vtx-inline-setup ./clients/r3s-inline/cmd/setup/

# Assembles the payload that install.sh expects to find in cwd. install.sh on
# the target R3S unpacks this layout and runs from inside it.
build-inline: build-gateway build-inline-setup
	rm -rf dist/r3s-inline/payload
	mkdir -p dist/r3s-inline/payload
	cp dist/gateway/linux-arm64/vtx-gateway              dist/r3s-inline/payload/
	cp dist/r3s-inline/linux-arm64/vtx-inline-setup      dist/r3s-inline/payload/
	cp -r clients/r3s-inline/scripts   dist/r3s-inline/payload/
	cp -r clients/r3s-inline/configs   dist/r3s-inline/payload/
	cp -r clients/r3s-inline/systemd   dist/r3s-inline/payload/
	@echo "Payload assembled at dist/r3s-inline/payload/"

# Manual deploy to a specific inline R3S box (e.g. INLINE_HOST=user1.r3s in
# ~/.ssh/config). Independent of `deploy-r3s` which targets the production
# Mikrotik-R3S — never accidentally overlap them.
# Requires the remote account to have passwordless sudo (NOPASSWD) so install.sh
# can run unattended. Preflight `sudo -n true` exits 1 if password would be asked.
deploy-inline-r3s: build-inline
	@test -n "$(INLINE_HOST)" || { echo "ERROR: set INLINE_HOST=<ssh-alias>"; exit 1; }
	@ssh $(INLINE_HOST) "sudo -n true" 2>/dev/null || { echo "ERROR: $(INLINE_HOST) lacks passwordless sudo (NOPASSWD:ALL needed for install.sh)"; exit 1; }
	ssh $(INLINE_HOST) "rm -rf /tmp/vtx-inline && mkdir -p /tmp/vtx-inline"
	scp -r dist/r3s-inline/payload/* $(INLINE_HOST):/tmp/vtx-inline/
	ssh $(INLINE_HOST) "cd /tmp/vtx-inline && sudo bash scripts/install.sh"

# Run Docker integration tests
test:
	./test-docker.sh

# Clean build artifacts
clean:
	rm -rf dist/

# ── iOS client (Xcode, physical iPhone only — Simulator does not run NEPacketTunnelProvider) ──
gen-ios:
	cd clients/ios/Vertex && xcodegen generate

build-ios: gen-ios
	cd clients/ios/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Debug -destination 'generic/platform=iOS' \
	  -derivedDataPath build/DerivedData build
	@mkdir -p dist/ios && rm -rf dist/ios/Vertex.app && \
	  cp -R clients/ios/Vertex/build/DerivedData/Build/Products/Debug-iphoneos/Vertex.app dist/ios/

# Release build for iPhone install. The .app is copied to dist/ios/Vertex.app,
# ready for `xcrun devicectl device install app dist/ios/Vertex.app`.
# This is the default for shipping; Debug is only for attaching Xcode debugger.
build-ios-release: gen-ios
	cd clients/ios/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Release -destination 'generic/platform=iOS' \
	  -derivedDataPath build/DerivedData build
	@mkdir -p dist/ios && rm -rf dist/ios/Vertex.app && \
	  cp -R clients/ios/Vertex/build/DerivedData/Build/Products/Release-iphoneos/Vertex.app dist/ios/

# Compile-only check using the Simulator SDK — useful to validate Swift 6 concurrency.
# Not copied to dist/ — Simulator builds are not deployable (NEPacketTunnelProvider
# only runs on a physical device).
build-ios-sim: gen-ios
	cd clients/ios/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Debug -destination 'generic/platform=iOS Simulator' \
	  -derivedDataPath build/DerivedData build

archive-ios: gen-ios
	cd clients/ios/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Release -destination 'generic/platform=iOS' \
	  -archivePath build/Vertex.xcarchive archive
	@mkdir -p dist/ios && rm -rf dist/ios/Vertex.xcarchive && \
	  cp -R clients/ios/Vertex/build/Vertex.xcarchive dist/ios/

# ── iOS App Store distribution ────────────────────────────────────────
# Требуется: ASC API key (.p8) в ~/.private_keys/AuthKey_<ID>.p8
# ENV: ASC_KEY_ID (10-char Key ID), ASC_ISSUER_ID (UUID Issuer ID)

export-ios-ipa: archive-ios
	@if [ -z "$$ASC_KEY_ID" ] || [ -z "$$ASC_ISSUER_ID" ]; then \
	  echo "ERROR: ASC_KEY_ID and ASC_ISSUER_ID env vars must be set"; exit 1; fi
	@if [ ! -f "$$HOME/.private_keys/AuthKey_$$ASC_KEY_ID.p8" ]; then \
	  echo "ERROR: ~/.private_keys/AuthKey_$$ASC_KEY_ID.p8 not found"; exit 1; fi
	rm -rf dist/ios/AppStore
	xcodebuild -exportArchive \
	  -archivePath dist/ios/Vertex.xcarchive \
	  -exportOptionsPlist clients/ios/Vertex/ExportOptions.plist \
	  -exportPath dist/ios/AppStore \
	  -allowProvisioningUpdates \
	  -authenticationKeyPath "$$HOME/.private_keys/AuthKey_$$ASC_KEY_ID.p8" \
	  -authenticationKeyID "$$ASC_KEY_ID" \
	  -authenticationKeyIssuerID "$$ASC_ISSUER_ID"
	@ls -la dist/ios/AppStore/

validate-ios-appstore: export-ios-ipa
	@if [ -z "$$ASC_KEY_ID" ] || [ -z "$$ASC_ISSUER_ID" ]; then \
	  echo "ERROR: ASC_KEY_ID and ASC_ISSUER_ID env vars must be set"; exit 1; fi
	xcrun altool --validate-app \
	  -f dist/ios/AppStore/Vertex.ipa -t ios \
	  --apiKey "$$ASC_KEY_ID" --apiIssuer "$$ASC_ISSUER_ID"

upload-ios-appstore: export-ios-ipa
	@if [ -z "$$ASC_KEY_ID" ] || [ -z "$$ASC_ISSUER_ID" ]; then \
	  echo "ERROR: ASC_KEY_ID and ASC_ISSUER_ID env vars must be set"; exit 1; fi
	xcrun altool --upload-app \
	  -f dist/ios/AppStore/Vertex.ipa -t ios \
	  --apiKey "$$ASC_KEY_ID" --apiIssuer "$$ASC_ISSUER_ID"

clean-ios:
	rm -rf clients/ios/Vertex/build clients/ios/Vertex/Vertex.xcodeproj

# ── macOS client (Xcode) ──────────────────────────────────────────────
gen-macos:
	cd clients/macos/Vertex && xcodegen generate

build-macos: gen-macos
	cd clients/macos/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Debug -destination 'generic/platform=macOS' \
	  -derivedDataPath build/DerivedData -allowProvisioningUpdates build
	@mkdir -p dist/macos && rm -rf dist/macos/Vertex.app && \
	  cp -R clients/macos/Vertex/build/DerivedData/Build/Products/Debug/Vertex.app dist/macos/

build-macos-release: gen-macos
	cd clients/macos/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Release -destination 'generic/platform=macOS' \
	  -derivedDataPath build/DerivedData -allowProvisioningUpdates build
	@mkdir -p dist/macos && rm -rf dist/macos/Vertex.app && \
	  cp -R clients/macos/Vertex/build/DerivedData/Build/Products/Release/Vertex.app dist/macos/

archive-macos: gen-macos
	cd clients/macos/Vertex && \
	xcodebuild -project Vertex.xcodeproj -scheme Vertex \
	  -configuration Release -destination 'generic/platform=macOS' \
	  -archivePath build/Vertex.xcarchive archive
	@mkdir -p dist/macos && rm -rf dist/macos/Vertex.xcarchive && \
	  cp -R clients/macos/Vertex/build/Vertex.xcarchive dist/macos/

clean-macos:
	rm -rf clients/macos/Vertex/build clients/macos/Vertex/Vertex.xcodeproj

# ── Android client (Gradle) ───────────────────────────────────────────
# Native Kotlin client (Compose + VpnService). Multi-module project lives in
# clients/android/Vertex/. See clients/android/PLAN.md for design and roadmap.
# Distribution: Gitea release + sideload APK only (no Play Store).

# Resolve JDK 17 + Android SDK locations the same way Android Studio does:
# pick whatever the user already exports, otherwise fall back to the Homebrew /
# default-install paths so `make build-android` works straight after running
# `brew install openjdk@17` and unpacking command-line tools.
ANDROID_JAVA_HOME ?= $(shell test -d /opt/homebrew/opt/openjdk@17 && echo /opt/homebrew/opt/openjdk@17 || /usr/libexec/java_home -v 17 2>/dev/null)
ANDROID_SDK_HOME  ?= $(if $(ANDROID_HOME),$(ANDROID_HOME),$(HOME)/Library/Android/sdk)

# Debug APK — for development; signed with the auto debug keystore.
build-android:
	@test -n "$(ANDROID_JAVA_HOME)" || (echo "JDK 17 not found. Install via: brew install openjdk@17" && exit 1)
	@test -d "$(ANDROID_SDK_HOME)" || (echo "Android SDK not found at $(ANDROID_SDK_HOME). See clients/android/Vertex/README.md" && exit 1)
	cd clients/android/Vertex && \
	  JAVA_HOME=$(ANDROID_JAVA_HOME) ANDROID_HOME=$(ANDROID_SDK_HOME) \
	  ./gradlew :app:assembleDebug
	@mkdir -p dist/android && \
	  cp clients/android/Vertex/app/build/outputs/apk/debug/app-debug.apk \
	     dist/android/Vertex-android-debug.apk

# Release APK — signed with the keystore configured via env vars
# (see CREDENTIALS.md "Android Keystore"). Requires:
#   VERTEX_ANDROID_KEYSTORE_PATH, VERTEX_ANDROID_KEYSTORE_PASSWORD,
#   VERTEX_ANDROID_KEY_ALIAS, VERTEX_ANDROID_KEY_PASSWORD
build-android-release:
	$(call require-version)
	cd clients/android/Vertex && \
	  JAVA_HOME=$(ANDROID_JAVA_HOME) ANDROID_HOME=$(ANDROID_SDK_HOME) \
	  ./gradlew :app:assembleRelease \
	    -PversionName=$(VERSION) \
	    -PversionCode=$(shell echo $(VERSION) | awk -F. '{print $$1*10000+$$2*100+$$3}')
	@mkdir -p dist/android && \
	  cp clients/android/Vertex/app/build/outputs/apk/release/app-release.apk \
	     dist/android/Vertex-android-v$(VERSION).apk

# Android App Bundle (.aab) — kept for completeness; not used in Gitea-only
# distribution, but produced on demand if a Play Store track is ever opened.
bundle-android:
	$(call require-version)
	cd clients/android/Vertex && \
	  JAVA_HOME=$(ANDROID_JAVA_HOME) ANDROID_HOME=$(ANDROID_SDK_HOME) \
	  ./gradlew :app:bundleRelease \
	    -PversionName=$(VERSION) \
	    -PversionCode=$(shell echo $(VERSION) | awk -F. '{print $$1*10000+$$2*100+$$3}')
	@mkdir -p dist/android && \
	  cp clients/android/Vertex/app/build/outputs/bundle/release/app-release.aab \
	     dist/android/Vertex-android-v$(VERSION).aab

# Run :core unit tests (crypto vectors + MQTT codec + JSON wire-format pin).
test-android:
	cd clients/android/Vertex && \
	  JAVA_HOME=$(ANDROID_JAVA_HOME) ANDROID_HOME=$(ANDROID_SDK_HOME) \
	  ./gradlew :core:test

clean-android:
	cd clients/android/Vertex && \
	  JAVA_HOME=$(ANDROID_JAVA_HOME) ANDROID_HOME=$(ANDROID_SDK_HOME) \
	  ./gradlew clean

# ── Marketing site (Astro) ────────────────────────────────────────────
# vertices.ru — статический сайт в site/, деплой на Cloudflare Pages.

dev-site:
	cd site && npm install --silent && npm run dev

build-site:
	cd site && npm install --silent && npm run build

preview-site: build-site
	cd site && npm run preview

clean-site:
	rm -rf site/dist site/.astro

# Деплой на home-сервер: rsync dist/ → ~/nginx/sites/vertices.ru/, reload nginx-proxy.
# Требует SSH alias `home` (см. ~/.ssh/config) и server-блок vertices.ru
# в ~/nginx/nginx.conf на стороне сервера (см. ~/Projects/nginx/).
deploy-site: build-site
	rsync -avz --delete --human-readable site/dist/ home:nginx/sites/vertices.ru/
	ssh home 'docker exec nginx-proxy nginx -t && docker exec nginx-proxy nginx -s reload'
	@echo
	@echo "Deployed → https://vertices.ru/"

# ── Releases (Gitea) ──────────────────────────────────────────────────
# Per-component SemVer: ios-vX.Y.Z, macos-vX.Y.Z, go-vX.Y.Z
# Token: $HOME/.config/vertex/gitea-token (chmod 600)
# Usage: make release-ios VERSION=2.9.8

# Verify VERSION is set; called as `$(call require-version)`.
require-version = @if [ -z "$(VERSION)" ]; then echo "Usage: make $@ VERSION=X.Y.Z"; exit 2; fi

release-ios: gen-ios
	$(call require-version)
	$(MAKE) build-ios-release
	@cd dist/ios && rm -f Vertex-ios-v$(VERSION).app.zip && \
	  zip -qr Vertex-ios-v$(VERSION).app.zip Vertex.app
	./scripts/gitea-release.sh "ios-v$(VERSION)" "iOS v$(VERSION)" \
	    dist/ios/Vertex-ios-v$(VERSION).app.zip

release-macos: gen-macos
	$(call require-version)
	$(MAKE) build-macos-release
	@cd dist/macos && rm -f Vertex-macos-v$(VERSION).app.zip && \
	  zip -qr Vertex-macos-v$(VERSION).app.zip Vertex.app
	./scripts/gitea-release.sh "macos-v$(VERSION)" "macOS v$(VERSION)" \
	    dist/macos/Vertex-macos-v$(VERSION).app.zip

release-android:
	$(call require-version)
	$(MAKE) build-android-release VERSION=$(VERSION)
	./scripts/gitea-release.sh "android-v$(VERSION)" "Android v$(VERSION)" \
	    dist/android/Vertex-android-v$(VERSION).apk

release-go:
	$(call require-version)
	$(MAKE) build-all VERSION=$(VERSION)
	./scripts/gitea-release.sh "go-v$(VERSION)" "Go binaries v$(VERSION)" \
	    dist/cli/darwin-arm64/vtx-client \
	    dist/gateway/linux-arm64/vtx-gateway \
	    dist/exit/linux-amd64/vtx-exit \
	    dist/admin/linux-amd64/vtx-admin

# Windows MSI is built on the Parallels VM `vertex-win` (the WiX/WindowsAppSDK
# toolchain only runs on Windows). The repo is shared into the VM read-write
# via Parallels Shared Folders, but `mt.exe` and similar manifest tools choke
# on UNC paths — so the build is staged through a local copy at C:\vertex-build.
#
# Prereq: `Directory.Build.props` VersionPrefix matches $(VERSION) (the
# wixproj reads it for the MSI ProductVersion). Bump it before invoking.
build-windows-release:
	$(call require-version)
	@if ! ssh -o ConnectTimeout=5 -o BatchMode=yes vertex-win 'echo ok' >/dev/null 2>&1; then \
		echo "vertex-win VM unreachable — start the Parallels VM first."; exit 1; \
	fi
	ssh vertex-win 'robocopy \\Mac\vertex\clients\windows C:\vertex-build /MIR /XD obj bin publish .vs node_modules /XF *.user /NFL /NDL /NJH /NJS' || true
	ssh vertex-win 'cd C:\vertex-build; .\tools\make.ps1 publish-amd64'
	ssh vertex-win 'cd C:\vertex-build; .\tools\make.ps1 msi-amd64' || true
	@mkdir -p dist/windows
	ssh vertex-win 'Copy-Item C:\vertex-build\packaging\bin\x64\Release\en-US\Vertex-windows-v$(VERSION)-x64.msi -Destination \\Mac\vertex\dist\windows\ -Force'
	@test -f dist/windows/Vertex-windows-v$(VERSION)-x64.msi || \
		(echo "MSI not found at dist/windows/Vertex-windows-v$(VERSION)-x64.msi — check VM build output."; exit 1)
	@ls -la dist/windows/Vertex-windows-v$(VERSION)-x64.msi

# Reuses an existing MSI in dist/windows/ if present (Windows is built
# out-of-band on the Parallels VM; recompiling for every release call is
# wasteful and requires the VM to be running). Falls through to
# build-windows-release otherwise.
release-windows:
	$(call require-version)
	@if [ ! -f dist/windows/Vertex-windows-v$(VERSION)-x64.msi ]; then \
		$(MAKE) build-windows-release VERSION=$(VERSION); \
	fi
	./scripts/gitea-release.sh "windows-v$(VERSION)" "Windows v$(VERSION)" \
	    dist/windows/Vertex-windows-v$(VERSION)-x64.msi
