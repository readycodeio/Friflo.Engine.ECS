// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// Explicit entry points for creating the process-wide <see cref="EntitySchema"/>.
/// <para>
/// There are two ways to build a schema, and both are explicit:
/// <list type="bullet">
///   <item><see cref="NativeAOT.CreateSchema"/> - register every type by hand. Required when component
///   types are not .NET types in this runtime, for example server-side mod components that cross a
///   runtime boundary as a stride plus function pointers.</item>
///   <item><see cref="CreateFromLoadedAssemblies"/> - discover component, tag and script types by
///   scanning the assemblies currently loaded. Only sees what is loaded when it runs, so every mod
///   assembly must already be loaded.</item>
/// </list>
/// </para>
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>Whether the schema has been created. Reading this never creates it.</summary>
    public static bool IsSchemaCreated => EntitySchemaHolder.IsCreated;

    /// <summary>
    /// Creates the schema by scanning the assemblies currently loaded for <see cref="IComponent"/>,
    /// <see cref="ITag"/> and <see cref="Script"/> types.
    /// <para>
    /// Call this only once every mod assembly is loaded. Types in assemblies loaded afterwards are
    /// absent from the schema permanently, and using one then throws from <see cref="StructInfo{T}.Index"/>.
    /// </para>
    /// </summary>
    public static EntitySchema CreateFromLoadedAssemblies()
    {
        var schema = SchemaUtils.CreateSchemaByReflection();
        EntitySchemaHolder.Set(schema);
        return schema;
    }
}
