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
    private static readonly object          gate = new object();

    private static volatile EntitySchema        schema;
    private static          StructHeap[]        defaultHeapMap;
    private static          EntitySchemaSource  source = EntitySchemaSource.NotCreated;

    /// <summary>Whether the schema has been created. Reading this never creates it.</summary>
    internal static bool IsCreated => schema != null;

    /// <summary>How the schema was created, or <see cref="EntitySchemaSource.NotCreated"/> if it was not.</summary>
    internal static EntitySchemaSource Source { get { lock (gate) { return source; } } }

    internal static EntitySchema Schema => schema ?? throw NotCreated();

    /// <summary>All items are always null. Sized by the schema's <c>maxStructIndex</c>.</summary>
    internal static StructHeap[] DefaultHeapMap => schema != null ? defaultHeapMap : throw NotCreated();

    internal static void Set(EntitySchema entitySchema, EntitySchemaSource schemaSource)
    {
        if (entitySchema == null) {
            throw new ArgumentNullException(nameof(entitySchema));
        }
        if (schemaSource == EntitySchemaSource.NotCreated) {
            throw new ArgumentOutOfRangeException(nameof(schemaSource),
                "A created schema must record how it was created.");
        }
        // Locked, so two threads racing to create cannot both pass the check and have the last writer win
        // silently. The loser gets AlreadyCreated, which is the whole point of the guard.
        lock (gate) {
            if (schema != null) {
                throw AlreadyCreated(schemaSource);
            }
            // Assign the derived state first so IsCreated == true implies everything is ready.
            defaultHeapMap  = new StructHeap[entitySchema.maxStructIndex];
            source          = schemaSource;
            schema          = entitySchema;
        }
    }

    /// <summary>
    /// The schema exists and something tried to create a second one. The two mechanisms are mutually
    /// exclusive: a reflection scan builds its own set of schema types and ignores anything registered on a
    /// <see cref="NativeAOT"/> instance, so they cannot be combined or merged.
    /// </summary>
    internal static InvalidOperationException AlreadyCreated(EntitySchemaSource attempted)
    {
        lock (gate) {
            var already = source == attempted
                ? "It was already created the same way, so this is a duplicate call."
                : "The two mechanisms are mutually exclusive: exactly one of them creates the schema, and a "
                  + "reflection scan cannot see types registered explicitly, nor the other way round.";
            return new InvalidOperationException(
                $"EntitySchema already created from {source}, and {attempted} tried to create it again. " +
                "The schema is immutable and there can be only one per process. " + already);
        }
    }

    private static InvalidOperationException NotCreated() => new InvalidOperationException(
        "EntitySchema has not been created. It must be created explicitly, in this order:" + '\n' +
        "  1. load every mod assembly" + '\n' +
        "  2. register every component, tag and script type" + '\n' +
        "  3. create the schema - SchemaBootstrap.CreateFromRegisteredTypes(aot) for explicit registration, " +
        "or SchemaBootstrap.CreateFromLoadedAssemblies() to scan loaded assemblies. Exactly one of them." + '\n' +
        "  4. only then create an EntityStore" + '\n' +
        "The schema is never created implicitly, so whatever asked for it here ran too early.");
}
