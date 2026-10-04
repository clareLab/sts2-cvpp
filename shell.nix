{ pkgs ? import <nixpkgs> {} }:
pkgs.mkShell {
  packages = [ pkgs.dotnet-sdk_9 pkgs.cargo pkgs.rustc pkgs.rustfmt pkgs.clippy pkgs.python3 pkgs.ruff pkgs.shellcheck pkgs.ripgrep ];
  LD_LIBRARY_PATH = pkgs.lib.makeLibraryPath [ pkgs.stdenv.cc.cc.lib pkgs.zlib ];
  DOTNET_CLI_TELEMETRY_OPTOUT = "1";
  DOTNET_NOLOGO = "1";
}
