using System;

namespace DebugToolbox.utils;

public static class PatchUtils
{
    public static string PatchID(Type type)
    {
        string base_id = $"inmny.debugtoolbox.{type.Namespace}.{type.Name}";
        foreach(Type generic_type in type.GetGenericArguments())
        {
            base_id += $"_{generic_type.Name}";
        }

        return base_id;
    }
}