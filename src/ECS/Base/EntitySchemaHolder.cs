// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;

// ReSharper disable once CheckNamespace
namespace Friflo.Engine.ECS;

/// <summary>
/// The single place the process-wide <see cref="EntitySchema"/> lives.
/// <para>
/// The schema is created EXPLICITLY, never implicitly. It must be created after every mod assembly is
/// loaded and every component type is registered, and before the first <see cref="EntityStore"/> exists.
/// Anything that needs the schema earlier gets <see cref="NotCreated"/> rather than a silently wrong schema.
/// </para>
/// <para>
/// The schema is immutable and cannot be replaced once set: <see cref="StructInfo{T}.Index"/> and the
/// archetype bit sets cache struct indices derived from it, so a second schema would alias unrelated
/// component types onto the same index.
/// </para>
/// </summary>
internal static class EntitySchemaHolder
{
    private static volatile EntitySchema   schema;
    private static          StructHeap[]   defaultHeapMap;

    /// <summary>Whether the schema has been created. Reading this never creates it.</summary>
    internal static bool IsCreated => schema != null;

    internal static EntitySchema Schema => schema ?? throw NotCreated();

    /// <summary>All items are always null. Sized by the schema's <c>maxStructIndex</c>.</summary>
    internal static StructHeap[] DefaultHeapMap => schema != null ? defaultHeapMap : throw NotCreated();

    internal static void Set(EntitySchema entitySchema)
    {
        if (entitySchema == null) {
            throw new ArgumentNullException(nameof(entitySchema));
        }
        if (schema != null) {
            throw new InvalidOperationException(
                "EntitySchema already created. It is immutable and can be created only once per process.");
        }
        // Assign the derived state first so IsCreated == true implies everything is ready.
        defaultHeapMap  = new StructHeap[entitySchema.maxStructIndex];
        schema          = entitySchema;
    }

    private static InvalidOperationException NotCreated() => new InvalidOperationException(
        "EntitySchema has not been created. It must be created explicitly, in this order:" + '\n' +
        "  1. load every mod assembly" + '\n' +
        "  2. register every component, tag and script type" + '\n' +
        "  3. create the schema - NativeAOT.CreateSchema() for explicit registration, " +
        "or SchemaBootstrap.CreateFromLoadedAssemblies() to scan loaded assemblies" + '\n' +
        "  4. only then create an EntityStore" + '\n' +
        "The schema is never created implicitly, so whatever asked for it here ran too early.");
}
