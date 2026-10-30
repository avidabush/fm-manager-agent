
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public Object(IntPtr pointer) : base(pointer) { }
        public Object() : base(IntPtr.Zero) { }
        public string name { get; set; }
        public static void DontDestroyOnLoad(Object target) { }
        public static Il2CppArrayBase<T> FindObjectsOfType<T>() where T : Object => null;
    }
    public sealed class GameObject : Object
    {
        public GameObject(string name) : base(IntPtr.Zero) { }
        public T AddComponent<T>() where T : Component => null;
    }
    public class Component : Object
    {
        public Component(IntPtr pointer) : base(pointer) { }
        public GameObject gameObject => null;
    }
    public class Behaviour : Component { public Behaviour(IntPtr pointer) : base(pointer) { } }
    public class MonoBehaviour : Behaviour { public MonoBehaviour(IntPtr pointer) : base(pointer) { } }
    public struct Color { public float r; public float g; public float b; public float a; }
    public struct Rect
    {
        public float x { get => 0; set { } }
        public float y { get => 0; set { } }
        public float width { get => 0; set { } }
        public float height { get => 0; set { } }
    }
    public sealed class Screen : Il2CppSystem.Object
    {
        public Screen(IntPtr pointer) : base(pointer) { }
        public static int width => 0;
        public static int height => 0;
    }
}
