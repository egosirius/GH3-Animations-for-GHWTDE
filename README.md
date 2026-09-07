# GH3 Animations for GHWTDE — Patcher Source Code

This repository contains the complete source code for the installer and uninstaller used by **GH3 Animations for GHWTDE**.

The program applies reversible binary animation patches to a compatible installation of the `Guitar Hero 3 Legends Of Rock` song pack for **Guitar Hero World Tour: Definitive Edition**. It does not contain songs, audio, complete PAK files, proprietary game assets, or the binary animation patch data distributed separately with the mod.

## What the program does

- Verifies the SHA-256 hash of every supported song PAK before changing anything.
- Refuses to modify unknown or incompatible files.
- Applies the animation-only delta for each supported song.
- Validates the resulting file against its expected SHA-256 hash.
- Creates timestamped safety backups before installation or removal.
- Rolls back files already processed if an operation fails.
- Uses the same bidirectional patch data for installation and uninstallation.
- Refuses to run while the GHWTDE game process is open.

The patch format stores copy and addition commands compressed with the .NET `DeflateStream` implementation. File paths are validated before use so that manifest entries cannot escape the intended package or song-pack directories.

## Source files

- `Program.cs` — complete patching, validation, backup, and rollback logic.
- `GH3AnimationsForGHWTDE.csproj` — .NET project and publishing configuration.

The application has no third-party NuGet dependencies. It uses only the .NET 8 standard library.

## Requirements for building

- Windows 64-bit.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Build instructions

Clone or download this repository, open PowerShell in its directory, and run:

```powershell
dotnet restore
dotnet publish .\GH3AnimationsForGHWTDE.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The resulting standalone executable will be located at:

```text
bin\Release\net8.0\win-x64\publish\GH3AnimationsForGHWTDE.exe
```

Because the project is published as a self-contained single file, users do not need to install the .NET runtime separately. The resulting executable is relatively large because the required runtime components are bundled inside it.

## Command-line interface

```text
GH3AnimationsForGHWTDE.exe install <package-directory> [DATA\MODS-directory]
GH3AnimationsForGHWTDE.exe uninstall <package-directory> [DATA\MODS-directory]
GH3AnimationsForGHWTDE.exe verify <package-directory>
GH3AnimationsForGHWTDE.exe make-patches <install-report.json> <package-directory>
GH3AnimationsForGHWTDE.exe test-patches <install-report.json> <package-directory>
```

The public mod package uses `Installer.cmd` and `Uninstaller.cmd` as simple launchers for the first two commands.

## Distributed executable

SHA-256 of the executable submitted with the initial Nexus Mods package:

```text
E789C30918304D43DCDAA0E7248A7A408CAEC87B548B7B4AB734E56CA65E1C59
```

The executable is not digitally signed. Its self-contained format and lack of an established reputation may cause automated security systems to hold it for manual review. This repository is provided so that moderators and users can inspect the complete source and reproduce the build.

## Credits

- Project direction and testing: the GH3 Animations for GHWTDE project author.
- Translation system and installer developed collaboratively with OpenAI Codex.
- Addy Mills for the Guitar Hero tooling used during research and development.
- The GHWT: Definitive Edition team for expanding and maintaining the game.
- Original animation work by the developers of Guitar Hero III and Guitar Hero: Smash Hits.

## Disclaimer

This is an unofficial community project. It is not affiliated with or endorsed by Activision, Neversoft, or the Guitar Hero World Tour: Definitive Edition team.

