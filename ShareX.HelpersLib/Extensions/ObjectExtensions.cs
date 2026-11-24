#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

// Source: https://github.com/Burtsev-Alexey/net-object-deep-copy

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace ShareX.HelpersLib
{
    public static class ObjectExtensions
    {
        private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Func<object, object> Clone = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>), CloneMethod);
        private static readonly ConcurrentDictionary<Type, bool> ShallowCopySafeCache = new ConcurrentDictionary<Type, bool>();
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsToCopyCache = new ConcurrentDictionary<Type, FieldInfo[]>();

        public static bool IsPrimitive(this Type type)
        {
            if (type == typeof(string)) return true;
            return type.IsValueType && type.IsPrimitive;
        }

        private static bool IsImmutable(Type type)
        {
            return type == typeof(string)
                || (type.IsValueType && (type.IsPrimitive || type.IsEnum
                    || type == typeof(decimal)
                    || type == typeof(DateTime)
                    || type == typeof(DateTimeOffset)
                    || type == typeof(TimeSpan)
                    || type == typeof(Guid)));
        }

        // True when MemberwiseClone already makes an independent copy of a field/array element of this type
        // (immutable types and value types whose fields are all shallow-copy-safe, e.g. Color/Point/Size/Rectangle).
        private static bool IsShallowCopySafe(Type type)
        {
            if (IsImmutable(type)) return true;
            if (!type.IsValueType) return false;
            return ShallowCopySafeCache.GetOrAdd(type, static t => ComputeShallowCopySafe(t, new HashSet<Type>()));
        }

        private static bool ComputeShallowCopySafe(Type type, HashSet<Type> visiting)
        {
            if (IsImmutable(type)) return true;
            if (!type.IsValueType) return false;
            if (ShallowCopySafeCache.TryGetValue(type, out bool cached)) return cached;
            if (!visiting.Add(type)) return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (!ComputeShallowCopySafe(field.FieldType, visiting))
                {
                    return false;
                }
            }

            visiting.Remove(type);
            return true;
        }

        private static FieldInfo[] GetFieldsToCopy(Type type)
        {
            return FieldsToCopyCache.GetOrAdd(type, static t =>
            {
                var fields = new List<FieldInfo>();
                CollectFields(t, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy, null, fields);
                CollectBasePrivateFields(t.BaseType, fields);
                return fields.Count == 0 ? Array.Empty<FieldInfo>() : fields.ToArray();
            });
        }

        private static void CollectBasePrivateFields(Type baseType, List<FieldInfo> fields)
        {
            if (baseType == null) return;
            CollectBasePrivateFields(baseType.BaseType, fields);
            CollectFields(baseType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, static info => info.IsPrivate, fields);
        }

        private static void CollectFields(Type type, BindingFlags bindingFlags, Func<FieldInfo, bool> filter, List<FieldInfo> fields)
        {
            foreach (FieldInfo fieldInfo in type.GetFields(bindingFlags))
            {
                if (filter != null && !filter(fieldInfo)) continue;
                if (IsShallowCopySafe(fieldInfo.FieldType)) continue;
                fields.Add(fieldInfo);
            }
        }

        public static object Copy(this object originalObject)
        {
            return InternalCopy(originalObject, new Dictionary<object, object>(new ReferenceEqualityComparer()));
        }

        private static object InternalCopy(object originalObject, IDictionary<object, object> visited)
        {
            if (originalObject == null) return null;
            Type typeToReflect = originalObject.GetType();
            if (IsImmutable(typeToReflect)) return originalObject;
            if (visited.TryGetValue(originalObject, out object existing)) return existing;
            if (typeof(Delegate).IsAssignableFrom(typeToReflect)) return null;
            object cloneObject = Clone(originalObject);
            if (typeToReflect.IsArray)
            {
                Type arrayType = typeToReflect.GetElementType();
                if (!IsShallowCopySafe(arrayType))
                {
                    Array clonedArray = (Array)cloneObject;
                    clonedArray.ForEach((array, indices) => array.SetValue(InternalCopy(clonedArray.GetValue(indices), visited), indices));
                }
            }
            visited.Add(originalObject, cloneObject);
            CopyFields(originalObject, visited, cloneObject, typeToReflect);
            return cloneObject;
        }

        private static void CopyFields(object originalObject, IDictionary<object, object> visited, object cloneObject, Type typeToReflect)
        {
            foreach (FieldInfo fieldInfo in GetFieldsToCopy(typeToReflect))
            {
                object originalFieldValue = fieldInfo.GetValue(originalObject);
                if (originalFieldValue == null) continue;
                object clonedFieldValue = InternalCopy(originalFieldValue, visited);
                fieldInfo.SetValue(cloneObject, clonedFieldValue);
            }
        }

        public static T Copy<T>(this T original)
        {
            return (T)Copy((object)original);
        }
    }

    public class ReferenceEqualityComparer : EqualityComparer<object>
    {
        public override bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public override int GetHashCode(object obj)
        {
            if (obj == null) return 0;
            return obj.GetHashCode();
        }
    }

    public static class ArrayExtensions
    {
        public static void ForEach(this Array array, Action<Array, int[]> action)
        {
            if (array.LongLength == 0) return;
            ArrayTraverse walker = new ArrayTraverse(array);
            do action(array, walker.Position);
            while (walker.Step());
        }
    }

    internal class ArrayTraverse
    {
        public int[] Position;
        private int[] maxLengths;

        public ArrayTraverse(Array array)
        {
            maxLengths = new int[array.Rank];
            for (int i = 0; i < array.Rank; ++i)
            {
                maxLengths[i] = array.GetLength(i) - 1;
            }
            Position = new int[array.Rank];
        }

        public bool Step()
        {
            for (int i = 0; i < Position.Length; ++i)
            {
                if (Position[i] < maxLengths[i])
                {
                    Position[i]++;
                    for (int j = 0; j < i; j++)
                    {
                        Position[j] = 0;
                    }
                    return true;
                }
            }
            return false;
        }
    }
}