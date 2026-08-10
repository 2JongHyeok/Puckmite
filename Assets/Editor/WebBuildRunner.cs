using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Puckmite.EditorTools
{
    /// <summary>
    /// Builds the WebGL player into docs/ at the repository root, which GitHub Pages serves directly
    /// (repo Settings → Pages → branch main, folder /docs). Brotli + decompression fallback is forced
    /// because GitHub Pages sends no Content-Encoding header, so a plainly compressed build would fail
    /// to load. Idempotent: rebuilding overwrites the previous output in place.
    /// </summary>
    public static class WebBuildRunner
    {
        private const string OutputDir = "docs";

        [MenuItem("Tools/PuckHero/Build Web (docs)")]
        public static void BuildWeb()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                Debug.LogError("WebBuildRunner: Web Build Support module is not installed for this editor. Add it in Unity Hub, then retry.");
                return;
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("WebBuildRunner: no enabled scenes in Build Settings. Run Tools/PuckHero/Setup Game Scenes first.");
                return;
            }

            // The fallback makes the player decompress in JS, so the build works on any static host
            // while staying Brotli-small.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;

            BuildReport report = BuildPipeline.BuildPlayer(scenes, OutputDir, BuildTarget.WebGL, BuildOptions.None);
            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"WebBuildRunner: build ended as {summary.result} with {summary.totalErrors} error(s).");
                return;
            }

            // Keeps GitHub Pages from running the output through Jekyll (needless processing, underscore-path rules).
            File.WriteAllText(Path.Combine(OutputDir, ".nojekyll"), string.Empty);
            Debug.Log($"WebBuildRunner: build succeeded — {summary.outputPath}, {summary.totalSize / (1024f * 1024f):F1} MB.");
        }
    }
}
