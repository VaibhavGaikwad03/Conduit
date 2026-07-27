# Builds ConduitCamera.dll (the virtual-camera COM source) with the VS 2022 toolchain.
# Run from anywhere; output lands in native/ConduitCamera/build/Release.
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "ConduitCamera"
$build = Join-Path $src "build"

$cmake = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"

& $cmake -S $src -B $build -G "Visual Studio 17 2022" -A x64
& $cmake --build $build --config Release

Write-Output "`nBuilt: $build\Release\ConduitCamera.dll"
