using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ManagerAgent;

/// <summary>
/// Native IL2CPP type information. The managed proxy for an FM element is often just
/// <c>VisualElement</c>; the native class tells us what it really is (SIButton, SIText, ...).
/// </summary>
internal static class Il2CppUtil
{
    private static readonly Dictionary<IntPtr, string[]> Chains = new();

    /// <summary>Native class name followed by all its base class names.</summary>
    public static string[] TypeChain(Il2CppObjectBase obj)
    {
        if (obj == null) return Array.Empty<string>();
        try
        {
            var objPtr = obj.Pointer;
            if (objPtr == IntPtr.Zero) return new[] { obj.GetType().Name };
            var klass = IL2CPP.il2cpp_object_get_class(objPtr);
            if (klass == IntPtr.Zero) return new[] { obj.GetType().Name };
            if (Chains.TryGetValue(klass, out var cached)) return cached;

            var names = new List<string>();
            var k = klass;
            for (int guard = 0; k != IntPtr.Zero && guard < 32; guard++)
            {
                var n = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(k));
                if (string.IsNullOrEmpty(n) || n == "Object") break;
                names.Add(n);
                k = IL2CPP.il2cpp_class_get_parent(k);
            }
            var arr = names.Count > 0 ? names.ToArray() : new[] { obj.GetType().Name };
            Chains[klass] = arr;
            return arr;
        }
        catch
        {
            return new[] { obj.GetType().Name };
        }
    }

    public static string TypeName(Il2CppObjectBase obj)
    {
        var chain = TypeChain(obj);
        return chain.Length > 0 ? chain[0] : "?";
    }

    public static bool IsA(Il2CppObjectBase obj, string typeName)
    {
        foreach (var n in TypeChain(obj))
            if (n == typeName) return true;
        return false;
    }

    public static long Id(Il2CppObjectBase obj) => obj == null ? 0 : obj.Pointer.ToInt64();
}

/// <summary>
/// Late-bound access to members that may not exist in every game build. Anything not
/// proven to exist in FM26's interop goes through here, so a missing member is a runtime
/// "no" instead of a compile error.
/// </summary>
internal static class Reflect
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<string, Type> TypeCache = new();
    private static MethodInfo _tryCast;

    /// <summary>
    /// A property by name, most-derived declaration first. Interop types re-declare inherited
    /// properties (e.g. <c>value</c> on every field class), so a plain GetProperty is ambiguous.
    /// </summary>
    public static PropertyInfo Prop(Type t, string member, bool writable = false)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            PropertyInfo p;
            try { p = cur.GetProperty(member, All | BindingFlags.DeclaredOnly); }
            catch (AmbiguousMatchException) { p = cur.GetProperties(All | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == member && x.GetIndexParameters().Length == 0); }
            if (p != null && p.GetIndexParameters().Length == 0 && (!writable || p.CanWrite)) return p;
        }
        return null;
    }

    private static readonly Dictionary<IntPtr, Type> ActualTypes = new();

    /// <summary>
    /// The object as its real interop class (the proxy we hold is often just VisualElement), so members
    /// declared further down — like a LongField's <c>value</c> — can be reached. Falls back to the object itself.
    /// </summary>
    public static object Actual(Il2CppObjectBase obj)
    {
        if (obj == null) return null;
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
            if (!ActualTypes.TryGetValue(klass, out var type))
            {
                type = null;
                foreach (var name in Il2CppUtil.TypeChain(obj))
                {
                    if (name.IndexOf('`') >= 0) continue;
                    var t = FindType(name);
                    if (t != null) { type = t; break; }
                }
                ActualTypes[klass] = type;
            }
            if (type == null || type.IsInstanceOfType(obj)) return obj;
            return TryCast(obj, type) ?? obj;
        }
        catch { return obj; }
    }

    public static object Get(object target, string member)
    {
        if (target == null) return null;
        try
        {
            var t = target.GetType();
            var p = Prop(t, member);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
            var f = t.GetField(member, All);
            if (f != null) return f.GetValue(target);
        }
        catch (TargetInvocationException) { }
        catch (Exception) { }
        return null;
    }

    public static bool TrySet(object target, string member, object value)
    {
        if (target == null) return false;
        try
        {
            var p = Prop(target.GetType(), member, writable: true);
            if (p != null) { p.SetValue(target, value); return true; }
            var f = target.GetType().GetField(member, All);
            if (f != null) { f.SetValue(target, value); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>Sets an enum-typed property by the enum member's name.</summary>
    public static bool TrySetEnum(object target, string member, string enumName)
    {
        if (target == null) return false;
        try
        {
            var p = target.GetType().GetProperty(member, All);
            if (p == null || !p.CanWrite || !p.PropertyType.IsEnum) return false;
            p.SetValue(target, Enum.Parse(p.PropertyType, enumName, true));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Calls the first overload of <paramref name="method"/> whose parameters accept <paramref name="args"/>.</summary>
    public static bool TryCall(object target, string method, out object result, params object[] args)
    {
        result = null;
        if (target == null) return false;
        foreach (var m in target.GetType().GetMethods(All))
        {
            if (m.Name != method || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != args.Length) continue;
            bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
                ok = args[i] == null ? !ps[i].ParameterType.IsValueType : ps[i].ParameterType.IsInstanceOfType(args[i]);
            if (!ok) continue;
            try
            {
                result = m.Invoke(target, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                AgentLog.Debug($"{method} threw: {ex.InnerException?.Message}");
                return false;
            }
        }
        return false;
    }

    public static bool TryCall(object target, string method, params object[] args) => TryCall(target, method, out _, args);

    /// <summary>Finds an interop type by simple name in any loaded assembly (cached; null if absent).</summary>
    public static Type FindType(string simpleName)
    {
        if (TypeCache.TryGetValue(simpleName, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t.Name == simpleName && typeof(Il2CppObjectBase).IsAssignableFrom(t)) { found = t; break; }
            }
            if (found != null) break;
        }
        TypeCache[simpleName] = found;
        return found;
    }

    /// <summary>Il2CppObjectBase.TryCast&lt;T&gt;() for a runtime Type.</summary>
    public static object TryCast(Il2CppObjectBase obj, Type t)
    {
        if (obj == null || t == null) return null;
        try
        {
            _tryCast ??= typeof(Il2CppObjectBase).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            return _tryCast.MakeGenericMethod(t).Invoke(obj, null);
        }
        catch { return null; }
    }
}
