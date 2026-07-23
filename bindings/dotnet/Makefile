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
MANAGED_PACKAGE_VERSION ?=
MANAGED_PACKAGE_OUTPUT ?= ./artifacts/managed-packages
MANAGED_PACKAGE_CONSUMER_OUTPUT ?= ./artifacts/managed-package-consumer
MANAGED_PACKAGE_OUTPUT_ARG = $(if $(MANAGED_PACKAGE_OUTPUT),--output "$(MANAGED_PACKAGE_OUTPUT)")
MANAGED_PACKAGE_VERSION_ARG = $(if $(MANAGED_PACKAGE_VERSION),-p:PackageVersion=$(MANAGED_PACKAGE_VERSION))
MANAGED_PACKAGE_OUTPUT_ABSOLUTE = $(abspath $(MANAGED_PACKAGE_OUTPUT))
MANAGED_NATIVEAOT_OUTPUT ?= ./artifacts/managed-nativeaot-smoke
MANAGED_NATIVEAOT_PACKAGES ?= $(MANAGED_PACKAGE_OUTPUT)/.nuget-packages
MANAGED_NATIVEAOT_NUGET_CONFIG = ./samples/ManagedNativeAot/obj/managed-nativeaot.nuget.config

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
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target x86_64-pc-windows-msvc $(RUST_RELEASE_OPT)

build-rust-windowsarm64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target aarch64-pc-windows-msvc $(RUST_RELEASE_OPT)

build-rust-linux64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target x86_64-unknown-linux-gnu $(RUST_RELEASE_OPT)

build-rust-linuxarm64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target aarch64-unknown-linux-gnu $(RUST_RELEASE_OPT)

build-rust-macos64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target x86_64-apple-darwin $(RUST_RELEASE_OPT)

build-rust-macosarm64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-darwin $(RUST_RELEASE_OPT)

build-rust-androidarm64:
	cargo ndk --target aarch64-linux-android --platform 31 build -p turso_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidarm:
	cargo ndk --target armv7-linux-androideabi --platform 31 build -p turso_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidx64:
	cargo ndk --target x86_64-linux-android --platform 31 build -p turso_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-androidx86:
	cargo ndk --target i686-linux-android --platform 31 build -p turso_sdk_kit --target-dir ./rs_compiled $(RUST_RELEASE_OPT)

build-rust-android: build-rust-androidarm64 build-rust-androidarm build-rust-androidx64 build-rust-androidx86

build-rust-iosarm64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-ios $(RUST_RELEASE_OPT)

build-rust-iossimulatorarm64:
	cargo build -p turso_sdk_kit --target-dir ./rs_compiled --target aarch64-apple-ios-sim $(RUST_RELEASE_OPT)

build-rust-ios: build-rust-iosarm64 build-rust-iossimulatorarm64

package-rust-iosframework:
	rm -rf "$(IOS_FRAMEWORK_BUILD_DIR)" "$(IOS_XCFRAMEWORK_DIR)"
	mkdir -p "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework" "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework" ./rs_compiled/ios-universal/native
	cp ./rs_compiled/aarch64-apple-ios/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	install_name_tool -id @rpath/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME) "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
	cp ./rs_compiled/aarch64-apple-ios-sim/$(RUST_PROFILE_DIR)/$(IOS_DYLIB_FILE) "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework/$(IOS_FRAMEWORK_NAME)"
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
	xcodebuild -create-xcframework -framework "$(IOS_FRAMEWORK_BUILD_DIR)/device/$(IOS_FRAMEWORK_NAME).framework" -framework "$(IOS_FRAMEWORK_BUILD_DIR)/simulator/$(IOS_FRAMEWORK_NAME).framework" -output "$(IOS_XCFRAMEWORK_DIR)"

build-rust: 
	echo "Building for $(OS_TARGET)"
	$(MAKE) build-rust-$(OS_TARGET)

build-dotnet:
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Data/Turso.Data.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet build $(DOTNET_RELEASE_OPT) ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj

# The default restore/build/test/pack path is managed-only: it neither invokes
# Cargo nor requires rs_compiled native runtime assets. Native distribution
# targets remain explicit below.
.PHONY: restore build test pack restore-managed build-managed test-managed pack-managed validate-managed-project-closure validate-managed-package validate-managed-nativeaot build-native test-native pack-native pack-nativeaot-static pack-release
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

pack-managed: restore-managed
	rm -rf "$(MANAGED_PACKAGE_OUTPUT)"
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.EntityFrameworkCore.Sqlite/Turso.EntityFrameworkCore.Sqlite.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Platform.Client/Turso.Platform.Client.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Turso.Serverless.Client/Turso.Serverless.Client.csproj

validate-managed-project-closure:
	@if grep -R -E -i --include='*.csproj' --include='*.props' --include='*.targets' '(Turso\.(Raw|Data\.Native|Data\.Sqlite\.Native[^"]*)|cargo|rustc|cargo-ndk|turso_sdk_kit)' ./src/Turso.Core ./src/Turso.Data ./src/Turso.Data.Sqlite ./samples/ManagedPackageConsumer ./samples/ManagedNativeAot; then \
		echo "Managed package and NativeAOT build configurations must not reference native Turso packages, assets, or Rust tooling"; \
		exit 1; \
	fi

validate-managed-package: validate-managed-project-closure
	$(MAKE) pack-managed MANAGED_PACKAGE_VERSION=0.0.0-managed-local
	rm -rf ./samples/ManagedPackageConsumer/bin ./samples/ManagedPackageConsumer/obj "$(MANAGED_PACKAGE_CONSUMER_OUTPUT)"
	dotnet restore ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj --source "$(MANAGED_PACKAGE_OUTPUT)" --packages "$(MANAGED_PACKAGE_OUTPUT)/.nuget-packages" -p:TursoPackageVersion=0.0.0-managed-local
	dotnet build --no-restore ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local
	dotnet run --no-build --no-restore --project ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local
	dotnet publish --configuration Debug --no-build --no-restore --output "$(MANAGED_PACKAGE_CONSUMER_OUTPUT)" ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:TursoPackageVersion=0.0.0-managed-local
	@if find "$(MANAGED_PACKAGE_CONSUMER_OUTPUT)" -type f \( -iname 'Turso.Raw.dll' -o -iname 'Turso.Data.Native.dll' -o -iname 'turso_sdk_kit.dll' -o -iname 'libturso_sdk_kit.so' -o -iname 'libturso_sdk_kit.dylib' -o -iname 'libturso_sdk_kit.a' \) -print -quit | grep -q .; then \
		echo "Managed package publish output must not contain native companion assets"; \
		exit 1; \
	fi

validate-managed-nativeaot: validate-managed-project-closure
	$(MAKE) pack-managed MANAGED_PACKAGE_VERSION=0.0.0-managed-local
	rm -rf ./samples/ManagedNativeAot/bin ./samples/ManagedNativeAot/obj "$(MANAGED_NATIVEAOT_OUTPUT)"
	mkdir -p ./samples/ManagedNativeAot/obj
	printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' '    <clear />' '    <add key="managed-package" value="$(MANAGED_PACKAGE_OUTPUT_ABSOLUTE)" />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' '  </packageSources>' '</configuration>' > "$(MANAGED_NATIVEAOT_NUGET_CONFIG)"
	dotnet restore --runtime "$(MANAGED_NATIVEAOT_RID)" ./samples/ManagedNativeAot/ManagedNativeAotSmoke.csproj --configfile "$(MANAGED_NATIVEAOT_NUGET_CONFIG)" --packages "$(MANAGED_NATIVEAOT_PACKAGES)" -p:TursoPackageVersion=0.0.0-managed-local
	dotnet publish -c Release -r "$(MANAGED_NATIVEAOT_RID)" --self-contained true --no-restore -o "$(MANAGED_NATIVEAOT_OUTPUT)" ./samples/ManagedNativeAot/ManagedNativeAotSmoke.csproj -p:TursoPackageVersion=0.0.0-managed-local
	"$(MANAGED_NATIVEAOT_EXECUTABLE)"
	@if ! grep -E -i '"Turso\.Data\.Sqlite/' ./samples/ManagedNativeAot/obj/project.assets.json; then \
		echo "Managed NativeAOT validation must restore the primary managed Turso package"; \
		exit 1; \
	fi
	@if grep -E -i '"Turso\.(Raw|Data\.Native|Data\.Sqlite\.Native[^"]*)/' ./samples/ManagedNativeAot/obj/project.assets.json; then \
		echo "Managed NativeAOT restore closure must not include a native Turso package"; \
		exit 1; \
	fi
	@if find "$(MANAGED_NATIVEAOT_OUTPUT)" -type f ! -name 'ManagedNativeAotSmoke' ! -name 'ManagedNativeAotSmoke.exe' ! -name '*.pdb' ! -name '*.dbg' ! -name '*.xml' -print -quit | grep -q .; then \
		echo "Managed NativeAOT publish output must contain only the executable, symbols, and documentation"; \
		exit 1; \
	fi

pack-native:
	dotnet restore ./src/Turso.Data.Native/Turso.Data.Native.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Raw/Turso.Raw.csproj
	dotnet pack --no-restore -c Release ./src/Turso.Data.Native/Turso.Data.Native.csproj

# Generate the Turso Platform API client from the pinned OpenAPI spec.
generate-platform-client:
	dotnet build ./src/Turso.Platform.Client/Turso.Platform.Client.csproj

build: build-managed

build-production:
	$(MAKE) build-native RUST_RELEASE_OPT='--release' DOTNET_RELEASE_OPT='-c Release'

build-native: build-rust build-dotnet

test: test-managed

test-native: build-native
	dotnet test ./src/Turso.Tests/Turso.Tests.csproj

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
