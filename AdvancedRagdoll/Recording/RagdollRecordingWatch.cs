// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CombatSimulator.Animation.Recording;

/// <summary>
/// Notices when the value of a property changes. The recorder asks once per render frame for every
/// configuration property the ragdoll reads, so asking must cost nothing while the value stays the
/// same: the getters are bound to delegates once, values are compared in their own type (no boxing),
/// a list of strings is compared item by item against a kept copy, and a settings object is compared
/// property by property. Only a property of a type not covered here falls back to reflection.
///
/// A watch remembers the last value it saw. <see cref="Clone"/> gives a watch for another observer
/// that shares the bound getters and remembers nothing yet.
/// </summary>
public abstract class RagdollRecordingWatch
{
    private const int MaxDepth = 4;

    /// <summary>True when the value differs from the one remembered (always true the first time);
    /// the new value is remembered. <paramref name="owner"/> is the object that has the property.</summary>
    public abstract bool Changed(object owner);

    public abstract RagdollRecordingWatch Clone();

    /// <summary>False for the reflection fallback, which allocates on every look.</summary>
    public virtual bool AllocationFree => true;

    public static RagdollRecordingWatch For(PropertyInfo property) => Build(property, 0);

    private static RagdollRecordingWatch Build(PropertyInfo property, int depth)
    {
        try
        {
            var getter = property.GetMethod;
            var owner = property.DeclaringType;
            var type = property.PropertyType;
            if (getter == null || getter.IsStatic || owner == null || owner.IsValueType ||
                property.GetIndexParameters().Length != 0)
                return new Fallback(property);

            if (IsLeaf(type))
                return (RagdollRecordingWatch)Activator.CreateInstance(
                    typeof(Leaf<,>).MakeGenericType(owner, type), getter)!;
            if (typeof(IReadOnlyList<string>).IsAssignableFrom(type))
                return (RagdollRecordingWatch)Activator.CreateInstance(
                    typeof(StringList<>).MakeGenericType(owner), getter)!;
            if (type.IsClass && !typeof(IEnumerable).IsAssignableFrom(type) && depth < MaxDepth)
            {
                var children = new List<RagdollRecordingWatch>();
                foreach (var child in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (child.GetMethod != null && child.GetIndexParameters().Length == 0)
                        children.Add(Build(child, depth + 1));
                return (RagdollRecordingWatch)Activator.CreateInstance(
                    typeof(Members<>).MakeGenericType(owner), getter, children.ToArray())!;
            }
        }
        catch
        {
            // A getter that cannot be bound: watch it the slow way.
        }
        return new Fallback(property);
    }

    private static bool IsLeaf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
    }

    private sealed class Leaf<TOwner, T> : RagdollRecordingWatch where TOwner : class
    {
        private readonly Func<TOwner, T> get;
        private T last = default!;
        private bool known;

        public Leaf(MethodInfo getter) => get = getter.CreateDelegate<Func<TOwner, T>>();
        private Leaf(Func<TOwner, T> get) => this.get = get;

        public override bool Changed(object owner)
        {
            var now = get((TOwner)owner);
            if (known && EqualityComparer<T>.Default.Equals(now, last)) return false;
            last = now;
            known = true;
            return true;
        }

        public override RagdollRecordingWatch Clone() => new Leaf<TOwner, T>(get);
    }

    private sealed class StringList<TOwner> : RagdollRecordingWatch where TOwner : class
    {
        private readonly Func<TOwner, IReadOnlyList<string>?> get;
        private string?[] last = Array.Empty<string?>();
        private int lastCount = -2; // -2 nothing seen yet, -1 null

        public StringList(MethodInfo getter) => get = getter.CreateDelegate<Func<TOwner, IReadOnlyList<string>?>>();
        private StringList(Func<TOwner, IReadOnlyList<string>?> get) => this.get = get;

        public override bool Changed(object owner)
        {
            var now = get((TOwner)owner);
            var count = now?.Count ?? -1;
            var same = count == lastCount;
            for (var i = 0; same && i < count; i++)
                same = string.Equals(now![i], last[i], StringComparison.Ordinal);
            if (same) return false;

            lastCount = count;
            if (count > last.Length) last = new string?[count];
            for (var i = 0; i < count; i++) last[i] = now![i];
            return true;
        }

        public override RagdollRecordingWatch Clone() => new StringList<TOwner>(get);
    }

    /// <summary>A settings object: changed when it appears, disappears, or any of its readable properties changes.</summary>
    private sealed class Members<TOwner> : RagdollRecordingWatch where TOwner : class
    {
        private readonly Func<TOwner, object?> get;
        private readonly RagdollRecordingWatch[] members;
        private bool known;
        private bool lastNull;

        public Members(MethodInfo getter, RagdollRecordingWatch[] members)
        {
            get = getter.CreateDelegate<Func<TOwner, object?>>();
            this.members = members;
        }

        private Members(Func<TOwner, object?> get, RagdollRecordingWatch[] members)
        {
            this.get = get;
            this.members = members;
        }

        public override bool AllocationFree
        {
            get
            {
                foreach (var member in members)
                    if (!member.AllocationFree) return false;
                return true;
            }
        }

        public override bool Changed(object owner)
        {
            var now = get((TOwner)owner);
            var changed = !known || (now == null) != lastNull;
            known = true;
            lastNull = now == null;
            if (now == null) return changed;
            // Every member is looked at, so each remembers its current value.
            foreach (var member in members)
                changed |= member.Changed(now);
            return changed;
        }

        public override RagdollRecordingWatch Clone()
        {
            var copies = new RagdollRecordingWatch[members.Length];
            for (var i = 0; i < copies.Length; i++) copies[i] = members[i].Clone();
            return new Members<TOwner>(get, copies);
        }
    }

    /// <summary>Any other type: read through reflection and compared as text. Allocates on every look.</summary>
    private sealed class Fallback : RagdollRecordingWatch
    {
        private readonly PropertyInfo property;
        private string? last;
        private bool known;

        public Fallback(PropertyInfo property) => this.property = property;

        public override bool AllocationFree => false;

        public override bool Changed(object owner)
        {
            string? now;
            try
            {
                var value = property.GetValue(owner);
                now = value == null ? null : System.Text.Json.JsonSerializer.Serialize(value, value.GetType());
            }
            catch (Exception ex)
            {
                now = "<unreadable: " + ex.GetType().Name + ">";
            }
            if (known && string.Equals(now, last, StringComparison.Ordinal)) return false;
            last = now;
            known = true;
            return true;
        }

        public override RagdollRecordingWatch Clone() => new Fallback(property);
    }
}
