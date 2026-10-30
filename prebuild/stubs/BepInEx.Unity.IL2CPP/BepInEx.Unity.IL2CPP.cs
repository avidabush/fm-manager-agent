
namespace BepInEx.Unity.IL2CPP
{
    public abstract class BasePlugin
    {
        protected BasePlugin() { }
        public BepInEx.Logging.ManualLogSource Log { get; }
        public BepInEx.Configuration.ConfigFile Config { get; }
        public abstract void Load();
        public virtual bool Unload() => false;
    }
}
