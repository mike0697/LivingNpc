using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LivingNPC.EditorTools
{
    /// <summary>
    /// Builds the Windows game from the command line (tools/build-game.ps1):
    ///   Unity.exe -batchmode -nographics -projectPath unity/LivingNPC -executeMethod LivingNPC.EditorTools.BuildGame.Windows -out &lt;folder&gt; -quit
    /// The game goes to &lt;folder&gt;/LivingNPC.exe with the scenes enabled in the build settings. Exit code 0 when the build succeeds.
    /// The worlds are not part of the build: the script puts one in a "World" folder next to the exe.
    /// </summary>
    public static class BuildGame
    {
        [MenuItem("LivingNPC/Build Windows Game...")]
        static void BuildFromMenu()
        {
            var folder = EditorUtility.SaveFolderPanel("Build the Windows game into", "", "");
            if (!string.IsNullOrEmpty(folder)) Build(folder);
        }

        public static void Windows()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-out");
            if (i < 0 || i + 1 >= args.Length)
            {
                Debug.LogError("[LivingNPC] BuildGame.Windows needs -out <folder>");
                EditorApplication.Exit(2);
                return;
            }
            EditorApplication.Exit(Build(args[i + 1]) ? 0 : 1);
        }

        static bool Build(string folder)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(folder, PlayerSettings.productName + ".exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[LivingNPC] build {s.result}: {s.outputPath}, {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} error(s), {s.totalTime}");
            return s.result == BuildResult.Succeeded;
        }
    }
}
