
using System;
using System.Collections;
using System.Collections.Generic;
namespace Il2CppInterop.Runtime.InteropTypes
{
    public class Il2CppObjectBase
    {
        public Il2CppObjectBase(IntPtr pointer) { }
        public IntPtr Pointer => IntPtr.Zero;
        public T TryCast<T>() where T : Il2CppObjectBase => this as T;
        public T Cast<T>() where T : Il2CppObjectBase => (T)this;
    }
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public abstract class Il2CppArrayBase<T> : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, IEnumerable<T>
    {
        protected Il2CppArrayBase(IntPtr p) : base(p) { }
        public int Length => 0;
        public IEnumerator<T> GetEnumerator() { yield break; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
namespace Il2CppInterop.Runtime
{
    public static class IL2CPP
    {
        public static IntPtr il2cpp_object_get_class(IntPtr obj) => IntPtr.Zero;
        public static IntPtr il2cpp_class_get_name(IntPtr klass) => IntPtr.Zero;
        public static IntPtr il2cpp_class_get_parent(IntPtr klass) => IntPtr.Zero;
    }
}
namespace Il2CppInterop.Runtime.Injection
{
    public static class ClassInjector
    {
        public static void RegisterTypeInIl2Cpp<T>() where T : class { }
    }
}
