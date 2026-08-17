// Copyright (c) ReadyM / ReadyCode Limited. All rights reserved.
// Friflo.Engine.ECS fork addition.

using System;
using System.Collections.Generic;

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
/// <para>
/// Read the synchronization contract on the fields below before adding a lock or a volatile here.
/// </para>
/// </summary>
internal static class EntitySchemaHolder
{
    // ---------------------------------------------------------------------------------------------------
    // Synchronization contract
    // ---------------------------------------------------------------------------------------------------
    // The schema is written once during startup and is immutable afterwards, so there is no mutable shared
    // state to protect. The read path therefore carries NO synchronization: no lock, no volatile. That is a
    // deliberate choice, and it rests on an obligation the caller has to meet regardless.
    //
    // THE CALLER'S OBLIGATION. Creating the schema must happen-before every read of it. This cannot be the
    // holder's job. A holder that synchronized every read would still not help a caller that creates a
    // world on one thread while another is still registering component types: that caller does not get a
    // stale schema, it gets NotCreated, nondeterministically. Ordering startup is the caller's
    // responsibility, and once it is met, plain field reads are correct, because the writes were published
    // by whatever established that ordering - typically starting the threads that later read them.
    //
    // WHAT THE LOCK IS FOR. Initialize does the check, the build and the assignment under `gate`, so every
    // caller leaves with a happens-before edge to the writes whether it created the schema or found one
    // already there. That is what makes "several callers each initialize on their own" sound rather than
    // hopeful, and it is why Initialize is the ONLY way in.
    //
    // WHY THERE IS NO PUBLIC IsCreated. A published "has it been created" flag invites
    // `if (!IsCreated) Create()`, and the thread that observes true and skips the call never touches `gate`,
    // so it gets no edge with the writer. Rather than document that trap we removed the means to fall into
    // it: callers call Initialize unconditionally.
    //
    // WHY NOT SIMPLY LOCK THE READS. Static.EntitySchema is read on every archetype creation and inside the
    // query enumerators. A lock or a volatile there buys nothing a correct caller needs and costs something
    // on a genuinely hot path. The error path is the one exception: see AlreadyCreated.
    // ---------------------------------------------------------------------------------------------------

    private static readonly object              gate = new object();

    private static          EntitySchema        schema;
    private static          StructHeap[]        defaultHeapMap;
    private static          EntitySchemaSource  source = EntitySchemaSource.NotCreated;

    // Null by default, which makes a repeated initialization a hard failure. See
    // AllowRepeatedInitialization for the only situation that legitimately relaxes it.
    private static          Action<string>      onRepeatedInitialization;

    /// <summary>Whether the schema has been created. Reading this never creates it.</summary>
    internal static bool IsCreated => schema != null;

    /// <summary>How the schema was created, or <see cref="EntitySchemaSource.NotCreated"/> if it was not.</summary>
    internal static EntitySchemaSource Source => source;

    /// <summary>
    /// Whether a repeated initialization through the same mechanism is tolerated in this process. Whether a
    /// repeat throws depends on it, so anything asserting either behaviour should state which it expects
    /// rather than assume.
    /// </summary>
    internal static bool RepeatsAllowed { get { lock (gate) { return onRepeatedInitialization != null; } } }

    internal static EntitySchema Schema => schema ?? throw NotCreated();

    /// <summary>All items are always null. Sized by the schema's <c>maxStructIndex</c>.</summary>
    internal static StructHeap[] DefaultHeapMap => schema != null ? defaultHeapMap : throw NotCreated();

    /// <summary>
    /// Downgrades a repeated initialization through the SAME mechanism from a hard failure to a report on
    /// <paramref name="onRepeat"/>. A conflict between the two mechanisms stays a hard failure either way:
    /// that is never legitimate.
    /// <para>
    /// Enabling this requires supplying somewhere to report to, so it can never be turned on silently.
    /// </para>
    /// </summary>
    internal static void AllowRepeatedInitialization(Action<string> onRepeat)
    {
        if (onRepeat == null) {
            throw new ArgumentNullException(nameof(onRepeat));
        }
        lock (gate) {
            onRepeatedInitialization = onRepeat;
        }
    }

    /// <summary>Restores the default, where a repeated initialization is a hard failure.</summary>
    internal static void DisallowRepeatedInitialization()
    {
        lock (gate) {
            onRepeatedInitialization = null;
        }
    }

    /// <summary>
    /// The only way to create the schema. Everything happens under <c>gate</c>: the check, the build, and
    /// the assignment. So every caller leaves with a happens-before edge to the writes, and there is no way
    /// to observe "not created" and then act on it unsynchronized.
    /// <para>
    /// <paramref name="sealSchema"/> runs only if this call is the one that creates the schema, and must
    /// call <see cref="Set"/> with <paramref name="schemaSource"/>. It is invoked while the lock is held,
    /// which is safe because <see cref="Set"/> re-enters the same lock.
    /// </para>
    /// </summary>
    internal static EntitySchema Initialize(
        EntitySchemaSource  schemaSource,
        Func<string>        describeCandidate,
        Action              sealSchema)
    {
        if (schemaSource == EntitySchemaSource.NotCreated) {
            throw new ArgumentOutOfRangeException(nameof(schemaSource),
                "A created schema must record how it was created.");
        }
        if (describeCandidate == null) {
            throw new ArgumentNullException(nameof(describeCandidate));
        }
        if (sealSchema == null) {
            throw new ArgumentNullException(nameof(sealSchema));
        }

        Action<string> report;
        string         message;
        EntitySchema   existing;

        lock (gate) {
            if (schema == null) {
                sealSchema();
                if (schema == null) {
                    throw new InvalidOperationException(
                        $"Initializing the EntitySchema from {schemaSource} did not create one.");
                }
                return schema;
            }

            // A different mechanism is always a bug, flag or no flag.
            if (source != schemaSource) {
                throw AlreadyCreated(schemaSource);
            }
            // Same mechanism, and nobody opted into tolerating repeats: this is the default, and it fails.
            if (onRepeatedInitialization == null) {
                throw AlreadyCreated(schemaSource);
            }
            // Tolerating a repeat is only safe if this caller would have produced the same schema. If it
            // would not, its component types are NOT the ones in the sealed schema, and quietly handing it
            // the existing schema is how struct indices come to mean different things to different callers.
            // That is the bug this whole holder exists to prevent, so it fails even in the tolerant mode.
            var candidate = describeCandidate();
            var inPlace   = DescribeShape(schema);
            if (candidate != inPlace) {
                throw new InvalidOperationException(
                    $"EntitySchema was already created from {source}, and a repeated initialization from " +
                    $"{schemaSource} would have produced a DIFFERENT schema. Repeated initialization is " +
                    "allowed for this process, but only when the shapes match: a caller whose types are not " +
                    "the ones in the sealed schema would read struct indices that mean something else." +
                    '\n' + "in place:  " + inPlace + '\n' + "candidate: " + candidate);
            }
            report   = onRepeatedInitialization;
            message  = $"EntitySchema was already created from {source} and has been initialized again with " +
                       "a matching shape. Tolerated because repeated initialization was explicitly allowed " +
                       "for this process.";
            existing = schema;
        }

        // Reported outside the lock: the sink is caller-supplied and must not run under our lock.
        report(message);
        return existing;
    }

    /// <summary>
    /// A description of what a schema is built from, comparable between a schema already created and a
    /// <see cref="NativeAOT"/> instance that has registered types but not created one.
    /// <para>
    /// It covers the registered .NET types plus the mod components, which have no .NET type and are compared
    /// by struct index and size. It deliberately does NOT describe the finished component and tag tables:
    /// those are only populated by <c>SchemaTypes.CreateSchemaTypes</c> during real creation, so producing
    /// them for a candidate would mean building a second schema, and that mutates Friflo's process-global
    /// type state. Registered types plus mod components is what can be compared without side effects, and it
    /// is enough to catch a caller whose registrations differ from the sealed schema's.
    /// </para>
    /// </summary>
    internal static string DescribeShape(EntitySchema entitySchema)
    {
        var types = new List<string>();
        foreach (var pair in entitySchema.ComponentTypeByType) {
            types.Add(pair.Key.FullName);
        }
        foreach (var pair in entitySchema.TagTypeByType) {
            types.Add(pair.Key.FullName);
        }
        var mods = new List<string>();
        foreach (var component in entitySchema.components) {
            if (component is ModComponentType) {
                mods.Add($"{component.StructIndex}:{component.StructSize}");
            }
        }
        return Describe(types, mods);
    }

    /// <inheritdoc cref="DescribeShape(EntitySchema)"/>
    internal static string DescribeShape(IEnumerable<Type> registeredTypes, List<ComponentType> components)
    {
        var types = new List<string>();
        foreach (var type in registeredTypes) {
            types.Add(type.FullName);
        }
        var mods = new List<string>();
        foreach (var component in components) {
            if (component is ModComponentType) {
                mods.Add($"{component.StructIndex}:{component.StructSize}");
            }
        }
        return Describe(types, mods);
    }

    private static string Describe(List<string> types, List<string> mods)
    {
        types.Sort(StringComparer.Ordinal);
        mods.Sort(StringComparer.Ordinal);
        return "types[" + string.Join(",", types) + "] mods[" + string.Join(",", mods) + "]";
    }

    internal static void Set(EntitySchema entitySchema, EntitySchemaSource schemaSource)
    {
        if (entitySchema == null) {
            throw new ArgumentNullException(nameof(entitySchema));
        }
        if (schemaSource == EntitySchemaSource.NotCreated) {
            throw new ArgumentOutOfRangeException(nameof(schemaSource),
                "A created schema must record how it was created.");
        }
        lock (gate) {
            if (schema != null) {
                throw AlreadyCreated(schemaSource);
            }
            defaultHeapMap  = new StructHeap[entitySchema.maxStructIndex];
            source          = schemaSource;
            // Written last, so a caller that ignored the ordering contract is more likely to fail on
            // NotCreated than to read a half-published holder. Defence in depth, not a guarantee: without
            // the required happens-before there is no guarantee to give.
            schema          = entitySchema;
        }
    }

    /// <summary>
    /// The schema exists and something tried to create a second one. The two mechanisms are mutually
    /// exclusive: a reflection scan builds its own set of schema types and ignores anything registered on a
    /// <see cref="NativeAOT"/> instance, so they cannot be combined or merged.
    /// <para>
    /// This takes <c>gate</c>, unlike the read path. It is an error path where the cost is irrelevant, and a
    /// caller racing to create is precisely the case where an unsynchronized read of <c>source</c> could
    /// name the wrong mechanism, or none, in the one message someone will use to work out what happened.
    /// </para>
    /// </summary>
    internal static InvalidOperationException AlreadyCreated(EntitySchemaSource attempted)
    {
        lock (gate) {
            var already = source == attempted
                ? "It was already created the same way, so this is a duplicate initialization."
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
        "  3. create the schema - SchemaBootstrap.InitializeFromRegisteredTypes(aot) for explicit " +
        "registration, or SchemaBootstrap.InitializeFromLoadedAssemblies() to scan loaded assemblies. " +
        "Exactly one of them." + '\n' +
        "  4. only then create an EntityStore" + '\n' +
        "The schema is never created implicitly, so whatever asked for it here ran too early.");
}
