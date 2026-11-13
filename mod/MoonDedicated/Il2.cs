// Thin bridge to the game's IL2CPP runtime. Everything is looked up by name at
// run time (exports of GameAssembly.dll), so no game assemblies are needed to
// build the mod and nothing from the game is copied into it.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MoonDedicated
{
    internal sealed unsafe class CStr : IDisposable
    {
        public readonly byte* P;
        public CStr(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            P = (byte*)Marshal.AllocHGlobal(b.Length + 1);
            for (int i = 0; i < b.Length; i++) P[i] = b[i];
            P[b.Length] = 0;
        }
        public void Dispose() => Marshal.FreeHGlobal((IntPtr)P);
    }

    internal static unsafe class Il2
    {
        public static IntPtr Module;
        static delegate* unmanaged[Cdecl]<IntPtr> domain_get;
        static delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr*> domain_get_assemblies;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> assembly_get_image;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*> image_get_name;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr> class_from_name;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr> class_get_method_from_name;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> class_get_field_from_name;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void> field_get_value;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void> field_set_value;
        static delegate* unmanaged[Cdecl]<IntPtr, void*, void> field_static_get_value;
        static delegate* unmanaged[Cdecl]<IntPtr, void*, void> field_static_set_value;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void**, IntPtr*, IntPtr> runtime_invoke;
        static delegate* unmanaged[Cdecl]<IntPtr, void*> object_unbox;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> object_new;
        static delegate* unmanaged[Cdecl]<byte*, IntPtr> string_new;
        static delegate* unmanaged[Cdecl]<IntPtr, char*> string_chars;
        static delegate* unmanaged[Cdecl]<IntPtr, int> string_length;
        static delegate* unmanaged[Cdecl]<IntPtr, nuint> array_length;
        static delegate* unmanaged[Cdecl]<uint> array_object_header_size;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> format_exception;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> format_stack_trace;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_nested_types;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*> class_get_name;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> object_get_class;
        // Unity 6 uses pointer-sized GC handles. Truncation to uint corrupts
        // object lookups depending on the process address layout.
        static delegate* unmanaged[Cdecl]<IntPtr, byte, nuint> gchandle_new;
        static delegate* unmanaged[Cdecl]<nuint, IntPtr> gchandle_get_target;
        static delegate* unmanaged[Cdecl]<nuint, void> gchandle_free;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_methods;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_fields;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*> method_get_name;
        static delegate* unmanaged[Cdecl]<IntPtr, uint> method_get_param_count;
        static delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr> method_get_param;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*> type_get_name;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*> field_get_name;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> field_get_type;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> method_get_return_type;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint> method_get_flags;
        static delegate* unmanaged[Cdecl]<IntPtr, int> field_get_offset;
        static delegate* unmanaged[Cdecl]<IntPtr, int> field_get_flags;
        static delegate* unmanaged[Cdecl]<IntPtr> get_corlib;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_get_type;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> type_get_object;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> method_get_object;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> method_get_from_reflection;
        static delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr> array_new;
        static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr, void> gc_wbarrier_set_field;

        static IntPtr Exp(string name)
        {
            if (!NativeLibrary.TryGetExport(Module, "il2cpp_" + name, out var p)) throw new EntryPointNotFoundException("il2cpp_" + name);
            return p;
        }

        public static bool Init()
        {
            if (!NativeLibrary.TryLoad("GameAssembly", out Module) && !NativeLibrary.TryLoad("GameAssembly.dll", out Module)) return false;
            try
            {
                domain_get = (delegate* unmanaged[Cdecl]<IntPtr>)Exp("domain_get");
                domain_get_assemblies = (delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr*>)Exp("domain_get_assemblies");
                assembly_get_image = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("assembly_get_image");
                image_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Exp("image_get_name");
                class_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr>)Exp("class_from_name");
                class_get_method_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr>)Exp("class_get_method_from_name");
                class_get_field_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)Exp("class_get_field_from_name");
                field_get_value = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void>)Exp("field_get_value");
                field_set_value = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void*, void>)Exp("field_set_value");
                field_static_get_value = (delegate* unmanaged[Cdecl]<IntPtr, void*, void>)Exp("field_static_get_value");
                field_static_set_value = (delegate* unmanaged[Cdecl]<IntPtr, void*, void>)Exp("field_static_set_value");
                runtime_invoke = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void**, IntPtr*, IntPtr>)Exp("runtime_invoke");
                object_unbox = (delegate* unmanaged[Cdecl]<IntPtr, void*>)Exp("object_unbox");
                object_new = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("object_new");
                string_new = (delegate* unmanaged[Cdecl]<byte*, IntPtr>)Exp("string_new");
                string_chars = (delegate* unmanaged[Cdecl]<IntPtr, char*>)Exp("string_chars");
                string_length = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("string_length");
                array_length = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)Exp("array_length");
                array_object_header_size = (delegate* unmanaged[Cdecl]<uint>)Exp("array_object_header_size");
                format_exception = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void>)Exp("format_exception");
                format_stack_trace = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void>)Exp("format_stack_trace");
                class_get_nested_types = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)Exp("class_get_nested_types");
                class_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Exp("class_get_name");
                object_get_class = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("object_get_class");
                gchandle_new = (delegate* unmanaged[Cdecl]<IntPtr, byte, nuint>)Exp("gchandle_new");
                gchandle_get_target = (delegate* unmanaged[Cdecl]<nuint, IntPtr>)Exp("gchandle_get_target");
                gchandle_free = (delegate* unmanaged[Cdecl]<nuint, void>)Exp("gchandle_free");
                class_get_methods = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)Exp("class_get_methods");
                class_get_fields = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)Exp("class_get_fields");
                method_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Exp("method_get_name");
                method_get_param_count = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Exp("method_get_param_count");
                method_get_param = (delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr>)Exp("method_get_param");
                type_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Exp("type_get_name");
                field_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Exp("field_get_name");
                field_get_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("field_get_type");
                method_get_return_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("method_get_return_type");
                method_get_flags = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint>)Exp("method_get_flags");
                field_get_offset = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("field_get_offset");
                field_get_flags = (delegate* unmanaged[Cdecl]<IntPtr, int>)Exp("field_get_flags");
                get_corlib = (delegate* unmanaged[Cdecl]<IntPtr>)Exp("get_corlib");
                class_get_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("class_get_type");
                type_get_object = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("type_get_object");
                method_get_object = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)Exp("method_get_object");
                method_get_from_reflection = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Exp("method_get_from_reflection");
                array_new = (delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr>)Exp("array_new");
                gc_wbarrier_set_field = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr, void>)Exp("gc_wbarrier_set_field");
            }
            catch (Exception e) { Log.Error("IL2CPP export missing: " + e.Message); return false; }
            return true;
        }

        static string Utf8(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

        // ---- images / classes / methods -------------------------------------------------
        static readonly Dictionary<string, IntPtr> images = new Dictionary<string, IntPtr>();
        public static IntPtr Image(string name)
        {
            if (images.TryGetValue(name, out var cached)) return cached;
            nuint n = 0;
            var asms = domain_get_assemblies(domain_get(), &n);
            for (nuint i = 0; i < n; i++)
            {
                var img = assembly_get_image(asms[i]);
                if (Utf8(image_get_name(img)) == name) { images[name] = img; return img; }
            }
            return IntPtr.Zero;
        }

        public static IntPtr Class(IntPtr image, string ns, string name)
        {
            if (image == IntPtr.Zero) return IntPtr.Zero;
            using var a = new CStr(ns); using var b = new CStr(name);
            return class_from_name(image, a.P, b.P);
        }

        public static IntPtr Nested(IntPtr klass, string name)
        {
            IntPtr it = IntPtr.Zero, k;
            while ((k = class_get_nested_types(klass, &it)) != IntPtr.Zero)
                if (Utf8(class_get_name(k)) == name) return k;
            return IntPtr.Zero;
        }

        public static IntPtr Method(IntPtr klass, string name, int argc)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            using var n = new CStr(name);
            return class_get_method_from_name(klass, n.P, argc);
        }

        public static IntPtr Field(IntPtr klass, string name)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;
            using var n = new CStr(name);
            return class_get_field_from_name(klass, n.P);
        }

        /// Native entry point of a managed method, or zero if it has no code.
        public static IntPtr Code(IntPtr method) => method == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)method;

        public static IntPtr ClassOf(IntPtr obj) => object_get_class(obj);
        public static IntPtr ReflectionType(IntPtr klass) => type_get_object(class_get_type(klass));

        // ---- invoking ---------------------------------------------------------------------
        public static string LastError = "";

        /// Call a managed method. Value-type arguments are passed as pointers to the value, reference types as the object.
        public static IntPtr Invoke(IntPtr method, IntPtr self, params IntPtr[] args)
        {
            TryInvoke(method, self, out var result, args);
            return result;
        }

        public static bool TryInvoke(IntPtr method, IntPtr self, out IntPtr result, params IntPtr[] args)
        {
            result = IntPtr.Zero;
            LastError = "";
            if (method == IntPtr.Zero) { LastError = "missing managed method"; return false; }
            IntPtr exc = IntPtr.Zero;
            IntPtr r;
            fixed (IntPtr* a = args)
                r = runtime_invoke(method, self, (void**)a, &exc);
            if (exc != IntPtr.Zero)
            {
                byte* buf = stackalloc byte[8192];
                format_exception(exc, buf, 8192);
                LastError = Utf8(buf);
                format_stack_trace(exc, buf, 8192);
                LastError += "\n" + Utf8(buf);
                var describe = Method(ClassOf(exc), "ToString", 0);
                if (describe != IntPtr.Zero) {
                    IntPtr nested = IntPtr.Zero;
                    var detail = runtime_invoke(describe, exc, null, &nested);
                    if (nested == IntPtr.Zero && detail != IntPtr.Zero) LastError += "\n" + ReadString(detail);
                }
                Log.Warn("managed exception: " + LastError);
                return false;
            }
            result = r;
            return true;
        }

        public static bool IsStatic(IntPtr method) { uint flags = 0; return (method_get_flags(method, &flags) & 0x10) != 0; }

        public static T Value<T>(IntPtr boxed) where T : unmanaged => boxed == IntPtr.Zero ? default : *(T*)object_unbox(boxed);

        public static bool Bool(IntPtr boxed) => boxed != IntPtr.Zero && *(byte*)object_unbox(boxed) != 0;
        public static int Int(IntPtr boxed) => boxed == IntPtr.Zero ? 0 : *(int*)object_unbox(boxed);
        public static ulong ULong(IntPtr boxed) => boxed == IntPtr.Zero ? 0 : *(ulong*)object_unbox(boxed);

        public static bool CallBool(IntPtr method, IntPtr self = default) => Bool(Invoke(method, self));

        // ---- strings / objects --------------------------------------------------------
        public static IntPtr NewString(string s) { using var c = new CStr(s); return string_new(c.P); }
        public static string ReadString(IntPtr s) => s == IntPtr.Zero ? "" : new string(string_chars(s), 0, string_length(s));
        public static IntPtr New(IntPtr klass) => object_new(klass);
        public static long ArrayLength(IntPtr arr) => arr == IntPtr.Zero ? 0 : (long)array_length(arr);
        public static uint ArrayHeader() => array_object_header_size();
        public static IntPtr NewArray(IntPtr element, int length) => array_new(element, (nuint)length);
        public static IntPtr ArrayRef(IntPtr array, int index) => *((IntPtr*)((byte*)array + array_object_header_size()) + index);
        public static void ArrayRef(IntPtr array, int index, IntPtr value) => gc_wbarrier_set_field(array, (IntPtr*)((byte*)array + array_object_header_size()) + index, value);

        // ---- fields -----------------------------------------------------------------------
        public static T GetField<T>(IntPtr obj, IntPtr field) where T : unmanaged { T v = default; if (field != IntPtr.Zero) field_get_value(obj, field, &v); return v; }
        public static IntPtr GetRef(IntPtr obj, IntPtr field) { IntPtr v = IntPtr.Zero; if (field != IntPtr.Zero) field_get_value(obj, field, &v); return v; }
        public static void SetField<T>(IntPtr obj, IntPtr field, T value) where T : unmanaged { if (field != IntPtr.Zero) field_set_value(obj, field, &value); }
        public static void SetRef(IntPtr obj, IntPtr field, IntPtr value) { if (field != IntPtr.Zero) field_set_value(obj, field, (void*)value); }
        public static IntPtr GetStaticRef(IntPtr field) { IntPtr v = IntPtr.Zero; if (field != IntPtr.Zero) field_static_get_value(field, &v); return v; }
        public static void SetStatic<T>(IntPtr field, T value) where T : unmanaged { if (field != IntPtr.Zero) field_static_set_value(field, &value); }
        public static void SetStaticRef(IntPtr field, IntPtr value) { if (field != IntPtr.Zero) field_static_set_value(field, (void*)value); }

        // ---- GC handles ---------------------------------------------------------------------
        public static nuint Pin(IntPtr obj) => gchandle_new(obj, 0);
        public static IntPtr Target(nuint handle) => handle == 0 ? IntPtr.Zero : gchandle_get_target(handle);
        public static void Unpin(nuint handle) { if (handle != 0) gchandle_free(handle); }

        // ---- diagnostics: list a class's members -------------------------------------------
        public static string Describe(IntPtr klass)
        {
            var sb = new StringBuilder();
            IntPtr it = IntPtr.Zero, f;
            while ((f = class_get_fields(klass, &it)) != IntPtr.Zero)
                sb.Append("  field  ").Append(Utf8(type_get_name(field_get_type(f)))).Append(' ').Append(Utf8(field_get_name(f))).Append('\n');
            it = IntPtr.Zero;
            IntPtr m;
            while ((m = class_get_methods(klass, &it)) != IntPtr.Zero)
            {
                sb.Append("  method ").Append(Utf8(type_get_name(method_get_return_type(m)))).Append(' ').Append(Utf8(method_get_name(m))).Append('(');
                uint n = method_get_param_count(m);
                for (uint i = 0; i < n; i++) { if (i > 0) sb.Append(", "); sb.Append(Utf8(type_get_name(method_get_param(m, i)))); }
                sb.Append(")");
                var code = Code(m);
                if (code != IntPtr.Zero) sb.Append(" @0x").Append((code.ToInt64() - Module.ToInt64() + 0x180000000L).ToString("X"));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// Static field of a class by byte offset in the static block and type name (offsets seen in the game's code).
        public static IntPtr StaticFieldAt(IntPtr klass, int offset, string typeName)
        {
            IntPtr it = IntPtr.Zero, f;
            while ((f = class_get_fields(klass, &it)) != IntPtr.Zero)
                if ((field_get_flags(f) & 0x10) != 0 && field_get_offset(f) == offset && Utf8(type_get_name(field_get_type(f))) == typeName) return f;
            return IntPtr.Zero;
        }

        /// Methods of a class by exact name: used to find the right overload of generic helpers.
        public static IntPtr FindMethod(IntPtr klass, string name, Func<IntPtr, bool> pick)
        {
            IntPtr it = IntPtr.Zero, m;
            while ((m = class_get_methods(klass, &it)) != IntPtr.Zero)
                if (Utf8(method_get_name(m)) == name && pick(m)) return m;
            return IntPtr.Zero;
        }
        public static uint ParamCount(IntPtr m) => method_get_param_count(m);
        public static string ParamType(IntPtr m, uint i) => Utf8(type_get_name(method_get_param(m, i)));

        // ---- the generic-method instantiation used by the headless texture hook -------------
        public static IntPtr CorlibClass(string ns, string name) => Class(get_corlib(), ns, name);

        public static IntPtr MakeGenericSingle(IntPtr genericDefinition)
        {
            var single = CorlibClass("System", "Single");
            var type = CorlibClass("System", "Type");
            if (single == IntPtr.Zero || type == IntPtr.Zero) return IntPtr.Zero;
            var types = array_new(type, 1);
            var st = type_get_object(class_get_type(single));
            uint header = array_object_header_size();
            if (types == IntPtr.Zero || st == IntPtr.Zero || header < 16 || header > 128) return IntPtr.Zero;
            gc_wbarrier_set_field(types, (IntPtr*)((byte*)types + header), st);
            var reflection = method_get_object(genericDefinition, IntPtr.Zero);
            if (reflection == IntPtr.Zero) return IntPtr.Zero;
            var make = Method(object_get_class(reflection), "MakeGenericMethod", 1);
            var inst = Invoke(make, reflection, types);
            return inst == IntPtr.Zero ? IntPtr.Zero : method_get_from_reflection(inst);
        }
    }
}
