using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using HarmonyLib;
using NeoModLoader.services;
using UnityEngine;

namespace DebugToolbox;

public class FullExceptionTracker
{
    public FullExceptionTracker()
    {
        AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
        {
            exceptions.Enqueue(args.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            exceptions.Enqueue(args.ExceptionObject.ToString());
        };
    }
    public Queue<string> exceptions = new();
}