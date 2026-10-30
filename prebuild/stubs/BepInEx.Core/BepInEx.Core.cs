
using System;
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class BepInPlugin : Attribute
    {
        public BepInPlugin(string GUID, string Name, string Version) { }
    }
    public static class Paths
    {
        public static string ConfigPath { get; private set; }
    }
}
namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public void LogError(object data) { }
        public void LogWarning(object data) { }
        public void LogInfo(object data) { }
    }
}
namespace BepInEx.Configuration
{
    public abstract class ConfigEntryBase { }
    public sealed class ConfigEntry<T> : ConfigEntryBase { public T Value { get; set; } }
    public class ConfigFile
    {
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) => new ConfigEntry<T> { Value = defaultValue };
    }
}
