# Building the engine for Windows

The engine is a Rust binary. Windows cannot run the Linux build, and the shell reports `is not a Windows build` when it tries.

## Toolchain, installed once

Neither Visual Studio nor the Windows SDK is required. The GNU target works with a MinGW linker.

```sh
winget install --id Rustlang.Rustup -e          # or run rustup-init.exe
curl -sSL -o mingw.zip https://github.com/brechtsanders/winlibs_mingw/releases/download/16.2.0posix-23.1.3-14.0.0-ucrt-r3/winlibs-x86_64-posix-seh-gcc-16.2.0-mingw-w64ucrt-14.0.0-r3.zip
powershell -c "Expand-Archive mingw.zip -DestinationPath $env:LOCALAPPDATA\emendly-tools\mingw"
rustup toolchain install stable-x86_64-pc-windows-gnu --profile minimal
rustup default stable-x86_64-pc-windows-gnu
```

Point Cargo at the linker, in `%USERPROFILE%\.cargo\config.toml`:

```toml
[target.x86_64-pc-windows-gnu]
linker = "C:/Users/<user>/AppData/Local/emendly-tools/mingw/mingw64/bin/gcc.exe"
```

## Build

```sh
export PATH="$USERPROFILE/.cargo/bin:$LOCALAPPDATA/emendly-tools/mingw/mingw64/bin:$PATH"
export CARGO_INCREMENTAL=0
cargo build --manifest-path '\\wsl.localhost\Ubuntu-24.04\home\<user>\github\emendly\engine\Cargo.toml'
```

`CARGO_INCREMENTAL=0` is mandatory when the sources live in WSL. Incremental compilation needs a lock the 9p filesystem does not provide, and the build fails with `could not create session directory lock file`.

Output: `engine/target/debug/emendly-engine.exe`, a PE32+ binary. Release builds work the same way.

## Check it

```sh
printf '{"id":"a","type":"health"}\n' | engine/target/debug/emendly-engine.exe --once
# {"id":"a","event":"health","version":"0.1.0","protocol":1}
```

## What the shell looks for

`EMENDLY_ENGINE`, else `../../engine/target/debug/emendly-engine` relative to the app directory. On Windows the `.exe` suffix is appended by the loader, so the same path works for both builds.

## Known limitation

The engine needs `EMENDLY_API_KEY` at call time. Without it, every action returns `{"code":"missing_key"}` and the panel shows that message. That is a configuration gap, not a build failure.
