{
  description = "Viset local development shell";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
    dotnet-nixpkgs.url = "github:NixOS/nixpkgs/381da955cb03aaa991c7ce15961d3fe50351f895";
  };

  outputs =
    {
      dotnet-nixpkgs,
      nixpkgs,
      ...
    }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      dotnetPkgs = import dotnet-nixpkgs { inherit system; };
    in
    {
      devShells.${system}.default = pkgs.mkShellNoCC {
        packages = [
          dotnetPkgs.dotnet-sdk_10
          pkgs.nixfmt
          pkgs.python3
        ];
      };
    };
}
