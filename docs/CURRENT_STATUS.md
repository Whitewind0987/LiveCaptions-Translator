# Current Status

## Repository state

- Branch: `feature/win10-asr-backend`
- Baseline commit: `7a6fe4a5b294dacc4aa1b3666981d6c00dbcd183`
- Upstream repository: `SakiRinn/LiveCaptions-Translator`
- Completed stage: Stage 0 environment and baseline verification
- Source modifications before these documentation files: none
- Git working tree before documentation: clean

## Environment

- .NET SDK: `10.0.201`
- Installed runtimes:
  - `Microsoft.NETCore.App 8.0.29`
  - `Microsoft.WindowsDesktop.App 8.0.29`
  - `Microsoft.NETCore.App 10.0.5`
  - `Microsoft.WindowsDesktop.App 10.0.5`

## Baseline verification

- `dotnet restore`: succeeds
- `dotnet build`: succeeds
- Baseline build warnings: 191

The 191 warnings are existing baseline warnings. Warning cleanup is outside
Stage 0, and these warnings are not addressed in this stage.

## Confirmed Windows 10 incompatibility

The current baseline fails at runtime on Windows 10 because it has a mandatory
startup dependency on Windows Live Captions:

1. `Translator` static initialization calls
   `LiveCaptionsHandler.LaunchLiveCaptions()`.
2. `LaunchLiveCaptions()` calls `Process.Start("LiveCaptions")`.
3. Windows 10 does not provide `LiveCaptions.exe`.
4. Process creation raises `Win32Exception` with error code 2 (file not found),
   which then causes a `TypeInitializationException` for `Translator`.

## Next stage

Stage 1 will remove the mandatory startup dependency on Windows Live Captions
while preserving the existing Windows 11 behavior.

This file must be updated at the end of every future implementation stage.

