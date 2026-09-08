using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace DebugToolbox.Runtime.Inspection;

internal sealed class ObjectRegistry
{
    public sealed class RegistryStats
    {
        public int TotalHandles { get; set; }
        public int LiveHandles { get; set; }
        public int DeadHandles { get; set; }
    }

    private sealed class HandleBox
    {
        public string Handle { get; set; }
    }

    private readonly object _lock = new();
    private readonly ConditionalWeakTable<object, HandleBox> _reverse = new();
    private readonly Dictionary<string, WeakReference> _objects = new();
    private long _nextId;
    private int _registrationsSinceCleanup;

    public string Register(object value)
    {
        if (value == null)
        {
            return null;
        }

        lock (_lock)
        {
            if (++_registrationsSinceCleanup >= 128)
            {
                CleanupLocked();
                _registrationsSinceCleanup = 0;
            }

            if (_reverse.TryGetValue(value, out var existing))
            {
                return existing.Handle;
            }

            var handle = "obj:" + Interlocked.Increment(ref _nextId);
            _reverse.Add(value, new HandleBox { Handle = handle });
            _objects[handle] = new WeakReference(value);
            return handle;
        }
    }

    public bool Release(string handle)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(handle, out var reference)) return false;
            _objects.Remove(handle);
            var target = reference.Target;
            if (target != null)
            {
                _reverse.Remove(target);
            }
            return true;
        }
    }

    public int Cleanup()
    {
        lock (_lock)
        {
            return CleanupLocked();
        }
    }

    public RegistryStats GetStats()
    {
        lock (_lock)
        {
            var live = 0;
            foreach (var reference in _objects.Values)
            {
                if (reference.Target != null) live++;
            }
            return new RegistryStats
            {
                TotalHandles = _objects.Count,
                LiveHandles = live,
                DeadHandles = _objects.Count - live
            };
        }
    }

    public object Resolve(string handle)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(handle, out var reference) || !reference.IsAlive)
            {
                throw new KeyNotFoundException($"Object handle is no longer valid: {handle}");
            }
            return reference.Target;
        }
    }

    public JObject Reference(object value)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        var result = new JObject
        {
            ["handle"] = Register(value),
            ["type"] = type.FullName
        };

        return result;
    }

    private int CleanupLocked()
    {
        var dead = new List<string>();
        foreach (var pair in _objects)
        {
            if (pair.Value.Target == null)
            {
                dead.Add(pair.Key);
            }
        }
        foreach (var handle in dead)
        {
            _objects.Remove(handle);
        }
        return dead.Count;
    }
}
