namespace DebugToolbox.utils;

public static unsafe class MiscUtils
{
    public static System.IntPtr GetAddress<T>(this T obj) where T : class
    {
        System.TypedReference reference = __makeref(obj);
        return *(System.IntPtr*) (&reference);
    }
}