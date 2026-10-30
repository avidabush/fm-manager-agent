using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ManagerAgent;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.manageragent.fm";
    public const string Name = "Manager Agent";
    public const string Version = "0.3.3";

    internal static Agent Agent;

    public override void Load()
    {
        AgentLog.Source = Log;
        try
        {
            if (!SelfTest.Run(out var summary))
            {
                Log.LogError("Manager Agent self-test " + summary + ". This build doesn't match your game version; " +
                             "run ManagerAgent-Setup and choose 'Repair' to build a matching one. Details: " + SelfTest.StatusFile);
                return;
            }
            Log.LogInfo("Manager Agent self-test " + summary);

            var settings = new AgentSettings(Config);
            Agent = new Agent(settings);

            // IL2CPP needs managed MonoBehaviours registered before AddComponent.
            ClassInjector.RegisterTypeInIl2Cpp<AgentHost>();
            var go = new GameObject("ManagerAgent.Host");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<AgentHost>();

            AgentLog.Info(Name + " " + Version + " loaded. Data folder: " + AgentPaths.DataDir, panel: false);
        }
        catch (Exception ex)
        {
            Log.LogError(Name + " failed to load: " + ex);
        }
    }
}

/// <summary>
/// Thin MonoBehaviour that drives the agent once per frame.
/// Kept deliberately tiny: everything with managed-only types lives in plain C# classes,
/// which avoids IL2CPP class-injection problems.
/// </summary>
public class AgentHost : MonoBehaviour
{
    public AgentHost(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        Plugin.Agent?.Tick();
    }
}
