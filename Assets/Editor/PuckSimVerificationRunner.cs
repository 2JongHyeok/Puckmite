using System.Collections.Generic;
using System.Text;
using Puckmite.Sim;
using UnityEditor;
using UnityEngine;

namespace Puckmite.EditorTools
{
    /// <summary>
    /// Editor entry point that runs <see cref="PuckSimVerification"/>'s headless self-checks. Prints a
    /// single PASS line when everything passes; on any failure it logs an error listing only the failed
    /// checks, so problems stand out without scrolling past the passing ones. Not a MonoBehaviour and
    /// touches no scene; runnable via Tools/PuckHero/Run Sim Verification.
    /// </summary>
    public static class PuckSimVerificationRunner
    {
        [MenuItem("Tools/PuckHero/Run Sim Verification")]
        public static void Run()
        {
            IReadOnlyList<PuckSimVerification.CheckResult> results = PuckSimVerification.RunAll();

            int passed = 0;
            foreach (PuckSimVerification.CheckResult result in results)
            {
                if (result.Passed)
                {
                    passed++;
                }
            }

            if (passed == results.Count)
            {
                Debug.Log($"[PuckHero] Sim verification: {passed}/{results.Count} PASS");
                return;
            }

            StringBuilder report = new StringBuilder();
            report.AppendLine($"[PuckHero] Sim verification: {passed}/{results.Count} PASS — failures:");
            foreach (PuckSimVerification.CheckResult result in results)
            {
                if (!result.Passed)
                {
                    report.AppendLine($"  FAIL  {result.Name}: {result.Detail}");
                }
            }

            Debug.LogError(report.ToString());
        }
    }
}
