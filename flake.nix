{
  description = "Script reproducible browser screenshots and animations";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
    dotnet-nixpkgs.url = "github:NixOS/nixpkgs/381da955cb03aaa991c7ce15961d3fe50351f895";
  };

  outputs =
    {
      self,
      dotnet-nixpkgs,
      nixpkgs,
    }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      mkPkgs = system: import nixpkgs { inherit system; };
      mkDotnetPkgs = system: import dotnet-nixpkgs { inherit system; };

      mkBrowser =
        pkgs:
        let
          browserLock = builtins.fromTOML (builtins.readFile ./browser-lock.toml);
          appleLock = browserLock.platforms."osx-arm64";
          appleArchive = pkgs.fetchurl {
            inherit (appleLock) url sha256;
          };
          appleBrowser =
            pkgs.runCommand "chrome-for-testing-${browserLock.browser_version}"
              {
                nativeBuildInputs = [ pkgs.unzip ];
              }
              ''
                mkdir -p "$out"
                unzip -q ${appleArchive} -d "$out"
              '';
        in
        if pkgs.stdenv.hostPlatform.isLinux then
          {
            package = pkgs.chromium;
            executable = nixpkgs.lib.getExe pkgs.chromium;
          }
        else if pkgs.stdenv.hostPlatform.isDarwin then
          {
            package = appleBrowser;
            executable = "${appleBrowser}/${appleLock.executable}";
          }
        else
          throw "Viset's Nix browser wrapper does not support ${pkgs.stdenv.hostPlatform.system}";

      mkViset =
        system:
        let
          pkgs = mkPkgs system;
          inherit (pkgs) lib;
          browser = mkBrowser pkgs;
          sdk = (mkDotnetPkgs system).dotnet-sdk_10;
          sdkPackageNames = map (package: package.name) sdk.packages;
          nugetDependencies = map pkgs.dotnetCorePackages.fetchNupkg (
            builtins.filter (
              dependency: !(lib.elem "${dependency.pname}-${dependency.version}" sdkPackageNames)
            ) (builtins.fromJSON (builtins.readFile ./nix/deps.json))
          );
        in
        pkgs.buildDotnetModule {
          pname = "viset";
          version = "0.1.0";

          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [
              ./Directory.Build.props
              ./Directory.Packages.props
              ./browser-lock.toml
              ./global.json
              ./nuget.config
              ./packages.lock.json
              ./src/Viset
              ./src/Viset.Serialization
            ];
          };

          projectFile = "src/Viset/Viset.fsproj";
          nugetDeps = nugetDependencies;
          dotnet-sdk = sdk;
          selfContainedBuild = true;
          executables = [ "viset" ];

          configurePhase = ''
            runHook preConfigure
            # Nix normalizes NuGet archives; nix/deps.json is the fixed-output build lock.
            dotnet restore src/Viset/Viset.fsproj \
              -p:ContinuousIntegrationBuild=true \
              -p:Deterministic=true \
              -p:NuGetAudit=false \
              -p:RestoreLockedMode=false \
              --force-evaluate
            runHook postConfigure
          '';
          dotnetBuildFlags = [
            "-p:PublishAot=true"
            "-p:PublishTrimmed=true"
          ];
          dotnetInstallFlags = [
            "-p:PublishAot=true"
            "-p:PublishTrimmed=true"
          ];

          nativeBuildInputs = [ pkgs.clang ];
          buildInputs = [ pkgs.zlib ];
          runtimeDeps = [ pkgs.openssl ];

          postInstall = ''
            cp browser-lock.toml "$out/lib/viset/browser-lock.toml"
            rm -f "$out/lib/viset/"*.dbg "$out/lib/viset/"*.pdb
            rm -f "$out/lib/viset/libwebpdemux."*
            rm -rf "$out/lib/viset/viset.dSYM"
          '';

          makeWrapperArgs = [
            "--set-default"
            "VISET_BROWSER"
            browser.executable
          ];

          meta = {
            description = "Script reproducible browser screenshots and animations";
            homepage = "https://github.com/alsi-lawr/Viset";
            license = lib.licenses.mit;
            mainProgram = "viset";
            platforms = systems;
          };
        };

      mkCliCheck =
        system:
        let
          pkgs = mkPkgs system;
          viset = self.packages.${system}.default;
        in
        pkgs.runCommand "viset-cli-check" { } ''
          test "$(${viset}/bin/viset --version)" = "viset 0.1.0"
          ${viset}/bin/viset --help > help.txt
          grep -F "viset capture" help.txt
          test -x "${viset}/lib/viset/viset"
          test -f "${viset}/lib/viset/browser-lock.toml"
          touch "$out"
        '';

    in
    {
      packages = forAllSystems (system: {
        default = mkViset system;
        viset = self.packages.${system}.default;
      });

      apps = forAllSystems (system: {
        default = self.apps.${system}.viset;
        viset = {
          type = "app";
          program = "${self.packages.${system}.default}/bin/viset";
          meta = self.packages.${system}.default.meta;
        };
      });

      devShells = forAllSystems (
        system:
        let
          pkgs = mkPkgs system;
          dotnetPkgs = mkDotnetPkgs system;
          browser = mkBrowser pkgs;
        in
        {
          default = pkgs.mkShellNoCC {
            packages = [
              pkgs.clang
              dotnetPkgs.dotnet-sdk_10
              pkgs.lua-language-server
              pkgs.nixfmt
              pkgs.python3
              pkgs.tree-sitter
              pkgs.zlib
              browser.package
            ];

            shellHook = ''
              if test -z "''${VISET_BROWSER-}"; then
                export VISET_BROWSER=${nixpkgs.lib.escapeShellArg browser.executable}
              fi
            '';
          };
        }
      );

      formatter = forAllSystems (system: (mkPkgs system).nixfmt);

      checks = forAllSystems (system: {
        package = self.packages.${system}.default;
        cli = mkCliCheck system;
      });
    };
}
