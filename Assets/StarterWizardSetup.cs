using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public class StarterWizardSetup
{
    // SessionState persists across domain reloads within an Editor session.
    private const string SK_Active = "StarterWizard_Active";
    private const string SK_Phase = "StarterWizard_Phase";
    private const string SK_Reloads = "StarterWizard_Reloads";
    private const string SK_Quit = "StarterWizard_Quit";
    private const string PrefDone = "StarterWizardSetup.Done2";
    private const int MaxReloadsPerPhase = 3;
    private const double BatchTimeoutSeconds = 180;

    private static int _phase;
    private static double _phaseStart;
    private static bool _queued;
    private static int _idleFrames;
    private static bool _quitWhenDone;

    static StarterWizardSetup()
    {
        if (SessionState.GetBool(SK_Active, false))
        {
            ResumeAfterReload();
            return;
        }

        if (Application.isBatchMode) return;
        if (EditorPrefs.GetBool(PrefDone, false)) return;
        EditorApplication.update += StartWhenIdle;
    }

    private static void StartWhenIdle()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        EditorApplication.update -= StartWhenIdle;
        if (SessionState.GetBool(SK_Active, false)) return;
        if (EditorPrefs.GetBool(PrefDone, false)) return;
        RunBatchMode();
    }

    private static void ResumeAfterReload()
    {
        _quitWhenDone = SessionState.GetBool(SK_Quit, false);
        if (!Application.isBatchMode && !_quitWhenDone && EditorPrefs.GetBool(PrefDone, false))
        {
            SessionState.SetBool(SK_Active, false);
            return;
        }

        int phase = SessionState.GetInt(SK_Phase, 0);
        int reloads = SessionState.GetInt(SK_Reloads, 0) + 1;
        SessionState.SetInt(SK_Reloads, reloads);

        if (reloads > MaxReloadsPerPhase)
        {
            Debug.LogWarning("[StarterWizardSetup] " + PhaseName(phase)
                + " reloaded " + reloads + " times; moving on.");
            phase++;
            SessionState.SetInt(SK_Phase, phase);
            SessionState.SetInt(SK_Reloads, 0);
        }

        if (phase >= 2)
        {
            Finish();
            return;
        }

        Debug.Log("[StarterWizardSetup] Resuming " + PhaseName(phase)
            + " after domain reload (" + reloads + ").");
        BeginPhase(phase);
    }

    [MenuItem("Meta/Tools/Apply All Project Setup Tool Suggestions")]
    public static void AutoApplySetupSteps()
    {
        RunBatchMode();
    }

    private static void RunBatchMode()
    {
        _quitWhenDone = Application.isBatchMode;
        SessionState.SetBool(SK_Quit, _quitWhenDone);
        SessionState.SetBool(SK_Active, true);
        SessionState.SetInt(SK_Phase, 0);
        SessionState.SetInt(SK_Reloads, 0);
        BeginPhase(0);
    }

    private static void BeginPhase(int phase)
    {
        _phase = phase;
        _phaseStart = EditorApplication.timeSinceStartup;
        _queued = false;
        _idleFrames = 0;
        var target = phase == 0 ? BuildTargetGroup.Android : BuildTargetGroup.Standalone;
        Debug.Log("Applying all project setup tool suggestions: " + PhaseName(phase));
        QueueFix(target);
        EditorApplication.update -= BatchUpdate;
        EditorApplication.update += BatchUpdate;
    }

    // FixAllAsync spins on a thread-pool thread via Task.Run(WaitForCompletion).
    // A domain reload or editor exit while that thread is alive aborts the process
    // (libc++abi terminate_handler). Drive the same fixer on the main thread instead.
    private static void QueueFix(BuildTargetGroup group)
    {
        var setupType = typeof(OVRProjectSetup);
        var method = setupType.GetMethod("FixTasks", BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
            throw new MissingMethodException(setupType.FullName, "FixTasks");

        var logType = setupType.GetNestedType("LogMessages", BindingFlags.NonPublic);
        var disabled = Enum.ToObject(logType, 0);
        method.Invoke(null, new object[] { group, null, disabled, false, null, false });
        if (!QueueBusy())
        {
            var enabled = setupType.GetProperty("Enabled", BindingFlags.NonPublic | BindingFlags.Static);
            Debug.LogWarning("[StarterWizardSetup] Project setup fixer was not queued (Enabled="
                + enabled?.GetValue(null) + ").");
        }
        _queued = true;
    }

    private static bool QueueBusy()
    {
        var prop = typeof(OVRProjectSetup).GetProperty(
            "ProcessorQueue", BindingFlags.NonPublic | BindingFlags.Static);
        var queue = prop?.GetValue(null);
        if (queue == null) return false;
        var busy = queue.GetType().GetProperty("Busy", BindingFlags.Public | BindingFlags.Instance);
        return busy != null && (bool)busy.GetValue(queue);
    }

    private static void BatchUpdate()
    {
        if (!_queued) return;

        bool timedOut = (EditorApplication.timeSinceStartup - _phaseStart) > BatchTimeoutSeconds;
        if (QueueBusy())
        {
            _idleFrames = 0;
            if (timedOut)
            {
                Debug.LogWarning("[StarterWizardSetup] " + PhaseName(_phase)
                    + " timed out after " + BatchTimeoutSeconds + "s; continuing.");
                Advance();
            }
            return;
        }

        _idleFrames++;
        if (_idleFrames < 5 && !timedOut) return;
        Advance();
    }

    private static void Advance()
    {
        EditorApplication.update -= BatchUpdate;
        if (_phase == 0)
        {
            SessionState.SetInt(SK_Phase, 1);
            SessionState.SetInt(SK_Reloads, 0);
            BeginPhase(1);
            return;
        }

        Finish();
    }

    private static void Finish()
    {
        SessionState.SetBool(SK_Active, false);
        SessionState.SetInt(SK_Phase, 2);
        EditorPrefs.SetBool(PrefDone, true);
        AssetDatabase.SaveAssets();
        Debug.Log("[StarterWizardSetup] Complete.");
        if (_quitWhenDone)
            EditorApplication.update += ExitOnNextUpdate;
    }

    private static void ExitOnNextUpdate()
    {
        EditorApplication.update -= ExitOnNextUpdate;
        EditorApplication.Exit(0);
    }

    private static string PhaseName(int phase) => phase == 0 ? "Android" : "Standalone";
}
