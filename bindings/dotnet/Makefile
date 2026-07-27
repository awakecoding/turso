ifeq ($(OS),Windows_NT)
	OS_TARGET = windows64
else 
    UNAME_S := $(shell uname -s)
	ifeq ($(UNAME_S),Linux)
		OS_TARGET = linux64
	endif
	ifeq ($(UNAME_S),Darwin)
		UNAME_P := $(shell uname -p)
		ifeq ($(UNAME_P),x86_64)
			OS_TARGET = macos64
		else
			OS_TARGET = macosarm64
		endif
	endif
endif

RUST_RELEASE_OPT ?= 
RUST_PROFILE_DIR ?= debug
DOTNET_RELEASE_OPT ?=
IOS_FRAMEWORK_NAME = libturso_sdk_kit
IOS_DYLIB_FILE = libturso_sdk_kit.dylib
IOS_FRAMEWORK_BUILD_DIR = ./rs_compiled/ios-frameworks
IOS_XCFRAMEWORK_DIR = ./rs_compiled/ios-universal/native/$(IOS_FRAMEWORK_NAME).xcframework
IOS_SYNC_FRAMEWORK_NAME = libturso_sync_sdk_kit
IOS_SYNC_DYLIB_FILE = libturso_sync_sdk_kit.dylib
IOS_SYNC_FRAMEWORK_BUILD_DIR = ./rs_compiled/ios-sync-frameworks
IOS_SYNC_XCFRAMEWORK_DIR = ./rs_compiled/ios-sync-universal/native/$(IOS_SYNC_FRAMEWORK_NAME).xcframework
MANAGED_PACKAGE_VERSION ?=
MANAGED_PACKAGE_OUTPUT ?= ./artifacts/managed-packages
MANAGED_PACKAGE_CONSUMER_OUTPUT ?= ./artifacts/managed-package-consumer
MANAGED_PACKAGE_OUTPUT_ARG = $(if $(MANAGED_PACKAGE_OUTPUT),--output "$(MANAGED_PACKAGE_OUTPUT)")
MANAGED_PACKAGE_VERSION_ARG = $(if $(MANAGED_PACKAGE_VERSION),-p:PackageVersion=$(MANAGED_PACKAGE_VERSION))
MANAGED_PACKAGE_OUTPUT_ABSOLUTE = $(abspath $(MANAGED_PACKAGE_OUTPUT))
MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG = ./samples/ManagedPackageConsumer/obj/managed-package-consumer.nuget.config
MANAGED_PACKAGE_CONSUMER_FRAMEWORKS = net8.0 net9.0 net10.0
MANAGED_NATIVEAOT_OUTPUT ?= ./artifacts/managed-nativeaot-smoke
MANAGED_NATIVEAOT_PACKAGES ?= $(MANAGED_PACKAGE_OUTPUT)/.nuget-packages
MANAGED_NATIVEAOT_NUGET_CONFIG = ./samples/ManagedNativeAot/obj/managed-nativeaot.nuget.config
MANAGED_CLOSURE_VALIDATOR = pwsh -NoLogo -NoProfile -File ./scripts/Validate-ManagedPackageClosure.ps1

ifeq ($(OS),Windows_NT)
	MANAGED_NATIVEAOT_RID ?= win-x64
else
	MANAGED_NATIVEAOT_UNAME_M := $(shell uname -m)
	ifeq ($(UNAME_S),Linux)
		ifeq ($(filter aarch64 arm64,$(MANAGED_NATIVEAOT_UNAME_M)),)
			MANAGED_NATIVEAOT_RID ?= linux-x64
		else
			MANAGED_NATIVEAOT_RID ?= linux-arm64
		endif
	endif
	ifeq ($(UNAME_S),Darwin)
		ifneq ($(filter x86_64 amd64,$(MANAGED_NATIVEAOT_UNAME_M)),)
			MANAGED_NATIVEAOT_RID ?= osx-x64
		else
			MANAGED_NATIVEAOT_RID ?= osx-arm64
		endif
	endif
endif

MANAGED_NATIVEAOT_EXECUTABLE ?= $(MANAGED_NATIVEAOT_OUTPUT)/ManagedNativeAotSmoke$(if $(filter win-%,$(MANAGED_NATIVEAOT_RID)),.exe)

all:
	echo "make [restore|build|test|pack|validate-managed-package|validate-managed-nativeaot|build-native|test-native|pack-native|pack-nativeaot-static|pack-release|benchmark]"

build-rust-windows64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target x86_64-pc-windows-msvc $(RUST_RELEASE_OPT)

build-rust-windowsarm64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target aarch64-pc-windows-msvc $(RUST_RELEASE_OPT)

build-rust-linux64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target x86_64-unknown-linux-gnu $(RUST_RELEASE_OPT)

build-rust-linuxarm64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target aarch64-unknown-linux-gnu $(RUST_RELEASE_OPT)

build-rust-macos64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target x86_64-apple-darwin $(RUST_RELEASE_OPT)

build-rust-macosarm64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-darwin $(RUST_RELEASE_OPT)

build-rust-androidarm64:
	cargo ndk --target aarch64-linux-android --platform 31 build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidarm:
	cargo ndk --target armv7-linux-androideabi --platform 31 build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidx64:
	cargo ndk --target x86_64-linux-android --platform 31 build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidx86:
	cargo ndk --target i686-linux-android --platform 31 build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-android: build-rust-androidarm64 build-rust-androidarm build-rust-androidx64 build-rust-androidx86

build-rust-iosarm64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-ios $(RUST_RELEASE_OPT)

build-rust-iossimulatorarm64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-ios-sim $(RUST_RELEASE_OPT)

build-rust-iossimulator64:
	cargo build -p turso_sdk_kit -p turso_sync_sdk_kit --target-dir ./rs_compiled --target x86_64-apple-ios $(RUST_RELEASE_OPT)

build-rust-ios: build-rust-iosarm64 build-rust-iossimulatorarm64 build-rust-iossimulator64

package-rust-iosframework:
	rm -rf "$(IOS_FRAMEWORK_BUILD_DIR)" "$(IOS_XCFRAMEWORK_DIR)"
	mkdir -p "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework" "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework" ./rs_compiled/ios-universal/native
	cp ./rs_compiled/aarch64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	install_name_tool -id @rpath/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME) "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	lipo -create ./rs_compiled/aarch64-apple-ios-sim/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) ./rs_compiled/x86_64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) -output "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	install_name_tool -id @rpath/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME) "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	printf '%s\n' \
	'<?xml version="1.0" encoding="UTF-8"?>' \
	'<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">' \
	'<plist version="1.0">' \
	'<dict>' \
	'  <key>CFBundleDevelopmentRegion</key>' \
	'  <string>en</string>' \
	'  <key>CFBundleExecutable</key>' \
	'  <string>$(IOS_FRAMEWORK_NAME)</string>' \
	'  <key>CFBundleIdentifier</key>' \
	'  <string>com.turso.turso-sdk-kit</string>' \
	'  <key>CFBundleInfoDictionaryVersion</key>' \
	'  <string>6.0</string>' \
	'  <key>CFBundlePackageType</key>' \
	'  <string>FMWK</string>' \
	'  <key>CFBundleSignature</key>' \
	'  <string>????</string>' \
	'  <key>CFBundleVersion</key>' \
	'  <string>1.0.0</string>' \
	'  <key>CFBundleShortVersionString</key>' \
	'  <string>1.0.0</string>' \
	'  <key>MinimumOSVersion</key>' \
	'  <string>12.2</string>' \
	'</dict>' \
	'</plist>' > "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/Info.plist"
	cp "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/Info.plist" "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework/Info.plist"
	codesign --force --sign - "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	codesign --force --sign - "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	xcodebuild -create-xcframework -framework "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework" -framework "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework" -output "$(IOS_XCFRAMEWORK_DIR)"

package-rust-ios-syncframework:
	rm -rf "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)" "$(IOS_SYNC_XCFRAMEWORK_DIR)"
	mkdir -p "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework" "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework" ./rs_compiled/ios-sync-universal/native
	cp ./rs_compiled/aarch64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_SYNC_DYLIB_FILE) "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	install_name_tool -id @rpath/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME) "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	lipo -create ./rs_compiled/aarch64-apple-ios-sim/$(RUST_PROFILE_DIR)/$(IOS_SYNC_DYLIB_FILE) ./rs_compiled/x86_64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_SYNC_DYLIB_FILE) -output "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	install_name_tool -id @rpath/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME) "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	printf '%s\n' \
		'<?xml version="1.0" encoding="UTF-8"?>' \
		'<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">' \
		'<plist version="1.0">' \
		'<dict>' \
		'  <key>CFBundleDevelopmentRegion</key>' \
		'  <string>en</string>' \
		'  <key>CFBundleExecutable</key>' \
		'  <string>$(IOS_SYNC_FRAMEWORK_NAME)</string>' \
		'  <key>CFBundleIdentifier</key>' \
		'  <string>com.turso.turso-sync-sdk-kit</string>' \
		'  <key>CFBundleInfoDictionaryVersion</key>' \
		'  <string>6.0</string>' \
		'  <key>CFBundlePackageType</key>' \
		'  <string>FMWK</string>' \
		'  <key>CFBundleSignature</key>' \
		'  <string>????</string>' \
		'  <key>CFBundleVersion</key>' \
		'  <string>1.0.0</string>' \
		'  <key>CFBundleShortVersionString</key>' \
		'  <string>1.0.0</string>' \
		'  <key>MinimumOSVersion</key>' \
		'  <string>12.2</string>' \
		'</dict>' \
		'</plist>' > "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework/Info.plist"
	cp "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework/Info.plist" "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework/Info.plist"
	codesign --force --sign - "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	codesign --force --sign - "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework/$(IOS_SYNC_FRAMEWORK_NAME)"
	xcodebuild -create-xcframework -framework "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/device/$(IOS_SYNC_FRAMEWORK_NAME).framework" -framework "$(IOS_SYNC_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_SYNC_FRAMEWORK_NAME).framework" -output "$(IOS_SYNC_XCFRAMEWORK_DIR)"

build-rust: 
	echo "Building for $(OS_TARGET)"
	$(MAKE) build-rust-$(OS_TARGET)

build-dotnet:
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Data/Turso.Data.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Data.Sync/Turso.Data.Sync.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj

# The default restore/build/test/pack path is managed-only: it neither invokes
# Cargo nor requires rs_compiled native runtime assets. Native distribution
# targets remain explicit below.
.PHONY: restore build test pack restore-managed build-managed test-managed pack-managed validate-managed-project-closure validate-managed-packed-closure validate-managed-package validate-managed-nativeaot build-native build-sync-native test-native test-sync pack-native pack-sync pack-nativeaot-static pack-release
restore: restore-managed

restore-managed:
	dotnet restore ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet restore ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet restore ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet restore ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj

build-managed: restore-managed
	dotnet build --no-restore -c Debug ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet build --no-restore -c Debug ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet build --no-restore -c Debug ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet build --no-restore -c Debug ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj

test-managed: validate-managed-package
	dotnet test ./src/Turso.Tests/Turso.Tests.csproj

pack-managed: restore-managed validate-managed-project-closure
	rm -rf "$(MANAGED_PACKAGE_OUTPUT)"
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj
	$(MAKE) validate-managed-packed-closure

validate-managed-project-closure:
	@if grep -E -i '(Turso\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary)' ./Directory.Build.props ./Directory.Build.targets ./Turso.slnx || grep -R -E -i --include='*.csproj' --include='*.props' --include='*.targets' '(Turso\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary)' ./src/Turso.Core ./src/Turso.Data ./src/Turso.Data.Sqlite ./src/Turso.EntityFrameworkCore.Sqlite ./src/Turso.Platform.Client ./src/Turso.Serverless.Client ./samples/ManagedPackageConsumer ./samples/ManagedNativeAot; then \
		echo "Managed package, solution, and NativeAOT build configurations must not reference native Turso packages, P/Invoke assets, or Rust tooling"; \
		exit 1; \
	fi

validate-managed-packed-closure:
	$(MANAGED_CLOSURE_VALIDATOR) -PackageDirectory "$(MANAGED_PACKAGE_OUTPUT)"

validate-managed-package: validate-managed-project-closure
	$(MAKE) pack-managed MANAGED_PACKAGE_VERSION=0.0.0-managed-local
	$(MAKE) validate-managed-packed-closure
	@set -e; for target_framework in $(MANAGED_PACKAGE_CONSUMER_FRAMEWORKS); do \
		consumer_output="$(MANAGED_PACKAGE_CONSUMER_OUTPUT)/$$target_framework"; \
		rm -rf ./samples/ManagedPackageConsumer/bin ./samples/ManagedPackageConsumer/obj "$$consumer_output"; \
		mkdir -p ./samples/ManagedPackageConsumer/obj; \
		printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' '    <clear />' '    <add key="managed-package" value="$(MANAGED_PACKAGE_OUTPUT_ABSOLUTE)" />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' '  </packageSources>' '</configuration>' > "$(MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG)"; \
		dotnet restore ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj --configfile "$(MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG)" --packages "$(MANAGED_PACKAGE_OUTPUT)/.nuget-packages" -p:TursoPackageVersion=0.0.0-managed-local -p:TursoConsumerTargetFramework=$$target_framework; \
		dotnet build --no-restore --framework $$target_framework ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local -p:TursoConsumerTargetFramework=$$target_framework; \
		dotnet run --no-build --no-restore --framework $$target_framework --project ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local -p:TursoConsumerTargetFramework=$$target_framework; \
		dotnet publish --configuration Debug --no-build --no-restore --framework $$target_framework --output "$$consumer_output" ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local -p:TursoConsumerTargetFramework=$$target_framework; \
		$(MANAGED_CLOSURE_VALIDATOR) -ProjectAssetsFile ./samples/ManagedPackageConsumer/obj/project.assets.json -PublishOutput "$$consumer_output"; \
	done
	@set -e; \
		restore_log="$(MANAGED_PACKAGE_OUTPUT)/unsupported-ef-restore.log"; \
		rm -rf ./samples/ManagedPackageConsumer/bin ./samples/ManagedPackageConsumer/obj "$$restore_log"; \
		mkdir -p ./samples/ManagedPackageConsumer/obj; \
		printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' '    <clear />' '    <add key="managed-package" value="$(MANAGED_PACKAGE_OUTPUT_ABSOLUTE)" />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' '  </packageSources>' '</configuration>' > "$(MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG)"; \
		if dotnet restore ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj --configfile "$(MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG)" --packages "$(MANAGED_PACKAGE_OUTPUT)/.nuget-packages" -p:TursoPackageVersion=0.0.0-managed-local -p:TursoConsumerTargetFramework=net10.0 -p:TursoConsumerEntityFrameworkCoreVersion=10.0.0 > "$$restore_log" 2>&1; then \
			echo "Managed EF package must reject an EF Core 10 override"; \
			cat "$$restore_log"; \
			exit 1; \
		elif ! grep -Fq 'NU1608' "$$restore_log"; then \
			echo "Unsupported EF Core restore did not fail with the expected package-range conflict"; \
			cat "$$restore_log"; \
			exit 1; \
		fi; \
		rm -f "$$restore_log"

validate-managed-nativeaot: validate-managed-project-closure
	$(MAKE) pack-managed MANAGED_PACKAGE_VERSION=0.0.0-managed-local
	$(MAKE) validate-managed-packed-closure
	rm -rf ./samples/ManagedNativeAot/bin ./samples/ManagedNativeAot/obj "$(MANAGED_NATIVEAOT_OUTPUT)"
	mkdir -p ./samples/ManagedNativeAot/obj
	printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' '    <clear />' '    <add key="managed-package" value="$(MANAGED_PACKAGE_OUTPUT_ABSOLUTE)" />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' '  </packageSources>' '</configuration>' > "$(MANAGED_NATIVEAOT_NUGET_CONFIG)"
	dotnet restore --runtime "$(MANAGED_NATIVEAOT_RID)" ./samples/ManagedNativeAot/ManagedNativeAotSmoke.csproj --configfile "$(MANAGED_NATIVEAOT_NUGET_CONFIG)" --packages "$(MANAGED_NATIVEAOT_PACKAGES)" -p:TursoPackageVersion=0.0.0-managed-local
	dotnet publish -c Release -r "$(MANAGED_NATIVEAOT_RID)" --self-contained true --no-restore -o "$(MANAGED_NATIVEAOT_OUTPUT)" ./samples/ManagedNativeAot/ManagedNativeAotSmoke.csproj -p:TursoPackageVersion=0.0.0-managed-local
	"$(MANAGED_NATIVEAOT_EXECUTABLE)"
	$(MANAGED_CLOSURE_VALIDATOR) -ProjectAssetsFile ./samples/ManagedNativeAot/obj/project.assets.json -PublishOutput "$(MANAGED_NATIVEAOT_OUTPUT)" -NativeAot

pack-native:
	dotnet restore ./src/Turso.Data.Native/Turso.Data.Native.csproj
	dotnet restore ./src/Turso.Data.Sync/Turso.Data.Sync.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Raw/Turso.Raw.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Data.Native/Turso.Data.Native.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Data.Sync/Turso.Data.Sync.csproj

pack-sync:
	dotnet restore ./src/Turso.Data.Sync/Turso.Data.Sync.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Data.Sync/Turso.Data.Sync.csproj

# Generate the Turso Platform API client from the pinned OpenAPI spec.
generate-platform-client:
	dotnet build ./src/Turso.Platform.Client/Turso.Platform.Client.csproj

build: build-managed

build-production:
	$(MAKE) build-native RUST_RELEASE_OPT='--release' DOTNET_RELEASE_OPT='-c Release'

build-native: build-rust build-dotnet

build-sync-native: build-native

test: test-managed

test-native: build-native
	dotnet test ./src/Turso.Tests/Turso.Tests.csproj

test-sync: build-sync-native
	dotnet test ./src/Turso.Tests/Turso.Tests.csproj --filter "FullyQualifiedName~TursoRemoteTests"

benchmark: build-production
	dotnet run -c Release --project ./src/Benchmarks/Benchmarks.csproj

pack-nativeaot-static:
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=win-x64
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=win-arm64
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=linux-x64
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=linux-arm64
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=osx-x64
	dotnet pack -c Release ./src/Turso.Data.Sqlite.NativeAot/Turso.Data.Sqlite.NativeAot.csproj -p:NativeAotRid=osx-arm64

pack:
	$(MAKE) pack-managed

pack-release:
	$(MAKE) pack-managed MANAGED_PACKAGE_OUTPUT= MANAGED_PACKAGE_VERSION=
	$(MAKE) pack-native
	$(MAKE) pack-nativeaot-static
