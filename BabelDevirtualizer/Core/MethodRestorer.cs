using BabelDevirtualizer.Logger;
using BabelVMRestore.Logger;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SRE = System.Reflection.Emit;

namespace BabelDevirtualizer.Core
{
    public class MethodDevirtualizer : IDisposable
    {
        ModuleDefMD currentModule;
        MethodDef vmResolverMethod;
        Assembly reflectionAssembly;

        public MethodDevirtualizer(string path)
        {
            ModuleDefMD module = ModuleDefMD.Load(path);
            reflectionAssembly = Assembly.LoadFile(path);
            ConsoleLogger.Success("Devirtualizing module: {0}", module.FullName);
            currentModule = module;
        }

        public void Run()
        {
            if (!FindVMResolverMethod())
                return;
            List<EncryptedInfo> virtualizedMethods = FindVirtualizedMethods();
            if (virtualizedMethods.Count == 0)
            {
                ConsoleLogger.Error("Could not find any virtualized method!");
                return;
            }
            DevirtualizeVirtualizedMethods(virtualizedMethods);
        }

        public void Write(string outputFile)
        {
            var opts = new ModuleWriterOptions(currentModule);
            opts.MetadataOptions.Flags = MetadataFlags.PreserveAll;
            opts.Logger = new ModuleWriterLogger();
            currentModule.Write(outputFile, opts);
            if (File.Exists(outputFile))
                ConsoleLogger.Success("Output file: " + outputFile);
            else
                ConsoleLogger.Error("Output file got deleted or output file cannot be write to disk!");
        }

        bool FindVMResolverMethod()
        {
            foreach (TypeDef type in currentModule.GetTypes())
            {
                foreach (MethodDef md in type.Methods)
                {
                    if (!md.HasBody || !md.IsPrivate || md.IsStatic || md.Parameters.Count < 2 || md.Parameters[1].Type.FullName != "System.Int32"  || md.Body.ExceptionHandlers.Count == 0)
                        continue;
                    for (int i = 0; i < md.Body.Instructions.Count; i++)
                    {
                        Instruction instruction = md.Body.Instructions[i];
                        //if (instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "babel e3 {0}: {1}")
                        if (instruction.OpCode == OpCodes.Callvirt && instruction.Operand.ToString().Contains("System.Threading.ReaderWriterLock::AcquireReaderLock"))
                        {
                            vmResolverMethod = md;
                            ConsoleLogger.Info($"Found VM resolver method: {md.FullName} [0x{md.MDToken}]!");
                            return true;
                        }
                    }
                }
            }
            ConsoleLogger.Error("Could not find VM resolver method!");
            ConsoleLogger.Info("Do you khow the VM resolver method MDToken [Y/N]?");
            char c;
            while ((c = Console.ReadKey().KeyChar) != 'y' && c != 'n')
            {
                ConsoleLogger.Error("Press Y or N!");
                ConsoleLogger.Info("Do you khow the VM resolver method [Y/N]?");
            }
            string vmResolverMDToken = "";
            if (c == 'n')
                return false;
            if (c == 'y')
            {
                Console.Write("MDToken of VM resolver method: ");
                vmResolverMDToken = Console.ReadLine().Replace("0x", "");
            }
            foreach (TypeDef type in currentModule.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                    if (method.MDToken.ToString() == vmResolverMDToken)
                    {
                        vmResolverMethod = method;
                        return true;
                    }
            }
            ConsoleLogger.Error($"No method with MDToken {vmResolverMDToken}!");
            return false;
        }

        List<EncryptedInfo> FindVirtualizedMethods()
        {
            List<EncryptedInfo> virtualizedMethods = new List<EncryptedInfo>();
            foreach (TypeDef type in currentModule.GetTypes())
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (!method.HasBody || !method.Body.HasInstructions || method.Body.Instructions.Count < 7)
                        continue;
                    EncryptedInfo info = new EncryptedInfo();
                    bool isFoundVirtualizedMethod = false;
                    int keyOffset = 0;
                    for (int i = 0; i < method.Body.Instructions.Count; i++)
                    {
                        Instruction instruction = method.Body.Instructions[i];
                        MethodDef methodDef = null;
                        if (instruction.OpCode == OpCodes.Call && instruction.Operand is IMethodDefOrRef)
                        {
                            if (instruction.Operand is MethodDef)
                                methodDef = instruction.Operand as MethodDef;
                            else if (instruction.Operand is MemberRef)
                                methodDef = ((MemberRef)instruction.Operand).ResolveMethodDef();
                            if (methodDef == null || methodDef.Module != method.Module || methodDef.Parameters.Count == 4 && (methodDef.Parameters[0].Type.FullName != "System.Int32" || methodDef.Parameters[1].Type.FullName != "System.Reflection.MethodBase" || methodDef.Parameters[2].Type.FullName != "System.Object" || methodDef.Parameters[3].Type.FullName != "System.Object[]") || !methodDef.IsStatic || !methodDef.IsPublic || methodDef.ReturnType.FullName != "System.Object")
                                continue;
                            info.Method = method;
                            keyOffset = i - 4;
                            if (keyOffset < 0)
                                continue;
                            if (i >= 3 && method.Body.Instructions[i - 3].OpCode == OpCodes.Ldnull) info.hasMethodBase = false;
                            else if (i >= 8 && method.Body.Instructions[i - 3].OpCode == OpCodes.Call && method.Body.Instructions[i - 3].Operand.ToString().Contains("System.Reflection.MethodBase::GetMethodFromHandle"))
                            {
                                keyOffset = i - 9;
                                if (keyOffset < 0)
                                    continue;
                                info.hasMethodBase = true;
                                if (!type.BaseType.FullName.Contains("System.Object") && method.Body.Instructions[i - 6].OpCode == OpCodes.Ldarg_0 && method.Body.Instructions[i - 5].OpCode == OpCodes.Call && method.Body.Instructions[i - 5].Operand.ToString().Contains("System.Object::GetType"))
                                    info.hasBaseMethodBase = true;
                            }
                            isFoundVirtualizedMethod = true;
                            break;
                        }
                    }
                    if (isFoundVirtualizedMethod)
                    {
                        Instruction instuction = method.Body.Instructions[keyOffset];
                        if (instuction.OpCode == OpCodes.Ldc_I4)
                        {
                            info.Key = instuction.GetLdcI4Value();
                            if (info.Key == 1 || info.Key == 0)
                                continue;
                            ConsoleLogger.Verbose($"Virtualized method found: {method.FullName} [0x{method.MDToken}]!");
                            virtualizedMethods.Add(info);
                        }
                    }
                }
            }
            ConsoleLogger.Info($"Found {virtualizedMethods.Count} virtualized method!");
            return virtualizedMethods;
        }

        static object GetInstanceField(object instance, string fieldName)
        {
            Type type = instance.GetType();
            BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static;
            FieldInfo field = type.GetField(fieldName, bindFlags);
            return field.GetValue(instance);
        }

        void DevirtualizeVirtualizedMethods(List<EncryptedInfo> virtualizedMethods)
        {
            int changes = 0;
            string fieldName = "";
            MethodBase vmResolverMethodBase = reflectionAssembly.ManifestModule.ResolveMethod(vmResolverMethod.MDToken.ToInt32());
            ConstructorInfo vmResolverConstructor = vmResolverMethodBase.DeclaringType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (vmResolverConstructor == null)
                vmResolverConstructor = vmResolverMethodBase.DeclaringType.GetTypeInfo().DeclaredConstructors.ElementAt(0);
            object vmResolverInstance = vmResolverConstructor.Invoke(new object[] { });
            int failedMethodCount = 0;
            for (int i = 0; i < virtualizedMethods.Count; i++)
            {
                EncryptedInfo info = virtualizedMethods[i]; 
                try
                {
                    MethodBase methodBaseOfVirtualizedMethod = null;
                    if (info.hasMethodBase)
                    {
                        MethodBase methodBase = reflectionAssembly.ManifestModule.ResolveMethod(info.Method.MDToken.ToInt32());
                        if (info.hasBaseMethodBase)
                        {
                            try
                            {
                                methodBaseOfVirtualizedMethod = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, methodBase.DeclaringType.BaseType.TypeHandle);
                            }
                            catch (ArgumentException)
                            {
                                methodBaseOfVirtualizedMethod = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, methodBase.DeclaringType.TypeHandle);
                            }
                        }
                        else
                            methodBaseOfVirtualizedMethod = MethodBase.GetMethodFromHandle(methodBase.MethodHandle, methodBase.DeclaringType.TypeHandle);
                    }
                    object instanceOfDevirtualizedMethodType = vmResolverMethodBase.Invoke(vmResolverInstance, new object[] { info.Key, methodBaseOfVirtualizedMethod, null });
                    object instanceOfDynamicType = null;
                    try
                    {
                        instanceOfDynamicType = instanceOfDevirtualizedMethodType.GetType().GetTypeInfo().DeclaredFields.First((f) => f.FieldType == typeof(object)).GetValue(instanceOfDevirtualizedMethodType);
                    }
                    catch (InvalidOperationException)
                    {
                        object delegateInstance = instanceOfDevirtualizedMethodType.GetType().GetTypeInfo().DeclaredFields.First((f) => f.FieldType == typeof(Delegate)).GetValue(instanceOfDevirtualizedMethodType);
                        instanceOfDynamicType = delegateInstance.GetType().GetField("_methodBase", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(delegateInstance);
                    }
                    foreach (FieldInfo field in instanceOfDynamicType.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (field.FieldType.FullName == "System.Reflection.Emit.DynamicMethod")
                        {
                            fieldName = field.Name;
                            ConsoleLogger.Verbose("Found Dynamic Method Field in dynamic type!");
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(fieldName))
                    {
                        ConsoleLogger.Error("Could not find Dynamic Method Field Name! Trying with \\uE006!");
                        fieldName = "\uE006";
                    }
                    info.ResolvedDynamicMethod = GetInstanceField(instanceOfDynamicType, fieldName) as System.Reflection.Emit.DynamicMethod;
                    DynamicMethodReaderCustom reader = new DynamicMethodReaderCustom(currentModule, info.ResolvedDynamicMethod);
                    reader.Read();
                    info.ResolvedMethod = reader.GetMethod();
                    info.Method.Body = info.ResolvedMethod.Body;
                    changes++;
                    ConsoleLogger.Verbose($"Encrypted Method Restored: {info.Method.FullName} [0x{info.Method.MDToken}]!");
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error($"Failed to devirtualize method {info.Method.FullName} [0x{info.Method.MDToken}] with key {info.Key}!");
                    ConsoleLogger.Warning(ex.ToString());
                    failedMethodCount++;
                }
            }
            ConsoleLogger.Error($"Failed methods: {failedMethodCount}");
            ConsoleLogger.Success($"Devirtualized methods: {changes}");
        }

        public void Dispose()
        {
            vmResolverMethod = null;
            reflectionAssembly = null;
            currentModule.Dispose();
        }

        public class EncryptedInfo
        {
            public MethodDef Method;
            public int Key;
            public bool hasMethodBase;
            public bool hasBaseMethodBase;
            public SRE.DynamicMethod ResolvedDynamicMethod;
            public MethodDef ResolvedMethod;
        }
    }
}
