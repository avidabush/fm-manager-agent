
using System;
namespace UnityEngine.UIElements
{
    public enum DisplayStyle { Flex, None }
    public class CallbackEventHandler : Il2CppSystem.Object { public CallbackEventHandler(IntPtr p) : base(p) { } }
    public class Focusable : CallbackEventHandler { public Focusable(IntPtr p) : base(p) { } }
    public class IStyle : Il2CppSystem.Object { public IStyle(IntPtr p) : base(p) { } }
    public class IResolvedStyle : Il2CppSystem.Object
    {
        public IResolvedStyle(IntPtr p) : base(p) { }
        public DisplayStyle display => DisplayStyle.Flex;
    }
    public class VisualElement : Focusable
    {
        public VisualElement(IntPtr p) : base(p) { }
        public VisualElement() : base(IntPtr.Zero) { }
        public string name { get; set; }
        public int childCount => 0;
        public VisualElement this[int key] => null;
        public VisualElement parent => null;
        public Rect layout => default;
        public Rect worldBound => default;
        public bool visible { get; set; }
        public bool enabledInHierarchy => true;
        public IStyle style => null;
        public IResolvedStyle resolvedStyle => null;
        public bool ClassListContains(string cls) => false;
        public void Add(VisualElement child) { }
        public void Clear() { }
        public void RemoveFromHierarchy() { }
    }
    public class TextElement : VisualElement
    {
        public TextElement(IntPtr p) : base(p) { }
        public TextElement() : base(IntPtr.Zero) { }
        public virtual string text { get; set; }
    }
    public class UIDocument : MonoBehaviour
    {
        public UIDocument(IntPtr p) : base(p) { }
        public VisualElement rootVisualElement => null;
    }
}
