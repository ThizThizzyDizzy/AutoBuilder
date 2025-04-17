using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase.Editor.Api;

namespace AutoBuilder
{
    [AttributeUsage(AttributeTargets.Method)]
    public class AutoBuilderStepAttribute : Attribute
    {
        public AutoBuilderStep step;

        public AutoBuilderStepAttribute(string name, int order, ReloadHandlingMode reloadHandlingMode = ReloadHandlingMode.Cancel, int retryLimit = 1)
        {
            step = new AutoBuilderStep(name, order, reloadHandlingMode, retryLimit);
        }
    }

    public class AutoBuilderStep
    {
        public const int ORDER_OPEN_INITIAL_SCENE = -1000;
        public const int ORDER_SET_BUILD_METADATA = 9000;
        public const int ORDER_BUILD = 10000;
        public const int ORDER_UPLOAD = 20000;
        public const int ORDER_FINISH = 50000;

        public readonly string name;
        public readonly int order;
        public readonly ReloadHandlingMode reloadHandlingMode;
        public readonly int retryLimit;
        public Func<BuildInfo, Task> run;

        public AutoBuilderStep(string name, int order, ReloadHandlingMode reloadHandlingMode, int retryLimit)
        {
            this.name = name;
            this.order = order;
            this.reloadHandlingMode = reloadHandlingMode;
            this.retryLimit = retryLimit;
        }
    }

    public enum ReloadHandlingMode
    {
        Retry,
        Continue,
        Cancel
    }

    public static class AutoBuilderStepAssembler
    {
        public static List<AutoBuilderStep> GetSteps()
        {
            var steps = new List<AutoBuilderStep>();
            var methods = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try
                    {
                        return a.GetTypes();
                    }
                    catch
                    {
                        return Type.EmptyTypes;
                    }
                })
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttributes(typeof(AutoBuilderStepAttribute), false).Length > 0);
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length > 1 || parameters.Length == 1 && !parameters[0].ParameterType.IsAssignableFrom(typeof(BuildInfo)))
                {
                    Debug.LogWarning($"[AutoBuilder] Skipping AutoBuilderStep {method.DeclaringType.FullName}.{method.Name} due to invalid parameters. Auto Builder steps must have exactly one parameter, of type {nameof(BuildInfo)}.");
                    continue;
                }

                if (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task))
                {
                    Debug.LogWarning($"[AutoBuilder] Skipping AutoBuilderStep {method.DeclaringType.FullName}.{method.Name} due to invalid return type. Auto Builder steps must return void, or Task if async.");
                }

                var attr = (AutoBuilderStepAttribute)method.GetCustomAttributes(typeof(AutoBuilderStepAttribute), false)[0];
                Func<BuildInfo, Task> callback;

                if (parameters.Length == 0)
                {
                    if (method.ReturnType == typeof(void))
                        callback = info =>
                        {
                            ((Action)Delegate.CreateDelegate(typeof(Action), method))();
                            return Task.CompletedTask;
                        };
                    else
                        callback = async (info) => await ((Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), method))();
                }
                else
                {
                    if (method.ReturnType == typeof(void))
                        callback = info =>
                        {
                            ((Action<BuildInfo>)Delegate.CreateDelegate(typeof(Action<BuildInfo>), method))(info);
                            return Task.CompletedTask;
                        };
                    else
                        callback = (Func<BuildInfo, Task>)Delegate.CreateDelegate(typeof(Func<BuildInfo, Task>), method);
                }

                attr.step.run = callback;
                steps.Add(attr.step);
            }

            steps.Sort((a, b) => a.order.CompareTo(b.order));

            return steps;
        }
    }

    public static class AutoBuilder
    {
        private static BuildInfo _buildInfo;

        public static BuildInfo BuildInfo
        {
            get
            {
                if (_buildInfo != null) return _buildInfo;
                return _buildInfo = (BuildInfo)JsonUtility.FromJson(File.ReadAllText("../build.json"), typeof(BuildInfo));
            }
        }

        [MenuItem("Tools/Auto Builder/TEST")]
        // This method is called by the Auto Builder Monitor.
        public static async void Start()
        {
            ClearEditorPrefs();
            if (BuildInfo == null) AbortBuild("Could not load build info!");

            // Print out steps for Monitor.
            var steps = AutoBuilderStepAssembler.GetSteps();
            Log("INITIALIZING - " + steps.Count + " Steps:");
            for (var s = 0; s < steps.Count; s++) Log($"- Step {s} - {steps[s].name}");

            await StartBuilder();
        }

        private static void ClearEditorPrefs()
        {
            EditorPrefs.DeleteKey("AutoBuilder_Step");
            EditorPrefs.DeleteKey("AutoBuilder_Step_Retries");
            EditorPrefs.DeleteKey("AutoBuilder_Build_Platform");
        }

        private static async Task StartBuilder(int stepNumber = 0)
        {
            try
            {
                var steps = AutoBuilderStepAssembler.GetSteps();
                if (stepNumber >= steps.Count)
                {
                    // Clean up EditorPrefs
                    ClearEditorPrefs();
                    Log("Done!");
                    EditorApplication.Exit(0);
                    return;
                }

                var step = steps[stepNumber];

                EditorPrefs.SetInt("AutoBuilder_Step", stepNumber);
                int retries;
                EditorPrefs.SetInt("AutoBuilder_Step_Retries", retries = EditorPrefs.GetInt("AutoBuilder_Step_Retries", -1) + 1);

                if (retries > step.retryLimit) throw new Exception($"Failed to run step: {step.name}! Step was killed by a unity reload. ({retries + 1} tries)");

                Log($"Running step {stepNumber}: {step.name}{(step.reloadHandlingMode == ReloadHandlingMode.Retry ? $" (Attempt {retries + 1}/{step.retryLimit + 1})" : "")}");

                await step.run(BuildInfo);

                Log($"Completed step {stepNumber}! {step.name}{(step.reloadHandlingMode == ReloadHandlingMode.Retry ? $" (Attempt {retries + 1}/{step.retryLimit + 1})" : "")}");

                EditorPrefs.SetInt("AutoBuilder_Step_Retries", -1); // Reset for next step
                await StartBuilder(stepNumber + 1);
            }
            catch (Exception ex)
            {
                AbortBuild(ex);
            }
        }

        [AutoBuilderStep("Open Initial Scene", AutoBuilderStep.ORDER_OPEN_INITIAL_SCENE)]
        private static void OpenInitialScene()
        {
            if (BuildInfo.scene != null) EditorSceneManager.OpenScene(BuildInfo.scene);
        }

        [AutoBuilderStep("Set Build Metadata", AutoBuilderStep.ORDER_SET_BUILD_METADATA)]
        private static void SetBuildMetadata()
        {
            if (BuildInfo.metadata != null) WorldBuilder.SetBuildMetadata(BuildInfo.metadata);
        }

        [AutoBuilderStep("Build", AutoBuilderStep.ORDER_BUILD, ReloadHandlingMode.Retry, 1)]
        private static async Task Build()
        {
            if (BuildInfo.build_targets == null) return; // Not building anything
            var platformIndex = EditorPrefs.GetInt("AutoBuilder_Build_Platform", 0);
            if (platformIndex >= BuildInfo.build_targets.Length) return; // Built for all platforms!
            for (int i = platformIndex; i < BuildInfo.build_targets.Length; i++)
            {
                var target = BuildInfo.build_targets[i];
                await SetBuildTarget(target);

                var world = await WorldBuilder.Build(BuildInfo.blueprint_id);
                BuildInfo.blueprint_id = world.blueprintId; // Just in case this is a new build and the id gets lost when changing platforms

                Log($"World build complete: {world.path}");
                EditorPrefs.SetString($"AutoBuilder_Build_Path_{i}", world.path);

                EditorPrefs.SetInt("AutoBuilder_Build_Platform", i + 1);
                EditorPrefs.SetInt("AutoBuilder_Step_Retries", -1); // Reset retries, since this is now switching platforms
            }
        }

        [AutoBuilderStep("Upload", AutoBuilderStep.ORDER_UPLOAD)]
        private static async Task Upload()
        {
            List<string> toUpload = new();
            if (BuildInfo.upload_after_build)
            {
                for (int i = 0; i < BuildInfo.build_targets.Length; i++)
                {
                    toUpload.Add(EditorPrefs.GetString($"AutoBuilder_Build_Path_{i}"));
                }
            }

            if (BuildInfo.upload != null) toUpload.AddRange(BuildInfo.upload);

            FieldInfo _platform = typeof(VRC.Tools).GetField("_platform", BindingFlags.NonPublic | BindingFlags.Static);
            if (_platform == null) throw new Exception("Could not set VRC upload platform!");

            foreach (string path in toUpload)
            {
                var file = new FileInfo(path);
                string platform = file.Name.Split("-", 3)[1].ToLowerInvariant().Replace("64", "");

                Log($"Changing upload platform to {platform}");

                _platform.SetValue(null, platform); // Set VRC platform to override it instead of using current platform

                Log($"Uploading world: {file.Name} for platform {VRC.Tools.Platform}");

                var signature = File.ReadAllText($"{file.DirectoryName}\\{file.Name.Substring(0, file.Name.Length - 5)}.sig");
                await WorldBuilder.Upload(path, BuildInfo.blueprint_id, signature);
            }

            _platform.SetValue(null, null); // Reset to current platform
        }

        [AutoBuilderStep("Finish", AutoBuilderStep.ORDER_FINISH, ReloadHandlingMode.Retry, retryLimit: 1)]
        private static async Task Finish()
        {
            if (BuildInfo.default_platform != null)
            {
                Log($"Returning to default platform: {BuildInfo.default_platform}");
                await SetBuildTarget(BuildInfo.default_platform.Value);
            }
        }

        [InitializeOnLoadMethod]
        private static async void AutoResume()
        {
            int currentStep = EditorPrefs.GetInt("AutoBuilder_Step", -1);
            if (currentStep < 0) return; // Not currently running.

            try
            {
                var steps = AutoBuilderStepAssembler.GetSteps();
                var step = steps[currentStep];

                switch (step.reloadHandlingMode)
                {
                    case ReloadHandlingMode.Cancel:
                        AbortBuild($"Failed to run step: {step.name}! Step was killed by a unity reload.");
                        break;
                    case ReloadHandlingMode.Continue:
                        currentStep++;
                        break;
                    case ReloadHandlingMode.Retry:
                        break;
                    default:
                        throw new Exception($"Invalid reload handling mode: {step.reloadHandlingMode}");
                }

                await StartBuilder(currentStep);
            }
            catch (Exception ex)
            {
                AbortBuild(ex);
            }
        }

        private static async Task SetBuildTarget(BuildTarget target)
        {
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Log($"Switching to build target: {target}");
                BuildTargetGroup group = target switch
                {
                    BuildTarget.StandaloneWindows64 => BuildTargetGroup.Standalone,
                    BuildTarget.Android => BuildTargetGroup.Android,
                    BuildTarget.iOS => BuildTargetGroup.iOS,
                    _ => BuildTargetGroup.Unknown
                };

                EditorUserBuildSettings.selectedBuildTargetGroup = group;
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                {
                    throw new Exception($"Could not switch to build target: {target}");
                }

                Log($"Switched to build target: {target}");

                Log("Wait for C# Compile...");
                while (EditorApplication.isCompiling) await Task.Delay(100);
            }
        }

        private static void AbortBuild(string message)
        {
            var ex = new Exception(message);
            AbortBuild(ex);
            throw ex;
        }

        private static void AbortBuild(Exception ex)
        {
            LogError($"Auto build aborted! {ex}");
            ClearEditorPrefs();
            switch (ex)
            {
                case ApiErrorException aee:
                    LogError($"API Error Details: HTTP {aee.StatusCode} - {aee.HttpMessage} ({aee.ErrorMessage}");
                    break;
            }

            EditorApplication.Exit(1);
        }

        private static void Log(string log) => Debug.Log($"[AutoBuilder] {log}");
        private static void LogWarning(string log) => Debug.LogWarning($"[AutoBuilder] {log}");
        private static void LogError(string log) => Debug.LogError($"[AutoBuilder] {log}");
    }

    [Serializable]
    public class BuildInfo
    {
        public string blueprint_id;
        public string scene;
        public BuildTarget[] build_targets;
        public bool upload_after_build;
        public string[] upload;
        public BuildMetadata metadata;
        public BuildTarget? default_platform;
    }

    [Serializable]
    public class BuildMetadata
    {
        public string version;
        public int timestamp;
        public int build_id;
        public GitMetadata[] git = new GitMetadata[0];
        public string[] extra_build_info = new string[0];

        public override string ToString()
        {
            var meta = $"{version}+{build_id}";
            if (git != null)
            {
                foreach (var g in git) meta += $".{g}";
            }

            if (extra_build_info != null)
            {
                foreach (var s in extra_build_info) meta += $".{s}";
            }

            return meta;
        }
    }

    [Serializable]
    public class GitMetadata
    {
        public string repo;
        public string head;

        public override string ToString()
        {
            return $"{repo}-{head.Substring(0, Math.Min(head.Length, 8))}";
        }
    }
}