// Copyright (c) Ullrich Praetz - https://github.com/friflo. All rights reserved.
// See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Friflo.Engine.ECS.Index;
using Friflo.Engine.ECS.Relations;

// ReSharper disable UseRawString
// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

// ReSharper disable once InconsistentNaming
public sealed partial class NativeAOT
{
    private             EntitySchema                entitySchema;
    private             bool                        engineTypesRegistered;
        
    private readonly    HashSet<Type>               typeSet     = new();
    private readonly    SchemaTypes                 schemaTypes = new();
    private readonly    Dictionary<Assembly, int>   assemblyMap = new();
    private readonly    List<Assembly>              assemblies  = new();
    
    private static      NativeAOT           Instance;
    
    [ExcludeFromCodeCoverage]
    internal static EntitySchema GetSchema() => EntitySchemaHolder.Schema;

    /// <summary>
    /// Dead: this was the silent fallback that built an engine-types-only schema when none had been
    /// created, leaving every later component lookup pointing at the wrong table. The schema is now
    /// created explicitly or not at all.
    /// </summary>
    [ExcludeFromCodeCoverage]
#pragma warning disable CS0162 // unreachable code - body kept for reference
    private static EntitySchema CreateDefaultSchema()
    {
        throw new InvalidOperationException(
            "NativeAOT.CreateDefaultSchema is dead code. The EntitySchema is never created implicitly.");

        var schema = Instance?.entitySchema;
        if (schema != null) {
            return schema;
        }
        var msg =
@"EntitySchema not created.
NativeAOT requires schema creation on startup:
1. Create NativeAOT instance:   var aot = new NativeAOT();
2. Register types with:         aot.Register...(); 
3. Finish with:                 aot.CreateSchema();";
        Console.Error.WriteLine(msg);
        var aot = new NativeAOT();
        Console.WriteLine("Using default EntitySchema");
        return aot.CreateSchemaInternal();
/*  Return default schema instead of throwing an exception.
    By doing this subsequent access to components, tags & script result in meaningful stack traces.
    
    Throwing an exception is not helpful.
    E.g. the exception is thrown from within a constructor - like EntityStore(). In this case the exception log looks like:
        
A type initializer threw an exception. To determine which type, inspect the InnerException's StackTrace property.
   Stack Trace:
   at System.Runtime.CompilerServices.ClassConstructorRunner.EnsureClassConstructorRun(StaticClassConstructionContext*) + 0x247
   at System.Runtime.CompilerServices.ClassConstructorRunner.CheckStaticClassConstructionReturnGCStaticBase(StaticClassConstructionContext*, Object) + 0x1c
   at Friflo.Engine.ECS.EntityStoreBase.GetArchetypeConfig(EntityStoreBase) + 0x39
   at Friflo.Engine.ECS.EntityStoreBase..ctor() + 0xe1
   at Friflo.Engine.ECS.EntityStore..ctor(PidType) + 0x43
   at Friflo.Engine.ECS.EntityStore..ctor() + 0x1a
*/
    }
#pragma warning restore CS0162

    private EntitySchema CreateSchemaInternal()
    {
        if (EntitySchemaHolder.IsCreated) {
            throw EntitySchemaHolder.AlreadyCreated(EntitySchemaSource.RegisteredTypes);
        }
        RegisterEngineTypes();

        var dependants  = schemaTypes.CreateSchemaTypes(assemblies);
        entitySchema    = new EntitySchema(dependants, schemaTypes);
        Instance        = this;
        EntitySchemaHolder.Set(entitySchema, EntitySchemaSource.RegisteredTypes);
        return entitySchema;
    }

    /// <summary>
    /// Creates the schema from the types registered on this instance.
    /// Prefer <see cref="SchemaBootstrap.InitializeFromRegisteredTypes"/>: schema creation is routed through
    /// <see cref="SchemaBootstrap"/> so there is a single place that documents how and when it happens.
    /// </summary>
    public EntitySchema CreateSchema()
    {
        Console.WriteLine("NativeAOT.CreateSchema()");
        return CreateSchemaInternal();
    }

    /// <summary>
    /// The shape of the types registered on this instance, for comparing a repeated initialization against
    /// the schema already in place. Registers the engine types first, exactly as schema creation would, so
    /// the description covers the same set a real creation would produce.
    /// </summary>
    internal string DescribeRegisteredTypes()
    {
        RegisterEngineTypes();
        return EntitySchemaHolder.DescribeShape(typeSet, schemaTypes.components);
    }

    /// <summary>
    /// Adds the engine's own types, once per instance. Called by every registration entry point so they are
    /// present however the instance is used.
    /// <para>
    /// Deliberately NOT guarded on "a schema already exists". Registering into an instance that will not
    /// create the schema is harmless - it is a throwaway object - and it is what a process with more than one
    /// container does. The guard that matters lives on creation instead: see CreateSchemaInternal,
    /// EntitySchemaHolder.Initialize and EntitySchemaHolder.Set.
    /// </para>
    /// </summary>
    private void RegisterEngineTypes()
    {
        if (engineTypesRegistered) {
            return;
        }
        engineTypesRegistered = true;

        // components
        RegisterComponent<EntityName>();
        RegisterComponent<Position>();
        RegisterComponent<Rotation>();
        RegisterComponent<Scale3>();
        RegisterComponent<Transform>();
        RegisterComponent<TreeNode>();
        RegisterComponent<Unresolved>();
        
        // indexed components
        RegisterIndexedComponentClass<UniqueEntity, string>();

        RegisterTag<Disabled>();
    }
    
    private void AddType(Type type, SchemaTypeKind kind)
    {
        var assembly = type.Assembly;
        if (!assemblyMap.TryGetValue(assembly, out int assemblyIndex)) {
            assemblyIndex = assemblies.Count;
            assemblyMap.Add(assembly, assemblyIndex);
            assemblies.Add(assembly);
        }
        schemaTypes.AddSchemaType(new AssemblyType(type, kind, assemblyIndex));
    }
    
    /// <summary>
    /// Registers a component compiled into this build. Its managed type is its identity, so the schema keys it
    /// by that type and its struct index falls out of <c>StructInfo&lt;T&gt;</c> once the schema is created.
    /// See <see cref="RegisterModComponent"/> for a component a mod defines, which has no managed type here.
    /// </summary>
    public void RegisterComponent<T>() where T : struct, IComponent 
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T))) {
            AddType(typeof(T), SchemaTypeKind.Component);
            SchemaUtils.CreateComponentType<T>(0, null, null); // dummy call to prevent trimming required type info
        }
    }
    
    public void RegisterIndexedComponentClass<T, TValue>()
        where T : struct, IIndexedComponent<TValue>
        where TValue : class
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T)))
        {
            AddType(typeof(T), SchemaTypeKind.Component);
            SchemaUtils.CreateComponentType<T>(0, null, null);              // dummy call to prevent trimming required type info
            IndexedValueUtils.GetIndexedComponentValue<T, TValue>(default); // dummy call to prevent trimming required type info
            ComponentIndexUtils.CreateComponentIndexNativeAot[typeof(T)] = (store, componentType) => {
                return new ValueClassIndex<T, TValue>(store, componentType);
            };
        }
    }
    
    public void RegisterIndexedComponentStruct<T, TValue>()
        where T : struct, IIndexedComponent<TValue>
        where TValue : struct
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T)))
        {
            AddType(typeof(T), SchemaTypeKind.Component);
            SchemaUtils.CreateComponentType<T>(0, null, null);              // dummy call to prevent trimming required type info
            IndexedValueUtils.GetIndexedComponentValue<T, TValue>(default); // dummy call to prevent trimming required type info
            ComponentIndexUtils.CreateComponentIndexNativeAot[typeof(T)] = (store, componentType) => {
                return new ValueStructIndex<T, TValue>(store, componentType);
            };
        }
    }
    
    public void RegisterIndexedComponentEntity<T>()
        where T : struct, ILinkComponent
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T)))
        {
            AddType(typeof(T), SchemaTypeKind.Component);
            SchemaUtils.CreateComponentType<T>(0, null, null);              // dummy call to prevent trimming required type info
            IndexedValueUtils.GetIndexedComponentValue<T, Entity>(default); // dummy call to prevent trimming required type info
            ComponentIndexUtils.CreateComponentIndexNativeAot[typeof(T)] = (store, componentType) => {
                return new EntityIndex<T>(store, componentType);
            };
        }
    }
    
    public void RegisterRelation<T, TKey>()
        where T : struct, IRelation<TKey>
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T)))
        {
            AddType(typeof(T), SchemaTypeKind.Component);
            RelationUtils.GetRelationKey<T,TKey>(default);          // dummy call to prevent trimming required type info
            SchemaUtils.CreateRelationType<T>(0, null, null);       // dummy call to prevent trimming required type info
            AbstractEntityRelations.CreateEntityRelationsNativeAot[typeof(T)] = (componentType, archetype, heap) => {
                return new GenericEntityRelations<T, TKey>(componentType, archetype, heap);
            };
        }
    }
    
    public void RegisterLinkRelation<T>()
        where T : struct, ILinkRelation
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T)))
        {
            AddType(typeof(T), SchemaTypeKind.Component);
            RelationUtils.GetRelationKey<T,Entity>(default);        // dummy call to prevent trimming required type info
            SchemaUtils.CreateRelationType<T>(0, null, null);       // dummy call to prevent trimming required type info
            AbstractEntityRelations.CreateEntityRelationsNativeAot[typeof(T)] = (componentType, archetype, heap) => {
                return new EntityLinkRelations<T>(componentType, archetype, heap);
            };
        }
    }

    public void RegisterTag<T>()  where T : struct, ITag 
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T))) {
            AddType(typeof(T), SchemaTypeKind.Tag);
            SchemaUtils.CreateTagType<T>(0);                        // dummy call to prevent trimming required type info
        }
    }
    
    public void RegisterScript<T>()  where T : Script, new()
    {
        RegisterEngineTypes();
        if (typeSet.Add(typeof(T))) {
            AddType(typeof(T), SchemaTypeKind.Script);
            SchemaUtils.CreateScriptType<T>(0);          // dummy call to prevent trimming required type info
        }
    }
}