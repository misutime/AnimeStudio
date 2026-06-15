using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class Unity2017AnimationBundleProbe
{
    public static void Run()
    {
        var bundlePath = ReadArg("--bundle");
        var clipName = ReadArg("--clip_name");
        var output = ReadArg("--output");
        if (string.IsNullOrEmpty(output))
        {
            output = Path.Combine(Environment.CurrentDirectory, "unity2017_animation_bundle_probe.json");
        }

        var report = new Dictionary<string, object>();
        report["generatedAt"] = DateTime.UtcNow.ToString("O");
        report["unityVersion"] = Application.unityVersion;
        report["bundle"] = bundlePath ?? "";
        report["clipName"] = clipName ?? "";

        try
        {
            if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
            {
                throw new FileNotFoundException("missing --bundle", bundlePath ?? "");
            }

            var bundle = AssetBundle.LoadFromFile(bundlePath);
            report["bundleLoaded"] = bundle != null;
            if (bundle == null)
            {
                report["status"] = "bundle_load_failed";
                WriteJson(output, report);
                return;
            }

            try
            {
                var assetNames = bundle.GetAllAssetNames();
                report["assetNames"] = assetNames;
                var clips = bundle.LoadAllAssets<AnimationClip>();
                report["clipCount"] = clips == null ? 0 : clips.Length;
                report["clips"] = DescribeClips(clips, clipName);

                AnimationClip selected = null;
                if (clips != null)
                {
                    foreach (var clip in clips)
                    {
                        if (clip == null)
                        {
                            continue;
                        }
                        if (string.IsNullOrEmpty(clipName)
                            || string.Equals(clip.name, clipName, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = clip;
                            break;
                        }
                    }
                }

                report["status"] = selected == null ? "clip_not_found" : "ok";
                if (selected != null)
                {
                    report["selected"] = DescribeClip(selected);
                }
            }
            finally
            {
                bundle.Unload(false);
            }
        }
        catch (Exception ex)
        {
            report["status"] = "error";
            report["message"] = ex.Message;
            report["exception"] = ex.GetType().FullName;
        }

        WriteJson(output, report);
        Debug.Log("Unity2017AnimationBundleProbe wrote " + output);
    }

    private static List<Dictionary<string, object>> DescribeClips(AnimationClip[] clips, string clipName)
    {
        var result = new List<Dictionary<string, object>>();
        if (clips == null)
        {
            return result;
        }

        foreach (var clip in clips)
        {
            if (clip == null)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(clipName)
                && clip.name.IndexOf(clipName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            result.Add(DescribeClip(clip));
        }
        return result;
    }

    private static Dictionary<string, object> DescribeClip(AnimationClip clip)
    {
        var item = new Dictionary<string, object>();
        item["name"] = clip.name;
        item["legacy"] = clip.legacy;
        item["humanMotion"] = clip.humanMotion;
        item["isHumanMotion"] = clip.isHumanMotion;
        item["empty"] = clip.empty;
        item["length"] = clip.length;
        item["frameRate"] = clip.frameRate;
        item["curveBindingCount"] = AnimationUtility.GetCurveBindings(clip).Length;
        item["objectReferenceCurveBindingCount"] = AnimationUtility.GetObjectReferenceCurveBindings(clip).Length;
        item["eventsCount"] = AnimationUtility.GetAnimationEvents(clip).Length;
        return item;
    }

    private static string ReadArg(string name)
    {
        var expected = (name ?? "").TrimStart('-');
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals((args[i] ?? "").TrimStart('-'), expected, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void WriteJson(string path, Dictionary<string, object> values)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine("{");
            var first = true;
            foreach (var pair in values)
            {
                if (!first)
                {
                    writer.WriteLine(",");
                }
                first = false;
                writer.Write("  \"");
                writer.Write(Escape(pair.Key));
                writer.Write("\": ");
                WriteValue(writer, pair.Value);
            }
            writer.WriteLine();
            writer.WriteLine("}");
        }
    }

    private static void WriteValue(TextWriter writer, object value)
    {
        if (value == null)
        {
            writer.Write("null");
            return;
        }
        if (value is bool)
        {
            writer.Write((bool)value ? "true" : "false");
            return;
        }
        if (value is int || value is long || value is float || value is double)
        {
            writer.Write(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }
        var stringArray = value as string[];
        if (stringArray != null)
        {
            WriteArray(writer, stringArray);
            return;
        }
        var objectList = value as List<Dictionary<string, object>>;
        if (objectList != null)
        {
            WriteObjectList(writer, objectList);
            return;
        }
        var dict = value as Dictionary<string, object>;
        if (dict != null)
        {
            WriteObject(writer, dict);
            return;
        }

        writer.Write("\"");
        writer.Write(Escape(Convert.ToString(value)));
        writer.Write("\"");
    }

    private static void WriteArray(TextWriter writer, string[] values)
    {
        writer.Write("[");
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(", ");
            }
            writer.Write("\"");
            writer.Write(Escape(values[i]));
            writer.Write("\"");
        }
        writer.Write("]");
    }

    private static void WriteObjectList(TextWriter writer, List<Dictionary<string, object>> values)
    {
        writer.Write("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(", ");
            }
            WriteObject(writer, values[i]);
        }
        writer.Write("]");
    }

    private static void WriteObject(TextWriter writer, Dictionary<string, object> values)
    {
        writer.Write("{");
        var first = true;
        foreach (var pair in values)
        {
            if (!first)
            {
                writer.Write(", ");
            }
            first = false;
            writer.Write("\"");
            writer.Write(Escape(pair.Key));
            writer.Write("\": ");
            WriteValue(writer, pair.Value);
        }
        writer.Write("}");
    }

    private static string Escape(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
