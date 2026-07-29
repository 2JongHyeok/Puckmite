using System.Collections.Generic;
using System.Text;
using Puckmite.Sim;
using UnityEditor;
using UnityEngine;

namespace Puckmite.EditorTools
{
    /// <summary>
    /// Editor entry point that runs <see cref="PuckSimVerification"/>'s headless self-checks and
    /// prints the report to the Console. Not a MonoBehaviour and touches no scene; it only exists so
    /// the pure-C# checks are runnable from inside Unity via Tools/Puckmite/Run Sim Verification.
    /// </summary>
    public static class PuckSimVerificationRunner
    {
        [MenuItem("Tools/Puckmite/Run Sim Verification")]
        public static void Run()
        {
            IReadOnlyList<PuckSimVerification.CheckResult> results = PuckSimVerification.RunAll();

            StringBuilder report = new StringBuilder();
            report.AppendLine("[Puckmite] PuckSim verification");
            bool allPassed = true;
            foreach (PuckSimVerification.CheckResult result in results)
            {
                report.AppendLine($"  {(result.Passed ? "PASS" : "FAIL")}  {result.Name}: {result.Detail}");
                allPassed &= result.Passed;
            }

            if (allPassed)
            {
                Debug.Log(report.ToString());
            }
            else
            {
                Debug.LogError(report.ToString());
            }
        }
    }
}
