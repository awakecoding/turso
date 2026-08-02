# Ahtola — pure managed C# SQLite-compatible engine (Devolutions).
#
# This Makefile builds, tests, and packs the managed-only Ahtola packages.
# There is no native companion, no Rust tooling, and no P/Invoke coupling.

DOTNET_RELEASE_OPT ?=

MANAGED_PACKAGE_VERSION ?=
MANAGED_PACKAGE_OUTPUT ?= ./artifacts/managed-packages
MANAGED_PACKAGE_CONSUMER_OUTPUT ?= ./artifacts/managed-package-consumer
MANAGED_PACKAGE_OUTPUT_ARG = $(if $(MANAGED_PACKAGE_OUTPUT),--output "$(MANAGED_PACKAGE_OUTPUT)")
MANAGED_PACKAGE_VERSION_ARG = $(if $(MANAGED_PACKAGE_VERSION),-p:PackageVersion=$(MANAGED_PACKAGE_VERSION))
MANAGED_PACKAGE_OUTPUT_ABSOLUTE = $(abspath $(MANAGED_PACKAGE_OUTPUT))
MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG = ./samples/ManagedPackageConsumer/obj/managed-package-consumer.nuget.config
MANAGED_PACKAGE_CONSUMER_FRAMEWORKS = net8.0 net9.0 net10.0
MANAGED_CLOSURE_VALIDATOR = pwsh -NoLogo -NoProfile -File ./scripts/Validate-ManagedPackageClosure.ps1
MANAGED_TEST_RUNNER = pwsh -NoLogo -NoProfile -File ./scripts/Invoke-ManagedTestSuite.ps1

MANAGED_TEST_FRAMEWORK ?= net10.0
MANAGED_TEST_MINIMUM_EXECUTED ?= 2500

.PHONY: all restore build test pack format-check \
        restore-managed build-managed test-managed pack-managed \
        validate-managed-project-closure validate-managed-packed-closure validate-managed-package

all: build-managed

restore: restore-managed

restore-managed:
	dotnet restore ./src/Ahtola.Data.Sqlite/Ahtola.Data.Sqlite.csproj
	dotnet restore ./src/Ahtola.EntityFrameworkCore.Sqlite/Ahtola.EntityFrameworkCore.Sqlite.csproj

build-managed: restore-managed
	dotnet build --no-restore -c Debug ./src/Ahtola.Data.Sqlite/Ahtola.Data.Sqlite.csproj
	dotnet build --no-restore -c Debug ./src/Ahtola.EntityFrameworkCore.Sqlite/Ahtola.EntityFrameworkCore.Sqlite.csproj

test-managed: validate-managed-package
	$(MANAGED_TEST_RUNNER) -Framework $(MANAGED_TEST_FRAMEWORK) -MinimumExecutedTests $(MANAGED_TEST_MINIMUM_EXECUTED)

format-check:
	dotnet format ./Ahtola.slnx --verify-no-changes
	dotnet format ./src/Ahtola.Tests/Ahtola.Tests.csproj --verify-no-changes

pack-managed: restore-managed validate-managed-project-closure
	rm -rf "$(MANAGED_PACKAGE_OUTPUT)"
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Ahtola.Core/Ahtola.Core.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Ahtola.Data.Sqlite/Ahtola.Data.Sqlite.csproj
	dotnet pack --no-restore -c Release $(MANAGED_PACKAGE_OUTPUT_ARG) $(MANAGED_PACKAGE_VERSION_ARG) ./src/Ahtola.EntityFrameworkCore.Sqlite/Ahtola.EntityFrameworkCore.Sqlite.csproj
	$(MAKE) validate-managed-packed-closure

validate-managed-project-closure:
	@if grep -E -i '(Ahtola\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary)' ./Directory.Build.props ./Directory.Build.targets ./Ahtola.slnx || grep -R -E -i --include='*.csproj' --include='*.props' --include='*.targets' '(Ahtola\.(Raw|Data\.(Native|Sync)|Data\.Sqlite\.(Native[^"]*|Sync))|cargo|rustc|cargo-ndk|turso_sdk_kit|DirectPInvoke|NativeLibrary|DllImport|LibraryImport|TursoUseStaticNativeLibrary)' ./src/Ahtola.Core ./src/Ahtola.Data ./src/Ahtola.Data.Sqlite ./src/Ahtola.EntityFrameworkCore.Sqlite ./samples/ManagedPackageConsumer; then \
		echo "Managed package, solution, and build configurations must not reference native Ahtola packages, P/Invoke assets, or Rust tooling"; \
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
		dotnet restore ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj --configfile "$(MANAGED_PACKAGE_CONSUMER_NUGET_CONFIG)" --packages "$(MANAGED_PACKAGE_OUTPUT)/.nuget-packages" -p:AhtolaPackageVersion=0.0.0-managed-local -p:AhtolaConsumerTargetFramework=$$target_framework; \
		dotnet build --no-restore --framework $$target_framework ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:AhtolaPackageVersion=0.0.0-managed-local -p:AhtolaConsumerTargetFramework=$$target_framework; \
		dotnet run --no-build --no-restore --framework $$target_framework --project ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:AhtolaPackageVersion=0.0.0-managed-local -p:AhtolaConsumerTargetFramework=$$target_framework; \
		dotnet publish --configuration Debug --no-build --no-restore --framework $$target_framework --output "$$consumer_output" ./samples/ManagedPackageConsumer/ManagedPackageConsumer.csproj -p:AhtolaPackageVersion=0.0.0-managed-local -p:AhtolaConsumerTargetFramework=$$target_framework; \
		$(MANAGED_CLOSURE_VALIDATOR) -ProjectAssetsFile ./samples/ManagedPackageConsumer/obj/project.assets.json -PublishOutput "$$consumer_output"; \
	done

build: build-managed
test: test-managed
pack: pack-managed
