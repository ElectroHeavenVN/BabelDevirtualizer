using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;
using dnlib.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SR = System.Reflection;

namespace BabelDevirtualizer.Core
{

    /// <summary>
    /// Reads code from a DynamicMethod. Modified from <see cref="DynamicMethodBodyReader"/>
    /// </summary>
    public class DynamicMethodReaderCustom : MethodBodyReaderBase, ISignatureReaderHelper
    {
        static readonly ReflectionFieldInfo rtdmOwnerFieldInfo = new ReflectionFieldInfo("m_owner");
        static readonly ReflectionFieldInfo dmResolverFieldInfo = new ReflectionFieldInfo("m_resolver");
        static readonly ReflectionFieldInfo rslvCodeFieldInfo = new ReflectionFieldInfo("m_code");
        static readonly ReflectionFieldInfo rslvDynamicScopeFieldInfo = new ReflectionFieldInfo("m_scope");
        static readonly ReflectionFieldInfo rslvMethodFieldInfo = new ReflectionFieldInfo("m_method");
        static readonly ReflectionFieldInfo rslvLocalsFieldInfo = new ReflectionFieldInfo("m_localSignature");
        static readonly ReflectionFieldInfo rslvMaxStackFieldInfo = new ReflectionFieldInfo("m_stackSize");
        static readonly ReflectionFieldInfo rslvExceptionsFieldInfo = new ReflectionFieldInfo("m_exceptions");
        static readonly ReflectionFieldInfo rslvExceptionHeaderFieldInfo = new ReflectionFieldInfo("m_exceptionHeader");
        static readonly ReflectionFieldInfo scopeTokensFieldInfo = new ReflectionFieldInfo("m_tokens");
        static readonly ReflectionFieldInfo gfiFieldHandleFieldInfo = new ReflectionFieldInfo("m_field", "m_fieldHandle");
        static readonly ReflectionFieldInfo gfiContextFieldInfo = new ReflectionFieldInfo("m_context");
        static readonly ReflectionFieldInfo gmiMethodHandleFieldInfo = new ReflectionFieldInfo("m_method", "m_methodHandle");
        static readonly ReflectionFieldInfo gmiContextFieldInfo = new ReflectionFieldInfo("m_context");
        static readonly ReflectionFieldInfo ehCatchAddrFieldInfo = new ReflectionFieldInfo("m_catchAddr");
        static readonly ReflectionFieldInfo ehCatchClassFieldInfo = new ReflectionFieldInfo("m_catchClass");
        static readonly ReflectionFieldInfo ehCatchEndAddrFieldInfo = new ReflectionFieldInfo("m_catchEndAddr");
        static readonly ReflectionFieldInfo ehCurrentCatchFieldInfo = new ReflectionFieldInfo("m_currentCatch");
        static readonly ReflectionFieldInfo ehTypeFieldInfo = new ReflectionFieldInfo("m_type");
        static readonly ReflectionFieldInfo ehStartAddrFieldInfo = new ReflectionFieldInfo("m_startAddr");
        static readonly ReflectionFieldInfo ehEndAddrFieldInfo = new ReflectionFieldInfo("m_endAddr");
        static readonly ReflectionFieldInfo ehEndFinallyFieldInfo = new ReflectionFieldInfo("m_endFinally");
        static readonly ReflectionFieldInfo vamMethodFieldInfo = new ReflectionFieldInfo("m_method");
        static readonly ReflectionFieldInfo vamDynamicMethodFieldInfo = new ReflectionFieldInfo("m_dynamicMethod");
        static readonly ReflectionFieldInfo methodDynamicInfo = new ReflectionFieldInfo("m_DynamicILInfo");
        ModuleDef module;
        Importer importer;
        GenericParamContext gpContext;
        MethodDef method;
        int codeSize;
        int maxStack;
        List<object> tokens;
        IList<object> ehInfos;
        byte[] ehHeader;

        class ReflectionFieldInfo
        {
            SR.FieldInfo fieldInfo;
            readonly string fieldName1;
            readonly string fieldName2;

            public ReflectionFieldInfo(string fieldName)
            {
                fieldName1 = fieldName;
            }

            public ReflectionFieldInfo(string fieldName1, string fieldName2)
            {
                this.fieldName1 = fieldName1;
                this.fieldName2 = fieldName2;
            }

            public object Read(object instance)
            {
                if (fieldInfo == null)
                    InitializeField(instance.GetType());
                if (fieldInfo == null)
                    throw new Exception(string.Format("Couldn't find field '{0}' or '{1}'", fieldName1, fieldName2));

                return fieldInfo.GetValue(instance);
            }
            public object Read(object instance, Type type)
            {
                if (fieldInfo == null)
                    InitializeField(type);
                if (fieldInfo == null)
                    throw new Exception(string.Format("Couldn't find field '{0}' or '{1}'", fieldName1, fieldName2));

                return fieldInfo.GetValue(instance);
            }
            public bool Exists(object instance)
            {
                InitializeField(instance.GetType());
                return fieldInfo != null;
            }

            void InitializeField(Type type)
            {
                if (fieldInfo != null)
                    return;

                BindingFlags flags = SR.BindingFlags.Instance | SR.BindingFlags.Public | SR.BindingFlags.NonPublic;
                fieldInfo = type.GetField(fieldName1, flags);
                if (fieldInfo == null && fieldName2 != null)
                    fieldInfo = type.GetField(fieldName2, flags);
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="module">Module that will own the method body</param>
        /// <param name="obj">This can be one of several supported types: the delegate instance
        /// created by DynamicMethod.CreateDelegate(), a DynamicMethod instance, a RTDynamicMethod
        /// instance or a DynamicResolver instance.</param>
        public DynamicMethodReaderCustom(ModuleDef module, object obj)
            : this(module, obj, new GenericParamContext())
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="module">Module that will own the method body</param>
        /// <param name="obj">This can be one of several supported types: the delegate instance
        /// created by DynamicMethod.CreateDelegate(), a DynamicMethod instance, a RTDynamicMethod
        /// instance or a DynamicResolver instance.</param>
        /// <param name="gpContext">Generic parameter context</param>
        public DynamicMethodReaderCustom(ModuleDef module, object obj, GenericParamContext gpContext)
        {
            this.module = module;
            importer = new Importer(module, ImporterOptions.TryToUseDefs, gpContext);
            this.gpContext = gpContext;
            if (obj == null)
                throw new ArgumentNullException("obj");
            if (obj is Delegate del)
            {
                obj = del.Method;
                if (obj == null)
                    throw new Exception("Delegate.Method == null");
            }
            if (obj.GetType().ToString() == "System.Reflection.Emit.DynamicMethod+RTDynamicMethod")
            {
                obj = rtdmOwnerFieldInfo.Read(obj) as DynamicMethod;
                if (obj == null)
                    throw new Exception("RTDynamicMethod.m_owner is null or invalid");
            }
            if (obj is DynamicMethod)
            {
                object obj2 = obj;
                obj = dmResolverFieldInfo.Read(obj);
                if (obj == null)
                {
                    obj = obj2;
                    obj = methodDynamicInfo.Read(obj);
                    if (obj == null)
                        throw new Exception("No resolver found");
                    SecondOption(obj);
                    return;
                }
            }
            if (obj.GetType().ToString() != "System.Reflection.Emit.DynamicResolver")
                throw new Exception("Couldn't find DynamicResolver");
            byte[] code = rslvCodeFieldInfo.Read(obj) as byte[];
            if (code == null)
                throw new Exception("No code");
            codeSize = code.Length;
            MethodBase delMethod = rslvMethodFieldInfo.Read(obj) as SR.MethodBase;
            if (delMethod == null)
                throw new Exception("No method");
            maxStack = (int)rslvMaxStackFieldInfo.Read(obj);
            object scope = rslvDynamicScopeFieldInfo.Read(obj);
            if (scope == null)
                throw new Exception("No scope");
            System.Collections.IList tokensList = scopeTokensFieldInfo.Read(scope) as System.Collections.IList;
            if (tokensList == null)
                throw new Exception("No tokens");
            tokens = new List<object>(tokensList.Count);
            for (int i = 0; i < tokensList.Count; i++)
                tokens.Add(tokensList[i]);
            ehInfos = (IList<object>)rslvExceptionsFieldInfo.Read(obj);
            ehHeader = rslvExceptionHeaderFieldInfo.Read(obj) as byte[];
            UpdateLocals(rslvLocalsFieldInfo.Read(obj) as byte[]);
            reader = ByteArrayDataReaderFactory.CreateReader(code);
            method = CreateMethodDef(delMethod);
            parameters = method.Parameters;
        }
        public static T GetFieldValue<T>(object obj, string fieldName)
        {
            if (obj == null)
                throw new ArgumentNullException("obj");
            FieldInfo field = obj.GetType().GetField(fieldName, SR.BindingFlags.Public |
                                                          SR.BindingFlags.NonPublic |
                                                          SR.BindingFlags.Instance);
            if (field == null)
                throw new ArgumentException("fieldName", "No such field was found.");
            if (!typeof(T).IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException("Field type and requested type are not compatible.");
            return (T)field.GetValue(obj);
        }
        void SecondOption(object obj)
        {
            byte[] code = GetFieldValue<byte[]>(obj, "m_code");
            if (code == null)
                throw new Exception("No code");
            codeSize = code.Length;
            MethodBase delMethod = GetFieldValue<SR.MethodBase>(obj, "m_method");
            if (delMethod == null)
                throw new Exception("No method");
            maxStack = GetFieldValue<int>(obj, "m_maxStackSize");
            object scope = GetFieldValue<object>(obj, "m_scope");
            if (scope == null)
                throw new Exception("No scope");
            System.Collections.IList tokensList = GetFieldValue<System.Collections.IList>(scope, "m_tokens");
            if (tokensList == null)
                throw new Exception("No tokens");
            tokens = new List<object>(tokensList.Count);
            for (int i = 0; i < tokensList.Count; i++)
                tokens.Add(tokensList[i]);
            //ehInfos = (IList<object>)rslvExceptionsFieldInfo.Read(obj);
            ehHeader = GetFieldValue<byte[]>(obj, "m_exceptions");
            UpdateLocals(GetFieldValue<byte[]>(obj, "m_localSignature"));
            reader = ByteArrayDataReaderFactory.CreateReader(code);
            method = CreateMethodDef(delMethod);
            parameters = method.Parameters;
            return;
        }
        class ExceptionInfo
        {
            public int[] CatchAddr;
            public Type[] CatchClass;
            public int[] CatchEndAddr;
            public int CurrentCatch;
            public int[] Type;
            public int StartAddr;
            public int EndAddr;
            public int EndFinally;
        }

        static List<ExceptionInfo> CreateExceptionInfos(IList<object> ehInfos)
        {
            if (ehInfos == null)
                return new List<ExceptionInfo>();
            List<ExceptionInfo> infos = new List<ExceptionInfo>(ehInfos.Count);
            foreach (object ehInfo in ehInfos)
            {
                infos.Add(new ExceptionInfo
                {
                    CatchAddr = (int[])ehCatchAddrFieldInfo.Read(ehInfo),
                    CatchClass = (Type[])ehCatchClassFieldInfo.Read(ehInfo),
                    CatchEndAddr = (int[])ehCatchEndAddrFieldInfo.Read(ehInfo),
                    CurrentCatch = (int)ehCurrentCatchFieldInfo.Read(ehInfo),
                    Type = (int[])ehTypeFieldInfo.Read(ehInfo),
                    StartAddr = (int)ehStartAddrFieldInfo.Read(ehInfo),
                    EndAddr = (int)ehEndAddrFieldInfo.Read(ehInfo),
                    EndFinally = (int)ehEndFinallyFieldInfo.Read(ehInfo),
                });
            }
            return infos;
        }

        void UpdateLocals(byte[] localsSig)
        {
            if (localsSig == null || localsSig.Length == 0)
                return;
            LocalSig sig = SignatureReader.ReadSig(this, module.CorLibTypes, localsSig, gpContext) as LocalSig;
            if (sig == null)
                return;
            foreach (TypeSig local in sig.Locals)
                locals.Add(new Local(local));
        }

        MethodDef CreateMethodDef(SR.MethodBase delMethod)
        {
            bool isStatic = true;
            MethodDefUser method = new MethodDefUser();
            TypeSig retType = GetReturnType(delMethod);
            List<TypeSig> pms = GetParameters(delMethod);
            if (isStatic)
                method.Signature = MethodSig.CreateStatic(retType, pms.ToArray());
            else
                method.Signature = MethodSig.CreateInstance(retType, pms.ToArray());
            method.Parameters.UpdateParameterTypes();
            method.ImplAttributes = dnlib.DotNet.MethodImplAttributes.IL;
            method.Attributes = dnlib.DotNet.MethodAttributes.PrivateScope;
            if (isStatic)
                method.Attributes |= dnlib.DotNet.MethodAttributes.Static;
            return module.UpdateRowId(method);
        }

        TypeSig GetReturnType(SR.MethodBase mb)
        {
            MethodInfo mi = mb as SR.MethodInfo;
            if (mi != null)
                return importer.ImportAsTypeSig(mi.ReturnType);
            return module.CorLibTypes.Void;
        }

        List<TypeSig> GetParameters(SR.MethodBase delMethod)
        {
            List<TypeSig> pms = new List<TypeSig>();
            foreach (ParameterInfo param in delMethod.GetParameters())
                pms.Add(importer.ImportAsTypeSig(param.ParameterType));
            return pms;
        }

        /// <summary>
        /// Reads the code
        /// </summary>
        /// <returns></returns>
        public bool Read()
        {
            ReadInstructionsNumBytes((uint)codeSize);
            CreateExceptionHandlers();
            return true;
        }

        void CreateExceptionHandlers()
        {
            if (ehHeader != null && ehHeader.Length != 0)
            {
                BinaryReader reader = new BinaryReader(new MemoryStream(ehHeader));
                byte b = reader.ReadByte();
                if ((b & 0x40) == 0)
                { 
                    // DynamicResolver only checks bit 6
                    // Calculate num ehs exactly the same way that DynamicResolver does
                    int numHandlers = (ushort)((reader.ReadByte() - 2) / 12);
                    reader.ReadInt16();
                    for (int i = 0; i < numHandlers; i++)
                    {
                        dnlib.DotNet.Emit.ExceptionHandler eh = new dnlib.DotNet.Emit.ExceptionHandler();
                        eh.HandlerType = (ExceptionHandlerType)reader.ReadInt16();
                        int offs = reader.ReadUInt16();
                        eh.TryStart = GetInstructionThrow((uint)offs);
                        eh.TryEnd = GetInstruction((uint)(reader.ReadSByte() + offs));
                        offs = reader.ReadUInt16();
                        eh.HandlerStart = GetInstructionThrow((uint)offs);
                        eh.HandlerEnd = GetInstruction((uint)(reader.ReadSByte() + offs));
                        if (eh.HandlerType == ExceptionHandlerType.Catch)
                            eh.CatchType = ReadToken(reader.ReadUInt32()) as ITypeDefOrRef;
                        else if (eh.HandlerType == ExceptionHandlerType.Filter)
                            eh.FilterStart = GetInstruction(reader.ReadUInt32());
                        else
                            reader.ReadUInt32();
                        exceptionHandlers.Add(eh);
                    }
                }
                else
                {
                    reader.BaseStream.Position--;
                    int numHandlers = (ushort)(((reader.ReadUInt32() >> 8) - 4) / 24);
                    for (int i = 0; i < numHandlers; i++)
                    {
                        dnlib.DotNet.Emit.ExceptionHandler eh = new dnlib.DotNet.Emit.ExceptionHandler();
                        eh.HandlerType = (ExceptionHandlerType)reader.ReadInt32();
                        int offs = reader.ReadInt32();
                        eh.TryStart = GetInstructionThrow((uint)offs);
                        eh.TryEnd = GetInstruction((uint)(reader.ReadInt32() + offs));
                        offs = reader.ReadInt32();
                        eh.HandlerStart = GetInstructionThrow((uint)offs);
                        eh.HandlerEnd = GetInstruction((uint)(reader.ReadInt32() + offs));
                        if (eh.HandlerType == ExceptionHandlerType.Catch)
                            eh.CatchType = ReadToken(reader.ReadUInt32()) as ITypeDefOrRef;
                        else if (eh.HandlerType == ExceptionHandlerType.Filter)
                            eh.FilterStart = GetInstruction(reader.ReadUInt32());
                        else
                            reader.ReadUInt32();
                        exceptionHandlers.Add(eh);
                    }
                }
            }
            else if (ehInfos != null)
            {
                foreach (ExceptionInfo ehInfo in CreateExceptionInfos(ehInfos))
                {
                    Instruction tryStart = GetInstructionThrow((uint)ehInfo.StartAddr);
                    Instruction tryEnd = GetInstruction((uint)ehInfo.EndAddr);
                    Instruction endFinally = ehInfo.EndFinally < 0 ? null : GetInstruction((uint)ehInfo.EndFinally);
                    for (int i = 0; i < ehInfo.CurrentCatch; i++)
                    {
                        dnlib.DotNet.Emit.ExceptionHandler eh = new dnlib.DotNet.Emit.ExceptionHandler();
                        eh.HandlerType = (ExceptionHandlerType)ehInfo.Type[i];
                        eh.TryStart = tryStart;
                        eh.TryEnd = eh.HandlerType == ExceptionHandlerType.Finally ? endFinally : tryEnd;
                        eh.FilterStart = null;	// not supported by DynamicMethod.ILGenerator
                        eh.HandlerStart = GetInstructionThrow((uint)ehInfo.CatchAddr[i]);
                        eh.HandlerEnd = GetInstruction((uint)ehInfo.CatchEndAddr[i]);
                        eh.CatchType = importer.Import(ehInfo.CatchClass[i]);
                        exceptionHandlers.Add(eh);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the created method. Must be called after <see cref="Read()"/>.
        /// </summary>
        /// <returns>A new <see cref="CilBody"/> instance</returns>
        public MethodDef GetMethod()
        {
            bool initLocals = true;
            CilBody cilBody = new CilBody(initLocals, instructions, exceptionHandlers, locals);
            cilBody.MaxStack = (ushort)Math.Min(maxStack, ushort.MaxValue);
            instructions = null;
            exceptionHandlers = null;
            locals = null;
            method.Body = cilBody;
            return method;
        }

        /// <inheritdoc/>
        protected override IField ReadInlineField(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as IField;
        }

        /// <inheritdoc/>
        protected override IMethod ReadInlineMethod(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as IMethod;
        }

        /// <inheritdoc/>
        protected override MethodSig ReadInlineSig(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as MethodSig;
        }

        /// <inheritdoc/>
        protected override string ReadInlineString(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as string ?? string.Empty;
        }

        /// <inheritdoc/>
        protected override ITokenOperand ReadInlineTok(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as ITokenOperand;
        }

        /// <inheritdoc/>
        protected override ITypeDefOrRef ReadInlineType(Instruction instr)
        {
            return ReadToken(reader.ReadUInt32()) as ITypeDefOrRef;
        }

        object ReadToken(uint token)
        {
            uint rid = token & 0x00FFFFFF;
            switch (token >> 24)
            {
                case 0x02:
                    return ImportType(rid);
                case 0x04:
                    return ImportField(rid);
                case 0x06:
                case 0x0A:
                    return ImportMethod(rid);
                case 0x11:
                    return ImportSignature(rid);
                case 0x70:
                    return Resolve(rid) as string;
                default:
                    return null;
            }
        }

        IMethod ImportMethod(uint rid)
        {
            object obj = Resolve(rid);
            if (obj == null)
                return null;
            if (obj is RuntimeMethodHandle)
                return importer.Import(SR.MethodBase.GetMethodFromHandle((RuntimeMethodHandle)obj));
            if (obj.GetType().ToString() == "System.Reflection.Emit.GenericMethodInfo")
            {
                RuntimeTypeHandle context = (RuntimeTypeHandle)gmiContextFieldInfo.Read(obj);
                MethodBase method = SR.MethodBase.GetMethodFromHandle((RuntimeMethodHandle)gmiMethodHandleFieldInfo.Read(obj), context);
                return importer.Import(method);
            }
            if (obj.GetType().ToString() == "System.Reflection.Emit.VarArgMethod")
            {
                MethodInfo method = GetVarArgMethod(obj);
                if (!(method is DynamicMethod))
                    return importer.Import(method);
                obj = method;
            }
            DynamicMethod dm = obj as DynamicMethod;
            if (dm != null)
                throw new Exception("DynamicMethod calls another DynamicMethod");
            return null;
        }

        SR.MethodInfo GetVarArgMethod(object obj)
        {
            if (vamDynamicMethodFieldInfo.Exists(obj))
            {
                // .NET 4.0+
                MethodInfo method = vamMethodFieldInfo.Read(obj) as SR.MethodInfo;
                DynamicMethod dynMethod = vamDynamicMethodFieldInfo.Read(obj) as DynamicMethod;
                return dynMethod ?? method;
            }
            else
            {
                // .NET 2.0
                // This is either a DynamicMethod or a MethodInfo
                return vamMethodFieldInfo.Read(obj) as SR.MethodInfo;
            }
        }

        IField ImportField(uint rid)
        {
            object obj = Resolve(rid);
            if (obj == null)
                return null;

            if (obj is RuntimeFieldHandle)
                return importer.Import(SR.FieldInfo.GetFieldFromHandle((RuntimeFieldHandle)obj));

            if (obj.GetType().ToString() == "System.Reflection.Emit.GenericFieldInfo")
            {
                RuntimeTypeHandle context = (RuntimeTypeHandle)gfiContextFieldInfo.Read(obj);
                FieldInfo field = SR.FieldInfo.GetFieldFromHandle((RuntimeFieldHandle)gfiFieldHandleFieldInfo.Read(obj), context);
                return importer.Import(field);
            }

            return null;
        }

        ITypeDefOrRef ImportType(uint rid)
        {
            object obj = Resolve(rid);
            if (obj is RuntimeTypeHandle)
                return importer.Import(Type.GetTypeFromHandle((RuntimeTypeHandle)obj));

            return null;
        }

        CallingConventionSig ImportSignature(uint rid)
        {
            byte[] sig = Resolve(rid) as byte[];
            if (sig == null)
                return null;

            return SignatureReader.ReadSig(this, module.CorLibTypes, sig, gpContext);
        }

        object Resolve(uint index)
        {
            if (index >= (uint)tokens.Count)
                return null;
            return tokens[(int)index];
        }

        ITypeDefOrRef ISignatureReaderHelper.ResolveTypeDefOrRef(uint codedToken, GenericParamContext gpContext)
        {
            uint token;
            if (!CodedToken.TypeDefOrRef.Decode(codedToken, out token))
                return null;
            uint rid = MDToken.ToRID(token);
            switch (MDToken.ToTable(token))
            {
                case Table.TypeDef:
                case Table.TypeRef:
                case Table.TypeSpec:
                    return ImportType(rid);
            }
            return null;
        }

        TypeSig ISignatureReaderHelper.ConvertRTInternalAddress(IntPtr address)
        {
            MethodBase methodBase = typeof(ModuleDef).Module.GetTypes().First((t) => t.Name == "MethodTableToTypeConverter").GetMethod("Convert", SR.BindingFlags.Public | SR.BindingFlags.Static | SR.BindingFlags.InvokeMethod);
            return importer.ImportAsTypeSig((Type)methodBase.Invoke(null, new object[] { address }));
        }
    }
}
