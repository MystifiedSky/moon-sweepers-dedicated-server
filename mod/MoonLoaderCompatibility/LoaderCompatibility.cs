using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Preloader.Core.Patching;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using System.Collections.Generic;
using System.Linq;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeSpecification = Mono.Cecil.TypeSpecification;
using GenericParameter = Mono.Cecil.GenericParameter;

namespace MoonDedicated
{
    [PatcherPluginInfo("moon.dedicated.loader", "Moon Dedicated Loader Compatibility", BuildVersion.Value)]
    public sealed unsafe class LoaderCompatibility : BasePatcher
    {
        static Delegate classInitializer;

        public override void Finalizer()
        {
            var unity = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Paths.GameRootPath, "UnityPlayer.dll"));
            if (!File.Exists(Path.Combine(Paths.GameRootPath, "MoonSweepers.exe")) || unity.FileMajorPart != 6000 || unity.FileMinorPart != 6)
            {
                Log.LogWarning("Loader compatibility requires Moon Sweepers on Unity 6000.6; leaving loader discovery unchanged");
                return;
            }
            // This bridge is for the tested Moon Sweepers loader, not a global
            // replacement for Il2CppInterop's version-dependent discovery.
            var module = NativeLibrary.Load(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            var domain = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(module, "il2cpp_domain_get");
            var open = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)NativeLibrary.GetExport(module, "il2cpp_domain_assembly_open");
            var image = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(module, "il2cpp_assembly_get_image");
            var find = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr>)NativeLibrary.GetExport(module, "il2cpp_class_from_name");
            var init = NativeLibrary.GetExport(module, "il2cpp_class_has_references");
            IntPtr klass;
            byte[] assembly = System.Text.Encoding.UTF8.GetBytes("mscorlib.dll\0"), ns = System.Text.Encoding.UTF8.GetBytes("System\0"), name = System.Text.Encoding.UTF8.GetBytes("Object\0");
            fixed (byte* a = assembly, n = ns, c = name)
                klass = find(image(open(domain(), a)), n, c);
            if (klass == IntPtr.Zero) throw new InvalidOperationException("Cannot validate Unity metadata initialization");
            // This public IL2CPP API initializes class metadata before inspecting
            // references. It avoids guessing an internal Class::Init signature.
            ((delegate* unmanaged[Cdecl]<IntPtr, byte>)init)(klass);
            var runtime = Assembly.Load("Il2CppInterop.Runtime");
            var helpers = runtime.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers", true);
            var field = helpers.GetField("ClassInit", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(helpers.FullName, "ClassInit");
            classInitializer = Marshal.GetDelegateForFunctionPointer(init, field.FieldType);
            field.SetValue(null, classInitializer);
            Log.LogInfo("Unity metadata initialization resolved through il2cpp_class_has_references");

            foreach (var nameToLoad in new[] { "UnityEngine.PhysicsCore2DModule", "UnityEngine.TimelineFoundationModule" })
            {
                var path = Path.Combine(Paths.BepInExRootPath, "interop", nameToLoad + ".dll");
                if (!File.Exists(path)) continue;
                int repaired = RepairGeneratedAssembly(path);
                if (repaired > 0) Log.LogInfo("Repaired " + repaired + " orphan metadata types in " + nameToLoad);
                var loaded = Assembly.LoadFrom(path);
                Log.LogInfo("Resolved generated Unity assembly: " + loaded.GetName().Name);
            }
        }

        internal static int OrphanCount(string path)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            int count = 0;
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & System.Reflection.TypeAttributes.VisibilityMask) > System.Reflection.TypeAttributes.Public && type.GetDeclaringType().IsNil)
                    count++;
            }
            return count;
        }

        // The patcher can emit nested TypeDefs with no enclosing NestedClass
        // row. These unreachable placeholders collide by name in .NET 6.
        // Cecil's ownership graph excludes them when reserializing the module.
        // Preserve the original and validate the replacement before swapping.
        internal static int RepairGeneratedAssembly(string path)
        {
            int count = OrphanCount(path);
            if (count == 0) return 0;
            string temporary = path + ".moon-repair-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(path))
                {
                    ValidateReferences(module);
                    module.Write(temporary);
                }
                if (OrphanCount(temporary) != 0) throw new InvalidDataException("Generated metadata repair did not remove orphan types");
                string backup = path + ".before-moon-repair-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).Substring(0, 16);
                if (!File.Exists(backup)) File.Copy(path, backup);
                File.Move(temporary, path, true);
                return count;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static void ValidateReferences(Mono.Cecil.ModuleDefinition module)
        {
            var reachable = new HashSet<TypeDefinition>(module.GetTypes());
            void Type(Mono.Cecil.TypeReference type)
            {
                if (type == null || type is GenericParameter) return;
                if (type is TypeDefinition definition && definition.Module == module && !reachable.Contains(definition))
                    throw new InvalidDataException("Cannot discard referenced orphan type " + type.FullName);
                Type(type.DeclaringType);
                if (type is TypeSpecification specification) Type(specification.ElementType);
                if (type is GenericInstanceType generic) foreach (var argument in generic.GenericArguments) Type(argument);
            }
            void Method(MethodReference method)
            {
                Type(method.DeclaringType); Type(method.ReturnType);
                foreach (var parameter in method.Parameters) Type(parameter.ParameterType);
                if (method is GenericInstanceMethod generic) foreach (var argument in generic.GenericArguments) Type(argument);
            }
            foreach (var type in reachable)
            {
                Type(type.BaseType);
                foreach (var item in type.Interfaces) Type(item.InterfaceType);
                foreach (var field in type.Fields) Type(field.FieldType);
                foreach (var property in type.Properties) Type(property.PropertyType);
                foreach (var item in type.Events) Type(item.EventType);
                foreach (var method in type.Methods)
                {
                    Method(method);
                    foreach (var parameter in method.GenericParameters)
                        foreach (var constraint in parameter.Constraints) Type(constraint.ConstraintType);
                    if (!method.HasBody) continue;
                    foreach (var variable in method.Body.Variables) Type(variable.VariableType);
                    foreach (var handler in method.Body.ExceptionHandlers) Type(handler.CatchType);
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is Mono.Cecil.TypeReference operandType) Type(operandType);
                        if (instruction.Operand is MethodReference operandMethod) Method(operandMethod);
                        if (instruction.Operand is FieldReference operandField) { Type(operandField.DeclaringType); Type(operandField.FieldType); }
                        if (instruction.Operand is CallSite site) { Type(site.ReturnType); foreach (var parameter in site.Parameters) Type(parameter.ParameterType); }
                    }
                }
                foreach (var parameter in type.GenericParameters)
                    foreach (var constraint in parameter.Constraints) Type(constraint.ConstraintType);
            }
        }
    }
}
