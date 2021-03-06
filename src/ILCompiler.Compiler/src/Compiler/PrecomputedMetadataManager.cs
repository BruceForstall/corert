﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Immutable;

using Internal.Compiler;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using ILCompiler.Metadata;
using ILCompiler.DependencyAnalysis;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    class PrecomputedMetadataManager : MetadataManager
    {
        private const string MetadataMappingTypeName = "_$ILCT$+$ILT$ReflectionMapping$";

        class MetadataLoadedInfo
        {
            public ImmutableArray<ModuleDesc> MetadataModules = ImmutableArray<ModuleDesc>.Empty;
            public ImmutableArray<ModuleDesc> LocalMetadataModules = ImmutableArray<ModuleDesc>.Empty;
            public ImmutableArray<ModuleDesc> ExternalMetadataModules = ImmutableArray<ModuleDesc>.Empty;
            public List<MetadataMapping<MetadataType>> StrongTypeMappings = new List<MetadataMapping<MetadataType>>();
            public List<MetadataMapping<MetadataType>> AllTypeMappings = new List<MetadataMapping<MetadataType>>();
            public List<MetadataMapping<MethodDesc>> MethodMappings = new List<MetadataMapping<MethodDesc>>();
            public List<MetadataMapping<FieldDesc>> FieldMappings = new List<MetadataMapping<FieldDesc>>();
            public HashSet<MetadataType> ReflectionBlockedTypes = new HashSet<MetadataType>();
        }

        private readonly ModuleDesc _metadataDescribingModule;
        private readonly HashSet<ModuleDesc> _compilationModules;
        private readonly Lazy<MetadataLoadedInfo> _loadedMetadata;
        private Lazy<Dictionary<MethodDesc, MethodDesc>> _dynamicInvokeStubs;
        private readonly byte[] _metadataBlob;

        public PrecomputedMetadataManager(NodeFactory factory, ModuleDesc metadataDescribingModule, IEnumerable<ModuleDesc> compilationModules, byte[] metadataBlob) : base(factory)
        {
            _metadataDescribingModule = metadataDescribingModule;
            _compilationModules = new HashSet<ModuleDesc>(compilationModules);
            _loadedMetadata = new Lazy<MetadataLoadedInfo>(LoadMetadata);
            _dynamicInvokeStubs = new Lazy<Dictionary<MethodDesc, MethodDesc>>(LoadDynamicInvokeStubs);
            _metadataBlob = metadataBlob;
        }

        /// <summary>
        /// Read a method that describes the type system to metadata mappings.
        /// </summary>
        private void ReadMetadataMethod(MethodIL methodOfMappings, out List<MetadataMapping<MetadataType>> typeMappings, out List<MetadataMapping<MethodDesc>> methodMappings, out List<MetadataMapping<FieldDesc>> fieldMappings, ref HashSet<ModuleDesc> metadataModules)
        {
            typeMappings = new List<MetadataMapping<MetadataType>>();
            methodMappings = new List<MetadataMapping<MethodDesc>>();
            fieldMappings = new List<MetadataMapping<FieldDesc>>();

            ILStreamReader il = new ILStreamReader(methodOfMappings);
            // structure is 
            // REPEAT N TIMES    
            // ldtoken type/method/field OR ldc.i4 0. If ldtoken instruction is replaced with a ldc.i4, skip this entry
            // ldc.i4 metadata value
            // pop
            // pop
            while (true)
            {
                if (il.TryReadRet()) // ret
                    break;

                TypeSystemEntity tse;
                if (il.TryReadLdtokenAsTypeSystemEntity(out tse))
                {
                    int metadataTokenValue = il.ReadLdcI4();
                    il.ReadPop();
                    il.ReadPop();

                    if (tse is MetadataType)
                    {
                        MetadataType type = (MetadataType)tse;
                        metadataModules.Add(type.Module);
                        typeMappings.Add(new MetadataMapping<MetadataType>(type, metadataTokenValue));
                    }
                    else if (tse is MethodDesc)
                    {
                        MethodDesc method = (MethodDesc)tse;
                        metadataModules.Add(((MetadataType)method.OwningType).Module);
                        methodMappings.Add(new MetadataMapping<MethodDesc>(method, metadataTokenValue));
                    }
                    else if (tse is FieldDesc)
                    {
                        FieldDesc field = (FieldDesc)tse;
                        metadataModules.Add(((MetadataType)field.OwningType).Module);
                        fieldMappings.Add(new MetadataMapping<FieldDesc>(field, metadataTokenValue));
                    }
                }
                else
                {
                    int deadLdtokenSignifier = il.ReadLdcI4();
                    Debug.Assert(deadLdtokenSignifier == 0);
                    il.ReadLdcI4();
                    il.ReadPop();
                    il.ReadPop();
                }
            }
        }

        public static bool ModuleHasMetadataMappings(ModuleDesc metadataDescribingModule)
        {
            return metadataDescribingModule.GetTypeByCustomAttributeTypeName(MetadataMappingTypeName, throwIfNotFound: false) != null;
        }

        private MetadataLoadedInfo LoadMetadata()
        {
            HashSet<ModuleDesc> metadataModules = new HashSet<ModuleDesc>();
            MetadataType typeWithMetadataMappings = (MetadataType)_metadataDescribingModule.GetTypeByCustomAttributeTypeName(MetadataMappingTypeName);

            MethodDesc fullMetadataMethod = typeWithMetadataMappings.GetMethod("Metadata", null);
            MethodDesc weakMetadataMethod = typeWithMetadataMappings.GetMethod("WeakMetadata", null);

            ILProvider ilProvider = new ILProvider(null);

            MetadataLoadedInfo result = new MetadataLoadedInfo();

            if (fullMetadataMethod != null)
            {
                MethodIL fullMethodIL = ilProvider.GetMethodIL(fullMetadataMethod);
                ReadMetadataMethod(fullMethodIL, out result.StrongTypeMappings, out result.MethodMappings, out result.FieldMappings, ref metadataModules);
            }

            if (weakMetadataMethod != null)
            {
                MethodIL weakMethodIL = ilProvider.GetMethodIL(weakMetadataMethod);
                List<MetadataMapping<MethodDesc>> weakMethodMappings = new List<MetadataMapping<MethodDesc>>();
                List<MetadataMapping<FieldDesc>> weakFieldMappings = new List<MetadataMapping<FieldDesc>>();
                ReadMetadataMethod(weakMethodIL, out result.AllTypeMappings, out weakMethodMappings, out weakFieldMappings, ref metadataModules);
                if ((weakMethodMappings.Count > 0) || (weakFieldMappings.Count > 0))
                {
                    // the format does not permit weak field/method mappings
                    throw new BadImageFormatException();
                }
            }

#if DEBUG
            // No duplicates are permitted in metadata mappings
            HashSet<TypeSystemEntity> mappingsDuplicateChecker = new HashSet<TypeSystemEntity>();
            foreach (MetadataMapping<MetadataType> mapping in result.AllTypeMappings)
            {
                if (!mappingsDuplicateChecker.Add(mapping.Entity))
                    throw new BadImageFormatException();
            }

            foreach (MetadataMapping<MetadataType> mapping in result.StrongTypeMappings)
            {
                if (!mappingsDuplicateChecker.Add(mapping.Entity))
                    throw new BadImageFormatException();
            }

            foreach (MetadataMapping<FieldDesc> mapping in result.FieldMappings)
            {
                if (!mappingsDuplicateChecker.Add(mapping.Entity))
                    throw new BadImageFormatException();
            }

            foreach (MetadataMapping<MethodDesc> mapping in result.MethodMappings)
            {
                if (!mappingsDuplicateChecker.Add(mapping.Entity))
                    throw new BadImageFormatException();
            }
#endif

            // All type mappings is the combination of strong and weak type mappings.
            result.AllTypeMappings.AddRange(result.StrongTypeMappings);

            result.MetadataModules = ImmutableArray.CreateRange(metadataModules);

            ImmutableArray<ModuleDesc>.Builder externalMetadataModulesBuilder = ImmutableArray.CreateBuilder<ModuleDesc>();
            ImmutableArray<ModuleDesc>.Builder localMetadataModulesBuilder = ImmutableArray.CreateBuilder<ModuleDesc>();
            foreach (ModuleDesc module in result.MetadataModules)
            {
                if (!_compilationModules.Contains(module))
                    externalMetadataModulesBuilder.Add(module);
                else
                    localMetadataModulesBuilder.Add(module);
            }
            result.ExternalMetadataModules = externalMetadataModulesBuilder.ToImmutable();
            result.LocalMetadataModules = localMetadataModulesBuilder.ToImmutable();

            return result;
        }

        public override IEnumerable<ModuleDesc> GetCompilationModulesWithMetadata()
        {
            return _loadedMetadata.Value.LocalMetadataModules;
        }

        public override bool IsReflectionBlocked(MetadataType type)
        {
            return type.HasCustomAttribute("System.Runtime.CompilerServices", "ReflectionBlockedAttribute");
        }

        protected override void ComputeMetadata(out byte[] metadataBlob, out List<MetadataMapping<MetadataType>> typeMappings, out List<MetadataMapping<MethodDesc>> methodMappings, out List<MetadataMapping<FieldDesc>> fieldMappings)
        {
            MetadataLoadedInfo loadedMetadata = _loadedMetadata.Value;
            metadataBlob = _metadataBlob;
            typeMappings = loadedMetadata.AllTypeMappings;
            methodMappings = loadedMetadata.MethodMappings;
            fieldMappings = loadedMetadata.FieldMappings;
        }

        private Dictionary<MethodDesc, MethodDesc> LoadDynamicInvokeStubs()
        {
            MetadataType typeWithMetadataMappings = (MetadataType)_metadataDescribingModule.GetTypeByCustomAttributeTypeName(MetadataMappingTypeName);
            Dictionary<MethodDesc, MethodDesc> dynamicInvokeMapTable = new Dictionary<MethodDesc, MethodDesc>();
            MethodDesc dynamicInvokeStubDescriptorMethod = typeWithMetadataMappings.GetMethod("DynamicInvokeStubs", null);

            if (dynamicInvokeStubDescriptorMethod == null)
                return dynamicInvokeMapTable;

            ILProvider ilProvider = new ILProvider(null);
            ILStreamReader il = new ILStreamReader(ilProvider.GetMethodIL(dynamicInvokeStubDescriptorMethod));
            // structure is 
            // REPEAT N TIMES    
            //ldtoken method
            //ldtoken dynamicInvokeStubMethod
            //pop
            //pop
            while (true)
            {
                if (il.TryReadRet()) // ret
                    break;

                MethodDesc method = il.ReadLdtokenAsTypeSystemEntity() as MethodDesc;
                MethodDesc dynamicMethodInvokeStub = il.ReadLdtokenAsTypeSystemEntity() as MethodDesc;
                il.ReadPop();
                il.ReadPop();

                if ((method != null) && (dynamicMethodInvokeStub != null))
                {
                    dynamicInvokeMapTable[method] = dynamicMethodInvokeStub;
                }
                else
                {
                    throw new BadImageFormatException();
                }
            }

            return dynamicInvokeMapTable;
        }

        /// <summary>
        /// Is there a reflection invoke stub for a method that is invokable?
        /// </summary>
        public override bool HasReflectionInvokeStubForInvokableMethod(MethodDesc method)
        {
            return GetReflectionInvokeStub(method) != null;
        }


        /// <summary>
        /// Gets a stub that can be used to reflection-invoke a method with a given signature.
        /// </summary>
        public override MethodDesc GetReflectionInvokeStub(MethodDesc method)
        {
            MethodDesc typicalInvokeTarget = method.GetTypicalMethodDefinition();
            MethodDesc typicalDynamicInvokeStub;

            if (!_dynamicInvokeStubs.Value.TryGetValue(typicalInvokeTarget, out typicalDynamicInvokeStub))
                return null;

            return InstantiateDynamicInvokeMethodForMethod(typicalDynamicInvokeStub, method);
        }
    }
}
