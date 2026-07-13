# Repository Guidelines

## Project Structure & Module Organization

This repository builds a Windows 64-bit Whisper desktop app from C++, .NET, and React. Open `WhisperCpp.sln` for the full Visual Studio workspace. Core native code lives in `Whisper/`, `Native/`, `ComputeShaders/`, and `ComLightLib/`. .NET interop and packaging support live in `WhisperNet/` and `WhisperPS/`. Apps and samples are under `Examples/`: `WhisperDesktop.Wpf/` is the WPF + WebView2 shell, `WhisperDesktop.Web/` is the Vite/React UI, `WhisperDesktop.Cli/` builds the `whisperctl` desktop-control client, and `WhisperDesktop.Protocol/` owns its Named Pipe JSON contract. `TranscribeCS/` and `MicrophoneCS/` remain independent CLI examples. Build helpers are in `Tools/`; screenshots and docs assets are in `images/` and `docs/`.

## Build, Test, and Development Commands

- `Tools\build-webui.cmd`: installs/builds the React UI into `Examples\WhisperDesktop.Web\dist\`.
- `cd Examples\WhisperDesktop.Web; npm run dev`: starts Vite, normally on `localhost:5173`, for WPF hot reload.
- `Tools\build-whispercpp.cmd`: builds the optional CPU/CUDA whisper.cpp backend DLLs.
- `Tools\package-daily.cmd`: builds `Release|x64` and produces a portable package under `Releases\Daily\YYYY-MM-DD\`.
- `dotnet build Examples\WhisperDesktop.Cli\WhisperDesktop.Cli.csproj -c Release`: builds `whisperctl.exe` and the shared desktop-control protocol.
- `dotnet run --project Examples\TranscribeCS\TranscribeCS.csproj -c Debug -p:Platform=x64 -- --engine cuda -m models\ggml-medium.bin -l zh -osrt sample.wav`: smoke-tests file transcription.

For full native builds, use Visual Studio 2022/2026, select `x64`, build `ComputeShaders` first, then build the solution.

## Coding Style & Naming Conventions

C# projects target modern .NET with nullable references and implicit usings where enabled; use 4-space indentation, PascalCase for public types/members, camelCase for locals, and descriptive async names. C++ projects use MSVC v143, C++20, `/utf-8`, warning level 4, and Unicode character sets. React code is TypeScript modules; use PascalCase component names and keep UI assets close to the web project.

## Testing Guidelines

Use focused smoke tests for the changed surface. Run `dotnet run --project Tools\ModernUiTests\ModernUiTests.csproj` for subtitle pipeline checks. Build or run `Tools\SubtitleTests\SubtitleTests.vcxproj` from Visual Studio for native subtitle queue/pipeline coverage. For Agent protocol changes, build both `WhisperDesktop.Wpf` and `WhisperDesktop.Cli`, then smoke-test `whisperctl ping --json`, submit/status/result, persistence across a desktop restart, and any affected `ui-state` or screenshot path. For UI changes, run `npm run build` in `Examples\WhisperDesktop.Web` before packaging.

## Commit & Pull Request Guidelines

Recent commit messages are short, imperative summaries such as `Modernize TranscribeCS CLI backend` or `Add Gemini subtitle optimization reports`. Follow that style: one clear sentence, no trailing period. Pull requests should describe the user-visible change, list build/test commands run, note model/runtime requirements, and include screenshots for WPF or React UI changes.

## Security & Configuration Tips

Do not commit model files, local audio, generated packages, or machine-specific build output. Keep large scratch work in `.tmp/` and release artifacts in `Releases/`. Verify WebView2, .NET Desktop Runtime, CUDA, and model paths locally before reporting runtime issues.
