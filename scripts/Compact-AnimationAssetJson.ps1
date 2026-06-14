param(
    [string]$LibraryRoot,
    [string]$AnimationDir,
    [double]$MinSizeMB = 1,
    [int]$MaxBindingPaths = 128,
    [int]$MaxBindings = 256,
    [switch]$Backup,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($AnimationDir)) {
    if ([string]::IsNullOrWhiteSpace($LibraryRoot)) {
        throw "Please pass -LibraryRoot <AS library root> or -AnimationDir <Animations directory>."
    }

    $AnimationDir = Join-Path $LibraryRoot "Animations"
}

if (-not (Test-Path -LiteralPath $AnimationDir)) {
    throw "Animation directory does not exist: $AnimationDir"
}

$helperDir = Join-Path ([System.IO.Path]::GetTempPath()) "AnimeStudioCompactAnimationJson"
$helperCs = Join-Path $helperDir "CompactAnimationAssetJson.cs"
$helperProj = Join-Path $helperDir "CompactAnimationAssetJson.csproj"
$helperOut = Join-Path $helperDir "out"
$helperExe = Join-Path $helperOut "CompactAnimationAssetJson.exe"
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null

$helperSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

internal static class Program
{
    private static readonly HashSet<string> DecodedCurveArrays = new(StringComparer.OrdinalIgnoreCase)
    {
        "translations", "rotations", "scales", "eulers", "floats", "pptrs"
    };

    private static readonly HashSet<string> LongPathArrays = new(StringComparer.OrdinalIgnoreCase)
    {
        "transformBindingPaths", "bindingPaths", "nodePaths", "bonePaths", "meshPaths"
    };

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (string.IsNullOrWhiteSpace(options.Directory) || !Directory.Exists(options.Directory))
        {
            Console.Error.WriteLine("Animation directory does not exist.");
            return 2;
        }

        var files = Directory.EnumerateFiles(options.Directory, "*.animation_asset.json", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .Where(x => x.Length >= options.MinBytes)
            .OrderByDescending(x => x.Length)
            .ToList();

        long before = 0;
        long after = 0;
        int processed = 0;
        int changed = 0;
        int failed = 0;

        Console.WriteLine($"Scanning {options.Directory}");
        Console.WriteLine($"Candidate sidecars: {files.Count}, minSizeMB={options.MinBytes / 1024d / 1024d:0.##}, dryRun={options.DryRun}");

        foreach (var file in files)
        {
            processed++;
            before += file.Length;
            try
            {
                var result = CompactFile(file.FullName, options);
                after += result.AfterBytes;
                if (result.Changed)
                {
                    changed++;
                }

                if (processed == 1 || processed % 100 == 0 || result.BeforeBytes >= 64L * 1024 * 1024)
                {
                    Console.WriteLine(
                        $"[{processed}/{files.Count}] {(result.Changed ? "compacted" : "kept")} " +
                        $"{Path.GetFileName(file.FullName)} {ToMB(result.BeforeBytes):0.##}MB -> {ToMB(result.AfterBytes):0.##}MB");
                }
            }
            catch (Exception ex)
            {
                failed++;
                after += file.Length;
                Console.Error.WriteLine($"[failed] {file.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"Done. processed={processed}, changed={changed}, failed={failed}, " +
            $"before={ToGB(before):0.##}GB, after={ToGB(after):0.##}GB, saved={ToGB(before - after):0.##}GB");
        return failed > 0 ? 1 : 0;
    }

    private static CompactResult CompactFile(string path, Options options)
    {
        var before = new FileInfo(path).Length;

        var temp = path + ".compact.tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        // Keep all readers/writers inside this block so the source JSON is fully closed
        // before we replace it with the compacted temp file.
        using (var input = File.OpenRead(path))
        using (var doc = JsonDocument.Parse(input, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 256,
        }))
        {
            using var output = File.Create(temp);
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            WriteElement(doc.RootElement, writer, null, options);
        }

        var after = new FileInfo(temp).Length;
        var changed = after < before;

        if (options.DryRun)
        {
            File.Delete(temp);
            return new CompactResult(before, after, changed);
        }

        if (!changed)
        {
            File.Delete(temp);
            return new CompactResult(before, before, false);
        }

        if (options.Backup)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }

        ReplaceFileWithRetry(temp, path);
        return new CompactResult(before, after, true);
    }

    private static void ReplaceFileWithRetry(string temp, string path)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                try
                {
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    // Best effort only. The replace below will report the real failure if it matters.
                }

                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(150 * attempt);
            }
        }

        try
        {
            File.Copy(temp, path, true);
            File.Delete(temp);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            last = ex;
        }

        throw new IOException($"Unable to replace compacted JSON after retries: {path}", last);
    }

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer, string? propertyName, Options options)
    {
        if (string.Equals(propertyName, "decoded", StringComparison.OrdinalIgnoreCase)
            && element.ValueKind == JsonValueKind.Object)
        {
            WriteCompactedDecoded(element, writer);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer, property.Name, options);
                }

                if (string.Equals(propertyName, null, StringComparison.OrdinalIgnoreCase))
                {
                    // unreachable; kept explicit through top-level injection below
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                WriteArray(element, writer, propertyName, options);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteArray(JsonElement element, Utf8JsonWriter writer, string? propertyName, Options options)
    {
        var limit = int.MaxValue;
        if (propertyName != null && LongPathArrays.Contains(propertyName))
        {
            limit = Math.Max(0, options.MaxBindingPaths);
        }
        else if (string.Equals(propertyName, "bindings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(propertyName, "muscleBindings", StringComparison.OrdinalIgnoreCase))
        {
            limit = Math.Max(0, options.MaxBindings);
        }

        writer.WriteStartArray();
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (i >= limit)
            {
                break;
            }
            WriteElement(item, writer, propertyName, options);
            i++;
        }
        writer.WriteEndArray();
    }

    private static void WriteCompactedDecoded(JsonElement decoded, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("status", "compacted");

        if (decoded.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            writer.WriteString("originalStatus", status.GetString());
        }

        CopyString(decoded, writer, "coordinateSpace");
        CopyString(decoded, writer, "note");

        if (decoded.TryGetProperty("curveCounts", out var curveCounts))
        {
            writer.WritePropertyName("curveCounts");
            curveCounts.WriteTo(writer);
        }
        else
        {
            writer.WritePropertyName("curveCounts");
            writer.WriteStartObject();
            foreach (var name in DecodedCurveArrays)
            {
                if (decoded.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteNumber(name, arr.GetArrayLength());
                }
            }
            writer.WriteEndObject();
        }

        writer.WriteString("compaction", "Full decoded keyframe arrays were removed by Compact-AnimationAssetJson.ps1. Use Unity bake or a targeted re-export when detailed curves are required.");
        writer.WriteEndObject();
    }

    private static void CopyString(JsonElement obj, Utf8JsonWriter writer, string property)
    {
        if (obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            writer.WriteString(property, value.GetString());
        }
    }

    private static double ToMB(long bytes) => bytes / 1024d / 1024d;
    private static double ToGB(long bytes) => bytes / 1024d / 1024d / 1024d;

    private sealed record CompactResult(long BeforeBytes, long AfterBytes, bool Changed);

    private sealed class Options
    {
        public string Directory { get; private set; } = "";
        public long MinBytes { get; private set; } = 1024 * 1024;
        public int MaxBindingPaths { get; private set; } = 128;
        public int MaxBindings { get; private set; } = 256;
        public bool Backup { get; private set; }
        public bool DryRun { get; private set; }

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--dir":
                        result.Directory = args[++i];
                        break;
                    case "--min-bytes":
                        result.MinBytes = long.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--max-binding-paths":
                        result.MaxBindingPaths = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--max-bindings":
                        result.MaxBindings = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--backup":
                        result.Backup = true;
                        break;
                    case "--dry-run":
                        result.DryRun = true;
                        break;
                }
            }
            return result;
        }
    }
}
'@

Set-Content -LiteralPath $helperCs -Value $helperSource -Encoding UTF8

$helperProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>CompactAnimationAssetJson</AssemblyName>
  </PropertyGroup>
</Project>
'@
Set-Content -LiteralPath $helperProj -Value $helperProject -Encoding UTF8

if (-not (Test-Path -LiteralPath $helperExe) -or (Get-Item -LiteralPath $helperExe).LastWriteTimeUtc -lt (Get-Item -LiteralPath $helperCs).LastWriteTimeUtc) {
    & dotnet publish $helperProj -c Release -o $helperOut --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile compact helper."
    }
}

$minBytes = [long]($MinSizeMB * 1MB)
$helperArgs = @(
    "--dir", $AnimationDir,
    "--min-bytes", $minBytes,
    "--max-binding-paths", $MaxBindingPaths,
    "--max-bindings", $MaxBindings
)
if ($Backup) { $helperArgs += "--backup" }
if ($DryRun) { $helperArgs += "--dry-run" }

& $helperExe @helperArgs
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Animation sidecar compaction finished with some failed file(s). Exit code: $LASTEXITCODE. Close apps that may be reading the library and re-run the same command to compact remaining files."
}
