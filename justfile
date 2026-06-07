set shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"]

browser_project := "AnimeStudio.LibraryBrowser/AnimeStudio.LibraryBrowser.csproj"
browser_framework := "net9.0-windows"
browser_publish_dir := "artifacts/AnimeStudio.LibraryBrowser"

# 显示可用命令。
default:
    @just --list

# 还原 Library Browser 依赖。
browser-restore:
    @dotnet restore "{{browser_project}}"

# Debug 构建 Library Browser。
browser-build:
    @dotnet build "{{browser_project}}" -f {{browser_framework}}

# Release 构建 Library Browser。
browser-build-release:
    @dotnet build "{{browser_project}}" -f {{browser_framework}} -c Release

# 输出到系统临时目录，适合 exe 正在运行、bin 目录被锁住时做编译验证。
browser-build-temp:
    @$out = Join-Path $env:TEMP "AnimeStudio.LibraryBrowser-build"; dotnet build "{{browser_project}}" -f {{browser_framework}} -o $out; Write-Host "输出目录: $out"

# 启动 Library Browser 调试。
browser-run:
    @dotnet run --project "{{browser_project}}" -f {{browser_framework}}

# 清理 Library Browser 构建产物。
browser-clean:
    @dotnet clean "{{browser_project}}" -f {{browser_framework}}

# 发布一个可直接运行的 Release 目录。
browser-publish:
    @dotnet publish "{{browser_project}}" -f {{browser_framework}} -c Release -o "{{browser_publish_dir}}"

# 单独调试一个 glTF 缩略图渲染。
browser-render-thumbnail gltf output:
    @dotnet run --project "{{browser_project}}" -f {{browser_framework}} -- --render-thumbnail "{{gltf}}" "{{output}}"
