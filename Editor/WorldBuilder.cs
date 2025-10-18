using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using VRC;
using VRC.Core;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Editor.Validation;

namespace AutoBuilder
{
    public class WorldBuilder : BuilderCore
    {
        public static async Task<BuiltWorld> Build(string blueprintId = null)
        {
            Log("Ensuring Blueprint ID is set");
            var manager = FetchPipelineManager();
            if (!string.IsNullOrEmpty(blueprintId)) manager.blueprintId = blueprintId;
            if (string.IsNullOrEmpty(manager.blueprintId)) manager.AssignId();
            blueprintId = manager.blueprintId;

            Log("Preparing SDK for World Build");
            var builder = GetSdkWorldBuilder();

            var user = await TryLogin();

            if (!builder.IsValidBuilder(out var message)) throw new Exception("Builder is invalid: " + message); // This method will cause a scan for scene descriptors, removing one possible method of failure.

            Log("Building World...");
            DateTime preBuildTime = DateTime.Now;
            var path = await builder.Build();
            if (path == null) throw new Exception("Built path is null!");

            var file = new FileInfo(path);
            var signature = SessionState.GetString("VRC.SDK3.Editor_worldSignatureLastBuild", null);
            File.WriteAllText($"{file.DirectoryName}\\{file.Name.Substring(0, file.Name.Length - 5)}.sig", signature);
            if (new FileInfo(path).LastWriteTimeUtc < preBuildTime) throw new Exception("World file was not updated!");
            Log("Build complete!");

            return new BuiltWorld
            {
                blueprintId = blueprintId,
                path = path,
                signature = signature
            };
        }

        public static async Task<VRCWorld> Upload(string path, string blueprintId, string signature, bool? mobile = null)
        {
            var user = await TryLogin();

            if (!File.Exists(path)) throw new Exception($"Could not find built world file: {path}");

            Log("Preparing for World Upload");
            
            if (mobile == null) mobile = ValidationEditorHelpers.IsMobilePlatform();
            if (ValidationEditorHelpers.CheckIfAssetBundleFileTooLarge(ContentType.World, path, out int fileSize, mobile.Value))
            {
                var limit = ValidationHelpers.GetAssetBundleSizeLimit(ContentType.World, mobile.Value);
                throw new Exception($"World download size is too large! {ValidationHelpers.FormatFileSize(fileSize)} > {ValidationHelpers.FormatFileSize(limit)}");
            }

            if (ValidationEditorHelpers.CheckIfAssetBundleFileTooLarge(ContentType.World, path, out int uncompressedSize,
                    mobile.Value))
            {
                var limit = ValidationHelpers.GetAssetBundleSizeLimit(ContentType.World, mobile.Value);
                throw new Exception($"World uncompressed size is too large! {ValidationHelpers.FormatFileSize(uncompressedSize)} > {ValidationHelpers.FormatFileSize(limit)}");
            }

            // Assume the world already exists, I guess?

            Log("Fetching world info");
            var world = await VRCApi.GetWorld(blueprintId);
            if (world.AuthorId != "" && world.AuthorId != user.id) throw new ArgumentException("User ID does not match target world author!");

            Log("Uploading World...");
            world = await VRCApi.UpdateWorldBundle(blueprintId, world, path, signature,
                (status, percentage) => Log($"Uploading... {status}: {percentage * 100}%"));

            Log("Upload complete!");
            return world;
        }

        public static IVRCSdkWorldBuilderApi GetSdkWorldBuilder()
        {
            // Wake up the SDK window
            VRCSettings.ActiveWindowPanel = 1;
            EditorWindow.GetWindow<VRCSdkControlPanel>();
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder)) throw new Exception("Could not load VRC SDK World Builder API!");
            return builder;
        }

        public static PipelineManager FetchPipelineManager()
        {
            var pipelineManagers = FindObjectsOfType<PipelineManager>();

            if (pipelineManagers.Length < 1)
                throw new Exception("Could not find a pipeline manager! Is the current scene a valid VRC World?");

            if (pipelineManagers.Length != 1)
                throw new Exception($"Scene requires exactly one pipeline manager! (Found {pipelineManagers.Length})");

            return pipelineManagers[0];
        }

        public static void SetBuildMetadata(BuildMetadata metadata)
        {
            foreach (var metadataDisplay in FindObjectsOfType<AutoBuilderBuildMetadata>())
            {
                Log($"Applying build metadata {metadata} to Metadata Display Object {metadataDisplay.gameObject.name}");
                metadataDisplay.SetBuildMetadata($"{metadata}");
            }
        }
    }

    public struct BuiltWorld
    {
        public string blueprintId;
        public string path;
        public string signature;
    }
}