using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class AvatarOracleExporter
    {
        public static void Export(string sourceIndexPath, string selector, string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Error($"Avatar oracle export failed: source index not found: {sourceIndexPath}");
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT source_path, serialized_file, path_id, name, raw_json
FROM source_objects
WHERE type='Avatar'
ORDER BY name COLLATE NOCASE, path_id;";

            JObject selected = null;
            string selectedSourcePath = null;
            string selectedSerializedFile = null;
            long selectedPathId = 0;
            string selectedName = null;
            JObject selectedOracle = null;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sourcePath = reader.GetString(0);
                var serializedFile = reader.GetString(1);
                var pathId = reader.GetInt64(2);
                var name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var rawJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    continue;
                }

                JObject raw;
                try
                {
                    raw = JObject.Parse(rawJson);
                }
                catch (JsonException)
                {
                    continue;
                }

                var oracle = raw["avatar"]?["oracle"] as JObject;
                if (oracle == null || !IsCompleteEnough(oracle))
                {
                    continue;
                }

                if (!MatchesSelector(selector, sourcePath, serializedFile, pathId, name))
                {
                    continue;
                }

                selected = raw;
                selectedSourcePath = sourcePath;
                selectedSerializedFile = serializedFile;
                selectedPathId = pathId;
                selectedName = string.IsNullOrWhiteSpace(name) ? $"Avatar_{pathId}" : name;
                selectedOracle = oracle;
                break;
            }

            if (selected == null || selectedOracle == null)
            {
                Logger.Error(string.IsNullOrWhiteSpace(selector)
                    ? "Avatar oracle export failed: no complete AvatarConstant oracle found in source index."
                    : $"Avatar oracle export failed: no complete AvatarConstant oracle matched selector: {selector}");
                return;
            }

            var targetFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceIndexPath)) ?? Environment.CurrentDirectory, "AvatarOracleExports")
                : outputFolder;
            Directory.CreateDirectory(targetFolder);

            var safeName = MakeSafeFileName(selectedName);
            var outputPath = Path.Combine(targetFolder, $"{safeName}_{selectedPathId.ToString(CultureInfo.InvariantCulture)}_avatar_oracle.json");
            var result = new JObject
            {
                ["version"] = 1,
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                ["status"] = "ok",
                ["note"] = "这是从 Unity AvatarConstant 导出的确定性 oracle。它用于后续恢复 Unity bake 的 Avatar，不等于已经完成生产 bake。",
                ["avatar"] = new JObject
                {
                    ["name"] = selectedName,
                    ["pathId"] = selectedPathId,
                    ["sourcePath"] = selectedSourcePath,
                    ["serializedFile"] = selectedSerializedFile,
                    ["hasHumanDescription"] = selected["avatar"]?["hasHumanDescription"] ?? false,
                    ["humanDescriptionSkipReason"] = selected["avatar"]?["humanDescriptionSkipReason"],
                },
                ["counts"] = BuildCounts(selectedOracle),
                ["oracle"] = selectedOracle.DeepClone(),
            };

            File.WriteAllText(outputPath, result.ToString(Formatting.Indented));
            Logger.Info($"Avatar oracle exported: {outputPath}");
        }

        private static bool IsCompleteEnough(JObject oracle)
        {
            var humanBoneCount = JsonArrayCount(oracle["humanBoneIndex"]);
            var humanNodeCount = JsonInt(oracle["humanSkeleton"]?["nodeCount"]);
            var humanPoseCount = JsonArrayCount(oracle["humanSkeleton"]?["pose"]);
            var avatarNodeCount = JsonInt(oracle["avatarSkeleton"]?["nodeCount"]);
            var avatarDefaultPoseCount = JsonArrayCount(oracle["avatarSkeleton"]?["defaultPose"]);
            return humanBoneCount > 0
                && humanNodeCount > 0
                && humanPoseCount >= humanNodeCount
                && avatarNodeCount > 0
                && avatarDefaultPoseCount >= avatarNodeCount;
        }

        private static JObject BuildCounts(JObject oracle)
        {
            return new JObject
            {
                ["humanBoneIndex"] = JsonArrayCount(oracle["humanBoneIndex"]),
                ["humanSkeletonNodes"] = JsonInt(oracle["humanSkeleton"]?["nodeCount"]),
                ["humanSkeletonPose"] = JsonArrayCount(oracle["humanSkeleton"]?["pose"]),
                ["avatarSkeletonNodes"] = JsonInt(oracle["avatarSkeleton"]?["nodeCount"]),
                ["avatarDefaultPose"] = JsonArrayCount(oracle["avatarSkeleton"]?["defaultPose"]),
            };
        }

        private static bool MatchesSelector(string selector, string sourcePath, string serializedFile, long pathId, string name)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            var id = pathId.ToString(CultureInfo.InvariantCulture);
            return Contains(name, selector)
                || string.Equals(id, selector.Trim(), StringComparison.OrdinalIgnoreCase)
                || Contains(serializedFile, selector)
                || Contains(sourcePath, selector);
        }

        private static bool Contains(string value, string selector)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int JsonArrayCount(JToken token)
        {
            return token is JArray array ? array.Count : 0;
        }

        private static int JsonInt(JToken token)
        {
            return token?.Type == JTokenType.Integer ? token.Value<int>() : 0;
        }

        private static string MakeSafeFileName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "Avatar" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name;
        }
    }
}
