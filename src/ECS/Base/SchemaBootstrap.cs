// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// How the process-wide <see cref="EntitySchema"/> was created. Exactly one mechanism creates it: they are
/// mutually exclusive, not combinable.
/// </summary>
public enum EntitySchemaSource
{
    /// <summary>No schema has been created yet. Creating an <see cref="EntityStore"/> in this state throws.</summary>
    NotCreated,

    /// <summary>Created by <see cref="SchemaBootstrap.CreateFromRegisteredTypes"/> from types registered on a
    /// <see cref="NativeAOT"/> instance.</summary>
    RegisteredTypes,

    /// <summary>Created by <see cref="SchemaBootstrap.CreateFromLoadedAssemblies"/> by scanning the assemblies
    /// that were loaded at the time.</summary>
    LoadedAssemblies,
}

/// <summary>
/// The entry point for creating the process-wide <see cref="EntitySchema"/>. Every path goes through here,
/// so there is one place to look for how and when the schema comes into existence.
/// <para>
/// There are two ways to build one, both explicit, differing only in how component types are discovered:
/// <list type="bullet">
///   <item><see cref="CreateFromRegisteredTypes"/> - every type registered by hand on a
///   <see cref="NativeAOT"/> instance. Required when component types are not .NET types in this runtime,
///   for example server-side mod components that cross a runtime boundary as a stride plus function
///   pointers.</item>
///   <item><see cref="CreateFromLoadedAssemblies"/> - component, tag and script types discovered by
///   scanning the assemblies currently loaded. Only sees what is loaded when it runs, so every mod
///   assembly must already be loaded.</item>
/// </list>
/// </para>
/// <para>
/// Either way the schema is sealed once and for all. Whichever one you use, call it after all mods are
/// loaded and before the first <see cref="EntityStore"/> exists.
/// </para>
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>Whether the schema has been created. Reading this never creates it.</summary>
    public static bool IsSchemaCreated => EntitySchemaHolder.IsCreated;

    /// <summary>How the schema was created, or <see cref="EntitySchemaSource.NotCreated"/> if it was not.</summary>
    public static EntitySchemaSource SchemaSource => EntitySchemaHolder.Source;

    /// <summary>
    /// Creates the schema from the types registered explicitly on <paramref name="aot"/>. Register every
    /// component, tag, script, indexed component and mod component on it first: the schema is immutable,
    /// so anything missing here is missing for the lifetime of the process.
    /// </summary>
    /// <exception cref="InvalidOperationException">A schema was already created, by either mechanism.</exception>
    public static EntitySchema CreateFromRegisteredTypes(NativeAOT aot)
    {
        if (aot == null) {
            throw new ArgumentNullException(nameof(aot));
        }
        if (EntitySchemaHolder.IsCreated) {
            throw EntitySchemaHolder.AlreadyCreated(EntitySchemaSource.RegisteredTypes);
        }
        // NativeAOT.CreateSchema seals through EntitySchemaHolder itself, which re-checks under a lock.
        return aot.CreateSchema();
    }

    /// <summary>
    /// Creates the schema by scanning the assemblies currently loaded for <see cref="IComponent"/>,
    /// <see cref="ITag"/> and <see cref="Script"/> types.
    /// <para>
    /// Call this only once every mod assembly is loaded. Types in assemblies loaded afterwards are
    /// absent from the schema permanently, and using one then throws from <see cref="StructInfo{T}.Index"/>.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A schema was already created, by either mechanism.</exception>
    public static EntitySchema CreateFromLoadedAssemblies()
    {
        // Checked before the scan, which walks and force-loads the whole reference graph. Set() re-checks
        // under a lock, but there is no reason to do all that work only to throw at the end.
        if (EntitySchemaHolder.IsCreated) {
            throw EntitySchemaHolder.AlreadyCreated(EntitySchemaSource.LoadedAssemblies);
        }
        var schema = SchemaUtils.CreateSchemaByReflection();
        EntitySchemaHolder.Set(schema, EntitySchemaSource.LoadedAssemblies);
        return schema;
    }
}
