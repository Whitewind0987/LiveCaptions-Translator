# Testing

## Stage 0 baseline

### Git checks

- Branch: `feature/win10-asr-backend`
- `origin`: `Whitewind0987/LiveCaptions-Translator`
- `upstream`: `SakiRinn/LiveCaptions-Translator`
- Working tree before documentation: clean
- `HEAD`: synchronized with `upstream/master` at
  `7a6fe4a5b294dacc4aa1b3666981d6c00dbcd183`

### Build checks

- `dotnet restore`: passed
- `dotnet build`: passed
- Target framework: `net8.0-windows`
- Baseline warnings: 191

Warning cleanup is outside Stage 0. The warning count records the existing
baseline rather than an acceptance requirement for later stages.

### Windows 10 runtime check

- `dotnet run`: failed as expected on Windows 10
- Exception chain: `TypeInitializationException` for `Translator`, caused by a
  `Win32Exception` with error code 2
- Root cause: `Translator` static initialization calls
  `LiveCaptionsHandler.LaunchLiveCaptions()`, which calls
  `Process.Start("LiveCaptions")`; Windows 10 does not provide
  `LiveCaptions.exe`

Failure is the expected current baseline behavior. Stage 0 records the failure
and does not change runtime behavior.

### Acceptance result

- Environment is valid.
- The repository restores and builds.
- The Windows 10 incompatibility is reproduced and understood.
- No implementation changes were made.

## Future fixed audio fixtures

To be defined in a later implementation stage. No fixture results are recorded
in Stage 0.

## Future regression tests

To be defined and populated as implementation stages add independently testable
behavior. No regression-test results are recorded in Stage 0.

